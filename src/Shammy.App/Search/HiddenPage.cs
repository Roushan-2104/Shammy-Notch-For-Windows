using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>
/// A page loaded out of sight to lift a short answer from it: Google AI Mode's first lines for a question, or Lens's AI
/// Overview for a circled picture. It is a second view in the same browser profile as the results card (so Google's
/// cookies and its robot-check pass carry over), parked off-screen inside the notch's window, and closed as soon as
/// the answer is read. Only the page's visible text is read; nothing on it is clicked or filled in.
/// </summary>
internal static class HiddenPage
{
    const string Probe = """
        (() => {
          if (/\/sorry\//.test(location.href)) return JSON.stringify({ state: 'robot' });
          const t = document.body ? document.body.innerText : '';
          let at = -1;
          for (const m of ['AI Mode reply for', 'AI Overview']) {
            const k = t.indexOf(m);
            if (k >= 0) { at = t.indexOf('\n', k); break; }
          }
          if (at < 0) return JSON.stringify({ state: 'wait' });
          const lines = t.slice(at + 1).split('\n').map(s => s.trim())
            .filter(s => (s.length >= 40 || (s.length >= 6 && /[.!?。)"”]$/.test(s)))
                && !/^AI (can|responses may) make mistakes/i.test(s) && !/^(Thinking|Searching)/.test(s)
                && !/^(Would you like|Do you want|Let me know|Feel free|I can also|If you'd like)/i.test(s));
          return JSON.stringify({ state: lines.length ? 'text' : 'wait', text: lines.slice(0, 2).join('\n') });
        })()
        """;

    sealed record ProbeResult(string? state, string? text);

    /// <summary>Test pipe ("answer-probe"): what the last look at a hidden page found.</summary>
    public static string LastProbe { get; private set; } = "-";

    /// <summary>The short answer on <paramref name="url"/> (AI Mode), or null.</summary>
    public static Task<ShortAnswer?> ReadAsync(IntPtr parent, Uri url, TimeSpan limit, CancellationToken ct) =>
        ReadAsync(parent, core => core.Navigate(url.AbsoluteUri), limit, ct);

    const string TranslateProbe = """
        (() => {
          if (/\/sorry\//.test(location.href)) return JSON.stringify({ state: 'robot' });
          const r = document.querySelector('.result-container');
          const t = r ? r.innerText.trim() : '';
          return JSON.stringify({ state: t ? 'text' : 'wait', text: t });
        })()
        """;

    /// <summary>Google Translate's basic page (the one its "basic version" link opens): the translation, or null.</summary>
    public static async Task<string?> TranslateAsync(IntPtr parent, string text, string target, TimeSpan limit, CancellationToken ct)
    {
        var url = $"https://translate.google.com/m?sl=auto&tl={Uri.EscapeDataString(target)}&hl=en&q={Uri.EscapeDataString(text)}";
        var answer = await ReadAsync(parent, core => core.Navigate(url), limit, ct, TranslateProbe, steadyPolls: 0);
        return answer?.Text;
    }

    /// <summary>Lens's short answer for a picture (the same upload the results card's Lens makes), or null.</summary>
    public static Task<ShortAnswer?> ReadLensAsync(IntPtr parent, byte[] png, TimeSpan limit, CancellationToken ct) =>
        ReadAsync(parent, core =>
        {
            string boundary = "----ShammyLens" + Guid.NewGuid().ToString("N");
            var body = new MemoryStream();
            void Write(string s)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                body.Write(bytes, 0, bytes.Length);
            }
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"encoded_image\"; filename=\"shammy.png\"\r\nContent-Type: image/png\r\n\r\n");
            body.Write(png, 0, png.Length);
            Write($"\r\n--{boundary}--\r\n");
            body.Position = 0;
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var request = core.Environment.CreateWebResourceRequest($"https://lens.google.com/v3/upload?hl=en&re=df&stcs={stamp}&ep=gsbubb", "POST", body,
                $"Content-Type: multipart/form-data; boundary={boundary}");
            core.NavigateWithWebResourceRequest(request);
        }, limit, ct);

    static async Task<ShortAnswer?> ReadAsync(IntPtr parent, Action<CoreWebView2> open, TimeSpan limit, CancellationToken ct,
        string probeScript = Probe, int steadyPolls = -1)
    {
        CoreWebView2Controller? controller = null;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebView);
            controller = await env.CreateCoreWebView2ControllerAsync(parent);
            // "Visible" to the page (so it renders and streams at full speed), but parked far outside the window.
            controller.Bounds = new System.Drawing.Rectangle(-6000, -6000, 900, 1000);
            controller.IsVisible = true;
            var core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            // Nothing on the page may open windows or hand links to other apps.
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            open(core);

            var until = DateTime.UtcNow + limit;
            string last = "";
            int steady = 0;
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(450, ct); }
                catch (OperationCanceledException) { return null; }
                ProbeResult? probe;
                try
                {
                    var raw = await core.ExecuteScriptAsync(probeScript);
                    var inner = JsonSerializer.Deserialize<string>(raw);
                    probe = inner is null ? null : JsonSerializer.Deserialize<ProbeResult>(inner);
                }
                catch (Exception) { continue; }
                LastProbe = $"{probe?.state} len={probe?.text?.Length ?? 0} host={(Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath : "-")}";
                if (probe?.state == "robot") return null;
                if (probe?.state != "text" || string.IsNullOrWhiteSpace(probe.text)) continue;
                // The answer streams in: take it once it has stopped growing (two lines: sooner).
                if (probe.text == last) steady++;
                else
                {
                    last = probe.text;
                    steady = 0;
                }
                int need = steadyPolls >= 0 ? steadyPolls : last.Contains('\n') ? 1 : 2;
                if (steady >= need) return new ShortAnswer(last, core.Source);
            }
            return last.Length > 0 && !ct.IsCancellationRequested ? new ShortAnswer(last, core.Source) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("A hidden page for a short answer failed", ex);
            return null;
        }
        finally
        {
            try { controller?.Close(); }
            catch (Exception) { }
        }
    }
}

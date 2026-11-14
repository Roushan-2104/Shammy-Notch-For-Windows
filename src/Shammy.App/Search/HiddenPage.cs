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

    // A one-line reply ("Weekend" for a translation): the first real line under the question, however short, skipping
    // the page's own labels. Taken once it has stayed the same for a moment, so a reply still streaming isn't cut.
    const string BriefProbe = """
        (() => {
          if (/\/sorry\//.test(location.href)) return JSON.stringify({ state: 'robot' });
          const t = document.body ? document.body.innerText : '';
          const k = t.indexOf('AI Mode reply for');
          if (k < 0) return JSON.stringify({ state: 'wait' });
          const lines = t.slice(t.indexOf('\n', k) + 1).split('\n').map(s => s.trim())
            .filter(s => /[\p{L}\p{N}]/u.test(s)
                && !/^(Skip to|AI Mode response|AI (can|responses may) make mistakes|Thinking|Searching|Show (more|all)|Ask anything)/i.test(s)
                && !/^(Would you like|Do you want|Let me know|Feel free|I can also|If you'd like)/i.test(s));
          return JSON.stringify({ state: lines.length ? 'text' : 'wait', text: lines.length ? lines[0] : '' });
        })()
        """;

    /// <summary>AI Mode's one-line reply on <paramref name="url"/> (a translation, a single word), or null.</summary>
    public static Task<ShortAnswer?> ReadBriefAsync(IntPtr parent, Uri url, TimeSpan limit, CancellationToken ct) =>
        ReadAsync(parent, core => core.Navigate(url.AbsoluteUri), limit, ct, BriefProbe, steadyPolls: 2);

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

    const string TextProbe = """
        (() => {
          if (/\/sorry\//.test(location.href)) return JSON.stringify({ state: 'robot' });
          if (document.readyState !== 'complete' || !document.body) return JSON.stringify({ state: 'wait' });
          const t = document.body.innerText.slice(0, 30000);
          return JSON.stringify({ state: t.length > 200 ? 'text' : 'wait', text: t });
        })()
        """;

    /// <summary>A page's visible text once it has settled (up to 30,000 characters), or null (a robot check, nothing in time).</summary>
    public static async Task<string?> ReadTextAsync(IntPtr parent, Uri url, TimeSpan limit, CancellationToken ct)
    {
        var page = await ReadAsync(parent, core => core.Navigate(url.AbsoluteUri), limit, ct, TextProbe, steadyPolls: 2);
        return page?.Text;
    }

    /// <summary>Google Shopping's results for <paramref name="product"/> in India, as visible text (for <see cref="Core.Shopping.ShoppingOffers"/>), or null.</summary>
    public static Task<string?> ReadShoppingAsync(IntPtr parent, string product, TimeSpan limit, CancellationToken ct) =>
        ReadTextAsync(parent, ShoppingUrl(product), limit, ct);

    /// <summary>Google Shopping's results page for <paramref name="product"/> (Indian stores, English).</summary>
    public static Uri ShoppingUrl(string product) => new("https://www.google.com/search?udm=28&hl=en&gl=in&q=" + Uri.EscapeDataString(product));

    /// <summary>
    /// Lens's whole results page for a picture, as visible text: its AI Overview (see <see cref="AnswerFrom"/>) and, for a
    /// product, its shopping results. Null on a robot check or nothing in time.
    /// </summary>
    public static async Task<string?> ReadLensPageAsync(IntPtr parent, byte[] png, TimeSpan limit, CancellationToken ct)
    {
        var page = await ReadAsync(parent, core => UploadToLens(core, png), limit, ct, LensTextProbe, steadyPolls: 1);
        return page?.Text;
    }

    // Lens's page is ready once its AI Overview has words under it (or it plainly has none and has settled).
    const string LensTextProbe = """
        (() => {
          if (/\/sorry\//.test(location.href)) return JSON.stringify({ state: 'robot' });
          if (document.readyState !== 'complete' || !document.body) return JSON.stringify({ state: 'wait' });
          const t = document.body.innerText.slice(0, 30000);
          const k = t.indexOf('AI Overview');
          const ready = k < 0 ? t.length > 400 : t.slice(k).split('\n').some(s => s.trim().length >= 40);
          return JSON.stringify({ state: ready ? 'text' : 'wait', text: t });
        })()
        """;

    /// <summary>The short answer in a page's text (its first lines under "AI Mode reply" or "AI Overview"), or null; the same rule as the live probe.</summary>
    public static string? AnswerFrom(string? pageText)
    {
        if (string.IsNullOrEmpty(pageText)) return null;
        int at = -1;
        foreach (var marker in new[] { "AI Mode reply for", "AI Overview" })
        {
            int k = pageText.IndexOf(marker, StringComparison.Ordinal);
            if (k >= 0)
            {
                at = pageText.IndexOf('\n', k);
                break;
            }
        }
        if (at < 0) return null;
        var lines = pageText[(at + 1)..].Split('\n').Select(s => s.Trim())
            .Where(s => (s.Length >= 40 || (s.Length >= 6 && ".!?。)\"”".Contains(s[^1])))
                        && !s.StartsWith("AI can make mistakes", StringComparison.OrdinalIgnoreCase)
                        && !s.StartsWith("AI responses may make mistakes", StringComparison.OrdinalIgnoreCase)
                        && !s.StartsWith("Thinking", StringComparison.Ordinal) && !s.StartsWith("Searching", StringComparison.Ordinal))
            .Take(2).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    static void UploadToLens(CoreWebView2 core, byte[] png)
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
        var request = core.Environment.CreateWebResourceRequest($"https://lens.google.com/v3/upload?hl=en&gl=in&re=df&stcs={stamp}&ep=gsbubb", "POST", body,
            $"Content-Type: multipart/form-data; boundary={boundary}");
        core.NavigateWithWebResourceRequest(request);
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

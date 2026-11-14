using System.Text.RegularExpressions;
using Shammy.Search;

namespace Shammy.Modules.Screenshot;

/// <summary>"take a screenshot" by voice or typing: captures the whole screen the pointer is on.</summary>
internal sealed class ScreenshotModule : IShammyModule, ITextCommand
{
    static readonly Regex Phrase = new(
        @"^(?:please\s+)?(?:take|grab|capture|save)\s+(?:a\s+|the\s+)?(?:screen\s*shot|screen|snapshot)(?:\s+of\s+(?:the\s+)?(?:screen|whole\s+screen|everything|this|page))?(?:\s+please)?$|^screen\s*shot(?:\s+(?:this|now|please))?$|^print\s*screen$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    const string Glyph = "";

    ModuleContext _ctx = null!;
    string _last = "none";

    public string Name => "screenshot";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        ctx.AddCommand(this);
        // "screenshot-now" captures in the background; "screenshot-state" says how the last one went.
        ctx.AddPipeCommand("screenshot-now", arg =>
        {
            _ = PipeCaptureAsync();
            return "capturing";
        });
        ctx.AddPipeCommand("screenshot-state", _ => _last);
    }

    public bool CanHandle(string text) => Phrase.IsMatch(text.Trim().TrimEnd('.', '!', '?').Trim());

    /// <summary>Says "saved" only once the file is written; a failed capture or save says so instead.</summary>
    public async Task<CommandResult> RunAsync(string text)
    {
        var path = await CaptureAsync();
        return path is null
            ? new CommandResult(Glyph, "Screenshot failed", "Couldn't capture or save the screen", IsError: true)
            : new CommandResult(Glyph, "Screenshot", "Saved to Pictures › Screenshots and copied");
    }

    async Task PipeCaptureAsync()
    {
        var path = await CaptureAsync();
        if (path is null) _ctx.ShowMessage(Glyph, "Screenshot failed", error: true);
        else _ctx.ShowMessage(Glyph, "Screenshot saved", seconds: 2);
    }

    /// <summary>
    /// Waits for the frozen-screen overlay (voice commands) to fade out first, and keeps the notch itself (the
    /// "thinking" bar) out of the picture. Runs on the UI thread. Returns the saved file, or null on failure.
    /// </summary>
    async Task<string?> CaptureAsync()
    {
        await Task.Delay(650);
        // Stage or a recording may already keep the notch out of captures; leave it that way afterwards.
        bool wasExcluded = _ctx.Notch.ExcludedFromCapture;
        try
        {
            _ctx.Notch.ExcludeFromCapture(true);
            var shot = ScreenCapture.CaptureMonitorAtCursor();
            var path = ScreenshotSaver.Save(shot.Image);
            _last = path is null ? "failed: couldn't save" : $"saved {Path.GetFileName(path)}";
            return path;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Screenshot failed", ex);
            _last = "failed: " + ex.GetType().Name;
            return null;
        }
        finally
        {
            _ctx.Notch.ExcludeFromCapture(wasExcluded);
        }
    }

    public void Dispose() { }
}

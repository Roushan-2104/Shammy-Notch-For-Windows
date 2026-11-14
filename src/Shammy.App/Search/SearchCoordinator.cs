using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Shell;

namespace Shammy.Search;

/// <summary>
/// The hotkey flows:
/// <list type="bullet">
/// <item>Right Alt tap (under 180 ms): a type-to-search box drops from the notch;</item>
/// <item>Right Alt hold: the mic listens (the screen stays live); release to run the command or search;</item>
/// <item>Copilot key / lone Right Ctrl: the screen freezes, the mic listens, and you can circle anything;
/// release to search, or release without speaking or circling to circle at leisure.</item>
/// </list>
/// Results come from Google AI Mode, the plain Web tab, or Lens for images, rendered in the notch.
/// </summary>
internal sealed partial class SearchCoordinator
{
    enum Phase { Idle, Pending, Holding, Circling, Processing, Typing, Showing, Message, Choices }

    static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(180);
    static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(250);

    readonly NotchWindow _notch;
    readonly GlobalKeys _keys;
    readonly WhisperService _whisper;
    readonly AppSettings _settings;
    readonly Modules.ModuleRegistry _modules;
    readonly MicRecorder _mic = new();
    readonly DispatcherTimer _holdTimer;

    Phase _phase;
    int _session;
    bool _micOk;
    bool _listenOnline; // this hold's recording goes online (the listening view said so)
    string? _micError;
    ScreenShot? _shot;
    SearchOverlay? _overlay;
    Task<IReadOnlyList<OcrWord>>? _ocr;
    Context? _ctx;
    CancellationTokenSource? _transcribe;
    HotKey _key;

    sealed record Context(string Query, string DisplayQuery, SearchMode Mode, SearchFilter Filters, byte[]? Png, BitmapSource? Thumb)
    {
        public Modules.SelectionInfo? Selection { get; init; }

        /// <summary>A page to show instead of a search (a command's translation); a mode or filter chip searches as usual.</summary>
        public Uri? Page { get; init; }
    }

    public SearchCoordinator(NotchWindow notch, GlobalKeys keys, WhisperService whisper, AppSettings settings, Modules.ModuleRegistry modules)
    {
        _notch = notch;
        _keys = keys;
        _whisper = whisper;
        _settings = settings;
        _modules = modules;
        Modules.HostLinks.ShowRemembered = ShowRemembered;
        _holdTimer = new DispatcherTimer(HoldThreshold, DispatcherPriority.Input, (_, _) => OnHoldElapsed(), notch.Dispatcher) { IsEnabled = false };

        keys.Pressed += OnKeyDown;
        keys.Released += OnKeyUp;
        keys.Aborted += key =>
        {
            // AltGr was used to type a character: quietly undo the listening it had started.
            if (key == _key && _phase is Phase.Pending or Phase.Holding) Cancel();
        };
        keys.EscapePressed += Cancel;
        keys.ChoiceKey += n =>
        {
            if (_phase != Phase.Choices || _choices is null) return;
            int at = n == 0 ? 0 : n - 1;
            if (at <= _choices.Count) PickChoice(at);
        };
        notch.InteractiveDeactivated += () =>
        {
            if (_phase is Phase.Showing or Phase.Typing) Cancel();
        };
        notch.SearchBarView.Submitted += text => _ = ProcessTextAsync(text);
        // Palette rows (Modules/Palette): the web row skips commands; rows that acted themselves show a confirmation.
        notch.SearchBarView.WebSearchRequested += text =>
        {
            if (_phase == Phase.Typing) _ = SearchWebAsync(text);
        };
        notch.SearchBarView.ActionCompleted += result =>
        {
            if (_phase != Phase.Typing) return;
            if (result is null) Cancel();
            else ShowCommandResult(result);
        };
        notch.ResultView.CloseRequested += Cancel;
        notch.ResultView.ModeChanged += mode =>
        {
            if (_ctx is null) return;
            // An image with no words in or with it has nothing to search as text ("Selected area" isn't a query).
            if (mode != SearchMode.Lens && _ctx.Query.Length == 0)
            {
                notch.ResultView.SetModeChips(_ctx.Mode);
                return;
            }
            _ctx = _ctx with { Mode = mode, Page = null };
            _ = NavigateAsync(_ctx);
        };
        notch.ResultView.FiltersChanged += filters =>
        {
            if (_ctx is null) return;
            _ctx = _ctx with { Filters = filters, Mode = _ctx.Mode == SearchMode.Lens ? _settings.DefaultSearchMode : _ctx.Mode, Page = null };
            _ = NavigateAsync(_ctx);
        };
        notch.AnswerView.SearchWebRequested += query => _ = ShowResultAsync(new Context(query, query, _settings.DefaultSearchMode, SearchFilter.None, null, null));
        notch.AnswerView.MomentExpandRequested += ExpandMoment;
        notch.AnswerView.MomentDone += Cancel;
    }

    public bool IsActive => _phase != Phase.Idle;

    // ---- Teardown ------------------------------------------------------------------------------

    public void Cancel()
    {
        if (_phase == Phase.Idle) return;
        Reset(keepNotch: false);
    }

    void Reset(bool keepNotch)
    {
        _session++;
        _holdTimer.Stop();
        _silence?.Stop();
        if (_mic.IsRecording) _mic.Stop();
        DismissOverlay();
        // Teardown must always finish: a throw here used to leave Escape swallowed system-wide.
        try
        {
            if (_phase == Phase.Showing) _notch.ResultView.Release();
            else _notch.ResultView.CancelPending();
        }
        catch (Exception ex)
        {
            Log.Warn("Releasing the results view failed", ex);
        }
        _transcribe?.Cancel();
        _transcribe = null;
        _factLookup?.Cancel();
        _factLookup = null;
        _momentFull = null;
        _shot = null;
        _ocr = null;
        _ctx = null;
        _phase = Phase.Idle;
        _keys.InterceptEscape = false;
        _keys.InterceptChoices = false;
        _choices = null;
        _notch.SearchBarView.HideChoices();
        _notch.SetInteractive(false);
        if (!keepNotch) _notch.SetSearchMode(null);
    }

    void DismissOverlay()
    {
        var o = _overlay;
        _overlay = null;
        o?.Dismiss();
    }

    /// <summary>Entry points for the command channel and tests.</summary>
    public Task SearchTextAsync(string text)
    {
        Reset(keepNotch: true);
        _session++;
        return ProcessTextAsync(text);
    }

    public Task SearchImageAsync(byte[] png)
    {
        Reset(keepNotch: true);
        _session++;
        var thumb = new BitmapImage();
        thumb.BeginInit();
        thumb.StreamSource = new MemoryStream(png);
        thumb.CacheOption = BitmapCacheOption.OnLoad;
        thumb.EndInit();
        thumb.Freeze();
        return ShowResultAsync(new Context("", "Image", SearchMode.Lens, SearchFilter.None, png, thumb));
    }

    /// <summary>Test hook: show the results card without a search (used by the browser-hardening test).</summary>
    public void ShowCardForTest()
    {
        Reset(keepNotch: true);
        _phase = Phase.Showing;
        _keys.InterceptEscape = true;
        _notch.ResultView.SetContext("Security test page", null, false, SearchMode.Ai, SearchFilter.None);
        _notch.SetSearchMode(NotchMode.Result);
    }

    public void OpenTyping()
    {
        Reset(keepNotch: true);
        ShowTyping();
    }
}

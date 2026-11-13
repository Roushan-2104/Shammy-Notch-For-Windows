using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Motion;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Shammy.Views;

/// <summary>
/// Google's own results (AI Mode, the Web tab or Lens) rendered inside the notch. Uses the WebView2
/// composition control because a normal WebView2 is a child HWND, which can't live inside a
/// per-pixel-transparent window like the notch.
/// </summary>
public partial class ResultView : UserControl
{
    const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36";

    string? _desktopUserAgent;

    Task? _init;
    readonly SpringValue _loading = new(0, 0.001);
    IDisposable? _loadingFrames;
    bool _isLoading;
    ulong _latestNavigation;

    public ResultView()
    {
        InitializeComponent();
    }

    internal event Action? CloseRequested;
    internal event Action<SearchMode>? ModeChanged;
    internal event Action<SearchFilter>? FiltersChanged;

    internal bool CompactLayout { get; set; } = true;

    public Uri? CurrentUrl => Core?.Source is { Length: > 0 } s ? new Uri(s) : null;

    // ---- The browser's lifetime ------------------------------------------------------------------
    // No browser exists while Shammy is idle: the control (and with it the WebView2 browser processes, ~150 MB)
    // is made when a result is about to show and freed a minute after the card closes. The next search makes a
    // new one (a second or so; the profile on disk keeps cookies such as Google's robot-check exemption).

    /// <summary>How long a closed card keeps its browser, so a quick second search doesn't start a new one.</summary>
    static readonly TimeSpan FreeAfter = TimeSpan.FromSeconds(60);

    WebView2CompositionControl? _web;
    System.Windows.Threading.DispatcherTimer? _freeTimer;
    static volatile string _browserState = "none";

    /// <summary>"none", "loaded" or "closing" (freed, its processes still exiting): for the memory line.</summary>
    internal static string BrowserState => _browserState;

    /// <summary>The browser control, made on first use and put behind the loading bar and the error panel.</summary>
    WebView2CompositionControl Web
    {
        get
        {
            if (_web is not null) return _web;
            _web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x10, 0x12) };
            WebFrame.Children.Insert(0, _web);
            return _web;
        }
    }

    /// <summary>Frees the browser a minute from now unless a search uses it first.</summary>
    void FreeLater()
    {
        _inUse = false;
        if (_web is null) return;
        _freeTimer ??= new System.Windows.Threading.DispatcherTimer(FreeAfter, System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => FreeBrowser(), Dispatcher) { IsEnabled = false };
        _freeTimer.Stop();
        _freeTimer.Start();
    }

    void KeepBrowser()
    {
        _inUse = true;
        _freeTimer?.Stop();
    }

    /// <summary>A search is showing (or about to): the browser stays.</summary>
    bool _inUse;

    /// <summary>Closes the browser control; its WebView2 processes exit once it is gone.</summary>
    void FreeBrowser()
    {
        _freeTimer?.Stop();
        var web = _web;
        if (web is null) return;
        // Showing again (a new search raced the timer): keep it.
        if (_inUse) return;
        _web = null;
        _init = null;
        _generation++;
        WebFrame.Children.Remove(web);
        try { web.Dispose(); }
        catch (Exception ex) { Log.Warn("Freeing the results browser failed", ex); }
        if (_browserState == "loaded") _browserState = "closing";
        Log.Info("Results browser freed after a quiet minute");
    }

    /// <summary>Test hook ("browser-free"): frees the browser now, as a quiet minute would.</summary>
    internal string FreeNowForTest()
    {
        bool had = _web is not null;
        FreeBrowser();
        return had ? "freed" : "no browser";
    }

    /// <summary>Starts the browser engine. Safe to call repeatedly; the first call does the work.</summary>
    internal Task EnsureReadyAsync()
    {
        KeepBrowser();
        return _init ??= InitAsync();
    }

    async Task InitAsync()
    {
        var web = Web;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebView);
            await web.EnsureCoreWebView2Async(env);
            _browserState = "loaded";
            env.BrowserProcessExited += (_, _) =>
            {
                // A newer browser may already be running; only the one that was freed reports "none".
                if (_browserState == "closing") _browserState = "none";
                Log.Info("Results browser processes exited");
            };
            var core = web.CoreWebView2;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            _desktopUserAgent = core.Settings.UserAgent;

            core.NewWindowRequested += (_, e) =>
            {
                // Links that want a new tab go to the user's real browser, but only real web links the
                // user clicked: a page must never get Shammy to shell-execute search-ms:, ms-settings:,
                // file:, or any other protocol handler, or to open windows on its own.
                e.Handled = true;
                if (e.IsUserInitiated) OpenExternal(e.Uri);
            };
            // Same rule for links a page tries to hand to installed apps directly.
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!IsAllowedNavigation(e.Uri))
                {
                    e.Cancel = true;
                    Log.Warn("Blocked a non-web navigation in the results view");
                    return;
                }
                _latestNavigation = e.NavigationId;
                ErrorPanel.Visibility = Visibility.Collapsed;
                Notice.Visibility = Visibility.Collapsed;
                SetLoading(true);
            };
            core.NavigationCompleted += (_, e) =>
            {
                // A navigation that was superseded (redirects, a new search) reports as aborted; only the
                // latest one decides what the card shows.
                Log.Info($"Navigation {e.NavigationId} done: ok={e.IsSuccess} http={e.HttpStatusCode} " +
                         $"latest={e.NavigationId == _latestNavigation} {HostAndPath(core.Source)}");
                if (e.NavigationId != _latestNavigation) return;
                SetLoading(false);
                if (_releasing)
                {
                    SuspendAfterBlank();
                    return;
                }
                // Google's "unusual traffic" check (/sorry/, HTTP 429). The page itself is the fix: the
                // user ticks the box once and the persistent profile keeps the exemption cookie.
                bool robotCheck = e.HttpStatusCode == 429 || (core.Source?.Contains("/sorry/", StringComparison.Ordinal) ?? false);
                Notice.Visibility = robotCheck ? Visibility.Visible : Visibility.Collapsed;
                if (robotCheck)
                {
                    Log.Warn("Google asked for a robot check");
                    return;
                }
                if (e.IsSuccess || e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted)
                    return;
                Log.Warn($"Results navigation failed: {e.WebErrorStatus} ({e.HttpStatusCode})");
                ShowError(e.WebErrorStatus == CoreWebView2WebErrorStatus.Disconnected
                    ? "You're offline. Check your connection and try again."
                    : $"Couldn't load the results ({e.WebErrorStatus}).");
            };
            core.ProcessFailed += (_, e) =>
            {
                Log.Warn($"WebView2 process failed: {e.ProcessFailedKind}");
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    Dispatcher.BeginInvoke(ReplaceCrashedBrowser);
            };
            core.NavigateToString("<html><body style='background:#101012'></body></html>");
            Log.Info($"WebView2 ready ({env.BrowserVersionString})");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _init = null;
            ShowError("Search needs the Microsoft Edge WebView2 Runtime. Install it, then search again.");
            InstallWebView2.Visibility = Visibility.Visible;
            throw;
        }
        catch (Exception ex)
        {
            _init = null;
            Log.Error("WebView2 init failed", ex);
            ShowError("The results view couldn't start. See the log for details.");
            throw;
        }
    }

    /// <summary>
    /// A control whose browser process died can never start again (it throws on every use), so swap in
    /// a fresh one; the next search starts a new engine in it.
    /// </summary>
    void ReplaceCrashedBrowser()
    {
        var old = _web;
        _web = null;
        if (old is not null)
        {
            WebFrame.Children.Remove(old);
            try { old.Dispose(); } catch (Exception ex) { Log.Warn("Disposing the crashed results view failed", ex); }
        }
        _browserState = "none";
        _init = null;
        _generation++;
        if (IsVisible) ShowError("The results view stopped. Search again to restart it.");
        Log.Info("Results view replaced after its browser process exited");
    }

    internal void SetContext(string query, ImageSource? thumbnail, bool lensAvailable, SearchMode mode, SearchFilter filters)
    {
        QueryText.Text = query;
        Thumb.Visibility = thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        QueryIcon.Visibility = thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        Thumb.Background = thumbnail is null ? null : new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill };
        ModeLens.Visibility = lensAvailable ? Visibility.Visible : Visibility.Collapsed;
        SetModeChips(mode);
        FilterReddit.IsChecked = filters.HasFlag(SearchFilter.Reddit);
        FilterRecent.IsChecked = filters.HasFlag(SearchFilter.Recent);
        FilterNoJunk.IsChecked = filters.HasFlag(SearchFilter.NoJunk);
        FilterDocs.IsChecked = filters.HasFlag(SearchFilter.Docs);
        ErrorPanel.Visibility = Visibility.Collapsed;
        UploadBadge.Visibility = Visibility.Collapsed;
        InstallWebView2.Visibility = Visibility.Collapsed;
    }

    internal void SetModeChips(SearchMode mode)
    {
        ModeAi.IsChecked = mode == SearchMode.Ai;
        ModeWeb.IsChecked = mode == SearchMode.Web;
        ModeLens.IsChecked = mode == SearchMode.Lens;
        bool filtersApply = mode != SearchMode.Lens;
        FilterReddit.IsEnabled = FilterRecent.IsEnabled = FilterNoJunk.IsEnabled = FilterDocs.IsEnabled = filtersApply;
        FilterReddit.Opacity = FilterRecent.Opacity = FilterNoJunk.Opacity = FilterDocs.Opacity = filtersApply ? 1 : 0.4;
    }

    internal async Task NavigateAsync(Uri url)
    {
        int generation = _generation;
        await EnsureReadyAsync();
        // Cancelled while the engine was starting: don't open the page in a closed card.
        if (generation != _generation || Core is not { } core) return;
        Wake();
        ErrorPanel.Visibility = Visibility.Collapsed;
        UseUserAgent(mobile: CompactLayout);
        core.Navigate(url.AbsoluteUri);
    }

    /// <summary>
    /// After the card closes: blank the page (stops any video/audio and page scripts) and suspend the
    /// renderer so a hidden results page costs no CPU and less memory until the next search.
    /// </summary>
    /// <summary>Drops a search that is still waiting for the engine to start (the card was cancelled).</summary>
    internal void CancelPending()
    {
        _generation++;
        FreeLater();
    }

    internal void Release()
    {
        _generation++;
        FreeLater();
        var core = Core;
        if (core is null) return;
        _releasing = true;
        core.NavigateToString("<html><body style='background:#101012'></body></html>");
        // No search history left behind on disk. Cookies stay: they hold Google's robot-check exemption.
        _ = ClearHistoryAsync(core);
    }

    static async Task ClearHistoryAsync(CoreWebView2 core)
    {
        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory);
        }
        catch (Exception ex)
        {
            Log.Warn("Clearing results history failed", ex);
        }
    }

    bool _releasing;

    void SuspendAfterBlank()
    {
        _releasing = false;
        // TrySuspend is refused by the composition-hosted controller (ERROR_INVALID_STATE on every call),
        // so ask the browser to trim memory instead; the blank page has already stopped media and scripts.
        if (Core is not { } core) return;
        try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
        catch (Exception ex) { Log.Warn("Lowering results view memory failed", ex); }
    }

    void Wake()
    {
        _releasing = false;
        if (Core is not { } core) return;
        try
        {
            if (core.IsSuspended) core.Resume();
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex) { Log.Warn("Waking results view failed", ex); }
    }

    /// <summary>
    /// The browser engine, or null if it isn't started or its process died: after a crash the control
    /// throws on every access, and a throw in Release used to leave the card stuck open.
    /// </summary>
    CoreWebView2? Core
    {
        get
        {
            try { return _web?.CoreWebView2; }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>Bumped whenever the card is released, so a navigation still waiting on start-up is dropped.</summary>
    int _generation;

    /// <summary>
    /// AI Mode and Web results get a phone layout that fits the card; Lens uploads go out as the
    /// normal desktop Edge identity, since an upload from a "phone" on a desktop IP looks odd to Google.
    /// </summary>
    void UseUserAgent(bool mobile)
    {
        if (Core is not { } core) return;
        var settings = core.Settings;
        var want = mobile ? MobileUserAgent : _desktopUserAgent;
        if (want is not null && settings.UserAgent != want) settings.UserAgent = want;
    }

    /// <summary>
    /// Uploads the selection to Lens the same way lens.google.com's own "upload" button does, so the
    /// results page (and its cookies/consent) behave exactly as in a browser.
    /// </summary>
    internal async Task NavigateLensAsync(byte[] png)
    {
        int generation = _generation;
        await EnsureReadyAsync();
        // Cancelled while the engine was starting: the image must not be uploaded after all.
        if (generation != _generation || Core is null) return;
        Wake();
        ErrorPanel.Visibility = Visibility.Collapsed;
        UploadBadge.Visibility = Visibility.Visible;
        UseUserAgent(mobile: false);
        var core = Core!;
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
        var url = $"https://lens.google.com/v3/upload?hl=en&re=df&stcs={stamp}&ep=gsbubb";
        var request = core.Environment.CreateWebResourceRequest(url, "POST", body,
            $"Content-Type: multipart/form-data; boundary={boundary}");
        core.NavigateWithWebResourceRequest(request);
    }

    internal void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        SetLoading(false);
    }

    void SetLoading(bool loading)
    {
        if (loading == _isLoading) return;
        _isLoading = loading;
        if (loading)
        {
            _loading.Snap(0);
            LoadingBar.Opacity = 1;
            _loading.AnimateTo(0.85, 2.4, 1);
        }
        else
        {
            _loading.AnimateTo(1, 0.25, 1);
        }
        _loadingFrames ??= Animator.Subscribe(_ =>
        {
            LoadingBar.Width = _loading.Value * WebFrame.ActualWidth;
            if (!_isLoading && _loading.IsSettled)
            {
                LoadingBar.Opacity = 0;
                _loadingFrames?.Dispose();
                _loadingFrames = null;
            }
        });
    }

    /// <summary>Module chips after the filters (e.g. "Copy text", "Translate").</summary>
    internal void SetExtraChips(IEnumerable<(string Label, Action OnClick)> chips)
    {
        ExtraChips.Children.Clear();
        bool first = true;
        foreach (var (label, onClick) in chips)
        {
            if (first)
            {
                ExtraChips.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 1, Height = 14, Fill = (Brush)FindResource("WallHairline"), Margin = new Thickness(6, 0, 6, 0),
                });
                first = false;
            }
            var chip = new ToggleButton { Content = label, Style = (Style)FindResource("WallChip") };
            chip.Click += (_, _) =>
            {
                chip.IsChecked = false;
                onClick();
            };
            ExtraChips.Children.Add(chip);
        }
    }

    void OnFrameSizeChanged(object sender, SizeChangedEventArgs e) =>
        WebFrame.Clip = new RectangleGeometry(new Rect(e.NewSize), 18, 18);

    void OnModeClick(object sender, RoutedEventArgs e)
    {
        var mode = sender == ModeWeb ? SearchMode.Web : sender == ModeLens ? SearchMode.Lens : SearchMode.Ai;
        SetModeChips(mode);
        ModeChanged?.Invoke(mode);
    }

    void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var f = SearchFilter.None;
        if (FilterReddit.IsChecked == true) f |= SearchFilter.Reddit;
        if (FilterRecent.IsChecked == true) f |= SearchFilter.Recent;
        if (FilterNoJunk.IsChecked == true) f |= SearchFilter.NoJunk;
        if (FilterDocs.IsChecked == true) f |= SearchFilter.Docs;
        FiltersChanged?.Invoke(f);
    }

    void OnReload(object sender, RoutedEventArgs e) => Core?.Reload();

    async void OnFullPageScreenshot(object sender, RoutedEventArgs e)
    {
        FullPageButton.IsEnabled = false;
        try
        {
            var image = await CaptureFullPageAsync();
            if (image is not null) Search.ScreenshotSaver.Save(image, "page");
        }
        catch (Exception ex)
        {
            Log.Warn("Full-page screenshot failed", ex);
        }
        finally
        {
            FullPageButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The whole page top to bottom, not just what's visible, via the browser's own DevTools capture.
    /// Very long pages are capped so the PNG stays a sensible size.
    /// </summary>
    internal async Task<BitmapSource?> CaptureFullPageAsync()
    {
        if (Core is not { } core) return null;
        int generation = _generation;
        var source = core.Source;
        const int MaxHeight = 16000;

        using var metrics = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
        var size = metrics.RootElement.TryGetProperty("cssContentSize", out var css) ? css : metrics.RootElement.GetProperty("contentSize");
        double width = Math.Ceiling(size.GetProperty("width").GetDouble());
        double height = Math.Min(MaxHeight, Math.Ceiling(size.GetProperty("height").GetDouble()));

        var args = System.FormattableString.Invariant(
            $"{{\"format\":\"png\",\"captureBeyondViewport\":true,\"clip\":{{\"x\":0,\"y\":0,\"width\":{width},\"height\":{height},\"scale\":1}}}}");
        using var shot = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args));
        // The card was closed (and the page blanked) mid-capture: don't save a dark empty image.
        if (generation != _generation || core.Source != source) return null;
        var bytes = Convert.FromBase64String(shot.RootElement.GetProperty("data").GetString() ?? "");

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        if (CurrentUrl is { } url && url.Scheme.StartsWith("http")) OpenExternal(url.AbsoluteUri);
        CloseRequested?.Invoke();
    }

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    void OnDismissNotice(object sender, RoutedEventArgs e) => Notice.Visibility = Visibility.Collapsed;

    /// <summary>For the log: host and path only, never the query string (it holds what you searched).</summary>
    static string HostAndPath(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host + u.AbsolutePath : u?.Scheme ?? "";

    /// <summary>Opens a web link in the default browser. Anything that isn't plain http(s) is refused.</summary>
    internal async Task LoadTestHtmlAsync(string html)
    {
        await EnsureReadyAsync();
        Wake();
        Core?.NavigateToString(html);
    }

    void OnInstallWebView2(object sender, RoutedEventArgs e)
    {
        WebView2Check.OpenDownload();
    }

    internal static void OpenExternal(string url)
    {
        if (!IsWebUrl(url, out var uri))
        {
            Log.Warn("Refused to open a non-web link from the results view");
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Opening a link failed", ex); }
    }

    /// <summary>http/https to a named host only (no UNC, no file:, no protocol handlers).</summary>
    static bool IsWebUrl(string? url, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
               && parsed.Scheme is "http" or "https" && !parsed.IsUnc && parsed.Host.Length > 0
               && (uri = parsed) is not null;
    }

    /// <summary>What the card itself may load: the web, plus the blank page Shammy shows between searches.</summary>
    static bool IsAllowedNavigation(string? url) =>
        IsWebUrl(url, out _)
        || string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)
        || (url?.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) ?? false);
}

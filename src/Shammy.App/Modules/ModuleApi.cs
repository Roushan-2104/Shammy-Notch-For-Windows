using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Infrastructure;
using Shammy.Shelf;
using Shammy.Shell;
using Shammy.Views;

namespace Shammy.Modules;

/// <summary>
/// A self-contained feature (timers, clipboard, music commands...). Every non-abstract class that
/// implements this is found by reflection at startup and started once, so adding a feature never
/// means editing the host. Keep everything a module needs in its own folder under Modules/.
/// </summary>
internal interface IShammyModule : IDisposable
{
    string Name { get; }

    /// <summary>Called once on the UI thread after the notch and core services exist.</summary>
    void Start(ModuleContext ctx);
}

/// <summary>Text typed in the search box (tap Right Alt) or spoken (hold Right Alt), e.g. "timer 25m".</summary>
internal interface ITextCommand
{
    /// <summary>Cheap synchronous check whether the text is this command. Checked before web search.</summary>
    bool CanHandle(string text);

    Task<CommandResult> RunAsync(string text);
}

/// <summary>
/// Optional on an <see cref="ITextCommand"/>: what Enter would do, shown as the command row in the typing
/// palette ("= 42", "Start a 25 min timer"). Called on every keystroke, so it must be cheap and have no
/// side effects; return null when it can't tell, and the row just says the command will run.
/// </summary>
internal interface ICommandPreview
{
    CommandPreview? Preview(string text);
}

internal sealed record CommandPreview(string Glyph, string Title, string? Detail = null);

/// <summary>
/// What the notch shows briefly after a command ran. With <see cref="SearchInstead"/> set, nothing is shown and
/// that text is searched on the web instead: for a command that only learns while running that it can't answer
/// ("weather in" a place the geocoder doesn't know).
/// </summary>
internal sealed record CommandResult(string Glyph, string Title, string? Detail = null, bool IsError = false, string? SearchInstead = null)
{
    /// <summary>What the confirmation's Copy button copies (a converted amount, a time); null = no Copy button.</summary>
    public string? CopyText { get; init; }

    /// <summary>A page to show in the results card instead of the confirmation (a translation), like a search would.</summary>
    public ResultsPage? OpenInResults { get; init; }
}

/// <param name="Query">What the results card's header shows (and its Translate chip works on).</param>
internal sealed record ResultsPage(Uri Url, string Query);

/// <summary>
/// Content a module draws in the compact notch's two ears. Put an instance in
/// <see cref="Activity.Payload"/> with <see cref="ActivityKind.Custom"/>. Re-posting the same instance
/// keeps the built UI (update your own elements for live values); a new instance rebuilds.
/// </summary>
internal interface ICompactContent
{
    /// <summary>Fill the ears and return the width one ear needs (the pill grows to fit).</summary>
    double Build(ContentControl left, ContentControl right);

    /// <summary>Optional content for the small detached bubble when this activity is the secondary one.</summary>
    UIElement? BuildBubble() => null;
}

/// <summary>What was circled on the frozen screen.</summary>
internal sealed record SelectionInfo(string Text, byte[]? Png, BitmapSource? Image, bool IsImage);

/// <summary>Gets first look at every circle; can take over instead of a web search (e.g. a QR code).</summary>
internal interface ISelectionHandler
{
    Task<CommandResult?> TryTakeOverAsync(SelectionInfo selection);
}

/// <summary>An extra chip on the results card. <see cref="Selection"/> is null for typed/spoken searches.</summary>
internal sealed record ResultChip(string Label, Func<SelectionInfo?, bool> IsVisible, Action<SelectionInfo?, ResultView> OnClick);

internal sealed record ShelfAction(string Label, Action<ShelfItem> Run)
{
    /// <summary>Whether the action is offered for this item (Convert only for images); null means always.</summary>
    public Func<ShelfItem, bool>? AppliesTo { get; init; }
}

/// <param name="IsVisible">Asked each time the tray menu opens; null = always shown.</param>
internal sealed record ModuleTrayItem(string Text, Action? OnClick = null, Func<bool>? IsChecked = null, Action<bool>? SetChecked = null,
    Func<bool>? IsVisible = null);

/// <summary>Everything modules register; the host and the search flow read from here.</summary>
internal sealed class ModuleRegistry
{
    public List<ITextCommand> Commands { get; } = new();
    public List<ISelectionHandler> SelectionHandlers { get; } = new();
    public List<ResultChip> ResultChips { get; } = new();
    public List<ShelfAction> ShelfActions { get; } = new();
    public List<ModuleTrayItem> TrayItems { get; } = new();
    public Dictionary<string, Func<string, string>> PipeCommands { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Glance providers by id, for the resting notch and the peek (see <see cref="GlanceItem"/>).</summary>
    public Dictionary<string, Func<GlanceItem?>> Glances { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Words voice recognition is primed with (app names, ...); read when a voice model loads.</summary>
    public List<Func<IEnumerable<string>>> Vocabulary { get; } = new();

    /// <summary>Song and artist names a play command's second voice pass is primed with; read per command,
    /// from the transcription thread.</summary>
    public List<Func<IEnumerable<string>>> SongVocabulary { get; } = new();

    /// <summary>Raised when a module's vocabulary changed (e.g. the app list finished indexing).</summary>
    public event Action? VocabularyChanged;

    /// <summary>The circle-to-search image path (Google Lens on a PNG); set by the host once search exists.</summary>
    public Func<byte[], Task>? SearchImage { get; set; }

    /// <summary>Every module's name, started or disabled (for the settings' module switches).</summary>
    public List<string> ModuleNames { get; } = new();

    internal void OnVocabularyChanged() => VocabularyChanged?.Invoke();
}

/// <summary>A module's handle on Shammy. One per module.</summary>
internal sealed class ModuleContext
{
    static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        // Every module only fetches small JSON/RSS; cap responses so a hostile server can't balloon memory.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Shammy/0.1");
        return http;
    });

    readonly ActivityManager _activities;
    readonly ModuleRegistry _registry;

    internal ModuleContext(string name, Dispatcher ui, ShammyServices services, NotchWindow notch,
        ActivityManager activities, ModuleRegistry registry)
    {
        Name = name;
        Ui = ui;
        Services = services;
        Notch = notch;
        _activities = activities;
        _registry = registry;
    }

    public string Name { get; }
    public Dispatcher Ui { get; }
    public ShammyServices Services { get; }
    public AppSettings Settings => Services.Settings;
    public NotchWindow Notch { get; }

    /// <summary>Shared client with a browser-like user agent and a 15 s timeout.</summary>
    public HttpClient Http => SharedHttp.Value;

    // ---- Activities (compact notch) -------------------------------------------------------------

    /// <summary>Show or update an activity. Prefix ids with your module name. Higher priority wins the pill
    /// (HUD 90, notifications 80, battery 70-75, Bluetooth 60, messages 50, downloads 40, media 20).</summary>
    public void Post(Activity activity) => _activities.Post(activity);

    public bool Remove(string id) => _activities.Remove(id);

    /// <summary>Every module's live activities (read-only use: the phone link mirrors them to the phone's notch).</summary>
    internal ActivityManager Activities => _activities;
    public Activity? Get(string id) => _activities.Get(id);

    /// <summary>A short message in the compact notch.</summary>
    public void ShowMessage(string glyph, string text, bool error = false, double seconds = 3) =>
        Post(new Activity($"{Name}.message", ActivityKind.Message, 50, new MessagePayload(glyph, text, error),
            DateTime.UtcNow.AddSeconds(seconds)));

    // ---- Registration ----------------------------------------------------------------------------

    public void AddCommand(ITextCommand command) => _registry.Commands.Add(command);

    /// <summary>
    /// A command tried before every other module's (modules start in name order, so a broad matcher such as the
    /// app launcher's "open …" would otherwise claim an exact phrase like "open camera"). Only for phrases that
    /// match a fixed list, never for open-ended patterns.
    /// </summary>
    public void AddCommandFirst(ITextCommand command) => _registry.Commands.Insert(0, command);

    /// <summary>Every module's commands, in the order the search flow tries them (the palette previews them).</summary>
    internal IReadOnlyList<ITextCommand> Commands => _registry.Commands;

    /// <summary>Every module's name, including disabled ones.</summary>
    internal IReadOnlyList<string> ModuleNames => _registry.ModuleNames;

    /// <summary>Offer words for voice recognition to expect (e.g. installed app names).</summary>
    public void AddVocabulary(Func<IEnumerable<string>> words) => _registry.Vocabulary.Add(words);

    /// <summary>Offer song titles and artists for play commands to expect, best first. Called off the UI
    /// thread, so return a snapshot.</summary>
    public void AddSongVocabulary(Func<IEnumerable<string>> names) => _registry.SongVocabulary.Add(names);

    /// <summary>Call after the words from <see cref="AddVocabulary"/> changed.</summary>
    public void VocabularyChanged() => _registry.OnVocabularyChanged();
    public void AddSelectionHandler(ISelectionHandler handler) => _registry.SelectionHandlers.Add(handler);
    public void AddResultChip(ResultChip chip) => _registry.ResultChips.Add(chip);
    public void AddShelfAction(ShelfAction action) => _registry.ShelfActions.Add(action);
    /// <summary>A tray menu item; with <paramref name="visible"/> it shows only while that says so (e.g. "Disconnect" while connected).</summary>
    public void AddTrayItem(string text, Action onClick, Func<bool>? visible = null) =>
        _registry.TrayItems.Add(new ModuleTrayItem(text, onClick, IsVisible: visible));

    public void AddTrayToggle(string text, Func<bool> isChecked, Action<bool> setChecked) =>
        _registry.TrayItems.Add(new ModuleTrayItem(text, null, isChecked, setChecked));

    /// <summary>A command for the local test pipe, e.g. "timer-test 5".</summary>
    public void AddPipeCommand(string name, Func<string, string> handler) => _registry.PipeCommands[name] = handler;

    /// <summary>A card on the open notch's home row (v2 design); see <see cref="NotchCard"/>.</summary>
    public void AddCard(NotchCard card) => Notch.ExpandedView.AddCard(card);

    /// <summary>A source for the resting notch's rotating glance and the hover peek; see <see cref="GlanceItem"/>.</summary>
    public void AddGlance(string id, Func<GlanceItem?> provider) => _registry.Glances[id] = provider;

    /// <summary>Call when a card's <see cref="NotchCard.IsRelevant"/> answer may have changed.</summary>
    public void CardsChanged() => Notch.ExpandedView.CardsChanged();

    /// <summary>
    /// Open the notch on a card's detail (by <see cref="NotchCard.Id"/>, e.g. "weather"), in place, as if its card
    /// or chip had been clicked. Safe to call from a command's RunAsync: it opens right after the confirmation
    /// card closes. With <paramref name="hold"/> (the default) it stays open until the pointer has visited and
    /// left, or 8 s if the user never comes over; no timers needed. Returns false for an unknown id.
    /// </summary>
    public bool OpenCard(string cardId, bool hold = true) => Notch.OpenView(cardId, hold);

    // ---- Cards, glow, input ----------------------------------------------------------------------

    /// <summary>
    /// Show arbitrary content in the notch (e.g. a QR code). The notch morphs to width×height. With
    /// <paramref name="interactive"/> it takes keyboard focus and closes when you click elsewhere.
    /// With <paramref name="breaksFullscreen"/> (alarms, due reminders) it shows over a fullscreen app too,
    /// without taking focus from it; other cards wait until fullscreen ends.
    /// </summary>
    public void ShowCard(FrameworkElement content, double width, double height, bool interactive = false, bool breaksFullscreen = false) =>
        Notch.ShowCard(content, width, height, interactive, breaksFullscreen);

    public void CloseCard() => Notch.CloseCard();

    /// <summary>Searches an image with Google Lens in the results card, exactly like circling it on screen.</summary>
    public Task SearchImageAsync(byte[] png) => _registry.SearchImage?.Invoke(png) ?? Task.CompletedTask;

    /// <summary>Keep the notch's edge lit in a colour while a state lasts; null removes it.</summary>
    /// <param name="cheer">False for a glow that isn't news (the island warming before an alarm): the pet stays as it is.</param>
    public void SetGlow(Color? color, bool cheer = true) => Notch.SetGlow(color, cheer);

    /// <summary>Briefly light the notch's edge in a colour (an event, not a state).</summary>
    public void FlashEdge(Color color) => Notch.FlashEdge(color);

    // The wheel events subscribe straight to the notch, so a module that never listens leaves no handler behind.

    /// <summary>Vertical mouse wheel over the compact/idle notch (delta, 120 per notch).</summary>
    public event Action<int>? NotchWheel { add => Notch.NotchWheel += value; remove => Notch.NotchWheel -= value; }

    /// <summary>Horizontal wheel / two-finger swipe over the notch (positive = right).</summary>
    public event Action<int>? NotchHorizontalWheel { add => Notch.NotchHorizontalWheel += value; remove => Notch.NotchHorizontalWheel -= value; }

    // ---- Storage and helpers ---------------------------------------------------------------------

    /// <summary>
    /// Load this module's data (%LOCALAPPDATA%\Shammy\modules\&lt;name&gt;.dat): JSON encrypted with Windows
    /// DPAPI, so only this Windows account can read it. A plain &lt;name&gt;.json from older versions is
    /// still read, and replaced by the encrypted file on the next save.
    /// </summary>
    public T Load<T>() where T : new()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<T>(json) ?? new T();
            }
            if (File.Exists(LegacyPath)) return JsonSerializer.Deserialize<T>(File.ReadAllText(LegacyPath)) ?? new T();
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or UnauthorizedAccessException)
        {
            // Never let the next save overwrite it: a store copied from another PC or account (DPAPI can't
            // decrypt it) still holds the user's notes. Keep it next to the new one and say so.
            Log.Warn($"{Name}: data unreadable, starting fresh", ex);
            var kept = KeepUnreadable();
            if (kept is not null)
                Ui.BeginInvoke(() => ShowMessage("", $"Couldn't read saved {Name} data", error: true));
        }
        return new T();
    }

    public void Save<T>(T data)
    {
        try
        {
            var tmp = StorePath + ".tmp";
            var json = JsonSerializer.SerializeToUtf8Bytes(data);
            File.WriteAllBytes(tmp, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, StorePath, true);
            if (File.Exists(LegacyPath)) File.Delete(LegacyPath);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            Log.Warn($"{Name}: saving data failed", ex);
        }
    }

    /// <summary>Renames an unreadable store aside (…unreadable-yyyyMMdd-HHmmss); returns the new path or null.</summary>
    string? KeepUnreadable()
    {
        foreach (var path in new[] { StorePath, LegacyPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(path, aside);
                Log.Warn($"{Name}: kept the unreadable file as {Path.GetFileName(aside)}");
                return aside;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"{Name}: couldn't move the unreadable file aside", ex);
            }
        }
        return null;
    }

    static readonly byte[] Entropy = "Halo module store v1"u8.ToArray();

    string StorePath => Path.Combine(StoreDir, $"{Name}.dat");
    string LegacyPath => Path.Combine(StoreDir, $"{Name}.json");

    static string StoreDir
    {
        get
        {
            var dir = Path.Combine(Paths.Root, "modules");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Open a URL, file or shell target (e.g. "shell:AppsFolder\&lt;AUMID&gt;").</summary>
    /// <returns>False when Windows couldn't open it (deleted shortcut, uninstalled app, no handler).</returns>
    public bool Open(string target, string? arguments = null)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = arguments ?? "",
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"{Name}: opening something failed", ex);
            return false;
        }
    }

    public void Info(string message) => Log.Info($"[{Name}] {message}");
    public void Warn(string message, Exception? ex = null) => Log.Warn($"[{Name}] {message}", ex);
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Shammy.Core.Cards;
using Shammy.Views.Cards;

namespace Shammy.Modules.Mirror;

/// <summary>Remembered between opens: mirrored or not, and the brightness step.</summary>
internal sealed class MirrorStore
{
    public bool Mirrored { get; set; } = true;
    public int Level { get; set; } = 50;
}

/// <summary>
/// A webcam mirror for a quick look before a call. The camera runs only while the mirror's detail is on
/// screen: it starts when the detail shows and is released when it closes, fades, or the PC locks.
/// Chip-only on the home row (never "relevant"), so the camera is always something you opened.
/// </summary>
internal sealed class MirrorModule : IShammyModule
{
    ModuleContext _ctx = null!;
    MirrorStore _store = new();
    MirrorView? _view;
    bool _locked;

    public string Name => "mirror";

    internal MirrorStore Store => _store;
    internal bool Locked => _locked;

    /// <summary>The running module, for Stage's small mirror (it uses the same camera and the same Flip and brightness).</summary>
    internal static MirrorModule? Current { get; private set; }

    /// <summary>The PC was locked or unlocked (Stage releases its camera while locked).</summary>
    internal event Action<bool>? LockChanged;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        Current = this;
        _store = ctx.Load<MirrorStore>();
        _store.Level = Math.Clamp(_store.Level / 10 * 10, 0, 100);

        ctx.AddCard(new NotchCard
        {
            Id = "mirror",
            Title = "Mirror",
            Width = 176,
            Order = 90,
            Chip = true,
            IsRelevant = () => false,
            Compact = BuildCompact(),
            Detail = () =>
            {
                _view = new MirrorView(this, ctx.Ui);
                return _view;
            },
        });
        ctx.AddPipeCommand("mirror-state", _ => State());
        // Opens the mirror's detail in the notch (and so the camera), as the chip or "mirror" would.
        ctx.AddPipeCommand("mirror-open", _ => ctx.OpenCard("mirror") ? "ok" : "error: no mirror card");
        // "mirror", "how do I look", "open camera": before the launcher, which would start Windows' Camera app.
        ctx.AddCommandFirst(new MirrorCommand(ctx));
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    sealed class MirrorCommand(ModuleContext ctx) : ITextCommand, ICommandPreview
    {
        const string Glyph = "";

        public bool CanHandle(string text) => CardPhrases.IsMirror(text);

        public Task<CommandResult> RunAsync(string text)
        {
            // The camera starts only once the detail is on screen, after this confirmation has closed.
            ctx.OpenCard("mirror");
            return Task.FromResult(new CommandResult(Glyph, "Mirror", "The camera stays off until it opens"));
        }

        public CommandPreview? Preview(string text) => new(Glyph, "Open the mirror", "A quick look in your camera");
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect;
        bool unlocked = e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect;
        if (!locked && !unlocked) return;
        _ctx.Ui.BeginInvoke(() =>
        {
            _locked = locked;
            _view?.Evaluate();
            LockChanged?.Invoke(locked);
        });
    }

    internal void Save() => _ctx.Save(_store);

    internal void OpenSettings() => _ctx.Open("ms-settings:privacy-webcam");

    internal void Info(string message) => _ctx.Info(message);

    FrameworkElement BuildCompact() => MirrorTile.Build(() => _ctx.OpenCard("mirror"));

    string State()
    {
        var v = _view;
        bool open = v is { IsVisible: true };
        return string.Create(CultureInfo.InvariantCulture,
            $"detail={(open ? "open" : "closed")} camera={(v?.CameraState ?? "stopped")} frames={v?.Frames ?? 0} size={v?.FrameSize ?? "-"} {v?.Diagnostics} " +
            $"mirrored={_store.Mirrored} level={_store.Level} locked={_locked} problem={v?.Problem.ToString() ?? "-"}");
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _view?.Shutdown();
    }
}

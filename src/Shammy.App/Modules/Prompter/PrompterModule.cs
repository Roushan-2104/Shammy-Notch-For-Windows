using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Shammy.Core.Captions;
using Shammy.Core.Prompter;
using Shammy.Core.Stage;
using Shammy.Modules.Stage;

namespace Shammy.Modules.Prompter;

/// <summary>What Stage remembers: the last script and pace, the text size, and the ring light, voice and mirror choices.</summary>
internal sealed class PrompterData
{
    public string Script { get; set; } = "";
    public PrompterPace Pace { get; set; } = PrompterPace.Normal;
    public double TextSize { get; set; } = StageText.DefaultSize;
    public bool Ring { get; set; } = true;
    public double RingStrength { get; set; } = StageText.DefaultRing;
    public bool Voice { get; set; } = true;
    public bool Mirror { get; set; }
}

/// <summary>
/// Stage, the teleprompter under the camera (see <see cref="StageCard"/>): "stage" or "teleprompter" opens the last
/// script, "stage from clipboard" takes what was copied, "stage C:\path\script.txt" a text file; the wheel's Stage
/// droplet opens it too. While it is open the notch is hidden from screen recordings and screen shares, so the
/// people watching never see the script.
/// Pipes: "prompter-state", "prompter-open", "prompter-load &lt;text&gt;" (\n for new lines), "prompter-key space",
/// "prompter-pace slow|normal|fast", "prompter-seek &lt;0-100&gt;", "prompter-nudge &lt;pixels&gt;", "prompter-close",
/// and "stage-state", "stage-open", "stage-close", "stage-voice &lt;level 0-1&gt;|off" (a microphone level instead of
/// the microphone), "stage-ring on|off|toggle|&lt;0-100&gt;", "stage-captions-line" (what the quiet line shows),
/// "stage-key space|left|right|plus|minus|esc|l|c|m|v" (as if pressed with the pointer on Stage).
/// </summary>
internal sealed class PrompterModule : IShammyModule, Connect.IStageLink
{
    const string Glyph = "\uE8A5";

    ModuleContext _ctx = null!;
    PrompterData _data = new();
    StageCard? _card;
    bool _showInCaptures;
    // The screen freeze for circle-to-search also switches capture hiding; this puts it back while we are open.
    readonly DispatcherTimer _keepHidden = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };

    public string Name => "prompter";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _data = ctx.Load<PrompterData>();
        _keepHidden.Tick += (_, _) =>
        {
            // Something else took the notch (an alarm, another card): we are closed.
            if (_card is null || _card.Parent is null) Closed();
            else ctx.Notch.ExcludeFromCapture(!_showInCaptures);
        };
        ctx.AddCommand(new Command(this));
        ctx.AddTrayItem("Stage", () => Open());
        Connect.Links.Stage = this;

        ctx.AddPipeCommand("prompter-state", _ => State());
        ctx.AddPipeCommand("stage-state", _ => State());
        ctx.AddPipeCommand("prompter-open", _ =>
        {
            Open();
            return State();
        });
        ctx.AddPipeCommand("stage-open", _ =>
        {
            Open();
            return State();
        });
        ctx.AddPipeCommand("prompter-load", arg =>
        {
            var said = Load(arg.Replace("\\n", "\n"));
            Open();
            return said + " · " + State();
        });
        ctx.AddPipeCommand("prompter-key", Key);
        ctx.AddPipeCommand("stage-key", Key);
        ctx.AddPipeCommand("prompter-pace", arg =>
        {
            if (!Enum.TryParse<PrompterPace>(arg.Trim(), true, out var pace)) return "error: slow, normal or fast";
            SavePace(pace);
            _card?.SetPace(pace);
            return State();
        });
        ctx.AddPipeCommand("prompter-seek", arg =>
        {
            if (_card is null || !double.TryParse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return "closed or not a number";
            _card.Seek(p);
            return State();
        });
        // The old teleprompter scrolled by pixels; Stage moves by lines (about one per 33 pixels, at least one).
        ctx.AddPipeCommand("prompter-nudge", arg =>
        {
            if (_card is null || !double.TryParse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) return "closed or not a number";
            if (px != 0) _card.Step(Math.Sign(px) * Math.Max(1, (int)Math.Round(Math.Abs(px) / 33)));
            return State();
        });
        // Test only: "prompter-capture show" lets a screenshot see Stage; "hide" is the real behaviour.
        ctx.AddPipeCommand("prompter-capture", arg =>
        {
            _showInCaptures = arg.Trim() == "show";
            if (_card is not null) ctx.Notch.ExcludeFromCapture(!_showInCaptures);
            return _showInCaptures ? "shown in captures (test)" : "hidden from captures";
        });
        ctx.AddPipeCommand("prompter-close", _ =>
        {
            Close();
            return State();
        });
        ctx.AddPipeCommand("stage-close", _ =>
        {
            Close();
            return State();
        });
        ctx.AddPipeCommand("stage-voice", arg =>
        {
            if (!StageText.TryVoiceLevel(arg, out var level)) return "error: usage stage-voice <level 0-1>|off";
            if (_card is null) return "closed";
            _card.Simulate(level);
            return State();
        });
        ctx.AddPipeCommand("stage-ring", arg =>
        {
            if (!StageText.TryRing(arg, _data.Ring, _data.RingStrength, out var on, out var strength)) return "error: usage stage-ring on|off|toggle|<0-100>";
            if (_card is not null) _card.SetRing(on, strength);
            else
            {
                _data.Ring = on;
                _data.RingStrength = strength;
                _ctx.Save(_data);
            }
            return $"ring={(on ? "on" : "off")} strength={strength.ToString("0.00", CultureInfo.InvariantCulture)} light={_card?.Ring ?? "closed"}";
        });
        ctx.AddPipeCommand("stage-captions-line", _ => _card is null ? "closed" : $"captions={Captions.CaptionsFeed.IsOn} {_card.QuietLine}");
    }

    CommandResult Connect.IStageLink.Present(string text)
    {
        var said = Load(text);
        if (said == "Nothing to read there") return new CommandResult(Glyph, said, IsError: true);
        // After the moment that asked has gone: Stage takes the island.
        _ctx.Ui.BeginInvoke(Open, DispatcherPriority.Background);
        return new CommandResult(Glyph, "On Stage", said);
    }

    public void Dispose()
    {
        if (Connect.Links.Stage == this) Connect.Links.Stage = null;
        _keepHidden.Stop();
        _card?.Shutdown();
        if (_card is not null) _ctx.Notch.ExcludeFromCapture(false);
    }

    string State() => _card is { Parent: not null } card
        ? $"open {card.Describe()} hiddenFromCapture={!_showInCaptures} script={_data.Script.Length}"
        : $"closed pace={_data.Pace} size={_data.TextSize:0} ring={_data.Ring} voice={_data.Voice} mirror={_data.Mirror} script={_data.Script.Length}";

    string Key(string arg)
    {
        if (_card is null) return "closed";
        StageKey? key = arg.Trim().ToLowerInvariant() switch
        {
            "space" => StageKey.Space,
            "left" or "up" or "back" => StageKey.Back,
            "right" or "down" or "on" => StageKey.On,
            "plus" or "+" => StageKey.Bigger,
            "minus" or "-" => StageKey.Smaller,
            "esc" or "escape" => StageKey.Close,
            "l" => StageKey.Light,
            "c" => StageKey.Captions,
            "m" => StageKey.Mirror,
            "v" => StageKey.Voice,
            _ => null,
        };
        if (key is not { } k) return "error: space, left, right, plus, minus, esc, l, c, m or v";
        _card.OnKey(k);
        return State();
    }

    string Script => _data.Script.Length > 0 ? _data.Script : PrompterText.Example;

    /// <summary>Takes a new script; returns what to tell the user.</summary>
    string Load(string raw)
    {
        var (text, cut) = PrompterText.Clean(raw);
        if (text.Length == 0) return "Nothing to read there";
        _data.Script = text;
        _ctx.Save(_data);
        int words = PrompterText.Words(text);
        return cut ? $"Script loaded, cut to the first {words:N0} words" : $"{words:N0} words · about {PrompterText.Length(words, _data.Pace)}";
    }

    void SavePace(PrompterPace pace)
    {
        if (_data.Pace == pace) return;
        _data.Pace = pace;
        _ctx.Save(_data);
    }

    void Open()
    {
        // A new script (or opening again) starts a fresh Stage in the same place.
        if (_card is { } old)
        {
            _card = null;
            old.Shutdown();
        }
        StageCard? card = null;
        card = new StageCard(Script, _data, new StageHost(
            _ctx.Ui,
            Save: () => _ctx.Save(_data),
            Close: Close,
            Resize: (w, h) =>
            {
                if (ReferenceEquals(_card, card) && card!.Parent is not null) _ctx.ShowCard(card, w, h, interactive: false);
            },
            SetCaptions: SetCaptionsAsync,
            Monitor: () => _ctx.Notch.Monitor,
            Notch: () => new System.Windows.Interop.WindowInteropHelper(_ctx.Notch).Handle));
        _card = card;
        // Not interactive: the keyboard stays with the camera or meeting app (Stage's keys work while the pointer is on it).
        _ctx.ShowCard(card, card.CardWidth, card.CardHeight, interactive: false);
        _ctx.Notch.ExcludeFromCapture(!_showInCaptures);
        _keepHidden.Start();
    }

    void Close()
    {
        if (_card?.Parent is not null) _ctx.CloseCard();
        Closed();
    }

    void Closed()
    {
        if (_card is null && !_keepHidden.IsEnabled) return;
        _keepHidden.Stop();
        _card?.Shutdown();
        _card = null;
        // A recording keeps the notch out of its own video.
        _ctx.Notch.ExcludeFromCapture(Record.RecordModule.Current is { } r && (r.IsRecording || r.IsSaving));
    }

    /// <summary>Turns live captions on or off through the captions command, as typing "captions on" would; returns what went wrong, or null.</summary>
    async Task<string?> SetCaptionsAsync(bool on)
    {
        string phrase = on ? "captions on" : "captions off";
        var command = _ctx.Commands.FirstOrDefault(c => c is not Command && CaptionPhrases.Parse(phrase) is not null && c.CanHandle(phrase));
        if (command is null) return "Live captions aren’t available";
        try
        {
            var result = await command.RunAsync(phrase);
            return result.IsError ? result.Title : null;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Stage: switching captions failed", ex);
            return "Couldn’t switch captions";
        }
    }

    CommandResult Run(PrompterCommand command)
    {
        string detail;
        switch (command.Kind)
        {
            case PrompterCommandKind.Close:
                bool was = _card?.Parent is not null;
                Close();
                return new CommandResult(Glyph, was ? "Stage closed" : "Stage isn’t open");
            case PrompterCommandKind.FromClipboard:
                string copied;
                try { copied = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch (Exception ex)
                {
                    _ctx.Warn("Reading the clipboard for Stage failed", ex);
                    return new CommandResult(Glyph, "Couldn’t read the clipboard", "Copy your script again, then retry", IsError: true);
                }
                if (string.IsNullOrWhiteSpace(copied)) return new CommandResult(Glyph, "No text copied", "Copy your script first", IsError: true);
                detail = Load(copied);
                break;
            case PrompterCommandKind.FromFile:
                try
                {
                    var file = new FileInfo(command.Path!);
                    if (!file.Exists) return new CommandResult(Glyph, "Couldn’t find that file", file.Name, IsError: true);
                    if (file.Length > 2 * 1024 * 1024) return new CommandResult(Glyph, "That file is too big for a script", "Use a text file under 2 MB", IsError: true);
                    detail = Load(File.ReadAllText(file.FullName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    _ctx.Warn("Reading the script file failed", ex);
                    return new CommandResult(Glyph, "Couldn’t open that file", "Check the path and try again", IsError: true);
                }
                break;
            default:
                detail = _data.Script.Length > 0 ? "Your last script" : "An example script to start with";
                break;
        }
        // After the confirmation has had its moment, Stage takes the notch.
        _ctx.Ui.BeginInvoke(() => _ctx.Notch.AfterSearch(Open), DispatcherPriority.Background);
        return new CommandResult(Glyph, "Stage", detail);
    }

    sealed class Command(PrompterModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => PrompterText.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) =>
            Task.FromResult(PrompterText.Parse(text) is { } command ? module.Run(command) : new CommandResult(Glyph, "Stage"));

        public CommandPreview? Preview(string text) => PrompterText.Parse(text) switch
        {
            { Kind: PrompterCommandKind.Close } => new(Glyph, "Close Stage"),
            { Kind: PrompterCommandKind.FromClipboard } => new(Glyph, "Stage with what you copied", "Hidden from screen shares and recordings"),
            { Kind: PrompterCommandKind.FromFile } c => new(Glyph, $"Stage with {Path.GetFileName(c.Path)}", "Hidden from screen shares and recordings"),
            { } => new(Glyph, "Open Stage", "Your script under the camera, moving as you speak"),
            _ => null,
        };
    }
}

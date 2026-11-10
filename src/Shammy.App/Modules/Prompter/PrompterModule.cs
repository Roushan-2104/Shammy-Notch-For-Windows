using System.Windows;
using System.Windows.Threading;
using Shammy.Core.Prompter;

namespace Shammy.Modules.Prompter;

/// <summary>What the teleprompter remembers: the last script and pace.</summary>
internal sealed class PrompterData
{
    public string Script { get; set; } = "";
    public PrompterPace Pace { get; set; } = PrompterPace.Normal;
}

/// <summary>
/// A teleprompter under the camera: "teleprompter" opens the last script, "teleprompter from clipboard" takes what
/// was copied, "teleprompter C:\path\script.txt" a text file. While it is open the notch is hidden from screen
/// recordings and screen shares, so the people watching never see the script.
/// Pipes: "prompter-state", "prompter-open", "prompter-load &lt;text&gt;" (\n for new lines), "prompter-key space",
/// "prompter-pace slow|normal|fast", "prompter-seek &lt;0-100&gt;", "prompter-nudge &lt;pixels&gt;", "prompter-close".
/// </summary>
internal sealed class PrompterModule : IShammyModule
{
    const string Glyph = "\uE8A5";

    ModuleContext _ctx = null!;
    PrompterData _data = new();
    PrompterCard? _card;
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
            else ctx.Notch.ExcludeFromCapture(true);
        };
        ctx.AddCommand(new Command(this));
        ctx.AddTrayItem("Teleprompter", () => Open());

        ctx.AddPipeCommand("prompter-state", _ => State());
        ctx.AddPipeCommand("prompter-open", _ =>
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
        ctx.AddPipeCommand("prompter-key", arg =>
        {
            if (_card is null) return "closed";
            if (arg.Trim() == "space") _card.Toggle();
            return State();
        });
        ctx.AddPipeCommand("prompter-pace", arg =>
        {
            if (!Enum.TryParse<PrompterPace>(arg.Trim(), true, out var pace)) return "error: slow, normal or fast";
            SavePace(pace);
            _card?.SetPace(pace);
            return State();
        });
        ctx.AddPipeCommand("prompter-seek", arg =>
        {
            if (_card is null || !double.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return "closed or not a number";
            _card.Seek(p);
            return State();
        });
        ctx.AddPipeCommand("prompter-nudge", arg =>
        {
            if (_card is null || !double.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)) return "closed or not a number";
            _card.Nudge(px);
            return State();
        });
        ctx.AddPipeCommand("prompter-close", _ =>
        {
            Close();
            return State();
        });
    }

    public void Dispose()
    {
        _keepHidden.Stop();
        if (_card is not null) _ctx.Notch.ExcludeFromCapture(false);
    }

    string State() => _card is { Parent: not null } card
        ? $"open {card.Describe()} hiddenFromCapture=True script={_data.Script.Length}"
        : $"closed pace={_data.Pace} script={_data.Script.Length}";

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
        var card = new PrompterCard(Script, _data.Pace, SavePace, Close);
        _card = card;
        // Not interactive: the keyboard stays with the camera or meeting app.
        _ctx.ShowCard(card, PrompterCard.CardWidth, PrompterCard.CardHeight, interactive: false);
        _ctx.Notch.ExcludeFromCapture(true);
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
        _card = null;
        _ctx.Notch.ExcludeFromCapture(false);
    }

    CommandResult Run(PrompterCommand command)
    {
        string detail;
        switch (command.Kind)
        {
            case PrompterCommandKind.Close:
                bool was = _card?.Parent is not null;
                Close();
                return new CommandResult(Glyph, was ? "Teleprompter closed" : "The teleprompter isn’t open");
            case PrompterCommandKind.FromClipboard:
                string copied;
                try { copied = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch (Exception ex)
                {
                    _ctx.Warn("Reading the clipboard for the teleprompter failed", ex);
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
        // After the confirmation has had its moment, the teleprompter takes the notch.
        _ctx.Ui.BeginInvoke(() => _ctx.Notch.AfterSearch(Open), DispatcherPriority.Background);
        return new CommandResult(Glyph, "Teleprompter", detail);
    }

    sealed class Command(PrompterModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => PrompterText.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) =>
            Task.FromResult(PrompterText.Parse(text) is { } command ? module.Run(command) : new CommandResult(Glyph, "Teleprompter"));

        public CommandPreview? Preview(string text) => PrompterText.Parse(text) switch
        {
            { Kind: PrompterCommandKind.Close } => new(Glyph, "Close the teleprompter"),
            { Kind: PrompterCommandKind.FromClipboard } => new(Glyph, "Teleprompter with what you copied", "Hidden from screen shares and recordings"),
            { Kind: PrompterCommandKind.FromFile } c => new(Glyph, $"Teleprompter with {Path.GetFileName(c.Path)}", "Hidden from screen shares and recordings"),
            { } => new(Glyph, "Open the teleprompter", "Your script, right under the camera"),
            _ => null,
        };
    }
}

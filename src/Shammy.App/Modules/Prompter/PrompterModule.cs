using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Shammy.Core.Captions;
using Shammy.Core.Prompter;
using Shammy.Core.Stage;
using Shammy.Modules.SelectionTools;
using Shammy.Modules.Stage;

namespace Shammy.Modules.Prompter;

/// <summary>
/// What Stage remembers: the last script and pace, the text size, the ring light, voice and mirror choices, and the
/// last few scripts (stored with the rest, encrypted on this PC only).
/// </summary>
internal sealed class PrompterData
{
    public string Script { get; set; } = "";
    public PrompterPace Pace { get; set; } = PrompterPace.Normal;
    public double TextSize { get; set; } = StageText.DefaultSize;
    public bool Ring { get; set; } = true;
    public double RingStrength { get; set; } = StageText.DefaultRing;
    public bool Voice { get; set; } = true;
    public bool Mirror { get; set; }
    public List<RecentScript> Recent { get; set; } = new();
}

/// <summary>
/// Stage, the teleprompter under the camera (see <see cref="StageCard"/>): "stage" or "teleprompter" opens it, the
/// wheel's Stage droplet and the tray too. With no script yet it opens on a calm sheet (see
/// <see cref="StageScriptSheet"/>): Paste, Type, Open file and the recent scripts; with text copied it first asks
/// "Use the text you copied?"; a .txt or .md file or dragged text dropped on the island while Stage is open becomes
/// the script; "Script" (N) on Stage opens the sheet again. "stage from clipboard" and "stage C:\path\script.txt"
/// still work. While it is open the notch is hidden from screen recordings and screen shares, so the people watching
/// never see the script.
/// Pipes: "prompter-state", "prompter-open", "prompter-load &lt;text&gt;" (\n for new lines), "prompter-key space",
/// "prompter-pace slow|normal|fast", "prompter-seek &lt;0-100&gt;", "prompter-nudge &lt;pixels&gt;", "prompter-close",
/// and "stage-state", "stage-open", "stage-close", "stage-voice &lt;level 0-1&gt;|off" (a microphone level instead of
/// the microphone), "stage-ring on|off|toggle|&lt;0-100&gt;", "stage-captions-line" (what the quiet line shows),
/// "stage-key space|left|right|plus|minus|esc|l|c|m|v|n" (as if pressed with the pointer on Stage); for the script:
/// "stage-empty-state", "stage-change", "stage-offer [text]", "stage-use-clipboard", "stage-keep", "stage-recent [n]",
/// "stage-load-file &lt;path&gt;", "stage-drop-text &lt;text&gt;", "stage-drop-file &lt;path&gt;", "stage-type [text]",
/// "stage-type-start", "stage-clear".
/// </summary>
internal sealed class PrompterModule : IShammyModule, Connect.IStageLink, IStageScriptHost
{
    const string Glyph = "\uE8A5";
    const string NothingToRead = "Nothing to read there";

    ModuleContext _ctx = null!;
    PrompterData _data = new();
    // The prompter; kept while the script sheet is over it, so Back returns to the same line.
    StageCard? _card;
    StageScriptSheet? _sheet;
    // What Stage shows on the island now (the prompter or the sheet), and whether it has the keyboard.
    FrameworkElement? _view;
    bool _viewInteractive;
    // The copied text the user said "Keep my script" or "Not now" to (a fingerprint, not the words).
    string? _declinedKey;
    bool _offersOff;
    bool _showInCaptures;
    // Notices while we are open when something else took the notch, so Stage counts as closed.
    readonly DispatcherTimer _watch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };

    public string Name => "prompter";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _data = ctx.Load<PrompterData>();
        _data.Recent = StageScripts.Tidy(_data.Recent);
        // A script saved before there was a recent list is its first entry.
        if (_data.Recent.Count == 0 && _data.Script.Length > 0) _data.Recent = StageScripts.Remember(null, _data.Script, DateTime.Now);
        _watch.Tick += (_, _) =>
        {
            // Something else took the notch (an alarm, another card), or a click elsewhere closed the sheet: we are closed.
            if (!Showing)
            {
                ctx.Info($"Stage closed: the notch moved on ({ctx.Notch.Mode})");
                Closed();
            }
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
        ctx.AddPipeCommand("prompter-load", arg => LoadAndShow(arg.Replace("\\n", "\n")) + " · " + State());
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
            if (_view is not null) ctx.Notch.ExcludeFromCapture(!_showInCaptures);
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
        AddScriptPipes(ctx);
    }

    /// <summary>The test pipes for getting a script onto Stage (each path the sheet, the offer and drops take).</summary>
    void AddScriptPipes(ModuleContext ctx)
    {
        // What the script sheet shows: "sheet=pick|type|offer|none …", the script's length, the recent count and
        // whether the clipboard now would be offered.
        ctx.AddPipeCommand("stage-empty-state", _ => SheetState());
        ctx.AddPipeCommand("stage-offers", arg =>
        {
            _offersOff = arg.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
            return $"offers={(_offersOff ? "off" : "on")}";
        });
        // The "Script" chip (N) on Stage: the choices over the script; opens Stage on them if it was closed.
        ctx.AddPipeCommand("stage-change", _ =>
        {
            ChangeScript();
            return SheetState();
        });
        // Whether this text (or, with nothing given, the clipboard) would be offered as Stage opens; changes nothing.
        ctx.AddPipeCommand("stage-offer", arg =>
        {
            string? copied = arg.Length > 0 ? arg.Replace("\\n", "\n") : ClipboardText.TryGet();
            if (copied is null) return "error: the clipboard is busy";
            return $"offer={(StageScripts.ShouldOffer(copied, _data.Script, _declinedKey) ? "yes" : "no")} looksLikeScript={StageScripts.LooksLikeScript(copied)} " +
                $"same={PrompterText.Clean(copied).Text == _data.Script} declined={_declinedKey is not null && StageScripts.Key(copied) == _declinedKey} copied={copied.Length}";
        });
        // Paste on the sheet (and "Use it" on the offer, which takes the same clipboard text).
        ctx.AddPipeCommand("stage-use-clipboard", _ => UseClipboard() + " · " + State());
        // "Keep my script" / "Not now" on the offer.
        ctx.AddPipeCommand("stage-keep", _ =>
        {
            if (_sheet is not { Mode: StageSheetMode.Offer } || !ReferenceEquals(_view, _sheet)) return "error: no offer showing · " + SheetState();
            ((IStageScriptHost)this).KeepScript();
            return State();
        });
        // "stage-recent" lists the recent scripts; "stage-recent 2" puts the second on Stage.
        ctx.AddPipeCommand("stage-recent", arg =>
        {
            var now = DateTime.Now;
            if (arg.Trim().Length == 0)
                return $"recent={_data.Recent.Count}" + string.Concat(_data.Recent.Select((r, i) =>
                    $" | {i + 1}. \"{r.Title}\" {StageScripts.Meta(r, now)}{(r.Text == _data.Script ? " (on Stage)" : "")}"));
            if (!int.TryParse(arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > _data.Recent.Count)
                return $"error: usage stage-recent [1-{_data.Recent.Count}]";
            return LoadAndShow(_data.Recent[n - 1].Text) + " · " + State();
        });
        // Open file on the sheet, without Windows' dialog.
        ctx.AddPipeCommand("stage-load-file", arg => LoadFile(arg.Trim().Trim('"')) + " · " + State());
        // As if text, or a file, were dragged onto the island (only while Stage is open, like a real drop).
        ctx.AddPipeCommand("stage-drop-text", arg => Drop(new DataObject(DataFormats.UnicodeText, arg.Replace("\\n", "\n"))));
        ctx.AddPipeCommand("stage-drop-file", arg => Drop(new DataObject(DataFormats.FileDrop, new[] { arg.Trim().Trim('"') })));
        // Type on the sheet: "stage-type" opens the box (with the text, if given); "stage-type-start" presses Start.
        ctx.AddPipeCommand("stage-type", arg =>
        {
            ShowSheet(s => s.ShowType(arg.Replace("\\n", "\n")));
            return SheetState();
        });
        ctx.AddPipeCommand("stage-type-start", _ =>
        {
            if (_sheet is not { Mode: StageSheetMode.Type } sheet || !ReferenceEquals(_view, sheet)) return "error: the box isn't showing · " + SheetState();
            if (PrompterText.Words(sheet.TypedText) == 0) return "error: nothing typed · " + SheetState();
            sheet.Start();
            return State();
        });
        // Clears the script on Stage (the recent list stays); Stage then opens on the empty sheet.
        ctx.AddPipeCommand("stage-clear", _ =>
        {
            _data.Script = "";
            _ctx.Save(_data);
            if (_view is StageCard) ShowSheet(s => s.ShowPick());
            return State();
        });
    }

    string Drop(IDataObject data)
    {
        if (_view is not Shammy.Shell.ICardDrop target || !Showing) return "closed: drops only land while Stage is open";
        if (!target.AcceptsDrop(data)) return "not taken (Stage takes one .txt or .md file, or text that isn't only a link)";
        target.TakeDrop(data);
        return State();
    }

    CommandResult Connect.IStageLink.Present(string text)
    {
        var said = Load(text);
        if (said == NothingToRead) return new CommandResult(Glyph, said, IsError: true);
        // After the moment that asked has gone: Stage takes the island.
        _ctx.Ui.BeginInvoke(() => ShowStage(), DispatcherPriority.Background);
        return new CommandResult(Glyph, "On Stage", said);
    }

    public void Dispose()
    {
        if (Connect.Links.Stage == this) Connect.Links.Stage = null;
        _watch.Stop();
        _card?.Shutdown();
        if (_view is not null) _ctx.Notch.ExcludeFromCapture(false);
    }

    /// <summary>Whether what Stage last put on the island is still the card there.</summary>
    bool Showing => _view is not null && ReferenceEquals(_ctx.Notch.OpenCardContent, _view);

    string State() => Showing switch
    {
        true when _view is StageCard card => $"open {card.Describe()} hiddenFromCapture={!_showInCaptures} script={_data.Script.Length}",
        true when _view is StageScriptSheet sheet => $"sheet {sheet.Describe()} hiddenFromCapture={!_showInCaptures} script={_data.Script.Length} recent={_data.Recent.Count}",
        _ => $"closed pace={_data.Pace} size={_data.TextSize:0} ring={_data.Ring} voice={_data.Voice} mirror={_data.Mirror} script={_data.Script.Length} recent={_data.Recent.Count}",
    };

    string SheetState()
    {
        var copied = ClipboardText.TryGet();
        string offer = copied is null ? "busy" : StageScripts.ShouldOffer(copied, _data.Script, _declinedKey) ? "yes" : "no";
        string sheet = Showing && _view is StageScriptSheet s ? s.Describe() : "sheet=none view=" + (Showing ? "stage" : "closed");
        return $"{sheet} script={_data.Script.Length} recent={_data.Recent.Count} offerClipboard={offer}";
    }

    string Key(string arg)
    {
        if (_card is null || !ReferenceEquals(_view, _card)) return "closed";
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
            "n" => StageKey.Script,
            _ => null,
        };
        if (key is not { } k) return "error: space, left, right, plus, minus, esc, l, c, m, v or n";
        _card.OnKey(k);
        return State();
    }

    // ---- The script ------------------------------------------------------------------------------------------

    /// <summary>Takes a new script (and puts it at the top of the recent list); returns what to tell the user.</summary>
    string Load(string raw)
    {
        var (text, cut) = PrompterText.Clean(raw);
        if (text.Length == 0) return NothingToRead;
        _data.Script = text;
        _data.Recent = StageScripts.Remember(_data.Recent, text, DateTime.Now);
        _ctx.Save(_data);
        int words = PrompterText.Words(text);
        return cut ? $"Script loaded, cut to the first {words:N0} words" : $"{words:N0} words · about {PrompterText.Length(words, _data.Pace)}";
    }

    /// <summary>Takes a new script and shows it on Stage from the top; or says what went wrong where the user is looking.</summary>
    string LoadAndShow(string raw)
    {
        var said = Load(raw);
        if (said == NothingToRead)
        {
            Problem(said);
            return "error: " + said;
        }
        ShowStage(opening: said.StartsWith("Script loaded, cut", StringComparison.Ordinal) ? said : null);
        return said;
    }

    /// <summary>Reads a script file; on failure, the title and detail to tell the user.</summary>
    (string? Text, string? Problem, string? Detail) ReadFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return (null, "Couldn’t find that file", file.Name);
            if (file.Length > 2 * 1024 * 1024) return (null, "That file is too big for a script", "Use a text file under 2 MB");
            return (File.ReadAllText(file.FullName), null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _ctx.Warn("Reading the script file failed", ex);
            return (null, "Couldn’t open that file", "Check the path and try again");
        }
    }

    string LoadFile(string path)
    {
        if (!PrompterText.IsScriptFile(path))
        {
            Problem("Stage reads .txt and .md files");
            return "error: Stage reads .txt and .md files";
        }
        var (text, problem, _) = ReadFile(path);
        if (text is null)
        {
            Problem(problem!);
            return "error: " + problem;
        }
        return LoadAndShow(text);
    }

    string UseClipboard()
    {
        var copied = ClipboardText.TryGet();
        if (copied is null)
        {
            Problem("The clipboard is busy · try again in a moment");
            return "error: the clipboard is busy";
        }
        if (string.IsNullOrWhiteSpace(copied))
        {
            Problem("Nothing copied yet · copy your script, then Paste");
            return "error: nothing copied";
        }
        return LoadAndShow(copied);
    }

    /// <summary>Something didn't work: said on Stage if it is showing the script, else on the sheet's choices.</summary>
    void Problem(string text)
    {
        if (Showing && _view is StageCard card) card.Say(text);
        else ShowSheet(s => s.ShowPick(text));
    }

    void SavePace(PrompterPace pace)
    {
        if (_data.Pace == pace) return;
        _data.Pace = pace;
        _ctx.Save(_data);
    }

    // ---- What shows -------------------------------------------------------------------------------------------

    /// <summary>
    /// Stage as the user asked for it (the command, the tray, the wheel): first the question about copied text when
    /// there is some worth asking about, else the empty sheet when there is no script yet, else the script.
    /// </summary>
    void Open()
    {
        // Test runs can turn the question off ("stage-offers off"), so they never depend on the real clipboard.
        var copied = _offersOff ? null : ClipboardText.TryGet();
        if (StageScripts.ShouldOffer(copied, _data.Script, _declinedKey)) ShowSheet(s => s.ShowOffer(copied!));
        else if (_data.Script.Length == 0) ShowSheet(s => s.ShowPick());
        else ShowStage();
    }

    /// <summary>The script on Stage, from the top, in a fresh Stage in the same place.</summary>
    void ShowStage(string? opening = null)
    {
        if (_data.Script.Length == 0)
        {
            ShowSheet(s => s.ShowPick());
            return;
        }
        if (_card is { } old)
        {
            _card = null;
            old.Shutdown();
        }
        StageCard? card = null;
        card = new StageCard(_data.Script, _data, new StageHost(
            _ctx.Ui,
            Save: () => _ctx.Save(_data),
            Close: Close,
            Resize: (w, h) =>
            {
                if (ReferenceEquals(_view, card) && Showing) _ctx.ShowCard(card!, w, h, interactive: false);
            },
            SetCaptions: SetCaptionsAsync,
            Monitor: () => _ctx.Notch.Monitor,
            Notch: () => new System.Windows.Interop.WindowInteropHelper(_ctx.Notch).Handle,
            ChangeScript: ChangeScript,
            Dropped: Dropped)) { Opening = opening };
        _card = card;
        // Not interactive: the keyboard stays with the camera or meeting app (Stage's keys work while the pointer is on it).
        Present(card, card.CardWidth, card.CardHeight, interactive: false);
        _ctx.Info($"Stage opened (notch {_ctx.Notch.Mode})");
    }

    /// <summary>Back from the sheet to the script, on the line it was on.</summary>
    void ReturnToStage()
    {
        if (_card is not { } card)
        {
            ShowStage();
            return;
        }
        Present(card, card.CardWidth, card.CardHeight, interactive: false);
        // Its microphone, ring light and keys start again once it is back on the island.
        _ctx.Ui.BeginInvoke(() =>
        {
            if (ReferenceEquals(_view, card)) card.Resume();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The script sheet on the island (taking the keyboard, for typing and Tab), filled by <paramref name="fill"/>.</summary>
    void ShowSheet(Action<StageScriptSheet> fill)
    {
        // A prompter under the sheet rests (no microphone, light or keys) until Back brings it back.
        _card?.Shutdown();
        if (_sheet is null)
        {
            var sheet = new StageScriptSheet(this);
            sheet.FitChanged += () =>
            {
                if (ReferenceEquals(_view, sheet) && Showing) _ctx.ShowCard(sheet, sheet.Width, sheet.FitHeight, _viewInteractive);
            };
            _sheet = sheet;
        }
        fill(_sheet);
        Present(_sheet, StageScriptSheet.SheetWidth, _sheet.Fit(), interactive: true);
    }

    void ChangeScript() => ShowSheet(s => s.ShowPick());

    void Present(FrameworkElement view, double width, double height, bool interactive)
    {
        bool giveBack = _viewInteractive && !interactive;
        _view = view;
        _viewInteractive = interactive;
        _ctx.ShowCard(view, width, height, interactive);
        // From the sheet (which had the keyboard) to the prompter (which never takes it).
        if (giveBack) _ctx.Notch.SetInteractive(false);
        _ctx.Notch.ExcludeFromCapture(!_showInCaptures);
        _watch.Start();
    }

    void Close()
    {
        if (Showing) _ctx.CloseCard();
        Closed();
    }

    void Closed()
    {
        if (_view is null && _card is null && !_watch.IsEnabled) return;
        _watch.Stop();
        _card?.Shutdown();
        _card = null;
        _sheet = null;
        _view = null;
        _viewInteractive = false;
        // A recording keeps the notch out of its own video.
        _ctx.Notch.ExcludeFromCapture(Record.RecordModule.Current is { } r && (r.IsRecording || r.IsSaving));
    }

    void Dropped(IDataObject data)
    {
        if (StageDrop.File(data) is { } path) LoadFile(path);
        else if (StageDrop.Text(data) is { } text) LoadAndShow(text);
    }

    // ---- The sheet's buttons ------------------------------------------------------------------------------------

    bool IStageScriptHost.HasScript => _data.Script.Length > 0;

    IReadOnlyList<RecentScript> IStageScriptHost.Recent => _data.Recent.Where(r => r.Text != _data.Script).ToList();

    void IStageScriptHost.Paste() => UseClipboard();

    void IStageScriptHost.StartTyped(string text) => LoadAndShow(text);

    void IStageScriptHost.PickRecent(RecentScript script) => LoadAndShow(script.Text);

    void IStageScriptHost.UseCopied(string text) => LoadAndShow(text);

    void IStageScriptHost.Dropped(IDataObject data) => Dropped(data);

    void IStageScriptHost.KeepScript()
    {
        // Not asked again about this copy; a new copy is asked about as usual.
        if (_sheet is { Mode: StageSheetMode.Offer } sheet && sheet.Copied.Length > 0) _declinedKey = StageScripts.Key(sheet.Copied);
        if (_data.Script.Length > 0) ReturnToStage();
        else ShowSheet(s => s.ShowPick());
    }

    void IStageScriptHost.Leave()
    {
        if (_data.Script.Length > 0) ReturnToStage();
        else Close();
    }

    /// <summary>Open file: Windows' own dialog, for one .txt or .md file.</summary>
    void IStageScriptHost.OpenFile()
    {
        if (_sheet is not { } sheet || !ReferenceEquals(_view, sheet)) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a script",
            Filter = "Scripts (*.txt, *.md)|*.txt;*.md|Text files (*.txt)|*.txt|Markdown (*.md)|*.md",
            CheckFileExists = true,
            Multiselect = false,
        };
        // While Windows' dialog has the focus, the sheet mustn't close the way a card does when you click elsewhere.
        Present(sheet, sheet.Width, sheet.FitHeight, interactive: false);
        bool? picked = null;
        try
        {
            picked = dialog.ShowDialog(_ctx.Notch);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            _ctx.Warn("Stage: the open file dialog failed", ex);
        }
        // Something else took the island meanwhile: leave it be.
        if (!ReferenceEquals(_view, sheet) || !Showing) return;
        if (picked == true && dialog.FileName.Length > 0) LoadFile(dialog.FileName);
        else Present(sheet, sheet.Width, sheet.FitHeight, interactive: true);
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
                bool was = Showing;
                Close();
                return new CommandResult(Glyph, was ? "Stage closed" : "Stage isn’t open");
            case PrompterCommandKind.FromClipboard:
                var copied = ClipboardText.TryGet();
                if (copied is null) return new CommandResult(Glyph, "Couldn’t read the clipboard", "Copy your script again, then retry", IsError: true);
                if (string.IsNullOrWhiteSpace(copied)) return new CommandResult(Glyph, "No text copied", "Copy your script first", IsError: true);
                detail = Load(copied);
                break;
            case PrompterCommandKind.FromFile:
                var (text, problem, why) = ReadFile(command.Path!);
                if (text is null) return new CommandResult(Glyph, problem!, why, IsError: true);
                detail = Load(text);
                break;
            default:
                detail = _data.Script.Length > 0 ? "Your last script" : "Add your script";
                // After the confirmation has had its moment, Stage takes the notch (asking about copied text first).
                _ctx.Ui.BeginInvoke(() => _ctx.Notch.AfterSearch(Open), DispatcherPriority.Background);
                return new CommandResult(Glyph, "Stage", detail);
        }
        if (detail == NothingToRead) return new CommandResult(Glyph, detail, IsError: true);
        // A script was just given: straight onto Stage, no question about the clipboard.
        _ctx.Ui.BeginInvoke(() => _ctx.Notch.AfterSearch(() => ShowStage()), DispatcherPriority.Background);
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

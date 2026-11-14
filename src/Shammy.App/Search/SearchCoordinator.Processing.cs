using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Shell;

namespace Shammy.Search;

internal partial class SearchCoordinator
{
    // ---- Processing ----------------------------------------------------------------------------

    /// <summary>Test hook: acts on <paramref name="heard"/> exactly as on words a held talk key brought back.</summary>
    internal void HearForTest(string heard)
    {
        Reset(keepNotch: true);
        _session++;
        _ = ProcessAsync(null, null, heard);
    }

    /// <summary>Test hook: the listening view with live words, as a hold shows it (for pictures).</summary>
    internal void ShowLiveForTest(string words)
    {
        Reset(keepNotch: true);
        _session++;
        _phase = Phase.Holding;
        _key = HotKey.Talk;
        _keys.InterceptEscape = true;
        _notch.SetSearchMode(NotchMode.Listening);
        _notch.SearchBarView.ShowListening(() => 0.35f, "Listening… · " + Modules.OnlineVoice.VoiceRoute.Tag(_settings, false), "Speak, then let go", cat: _settings.CatTheme);
        if (words.Length > 0) _notch.SearchBarView.ShowLive(words, PlanFor(words));
    }

    async Task ProcessAsync(float[]? speechSamples, IReadOnlyList<Pt>? stroke, string? heard = null)
    {
        int session = _session;
        _phase = Phase.Processing;
        _notch.SetSearchMode(NotchMode.Thinking);

        string spoken = heard ?? "";
        if (heard is null && speechSamples is not null)
        {
            // Online listening first, when the user turned it on; null or nothing heard means the local model.
            string? heardOnline = null;
            if (_listenOnline && Modules.HostLinks.OnlineTranscribe is { } transcribeOnline)
            {
                _notch.SearchBarView.ShowThinking("Transcribing…");
                _transcribe = new CancellationTokenSource();
                try { heardOnline = await transcribeOnline(speechSamples, _transcribe.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Warn("Online transcription failed", ex); }
                if (session != _session) return;
            }
            if (!string.IsNullOrWhiteSpace(heardOnline))
            {
                spoken = heardOnline;
                Log.Info($"Heard online: {spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words");
            }
            else if (_whisper.IsReady)
            {
                // Online listening isn't available (no key, no internet) or gave nothing: the offline backup,
                // read into memory for this request and freed again a minute later.
                _notch.SearchBarView.ShowThinking(_whisper.IsLoaded ? "Transcribing…" : "Transcribing on this PC…");
                // Esc or a new search cancels this, so an abandoned transcription doesn't hold the model.
                _transcribe = new CancellationTokenSource();
                try { spoken = await _whisper.TranscribeAsync(speechSamples, _transcribe.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Error("Transcription failed", ex); }
                if (session != _session) return;
                // Never log what was said; the log isn't a transcript archive.
                Log.Info($"Heard on this PC: {spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words");
                if (spoken.Length == 0 && _whisper.Failure is { } failure && stroke is null)
                {
                    await ShowMessageThenCancel(session, "", "Voice isn’t available", failure + " · tap Right Alt to type");
                    return;
                }
            }
            else if (stroke is null)
            {
                // The offline backup isn't on this PC yet: fetch it for next time (if there is internet at all).
                bool network = Modules.OnlineVoice.GnaniClient.NetworkUp();
                if (network && !_whisper.IsDownloading) _ = _whisper.DownloadAsync();
                bool getting = _whisper.IsDownloading || network;
                await ShowMessageThenCancel(session, "",
                    getting ? "Getting the offline voice" : "Voice needs the internet",
                    getting ? "148 MB, once · or add a Gnani key in Settings › Voice · tap Right Alt to type"
                            : "Or add a Gnani key in Settings › Voice · tap Right Alt to type");
                return;
            }
        }

        var (query, filters) = QueryBuilder.ExtractSpokenFilters(spoken);

        Selection? selection = null;
        if (stroke is { Count: > 0 } && _shot is not null)
        {
            _notch.SearchBarView.ShowThinking(query.Length > 0 ? $"“{query}”" : "Reading the selection…");
            var words = _ocr is null ? Array.Empty<OcrWord>() : await _ocr;
            if (session != _session) return;
            selection = SelectionAnalyzer.Analyze(words, stroke);
            selection = selection with { Region = selection.Region.Clamp(_shot.Width, _shot.Height) };
            Log.Info($"Selection: {stroke.Count} pts, {words.Count} OCR words on screen -> {selection.Kind}, {selection.Text.Length} chars");
            // Only with the test pipe on (never in normal use): what was read, for the live checks.
            if (TestPipe) LastReadForTest = selection.Text;
            if (selection.Region.W < 4 || selection.Region.H < 4) selection = null;
            else _overlay?.ShowSelection(selection.Region);
        }

        // Modules get first look at a circle (a QR code opens its link, an amount converts...).
        Modules.SelectionInfo? info = null;
        if (selection is not null)
        {
            info = new Modules.SelectionInfo(selection.Text, _shot!.EncodePng(selection.Region), _shot.Crop(selection.Region),
                selection.Kind == SelectionKind.Image);
            foreach (var handler in _modules.SelectionHandlers)
            {
                Modules.CommandResult? taken = null;
                try { taken = await handler.TryTakeOverAsync(info); }
                catch (Exception ex) { Log.Warn($"Selection handler {handler.GetType().Name} failed", ex); }
                if (session != _session) return;
                if (taken is null) continue;
                ShowCommandResult(taken);
                return;
            }
        }

        // Understood on this PC (a number to call, a date, a sum, a colour…) or a short fact: the answer flows into the
        // island as a moment instead of a full results card. Only for a silent circle: a spoken question is a search.
        if (selection is not null && info is not null && query.Length == 0)
        {
            if (await TryMomentAsync(session, selection, info, ContextFor(selection, query, filters, info))) return;
            if (session != _session) return;
        }

        // Spoken commands ("play …", "timer 10 minutes") run instead of a web search.
        if (selection is null && spoken.Length > 0 && await TryRunCommandAsync(spoken)) return;

        if (selection is null && query.Length == 0)
        {
            await ShowMessageThenCancel(session, "", "Didn't catch that", "Try again, or tap Right Alt to type");
            return;
        }

        // Not a command, but something in Shammy could be meant: show the choices (the palette, filled in with
        // what was heard) instead of searching. A plain question still goes straight to the web.
        if (selection is null && spoken.Length > 0 && ChoicesFor(spoken) is { } choices)
        {
            ShowChoices(spoken, choices.List);
            return;
        }

        if (selection is null && Calculator.TryEvaluate(query, out var value, out _))
        {
            ShowAnswer(query, Calculator.DisplayForm(query), Calculator.Format(value));
            return;
        }

        var full = ContextFor(selection, query, filters, info);
        // A spoken question with nothing circled: a short answer in the island, read aloud.
        if (selection is null && !WantsResults(query, filters, out _)) await AskAsync(session, query, full, spoken: true);
        else await ShowResultAsync(full);
    }

    /// <summary>The full results for a circle (Lens for a picture, the default mode for words) or for words alone.</summary>
    Context ContextFor(Selection? selection, string query, SearchFilter filters, Modules.SelectionInfo? info)
    {
        Context ctx;
        if (selection is { Kind: SelectionKind.Image } && _shot is not null)
        {
            var png = info?.Png ?? _shot.EncodePng(selection.Region);
            ctx = new Context(query, query.Length > 0 ? query : "Selected area", SearchMode.Lens, filters, png, _shot.Crop(selection.Region));
        }
        else if (selection is not null)
        {
            var combined = QueryBuilder.Combine(query, selection.Text);
            ctx = new Context(combined, combined, _settings.DefaultSearchMode, filters, null, _shot?.Crop(selection.Region));
        }
        else
        {
            ctx = new Context(query, query, _settings.DefaultSearchMode, filters, null, null);
        }

        // An image selection can also be searched as text if OCR found words in it.
        if (ctx.Mode == SearchMode.Lens && selection is not null && selection.Text.Length > 0)
            ctx = ctx with { Query = QueryBuilder.Combine(query, selection.Text) };
        return ctx with { Selection = info };
    }

    // ---- Circle to search's moment --------------------------------------------------------------------------

    Context? _momentFull;
    CancellationTokenSource? _factLookup;

    /// <summary>
    /// Shows what was circled as a moment in the island when Shammy understands it on this PC, or finds a short fact for
    /// a circled name or term. True when it did (the moment is up or on its way); false means search as usual.
    /// </summary>
    async Task<bool> TryMomentAsync(int session, Selection selection, Modules.SelectionInfo info, Context full)
    {
        CircleMeaning? meaning = null;
        try { meaning = Understand(selection, info); }
        catch (Exception ex) { Log.Warn("Reading the circle failed", ex); }

        Views.IslandMoment? moment = null;
        if (meaning is not null)
        {
            Log.Info($"Circle understood on this PC: {meaning.Kind}");
            moment = CircleMoments.For(meaning, info, Cancel);
        }
        else if (selection.Kind == SelectionKind.Text && QuickFacts.Worth(selection.Text))
        {
            _notch.SearchBarView.ShowThinking("Looking it up…");
            _factLookup?.Cancel();
            _factLookup = new CancellationTokenSource();
            var fact = await QuickFacts.TryAsync(selection.Text, _factLookup.Token);
            if (session != _session) return true;
            if (fact is not null)
            {
                Log.Info("Circle answered with a short fact");
                moment = CircleMoments.For(fact, info, Cancel);
            }
        }
        if (moment is null) return false;
        ShowMoment(session, moment, full);
        return true;
    }

    /// <summary>Words: what they are. A picture: one colour filling it is that colour; otherwise any words in it.</summary>
    static CircleMeaning? Understand(Selection selection, Modules.SelectionInfo info)
    {
        if (selection.Kind == SelectionKind.Text) return CircleSense.Read(selection.Text, DateTime.Now);
        if (Modules.SelectionTools.QrReader.ReadPixels(info) is { } px && CircleSense.Swatch(px.Bgra, px.Width, px.Height) is { } swatch)
            return swatch;
        return selection.Text.Length > 0 ? CircleSense.Read(selection.Text, DateTime.Now) : null;
    }

    void ShowMoment(int session, Views.IslandMoment moment, Context full)
    {
        _phase = Phase.Showing;
        _momentFull = full;
        _momentLoaded = false;
        _keys.InterceptEscape = true;
        void Land()
        {
            if (session != _session) return;
            _notch.AnswerView.ShowMoment(moment);
            _notch.SetSearchMode(NotchMode.Answer);
            _notch.SetInteractive(true);
        }
        // The answer leaves the selection as a drop of light and lands in the island (at once with Calm motion).
        var overlay = _overlay;
        _overlay = null;
        if (overlay is null) Land();
        else overlay.FlowToIsland(Land);
    }

    /// <summary>The moment's top was clicked: the full results for the same circle.</summary>
    void ExpandMoment()
    {
        if (_phase != Phase.Showing || _momentFull is not { } full) return;
        _momentFull = null;
        _ = ShowResultAsync(full, navigate: !_momentLoaded);
    }

    internal void ExpandMomentForTest() => ExpandMoment();

    static readonly bool TestPipe = Environment.GetEnvironmentVariable("SHAMMY_TEST_PIPE") == "1";

    /// <summary>The last circle's words, kept only while the test pipe is on.</summary>
    internal static string LastReadForTest { get; private set; } = "";

    /// <summary>Test hook: a silent circle around <paramref name="text"/> (no screen, no picture), up to the moment or the search.</summary>
    internal async Task<string> CircleTextForTest(string text)
    {
        Reset(keepNotch: true);
        _session++;
        int session = _session;
        _phase = Phase.Processing;
        _notch.SetSearchMode(NotchMode.Thinking);
        var selection = new Selection(SelectionKind.Text, text, new Box(0, 0, 10, 10));
        var info = new Modules.SelectionInfo(text, null, null, false);
        var full = new Context(text, text, _settings.DefaultSearchMode, SearchFilter.None, null, null) { Selection = info };
        if (await TryMomentAsync(session, selection, info, full)) return "moment";
        if (session == _session) await ShowResultAsync(full);
        return "search";
    }

    /// <summary>Runs the first module command that recognises the text. Returns false if none did.</summary>
    async Task<bool> TryRunCommandAsync(string text)
    {
        int session = _session;
        // Several requests in one sentence ("screenshot le lo aur phone pe bhej") run one after another.
        if (ResolveChain(text) is { } chain)
        {
            await RunChainAsync(session, chain);
            return true;
        }
        var (command, clean) = Resolve(text);
        if (command is null) return false;

        _phase = Phase.Processing;
        // A command never uses the frozen screen; let it go now so a command that looks at the screen (a
        // screenshot) sees the live one rather than the dimmed freeze.
        DismissOverlay();
        _notch.SetSearchMode(NotchMode.Thinking);
        _notch.SearchBarView.ShowThinking($"“{clean}”");
        Modules.CommandResult result;
        try
        {
            result = await command.RunAsync(clean);
            Modules.HostLinks.RaiseCommandRan(clean);
        }
        catch (Exception ex)
        {
            Log.Error($"Command {command.GetType().Name} failed", ex);
            result = new Modules.CommandResult("", "That didn't work", ex.Message, IsError: true);
        }
        if (session != _session) return true;
        // The command found out it can't answer after all (an unknown place): search as if it never claimed the text.
        if (result.SearchInstead is { Length: > 0 } search) await SearchWebAsync(search);
        // A translation opens in the results card, the way the Translate chip shows it.
        else if (result.OpenInResults is { } page)
            await ShowResultAsync(new Context(page.Query, page.Query, _settings.DefaultSearchMode, SearchFilter.None, null, null) { Page = page.Url });
        else ShowCommandResult(result);
        return true;
    }

    /// <summary>
    /// The command that answers <paramref name="text"/> and the wording it answers: the words as said first, then
    /// each rewrite of them (Hinglish, other wordings, a doubled sentence, "hey Shammy, please …"), and for each the
    /// Hinglish timer/alarm/reminder form. (null, text) when nothing does.
    /// </summary>
    internal (Modules.ITextCommand? Command, string Clean) Resolve(string text)
    {
        foreach (var candidate in SpokenRewrite.Candidates(text))
        {
            if (Find(candidate) is { } command) return (command, candidate);
            if (HinglishCommands.ToEnglish(candidate) is { } english && Find(english) is { } translated) return (translated, english);
        }
        return (null, text.Trim().TrimEnd('.', '!', '?', '।').Trim());
    }

    /// <summary>"voice-route &lt;text&gt;": what a spoken or typed line would do, without doing it.</summary>
    /// <summary>
    /// The steps of a chained request, when the text splits at "and"/"aur"/"then" into pieces that are each a
    /// command; null otherwise (one request, or a piece no command takes).
    /// </summary>
    internal IReadOnlyList<(Modules.ITextCommand Command, string Clean)>? ResolveChain(string text)
    {
        var parts = SpokenChain.Split(text);
        if (parts.Count == 0) return null;
        var steps = new List<(Modules.ITextCommand, string)>();
        foreach (var part in parts)
        {
            var (command, clean) = Resolve(SpokenChain.Carry(part));
            if (command is null) return null;
            steps.Add((command, clean));
        }
        return steps;
    }

    internal static string StepTitle(Modules.ITextCommand command, string clean) => StepName(command, clean);

    static string StepName(Modules.ITextCommand command, string clean)
    {
        try { return (command as Modules.ICommandPreview)?.Preview(clean)?.Title ?? clean; }
        catch (Exception) { return clean; }
    }

    async Task RunChainAsync(int session, IReadOnlyList<(Modules.ITextCommand Command, string Clean)> steps)
    {
        _phase = Phase.Processing;
        DismissOverlay();
        _notch.SetSearchMode(NotchMode.Thinking);
        _notch.SearchBarView.ShowThinking(string.Join(" → ", steps.Select(s => StepName(s.Command, s.Clean))));
        Log.Info($"Running a chain of {steps.Count} commands");
        var done = new List<Modules.CommandResult>();
        foreach (var (command, clean) in steps)
        {
            Modules.CommandResult result;
            try
            {
                result = await command.RunAsync(clean);
                Modules.HostLinks.RaiseCommandRan(clean);
            }
            catch (Exception ex)
            {
                Log.Error($"Chained command {command.GetType().Name} failed", ex);
                result = new Modules.CommandResult("", "That didn't work", ex.Message, IsError: true);
            }
            if (session != _session) return;
            done.Add(result);
            // A step that failed, or wants a search instead, ends the chain there.
            if (result.IsError || result.SearchInstead is not null) break;
            // A moment between steps, so each can finish what it started (a screenshot, a card).
            await Task.Delay(350);
            if (session != _session) return;
        }
        var last = done[^1];
        ShowCommandResult(new Modules.CommandResult(last.Glyph, string.Join(" → ", done.Select(r => r.Title)),
            done.Count < steps.Count ? $"Stopped at step {done.Count} of {steps.Count}" : last.Detail, IsError: last.IsError));
    }

    /// <summary>"PrompterModule.Command": a nested command class named with its module, so two "Command"s never look alike.</summary>
    static string Who(Modules.ITextCommand c) => c.GetType().DeclaringType is { } outer ? outer.Name + "." + c.GetType().Name : c.GetType().Name;

    internal string RouteForTest(string text)
    {
        if (ResolveChain(text) is { } chain) return "CHAIN [" + string.Join(" | ", chain.Select(s => $"{Who(s.Command)} <- '{s.Clean}'")) + "]";
        var (command, clean) = Resolve(text);
        if (command is not null)
        {
            var preview = (command as Modules.ICommandPreview)?.Preview(clean);
            return $"CMD {Who(command)} <- '{clean}'" + (preview is null ? "" : $" ({preview.Title})");
        }
        if (Calculator.TryEvaluate(text, out _, out _)) return "CALC";
        return ChoicesFor(text) is { } choices ? $"CHOICES '{choices.Text}' [{string.Join(" | ", choices.List.Select(c => c.Title))}]" : "GOOGLE";
    }

    Modules.ITextCommand? Find(string text) => _modules.Commands.FirstOrDefault(c =>
    {
        try { return c.CanHandle(text); }
        catch (Exception ex)
        {
            Log.Warn($"Command {c.GetType().Name} threw in CanHandle", ex);
            return false;
        }
    });

    /// <summary>A short confirmation card that closes by itself.</summary>
    void ShowCommandResult(Modules.CommandResult result)
    {
        DismissOverlay();
        _phase = Phase.Showing;
        _notch.AnswerView.ShowCommand(result);
        _notch.SetInteractive(false);
        _notch.SetSearchMode(NotchMode.Answer);
        Log.Info($"Command result ({(result.IsError ? "error" : "ok")})");
        int session = _session;
        // An answer with a Copy button stays long enough to reach it, and any answer stays while the pointer is on it.
        _ = CloseResultLaterAsync(session, result.IsError ? 4000 : result.CopyText is not null ? 5000 : 2600);
    }

    /// <summary>Closes a command's answer after its time, but not while you're reading it: the pointer on the notch holds it.</summary>
    async Task CloseResultLaterAsync(int session, int ms)
    {
        await Task.Delay(ms);
        bool held = false;
        while (session == _session && _notch.PointerOverNotch())
        {
            held = true;
            await Task.Delay(250);
        }
        if (held) await Task.Delay(1200);
        if (session == _session) Cancel();
    }

    async Task ProcessTextAsync(string text)
    {
        if (await TryRunCommandAsync(text)) return;
        if (Calculator.TryEvaluate(text, out var value, out _))
        {
            ShowAnswer(text, Calculator.DisplayForm(text), Calculator.Format(value));
            return;
        }
        var (query, filters) = QueryBuilder.ExtractSpokenFilters(text);
        if (query.Length == 0) query = text;
        var ctx = new Context(query, query, _settings.DefaultSearchMode, filters, null, null);
        // A question gets a short answer in the island; "search …" or a filter ("on reddit") wants the results page.
        if (WantsResults(text, filters, out var search)) await ShowResultAsync(ctx with { Query = search, DisplayQuery = search });
        else await AskAsync(_session, query, ctx, spoken: false);
    }

    Task SearchWebAsync(string text)
    {
        var (query, filters) = QueryBuilder.ExtractSpokenFilters(text);
        if (query.Length == 0) query = text;
        return ShowResultAsync(new Context(query, query, _settings.DefaultSearchMode, filters, null, null));
    }

    void ShowAnswer(string query, string expression, string result)
    {
        DismissOverlay();
        _phase = Phase.Showing;
        _notch.AnswerView.Show(query, expression, result);
        _notch.SetSearchMode(NotchMode.Answer);
        _notch.SetInteractive(true);
    }

    async Task ShowResultAsync(Context ctx, bool navigate = true)
    {
        int session = _session;
        _answerRead?.Cancel();
        _ctx = ctx;
        _phase = Phase.Showing;
        DismissOverlay();
        _keys.InterceptEscape = true;
        _notch.ResultView.SetContext(ctx.DisplayQuery, ctx.Thumb, ctx.Png is not null, ctx.Mode, ctx.Filters);
        var selection = ctx.Selection;
        _notch.ResultView.SetExtraChips(_modules.ResultChips
            .Where(c =>
            {
                try { return c.IsVisible(selection); }
                catch (Exception) { return false; }
            })
            .Select(c => (c.Label, (Action)(() => c.OnClick(selection, _notch.ResultView))))
            .ToList());
        _notch.SetSearchMode(NotchMode.Result);
        _notch.SetInteractive(true);
        Log.Info($"Search [{ctx.Mode}] {ctx.Query.Length} chars");
        if (!navigate) return;
        try
        {
            await NavigateAsync(ctx);
        }
        catch (Exception ex)
        {
            if (session == _session) Log.Error("Showing results failed", ex);
        }
    }

    Task NavigateAsync(Context ctx)
    {
        if (ctx.Page is { } page) return _notch.ResultView.NavigateAsync(page);
        if (ctx.Mode == SearchMode.Lens && ctx.Png is not null) return _notch.ResultView.NavigateLensAsync(ctx.Png);
        var mode = ctx.Mode == SearchMode.Lens ? SearchMode.Ai : ctx.Mode;
        var q = ctx.Query.Length > 0 ? ctx.Query : ctx.DisplayQuery;
        var url = QueryBuilder.BuildUrl(QueryBuilder.Apply(q, ctx.Filters, mode, DateTime.Today), mode);
        return _notch.ResultView.NavigateAsync(url);
    }

    /// <summary>The wording (as said, or a rewrite of it) under which Shammy has something to offer; null for a plain question.</summary>
    static (string Text, IReadOnlyList<Modules.Choice> List)? ChoicesFor(string heard)
    {
        if (Modules.HostLinks.Choices is not { } choices) return null;
        foreach (var candidate in SpokenRewrite.Candidates(heard))
        {
            var list = choices(candidate);
            if (list.Count > 0) return (candidate, list);
        }
        return null;
    }

    IReadOnlyList<Modules.Choice>? _choices;
    string _choiceHeard = "";

    /// <summary>"Did you mean": up to three Shammy actions and Search Google, picked by click, 1–4 or Enter; Esc closes. It waits.</summary>
    void ShowChoices(string heard, IReadOnlyList<Modules.Choice> list)
    {
        DismissOverlay();
        Log.Info($"Offering {list.Count} choices for {heard.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} spoken words");
        _phase = Phase.Choices;
        _choices = list;
        _choiceHeard = heard.Trim().TrimEnd('.', '!', '?', '।').Trim();
        _keys.InterceptEscape = true;
        _keys.InterceptChoices = true;
        var chips = list.Select(c => (c.Title, c.Glyph)).Append(("Search Google", "")).ToList();
        _notch.SearchBarView.ShowChoices(_choiceHeard, chips, _settings.CatTheme, PickChoice);
        _notch.SetSearchMode(NotchMode.Typing);
        _notch.SetInteractive(true);
    }

    void PickChoice(int index)
    {
        if (_phase != Phase.Choices || _choices is not { } list) return;
        _keys.InterceptChoices = false;
        _notch.SearchBarView.HideChoices();
        _choices = null;
        if (index >= list.Count)
        {
            _ = SearchWebAsync(_choiceHeard);
            return;
        }
        // The pick runs as a palette row would: its confirmation, a card, or a search all arrive while typing.
        _phase = Phase.Typing;
        list[index].Run();
    }

    void ShowTyping()
    {
        _phase = Phase.Typing;
        _keys.InterceptEscape = true;
        _notch.SearchBarView.ShowTyping();
        _notch.SetSearchMode(NotchMode.Typing);
        _notch.SetInteractive(true);
        _notch.Dispatcher.BeginInvoke(_notch.SearchBarView.FocusInput, DispatcherPriority.Input);
    }

    async Task ShowMessageThenCancel(int session, string glyph, string primary, string secondary)
    {
        _notch.SetSearchMode(NotchMode.Thinking);
        _notch.SearchBarView.ShowMessage(glyph, primary, secondary);
        DismissOverlay();
        // Not Processing: the hint often says "tap Right Alt to type", and that tap must work right away.
        _phase = Phase.Message;
        await Task.Delay(2200);
        if (session == _session) Cancel();
    }
}

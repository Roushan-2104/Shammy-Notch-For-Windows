using Shammy.Core.Search;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>
/// What a circle does (the "say or type it" design): words said while circling, or typed into the island's box after,
/// pick the action (translate, search, explain, copy, track, my time…). A silent circle answers straight away when it
/// is plainly one thing (a number, a time with a zone, a flight, a UPI ID…); anything else shows what it is and the
/// choices, with its best guess as the accent and Search always among them. A silent picture gets Lens's short answer.
/// </summary>
internal partial class SearchCoordinator
{
    /// <summary>The circle the island is showing: what its "say or type" box acts on.</summary>
    sealed record CircleNow(Selection Selection, Modules.SelectionInfo Info, CircleMeaning? Meaning, Context Full);

    CircleNow? _circle;

    const string TextGlyph = "", CodeGlyph = "", TimeGlyph = "", TrackGlyph = "", PayGlyph = "",
        PlaceGlyph = "", CarGlyph = "", WordGlyph = "";

    /// <summary>
    /// A circle, with whatever was said while drawing it (<paramref name="said"/>, empty when silent). Always shows
    /// something: an answer, the choices, or (a spoken "search") the results.
    /// </summary>
    async Task CircleAsync(int session, Selection selection, Modules.SelectionInfo info, Context full, string said)
    {
        CircleMeaning? meaning = null;
        try { meaning = Understand(selection, info); }
        catch (Exception ex) { Log.Warn("Reading the circle failed", ex); }
        var now = new CircleNow(selection, info, meaning, full);
        _circle = now;
        if (meaning is not null) Log.Info($"Circle understood on this PC: {meaning.Kind}");

        if (CircleIntent.Parse(said) is { } intent)
        {
            Log.Info($"Circle with words: {intent.Action}");
            await DoAsync(session, now, intent, spoken: true);
            return;
        }
        // A picture with nothing in it Shammy can read: Lens's short answer.
        if (meaning is null && selection.Kind == SelectionKind.Image && info.Png is { } png)
        {
            await PictureAsync(session, png, selection.Text, full);
            return;
        }
        var moment = await MomentForAsync(session, now);
        if (moment is null || session != _session) return;
        ShowMoment(session, moment, full);
    }

    /// <summary>The island's card for a silent circle: the answer for a plain thing, else what it is and the choices.</summary>
    async Task<Views.IslandMoment?> MomentForAsync(int session, CircleNow now)
    {
        var m = now.Meaning;
        var text = now.Selection.Text;
        var sends = CircleMoments.Sends(now.Info, Cancel);
        Views.IslandMoment Ask(Views.IslandMoment moment) => moment with { Ask = words => OnAsk(now, words) };

        if (m is not null && CircleMoments.Shows(m.Kind)) return Ask(CircleMoments.For(m, now.Info, Cancel));
        if (m is not null && PlainMoment(session, now, m, sends) is { } plain) return Ask(plain);

        if (m is { Kind: CircleKind.Foreign })
            return Ask(new Views.IslandMoment(TextGlyph, m.Label, m.Display, null, new[]
            {
                Run("Translate", () => DoAsync(session, now, new CircleIntent(CircleAction.Translate, null, "translate") { AutoLanguage = true }, false)),
                Run("Search", () => ShowResultAsync(now.Full)),
                CircleMoments.Copy(text),
                Run("Ask AI", () => AskAsync(session, $"What does this mean? \"{Clip(text, 900)}\"", now.Full, false, m.Label)),
            }, sends));

        if (m is { Kind: CircleKind.Code })
            return Ask(new Views.IslandMoment(CodeGlyph, m.Label, m.Display, null, new[]
            {
                Run("Explain", () => AskAsync(session, $"Explain this error and how to fix it: {Clip(text, 1200)}", now.Full, false, m.Label, m.Display)),
                Run("Search", () => ShowResultAsync(now.Full)),
                CircleMoments.Copy(text),
            }, sends));

        // A name or a term: a short fact (Wikipedia) when there is one, else the choices with Explain first.
        if (now.Selection.Kind == SelectionKind.Text && QuickFacts.Worth(text))
        {
            _notch.SearchBarView.ShowThinking("Looking it up…");
            _factLookup?.Cancel();
            _factLookup = new CancellationTokenSource();
            var fact = await QuickFacts.TryAsync(text, _factLookup.Token);
            if (session != _session) return null;
            if (fact is not null)
            {
                Log.Info("Circle answered with a short fact");
                return Ask(CircleMoments.For(fact, now.Info, Cancel));
            }
        }
        if (m is { Kind: CircleKind.Term })
            return Ask(new Views.IslandMoment(WordGlyph, "Word or term", m.Display, null, new[]
            {
                Run("Explain", () => AskAsync(session, $"What is {m.Display}?", now.Full, false, "Word or term")),
                Run("Search", () => ShowResultAsync(now.Full)),
                CircleMoments.Copy(text),
                Run("Translate", () => DoAsync(session, now, new CircleIntent(CircleAction.Translate, null, "translate") { AutoLanguage = true }, false)),
            }, sends));

        // Anything else (a sentence, a paragraph, words in a picture): what it is, and the choices.
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        string label = now.Selection.Kind == SelectionKind.Image ? "Words in a picture" : words == 1 ? "1 word" : $"Text · {words} words";
        var choices = new List<Views.MomentAction>
        {
            Run("Ask AI", () => AskAsync(session, $"Explain: \"{Clip(text, 1200)}\"", now.Full, false, label)),
            Run("Search", () => ShowResultAsync(now.Full)),
        };
        if (text.Length > 0)
        {
            choices.Add(CircleMoments.Copy(text));
            choices.Add(Run("Translate", () => DoAsync(session, now, new CircleIntent(CircleAction.Translate, null, "translate") { AutoLanguage = true }, false)));
        }
        return Ask(new Views.IslandMoment(TextGlyph, label, text.Length > 0 ? Clip(text, 60) : "Selected area", null, choices, sends));
    }

    /// <summary>An action that starts something and leaves the island to it (no word back).</summary>
    static Views.MomentAction Run(string label, Func<Task> start) => new(label, () =>
    {
        _ = start();
        return null;
    });

    /// <summary>The answer for a plain thing understood on this PC (a time with a zone, a code to track or pay to…), or null.</summary>
    Views.IslandMoment? PlainMoment(int session, CircleNow now, CircleMeaning m, IReadOnlyList<Views.MomentSend> sends)
    {
        var search = Run("Search", () => ShowResultAsync(now.Full));
        switch (m.Kind)
        {
            case CircleKind.ZoneTime:
                return new Views.IslandMoment(TimeGlyph, m.Label, m.Display, m.Detail, new[]
                {
                    new Views.MomentAction("Remind me", () => CircleMoments.Remind(m)), CircleMoments.Copy(m.Display), search,
                }, sends);
            case CircleKind.Tracking:
                return new Views.IslandMoment(TrackGlyph, m.Label, m.Display, null, new[]
                {
                    new Views.MomentAction("Track", () => CircleMoments.Open(m.Value, null), Closes: true), CircleMoments.Copy(m.Detail ?? m.Display), search,
                }, sends);
            case CircleKind.Flight:
                return new Views.IslandMoment(TrackGlyph, m.Label, m.Display, null, new[]
                {
                    new Views.MomentAction("Track flight", () => CircleMoments.Open(m.Value, null), Closes: true), CircleMoments.Copy(m.Detail ?? m.Display), search,
                }, sends);
            case CircleKind.Pnr:
                return new Views.IslandMoment(TrackGlyph, m.Label, m.Display, null, new[]
                {
                    new Views.MomentAction("Check status", () => CircleMoments.Open("https://www.google.com/search?q=" + Uri.EscapeDataString("PNR status " + m.Value), null), Closes: true),
                    CircleMoments.Copy(m.Value), search,
                }, sends);
            case CircleKind.Upi:
                return new Views.IslandMoment(PayGlyph, m.Label, m.Display, null, new[]
                {
                    CircleMoments.Copy(m.Value),
                    new Views.MomentAction("Open a pay app", () => CircleMoments.Open("upi://pay?pa=" + Uri.EscapeDataString(m.Value), "No UPI app on this PC"), Closes: true),
                    search,
                }, sends);
            case CircleKind.Vehicle:
                return new Views.IslandMoment(CarGlyph, m.Label, m.Display, null, new[] { CircleMoments.Copy(m.Value), search }, sends);
            case CircleKind.Ifsc:
            {
                var moment = new Views.IslandMoment(PayGlyph, m.Label, m.Display, "Looking up the branch…", new[] { CircleMoments.Copy(m.Value), search }, sends);
                _ = FillLaterAsync(session, moment, async ct => await ShortAnswers.IfscAsync(m.Value, ct) is { } b
                    ? (b.Branch.Length > 0 ? $"{b.Bank} · {b.Branch}" : b.Bank) : "Branch not found");
                return moment;
            }
            case CircleKind.Pin:
            {
                var moment = new Views.IslandMoment(PlaceGlyph, m.Label, m.Display, "Looking up the place…", new[]
                {
                    CircleMoments.Copy(m.Value),
                    new Views.MomentAction("Maps", () => CircleMoments.Open("https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString("PIN " + m.Value + " India"), null), Closes: true),
                    search,
                }, sends);
                _ = FillLaterAsync(session, moment, async ct => await ShortAnswers.PinPlaceAsync(m.Value, ct) ?? "Place not found");
                return moment;
            }
            default:
                return null;
        }
    }

    /// <summary>A lookup that fills the moment's detail line once it answers (PIN code's place, IFSC's branch).</summary>
    async Task FillLaterAsync(int session, Views.IslandMoment moment, Func<CancellationToken, Task<string>> lookup)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string line;
        try { line = await lookup(cts.Token); }
        catch (Exception) { line = "Couldn't look it up"; }
        if (session != _session) return;
        // Only if the island is still showing this circle's card.
        if (_notch.AnswerView.CurrentMoment is not { } shown || shown.Title != moment.Title || shown.Label != moment.Label) return;
        _notch.AnswerView.ShowMoment(shown with { Body = line });
        _notch.RefitAnswer();
    }

    /// <summary>The "say or type what to do" box: the words pick the action for the same circle.</summary>
    void OnAsk(CircleNow now, string words)
    {
        var intent = CircleIntent.Parse(words) ?? new CircleIntent(CircleAction.AskAi, null, words);
        _ = DoAsync(_session, now, intent, spoken: false);
    }

    /// <summary>Does what was said or typed about a circle.</summary>
    async Task DoAsync(int session, CircleNow now, CircleIntent intent, bool spoken)
    {
        var text = now.Selection.Text;
        var m = now.Meaning;
        bool picture = now.Selection.Kind == SelectionKind.Image;
        switch (intent.Action)
        {
            case CircleAction.Translate when text.Length > 0:
            {
                // "meaning" of an English word: what it means, from AI Mode, rather than a translation.
                if (intent.WantsMeaning && (m?.LanguageCode ?? "en") == "en" && intent.TargetLanguage is null)
                {
                    await AskAsync(session, $"What does \"{Clip(text, 300)}\" mean?", now.Full, spoken, "Meaning");
                    return;
                }
                var source = m?.LanguageCode;
                await TranslateAsync(session, text, intent.TargetFor(source), m?.Language, now.Full);
                return;
            }
            case CircleAction.Search:
                await ShowResultAsync(now.Full);
                return;
            case CircleAction.Copy when text.Length > 0:
            {
                bool ok = Modules.SelectionTools.ClipboardText.TrySet(text);
                ShowMoment(session, new Views.IslandMoment(TextGlyph, ok ? "✓  Copied" : "Clipboard is busy", Clip(text, 60), null,
                    Array.Empty<Views.MomentAction>(), CircleMoments.Sends(now.Info, Cancel)) { Ask = words => OnAsk(now, words) }, now.Full);
                return;
            }
            case CircleAction.Convert or CircleAction.Track or CircleAction.Call or CircleAction.WhatsApp or CircleAction.Remind or CircleAction.Open
                when m is not null:
            {
                // The understood thing's own card, and its matching action when there is one ("call" → Call).
                var card = await MomentForAsync(session, now);
                if (card is null || session != _session) return;
                ShowMoment(session, card, now.Full);
                string? want = intent.Action switch
                {
                    CircleAction.Track => card.Actions.FirstOrDefault(a => a.Label.StartsWith("Track", StringComparison.Ordinal) || a.Label == "Check status")?.Label,
                    CircleAction.Call => "Call",
                    CircleAction.WhatsApp => "WhatsApp",
                    CircleAction.Remind => "Remind me",
                    CircleAction.Open => card.Actions.FirstOrDefault(a => a.Label is "Open" or "Maps" or "Write")?.Label,
                    _ => null,
                };
                if (want is not null && card.Actions.Any(a => a.Label == want)) await _notch.AnswerView.RunMomentForTest(want);
                return;
            }
            default:
            {
                // Ask AI (and anything a circle can't do itself): the question with the circled words, answered in the island.
                if (picture && text.Length == 0)
                {
                    // AI Mode can't see the picture: Lens with the words, as before.
                    await ShowResultAsync(now.Full with { Query = intent.Question, DisplayQuery = intent.Question });
                    return;
                }
                var question = intent.Question.Length > 0 && intent.Action == CircleAction.AskAi
                    ? QueryBuilder.Combine(intent.Question, text)
                    : $"Explain: \"{Clip(text, 1200)}\"";
                await AskAsync(session, question, now.Full, spoken, m?.Kind == CircleKind.Code ? m.Label : "About what you circled",
                    m?.Kind is CircleKind.Code or CircleKind.Term ? m.Display : null);
                return;
            }
        }
    }

    /// <summary>Test hook ("circle-say <words>|<text>"): a circle around <paramref name="text"/> with <paramref name="said"/> spoken while drawing it.</summary>
    internal Task CircleSayForTest(string said, string text)
    {
        Reset(keepNotch: true);
        _session++;
        int session = _session;
        _phase = Phase.Processing;
        _notch.SetSearchMode(Shell.NotchMode.Thinking);
        var selection = new Selection(SelectionKind.Text, text, new Box(0, 0, 10, 10));
        var info = new Modules.SelectionInfo(text, null, null, false);
        var full = new Context(text, text, _settings.DefaultSearchMode, SearchFilter.None, null, null) { Selection = info };
        return CircleAsync(session, selection, info, full, said);
    }
}

using System.IO;
using System.Windows;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Shell;

namespace Shammy.Search;

/// <summary>
/// Short answers in the island: a question (said, typed, or asked about a circle) is put to Google AI Mode in the
/// results browser, out of sight; its first lines come into the island as a moment, with the full answer, ChatGPT,
/// Copy and Search one tap away. A spoken question gets its answer read aloud (Gnani), when a key is saved.
/// </summary>
internal partial class SearchCoordinator
{
    CancellationTokenSource? _answerRead;

    /// <summary>The page behind the moment is already loaded: "Full answer" shows it without searching again.</summary>
    bool _momentLoaded;

    const string AskGlyph = "";

    /// <summary>The words read aloud: the first two sentences, about 260 characters at most (Gnani credits).</summary>
    static string Spoken(string answer)
    {
        var text = answer.Replace('\n', ' ').Trim().Trim('"', '“', '”').Trim();
        int end = 0, sentences = 0;
        for (int i = 0; i < text.Length && sentences < 2; i++)
            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || text[i + 1] == ' '))
            {
                end = i + 1;
                sentences++;
            }
        if (end == 0) end = text.Length;
        if (end > 260)
        {
            int cut = text.LastIndexOf(' ', 260);
            end = cut > 120 ? cut : 260;
        }
        return text[..Math.Min(end, text.Length)].Trim();
    }

    static string Clip(string text, int max = 70)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Asks Google AI Mode <paramref name="question"/> and shows its short answer in the island. Falls back to the full
    /// results when no short answer appears in time. <paramref name="label"/> is the moment's quiet top line.
    /// </summary>
    async Task AskAsync(int session, string question, Context full, bool spoken, string? label = null)
    {
        var ai = full with { Query = question, Mode = SearchMode.Ai, Png = null, Page = null };
        string title = Clip(full.Selection is { Text.Length: > 0 } ? question : full.DisplayQuery.Length > 0 ? full.DisplayQuery : question);
        label ??= "You asked";

        // At once: the question in the island, AI Mode thinking.
        var waiting = new Views.IslandMoment(AskGlyph, label, title, null, AnswerActions(question, ai, null), Array.Empty<Views.MomentSend>())
        {
            Answer = "Thinking…",
            Source = "Google AI Mode",
            Width = 620,
        };
        ShowMoment(session, waiting, ai);

        _answerRead?.Cancel();
        var cts = _answerRead = new CancellationTokenSource();
        _ctx = ai;
        var url = QueryBuilder.BuildUrl(question, SearchMode.Ai);
        var answer = await HiddenPage.ReadAsync(new System.Windows.Interop.WindowInteropHelper(_notch).Handle, url, TimeSpan.FromSeconds(15), cts.Token);
        if (session != _session || cts.IsCancellationRequested) return;
        if (answer is null)
        {
            // No short answer (a robot check, a slow page, nothing to lift): the page itself, as before.
            Log.Info("No short answer in time: showing the full results");
            await ShowResultAsync(ai);
            return;
        }
        Log.Info($"Short answer from AI Mode: {answer.Text.Length} chars");
        var done = waiting with { Actions = AnswerActions(question, ai, answer.Text) };
        // The first two sentences in the island (and aloud); the rest is one tap away under "Full answer".
        done = done with { Answer = Spoken(answer.Text), SpeakText = Spoken(answer.Text) };
        _notch.AnswerView.ShowMoment(done);
        _notch.RefitAnswer();
        if (spoken) Modules.HostLinks.SayAnyway?.Invoke(Spoken(answer.Text));
    }

    /// <summary>
    /// Whether words want the results page rather than a short answer: they start with "search" or "google" (the rest is
    /// what to search, in <paramref name="search"/>), or a filter was asked for ("on reddit", "PDFs").
    /// </summary>
    static bool WantsResults(string text, SearchFilter filters, out string search)
    {
        search = text.Trim();
        if (filters != SearchFilter.None) return true;
        var m = System.Text.RegularExpressions.Regex.Match(search, @"^(?:search(?:\s+for)?|google|look\s+up)\s+(.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        if (!m.Success) return false;
        search = m.Groups[1].Value.Trim();
        return search.Length > 0;
    }

    /// <summary>Full answer (the page already loaded), Open in ChatGPT, Copy (once there is an answer), Search the web.</summary>
    IReadOnlyList<Views.MomentAction> AnswerActions(string question, Context ai, string? answer)
    {
        var list = new List<Views.MomentAction>
        {
            new("Full answer", () =>
            {
                _ = ShowResultAsync(ai, navigate: !_momentLoaded);
                return null;
            }),
            new("Open in ChatGPT", () =>
            {
                Views.ResultView.OpenExternal(ShortAnswers.ChatGptUrl(question).AbsoluteUri);
                return null;
            }, Closes: true),
        };
        if (answer is not null)
            list.Add(new("Copy", () =>
            {
                try { Clipboard.SetText(answer); }
                catch (Exception) { return "Couldn't copy"; }
                return "Copied";
            }));
        list.Add(new("Search", () =>
        {
            _ = ShowResultAsync(ai with { Mode = SearchMode.Web }, navigate: true);
            return null;
        }));
        return list;
    }

    // ---- Translation ------------------------------------------------------------------------------------------

    const string TranslateGlyph = "";

    static string LanguageName(string code) => code switch
    {
        "en" => "English",
        "hi" => "Hindi",
        _ => TryCulture(code),
    };

    static string TryCulture(string code)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }

    /// <summary>
    /// <paramref name="text"/> translated into <paramref name="target"/> in the island: Google Translate's page first, AI
    /// Mode if that fails. <paramref name="from"/> names the source language for the label when known.
    /// </summary>
    async Task TranslateAsync(int session, string text, string target, string? from, Context full)
    {
        string label = $"{(from ?? "Translating")} → {LanguageName(target)}";
        var waiting = new Views.IslandMoment(TranslateGlyph, label, Clip(text, 60), null, Array.Empty<Views.MomentAction>(), Array.Empty<Views.MomentSend>())
        {
            Answer = "Translating…",
            Source = "Google Translate",
            Width = 620,
        };
        ShowMoment(session, waiting, full);
        _answerRead?.Cancel();
        var cts = _answerRead = new CancellationTokenSource();
        var source = text.Length > ShortAnswers.MaxTranslate ? text[..ShortAnswers.MaxTranslate] : text;
        var translated = await HiddenPage.TranslateAsync(new System.Windows.Interop.WindowInteropHelper(_notch).Handle, source, target,
            TimeSpan.FromSeconds(8), cts.Token);
        if (session != _session || cts.IsCancellationRequested) return;
        if (translated is null)
        {
            Log.Info("Google Translate gave nothing: asking AI Mode instead");
            await AskAsync(session, $"Translate to {LanguageName(target)}: \"{Clip(text, 900)}\"", full, spoken: false, label: label);
            return;
        }
        string other = target == "hi" ? "en" : "hi";
        var actions = new List<Views.MomentAction>
        {
            new("Copy", () => Modules.SelectionTools.ClipboardText.TrySet(translated) ? "Copied" : "Clipboard is busy"),
            new($"In {LanguageName(other)}", () =>
            {
                _ = TranslateAsync(session, text, other, from, full);
                return null;
            }),
            new("Search", () =>
            {
                _ = ShowResultAsync(full);
                return null;
            }),
        };
        // The translation is the hero; the original stays as the small line above it.
        var done = waiting with { Title = translated.Length <= 60 ? translated : Clip(text, 60), Actions = actions };
        done = done with { Answer = translated.Length <= 60 ? null : translated, Body = translated.Length <= 60 ? Clip(text, 120) : null };
        _notch.AnswerView.ShowMoment(done);
        _notch.RefitAnswer();
    }

    // ---- Pictures -----------------------------------------------------------------------------------------------

    const string PictureGlyph = "";

    /// <summary>
    /// A circled picture: Lens's own short answer (its AI Overview's first lines) in the island first, with the full Lens
    /// results one tap away. Falls back to the Lens results when there is no short answer.
    /// </summary>
    async Task PictureAsync(int session, byte[] png, string textInIt, Context full)
    {
        var waiting = new Views.IslandMoment(PictureGlyph, "Looking at the picture", "Picture", null, Array.Empty<Views.MomentAction>(), Array.Empty<Views.MomentSend>())
        {
            Answer = "Looking…",
            Source = "Google Lens",
            Width = 620,
        };
        ShowMoment(session, waiting, full);
        _answerRead?.Cancel();
        var cts = _answerRead = new CancellationTokenSource();
        var answer = await HiddenPage.ReadLensAsync(new System.Windows.Interop.WindowInteropHelper(_notch).Handle, png, TimeSpan.FromSeconds(16), cts.Token);
        if (session != _session || cts.IsCancellationRequested) return;
        if (answer is null)
        {
            Log.Info("No short answer for the picture: showing Lens");
            await ShowResultAsync(full);
            return;
        }
        var shortText = Spoken(answer.Text);
        var actions = new List<Views.MomentAction>
        {
            new("Lens results", () =>
            {
                _ = ShowResultAsync(full);
                return null;
            }),
            new("Ask AI", () =>
            {
                _ = AskAsync(session, "What is this? " + shortText, full, spoken: false, label: "About the picture");
                return null;
            }),
        };
        if (textInIt.Length > 0)
        {
            actions.Add(new("Copy text", () => Modules.SelectionTools.ClipboardText.TrySet(textInIt) ? "Copied" : "Clipboard is busy"));
            actions.Add(new("Translate", () =>
            {
                _ = TranslateAsync(session, textInIt, "en", null, full);
                return null;
            }));
        }
        var done = waiting with { Label = "Looks like", Title = FirstPhrase(shortText), Answer = shortText, Actions = actions, SpeakText = shortText };
        _notch.AnswerView.ShowMoment(done);
        _notch.RefitAnswer();
    }

    /// <summary>A title from an answer's first sentence: up to its first comma or "is", at most about 40 characters.</summary>
    static string FirstPhrase(string sentence)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(sentence.Trim(),
            @"^(the|this) (image|picture|photo) (features|shows|depicts|is of|contains)\s+|^(this|it) (is|appears to be|looks like)\s+|^(a|an|the)\s+(?=\p{Lu})",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        if (s.Length > 0) s = char.ToUpperInvariant(s[0]) + s[1..];
        foreach (var cut in new[] { " is a ", " is an ", " is the ", ", ", " (" })
        {
            int at = s.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (at is > 2 and < 48) return s[..at].Trim();
        }
        return Clip(s, 42);
    }

    /// <summary>Test pipe ("translate-text <lang>|<text>"): translates as the Translate action would.</summary>
    internal async Task TranslateForTestAsync(string target, string text)
    {
        Cancel();
        int session = ++_session;
        var ctx = new Context(text, text, SearchMode.Ai, SearchFilter.None, null, null);
        await TranslateAsync(session, text, target, null, ctx);
    }

    /// <summary>Test pipe ("picture-file <path>"): a picture as a silent circle of it would be answered.</summary>
    internal async Task PictureForTestAsync(string path)
    {
        Cancel();
        int session = ++_session;
        var png = await File.ReadAllBytesAsync(path);
        var ctx = new Context("", "Selected area", SearchMode.Lens, SearchFilter.None, png, null);
        await PictureAsync(session, png, "", ctx);
    }

    /// <summary>Test pipe ("ask"): puts a question to AI Mode as a typed one would be, and says what the island shows.</summary>
    internal async Task<string> AskForTestAsync(string question, bool spoken)
    {
        Cancel();
        int session = ++_session;
        var ctx = new Context(question, question, SearchMode.Ai, SearchFilter.None, null, null);
        await AskAsync(session, question, ctx, spoken);
        return _phase == Phase.Showing && _notch.Mode == NotchMode.Answer ? "answer: " + _notch.AnswerView.DescribeMomentForTest()
            : _notch.Mode == NotchMode.Result ? "full results" : "nothing";
    }
}

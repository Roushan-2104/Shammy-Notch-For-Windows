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

    /// <summary>
    /// The short answer shown and read aloud: its first two whole sentences, as long as they fit about 320 characters
    /// (the first sentence alone if two don't); only one very long sentence is cut, at a word.
    /// </summary>
    static string Spoken(string answer)
    {
        // A page can show the same line twice (a heading and its paragraph): once is enough.
        var lines = answer.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var text = string.Join(' ', lines).Trim().Trim('"', '“', '”').Trim();
        int end = 0, sentences = 0;
        for (int i = 0; i < text.Length && sentences < 2; i++)
        {
            if (text[i] is not ('.' or '!' or '?') || (i + 1 < text.Length && text[i + 1] != ' ')) continue;
            if (sentences > 0 && i + 1 > Core.OnlineVoice.SpokenPhrases.AskedMaxLength) break;
            end = i + 1;
            sentences++;
        }
        if (end == 0) end = text.Length;
        return Core.OnlineVoice.SpokenPhrases.CleanAsked(text[..end]);
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
    /// <param name="brief">The reply is one short line (a translation): read it however short it is.</param>
    async Task AskAsync(int session, string question, Context full, bool spoken, string? label = null, string? title = null, bool brief = false)
    {
        var ai = full with { Query = question, Mode = SearchMode.Ai, Png = null, Page = null };
        // The big line: what was asked about (the circled thing) or the question itself.
        title = Clip(title ?? (full.Selection is { Text.Length: > 0 } sel ? sel.Text : full.DisplayQuery.Length > 0 ? full.DisplayQuery : question), 60);
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
        var parent = new System.Windows.Interop.WindowInteropHelper(_notch).Handle;
        var answer = brief
            ? await HiddenPage.ReadBriefAsync(parent, url, TimeSpan.FromSeconds(15), cts.Token)
            : await HiddenPage.ReadAsync(parent, url, TimeSpan.FromSeconds(15), cts.Token);
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
            await AskAsync(session, $"Translate to {LanguageName(target)}, only the translation: \"{Clip(text, 900)}\"", full, spoken: false, label: label, title: Clip(text, 60), brief: true);
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
        var page = await HiddenPage.ReadLensPageAsync(new System.Windows.Interop.WindowInteropHelper(_notch).Handle, png, TimeSpan.FromSeconds(16), cts.Token);
        if (session != _session || cts.IsCancellationRequested) return;
        var overview = HiddenPage.AnswerFrom(page);
        // A product (Lens lists it with prices): the price list, cheapest first, from Google Shopping.
        var lensOffers = Core.Shopping.ShoppingOffers.Parse(page);
        if (lensOffers.Count >= 2)
        {
            string product = overview is not null ? FirstPhrase(Spoken(overview)) : Clip(lensOffers[0].Title, 50);
            Log.Info($"Circled picture is a product ({lensOffers.Count} offers on Lens)");
            await ShopAsync(session, product, full, lensOffers);
            return;
        }
        var answer = overview is null ? null : new ShortAnswer(overview, "");
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
        var done = waiting with { Label = "Looks like", Title = FirstPhrase(shortText), Answer = shortText, Actions = actions, SpeakText = shortText, DragPng = png };
        _notch.AnswerView.ShowMoment(done);
        _notch.RefitAnswer();
    }

    // ---- Shopping ---------------------------------------------------------------------------------------------

    const string ShopGlyph = "";

    /// <summary>
    /// Circle to buy smarter: who sells <paramref name="product"/> cheapest, from Google Shopping (Indian stores), as a short
    /// list in the island: store, delivery and rating, price, the cheapest highlighted. All prices opens the full list;
    /// Watch price tells you in the island when it drops. <paramref name="fallback"/> (Lens's own offers) is used when the
    /// Shopping page gives nothing.
    /// </summary>
    async Task ShopAsync(int session, string product, Context full, IReadOnlyList<Core.Shopping.ShoppingOffer>? fallback = null)
    {
        product = product.Trim();
        var shoppingPage = full with { Page = HiddenPage.ShoppingUrl(product), Query = product, DisplayQuery = product, Mode = SearchMode.Web };
        var waiting = new Views.IslandMoment(ShopGlyph, "Finding prices", Clip(product, 50), null, Array.Empty<Views.MomentAction>(), Array.Empty<Views.MomentSend>())
        {
            Answer = "Looking at the stores…",
            Source = "Google Shopping",
            Width = 640,
        };
        ShowMoment(session, waiting, shoppingPage);
        _answerRead?.Cancel();
        var cts = _answerRead = new CancellationTokenSource();
        var text = await HiddenPage.ReadShoppingAsync(new System.Windows.Interop.WindowInteropHelper(_notch).Handle, product, TimeSpan.FromSeconds(18), cts.Token);
        if (session != _session || cts.IsCancellationRequested) return;
        var offers = Core.Shopping.ShoppingOffers.Best(Core.Shopping.ShoppingOffers.Parse(text), product);
        if (offers.Count == 0 && fallback is not null) offers = Core.Shopping.ShoppingOffers.Best(fallback, null);
        if (offers.Count == 0)
        {
            Log.Info("No prices found: showing the shopping page");
            await ShowResultAsync(shoppingPage);
            return;
        }
        Log.Info($"Prices: {offers.Count} stores");
        var rows = offers.Select((o, i) => new Views.MomentRow(o.Store, Detail(o), o.PriceText, i == 0, i == 0 ? "Cheapest" : null)).ToList();
        var cheapest = offers[0];
        var actions = new List<Views.MomentAction>
        {
            new("All prices", () =>
            {
                _ = ShowResultAsync(shoppingPage);
                return null;
            }),
            new("Watch price", () => Modules.HostLinks.WatchPrice?.Invoke(product, cheapest.Price, cheapest.Store) ?? "Price watch is off"),
            new("Copy", () => Modules.SelectionTools.ClipboardText.TrySet(string.Join(Environment.NewLine, offers.Select(o => $"{o.Store}: {o.PriceText}")))
                ? "Copied" : "Clipboard is busy"),
            new("Search", () =>
            {
                _ = ShowResultAsync(full with { Query = product, DisplayQuery = product, Page = null });
                return null;
            }),
        };
        var done = waiting with { Label = "Prices · cheapest first", Answer = null, Rows = rows, Actions = actions };
        _notch.AnswerView.ShowMoment(done);
        _notch.RefitAnswer();
    }

    /// <summary>"Free delivery · tomorrow · ★ 4.4 (1.1K)": delivery, fee and rating, as the owner asked for each store.</summary>
    static string Detail(Core.Shopping.ShoppingOffer o)
    {
        var parts = new List<string>();
        if (o.Delivery.Length > 0) parts.Add(o.Delivery);
        if (o.Rating is { } r) parts.Add($"★ {r:0.0}{(o.Reviews.Length > 0 ? $" ({o.Reviews})" : "")}");
        return string.Join(" · ", parts);
    }

    /// <summary>Test pipe ("shop <product>"): the price list for a product, as saying "price" over its name would show it.</summary>
    internal async Task ShopForTestAsync(string product)
    {
        Cancel();
        int session = ++_session;
        var ctx = new Context(product, product, SearchMode.Ai, SearchFilter.None, null, null);
        await ShopAsync(session, product, ctx);
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

using Shammy.Core.Conversions;
using Shammy.Core.Hover;
using Shammy.Core.Search;
using Shammy.Modules.SelectionTools;
using Shammy.Search;

namespace Shammy.Modules.Hover;

/// <summary>
/// Turns a hovered thing into the bubble's words. Prices, measurements and times are worked out on the PC; a
/// translation (Google Translate) and a word's meaning (Google AI Mode) send only those words, through the same hidden
/// page the island's short answers use. Answers are kept in memory for this run only, so moving back and forth over a
/// word costs nothing; nothing is written anywhere and nothing about a hover is logged.
/// </summary>
internal sealed class HoverAnswers
{
    const int MaxCached = 300;
    static readonly TimeSpan TranslateLimit = TimeSpan.FromSeconds(10);
    static readonly TimeSpan MeaningLimit = TimeSpan.FromSeconds(14);
    static readonly TimeSpan RatesWait = TimeSpan.FromSeconds(3);

    readonly ModuleContext _ctx;
    readonly ExchangeRates _rates;
    // Online answers by kind and words; a null value means "asked, and there was nothing to show".
    readonly Dictionary<string, HoverBubble?> _cache = new(StringComparer.Ordinal);

    public HoverAnswers(ModuleContext ctx)
    {
        _ctx = ctx;
        _rates = new ExchangeRates(ctx);
    }

    public int CacheCount => _cache.Count;

    /// <summary>Whether the answer needs the network (the bubble then shows its label first, with "…").</summary>
    public bool IsOnline(HoverTarget t) => t.Kind is HoverKind.Translation or HoverKind.Meaning && !_cache.ContainsKey(Key(t));

    /// <summary>What the bubble shows while an online answer is on its way.</summary>
    public static HoverBubble Waiting(HoverTarget t) => t.Kind == HoverKind.Meaning
        ? new HoverBubble(HoverKind.Meaning, "Meaning", "…", false, "meaning")
        : new HoverBubble(HoverKind.Translation, t.Language ?? "Translating", "…", false, "translation");

    /// <summary>The bubble for a hovered thing, or null when there's nothing worth showing. Runs on the UI thread; never throws.</summary>
    /// <param name="patient">Wait for exchange rates however long they take (the test pipes); a hover gives up after a few seconds.</param>
    public async Task<HoverBubble?> AnswerAsync(HoverTarget t, CancellationToken ct, bool patient = false)
    {
        try
        {
            switch (t.Kind)
            {
                case HoverKind.Measure when t.Conversion is { } c:
                    return HoverText.Measure(c);

                case HoverKind.Time when t.Time is { At: { } at } time:
                    return HoverText.Time(at, time.Detail);

                case HoverKind.Price when t.Money is { } money:
                {
                    var to = Currency.DefaultTarget(money.Code);
                    var pending = _rates.GetAsync();
                    if (!patient && await Task.WhenAny(pending, Task.Delay(RatesWait, ct)) != pending) return null;
                    var table = await pending;
                    if (table is null || !Currency.TryConvert(money.Amount, money.Code, to, table.Rates, out var value)) return null;
                    return HoverText.Price(value, to);
                }

                case HoverKind.Translation or HoverKind.Meaning:
                {
                    var key = Key(t);
                    if (_cache.TryGetValue(key, out var known)) return known;
                    var bubble = t.Kind == HoverKind.Translation ? await TranslateAsync(t, ct) : await MeaningAsync(t, ct);
                    // A cancelled lookup (the pointer moved on) isn't an answer: ask again next time.
                    if (ct.IsCancellationRequested) return null;
                    if (_cache.Count >= MaxCached) _cache.Clear();
                    _cache[key] = bubble;
                    return bubble;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Never the words themselves (nor an exception's message, which could quote them): only the kind and the type.
            _ctx.Warn($"A hover {t.Kind} lookup failed ({ex.GetType().Name})");
        }
        return null;
    }

    async Task<HoverBubble?> TranslateAsync(HoverTarget t, CancellationToken ct)
    {
        var parent = Parent();
        if (parent == IntPtr.Zero) return null;
        // Always into English: English text is never translated (Hindi only if asked, and a hover doesn't ask).
        var translated = await HiddenPage.TranslateAsync(parent, t.Text, "en", TranslateLimit, ct);
        if (translated is null && !ct.IsCancellationRequested)
        {
            // Google Translate's page unavailable (a robot check, slow): AI Mode translates instead, as circling does.
            var url = QueryBuilder.BuildUrl($"Translate to English, reply with only the translation: \"{t.Text}\"", SearchMode.Ai);
            var answer = await HiddenPage.ReadBriefAsync(parent, url, MeaningLimit, ct);
            translated = answer?.Text.Trim().Trim('"', '“', '”').Trim();
        }
        return HoverText.Translation(t.Text, translated, t.Language);
    }

    async Task<HoverBubble?> MeaningAsync(HoverTarget t, CancellationToken ct)
    {
        var parent = Parent();
        if (parent == IntPtr.Zero) return null;
        var url = QueryBuilder.BuildUrl("define " + t.Text, SearchMode.Ai);
        var answer = await HiddenPage.ReadAsync(parent, url, MeaningLimit, ct);
        return HoverText.Meaning(t.Text, answer?.Text);
    }

    /// <summary>The hidden page lives in the notch's window, as the island's short answers do.</summary>
    IntPtr Parent() => new System.Windows.Interop.WindowInteropHelper(_ctx.Notch).Handle;

    static string Key(HoverTarget t) => (t.Kind == HoverKind.Meaning ? "m:" : "t:") + t.Text;
}

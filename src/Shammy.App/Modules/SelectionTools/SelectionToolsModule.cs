using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Shammy.Core.Conversions;
using Shammy.Views;

namespace Shammy.Modules.SelectionTools;

/// <summary>
/// Circle-to-search superpowers: QR codes and barcodes act instead of searching, circled prices and
/// measurements convert, results get "Copy text" and "Translate" chips, and typed or spoken conversions
/// (units, money, time zones, translation) answer without a web search.
/// </summary>
internal sealed class SelectionToolsModule : IShammyModule
{
    ModuleContext _ctx = null!;
    ExchangeRates _rates = null!;
    QrTakeover _qr = null!;
    AmountTakeover _amount = null!;
    ConvertCommand _convert = null!;
    bool _qrWarm;

    public string Name => "selection";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _rates = new ExchangeRates(ctx);
        _qr = new QrTakeover(ctx);
        _amount = new AmountTakeover(ctx, _rates);
        _convert = new ConvertCommand(ctx, _rates);

        // Text checks are instant, so they go before the pixel decode.
        ctx.AddSelectionHandler(_amount);
        ctx.AddSelectionHandler(_qr);
        ctx.AddCommand(_convert);

        ctx.AddResultChip(new ResultChip("Copy text", s => !string.IsNullOrWhiteSpace(s?.Text), (s, _) => CopySelection(s)));
        ctx.AddResultChip(new ResultChip("Translate", s => TranslatableText(s) is not null, (s, view) => _ = TranslateInCardAsync(s, view)));

        // Circle → send it anywhere, from the full results too (the island's moment has the same four as icons).
        CircleSend.Use(ctx);
        ctx.AddResultChip(new ResultChip("Note", s => s is not null && CircleSend.CanNote(s), (s, view) => Said(view, "Note", CircleSend.ToNotes(s!))));
        ctx.AddResultChip(new ResultChip("Shelf", s => s is not null && CircleSend.CanShelf(s), (s, view) => Said(view, "Shelf", CircleSend.ToShelf(s!))));
        ctx.AddResultChip(new ResultChip("Phone", s => s is not null && CircleSend.CanPhone(s), (s, view) => _ = PhoneAsync(s!, view)));
        ctx.AddResultChip(new ResultChip("Stage", s => s is not null && CircleSend.CanStage(s), (s, _) =>
        {
            var r = CircleSend.ToStage(s!);
            // Stage takes the island: the results card closes to make room.
            if (!r.IsError) ctx.Notch.ResultView.RequestClose();
            else ctx.ShowMessage(r.Glyph, r.Title, error: true);
        }));
        ctx.AddPipeCommand("circle-save", arg => CircleSend.Save(new SelectionInfo(arg, null, null, false)) ?? "error");

        ctx.AddPipeCommand("selection-qr", QrFromFile);
        ctx.AddPipeCommand("selection-convert", ConvertForPipe);
        ctx.AddPipeCommand("selection-state", _ => State());

        _ = Task.Run(() =>
        {
            QrReader.WarmUp();
            _qrWarm = true;
        });
        _ = Task.Run(async () =>
        {
            // Rates ready before the first circled price, without slowing startup.
            await Task.Delay(TimeSpan.FromSeconds(10));
            await _rates.GetAsync();
        });
    }

    static void Said(ResultView view, string chip, CommandResult r) => view.SayOnChip(chip, r.IsError ? r.Title : ShortWord(r));

    static string ShortWord(CommandResult r) => r.Title switch
    {
        "On the Shelf" => "On the Shelf",
        _ when r.Title.StartsWith("Scan", StringComparison.Ordinal) => "Scan the code",
        _ => "Saved",
    };

    async Task PhoneAsync(SelectionInfo s, ResultView view)
    {
        var r = await CircleSend.ToPhoneAsync(s);
        if (r.IsError) view.SayOnChip("Phone", r.Title);
        // The QR card waits until the results card closes.
        else view.RequestClose();
    }

    void CopySelection(SelectionInfo? selection)
    {
        if (string.IsNullOrWhiteSpace(selection?.Text)) return;
        if (ClipboardText.TrySet(selection.Text.Trim())) _ctx.ShowMessage(Glyphs.Copy, "Copied");
        else _ctx.ShowMessage(Glyphs.Error, "Clipboard is busy", error: true);
    }

    /// <summary>The circled text, or for typed/spoken searches the query (chips aren't given it, but the card shows it).</summary>
    string? TranslatableText(SelectionInfo? selection)
    {
        if (selection is not null) return string.IsNullOrWhiteSpace(selection.Text) ? null : selection.Text.Trim();
        var query = _ctx.Notch.ResultView.QueryText.Text?.Trim();
        // "Image" is the card's label for a pasted image search: nothing to translate.
        return string.IsNullOrEmpty(query) || query == "Image" ? null : query;
    }

    async Task TranslateInCardAsync(SelectionInfo? selection, ResultView view)
    {
        var text = TranslatableText(selection);
        if (text is null) return;
        try
        {
            await view.NavigateAsync(Translation.BuildUrl(text, Translation.DefaultTarget(text)));
        }
        catch (Exception ex)
        {
            _ctx.Warn("Opening Google Translate in the card failed", ex);
        }
    }

    // ---- Test pipe ---------------------------------------------------------------------------------

    /// <summary>"selection-qr C:\code.png" → the decoded text or "none". Decodes only; nothing is opened or copied.</summary>
    string QrFromFile(string arg)
    {
        var path = arg.Trim().Trim('"');
        if (!File.Exists(path)) return "error: file not found";
        try
        {
            BitmapSource bitmap;
            using (var stream = File.OpenRead(path))
                bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var sw = Stopwatch.StartNew();
            var pixels = QrReader.ReadPixels(bitmap);
            var code = pixels is null ? null : QrReader.Decode(pixels);
            _qr.Record(code, sw.ElapsedMilliseconds, pixels is null ? "too small" : $"pipe {pixels.Width}x{pixels.Height}");
            return code is null ? "none" : code.Value.Text.ReplaceLineEndings(" ");
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    /// <summary>"selection-convert 5 ft in cm" → "152.4 cm | 5 ft"; falls back to the circled-amount path ("amount: …").</summary>
    string ConvertForPipe(string arg)
    {
        var text = arg.Trim();
        try
        {
            // Off the UI thread: the pipe handler runs on it and blocks until the answer is back.
            var outcome = Task.Run(() => _convert.EvaluateAsync(text)).GetAwaiter().GetResult();
            if (outcome is not null)
                return Describe(outcome.Result) + (outcome.OpenUrl is null ? "" : " | " + outcome.OpenUrl);
            var amount = Task.Run(() => _amount.EvaluateAsync(text, waitForRates: true)).GetAwaiter().GetResult();
            return amount is null ? "none" : "amount: " + Describe(amount);
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    static string Describe(CommandResult r) => (r.IsError ? "error: " : "") + r.Title + (r.Detail is null ? "" : " | " + r.Detail);

    string State() =>
        $"handlers=amount,qr commands=convert chips=Copy text,Translate qrWarm={_qrWarm} lastQr={_qr.Last} " +
        $"lastAmount={_amount.Last} rates={_rates.Describe()}";

    public void Dispose() => CircleSend.Use(null);
}

internal static class Glyphs
{
    public const string Link = "\uE71B";
    public const string Wifi = "\uE701";
    public const string Qr = "\uED14";
    public const string Copy = "\uE8C8";
    public const string Measure = "\uE8EF";
    public const string Money = "\uE8C7";
    public const string Clock = "\uE121";
    public const string Translate = "\uE774";
    public const string Error = "\uE783";
}

internal static class ClipboardText
{
    public static readonly CommandResult Busy = new(Glyphs.Error, "Clipboard is busy", "Try again in a moment", IsError: true);

    /// <summary>
    /// Sets clipboard text on the UI thread, retrying briefly while another app holds the clipboard.
    /// With <paramref name="secret"/> (Wi-Fi passwords, QR secrets) it carries the same flags password
    /// managers use, so Windows clipboard history, cloud clipboard and other monitors skip it.
    /// </summary>
    public static bool TrySet(string text, bool secret = false)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var data = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, text);
                if (secret)
                {
                    data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
                    data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                    data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
                }
                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}

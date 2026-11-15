using System.Media;
using System.Windows;
using System.Windows.Controls;
using Shammy.Core.PhoneLink;
using Shammy.Infrastructure;
using Shammy.Modules.PhoneSend;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// The phone button in the open notch's top bar (your call, 16 Nov) and the card it opens: whether the phone is online
/// (a green dot there and on the resting notch), pairing, and getting Shammy Island onto a phone with a QR code, from
/// this laptop over Wi-Fi or from GitHub. Also the falling chime that says "from your phone".
/// </summary>
internal sealed partial class PhoneLinkModule
{
    /// <summary>Where a published release keeps the phone app (uploaded with each Shammy release).</summary>
    const string GitHubApk = "https://github.com/Roushan-2104/Shammy-Notch-For-Windows/releases/latest/download/ShammyIsland.apk";

    const string PhoneIcon = "M8.5 3h7a1.5 1.5 0 0 1 1.5 1.5v15a1.5 1.5 0 0 1 -1.5 1.5h-7A1.5 1.5 0 0 1 7 19.5v-15A1.5 1.5 0 0 1 8.5 3z M11 18h2";
    const string DownloadIcon = "M12 4v11 M7.5 10.5L12 15l4.5-4.5 M5 19h14";

    static readonly Lazy<SoundPlayer> PhoneChime = new(() =>
    {
        var p = new SoundPlayer(new MemoryStream(Chime.Wav(Chime.Samples(Chime.FromPhone))));
        p.Load();
        return p;
    });

    PhoneChoiceSheet? _phoneSheet;
    PhoneTransfer? _apkTransfer;

    void StartCard()
    {
        _ctx.AddCard(new NotchCard
        {
            Id = "phone",
            Title = "Phone",
            Width = 176,
            Order = 89,
            Chip = true,
            // opened from its own button in the top bar (and the More chips), never a tile on the rail
            IsRelevant = () => false,
            Compact = new Border(),
            Detail = BuildPhoneSheet,
        });
    }

    void StopCard()
    {
        _apkTransfer?.Dispose();
        _apkTransfer = null;
    }

    FrameworkElement BuildPhoneSheet()
    {
        var sheet = new PhoneChoiceSheet(() => _ctx.OpenCard("home"));
        sheet.TitleText.Text = "Your phone";
        _phoneSheet = sheet;
        FillPhoneSheet();
        return sheet;
    }

    /// <summary>The card as things are now (it's rebuilt when the phone comes or goes, or pairs).</summary>
    void FillPhoneSheet()
    {
        if (_phoneSheet is not { } sheet) return;
        bool paired = _store.Phone is not null;
        string name = _store.PhoneName ?? "your phone";
        string status = !paired ? "not linked" : _live is not null ? $"{name} · online" : $"{name} · away";
        var rows = new List<PhoneChoiceSheet.Row>();
        if (!paired)
            rows.Add(new("pair", PhoneIcon, "Pair your phone", "Open Shammy Island on it and tap Pair", () => { OpenPairing(); return "pairing"; }));
        rows.Add(new("get-here", DownloadIcon, paired ? "Get the app on another phone" : "Get Shammy Island", "QR code: download it from this laptop over your Wi-Fi", GetFromLaptop));
        rows.Add(new("get-github", PhoneChoiceSheet.Icons.Globe, "Get it from GitHub instead", "QR code: the download on GitHub", GetFromGitHub));
        if (paired)
            rows.Add(new("unpair", CrossIcon, "Unpair", $"{name} stops linking until you pair again", () => { Unpair(); FillPhoneSheet(); return "unpaired"; }));
        sheet.Show(status, rows);
    }

    /// <summary>"From this laptop": the bundled app, behind a QR code, through Send to phone's own page (Wi-Fi only).</summary>
    string GetFromLaptop()
    {
        var apk = Path.Combine(AppContext.BaseDirectory, "Assets", "Phone", "ShammyIsland.apk");
        if (!File.Exists(apk)) return "error: the phone app isn't in this copy of Shammy";
        try
        {
            _apkTransfer?.Dispose();
            _apkTransfer = PhoneTransfer.Start(_ctx, apk);
            return "serving the app";
        }
        catch (PhoneSendException ex)
        {
            _ctx.ShowMessage("", ex.Message, error: true);
            return "error: " + ex.Message;
        }
    }

    /// <summary>"From GitHub": a QR code to the published download (no server runs).</summary>
    string GetFromGitHub()
    {
        var card = PhoneSendCard.ForQr(GitHubApk, "Shammy Island", "Scan with your phone", "Downloads the app from GitHub", "", "Shammy Island download");
        card.CloseRequested += () => _ctx.CloseCard();
        _ctx.ShowCard(card, PhoneSendCard.CardWidth, PhoneSendCard.CardHeight, interactive: true);
        return "github qr";
    }

    /// <summary>Online or not: the green dots and the card follow.</summary>
    void ShowOnline(bool on)
    {
        _ctx.Notch.SetPhoneOnline(on);
        FillPhoneSheet();
    }

    /// <summary>"From your phone" by ear: Shammy's falling two-note, never during a call or focus (quiet).</summary>
    void PlayPhoneChime()
    {
        if (QuietState.IsQuiet) return;
        try { PhoneChime.Value.Play(); }
        catch (Exception ex) { _ctx.Warn("The phone chime couldn't play", ex); }
    }
}

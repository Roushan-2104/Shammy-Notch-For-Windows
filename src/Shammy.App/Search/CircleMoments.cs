using System.Globalization;
using Shammy.Core.Search;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Modules.SelectionTools;
using Shammy.Views;

namespace Shammy.Search;

/// <summary>
/// Turns what was circled, once understood, into the island's moment: the big line, and what you'd do next with it.
/// A phone number → Call, WhatsApp, Copy; an email → Write, Copy; a link → Open, Copy; a date → Remind me, Calendar,
/// Copy; an address → Maps, Copy; a sum → Copy; a colour → Copy hex, Copy RGB; a short fact → Wikipedia. Every moment
/// also has the quiet "send it" icons (Notes, Shelf, phone, Stage) where those features are on.
/// </summary>
internal static class CircleMoments
{
    const string PhoneGlyph = "", MailGlyph = "", LinkGlyph = "", CalendarGlyph = "", MapGlyph = "",
        SumGlyph = "", InfoGlyph = "";

    /// <summary>The kinds this class has a moment for; any other kind is searched as before until it gets one.</summary>
    public static bool Shows(CircleKind kind) =>
        kind is CircleKind.Phone or CircleKind.Email or CircleKind.Link or CircleKind.When or CircleKind.Address or CircleKind.Sum or CircleKind.Colour;

    /// <param name="close">Closes the moment (Stage and the phone's QR card take the island next).</param>
    public static IslandMoment For(CircleMeaning m, SelectionInfo selection, Action close)
    {
        var sends = Sends(selection, close);
        switch (m.Kind)
        {
            case CircleKind.Phone:
            {
                var actions = new List<MomentAction> { new("Call", () => Open("tel:" + m.Value, "No app for calls on this PC"), Closes: true) };
                if (CircleSense.WhatsAppNumber(m.Value, Region()) is { } wa)
                    actions.Add(new MomentAction("WhatsApp", () => Open("https://wa.me/" + wa, null), Closes: true));
                actions.Add(Copy(m.Value));
                return new IslandMoment(PhoneGlyph, "Phone number · read on this PC", m.Display, null, actions, sends);
            }
            case CircleKind.Email:
                return new IslandMoment(MailGlyph, "Email address · read on this PC", m.Display, null,
                    new[] { new MomentAction("Write", () => Open("mailto:" + m.Value, "No email app on this PC"), Closes: true), Copy(m.Value) }, sends);
            case CircleKind.Link:
                return new IslandMoment(LinkGlyph, "Link · read on this PC", m.Display, null,
                    new[] { new MomentAction("Open", () => Open(m.Value, null), Closes: true), Copy(m.Value) }, sends);
            case CircleKind.When:
                return new IslandMoment(CalendarGlyph, (m.Title ?? (m.HasTime ? "Date and time" : "Date")) + " · read on this PC", m.Display, null,
                    new[] { new MomentAction("Remind me", () => Remind(m)), new MomentAction("Calendar", () => Calendar(m), Closes: true), Copy(m.Display) }, sends);
            case CircleKind.Address:
                return new IslandMoment(MapGlyph, "Address · read on this PC", m.Display, m.Value.Length > m.Display.Length ? m.Value : null,
                    new[]
                    {
                        new MomentAction("Maps", () => Open("https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(m.Value), null), Closes: true),
                        Copy(m.Value),
                    }, sends);
            case CircleKind.Sum:
                return new IslandMoment(SumGlyph, m.Label + " =", m.Display, null, new[] { Copy(m.Value) }, sends);
            case CircleKind.Colour:
            {
                var c = m.Colour!.Value;
                var rgb = string.Create(CultureInfo.InvariantCulture, $"rgb({c.R}, {c.G}, {c.B})");
                return new IslandMoment("", m.Label, m.Display, null, new[] { Copy(m.Value, "Copy hex"), Copy(rgb, "Copy RGB") }, sends)
                {
                    Swatch = System.Windows.Media.Color.FromRgb(c.R, c.G, c.B),
                };
            }
        }
        throw new ArgumentOutOfRangeException(nameof(m));
    }

    /// <summary>A short fact about a circled name or term; the top opens the full results, "Wikipedia" the article.</summary>
    public static IslandMoment For(QuickFacts.Fact fact, SelectionInfo selection, Action close) =>
        new(InfoGlyph, (fact.Description is { Length: > 0 } d ? Cap(d) + " · " : "") + "Wikipedia", fact.Title, fact.Summary,
            new[] { new MomentAction("Wikipedia", () => Open(fact.Page.AbsoluteUri, null), Closes: true), Copy(fact.Summary, "Copy") },
            Sends(selection, close));

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static IReadOnlyList<MomentSend> Sends(SelectionInfo s, Action close)
    {
        var list = new List<MomentSend>();
        if (CircleSend.CanNote(s)) list.Add(new MomentSend("", "Save to Notes", () => Task.FromResult<string?>(Word(CircleSend.ToNotes(s), "Saved to Notes"))));
        if (CircleSend.CanShelf(s)) list.Add(new MomentSend("", "Add to the Shelf", () => Task.FromResult<string?>(Word(CircleSend.ToShelf(s), "On the Shelf"))));
        if (CircleSend.CanPhone(s))
            list.Add(new MomentSend("", "Send to phone", async () =>
            {
                var r = await CircleSend.ToPhoneAsync(s);
                if (r.IsError) return r.Title;
                // The QR card shows once the moment has gone.
                close();
                return null;
            }));
        if (CircleSend.CanStage(s))
            list.Add(new MomentSend("", "Put on Stage", () =>
            {
                var r = CircleSend.ToStage(s);
                if (r.IsError) return Task.FromResult<string?>(r.Title);
                close();
                return Task.FromResult<string?>(null);
            }));
        return list;
    }

    static string Word(CommandResult r, string ok) => r.IsError ? r.Title : ok;

    static MomentAction Copy(string text, string label = "Copy") =>
        new(label, () => ClipboardText.TrySet(text) ? "Copied" : "Clipboard is busy");

    /// <summary>Opens a link or a tel:/mailto: address in its app; a word back when nothing could, else null (the moment closes).</summary>
    static string? Open(string target, string? missing)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            Log.Info($"Circle: opened a {target.Split(':')[0]} link");
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Circle: opening failed", ex);
            return missing ?? "Couldn't open that";
        }
    }

    /// <summary>A Shammy reminder at the circled time (9 AM on a circled day with no time).</summary>
    static string Remind(CircleMeaning m)
    {
        if (Modules.Connect.Links.Notes is not { } notes) return "Reminders are off";
        var due = m.HasTime ? m.At!.Value : m.At!.Value.Date.AddHours(9);
        if (due <= DateTime.Now) return "That time has passed";
        var r = notes.AddReminder(due, m.Title ?? "Circled: " + m.Display);
        return r.IsError ? r.Title : "Reminder set · " + due.ToString(due.Date == DateTime.Today ? "h:mm tt" : "ddd d MMM, h:mm tt", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The date as a calendar file, opened in whichever app handles them (Outlook, the Windows calendar…): that app adds
    /// it after you confirm there. Nothing is sent anywhere by Shammy.
    /// </summary>
    static string? Calendar(CircleMeaning m)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Shammy", "calendar");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"Shammy {DateTime.Now:yyyyMMdd-HHmmss}.ics");
            File.WriteAllText(path, CircleSense.CalendarFile(m, DateTime.UtcNow, Guid.NewGuid().ToString("N") + "@shammy"));
            return Open(path, "No calendar app on this PC");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Circle: writing the calendar file failed", ex);
            return "Couldn't make the calendar entry";
        }
    }

    static string? Region()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (ArgumentException) { return null; }
    }
}

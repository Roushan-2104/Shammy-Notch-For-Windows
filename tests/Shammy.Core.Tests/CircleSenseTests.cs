using System.Globalization;
using Shammy.Core.Music;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class CircleSenseTests
{
    // A Friday afternoon.
    static readonly DateTime Now = new(2026, 11, 13, 15, 0, 0);
    static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");
    static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    static CircleMeaning? Read(string text, CultureInfo? culture = null) => CircleSense.Read(text, Now, culture ?? India);

    [Theory]
    [InlineData("+91 98765 43210", "+919876543210")]
    [InlineData("Call us: +91 98765 43210", "+919876543210")]
    [InlineData("098765 43210", "09876543210")]
    [InlineData("98765-43210", "9876543210")]
    [InlineData("(415) 555-0132", "4155550132")]
    [InlineData("+1 415 555 0132", "+14155550132")]
    [InlineData("0044 20 7946 0958", "+442079460958")]
    [InlineData("1800 123 4567", "18001234567")]
    public void Reads_phone_numbers(string text, string value)
    {
        var m = Read(text);
        Assert.NotNull(m);
        Assert.Equal(CircleKind.Phone, m.Kind);
        Assert.Equal(value, m.Value);
    }

    [Theory]
    [InlineData("+9l 98765 432l0", "+919876543210")]
    [InlineData("+91 98765 4321O", "+919876543210")]
    public void Reading_mistakes_inside_numbers_are_fixed(string text, string value) => Assert.Equal(value, Read(text)?.Value);

    [Theory]
    [InlineData("Room 1O1", "Room 101")]
    [InlineData("Island l0 is lovely", "Island l0 is lovely")]
    [InlineData("I will call", "I will call")]
    [InlineData("Hello World", "Hello World")]
    [InlineData("2l of milk", "2l of milk")]
    public void Words_stay_words(string text, string fixedText) => Assert.Equal(fixedText, CircleSense.DigitFix(text));

    [Theory]
    [InlineData("3.14159265")]
    [InlineData("Order 12345")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("2026-11-21")]
    [InlineData("I called 98765 43210 and 98765 43211 yesterday about the order")]
    public void Not_a_phone_number(string text) => Assert.NotEqual(CircleKind.Phone, Read(text)?.Kind);

    [Theory]
    [InlineData("+919876543210", "IN", "919876543210")]
    [InlineData("09876543210", "IN", "919876543210")]
    [InlineData("9876543210", "IN", "919876543210")]
    [InlineData("4155550132", "US", "14155550132")]
    [InlineData("9876543210", null, null)]
    [InlineData("9876543210", "ZZ", null)]
    public void WhatsApp_needs_the_country(string value, string? region, string? expected) =>
        Assert.Equal(expected, CircleSense.WhatsAppNumber(value, region));

    [Theory]
    [InlineData("hello@shammy.app", "hello@shammy.app")]
    [InlineData("Email: priya.k+work@mail.example.co.in", "priya.k+work@mail.example.co.in")]
    [InlineData("hello @ shammy.app", "hello@shammy.app")]
    public void Reads_emails(string text, string value)
    {
        var m = Read(text);
        Assert.Equal(CircleKind.Email, m?.Kind);
        Assert.Equal(value, m!.Value);
    }

    [Theory]
    [InlineData("https://github.com/shammy/notch", "https://github.com/shammy/notch", "github.com/shammy/notch")]
    [InlineData("www.example.com", "https://www.example.com/", "example.com")]
    [InlineData("shammy.app/download.", "https://shammy.app/download", "shammy.app/download")]
    [InlineData("Docs: learn.microsoft.com/windows", "https://learn.microsoft.com/windows", "learn.microsoft.com/windows")]
    public void Reads_links(string text, string url, string display)
    {
        var m = Read(text);
        Assert.Equal(CircleKind.Link, m?.Kind);
        Assert.Equal(url, m!.Value);
        Assert.Equal(display, m.Display);
    }

    [Theory]
    [InlineData("I logged in. Then the README.md said see node.js docs for more about it")]
    [InlineData("v1.0.0")]
    [InlineData("e.g. this")]
    public void Not_a_link(string text) => Assert.NotEqual(CircleKind.Link, Read(text)?.Kind);

    [Theory]
    [InlineData("Sat 21 Nov 6:30 PM", 2026, 11, 21, 18, 30, true)]
    [InlineData("21 November 2026, 18:30", 2026, 11, 21, 18, 30, true)]
    [InlineData("Nov 21, 2026 at 6 pm", 2026, 11, 21, 18, 0, true)]
    [InlineData("2026-12-01T09:15", 2026, 12, 1, 9, 15, true)]
    [InlineData("25/12/2026", 2026, 12, 25, 0, 0, false)]
    [InlineData("03/04/2027", 2027, 4, 3, 0, 0, false)]
    [InlineData("tomorrow 10am", 2026, 11, 14, 10, 0, true)]
    [InlineData("Monday 9:30 AM", 2026, 11, 16, 9, 30, true)]
    [InlineData("6:30 PM", 2026, 11, 13, 18, 30, true)]
    [InlineData("9 AM", 2026, 11, 14, 9, 0, true)]
    [InlineData("5th Jan", 2027, 1, 5, 0, 0, false)]
    public void Reads_dates_and_times(string text, int y, int mo, int d, int h, int mi, bool hasTime)
    {
        var m = Read(text);
        Assert.Equal(CircleKind.When, m?.Kind);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), m!.At);
        Assert.Equal(hasTime, m.HasTime);
    }

    [Fact]
    public void Us_dates_are_month_first() => Assert.Equal(new DateTime(2027, 3, 4), Read("03/04/2027", Us)!.At);

    [Fact]
    public void A_date_keeps_its_title()
    {
        var m = Read("Dentist · Sat 21 Nov, 6 PM");
        Assert.Equal(CircleKind.When, m?.Kind);
        Assert.Equal("Dentist", m!.Title);
        Assert.Equal("Sat 21 Nov · 6 PM", m.Display);
    }

    [Theory]
    [InlineData("We shipped version 2:30 of the build for everyone on the team today")]
    [InlineData("Meeting moved to 21 Nov because the client asked us to wait for the new design and budget review")]
    public void A_sentence_is_not_a_date(string text) => Assert.NotEqual(CircleKind.When, Read(text)?.Kind);

    [Theory]
    [InlineData("12 × 7", "84")]
    [InlineData("18% of 2400", "432")]
    [InlineData("(1200 + 800) / 4", "500")]
    [InlineData("sqrt 144", "12")]
    public void Answers_sums(string text, string answer)
    {
        var m = Read(text);
        Assert.Equal(CircleKind.Sum, m?.Kind);
        Assert.Equal(answer, m!.Value);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("50%")]
    [InlineData("Chapter 3 - The return of 2 friends")]
    public void Not_a_sum(string text) => Assert.NotEqual(CircleKind.Sum, Read(text)?.Kind);

    [Theory]
    [InlineData("#7A3CFF", 0x7A, 0x3C, 0xFF)]
    [InlineData("#fff", 255, 255, 255)]
    [InlineData("3b5bff", 0x3B, 0x5B, 0xFF)]
    [InlineData("rgb(25, 195, 178)", 25, 195, 178)]
    [InlineData("rgba(25 195 178 / 50%)", 25, 195, 178)]
    [InlineData("hsl(0, 100%, 50%)", 255, 0, 0)]
    public void Reads_colours(string text, int r, int g, int b)
    {
        var m = Read(text);
        Assert.Equal(CircleKind.Colour, m?.Kind);
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), m!.Colour);
        Assert.StartsWith("#", m.Value);
    }

    [Fact]
    public void A_six_digit_number_is_not_a_colour() => Assert.NotEqual(CircleKind.Colour, Read("751024")?.Kind);

    [Theory]
    [InlineData("221B Baker Street, Marylebone, London NW1 6XE")]
    [InlineData("Plot 42, Sector 5, Salt Lake, Kolkata 700091")]
    [InlineData("Flat 3B, Green Tower, MG Road, Bengaluru, Karnataka 560001")]
    [InlineData("1600 Amphitheatre Pkwy, Mountain View, CA 94043")]
    public void Reads_addresses(string text) => Assert.Equal(CircleKind.Address, Read(text)?.Kind);

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    [InlineData("Shammy")]
    [InlineData("")]
    [InlineData("Best road trips in India, ranked by people who took them, with maps, costs and tips for the monsoon")]
    public void Plain_text_is_searched(string text) => Assert.Null(Read(text));

    [Fact]
    public void A_swatch_is_its_colour()
    {
        var px = Fill(40, 30, 0x3B, 0x5B, 0xFF);
        // A little anti-aliased edge doesn't change the answer.
        for (int x = 0; x < 40; x++) Set(px, 40, x, 0, 255, 255, 255);
        var m = CircleSense.Swatch(px, 40, 30);
        Assert.Equal(CircleKind.Colour, m?.Kind);
        Assert.Equal("#3B5BFF", m!.Value);
    }

    [Fact]
    public void A_picture_is_not_a_swatch()
    {
        var px = new byte[40 * 30 * 4];
        var rng = new Random(7);
        rng.NextBytes(px);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        Assert.Null(CircleSense.Swatch(px, 40, 30));
    }

    [Fact]
    public void Calendar_file_has_one_event()
    {
        var m = Read("Dentist, Fri 21 Nov 6:30 PM")!;
        var ics = CircleSense.CalendarFile(m, new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc), "abc@shammy");
        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("DTSTART:20261121T183000", ics);
        Assert.Contains("DTEND:20261121T193000", ics);
        Assert.Contains("SUMMARY:Dentist", ics);
        Assert.Contains("\r\n", ics);
    }

    [Fact]
    public void All_day_calendar_file()
    {
        var ics = CircleSense.CalendarFile(Read("25/12/2026")!, DateTime.UtcNow, "x");
        Assert.Contains("DTSTART;VALUE=DATE:20261225", ics);
        Assert.Contains("DTEND;VALUE=DATE:20261226", ics);
    }

    [Fact]
    public void A_row_split_by_ocr_reads_in_order()
    {
        // OCR gave "+91" its own line (index 4), after the number's line (3).
        var words = new[]
        {
            new OcrWord("98765", new Box(140, 100, 90, 30), 3),
            new OcrWord("43210", new Box(240, 100, 90, 30), 3),
            new OcrWord("+91", new Box(80, 101, 48, 29), 4),
        };
        Assert.Equal("+91 98765 43210", SelectionAnalyzer.JoinLines(words));
    }

    [Fact]
    public void Two_columns_stay_apart()
    {
        var words = new[]
        {
            new OcrWord("Left", new Box(0, 100, 60, 20), 0),
            new OcrWord("column", new Box(70, 100, 80, 20), 0),
            new OcrWord("Right", new Box(600, 100, 60, 20), 1),
        };
        Assert.Equal("Left column Right", SelectionAnalyzer.JoinLines(words));
        var below = new[] { new OcrWord("second", new Box(0, 140, 80, 20), 0), new OcrWord("first", new Box(0, 100, 60, 20), 1) };
        // Different rows keep OCR's order.
        Assert.Equal("second first", SelectionAnalyzer.JoinLines(below));
    }

    static byte[] Fill(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++) Set(px, w, x, y, r, g, b);
        return px;
    }

    static void Set(byte[] px, int w, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * w + x) * 4;
        px[i] = b;
        px[i + 1] = g;
        px[i + 2] = r;
        px[i + 3] = 255;
    }
}

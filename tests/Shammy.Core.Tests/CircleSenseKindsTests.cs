using System.Globalization;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

/// <summary>The kinds Circle to Search learnt in its second round: languages, zones, parcels, flights, Indian IDs, code, terms, percentages.</summary>
public class CircleSenseKindsTests
{
    // A Friday afternoon in India.
    static readonly DateTime Now = new(2026, 11, 13, 15, 0, 0);
    static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");
    static readonly TimeZoneInfo Ist = TimeZoneInfo.CreateCustomTimeZone("Test India", TimeSpan.FromMinutes(330), "India", "India");
    static readonly TimeZoneInfo London = TimeZoneInfo.CreateCustomTimeZone("Test GMT", TimeSpan.Zero, "GMT", "GMT");

    static CircleMeaning? Read(string text, DateTime? now = null, TimeZoneInfo? zone = null) => CircleSense.Read(text, now ?? Now, India, zone ?? Ist);

    static CircleMeaning ReadAs(string text, CircleKind kind)
    {
        var m = Read(text);
        Assert.True(m is not null, $"\"{text}\" was not read");
        Assert.Equal(kind, m!.Kind);
        return m;
    }

    // ---- Foreign ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("東京タワーは高さ333メートルです。", "Japanese", "ja")]
    [InlineData("今天天气很好，我们去公园吧", "Chinese", "zh")]
    [InlineData("안녕하세요 반갑습니다", "Korean", "ko")]
    [InlineData("नमस्ते, आप कैसे हैं?", "Hindi", "hi")]
    [InlineData("मी घरी आहे", "Marathi", "mr")]
    [InlineData("مرحبا كيف حالك", "Arabic", "ar")]
    [InlineData("آپ کیسے ہیں", "Urdu", "ur")]
    [InlineData("Привет, как дела?", "Russian", "ru")]
    [InlineData("Привіт, як справи? Їжак", "Ukrainian", "uk")]
    [InlineData("สวัสดีครับ ยินดีต้อนรับ", "Thai", "th")]
    [InlineData("வணக்கம் எப்படி இருக்கிறீர்கள்", "Tamil", "ta")]
    [InlineData("আমি ভালো আছি", "Bengali", "bn")]
    [InlineData("કેમ છો, મજામાં?", "Gujarati", "gu")]
    [InlineData("ਸਤ ਸ੍ਰੀ ਅਕਾਲ", "Punjabi", "pa")]
    [InlineData("నమస్కారం", "Telugu", "te")]
    [InlineData("Γεια σου κόσμε", "Greek", "el")]
    [InlineData("שלום עולם", "Hebrew", "he")]
    public void Reads_other_scripts(string text, string language, string code)
    {
        var m = ReadAs(text, CircleKind.Foreign);
        Assert.Equal(language, m.Language);
        Assert.Equal(code, m.LanguageCode);
        Assert.Equal(language + " text", m.Label);
        Assert.Equal(text, m.Value);
    }

    [Theory]
    [InlineData("¿Dónde está la estación de tren?", "Spanish", "es")]
    [InlineData("Muchas gracias por todo, nos vemos mañana", "Spanish", "es")]
    [InlineData("Je ne sais pas où est la gare", "French", "fr")]
    [InlineData("Ich habe heute keine Zeit für das Meeting", "German", "de")]
    [InlineData("Eu não sei onde fica a estação", "Portuguese", "pt")]
    [InlineData("Non lo so, dove si trova la stazione?", "Italian", "it")]
    [InlineData("Ik weet niet waar het station is", "Dutch", "nl")]
    [InlineData("Saya tidak tahu di mana stasiun itu", "Indonesian", "id")]
    [InlineData("Bu akşam ne yapıyorsun, benimle gelir misin?", "Turkish", "tr")]
    [InlineData("Kya haal hai bhai, kal milte hai?", "Hinglish", "hi")]
    [InlineData("Mujhe nahi pata yeh kaise kaam karta hai", "Hinglish", "hi")]
    [InlineData("aaj office nahi aa raha hoon", "Hinglish", "hi")]
    public void Reads_other_latin_languages(string text, string language, string code)
    {
        var m = ReadAs(text, CircleKind.Foreign);
        Assert.Equal(language, m.Language);
        Assert.Equal(code, m.LanguageCode);
    }

    [Fact]
    public void Lines_are_counted()
    {
        var m = ReadAs("こんにちは\n元気ですか", CircleKind.Foreign);
        Assert.Equal("Japanese text · 2 lines", m.Label);
        Assert.Equal("こんにちは\n元気ですか", m.Value);
        Assert.Equal("こんにちは 元気ですか", m.Display);
    }

    [Fact]
    public void A_whole_paragraph_in_japanese_is_foreign()
    {
        var para = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。何でも薄暗いじめじめした所でニャーニャー泣いていた事だけは記憶している。", 6));
        Assert.True(para.Length > 240);
        var m = ReadAs(para, CircleKind.Foreign);
        Assert.Equal("ja", m.LanguageCode);
        Assert.EndsWith("…", m.Display);
    }

    [Fact]
    public void A_long_paragraph_in_french_is_foreign()
    {
        var para = "Il était une fois une petite fille qui vivait dans un village près de la forêt. Elle avait une grand-mère très gentille " +
                   "qui lui avait donné un petit chaperon rouge. Un jour, sa mère lui dit qu'elle devait porter une galette et un petit pot de beurre à sa grand-mère.";
        Assert.True(para.Length > 240);
        Assert.Equal("fr", ReadAs(para, CircleKind.Foreign).LanguageCode);
    }

    [Theory]
    [InlineData("kaise")]
    [InlineData("Kaise")]
    [InlineData("Rahul Sharma")]
    [InlineData("Namaste")]
    [InlineData("bhai")]
    [InlineData("kya hai")]
    [InlineData("Juan de la Cruz")]
    [InlineData("Leonardo da Vinci")]
    [InlineData("Vincent van Gogh")]
    [InlineData("Namaste नमस्ते my friends, welcome to the show tonight")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    public void Not_foreign(string text) => Assert.NotEqual(CircleKind.Foreign, Read(text)?.Kind);

    // ---- Time with a zone ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("3 PM PST", "4:30 AM", "3:00 PM Pacific · your time", "tomorrow, Sat 14 Nov")]
    [InlineData("10:30 GMT", "4:00 PM", "10:30 AM GMT · your time", "today")]
    [InlineData("15:00 CET", "7:30 PM", "3:00 PM Central Europe · your time", "today")]
    [InlineData("15:00 CEST", "6:30 PM", "3:00 PM Central Europe · your time", "today")]
    [InlineData("9am ET", "7:30 PM", "9:00 AM Eastern · your time", "today")]
    [InlineData("18:00 UTC+2", "9:30 PM", "6:00 PM UTC+2 · your time", "today")]
    [InlineData("4 pm IST", "4:00 PM", "4:00 PM India · your time", "today")]
    [InlineData("11:00 PM EDT", "8:30 AM", "11:00 PM Eastern · your time", "tomorrow, Sat 14 Nov")]
    [InlineData("8:00 JST", "4:30 AM", "8:00 AM Japan · your time", "today")]
    [InlineData("10 AM SGT", "7:30 AM", "10:00 AM Singapore · your time", "today")]
    [InlineData("9:00 AEDT", "3:30 AM", "9:00 AM Sydney · your time", "today")]
    [InlineData("Starts 2 PM GMT+5:30", "2:00 PM", "2:00 PM GMT+5:30 · your time", "today")]
    [InlineData("1 PM GST", "2:30 PM", "1:00 PM Gulf · your time", "today")]
    [InlineData("Webinar at 7 PM BST", "11:30 PM", "7:00 PM UK · your time", "today")]
    public void Converts_zone_times_to_mine(string text, string display, string label, string detail)
    {
        var m = ReadAs(text, CircleKind.ZoneTime);
        Assert.Equal(display, m.Display);
        Assert.Equal(label, m.Label);
        Assert.Equal(detail, m.Detail);
        Assert.True(m.HasTime);
        Assert.NotNull(m.At);
    }

    [Fact]
    public void A_zone_time_is_a_point_in_my_time()
    {
        var m = ReadAs("3 PM PST", CircleKind.ZoneTime);
        Assert.Equal(new DateTime(2026, 11, 14, 4, 30, 0), m.At);
        Assert.Equal("2026-11-14T04:30:00", m.Value);
    }

    [Fact]
    public void Eastern_time_follows_daylight_saving()
    {
        // 10 July: New York is on EDT (UTC−4), so 9 AM there is 6:30 PM in India.
        var summer = ReadAs("9am ET", CircleKind.ZoneTime);
        Assert.Equal("7:30 PM", summer.Display);
        var july = Read("9am ET", new DateTime(2026, 7, 10, 12, 0, 0));
        Assert.Equal("6:30 PM", july!.Display);
        Assert.Equal("9:00 AM Eastern · your time", july.Label);
    }

    [Fact]
    public void A_zone_time_with_a_date_uses_that_day()
    {
        var m = Read("Sat 21 Nov 6 PM EST");
        Assert.Equal(CircleKind.ZoneTime, m?.Kind);
        Assert.Equal(new DateTime(2026, 11, 22, 4, 30, 0), m!.At);
        Assert.Equal("Sun 22 Nov", m.Detail);
    }

    [Fact]
    public void The_local_zone_is_injected()
    {
        var m = Read("3 PM PST", zone: London);
        Assert.Equal("11:00 PM", m!.Display);
        Assert.Equal("today", m.Detail);
    }

    [Theory]
    [InlineData("6:30 PM")]
    [InlineData("3 CET")]
    [InlineData("Sat 21 Nov 6:30 PM")]
    [InlineData("25 PM PST")]
    public void Not_a_zone_time(string text) => Assert.NotEqual(CircleKind.ZoneTime, Read(text)?.Kind);

    // ---- Tracking --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1Z999AA10123456784", "UPS", "https://www.ups.com/track?tracknum=1Z999AA10123456784", "1Z999AA10123456784")]
    [InlineData("1Z 999 AA1 01 2345 6784", "UPS", "https://www.ups.com/track?tracknum=1Z999AA10123456784", "1Z999AA10123456784")]
    [InlineData("FedEx 123456789012", "FedEx", "https://www.fedex.com/fedextrack/?trknbr=123456789012", "123456789012")]
    [InlineData("Tracking number: 123456789012345", "FedEx", "https://www.fedex.com/fedextrack/?trknbr=123456789012345", "123456789012345")]
    [InlineData("EE123456789IN", "India Post", "https://www.google.com/search?q=track%20EE123456789IN", "EE123456789IN")]
    [InlineData("Speed post: RR123456785CN", "China Post", "https://www.google.com/search?q=track%20RR123456785CN", "RR123456785CN")]
    [InlineData("TBA123456789012", "Amazon", "https://track.amazon.com/tracking/TBA123456789012", "TBA123456789012")]
    [InlineData("DHL 1234567890", "DHL", "https://www.dhl.com/global-en/home/tracking/tracking-express.html?submit=1&tracking-id=1234567890", "1234567890")]
    [InlineData("Delhivery tracking 1234567890123", "Delhivery", "https://www.google.com/search?q=track%201234567890123", "1234567890123")]
    public void Reads_tracking_numbers(string text, string carrier, string url, string number)
    {
        var m = ReadAs(text, CircleKind.Tracking);
        Assert.Equal(carrier, m.Carrier);
        Assert.Equal(url, m.Value);
        Assert.Equal(number, m.Display);
        Assert.Equal("Tracking · " + carrier, m.Label);
    }

    [Theory]
    [InlineData("AWB No: 7894561230123", "7894561230123")]
    [InlineData("Consignment number BD12345678", "BD12345678")]
    [InlineData("tracking id: AB1234CD5678", "AB1234CD5678")]
    public void Any_other_tracking_number_is_searched(string text, string number)
    {
        var m = ReadAs(text, CircleKind.Tracking);
        Assert.Null(m.Carrier);
        Assert.Equal("Tracking number", m.Label);
        Assert.Equal("https://www.google.com/search?q=track%20" + number, m.Value);
    }

    [Theory]
    [InlineData("123456789012")]
    [InlineData("1234567890")]
    [InlineData("Order 12345")]
    [InlineData("tracking is down today")]
    [InlineData("I tracked the parcel all day long but it never came to the house")]
    public void Not_tracking(string text) => Assert.NotEqual(CircleKind.Tracking, Read(text)?.Kind);

    // ---- Flights ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("6E 2134", "6E 2134", "IndiGo")]
    [InlineData("AI 101", "AI 101", "Air India")]
    [InlineData("UK945", "UK 945", "Vistara")]
    [InlineData("EK 507", "EK 507", "Emirates")]
    [InlineData("Flight 6E2134", "6E 2134", "IndiGo")]
    [InlineData("QR 571", "QR 571", "Qatar Airways")]
    [InlineData("Flight UK 2024", "UK 2024", "Vistara")]
    public void Reads_flights(string text, string display, string airline)
    {
        var m = ReadAs(text, CircleKind.Flight);
        Assert.Equal(display, m.Display);
        Assert.Equal("Flight · " + airline, m.Label);
        Assert.Equal(airline, m.Carrier);
        Assert.StartsWith("https://www.google.com/search?q=", m.Value);
        Assert.Contains("flight%20status", m.Value);
    }

    [Theory]
    [InlineData("AB 12")]
    [InlineData("UK 2024")]
    [InlineData("XY 123")]
    [InlineData("6E 2134 and AI 101")]
    [InlineData("ai 101")]
    public void Not_a_flight(string text) => Assert.NotEqual(CircleKind.Flight, Read(text)?.Kind);

    // ---- PNR -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("PNR 2451234567")]
    [InlineData("PNR No: 245-1234567")]
    [InlineData("pnr: 245 1234567")]
    [InlineData("PNR Status 2451234567")]
    public void Reads_pnr(string text)
    {
        var m = ReadAs(text, CircleKind.Pnr);
        Assert.Equal("2451234567", m.Value);
        Assert.Equal("245-1234567", m.Display);
        Assert.Equal("PNR · Indian Railways", m.Label);
    }

    [Fact]
    public void Ten_digits_without_pnr_are_a_phone_number() => Assert.Equal(CircleKind.Phone, Read("2451234567")?.Kind);

    // ---- UPI -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("rahul@ybl", "rahul@ybl", "PhonePe")]
    [InlineData("Pay to: shop.name@okhdfcbank", "shop.name@okhdfcbank", "Google Pay")]
    [InlineData("9876543210@paytm", "9876543210@paytm", "Paytm")]
    [InlineData("priya-k@oksbi", "priya-k@oksbi", "Google Pay")]
    [InlineData("UPI: kirana.store@upi", "kirana.store@upi", "BHIM")]
    [InlineData("rahul @ ybl", "rahul@ybl", "PhonePe")]
    [InlineData("mehta@ICICI", "mehta@icici", "ICICI Bank")]
    public void Reads_upi_ids(string text, string id, string app)
    {
        var m = ReadAs(text, CircleKind.Upi);
        Assert.Equal(id, m.Value);
        Assert.Equal("UPI ID · " + app, m.Label);
        Assert.Equal(app, m.Detail);
    }

    [Theory]
    [InlineData("rahul@gmail.com", CircleKind.Email)]
    [InlineData("hello@sbi.co.in", CircleKind.Email)]
    public void An_email_is_not_a_upi_id(string text, CircleKind kind) => Assert.Equal(kind, Read(text)?.Kind);

    [Fact]
    public void An_unknown_handle_is_not_a_upi_id() => Assert.NotEqual(CircleKind.Upi, Read("rahul@somewhere")?.Kind);

    // ---- IFSC ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("HDFC0001234", "HDFC0001234", "HDFC Bank")]
    [InlineData("IFSC: SBIN0005943", "SBIN0005943", "State Bank of India")]
    [InlineData("IFSC code UTIB0000123", "UTIB0000123", "Axis Bank")]
    [InlineData("ICIC0000104", "ICIC0000104", "ICICI Bank")]
    [InlineData("KKBK0000958", "KKBK0000958", "Kotak Mahindra Bank")]
    public void Reads_ifsc(string text, string code, string bank)
    {
        var m = ReadAs(text, CircleKind.Ifsc);
        Assert.Equal(code, m.Value);
        Assert.Equal("IFSC · " + bank, m.Label);
    }

    [Fact]
    public void An_unknown_bank_needs_the_word_ifsc()
    {
        Assert.NotEqual(CircleKind.Ifsc, Read("ABCD0123456")?.Kind);
        Assert.Equal("IFSC code", ReadAs("IFSC ABCD0123456", CircleKind.Ifsc).Label);
    }

    [Fact]
    public void An_ifsc_needs_its_zero() => Assert.NotEqual(CircleKind.Ifsc, Read("HDFC1001234")?.Kind);

    // ---- PIN codes -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("PIN 560001", "560001", "Karnataka")]
    [InlineData("Pincode: 110001", "110001", "Delhi")]
    [InlineData("PIN code 400 001", "400001", "Maharashtra")]
    [InlineData("Kolkata 700091", "700091", "West Bengal")]
    [InlineData("New Delhi - 110001", "110001", "Delhi")]
    [InlineData("Bengaluru, Karnataka 560001", "560001", "Karnataka")]
    [InlineData("Postal code: 403001", "403001", "Goa")]
    public void Reads_pin_codes(string text, string pin, string region)
    {
        var m = ReadAs(text, CircleKind.Pin);
        Assert.Equal(pin, m.Value);
        Assert.Equal("PIN code · " + region, m.Label);
        Assert.Equal(region, m.Detail);
    }

    [Theory]
    [InlineData("751024")]
    [InlineData("OTP 482913")]
    [InlineData("Order 482913")]
    [InlineData("PIN 060001")]
    public void Not_a_pin_code(string text) => Assert.NotEqual(CircleKind.Pin, Read(text)?.Kind);

    [Theory]
    [InlineData("110001", "Delhi")]
    [InlineData("122001", "Haryana")]
    [InlineData("160017", "Chandigarh")]
    [InlineData("248001", "Uttarakhand")]
    [InlineData("226001", "Uttar Pradesh")]
    [InlineData("302001", "Rajasthan")]
    [InlineData("380001", "Gujarat")]
    [InlineData("462001", "Madhya Pradesh")]
    [InlineData("500001", "Telangana")]
    [InlineData("520001", "Andhra Pradesh")]
    [InlineData("600001", "Tamil Nadu")]
    [InlineData("682001", "Kerala")]
    [InlineData("751024", "Odisha")]
    [InlineData("781001", "Assam")]
    [InlineData("800001", "Bihar")]
    [InlineData("834001", "Jharkhand")]
    [InlineData("900001", "Army Postal Service")]
    public void Pin_regions(string pin, string region) => Assert.Equal(region, CircleSense.PinRegion(pin));

    // ---- Number plates ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("MH 12 AB 1234", "MH 12 AB 1234", "Maharashtra")]
    [InlineData("DL3CAF0001", "DL 3C AF 0001", "Delhi")]
    [InlineData("KA-05-MN-2345", "KA 05 MN 2345", "Karnataka")]
    [InlineData("up 32 bq 7788", "UP 32 BQ 7788", "Uttar Pradesh")]
    [InlineData("TN 09 B 4321", "TN 09 B 4321", "Tamil Nadu")]
    [InlineData("22 BH 1234 AA", "22 BH 1234 AA", "Bharat series")]
    public void Reads_number_plates(string text, string display, string state)
    {
        var m = ReadAs(text, CircleKind.Vehicle);
        Assert.Equal(display, m.Display);
        Assert.Equal("Vehicle · " + state, m.Label);
        Assert.Equal(display.Replace(" ", ""), m.Value);
    }

    [Theory]
    [InlineData("XX 12 AB 1234")]
    [InlineData("MH 12 AB 123")]
    public void Not_a_number_plate(string text) => Assert.NotEqual(CircleKind.Vehicle, Read(text)?.Kind);

    // ---- Code ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("TypeError: Cannot read properties of undefined (reading 'map')", "TypeError", "TypeError: Cannot read properties of undefined (reading 'map')")]
    [InlineData("Uncaught ReferenceError: x is not defined", "ReferenceError", "Uncaught ReferenceError: x is not defined")]
    [InlineData("npm ERR! code ERESOLVE", "npm error", "npm ERR! code ERESOLVE")]
    [InlineData("Program.cs(12,5): error CS1002: ; expected", "Error CS1002", "Program.cs(12,5): error CS1002: ; expected")]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", "Fatal error", "fatal: not a git repository (or any of the parent directories): .git")]
    [InlineData("Segmentation fault (core dumped)", "Segmentation fault", "Segmentation fault (core dumped)")]
    [InlineData("Error: ENOENT: no such file or directory, open 'config.json'", "Error", "Error: ENOENT: no such file or directory, open 'config.json'")]
    [InlineData("java.lang.IllegalStateException: Fragment not attached", "IllegalStateException", "java.lang.IllegalStateException: Fragment not attached")]
    public void Reads_errors(string text, string display, string label)
    {
        var m = ReadAs(text, CircleKind.Code);
        Assert.Equal(display, m.Display);
        Assert.Equal(label, m.Label);
        Assert.Equal(text, m.Value);
    }

    [Fact]
    public void A_dotnet_stack_trace_is_named_by_its_exception()
    {
        var text = "System.NullReferenceException: Object reference not set to an instance of an object.\n" +
                   "   at Shammy.App.Main(String[] args) in C:\\src\\Program.cs:line 12\n" +
                   "   at Shammy.App.Run() in C:\\src\\Program.cs:line 30";
        var m = ReadAs(text, CircleKind.Code);
        Assert.Equal("NullReferenceException", m.Display);
        Assert.StartsWith("System.NullReferenceException: Object reference", m.Label);
        Assert.Equal(text, m.Value);
    }

    [Fact]
    public void A_stack_trace_read_off_the_screen_as_one_line_is_cut_at_its_frames()
    {
        var m = ReadAs("System.InvalidOperationException: Sequence contains no elements at System.Linq.Enumerable.First[TSource](IEnumerable`1 source) at Shammy.Program.Main()", CircleKind.Code);
        Assert.Equal("InvalidOperationException", m.Display);
        Assert.Equal("System.InvalidOperationException: Sequence contains no elements", m.Label);
    }

    [Fact]
    public void A_python_traceback_is_named_by_its_last_error()
    {
        var text = "Traceback (most recent call last):\n  File \"main.py\", line 3, in <module>\n    n = int(\"abc\")\nValueError: invalid literal for int() with base 10: 'abc'";
        var m = ReadAs(text, CircleKind.Code);
        Assert.Equal("ValueError", m.Display);
        Assert.Equal("ValueError: invalid literal for int() with base 10: 'abc'", m.Label);
    }

    [Theory]
    [InlineData("function add(a, b) {\n  return a + b;\n}", "function add(a, b) {")]
    [InlineData("for (int i = 0; i < 10; i++) { sum += i; }", "for (int i = 0; i < 10; i++) { sum += i; }")]
    [InlineData("def greet(name):\n    print(f\"Hello {name}\")", "def greet(name):")]
    [InlineData("const total = items.reduce((a, b) => a + b, 0);", "const total = items.reduce((a, b) => a + b, 0);")]
    [InlineData("#include <stdio.h>\nint main() { printf(\"hi\"); return 0; }", "#include <stdio.h>")]
    public void Reads_code(string text, string label)
    {
        var m = ReadAs(text, CircleKind.Code);
        Assert.Equal("Code", m.Display);
        Assert.Equal(label, m.Label);
        Assert.Equal(text, m.Value);
    }

    [Fact]
    public void Long_code_is_still_code()
    {
        var code = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"    var value{i} = Compute({i});"));
        Assert.True(code.Length > 240);
        Assert.Equal(CircleKind.Code, Read(code)?.Kind);
    }

    [Theory]
    [InlineData("With one exception, everyone came to the party")]
    [InlineData("Error handling is an important part of writing good software")]
    [InlineData("The function of the heart is to pump blood; it never rests.")]
    [InlineData("Tom & Jerry")]
    public void Not_code(string text) => Assert.NotEqual(CircleKind.Code, Read(text)?.Kind);

    // ---- Percentages -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("20% of 4,500", "900", "900", "20% of 4,500")]
    [InlineData("15% off ₹2,000", "1,700", "₹1,700", "15% off ₹2,000")]
    [InlineData("₹1,200 + 18% GST", "1,416", "₹1,416", "₹1,200 + 18% GST")]
    [InlineData("what is 12.5% of 80", "10", "10", "12.5% of 80")]
    [InlineData("₹1,00,000 + 18% GST", "118,000", "₹1,18,000", "₹1,00,000 + 18% GST")]
    [InlineData("$80 + 15% tip", "92", "$92", "$80 + 15% tip")]
    [InlineData("Rs. 999 - 10%", "899.1", "₹899.10", "Rs. 999 - 10%")]
    [InlineData("25% off 1299 rupees", "974.25", "₹974.25", "25% off 1299 rupees")]
    public void Works_out_percentages(string text, string value, string display, string label)
    {
        var m = ReadAs(text, CircleKind.Sum);
        Assert.Equal(value, m.Value);
        Assert.Equal(display, m.Display);
        Assert.Equal(label, m.Label);
    }

    // ---- Terms -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("photosynthesis")]
    [InlineData("Ada Lovelace")]
    [InlineData("quantitative easing")]
    [InlineData("Photosynthesis")]
    [InlineData("theory of relativity")]
    [InlineData("karma")]
    [InlineData("Schrödinger's cat")]
    [InlineData("self-driving car")]
    public void Reads_terms(string text)
    {
        var m = ReadAs(text, CircleKind.Term);
        Assert.Equal("Word or term", m.Label);
        Assert.Equal(text, m.Value);
        Assert.Equal(text, m.Display);
    }

    [Theory]
    [InlineData("Shammy")]
    [InlineData("I will call")]
    [InlineData("SALE")]
    [InlineData("of the")]
    [InlineData("Hello, how are you?")]
    [InlineData("go")]
    [InlineData("this is it")]
    [InlineData("one two three four five")]
    [InlineData("v2 build")]
    [InlineData("Thank you")]
    [InlineData("see you there")]
    public void Not_a_term(string text) => Assert.NotEqual(CircleKind.Term, Read(text)?.Kind);

    // ---- Ordinary text is still searched -----------------------------------------------------------------------------

    [Theory]
    [InlineData("We will be closed on Monday for maintenance, sorry for any trouble.")]
    [InlineData("Click here to read more about our new features and pricing plans")]
    [InlineData("I think this is a great idea and we should try it next week")]
    [InlineData("Error handling is an important part of writing good software")]
    [InlineData("With one exception, everyone came to the party")]
    [InlineData("Best road trips in India, ranked by people who took them, with maps, costs and tips for the monsoon")]
    [InlineData("Our flight AI 101 was delayed by three hours because of the fog in Delhi this morning")]
    [InlineData("Please share your PIN with nobody, not even someone who says they are from the bank")]
    public void Sentences_are_searched(string text) => Assert.Null(Read(text));

    [Fact]
    public void A_long_english_paragraph_is_searched()
    {
        var para = "Circle to Search lets you hold a key and draw around anything on the screen. The words inside are read on this PC, " +
                   "and when they are a phone number, an email, a link or a date the island offers what you would do next. Anything else " +
                   "goes to your search engine, the way it always has, so nothing changes for an ordinary paragraph like this one.";
        Assert.True(para.Length > 240);
        Assert.Null(Read(para));
    }

    [Fact]
    public void Never_throws_on_odd_input()
    {
        foreach (var s in new[] { "((((((((((", "%%%%", "@", "1Z", "₹", "PNR", "IFSC", "\u0000\u0001", new string('a', 20000), "🙂🙂🙂", "3 PM PST 4 PM IST" })
            _ = Read(s);
    }

    [Fact]
    public void Existing_kinds_keep_their_numbers()
    {
        Assert.Equal(0, (int)CircleKind.Phone);
        Assert.Equal(6, (int)CircleKind.Colour);
    }
}

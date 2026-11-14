using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class QueryBuilderTests
{
    [Theory]
    [InlineData("best mechanical keyboard only from reddit", "best mechanical keyboard", SearchFilter.Reddit)]
    [InlineData("best mechanical keyboard reddit only", "best mechanical keyboard", SearchFilter.Reddit)]
    [InlineData("nvidia news from the past week", "nvidia news", SearchFilter.Recent)]
    [InlineData("air fryer recipes no junk", "air fryer recipes", SearchFilter.NoJunk)]
    [InlineData("how to read a file in rust from the docs", "how to read a file in rust", SearchFilter.Docs)]
    [InlineData("what is the latest version of node", "what is the latest version of node", SearchFilter.None)]
    public void Extracts_spoken_filters(string spoken, string query, SearchFilter filters)
    {
        var (q, f) = QueryBuilder.ExtractSpokenFilters(spoken);
        Assert.Equal(query, q);
        Assert.Equal(filters, f);
    }

    [Fact]
    public void Combines_multiple_spoken_filters()
    {
        var (q, f) = QueryBuilder.ExtractSpokenFilters("laptop deals on reddit from the last week");
        Assert.Equal("laptop deals", q);
        Assert.Equal(SearchFilter.Reddit | SearchFilter.Recent, f);
    }

    [Fact]
    public void Web_mode_uses_operators()
    {
        var q = QueryBuilder.Apply("gpu prices", SearchFilter.Reddit | SearchFilter.Recent, SearchMode.Web, new DateTime(2026, 11, 2));
        Assert.Equal("gpu prices site:reddit.com after:2026-10-26", q);
    }

    [Fact]
    public void Ai_mode_uses_plain_language()
    {
        var q = QueryBuilder.Apply("gpu prices", SearchFilter.Reddit, SearchMode.Ai, DateTime.Today);
        Assert.Equal("gpu prices. Use Reddit discussions as sources.", q);
    }

    [Fact]
    public void Urls_target_ai_mode_and_web_tab()
    {
        Assert.Contains("udm=50", QueryBuilder.BuildUrl("a b", SearchMode.Ai).ToString());
        Assert.Contains("udm=14", QueryBuilder.BuildUrl("a b", SearchMode.Web).ToString());
        Assert.Contains("q=a%20b", QueryBuilder.BuildUrl("a b", SearchMode.Web).AbsoluteUri);
    }

    [Fact]
    public void Combine_quotes_selected_text()
    {
        Assert.Equal("explain this: \"NullReferenceException at line 4\"",
            QueryBuilder.Combine("explain this", "NullReferenceException at line 4"));
        Assert.Equal("just text", QueryBuilder.Combine("", "just text"));
        Assert.Equal(10, QueryBuilder.Combine("", new string('a', 50), 10).Length);
    }
}

public class CalculatorTests
{
    [Theory]
    [InlineData("2+2", 4)]
    [InlineData("what is 18% of 2400", 432)]
    [InlineData("12 x 12", 144)]
    [InlineData("(3 + 4) * 2", 14)]
    [InlineData("2^10", 1024)]
    [InlineData("2^3^2", 512)]
    [InlineData("-5 + 3", -2)]
    [InlineData("sqrt 144 + 1", 13)]
    [InlineData("1,500 / 3", 500)]
    [InlineData("1,20,000 + 35,000", 155000)]
    [InlineData("calculate 7 times 6", 42)]
    [InlineData("2(3+4)", 14)]
    public void Evaluates(string input, double expected)
    {
        Assert.True(Calculator.TryEvaluate(input, out var r, out _));
        Assert.Equal(expected, r, 6);
    }

    [Theory]
    [InlineData("what is the capital of france")]
    [InlineData("iphone 15 price")]
    [InlineData("covid-19 cases")]
    [InlineData("2024-25 budget")]
    [InlineData("1/2 cup in grams")]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("1/0")]
    public void Rejects_non_math(string input)
    {
        Assert.False(Calculator.TryEvaluate(input, out _, out _));
    }

    [Fact]
    public void Display_form_keeps_the_users_wording() =>
        Assert.Equal("18% of 2400", Calculator.DisplayForm("What is 18% of 2400?"));

    [Theory]
    [InlineData(1234567, "1,234,567")]
    [InlineData(0.5, "0.5")]
    [InlineData(1.0 / 3, "0.3333333333")]
    public void Formats(double value, string expected) => Assert.Equal(expected, Calculator.Format(value));

    [Theory]
    [InlineData("-2^2", -4)]
    [InlineData("2^-1", 0.5)]
    [InlineData("-(2)^2", -4)]
    [InlineData("(-2)^2", 4)]
    [InlineData("10-3", 7)]
    public void Unary_minus_binds_looser_than_power(string text, double expected)
    {
        Assert.True(Calculator.TryEvaluate(text, out var v, out _));
        Assert.Equal(expected, v, 10);
    }

    [Theory]
    [InlineData("2025-11-03")]
    [InlineData("2024-25")]
    [InlineData("555-1234")]
    [InlineData("800-555-0199")]
    public void Dates_and_phone_numbers_are_searched_not_calculated(string text) =>
        Assert.False(Calculator.TryEvaluate(text, out _, out _));

    [Fact]
    public void Negative_zero_shows_as_zero() => Assert.Equal("0", Calculator.Format(-0.0));

    [Fact]
    public void Deeply_nested_input_is_refused_instead_of_overflowing_the_stack()
    {
        var hostile = new string('(', 3000) + "1+1" + new string(')', 3000);
        Assert.False(Calculator.TryEvaluate(hostile, out _, out _));
        Assert.True(Calculator.TryEvaluate("((((1+1))))*2", out var v, out _));
        Assert.Equal(4, v);
    }
}

public class SelectionAnalyzerTests
{
    static readonly OcrWord[] Words =
    {
        new("Hello", new Box(100, 100, 50, 20), 0),
        new("world", new Box(160, 100, 50, 20), 0),
        new("second", new Box(100, 130, 60, 20), 1),
        new("line", new Box(170, 130, 40, 20), 1),
        new("far", new Box(800, 800, 30, 20), 2),
    };

    static List<Pt> Circle(double cx, double cy, double rx, double ry, int n = 40) =>
        Enumerable.Range(0, n + 1)
            .Select(i => new Pt(cx + rx * Math.Cos(2 * Math.PI * i / n), cy + ry * Math.Sin(2 * Math.PI * i / n)))
            .ToList();

    [Fact]
    public void Circling_text_selects_words_inside_in_reading_order()
    {
        var sel = SelectionAnalyzer.Analyze(Words, Circle(155, 125, 90, 45));
        Assert.Equal(SelectionKind.Text, sel.Kind);
        Assert.Equal("Hello world second line", sel.Text);
    }

    [Fact]
    public void Circling_an_empty_area_is_an_image()
    {
        var sel = SelectionAnalyzer.Analyze(Words, Circle(500, 500, 120, 120));
        Assert.Equal(SelectionKind.Image, sel.Kind);
        Assert.Equal("", sel.Text);
    }

    [Fact]
    public void Tapping_a_word_selects_its_line()
    {
        var sel = SelectionAnalyzer.Analyze(Words, new[] { new Pt(180, 140) });
        Assert.Equal(SelectionKind.Text, sel.Kind);
        Assert.Equal("second line", sel.Text);
    }

    [Fact]
    public void Tapping_empty_space_selects_an_image_region()
    {
        var sel = SelectionAnalyzer.Analyze(Words, new[] { new Pt(500, 500) });
        Assert.Equal(SelectionKind.Image, sel.Kind);
        Assert.True(sel.Region.Contains(new Pt(500, 500)));
    }

    [Fact]
    public void Open_stroke_highlights_words_it_passes()
    {
        var stroke = new[] { new Pt(95, 110), new Pt(150, 111), new Pt(215, 110) };
        var sel = SelectionAnalyzer.Analyze(Words, stroke);
        Assert.Equal(SelectionKind.Text, sel.Kind);
        Assert.Equal("Hello world", sel.Text);
    }

    [Fact]
    public void Box_clamps_to_screen()
    {
        var b = new Box(-10, -10, 100, 100).Clamp(50, 50);
        Assert.Equal(new Box(0, 0, 50, 50), b);
    }
}

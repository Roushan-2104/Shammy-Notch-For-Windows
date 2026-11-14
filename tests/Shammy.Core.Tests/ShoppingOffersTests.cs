using Shammy.Core.Shopping;

namespace Shammy.Core.Tests;

public class ShoppingOffersTests
{
    // Shaped like the visible text of a Google Shopping results page (titles, prices, stores, delivery, ratings).
    const string Page = """
        Search Results
        Popular options
        Buy Airdopes 141 with 42-Hour Battery & Fast Charging
        ₹1,199
        73% off₹4,490
        Brand Store
         & more
        Free delivery
        4.8(708)
        Airdopes 141
        ₹779
        quickmart.com
         & more
        Free delivery within 1h
        4.4(1.1K)
        LOW PRICE
        Airdopes 141 ANC TWS Earbuds
        ₹749
        Usually ₹1,099
        Gadget Shop
        Free delivery
        4.8(39)
        Airdopes 141 Case Cover Silicone
        ₹199
        Covers Hub
        ₹40 delivery
        Airdopes 141 True Wireless Earbuds, White
        ₹1,399
        Electronics Mart
        Free delivery by Fri
        """;

    [Fact]
    public void Reads_each_offer_with_store_delivery_and_rating()
    {
        var offers = ShoppingOffers.Parse(Page);
        Assert.Equal(5, offers.Count);
        var first = offers[0];
        Assert.Equal(1199m, first.Price);
        Assert.Equal("Brand Store", first.Store);
        Assert.Equal("Free delivery", first.Delivery);
        Assert.Equal(4.8, first.Rating);
        Assert.Equal("708", first.Reviews);
        var cheap = offers.Single(o => o.Price == 749m);
        Assert.Equal("Gadget Shop", cheap.Store);
        var last = offers[^1];
        Assert.Equal("Free delivery by Fri", last.Delivery);
        Assert.Null(last.Rating);
        Assert.Equal("₹40 delivery", offers.Single(o => o.Store == "Covers Hub").Delivery);
    }

    [Fact]
    public void Best_keeps_the_product_itself_cheapest_first_one_per_store()
    {
        var best = ShoppingOffers.Best(ShoppingOffers.Parse(Page), "Airdopes 141 earbuds");
        // The silicone case is an accessory, not the earbuds: left out. One per store, cheapest first, three at most.
        Assert.Equal(new[] { 749m, 779m, 1199m }, best.Select(o => o.Price));
        var cases = ShoppingOffers.Best(ShoppingOffers.Parse(Page), "Airdopes 141 case");
        Assert.Equal(199m, cases[0].Price);
    }

    [Theory]
    [InlineData(749, "₹749")]
    [InlineData(1199, "₹1,199")]
    [InlineData(118000, "₹1,18,000")]
    [InlineData(1234567, "₹12,34,567")]
    public void Rupees_use_indian_grouping(int amount, string expected) => Assert.Equal(expected, ShoppingOffers.Rupees(amount));

    [Fact]
    public void Nothing_to_read_is_no_offers()
    {
        Assert.Empty(ShoppingOffers.Parse(null));
        Assert.Empty(ShoppingOffers.Parse("No results for that\nTry another search"));
    }
}

public class PriceIntentTests
{
    [Theory]
    [InlineData("price")]
    [InlineData("what's the price")]
    [InlineData("kitne ka hai")]
    [InlineData("where to buy this")]
    [InlineData("cheapest")]
    [InlineData("iska daam kya hai")]
    [InlineData("how much does it cost")]
    [InlineData("ye sasta hai ya mehenga")]
    public void Asking_the_price_is_the_shopping_list(string said) =>
        Assert.Equal(Shammy.Core.Search.CircleAction.Price, Shammy.Core.Search.CircleIntent.Parse(said)!.Action);

    [Theory]
    [InlineData("in rupees")]
    [InlineData("translate")]
    [InlineData("explain")]
    public void Other_words_are_not_the_price(string said) =>
        Assert.NotEqual(Shammy.Core.Search.CircleAction.Price, Shammy.Core.Search.CircleIntent.Parse(said)!.Action);
}

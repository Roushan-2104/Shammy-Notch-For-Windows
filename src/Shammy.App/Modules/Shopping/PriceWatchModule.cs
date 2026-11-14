using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Shopping;
using Shammy.Infrastructure;

namespace Shammy.Modules.Shopping;

/// <summary>A product you asked Shammy to watch: what it is, the cheapest it was then, and where.</summary>
internal sealed class WatchedPrice
{
    public string Product { get; set; } = "";
    public decimal Price { get; set; }
    public string Store { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public DateTime LastCheckUtc { get; set; }
}

/// <summary>
/// "Watch price" from Circle to buy smarter: up to ten products, checked against Google Shopping about once a day (only
/// while Shammy runs, one product at a time, out of sight), and a pop-up in the island when one gets cheaper. The list
/// is encrypted on this PC (DPAPI); only the product's name goes to Google, when it's checked.
/// </summary>
internal sealed class PriceWatchModule : IShammyModule
{
    public const int MaxWatched = 10;
    static readonly TimeSpan Every = TimeSpan.FromHours(24);
    static readonly byte[] Entropy = "Shammy price watch v1"u8.ToArray();
    static string FilePath => Path.Combine(Paths.Root, "price-watch.dat");

    ModuleContext _ctx = null!;
    List<WatchedPrice> _items = new();
    DispatcherTimer? _timer;
    bool _checking;
    string _last = "not checked";

    public string Name => "pricewatch";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _items = Load();
        HostLinks.WatchPrice = Watch;
        // A look every hour costs nothing; a product is only fetched once its day is up.
        _timer = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => _ = CheckDueAsync(force: false), ctx.Ui);
        _timer.Start();
        ctx.Ui.BeginInvoke(() => _ = CheckDueAsync(force: false), DispatcherPriority.ApplicationIdle);
        ctx.AddPipeCommand("pricewatch-state", _ => State());
        ctx.AddPipeCommand("pricewatch-add", arg =>
        {
            var p = arg.Split('|');
            if (p.Length < 3 || !decimal.TryParse(p[1], out var price)) return "error: usage pricewatch-add <product>|<price>|<store>";
            return Watch(p[0].Trim(), price, p[2].Trim());
        });
        ctx.AddPipeCommand("pricewatch-check-now", arg =>
        {
            _ = CheckDueAsync(force: true);
            return "started";
        });
        ctx.AddPipeCommand("pricewatch-clear", arg =>
        {
            _items.Clear();
            Save();
            return "ok";
        });
    }

    public void Dispose()
    {
        _timer?.Stop();
        HostLinks.WatchPrice = null;
    }

    /// <summary>Starts watching a product (or updates the one already watched); the word back for the island.</summary>
    string Watch(string product, decimal price, string store)
    {
        if (product.Length == 0) return "Nothing to watch";
        var existing = _items.FirstOrDefault(i => string.Equals(i.Product, product, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            if (_items.Count >= MaxWatched) return $"Already watching {MaxWatched}";
            _items.Add(new WatchedPrice { Product = product, Price = price, Store = store, AddedUtc = DateTime.UtcNow, LastCheckUtc = DateTime.UtcNow });
        }
        else
        {
            existing.Price = Math.Min(existing.Price, price);
            existing.Store = store;
        }
        Save();
        return "Watching · tells you when it drops";
    }

    async Task CheckDueAsync(bool force)
    {
        if (_checking || _items.Count == 0) return;
        _checking = true;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var item in _items.Where(i => force || now - i.LastCheckUtc >= Every).ToList())
            {
                // Never while presenting or on a call: it waits for the next hourly look.
                if (PrivacyState.Presenting) break;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var text = await HiddenPageReader(item.Product, cts.Token);
                item.LastCheckUtc = DateTime.UtcNow;
                var best = ShoppingOffers.Best(ShoppingOffers.Parse(text), item.Product, 1).FirstOrDefault();
                if (best is null)
                {
                    _last = $"no offers for a watched product ({DateTime.Now:HH:mm})";
                    continue;
                }
                if (best.Price < item.Price)
                {
                    var was = item.Price;
                    item.Price = best.Price;
                    item.Store = best.Store;
                    _ctx.Post(new Activity("pricewatch", ActivityKind.Message, 50,
                        new MessagePayload("", $"Cheaper now: {Short(item.Product)} · {best.PriceText} at {best.Store} (was {ShoppingOffers.Rupees(was)})"),
                        DateTime.UtcNow + TimeSpan.FromSeconds(8)));
                    _last = $"dropped to {best.PriceText} ({DateTime.Now:HH:mm})";
                }
                else _last = $"no drop: {best.PriceText} ({DateTime.Now:HH:mm})";
            }
            Save();
        }
        catch (Exception ex)
        {
            Log.Warn("Checking watched prices failed", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    Task<string?> HiddenPageReader(string product, CancellationToken ct) =>
        Search.HiddenPage.ReadShoppingAsync(new WindowInteropHelper(_ctx.Notch).Handle, product, TimeSpan.FromSeconds(20), ct);

    static string Short(string text) => text.Length <= 40 ? text : text[..39].TrimEnd() + "…";

    string State() =>
        $"watched={_items.Count} checking={_checking} last={_last} items=[" +
        string.Join("; ", _items.Select(i => $"{i.Product} {ShoppingOffers.Rupees(i.Price)} at {i.Store}")) + "]";

    static List<WatchedPrice> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<WatchedPrice>>(json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Reading the watched prices failed", ex);
            return new();
        }
    }

    void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_items), Entropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            Log.Warn("Saving the watched prices failed", ex);
        }
    }
}

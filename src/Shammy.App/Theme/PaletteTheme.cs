using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.Notch;
using Rgb = Shammy.Core.Music.Rgb;

namespace Shammy.Theme;

/// <summary>
/// The active palette (Settings › Notch › Palette): three colours for the island's waves, the first also the app's
/// accent (see <see cref="AccentTheme"/>). Album, the default, follows the song playing: its three dominant colours,
/// worked out once per cover on a tiny copy; with nothing playing it shows Monsoon's.
/// </summary>
internal static class PaletteTheme
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, Rgb[]> Covers = new();

    static string _chosen = IslandPalettes.Album;
    static Rgb[]? _album;
    static Rgb[] _colours = IslandPalettes.MonsoonPalette.Colours.ToArray();

    /// <summary>The colours changed (another palette, a new song under Album).</summary>
    public static event Action? Changed;

    public static string Chosen => _chosen;

    /// <summary>The three colours in use now.</summary>
    public static IReadOnlyList<Rgb> Colours => _colours;

    public static Rgb First => _colours[0];

    /// <summary>The album colours are showing (Album chosen and a song with a cover is playing).</summary>
    public static bool FromAlbum => _chosen == IslandPalettes.Album && _album is not null;

    public static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    public static void Choose(string? id)
    {
        var next = IslandPalettes.Normalize(id);
        if (next == _chosen) return;
        _chosen = next;
        Update();
    }

    /// <summary>The song changed or started/stopped: Album follows its cover while it plays.</summary>
    public static void SetMusic(ImageSource? art, bool playing)
    {
        var album = playing && art is not null ? CoverColours(art) : null;
        if (ReferenceEquals(album, _album)) return;
        _album = album;
        if (_chosen == IslandPalettes.Album) Update();
    }

    static void Update()
    {
        var next = _chosen == IslandPalettes.Album && _album is { } album ? album : IslandPalettes.Find(_chosen).Colours.ToArray();
        if (next.SequenceEqual(_colours)) return;
        _colours = next;
        AccentTheme.Reapply();
        Changed?.Invoke();
    }

    /// <summary>The cover's three colours, once per cover; null for an image that can't be read.</summary>
    static Rgb[]? CoverColours(ImageSource art)
    {
        if (Covers.TryGetValue(art, out var cached)) return cached;
        if (art is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0) return null;
        Rgb[]? colours = null;
        try
        {
            const int size = 24;
            var small = new TransformedBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0),
                new ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
            var px = new byte[small.PixelWidth * small.PixelHeight * 4];
            small.CopyPixels(px, small.PixelWidth * 4, 0);
            colours = AlbumColours.Extract(px);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // An odd image format: Album shows Monsoon's colours for this song.
        }
        if (colours is not null) Covers.AddOrUpdate(art, colours);
        return colours;
    }

    public static string Describe() =>
        $"palette={_chosen} album={(FromAlbum ? "yes" : "no")} colours=[{string.Join(",", _colours.Select(c => c.Hex))}] accent={AccentTheme.Brush.Color}";
}

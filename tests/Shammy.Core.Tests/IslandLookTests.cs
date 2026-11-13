using Shammy.Core.Music;
using Shammy.Core.Notch;
using Shammy.Core.Settings;

namespace Shammy.Core.Tests;

public class IslandPaletteTests
{
    static byte[] Image(params (Rgb Colour, int Count)[] parts)
    {
        var px = new List<byte>();
        foreach (var (c, n) in parts)
            for (int i = 0; i < n; i++) px.AddRange(new[] { c.B, c.G, c.R, (byte)255 });
        return px.ToArray();
    }

    [Fact]
    public void Album_is_the_default_and_comes_first()
    {
        Assert.Equal(IslandPalettes.Album, IslandPalettes.All[0].Id);
        Assert.Equal(IslandPalettes.Album, IslandPalettes.Normalize(null));
        Assert.Equal(IslandPalettes.Album, IslandPalettes.Normalize("something old"));
    }

    [Fact]
    public void The_built_in_palettes_have_the_approved_colours()
    {
        Assert.Equal(new[] { "#3B5BFF", "#7A3CFF", "#19C3B2" }, IslandPalettes.Find("monsoon").Colours.Select(c => c.Hex));
        Assert.Equal(new[] { "#FF3D7F", "#7B2CFF", "#2D7BFF" }, IslandPalettes.Find("Neon dusk").Colours.Select(c => c.Hex));
        Assert.Equal(new[] { "#1BD68C", "#0E8BA8", "#7CF2D2" }, IslandPalettes.Find("NORTHERN").Colours.Select(c => c.Hex));
        Assert.Equal(new[] { "#F2B6FF", "#9FD8FF", "#FFE3B0" }, IslandPalettes.Find("holo").Colours.Select(c => c.Hex));
        Assert.Equal(new[] { "#D8DCE6", "#6E7486", "#AEB4C4" }, IslandPalettes.Find("frost").Colours.Select(c => c.Hex));
        Assert.Equal(6, IslandPalettes.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Album_falls_back_to_monsoons_colours()
    {
        Assert.Equal(IslandPalettes.MonsoonPalette.Colours, IslandPalettes.Find(IslandPalettes.Album).Colours);
    }

    [Theory]
    [InlineData(AccentColor.Orange, IslandPalettes.Album)]
    [InlineData(AccentColor.Mint, IslandPalettes.Northern)]
    [InlineData(AccentColor.Pink, IslandPalettes.NeonDusk)]
    public void An_old_accent_moves_to_the_closest_palette(AccentColor accent, string palette) =>
        Assert.Equal(palette, IslandPalettes.FromAccent(accent));

    [Fact]
    public void Every_old_accent_lands_on_a_real_palette_and_a_saved_palette_wins()
    {
        foreach (var a in Enum.GetValues<AccentColor>()) Assert.Contains(IslandPalettes.All, p => p.Id == IslandPalettes.FromAccent(a));
        Assert.Equal(IslandPalettes.Frost, IslandPalettes.Migrate("frost", AccentColor.Mint));
        Assert.Equal(IslandPalettes.Northern, IslandPalettes.Migrate(null, AccentColor.Mint));
    }

    [Fact]
    public void Contrast_matches_wcag()
    {
        Assert.Equal(21, Colour.Contrast(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 1);
        Assert.Equal(1, Colour.Contrast(new Rgb(10, 20, 30), new Rgb(10, 20, 30)), 3);
    }

    [Fact]
    public void Every_palettes_accent_reads_on_the_island_and_under_dark_text()
    {
        foreach (var p in IslandPalettes.All)
        {
            var (accent, hover) = IslandPalettes.Accent(p.A);
            Assert.True(Colour.Contrast(accent, IslandPalettes.Island) >= IslandPalettes.AccentContrast - 0.01, p.Id);
            Assert.True(Colour.Contrast(accent, IslandPalettes.OnAccent) >= IslandPalettes.AccentContrast - 0.01, p.Id);
            Assert.True(Colour.Luminance(hover) >= Colour.Luminance(accent), p.Id);
        }
    }

    [Fact]
    public void A_light_enough_colour_is_left_alone()
    {
        var holo = Colour.Parse("#F2B6FF");
        Assert.Equal(holo, IslandPalettes.Accent(holo).Accent);
    }

    [Fact]
    public void A_dark_colour_is_lifted_only_as_far_as_needed()
    {
        var navy = new Rgb(0x10, 0x18, 0x60);
        var lifted = Colour.AtLeast(navy, IslandPalettes.Island, 4.5);
        Assert.InRange(Colour.Contrast(lifted, IslandPalettes.Island), 4.5, 4.9);
        // Still blue, not white.
        Assert.True(lifted.B > lifted.R && lifted.B > lifted.G);
    }

    [Fact]
    public void Album_colours_come_from_the_cover_most_dominant_first()
    {
        var red = new Rgb(220, 30, 40);
        var blue = new Rgb(30, 60, 220);
        var yellow = new Rgb(240, 210, 30);
        var px = Image((red, 300), (blue, 180), (yellow, 96));
        var colours = AlbumColours.Extract(px)!;
        Assert.Equal(3, colours.Length);
        Assert.True(colours[0].R > 180 && colours[0].G < 80, colours[0].Hex);
        Assert.True(colours[1].B > 180, colours[1].Hex);
        Assert.True(colours[2].R > 180 && colours[2].G > 160, colours[2].Hex);
    }

    [Fact]
    public void A_vivid_accent_beats_a_bigger_grey_background()
    {
        var grey = new Rgb(128, 128, 128);
        var green = new Rgb(30, 200, 90);
        var colours = AlbumColours.Extract(Image((grey, 400), (green, 176)))!;
        Assert.True(colours[0].G > 150 && colours[0].R < 120, colours[0].Hex);
    }

    [Fact]
    public void A_one_colour_cover_still_gives_three_distinct_colours_that_read_on_the_island()
    {
        var colours = AlbumColours.Extract(Image((new Rgb(20, 10, 60), 576)))!;
        Assert.Equal(3, colours.Distinct().Count());
        foreach (var c in colours) Assert.True(Colour.Contrast(c, IslandPalettes.Island) >= IslandPalettes.AlbumContrast - 0.01, c.Hex);
    }

    [Fact]
    public void An_empty_or_transparent_cover_gives_nothing()
    {
        Assert.Null(AlbumColours.Extract(Array.Empty<byte>()));
        Assert.Null(AlbumColours.Extract(new byte[] { 255, 255, 255, 0, 1, 2, 3, 10 }));
    }
}

public class IslandLookTests
{
    static IslandConditions All(bool busy = true) => new(TransparencyOn: true, BatterySaver: false, HighContrast: false, MotionLively: true, Busy: busy);

    [Fact]
    public void Glass_waves_and_breathing_when_everything_allows()
    {
        Assert.Equal(new IslandLookPlan(true, true, true, true), IslandLook.Decide(All()));
    }

    [Fact]
    public void Idle_waves_hold_still()
    {
        var plan = IslandLook.Decide(All(busy: false));
        Assert.True(plan.Waves);
        Assert.False(plan.AnimateWaves);
    }

    [Fact]
    public void Transparency_off_gives_a_plain_tinted_island()
    {
        var plan = IslandLook.Decide(All() with { TransparencyOn = false });
        Assert.False(plan.Glass);
        Assert.True(plan.AnimateWaves);
    }

    [Fact]
    public void Battery_saver_drops_the_glass_and_stops_all_motion()
    {
        var plan = IslandLook.Decide(All() with { BatterySaver = true });
        Assert.False(plan.Glass);
        Assert.True(plan.Waves);
        Assert.False(plan.AnimateWaves);
        Assert.False(plan.Breathe);
    }

    [Fact]
    public void High_contrast_draws_no_glass_and_no_waves()
    {
        var plan = IslandLook.Decide(All() with { HighContrast = true });
        Assert.False(plan.Glass);
        Assert.False(plan.Waves);
        Assert.False(plan.AnimateWaves);
    }

    [Fact]
    public void Calm_motion_keeps_waves_and_rim_still()
    {
        var plan = IslandLook.Decide(All() with { MotionLively = false });
        Assert.True(plan.Glass);
        Assert.True(plan.Waves);
        Assert.False(plan.AnimateWaves);
        Assert.False(plan.Breathe);
    }

    [Fact]
    public void The_most_urgent_rim_state_wins()
    {
        Assert.Equal(RimState.MicLive, RimLight.Pick(music: true, focus: true, micLive: true, meetingSoon: true));
        Assert.Equal(RimState.MeetingSoon, RimLight.Pick(true, true, false, true));
        Assert.Equal(RimState.Focus, RimLight.Pick(true, true, false, false));
        Assert.Equal(RimState.Music, RimLight.Pick(true, false, false, false));
        Assert.Equal(RimState.None, RimLight.Pick(false, false, false, false));
        Assert.Null(RimLight.Colour(RimState.None, new Rgb(1, 2, 3)));
        Assert.Equal(new Rgb(1, 2, 3), RimLight.Colour(RimState.Music, new Rgb(1, 2, 3)));
    }

    [Fact]
    public void The_rim_breathes_softly_and_never_goes_out_or_full()
    {
        var levels = Enumerable.Range(0, 200).Select(i => RimLight.Breath(i * 0.05)).ToList();
        Assert.All(levels, l => Assert.InRange(l, 0.449, 0.851));
        Assert.True(levels.Max() - levels.Min() > 0.3);
        Assert.Equal(RimLight.Breath(0), RimLight.Breath(RimLight.Period), 6);
    }

    [Fact]
    public void The_gap_is_four_pixels_and_snaps_to_device_pixels()
    {
        Assert.Equal(4, IslandGeometry.SnappedGap(1));
        Assert.Equal(6 / 1.5, IslandGeometry.SnappedGap(1.5), 6);
        Assert.Equal(5 / 1.25, IslandGeometry.SnappedGap(1.25), 6);
        Assert.Equal(4, IslandGeometry.SnappedGap(0));
    }

    [Fact]
    public void Closed_states_grow_to_the_pill_and_content_is_centred()
    {
        Assert.Equal(38, IslandGeometry.ClosedPill(32));
        Assert.Equal(68, IslandGeometry.ClosedPill(68));
        Assert.Equal(0, IslandGeometry.ClosedPill(0));
        Assert.Equal(3, IslandGeometry.ContentTop(38, 32));
        Assert.Equal(0, IslandGeometry.ContentTop(38, 38));
        Assert.Equal(0, IslandGeometry.ContentTop(300, 640));
    }
}

public class IslandWavesTests
{
    static readonly Rgb[] Colours = { new(0x3B, 0x5B, 0xFF), new(0x7A, 0x3C, 0xFF), new(0x19, 0xC3, 0xB2) };

    static byte[] Frame(double t, double strength)
    {
        var px = new byte[IslandWaves.Width * IslandWaves.Height * 4];
        IslandWaves.Render(px, IslandWaves.Width, IslandWaves.Height, Colours, t, strength);
        return px;
    }

    [Fact]
    public void No_strength_draws_nothing()
    {
        Assert.All(Frame(1.3, 0), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Waves_stay_subtle_and_premultiplied()
    {
        var px = Frame(2.7, 1);
        int maxAlpha = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            maxAlpha = Math.Max(maxAlpha, a);
            Assert.True(px[i] <= a && px[i + 1] <= a && px[i + 2] <= a);
        }
        Assert.InRange(maxAlpha, 20, (int)Math.Ceiling(IslandWaves.MaxAlpha * 255 * 1.6));
    }

    [Fact]
    public void Waves_move_with_time_and_are_the_same_for_the_same_time()
    {
        Assert.Equal(Frame(1, 1), Frame(1, 1));
        Assert.NotEqual(Frame(1, 1), Frame(1.5, 1));
    }

    [Fact]
    public void The_upper_edge_stays_quieter_than_the_lower_half()
    {
        var px = Frame(0, 1);
        double Row(int y) => Enumerable.Range(0, IslandWaves.Width).Average(x => px[(y * IslandWaves.Width + x) * 4 + 3]);
        Assert.True(Row(0) < Row(IslandWaves.Height * 2 / 3));
    }
}

public class GlassBackdropTests
{
    [Theory]
    [InlineData("10", "0", WallpaperStyle.Fill)]
    [InlineData("6", "0", WallpaperStyle.Fit)]
    [InlineData("2", null, WallpaperStyle.Stretch)]
    [InlineData("0", "0", WallpaperStyle.Center)]
    [InlineData("0", "1", WallpaperStyle.Tile)]
    [InlineData("22", "0", WallpaperStyle.Span)]
    [InlineData(null, null, WallpaperStyle.Fill)]
    public void The_wallpaper_style_is_read_from_the_registry_values(string? style, string? tile, WallpaperStyle expected) =>
        Assert.Equal(expected, WallpaperFit.StyleFrom(style, tile));

    [Fact]
    public void Fill_covers_the_screen_and_crops_the_overflow()
    {
        var r = WallpaperFit.Place(4000, 2000, 1920, 1080, WallpaperStyle.Fill);
        Assert.Equal(1080, r.H, 3);
        Assert.Equal(2160, r.W, 3);
        Assert.Equal(-120, r.X, 3);
    }

    [Fact]
    public void Fit_shows_all_of_it_with_bars()
    {
        var r = WallpaperFit.Place(4000, 2000, 1920, 1080, WallpaperStyle.Fit);
        Assert.Equal(1920, r.W, 3);
        Assert.Equal(960, r.H, 3);
        Assert.Equal(60, r.Y, 3);
    }

    [Fact]
    public void The_crop_samples_the_right_part_and_outside_is_the_background()
    {
        // 2×1 source: left red, right blue, placed over a 200×100 screen from x=0..200.
        byte[] src = { 0, 0, 255, 255, 255, 0, 0, 255 };
        var placed = new PixelRect(0, 0, 200, 100);
        var right = GlassBackdrop.Crop(src, 2, 1, placed, new PixelRect(150, 0, 40, 20), 2, 1, (9, 9, 9));
        Assert.Equal(255, right[0]);
        Assert.Equal(0, right[2]);
        var outside = GlassBackdrop.Crop(src, 2, 1, placed, new PixelRect(0, 120, 40, 20), 1, 1, (9, 8, 7));
        Assert.Equal(new byte[] { 9, 8, 7, 255 }, outside);
    }

    [Fact]
    public void The_blur_softens_an_edge_and_keeps_flat_areas_flat()
    {
        int w = 16, h = 4;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte v = (byte)(x < w / 2 ? 0 : 200);
                px[o] = px[o + 1] = px[o + 2] = v;
                px[o + 3] = 255;
            }
        GlassBackdrop.Blur(px, w, h, 2);
        int At(int x) => px[(1 * w + x) * 4];
        Assert.Equal(0, At(0));
        Assert.InRange(At(0 + w - 1), 195, 200);
        Assert.InRange(At(w / 2), 40, 160);
        Assert.InRange(At(w / 2 - 1), 40, 160);
        Assert.Equal(255, px[3]);
    }
}

public class IslandWavesFlowTests
{
    [Fact]
    public void The_waves_dissolve_before_the_islands_ends_and_edges()
    {
        var colours = new[] { new Rgb(0x3B, 0x5B, 0xFF), new Rgb(0x7A, 0x3C, 0xFF), new Rgb(0x19, 0xC3, 0xB2) };
        int w = IslandWaves.Width, h = IslandWaves.Height;
        var px = new byte[w * h * 4];
        for (double t = 0; t < 6; t += 0.7)
        {
            IslandWaves.Render(px, w, h, colours, t, 1);
            for (int y = 0; y < h; y++)
            {
                Assert.True(px[(y * w + 0) * 4 + 3] <= 2, "left end");
                Assert.True(px[(y * w + w - 1) * 4 + 3] <= 2, "right end");
            }
        }
    }
}

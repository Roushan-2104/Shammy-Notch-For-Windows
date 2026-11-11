using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Launcher;
using Shammy.Core.Palette;
using Shammy.Core.Search;
using Shammy.Interop;
using Shammy.Modules.Launcher;

namespace Shammy.Modules.Palette;

internal partial class PaletteModule
{
    // ---- Test pipe -------------------------------------------------------------------------------

    static string Describe(RankedPalette r) => r.Items.Count == 0
        ? "no rows"
        : string.Join(" | ", r.Items.Select((i, n) =>
            $"{(n == r.Selected ? "*" : "")}{n + 1}. [{i.Kind}] {i.Title}{(i.Subtitle.Length > 0 ? " \u2014 " + i.Subtitle : "")} ({i.Hint}, {i.Match})"));

    /// <summary>"palette-query spotify": what the palette would show (files included), without touching the UI.</summary>
    string PipeQuery(string text)
    {
        if (text.Trim().Length == 0) return "suggestions || " + Describe(new RankedPalette(Suggestions(), -1));
        var sw = Stopwatch.StartNew();
        var local = LocalItems(text);
        var ranked = Rank(local, text);
        double localMs = sw.Elapsed.TotalMilliseconds;
        string files = "files skipped";
        if (WantsFiles(text, local) is { } sql)
        {
            var done = new ManualResetEventSlim();
            IReadOnlyList<FileHit>? hits = null;
            long ms = -1;
            _files.Submit(new FileSearch.Request(-1, sql, (_, h, t) =>
            {
                hits = h;
                ms = t;
                done.Set();
            }));
            if (!done.Wait(TimeSpan.FromSeconds(3))) files = "files timed out";
            else if (hits is null) files = $"files unavailable ({_files.LastError})";
            else
            {
                var fileRows = FileResults.Rank(text, hits, _profile);
                files = $"files {ms} ms ({hits.Count} hits, {fileRows.Count} shown)";
                ranked = PaletteRanker.Stabilize(ranked, Rank(local.Concat(fileRows.Select(r => FileItem(r.Hit, r.Match))), text));
            }
        }
        return $"local {localMs.ToString("0.0", CultureInfo.InvariantCulture)} ms \u00B7 {files} \u00B7 selected {ranked.Selected + 1} || {Describe(ranked)}";
    }

    string PipeState(string _)
    {
        var times = _localTimes.Count == 0 ? "-" :
            $"avg {_localTimes.Average().ToString("0.0", CultureInfo.InvariantCulture)} ms, max {_localTimes.Max().ToString("0.0", CultureInfo.InvariantCulture)} ms over {_localTimes.Count}";
        return $"text=\u201C{_bar.Text}\u201D notch={_ctx.Notch.Mode} palette={_bar.PaletteHeight:0}px selected={_ranked.Selected + 1} " +
               $"local=[{times}] lastFiles={_lastFileMs} ms/{_lastFileCount} index={_files.Available?.ToString() ?? "unknown"} || {Describe(_ranked)}";
    }

    string PipeKey(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        switch (a)
        {
            case "down": SetSelected(Math.Min(_ranked.Selected + 1, _ranked.Items.Count - 1)); break;
            case "up": SetSelected(Math.Max(_ranked.Selected - 1, -1)); break;
            case "enter" or "ctrl+enter":
                if (_ranked.Selected < 0) _bar.Submit(_bar.Text.Trim());
                else Invoke(_ranked.Selected, a == "ctrl+enter");
                return "ok";
            case "left" or "right":
                if (_ranked.SelectedItem?.Tag is PaletteEntry { Adjust: { } adjust }) adjust(a == "right" ? 1 : -1);
                break;
            default: return "error: down|up|enter|ctrl+enter|left|right";
        }
        return PipeState("");
    }

    string PipeFiles(string text)
    {
        var sql = WindowsSearchSql.Build(text, _profile, 30);
        if (sql is null) return "no query for that text";
        var done = new ManualResetEventSlim();
        IReadOnlyList<FileHit>? hits = null;
        long ms = -1;
        _files.Submit(new FileSearch.Request(-1, sql, (_, h, t) =>
        {
            hits = h;
            ms = t;
            done.Set();
        }));
        if (!done.Wait(TimeSpan.FromSeconds(3))) return "timed out";
        if (hits is null) return $"unavailable ({_files.LastError ?? "retrying later"})";
        return $"{ms} ms, {hits.Count} hits || " + string.Join(" | ", hits.Take(10).Select(h => (h.IsFolder ? "[dir] " : "") + h.Path));
    }

    string PipeAudio(string arg)
    {
        var devices = _audio.ListNow();
        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["set", var n] && int.TryParse(n, out var i) && i >= 1 && i <= devices.Count)
            return _audio.SetDefault(devices[i - 1].Id) ? $"default is now {devices[i - 1].Name}" : "error: switch failed";
        var vol = _audio.Volume();
        return $"volume={(vol is { } v ? $"{Math.Round(v.Level * 100)}%{(v.Muted ? " muted" : "")}" : "none")} || " +
               string.Join(" | ", devices.Select((d, k) => $"{k + 1}. {d.Name}{(d.IsDefault ? " (default)" : "")}"));
    }

    public void Dispose()
    {
        _files.Dispose();
        _audio.Dispose();
        _icons?.Dispose();
    }
}

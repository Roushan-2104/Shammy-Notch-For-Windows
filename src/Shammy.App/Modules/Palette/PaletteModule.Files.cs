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
    // ---- Files (async) ----------------------------------------------------------------------------

    /// <summary>The index query for the text, or null when files aren't worth asking for.</summary>
    string? WantsFiles(string text, List<PaletteItem> local)
    {
        if (_files.Available == false) return null;
        bool search = FileResults.ShouldSearch(text,
            hasCommand: local.Any(i => i.Kind == PaletteKind.Command && i.Key != "calc"),
            isCalculation: local.Any(i => i.Key == "calc"),
            exactSetting: local.Any(i => i.Kind == PaletteKind.Setting && i.Match >= 99));
        return search ? WindowsSearchSql.Build(text, _profile, 30) : null;
    }

    void SubmitFileQuery()
    {
        _fileTimer.Stop();
        if (_pendingSql is not { } sql) return;
        _pendingSql = null;
        int generation = _generation;
        string query = _query;
        _files.Submit(new FileSearch.Request(generation, sql, (id, hits, ms) => _ctx.Ui.BeginInvoke(() =>
        {
            if (id != _generation || hits is null) return;
            _lastFileMs = ms;
            var ranked = FileResults.Rank(query, hits, _profile);
            _lastFileCount = ranked.Count;
            if (ranked.Count == 0) return;
            _local.AddRange(ranked.Select(r => FileItem(r.Hit, r.Match)));
            _ranked = PaletteRanker.Stabilize(_ranked, Rank(_local, query));
            Render();
        })));
    }

    PaletteItem FileItem(FileHit hit, int match)
    {
        var name = Path.GetFileName(hit.Path.TrimEnd('\\'));
        var folder = Path.GetDirectoryName(hit.Path) ?? "";
        return new PaletteItem("file:" + hit.Path, PaletteKind.File, name, DisplayFolder(folder), match)
        {
            Hint = hit.IsFolder ? "Folder" : "File",
            Tag = new PaletteEntry
            {
                Glyph = hit.IsFolder ? "\uE8B7" : "\uE8A5",
                IconPath = hit.Path,
                Run = ctrl => OpenFile(hit, ctrl),
            },
        };
    }

    void OpenFile(FileHit hit, bool showInFolder)
    {
        var name = Path.GetFileName(hit.Path.TrimEnd('\\'));
        bool exists = hit.IsFolder ? Directory.Exists(hit.Path) : File.Exists(hit.Path);
        if (!exists)
        {
            _bar.Complete(new CommandResult("\uE783", $"{name} is gone", "It was moved or deleted", IsError: true));
            return;
        }
        bool ok = showInFolder
            ? _ctx.Open(Native.ExplorerPath, $"/select,\"{hit.Path}\"")
            : _ctx.Open(hit.Path);
        _bar.Complete(ok
            ? new CommandResult(hit.IsFolder ? "\uE8B7" : "\uE8A5", showInFolder ? "Showing in folder" : "Opening", name)
            : new CommandResult("\uE783", $"Couldn't open {name}", null, IsError: true));
    }

    /// <summary>"Documents › Work" instead of "C:\Users\me\Documents\Work".</summary>
    string DisplayFolder(string folder)
    {
        var shown = folder;
        if (_profile.Length > 0 && folder.StartsWith(_profile, StringComparison.OrdinalIgnoreCase))
            shown = folder[_profile.Length..].TrimStart('\\');
        return shown.Length == 0 ? "Home" : shown.Replace("\\", " \u203A ");
    }
}

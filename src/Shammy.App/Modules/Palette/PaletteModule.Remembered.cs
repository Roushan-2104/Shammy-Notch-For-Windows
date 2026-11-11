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
    // ---- Shammy's views, notes, snippets, clipboard ---------------------------------------------------

    /// <summary>
    /// Views ("calendar" opens the Calendar card), then what Shammy remembers: open notes and reminders, snippets,
    /// copied text. Notes and copied text never show while the screen is shared.
    /// </summary>
    void AddRemembered(List<PaletteItem> items, string text)
    {
        bool presenting = Infrastructure.PrivacyState.Presenting;
        foreach (var (view, match) in PaletteSources.FindViews(text, id => _ctx.Notch.ExpandedView.CanOpen(id)))
            items.Add(ViewItem(view, match));

        if (PaletteSources.Allowed(PaletteKind.Note, presenting) && Connect.Links.Notes is { } notes)
        {
            var reminders = notes.Reminders.Where(r => !r.Done).ToDictionary(r => "r:" + r.Id);
            var texts = notes.Reminders.Where(r => !r.Done).OrderBy(r => r.Due).Select(r => new PaletteText("r:" + r.Id, r.Title))
                .Concat(notes.Notes.Where(n => !n.Done).OrderByDescending(n => n.Created).Select(n => new PaletteText("n:" + n.Id, n.Text)))
                .ToList();
            var now = DateTime.Now;
            foreach (var (t, match) in PaletteSources.FindTexts(text, texts, 2))
            {
                var due = reminders.TryGetValue(t.Key, out var r) ? r.Due : (DateTime?)null;
                items.Add(new PaletteItem("note:" + t.Key, PaletteKind.Note, PaletteSources.Line(t.Text),
                    due is { } d ? $"Reminder \u00B7 {Shammy.Core.Productivity.TimeFormat.When(d, now)}" : "Note", match)
                {
                    Hint = due is null ? "Note" : "Reminder",
                    Tag = new PaletteEntry { Glyph = due is null ? "\uE70B" : "\uEA8F", Learn = false, Run = _ => OpenCard("notes") },
                });
            }
        }

        var snippets = Launcher.SnippetsModule.Current;
        if (snippets.Count > 0)
        {
            var texts = snippets.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new PaletteText(s.Key, s.Value, s.Key));
            foreach (var (t, match) in PaletteSources.FindTexts(text, texts, 2))
            {
                items.Add(new PaletteItem("snip:" + t.Trigger, PaletteKind.Snippet, $"{t.Trigger}  {PaletteSources.Line(t.Text, 50)}",
                    "Snippet \u00B7 Enter copies it", match)
                {
                    Hint = "Snippet",
                    Tag = new PaletteEntry { Glyph = "\uE8C8", Run = _ => CopySnippet(t.Trigger, t.Text) },
                });
            }
        }

        if (PaletteSources.Allowed(PaletteKind.Clip, presenting) && Connect.Links.Clipboard is { } clip)
        {
            var entries = clip.Items.Where(e => e.Kind == Shammy.Core.Clipboard.ClipKind.Text).OrderByDescending(e => e.Time).ToList();
            var byKey = entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
            var now = DateTime.Now;
            foreach (var (t, match) in PaletteSources.FindTexts(text, entries.Select(e => new PaletteText(e.Key, e.Text)), 2))
            {
                if (!byKey.TryGetValue(t.Key, out var entry)) continue;
                items.Add(new PaletteItem("clip:" + t.Key, PaletteKind.Clip, PaletteSources.Line(t.Text),
                    $"Clipboard \u00B7 {Shammy.Core.Clipboard.ClipText.Relative(entry.Time, now)}", match)
                {
                    Hint = "Copy",
                    Tag = new PaletteEntry
                    {
                        Glyph = "\uE77F",
                        Learn = false,
                        Run = _ =>
                        {
                            clip.CopyBack(entry);
                            _bar.Complete(null);
                        },
                    },
                });
            }
        }
    }

    PaletteItem ViewItem(ShammyView view, int match) =>
        new("view:" + view.Id, PaletteKind.View, view.Title, "Open in the notch", match)
        {
            Hint = "Shammy",
            Confident = match >= 100,
            Tag = new PaletteEntry { Glyph = view.Glyph, Run = _ => OpenCard(view.Id) },
        };

    /// <summary>Closes the palette, then opens the card in the notch (once the typing bar has gone).</summary>
    void OpenCard(string id)
    {
        _bar.Complete(null);
        _ctx.Ui.BeginInvoke(() =>
        {
            if (!Connect.Links.OpenCard(_ctx, id)) _ctx.ShowMessage("\uE783", "That view isn't available", error: true);
        });
    }

    void CopySnippet(string trigger, string template)
    {
        string? ClipboardText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.ExternalException) { return null; }
        }
        var expanded = SnippetText.Expand(template, DateTime.Now, ClipboardText);
        try
        {
            Clipboard.SetText(expanded);
            _bar.Complete(new CommandResult("\uE8C8", "Snippet copied", trigger));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _bar.Complete(new CommandResult("\uE783", "Clipboard is busy", "Try again in a moment", IsError: true));
        }
    }

    /// <summary>A second command's row: runs that module's command and shows its result like Enter would.</summary>
    async Task RunCommandAsync(ITextCommand cmd, string clean)
    {
        CommandResult result;
        try
        {
            result = await cmd.RunAsync(clean);
            HostLinks.RaiseCommandRan(clean);
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Command {cmd.GetType().Name} failed", ex);
            result = new CommandResult("\uE783", "That didn't work", ex.Message, IsError: true);
        }
        if (result.SearchInstead is { Length: > 0 } search) _bar.SearchWeb(search);
        else _bar.Complete(result);
    }
}

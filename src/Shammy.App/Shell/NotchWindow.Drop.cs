using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Music;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Notifications;
using Shammy.Views;

namespace Shammy.Shell;

/// <summary>
/// A card that takes some drops itself while it is open (Stage takes a .txt or .md script, or dragged text). Whatever
/// it doesn't take still goes to the shelf and the drop zone as usual.
/// </summary>
internal interface ICardDrop
{
    /// <summary>Whether the card takes this; asked as the drag moves, so it only looks.</summary>
    bool AcceptsDrop(IDataObject data);

    /// <summary>The drag is over the island with something the card takes (a chance to say "drop to …").</summary>
    void DropHover();

    /// <summary>It was dropped on the island while the card was open.</summary>
    void TakeDrop(IDataObject data);
}

public partial class NotchWindow
{
    /// <summary>The card on the island now (see <see cref="ShowCard"/>), or null when no card is open.</summary>
    internal object? OpenCardContent => _cardOpen ? CardHost.Content : null;

    /// <summary>The open card (or the open notch's card sheet), when it takes this drag itself (see <see cref="ICardDrop"/>).</summary>
    ICardDrop? CardTaking(DragEventArgs e) =>
        !_dragging && _searchMode is null && !_fullscreen
        && (OpenCardContent ?? (_mode == NotchMode.Expanded ? Expanded.OpenSheet : null)) is ICardDrop card && card.AcceptsDrop(e.Data) ? card : null;

    // ---- Drag and drop onto the shelf -----------------------------------------------------------

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragOut.Stop();
        // An open card that takes this drop itself (Stage and a script) keeps the island; the shelf never opens.
        if (CardTaking(e) is { } card)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            card.DropHover();
            return;
        }
        // A tab a module opened for you (held for 8 s) mustn't block dropping files; only a shelf drag does.
        // Files, and what a browser drags (an image, a link, selected words: see BrowserDrop).
        bool files = BrowserDrop.Accepts(e.Data) && _searchMode is null && _holdOpen - (_held ? 1 : 0) == 0;
        e.Effects = files ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        if (files && !_dragging)
        {
            _dragging = true;
            _peeking = false;
            _hoverIn.Stop();
            Recompute();
        }
    }

    void OnDragOut(object? sender, EventArgs e)
    {
        _dragOut.Stop();
        if (!_dragging) return;
        _dragging = false;
        DropZone.Reset();
        Recompute();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        _dragOut.Stop();
        if (CardTaking(e) is { } card)
        {
            e.Handled = true;
            card.TakeDrop(e.Data);
            return;
        }
        _dragging = false;
        // The drop zone knows which of its targets (Shelf, phone, compress, convert) the files landed on.
        DropData(e.Data, DropZone.TakeTarget());
        e.Handled = true;
    }

    /// <summary>
    /// What was dropped on <paramref name="target"/> (null: the Shelf): files as they are, a browser's drag as files in
    /// Shammy's folder, or a link or words on Send to phone as a link or copied text. Says what happened (the test pipe).
    /// </summary>
    internal string DropData(IDataObject data, Core.DropActions.DropAction? target)
    {
        var files = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
        if (files is not { Length: > 0 } && BrowserDrop.Read(data) is { } dragged)
        {
            // a link or words on Send to phone go as a link or copied text, not as a file
            if (target == Core.DropActions.DropAction.Phone && dragged.Text is { } text && Modules.Connect.Links.Phone is { } phone)
            {
                phone.SendText(text);
                Recompute();
                return "phone: " + dragged.Kind.ToString().ToLowerInvariant();
            }
            files = BrowserDrop.Save(dragged);
        }
        if (files is { Length: > 0 })
        {
            if (target is null or Core.DropActions.DropAction.Shelf)
            {
                FilesDropped?.Invoke(files);
                ExpandTo(shelf: true);
            }
            else
            {
                DropZone.RaiseDropped(target.Value, files);
                Recompute();
            }
            return $"{(target ?? Core.DropActions.DropAction.Shelf).ToString().ToLowerInvariant()}: {string.Join(" | ", files.Select(Path.GetFileName))}";
        }
        Recompute();
        return "nothing taken";
    }
}

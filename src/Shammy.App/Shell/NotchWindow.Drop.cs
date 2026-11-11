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

public partial class NotchWindow
{
    // ---- Drag and drop onto the shelf -----------------------------------------------------------

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragOut.Stop();
        // A tab a module opened for you (held for 8 s) mustn't block dropping files; only a shelf drag does.
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop) && _searchMode is null && _holdOpen - (_held ? 1 : 0) == 0;
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
        _dragging = false;
        // The drop zone knows which of its targets (Shelf, phone, compress, convert) the files landed on.
        var target = DropZone.TakeTarget();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
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
        }
        else
        {
            Recompute();
        }
        e.Handled = true;
    }
}

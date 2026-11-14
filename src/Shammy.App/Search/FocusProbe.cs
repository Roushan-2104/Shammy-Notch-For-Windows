using System.Windows.Automation;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>
/// What has the keyboard in the app in front, through UI Automation: a text box (Right Alt dictates there), and a
/// password box (dictation stays off). Read off the UI thread with a time limit, since a hung app can stall UI
/// Automation; no answer in time counts as "not a text box", never as a password box being safe.
/// </summary>
internal static class FocusProbe
{
    public sealed record Focus(bool Editable, bool Password, bool Known);

    static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(350);

    public static async Task<Focus> ReadAsync()
    {
        var read = Task.Run(Read);
        if (await Task.WhenAny(read, Task.Delay(Limit)) != read) return new Focus(false, false, false);
        return await read;
    }

    static Focus Read()
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el is null) return new Focus(false, false, false);
            bool password = el.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty) is true;
            var type = el.Current.ControlType;
            bool readOnly = el.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly;
            // An edit box; or a document you can write in (Word, a rich editor) — a web page's document is read-only.
            bool editable = !readOnly && (type == ControlType.Edit || (type == ControlType.Document && value is not null));
            return new Focus(editable || password, password, true);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn("Reading the focused element failed", ex);
            return new Focus(false, false, false);
        }
    }
}

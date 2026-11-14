using System.Runtime.InteropServices;
using System.Windows;
using Shammy.Infrastructure;
using Shammy.Modules.SelectionTools;
using static Shammy.Modules.Launcher.SnippetNative;

namespace Shammy.Search;

/// <summary>
/// Types dictated words into a window by pasting them: the clipboard is saved (every format that can be read), the
/// words go on it marked private (so neither Shammy's clipboard history nor Windows' Win+V keeps them), Ctrl+V goes
/// to the window, and the old clipboard comes back a moment later. UI thread only (the clipboard needs it).
/// </summary>
internal static class PasteInto
{
    public enum Result { Typed, WindowChanged, ClipboardBusy }

    /// <summary>Long enough for the app to read the clipboard after Ctrl+V; short enough that a copy right after is rare.</summary>
    static readonly TimeSpan RestoreAfter = TimeSpan.FromMilliseconds(320);

    const ushort VK_V = 0x56, VK_Z = 0x5A;

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr hwnd);

    public static async Task<Result> PasteAsync(string text, IntPtr target)
    {
        await KeysUpAsync();
        // The words go where the cursor was when dictation started, or nowhere.
        if (Interop.Native.GetForegroundWindow() != target) return Result.WindowChanged;

        var saved = Save();
        if (!ClipboardText.TrySet(text, secret: true)) return Result.ClipboardBusy;
        uint ours = GetClipboardSequenceNumber();
        Press(VK_V);
        await Task.Delay(RestoreAfter);
        // Something else copied in the meantime: that is the clipboard now, leave it.
        if (GetClipboardSequenceNumber() == ours) Restore(saved);
        return Result.Typed;
    }

    /// <summary>Undo: the window comes back to the front and gets Ctrl+Z. False when it closed.</summary>
    public static async Task<bool> UndoAsync(IntPtr target)
    {
        if (!IsWindow(target)) return false;
        Interop.Native.ForceForeground(target);
        await Task.Delay(80);
        if (Interop.Native.GetForegroundWindow() != target) return false;
        await KeysUpAsync();
        Press(VK_Z);
        return true;
    }

    /// <summary>Waits (up to 2 s) for Win, Alt, Shift and Ctrl to be let go, so Ctrl+V doesn't arrive as Win+Ctrl+V.</summary>
    static async Task KeysUpAsync()
    {
        for (int i = 0; i < 100 && (IsDown(VK_LWIN) || IsDown(VK_RWIN) || IsDown(VK_MENU) || IsDown(VK_SHIFT) || IsDown(VK_CONTROL)); i++)
            await Task.Delay(20);
    }

    static void Press(ushort vk)
    {
        var inputs = new[] { Key(VK_CONTROL, false), Key(vk, false), Key(vk, true), Key(VK_CONTROL, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Warn($"Dictation couldn't send keys (an elevated window?): {Marshal.GetLastWin32Error()}");
    }

    /// <summary>A copy of what's on the clipboard, format by format; null when it was empty or couldn't be read.</summary>
    static DataObject? Save()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } data) return null;
            var copy = new DataObject();
            int kept = 0;
            foreach (var format in data.GetFormats(false))
            {
                try
                {
                    var value = data.GetData(format, false);
                    if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
                    if (value is null) continue;
                    copy.SetData(format, value, false);
                    kept++;
                }
                catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException or NotImplementedException or OutOfMemoryException)
                {
                    // A format only its app can render (or a huge one): the rest still comes back.
                }
            }
            return kept > 0 ? copy : null;
        }
        catch (ExternalException ex)
        {
            Log.Warn("Saving the clipboard before dictation failed", ex);
            return null;
        }
    }

    static void Restore(DataObject? saved)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (saved is null) Clipboard.Clear();
                else Clipboard.SetDataObject(saved, true);
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        Log.Warn("Putting the clipboard back after dictation failed");
    }
}

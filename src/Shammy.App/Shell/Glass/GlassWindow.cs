using System.Runtime.InteropServices;
using System.Windows;
using Shammy.Interop;

namespace Shammy.Shell.Glass;

/// <summary>
/// The window frosted glass is shown on: borderless, click-through, never activated and placed just under the island's
/// own windows. It has no pixels of its own: <see cref="FrostedGlass"/> puts Windows' backdrop blur on it.
/// </summary>
internal sealed class GlassWindow : System.Windows.Forms.NativeWindow
{
    const int WsPopup = unchecked((int)0x80000000);
    const int ExNoRedirectionBitmap = 0x00200000, ExTopmost = 0x8, ExToolWindow = 0x80, ExNoActivate = 0x08000000, ExTransparent = 0x20, ExLayered = 0x80000;
    const int WmNcHitTest = 0x84, WmMouseActivate = 0x21, HtTransparent = -1, MaNoActivate = 3;

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    public GlassWindow()
    {
        CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "Shammy glass",
            Style = WsPopup,
            // Layered and transparent: clicks fall through to whatever is under the glass.
            ExStyle = ExNoRedirectionBitmap | ExTopmost | ExToolWindow | ExNoActivate | ExTransparent | ExLayered,
            Width = 1,
            Height = 1,
        });
        SetLayeredWindowAttributes(Handle, 0, 255, 2);
    }

    /// <summary>Shows the window over <paramref name="rect"/> (device pixels), just under <paramref name="under"/> (or on top).</summary>
    public void Place(Int32Rect rect, IntPtr under)
    {
        const uint noActivate = 0x10, show = 0x40;
        SetWindowPos(Handle, under != IntPtr.Zero ? under : Native.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, noActivate | show);
    }

    public void Hide() => ShowWindow(Handle, 0);

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        switch (m.Msg)
        {
            case WmNcHitTest:
                m.Result = HtTransparent;
                return;
            case WmMouseActivate:
                m.Result = MaNoActivate;
                return;
        }
        base.WndProc(ref m);
    }
}

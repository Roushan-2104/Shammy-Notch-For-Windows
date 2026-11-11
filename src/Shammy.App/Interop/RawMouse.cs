using System.Runtime.InteropServices;

namespace Shammy.Interop;

/// <summary>
/// The mouse's own movement reports (raw input), which keep coming when the pointer is stopped at the edge of the
/// screen. Only listened to while the pointer is over the notch, so the rest of the time no extra messages arrive.
/// </summary>
internal static class RawMouse
{
    public const int WM_INPUT = 0x00FF;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    const uint RIDEV_REMOVE = 0x00000001, RIDEV_INPUTSINK = 0x00000100, RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0;
    const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER
    {
        public uint Type, Size;
        public IntPtr Device, WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);

    static readonly int HeaderSize = Marshal.SizeOf<RAWINPUTHEADER>();

    /// <summary>Starts (or stops) sending the mouse's movement to <paramref name="hwnd"/> as WM_INPUT.</summary>
    public static bool Listen(IntPtr hwnd, bool on)
    {
        var device = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE, Target = on ? hwnd : IntPtr.Zero };
        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>
    /// The vertical movement in a WM_INPUT message (negative is up), or null when it is not a relative mouse
    /// movement (a pen, a touch screen or a remote session report positions, not movement).
    /// </summary>
    public static int? VerticalMove(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)HeaderSize);
        if (size == 0 || size > 1024) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)HeaderSize) != size) return null;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.Type != RIM_TYPEMOUSE) return null;
            // RAWMOUSE: usFlags (2 bytes, then 2 of padding), the buttons (4), the raw buttons (4), lLastX, lLastY.
            ushort flags = (ushort)Marshal.ReadInt16(buffer, HeaderSize);
            if ((flags & MOUSE_MOVE_ABSOLUTE) != 0) return null;
            return Marshal.ReadInt32(buffer, HeaderSize + 16);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

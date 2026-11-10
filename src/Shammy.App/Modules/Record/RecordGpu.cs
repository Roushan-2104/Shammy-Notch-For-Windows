using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Shammy.Modules.Record;

/// <summary>
/// The Direct3D 11 device a recording keeps its frames on, reached through the few COM calls it needs (no graphics
/// package): make a texture, copy one into another, and hand textures to Windows' capture and video APIs.
/// The device is switched to multithread-protected because the capture thread, the pacing thread and the video
/// encoder all use it.
/// </summary>
internal sealed class RecordGpu : IDisposable
{
    const int DriverHardware = 1, DriverWarp = 5, SdkVersion = 7;
    const uint BgraSupport = 0x20, VideoSupport = 0x800;
    const int FormatBgra8 = 87;
    const uint BindShaderResource = 0x8, BindRenderTarget = 0x20;

    // Slots in the COM method tables (IUnknown takes 0-2).
    const int DeviceCreateTexture2D = 5, ContextCopySubresourceRegion = 46, ContextCopyResource = 47, TextureGetDesc = 10,
        MultithreadSetProtected = 5;

    static readonly Guid DxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C"), DxgiSurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC"),
        Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C"), Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0"),
        CaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [StructLayout(LayoutKind.Sequential)]
    struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SubresourceData
    {
        public IntPtr SysMem;
        public uint SysMemPitch, SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateTexture2DFn(IntPtr self, ref TextureDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void CopySubresourceRegionFn(IntPtr self, IntPtr dst, uint dstSubresource, uint x, uint y, uint z, IntPtr src, uint srcSubresource, ref Box box);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void GetDescFn(IntPtr self, out TextureDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetMultithreadProtectedFn(IntPtr self, int on);

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount,
        uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out IntPtr result);
    }

    readonly IntPtr _device, _context;
    readonly CreateTexture2DFn _createTexture;
    readonly CopySubresourceRegionFn _copyRegion;
    readonly CopyResourceFn _copy;
    bool _disposed;

    RecordGpu(IntPtr device, IntPtr context, IDirect3DDevice winrt, bool software)
    {
        _device = device;
        _context = context;
        Device = winrt;
        Software = software;
        _createTexture = Method<CreateTexture2DFn>(device, DeviceCreateTexture2D);
        _copyRegion = Method<CopySubresourceRegionFn>(context, ContextCopySubresourceRegion);
        _copy = Method<CopyResourceFn>(context, ContextCopyResource);
    }

    /// <summary>The device as Windows' capture and video APIs take it.</summary>
    public IDirect3DDevice Device { get; }

    /// <summary>True when no graphics card could be used and Windows' software renderer stands in.</summary>
    public bool Software { get; }

    /// <summary>Makes the device: the graphics card, or Windows' software renderer when that fails.</summary>
    public static RecordGpu Create()
    {
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
        bool software = false;
        int hr = -1;
        foreach (var (driver, flags) in new[] { (DriverHardware, BgraSupport | VideoSupport), (DriverHardware, BgraSupport), (DriverWarp, BgraSupport) })
        {
            hr = D3D11CreateDevice(IntPtr.Zero, driver, IntPtr.Zero, flags, IntPtr.Zero, 0, SdkVersion, out device, out _, out context);
            if (hr >= 0)
            {
                software = driver == DriverWarp;
                break;
            }
        }
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            var iid = Multithread;
            if (Marshal.QueryInterface(device, ref iid, out var multithread) >= 0)
            {
                try { Method<SetMultithreadProtectedFn>(multithread, MultithreadSetProtected)(multithread, 1); }
                finally { Marshal.Release(multithread); }
            }

            iid = DxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref iid, out var dxgi));
            IntPtr inspectable;
            try { Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable)); }
            finally { Marshal.Release(dxgi); }
            try { return new RecordGpu(device, context, MarshalInterface<IDirect3DDevice>.FromAbi(inspectable), software); }
            finally { Marshal.Release(inspectable); }
        }
        catch
        {
            Marshal.Release(context);
            Marshal.Release(device);
            throw;
        }
    }

    /// <summary>The capture item for a whole monitor.</summary>
    public static GraphicsCaptureItem ItemForMonitor(IntPtr monitor)
    {
        var iid = CaptureItem;
        Marshal.ThrowExceptionForHR(GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForMonitor(monitor, ref iid, out var item));
        try { return GraphicsCaptureItem.FromAbi(item); }
        finally { Marshal.Release(item); }
    }

    /// <summary>The capture item for one window.</summary>
    public static GraphicsCaptureItem ItemForWindow(IntPtr window)
    {
        var iid = CaptureItem;
        Marshal.ThrowExceptionForHR(GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForWindow(window, ref iid, out var item));
        try { return GraphicsCaptureItem.FromAbi(item); }
        finally { Marshal.Release(item); }
    }

    /// <summary>
    /// A BGRA texture of this size (release it with <see cref="Release"/>). With <paramref name="black"/> it starts
    /// as black pixels; otherwise its content is whatever is copied into it first.
    /// </summary>
    public IntPtr CreateTexture(int width, int height, bool black = false)
    {
        var desc = new TextureDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatBgra8,
            SampleCount = 1,
            BindFlags = BindShaderResource | BindRenderTarget,
        };
        if (!black)
        {
            Marshal.ThrowExceptionForHR(_createTexture(_device, ref desc, IntPtr.Zero, out var plain));
            return plain;
        }
        var pixels = GCHandle.Alloc(new byte[width * height * 4], GCHandleType.Pinned);
        var data = new SubresourceData { SysMem = pixels.AddrOfPinnedObject(), SysMemPitch = (uint)(width * 4) };
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            Marshal.ThrowExceptionForHR(_createTexture(_device, ref desc, pinned.AddrOfPinnedObject(), out var texture));
            return texture;
        }
        finally
        {
            pinned.Free();
            pixels.Free();
        }
    }

    /// <summary>The texture as a surface Windows' video API can read. The surface keeps the texture alive.</summary>
    public static IDirect3DSurface Wrap(IntPtr texture)
    {
        var iid = DxgiSurface;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, ref iid, out var dxgi));
        IntPtr inspectable;
        try { Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgi, out inspectable)); }
        finally { Marshal.Release(dxgi); }
        try { return MarshalInterface<IDirect3DSurface>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    /// <summary>The texture behind a captured frame's surface (release it with <see cref="Release"/>).</summary>
    public static IntPtr TextureOf(IDirect3DSurface surface)
    {
        var iid = Texture2D;
        Marshal.ThrowExceptionForHR(surface.As<IDirect3DDxgiInterfaceAccess>().GetInterface(ref iid, out var texture));
        return texture;
    }

    public static (int Width, int Height) SizeOf(IntPtr texture)
    {
        Method<GetDescFn>(texture, TextureGetDesc)(texture, out var desc);
        return ((int)desc.Width, (int)desc.Height);
    }

    /// <summary>Copies a whole texture into another of the same size.</summary>
    public void Copy(IntPtr destination, IntPtr source) => _copy(_context, destination, source);

    /// <summary>Copies a rectangle of <paramref name="source"/> to the top-left corner of <paramref name="destination"/>.</summary>
    public void CopyRegion(IntPtr destination, IntPtr source, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        var box = new Box { Left = (uint)x, Top = (uint)y, Front = 0, Right = (uint)(x + width), Bottom = (uint)(y + height), Back = 1 };
        _copyRegion(_context, destination, 0, 0, 0, 0, source, 0, ref box);
    }

    public static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero) Marshal.Release(comObject);
    }

    static T Method<T>(IntPtr comObject, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Device.Dispose(); }
        catch (Exception) { /* already gone with the session */ }
        Marshal.Release(_context);
        Marshal.Release(_device);
    }
}

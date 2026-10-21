using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowsShell.Shots;

/// Видеокарта для записи экрана: устройство Direct3D 11, текстуры и их обёртки для Windows
/// (захват экрана Windows.Graphics.Capture и кодировщик видео работают с ними).
/// Методы COM зовём по номеру в таблице — так короче, чем описывать весь Direct3D.
internal sealed unsafe class Gpu : IDisposable
{
    public IntPtr Device { get; }
    readonly IntPtr _context;
    public IDirect3DDevice WinRT { get; }
    readonly object _lock = new();

    public Gpu()
    {
        const uint BGRA = 0x20, VIDEO = 0x800;
        // Сначала видеокарта; нет — программная (WARP), медленнее, но работает.
        var hr = D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, BGRA | VIDEO, IntPtr.Zero, 0, 7, out var device, out _, out var context);
        if (hr != 0) hr = D3D11CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, BGRA | VIDEO, IntPtr.Zero, 0, 7, out device, out _, out context);
        Marshal.ThrowExceptionForHR(hr);
        Device = device;
        _context = context;
        // Кодировщик работает с устройством из своих потоков.
        var mt = Query(context, IID_ID3D10Multithread);
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(mt)[5])(mt, 1);
        Marshal.Release(mt);
        var dxgi = Query(device, IID_IDXGIDevice);
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
        Marshal.Release(dxgi);
        WinRT = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
    }

    /// Текстура BGRA w×h (видеопамять). Освобождать через Release.
    public IntPtr CreateTexture(int w, int h)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 87 /* B8G8R8A8_UNORM */,
            SampleCount = 1, Usage = 0, BindFlags = 0x8 | 0x20 /* SHADER_RESOURCE | RENDER_TARGET */,
        };
        IntPtr tex;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Vtbl(Device)[5])(Device, &desc, IntPtr.Zero, &tex));
        return tex;
    }

    /// Обёртка текстуры для Windows (кадр для кодировщика).
    public static IDirect3DSurface Surface(IntPtr texture)
    {
        var dxgi = Query(texture, IID_IDXGISurface);
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgi, out var inspectable));
            var surface = MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            return surface;
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// Текстура под обёрткой Windows (кадр захвата экрана). Освобождать через Release.
    public static IntPtr Texture(IDirect3DSurface surface)
    {
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = Query(abi, IID_IDirect3DDxgiInterfaceAccess);
            try
            {
                var iid = IID_ID3D11Texture2D;
                IntPtr tex;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(access)[3])(access, &iid, &tex));
                return tex;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// Скопировать прямоугольник `src` (left, top, right, bottom) в левый верх `dst`.
    public void CopyRegion(IntPtr dst, IntPtr src, int left, int top, int right, int bottom)
    {
        var box = new D3D11_BOX { left = (uint)left, top = (uint)top, front = 0, right = (uint)right, bottom = (uint)bottom, back = 1 };
        lock (_lock)
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Vtbl(_context)[46])(_context, dst, 0, 0, 0, 0, src, 0, &box);
    }

    public void Copy(IntPtr dst, IntPtr src)
    {
        lock (_lock)
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(_context)[47])(_context, dst, src);
    }

    /// Захват монитора (Windows.Graphics.Capture).
    public static GraphicsCaptureItem ItemForMonitor(IntPtr monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        try
        {
            var iidInterop = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iidInterop, out var factory));
            try
            {
                var iidItem = IID_IGraphicsCaptureItem;
                IntPtr item;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(factory)[4])(factory, monitor, &iidItem, &item));
                var result = GraphicsCaptureItem.FromAbi(item);
                Marshal.Release(item);
                return result;
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    public void Dispose()
    {
        (WinRT as IDisposable)?.Dispose();
        Marshal.Release(_context);
        Marshal.Release(Device);
    }

    static IntPtr* Vtbl(IntPtr com) => *(IntPtr**)com;

    static IntPtr Query(IntPtr com, Guid iid)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(com, in iid, out var result));
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_BOX { public uint left, top, front, right, bottom, back; }

    static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    static readonly Guid IID_IDXGISurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
    static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    static readonly Guid IID_ID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
}

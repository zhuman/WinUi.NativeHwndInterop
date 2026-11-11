using Microsoft.UI.Composition;
using Microsoft.UI.Content;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectComposition;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;

#pragma warning disable CS8305 // ContentExternalOutputLink is experimental.

namespace WinUI.NativeHwndInterop;

/// <summary>
/// Redirects a layered child HWND into a WinUI composition visual without owning the HWND.
/// </summary>
/// <remarks>
/// Create, access and dispose on the HWND's owning compositor thread. The caller must detach
/// PlacementVisual before disposal and keep the HWND alive until disposal. PlacementVisual uses source
/// backing pixels, which can differ from displayed physical pixels for mixed-DPI windows; query
/// NativeScaleFactor for compensation. The caller controls size, scale, placement and parent.
/// No XAML element is required.
/// </remarks>
public sealed unsafe class NativeHwndSurface : INativeHwndSurface
{
    private readonly uint _threadId;
    private readonly nint _windowHandle;
    private ID3D11Device* _d3dDevice;
    private IDCompositionDevice* _dcompDevice;
    private IUnknown* _hwndSurface;
    private IDCompositionVisual* _visual;
    private IDCompositionTarget* _target;
    private ContentExternalOutputLink? _outputLink;
    private bool _disposed;

    public static bool IsSupported => ContentExternalOutputLink.IsSupported();

    public NativeHwndSurface(nint windowHandle, Compositor compositor)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        _threadId = PInvoke.GetCurrentThreadId();
        if (compositor.DispatcherQueue?.HasThreadAccess != true)
        {
            throw new InvalidOperationException("The compositor must belong to the calling thread.");
        }
        HWND hwnd = new(windowHandle);
        if (windowHandle == 0 || !PInvoke.IsWindow(hwnd))
        {
            throw new ArgumentException("A live HWND is required.", nameof(windowHandle));
        }
        if (PInvoke.GetWindowThreadProcessId(hwnd, null) != _threadId)
        {
            throw new ArgumentException("The HWND must belong to the calling thread.", nameof(windowHandle));
        }
        if ((PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & (int)WINDOW_STYLE.WS_CHILD) == 0 ||
            (PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & (int)WINDOW_EX_STYLE.WS_EX_LAYERED) == 0)
        {
            throw new ArgumentException("The HWND must have WS_CHILD and WS_EX_LAYERED styles.", nameof(windowHandle));
        }
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("ContentExternalOutputLink is not supported on this system.");
        }
        _windowHandle = windowHandle;
        try
        {
            CreateSurface(hwnd, compositor);
        }
        catch (Exception creationError)
        {
            Exception? cleanupError = Cleanup();
            _disposed = true;
            if (cleanupError is not null)
            {
                throw new AggregateException("Surface creation and cleanup failed.", creationError, cleanupError);
            }
            throw;
        }
    }

    public nint WindowHandle
    {
        get { VerifyAccess(); return _windowHandle; }
    }

    public Visual PlacementVisual
    {
        get { VerifyAccess(); return _outputLink!.PlacementVisual; }
    }

    /// <inheritdoc />
    public double NativeScaleFactor
    {
        get
        {
            VerifyAccess();
            HWND child = new(_windowHandle);
            HWND parent = PInvoke.GetParent(child);
            uint childDpi = PInvoke.GetDpiForWindow(child);
            uint parentDpi = PInvoke.GetDpiForWindow(parent);
            if (childDpi == 0 || parentDpi == 0)
            {
                throw new InvalidOperationException("The source HWND and its parent must remain live to query DPI scaling.");
            }
            return (double)parentDpi / childDpi;
        }
    }

    private void VerifyAccess()
    {
        if (PInvoke.GetCurrentThreadId() != _threadId)
        {
            throw new InvalidOperationException("NativeHwndSurface is thread-affine.");
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CreateSurface(HWND hwnd, Compositor compositor)
    {
        HRESULT hr = default;
        foreach (D3D_DRIVER_TYPE driver in (ReadOnlySpan<D3D_DRIVER_TYPE>)[D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP])
        {
            ID3D11Device* device = null;
            hr = PInvoke.D3D11CreateDevice(null, driver, HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, [], PInvoke.D3D11_SDK_VERSION,
                &device, out _, null);
            _d3dDevice = device;
            if (hr.Succeeded)
            {
                break;
            }
            Release(ref _d3dDevice);
        }
        hr.ThrowOnFailure();

        IDXGIDevice* dxgi = null;
        try
        {
            Guid iid = IDXGIDevice.IID_Guid;
            _d3dDevice->QueryInterface(&iid, (void**)&dxgi).ThrowOnFailure();
            IDCompositionDevice* device = null;
            hr = PInvoke.DCompositionCreateDevice(dxgi, out device);
            _dcompDevice = device;
            hr.ThrowOnFailure();
        }
        finally
        {
            Release(ref dxgi);
        }

        IUnknown* surface = null;
        try
        {
            _dcompDevice->CreateSurfaceFromHwnd(hwnd, &surface);
        }
        finally
        {
            _hwndSurface = surface;
        }
        IDCompositionVisual* visual = null;
        try
        {
            _dcompDevice->CreateVisual(&visual);
        }
        finally
        {
            _visual = visual;
        }
        _visual->SetContent(_hwndSurface);

        _outputLink = ContentExternalOutputLink.Create(compositor);
        nint abi = ((IWinRTObject)_outputLink).NativeObject.ThisPtr;
        IDCompositionTarget* target = null;
        Guid iidTarget = IDCompositionTarget.IID_Guid;
        hr = ((IUnknown*)abi)->QueryInterface(&iidTarget, (void**)&target);
        _target = target;
        GC.KeepAlive(_outputLink);
        hr.ThrowOnFailure();
        _target->SetRoot(_visual);
        _dcompDevice->Commit();
    }

    public void Dispose()
    {
        if (PInvoke.GetCurrentThreadId() != _threadId)
        {
            throw new InvalidOperationException("NativeHwndSurface must be disposed on its owning thread.");
        }
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Exception? error = Cleanup();
        if (error is not null)
        {
            throw error;
        }
    }

    private Exception? Cleanup()
    {
        Exception? error = null;
        try
        {
            if (_target is not null)
            {
                _target->SetRoot(null);
                if (_dcompDevice is not null)
                {
                    _dcompDevice->Commit();
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Release(ref _target);
            try
            {
                _outputLink?.Dispose();
            }
            catch (Exception ex)
            {
                error = error is null ? ex : new AggregateException(error, ex);
            }
            finally
            {
                _outputLink = null;
                Release(ref _visual);
                Release(ref _hwndSurface);
                Release(ref _dcompDevice);
                Release(ref _d3dDevice);
            }
        }
        return error;
    }

    private static void Release<T>(ref T* pointer) where T : unmanaged
    {
        if (pointer is not null)
        {
            T* value = pointer;
            pointer = null;
            ((IUnknown*)value)->Release();
        }
    }
}

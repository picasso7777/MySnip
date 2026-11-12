using System.Runtime.InteropServices;
using MySnip.App.Interop;

namespace MySnip.App.Services;

internal sealed class VirtualDesktopService : IDisposable
{
    private static readonly Guid ClassId = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private IVirtualDesktopManager? _manager;
    private bool _attempted;

    public Guid? ActiveDesktop()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return IsCurrent(foreground) ? GetDesktop(foreground) : null;
    }

    public bool MoveToDesktop(nint window, Guid desktop)
    {
        if (window == 0 || GetManager() is not { } manager) return false;
        if (GetDesktop(window) == desktop || IsCurrent(window)) return true;
        if (manager.MoveWindowToDesktop(window, in desktop) >= 0) return true;
        var owner = NativeMethods.GetAncestor(window, 3);
        return owner != 0 && owner != window && manager.MoveWindowToDesktop(owner, in desktop) >= 0;
    }

    private Guid? GetDesktop(nint window) => window != 0 && GetManager() is { } manager &&
        manager.GetWindowDesktopId(window, out var desktop) >= 0 && desktop != Guid.Empty ? desktop : null;

    private bool IsCurrent(nint window) => window != 0 && GetManager() is { } manager &&
        manager.IsWindowOnCurrentVirtualDesktop(window, out var current) >= 0 && current;

    private IVirtualDesktopManager? GetManager()
    {
        if (_attempted) return _manager;
        _attempted = true;
        try
        {
            var type = Type.GetTypeFromCLSID(ClassId);
            if (type is not null) _manager = Activator.CreateInstance(type) as IVirtualDesktopManager;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
        return _manager;
    }

    public void Dispose()
    {
        if (_manager is not null) Marshal.ReleaseComObject(_manager);
        _manager = null;
    }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(nint topLevelWindow, in Guid desktopId);
    }
}

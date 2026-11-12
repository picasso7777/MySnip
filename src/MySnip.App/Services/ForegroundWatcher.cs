using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using MySnip.App.Interop;

namespace MySnip.App.Services;

internal sealed class ForegroundWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DesktopInput.WinEventCallback _callback;
    private readonly nint _hook;

    public ForegroundWatcher(Action changed)
    {
        _callback = (_, _, _, _, _, _, _) => _dispatcher.BeginInvoke(changed, DispatcherPriority.Input);
        _hook = DesktopInput.SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
    }

    public static bool Matches(nint window, string? exclusions)
    {
        if (window == 0 || string.IsNullOrWhiteSpace(exclusions)) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            return exclusions.Split([',', ';', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(entry => name.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
                    (name + ".exe").Equals(entry, StringComparison.OrdinalIgnoreCase) ||
                    path?.Contains(entry.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    public void Dispose()
    {
        if (_hook != 0) DesktopInput.UnhookWinEvent(_hook);
    }
}

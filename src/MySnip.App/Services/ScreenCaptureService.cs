using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using MySnip.App.Interop;
using MySnip.Core.Models;
using MySnip.Core.Services;

namespace MySnip.App.Services;

internal sealed class ScreenCaptureService : IScreenCaptureService
{
    public CapturedFrame CaptureVirtualDesktop()
    {
        var bounds = VirtualBounds();
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new CapturedFrame(bounds, data.Stride, bytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public IReadOnlyList<ScreenDescriptor> GetScreens() =>
        System.Windows.Forms.Screen.AllScreens.Select(screen => new ScreenDescriptor(
            screen.DeviceName,
            new PixelRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height),
            GetScale(screen).X,
            GetScale(screen).Y)).ToArray();

    public IReadOnlyList<CaptureCandidate> GetCaptureCandidates()
        => CollectCaptureCandidates(includeAutomation: false, CancellationToken.None);

    internal IReadOnlyList<CaptureCandidate> GetAutomationCaptureCandidates(CancellationToken cancellationToken)
        => CollectCaptureCandidates(includeAutomation: true, cancellationToken);

    private static IReadOnlyList<CaptureCandidate> CollectCaptureCandidates(
        bool includeAutomation, CancellationToken cancellationToken)
    {
        const int maximumElements = 1200;
        const int maximumElementsPerWindow = 250;
        const int automationBudgetMilliseconds = 1000;
        var desktop = VirtualBounds();
        var candidates = new HashSet<CaptureCandidate>();
        var layer = 0;
        var totalElements = 0;
        var automationTimer = Stopwatch.StartNew();
        var currentProcessId = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) ||
                IsDesktopWindow(hwnd) || IsWindowCloaked(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == currentProcessId) return true;
            if (TryGetWindowBounds(hwnd) is not { IsEmpty: false } bounds) return true;
            bounds = bounds.ClampTo(desktop);
            if (bounds.IsEmpty) return true;

            var currentLayer = layer++;
            AddCandidate(candidates, bounds, CaptureCandidateKind.TopLevelWindow, currentLayer, desktop);
            if (includeAutomation && totalElements < maximumElements &&
                automationTimer.ElapsedMilliseconds < automationBudgetMilliseconds)
            {
                var windowLimit = Math.Min(maximumElementsPerWindow, maximumElements - totalElements);
                totalElements += AddAutomationElements(hwnd, candidates, currentLayer, desktop, windowLimit,
                    cancellationToken, automationTimer, automationBudgetMilliseconds);
            }

            return !includeAutomation || automationTimer.ElapsedMilliseconds < automationBudgetMilliseconds;
        }, 0);
        return candidates.ToArray();
    }

    private static int AddAutomationElements(nint hwnd, HashSet<CaptureCandidate> candidates,
        int layer, PixelRect desktop, int maximumElements, CancellationToken cancellationToken,
        Stopwatch automationTimer, int automationBudgetMilliseconds)
    {
        try
        {
            var cache = new CacheRequest
            {
                AutomationElementMode = AutomationElementMode.None,
                TreeScope = TreeScope.Descendants,
                TreeFilter = Automation.ControlViewCondition
            };
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            var root = AutomationElement.FromHandle(hwnd);
            AutomationElementCollection elements;
            using (cache.Activate())
                elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            var count = 0;
            for (var index = 0; index < elements.Count && count < maximumElements; index++)
            {
                if (cancellationToken.IsCancellationRequested ||
                    automationTimer.ElapsedMilliseconds >= automationBudgetMilliseconds) break;
                count++;
                try
                {
                    var cached = elements[index].Cached;
                    if (cached.IsOffscreen) continue;
                    var rectangle = cached.BoundingRectangle;
                    if (rectangle.IsEmpty || !double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
                        !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height)) continue;
                    var bounds = new PixelRect(
                        (int)Math.Round(rectangle.X), (int)Math.Round(rectangle.Y),
                        (int)Math.Round(rectangle.Width), (int)Math.Round(rectangle.Height));
                    AddCandidate(candidates, bounds, CaptureCandidateKind.UiElement, layer, desktop);
                }
                catch (Exception ex) when (IsAutomationFailure(ex))
                {
                    // Cached provider data for one element may still be incomplete.
                }
            }

            return count;
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            // UI Automation providers are external processes and can disappear while enumerating.
            // This HWND keeps its native fallback; EnumWindows continues with the next layer.
            return 0;
        }
    }

    private static void AddCandidate(HashSet<CaptureCandidate> candidates, PixelRect bounds,
        CaptureCandidateKind kind, int layer, PixelRect desktop)
    {
        bounds = bounds.ClampTo(desktop);
        if (bounds.Width >= 6 && bounds.Height >= 6 &&
            (kind == CaptureCandidateKind.TopLevelWindow || bounds != desktop))
            candidates.Add(new CaptureCandidate(bounds, kind, layer));
    }

    private static bool IsAutomationFailure(Exception exception) => exception is
        ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException;

    private static PixelRect? TryGetWindowBounds(nint hwnd)
    {
        NativeMethods.Rect rect;
        var hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaExtendedFrameBounds,
            out rect, Marshal.SizeOf<NativeMethods.Rect>());
        if (hr != 0 && !NativeMethods.GetWindowRect(hwnd, out rect)) return null;
        return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static bool IsDesktopWindow(nint hwnd)
    {
        var className = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW";
    }

    private static bool IsWindowCloaked(nint hwnd) =>
        NativeMethods.DwmGetWindowAttributeInt(hwnd, NativeMethods.DwmwaCloaked, out var cloaked, sizeof(int)) == 0 &&
        cloaked != 0;

    private static PixelRect VirtualBounds() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

    private static (double X, double Y) GetScale(System.Windows.Forms.Screen screen)
    {
        var center = new NativeMethods.Point
        {
            X = screen.Bounds.Left + screen.Bounds.Width / 2,
            Y = screen.Bounds.Top + screen.Bounds.Height / 2
        };
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
        if (monitor != 0 && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
            return (Math.Max(1, dpiX / 96d), Math.Max(1, dpiY / 96d));

        using var graphics = Graphics.FromHwnd(nint.Zero);
        return (Math.Max(1, graphics.DpiX / 96d), Math.Max(1, graphics.DpiY / 96d));
    }
}

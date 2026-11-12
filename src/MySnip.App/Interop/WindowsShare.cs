using System.Runtime.InteropServices;

namespace MySnip.App.Interop;

internal sealed unsafe partial class WindowsShare : IDisposable
{
    private const string ManagerClass = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
    private const string StreamClass = "Windows.Storage.Streams.RandomAccessStreamReference";
    private static readonly Guid DesktopInteropId = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");
    private static readonly Guid ManagerId = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    private static readonly Guid StreamId = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
    private static readonly Guid StreamFactoryId = new("857309DC-3FBF-4E7D-986F-EF3B1A07A964");
    private static readonly Guid ManagerStaticsId = new("C54EC2EC-9F97-4D63-9868-395E271AD8F5");
    private nint _manager;
    private long _token;
    private bool _subscribed;
    private bool _uninitialize;

    public static bool IsSupported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return false;
        var initialized = RoInitialize(0) >= 0;
        try
        {
            using var factory = GetFactory(ManagerClass, ManagerStaticsId);
            byte supported = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(factory.Handle, 6))(factory.Handle, &supported));
            return supported != 0;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return false; }
        finally { if (initialized) RoUninitialize(); }
    }

    public void Show(nint owner, byte[] png)
    {
        if (owner == 0) throw new ArgumentException("分享需要已開啟的視窗。", nameof(owner));
        Dispose();
        _uninitialize = RoInitialize(0) >= 0;
        try
        {
            using var interop = GetFactory(ManagerClass, DesktopInteropId);
            var id = ManagerId;
            nint manager = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(interop.Handle, 3))(
                interop.Handle, owner, &id, &manager));
            _manager = manager;

            var handler = new MySnipShareHandler(png);
            var callback = Marshal.GetComInterfaceForObject<MySnipShareHandler, IMySnipShareHandler>(handler);
            try
            {
                long token = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(manager, 6))(manager, callback, &token));
                _token = token;
                _subscribed = true;
            }
            finally { Marshal.Release(callback); }
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(interop.Handle, 4))(interop.Handle, owner));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static int FillRequest(nint args, byte[] png)
    {
        try
        {
            using var request = GetReference(args, 6);
            try
            {
                using var data = GetReference(request.Handle, 6);
                using var properties = GetReference(data.Handle, 7);
                using var title = new HString("MySnip 截圖");
                SetReference(properties.Handle, 7, title.Handle);
                using var bitmap = MakeStreamReference(png);
                SetReference(data.Handle, 21, bitmap.Handle);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                using var message = new HString("MySnip 無法準備分享的圖片。");
                SetReference(request.Handle, 9, message.Handle);
            }
            return 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { return ex.HResult; }
    }

    private static ComReference MakeStreamReference(byte[] bytes)
    {
        nint input;
        fixed (byte* pointer = bytes) input = SHCreateMemStream(pointer, (uint)bytes.Length);
        if (input == 0) throw new InvalidOperationException("無法建立分享圖片串流。");
        using var stream = new ComReference(input);
        var streamId = StreamId;
        Check(CreateRandomAccessStreamOverStream(stream.Handle, 0, in streamId, out var randomStream));
        using var random = new ComReference(randomStream);
        using var factory = GetFactory(StreamClass, StreamFactoryId);
        nint reference = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(factory.Handle, 8))(
            factory.Handle, random.Handle, &reference));
        return new ComReference(reference);
    }

    private static ComReference GetFactory(string runtimeClass, Guid id)
    {
        using var name = new HString(runtimeClass);
        Check(RoGetActivationFactory(name.Handle, in id, out var factory));
        return new ComReference(factory);
    }

    private static ComReference GetReference(nint instance, int slot)
    {
        nint reference = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &reference));
        return new ComReference(reference);
    }

    private static void SetReference(nint instance, int slot, nint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(instance, slot))(instance, value));

    private static nint Slot(nint instance, int slot)
    {
        if (instance == 0) throw new InvalidOperationException("Windows 分享物件未初始化。");
        return (*(nint**)instance)[slot];
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    public void Dispose()
    {
        if (_manager != 0)
        {
            if (_subscribed) ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(_manager, 7))(_manager, _token);
            Marshal.Release(_manager);
            _manager = 0;
            _subscribed = false;
        }
        if (_uninitialize) { RoUninitialize(); _uninitialize = false; }
    }

    private sealed class ComReference(nint handle) : IDisposable
    {
        internal nint Handle { get; } = handle;
        public void Dispose() { if (Handle != 0) Marshal.Release(Handle); }
    }

    private sealed class HString : IDisposable
    {
        internal HString(string value)
        {
            Check(WindowsCreateString(value, value.Length, out var handle));
            Handle = handle;
        }
        internal nint Handle { get; }
        public void Dispose() => WindowsDeleteString(Handle);
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);
    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();
    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out nint value);
    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);
    [LibraryImport("shlwapi.dll")]
    private static partial nint SHCreateMemStream(byte* initial, uint length);
    [LibraryImport("shcore.dll")]
    private static partial int CreateRandomAccessStreamOverStream(nint stream, uint options, in Guid iid, out nint result);
}

// IID of TypedEventHandler<DataTransferManager, DataRequestedEventArgs>.
[ComVisible(true), Guid("EC6F9CC8-46D0-5E0E-B4D2-7D7773AE37A0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMySnipShareHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class MySnipShareHandler(byte[] png) : IMySnipShareHandler
{
    public int Invoke(nint sender, nint args) => WindowsShare.FillRequest(args, png);
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace MySnip.App.Services;

internal sealed record InstanceCommand(string[] Arguments, string WorkingDirectory);

internal static class InstanceCommands
{
    private const int MaxRequestBytes = 64 * 1024;

    private static string PipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"MySnip.Commands.{process.SessionId}.{identity.User!.Value}";
    }

    public static bool Forward(InstanceCommand command)
    {
        try
        {
            var message = JsonSerializer.SerializeToUtf8Bytes(command);
            if (message.Length > MaxRequestBytes) return false;
            using var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
            pipe.Write(header);
            pipe.Write(message);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        { return false; }
    }

    public static async Task ListenAsync(Action<InstanceCommand> dispatch, CancellationToken cancellationToken)
    {
        var name = PipeName();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, timeout.Token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxRequestBytes) continue;
                var message = new byte[length];
                await pipe.ReadExactlyAsync(message, timeout.Token);
                var command = JsonSerializer.Deserialize<InstanceCommand>(message);
                if (command is { Arguments: not null, WorkingDirectory: not null }) dispatch(command);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
            { Trace.TraceWarning("Cannot read MySnip command: {0}", ex.Message); }
            catch (UnauthorizedAccessException ex)
            { Trace.TraceWarning("Cannot open MySnip command pipe: {0}", ex.Message); return; }
        }
    }
}

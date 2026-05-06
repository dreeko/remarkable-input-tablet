using System.IO.Pipelines;
using RemarkableTablet.Core.Tablet;
using Renci.SshNet;

namespace RemarkableTablet.Core.Transport;

/// <summary>
///     SSH transport that streams /dev/input/event1 from the rM2 into a PipeReader.
///     Uses BeginExecute + OutputStream for continuous binary streaming.
///     OutputStream is a PipeStream whose Read() blocks until data arrives.
///     The blocking read runs on a thread-pool thread so it does not starve the
///     async pipeline.
///     ConnectAsync may be called multiple times (e.g. after reconnection); each
///     call cleans up the previous connection before establishing a new one.
/// </summary>
public sealed class SshTransport : IAsyncDisposable
{
    private readonly ConnectionOptions _opts;
    private SshClient? _client;
    private SshCommand? _command;
    private Pipe? _pipe;
    private Task? _pumpTask;

    public SshTransport(ConnectionOptions opts)
    {
        _opts = opts;
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupConnectionAsync();
    }

    public event Action<ConnectionState>? StateChanged;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await CleanupConnectionAsync();

        StateChanged?.Invoke(ConnectionState.Connecting);

        _client = BuildClient();
        await Task.Run(() => _client.Connect(), ct);

        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 64 * 1024,
            resumeWriterThreshold: 32 * 1024));

        _command = _client.CreateCommand($"cat {ReMarkable2Constants.PenDevicePath}");
        _command.BeginExecute(null, null);

        _pumpTask = Task.Run(() => PumpBlocking(_pipe.Writer, ct));

        StateChanged?.Invoke(ConnectionState.Connected);
    }

    public PipeReader GetReader()
    {
        if (_pipe is null) throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        return _pipe.Reader;
    }

    private void PumpBlocking(PipeWriter writer, CancellationToken ct)
    {
        var stream = _command!.OutputStream;
        var buf = new byte[4096];
        Exception? fault = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = stream.Read(buf, 0, buf.Length);
                if (read == 0) break;

                var mem = writer.GetMemory(read);
                buf.AsMemory(0, read).CopyTo(mem);
                writer.Advance(read);

                var flushTask = writer.FlushAsync(ct).AsTask();
                flushTask.GetAwaiter().GetResult();
                if (flushTask.Result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { fault = ex; }
        finally
        {
            writer.Complete(fault);
        }
    }

    private SshClient BuildClient()
    {
        if (_opts.PrivateKeyPath is not null)
        {
            var key = new PrivateKeyFile(_opts.PrivateKeyPath);
            return new SshClient(_opts.Host, _opts.Port, _opts.Username, key);
        }

        return new SshClient(_opts.Host, _opts.Port, _opts.Username, _opts.Password ?? "");
    }

    private async Task CleanupConnectionAsync()
    {
        // Order matters: dispose the command first so its OutputStream is closed,
        // which unblocks the blocking stream.Read() in PumpBlocking. Disconnecting
        // the SSH client alone does not reliably unblock it in SSH.NET.
        _command?.Dispose();
        _client?.Disconnect();
        _client?.Dispose();
        if (_pipe?.Writer is not null)
            await _pipe.Writer.CompleteAsync();
        if (_pumpTask is not null)
            await _pumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _pumpTask = null;
        _command = null;
        _pipe = null;
        _client = null;
    }
}

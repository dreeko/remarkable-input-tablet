using Renci.SshNet;

namespace RemarkableTablet.Core.Transport;

/// <summary>
///     SSH transport that owns one SshClient and zero-or-more
///     <see cref="SshDeviceStream" />s on it (one per evdev device — pen on
///     event1, touchscreen on event2). Each stream decodes blocking
///     OutputStream bytes into a PipeReader on a thread-pool thread.
///     ConnectAsync may be called multiple times (e.g. after reconnection);
///     each call cleans up the previous connection's streams + client before
///     establishing a new one. Callers must re-open streams after each
///     reconnect — open streams from the previous session do not carry over.
/// </summary>
public sealed class SshTransport : IAsyncDisposable
{
    private readonly ConnectionOptions _opts;
    private readonly List<SshDeviceStream> _streams = new();
    private SshClient? _client;

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

        StateChanged?.Invoke(ConnectionState.Connected);
    }

    /// <summary>
    ///     Opens a `cat &lt;devicePath&gt;` stream on the connected client.
    ///     Multiple streams may be opened concurrently — SSH.NET handles
    ///     each as a separate channel under the same SSH session.
    /// </summary>
    public SshDeviceStream OpenStream(string devicePath, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var stream = new SshDeviceStream(_client, devicePath, ct);
        _streams.Add(stream);
        return stream;
    }

    /// <summary>
    ///     Runs a one-shot command on the connected client and returns its
    ///     stdout, trimmed. Used by <see cref="Devices.DeviceDetector" /> for
    ///     `uname -m`-style probes before the streaming pipeline starts.
    /// </summary>
    public Task<string> RunCommandAsync(string command, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        return Task.Run(() =>
        {
            using var cmd = _client.RunCommand(command);
            return (cmd.Result ?? "").Trim();
        }, ct);
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
        // Order matters (single-stream lesson, generalised to N):
        //   1. Dispose every SshCommand first — closes each OutputStream, which
        //      is what unblocks the blocking stream.Read() in each pump task.
        //      Disconnecting the SshClient alone does not reliably unblock them.
        //   2. Await every pump task with a timeout so the writers are fully
        //      completed (or we move on) before tearing down the client.
        //   3. Then disconnect and dispose the client. Doing this last avoids
        //      Disconnect blocking on still-active channels — if the pumps
        //      have all exited, every channel is closed by then.
        foreach (var s in _streams)
            s.DisposeCommand();

        var pumpTimeout = TimeSpan.FromSeconds(3);
        foreach (var s in _streams)
            await s.AwaitPumpAsync(pumpTimeout);

        try { _client?.Disconnect(); }
        catch
        {
            /* best-effort */
        }

        _client?.Dispose();

        _streams.Clear();
        _client = null;
    }
}

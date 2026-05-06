using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;
using RemarkableTablet.Core.Transport;

namespace RemarkableTablet.Core.Pipeline;

/// <summary>
///     Wires all pipeline stages together and owns the CancellationTokenSource.
///     Pipeline: SshTransport → EvdevParser → TabletStateMachine → CoordinateMapper → IOutputMode
///     Reconnects automatically on disconnect with exponential backoff.
///     Emits a synthetic pen-up before each reconnection attempt so drawing
///     applications don't get a stuck pen-down state.
/// </summary>
public sealed class TabletPipeline : IAsyncDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30];
    private readonly CancellationTokenSource _cts = new();
    private readonly CoordinateMapper _mapper;
    private readonly IOutputMode _output;

    private readonly SshTransport _transport;

    public TabletPipeline(SshTransport transport, CoordinateMapper mapper, IOutputMode output)
    {
        _transport = transport;
        _output = output;
        _mapper = mapper;

        _transport.StateChanged += s => ConnectionStateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _transport.DisposeAsync();
        _output.Dispose();
        _cts.Dispose();
    }

    public event Action<ConnectionState>? ConnectionStateChanged;

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        var attempt = 0;

        _output.Initialize();

        while (!ct.IsCancellationRequested)
        {
            var connectedCleanly = false;
            try
            {
                await RunOnceAsync(ct);
                connectedCleanly = true; // stream ended (device disconnected), not an exception
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Connection or auth error — fall through to reconnect path
            }

            if (ct.IsCancellationRequested) break;

            // Unexpected disconnect: emit pen-up, wait with backoff, then retry.
            EmitPenUp();
            ConnectionStateChanged?.Invoke(ConnectionState.Disconnected);

            // If we actually connected and ran (stream EOF), reset backoff;
            // if we failed immediately (auth error, host unreachable), keep accumulating.
            if (connectedCleanly) attempt = 0;

            var delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            attempt++;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await _transport.ConnectAsync(ct);

        var evdevChannel = Channel.CreateBounded<EvdevEvent>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        var frameChannel = Channel.CreateBounded<PenFrame>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        await Task.WhenAll(
            EvdevParser.RunAsync(_transport.GetReader(), evdevChannel.Writer, ct),
            TabletStateMachine.RunAsync(evdevChannel.Reader, frameChannel.Writer, ct),
            OutputLoopAsync(frameChannel.Reader, ct)
        );
    }

    private async Task OutputLoopAsync(ChannelReader<PenFrame> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
                _output.Send(_mapper.Map(frame));
        }
        catch (OperationCanceledException) { }
    }

    private void EmitPenUp()
    {
        try
        {
            _output.Send(new MappedFrame(0, 0, 0, 0, 0,
                false, false, false, false));
        }
        catch { }
    }

    public void Stop()
    {
        _cts.Cancel();
    }
}

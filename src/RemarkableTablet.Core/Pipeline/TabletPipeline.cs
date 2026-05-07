using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;
using RemarkableTablet.Core.Transport;

namespace RemarkableTablet.Core.Pipeline;

/// <summary>
///     Wires all pipeline stages together and owns the CancellationTokenSource.
///     Pen pipeline:   SshTransport(event1) → EvdevParser → TabletStateMachine → CoordinateMapper → IOutputMode
///     Touch pipeline: SshTransport(event2) → EvdevParser → TouchStateMachine → TouchCoordinateMapper → ITouchOutput
///                     (touch wiring is optional — pen-only operation is unchanged)
///     Reconnects automatically on disconnect with exponential backoff.
///     Emits a synthetic pen-up and "all touch contacts released" before each
///     reconnection attempt so drawing applications don't get stuck pen-down
///     or stuck contacts.
///     Ownership: pipeline disposes the SshTransport, the IOutputMode, and the
///     ITouchOutput (if any) passed in.
/// </summary>
public sealed class TabletPipeline : IAsyncDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30];
    private readonly CancellationTokenSource _cts = new();
    private readonly CoordinateMapper _mapper;
    private readonly IOutputMode _output;
    private readonly TouchCoordinateMapper? _touchMapper;
    private readonly ITouchOutput? _touchOutput;

    private readonly SshTransport _transport;

    public TabletPipeline(SshTransport transport, CoordinateMapper mapper, IOutputMode output)
        : this(transport, mapper, output, null, null) { }

    public TabletPipeline(
        SshTransport transport,
        CoordinateMapper mapper,
        IOutputMode output,
        TouchCoordinateMapper? touchMapper,
        ITouchOutput? touchOutput)
    {
        _transport = transport;
        _output = output;
        _mapper = mapper;
        _touchMapper = touchMapper;
        _touchOutput = touchOutput;

        _transport.StateChanged += s => ConnectionStateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _transport.DisposeAsync();
        _output.Dispose();
        _touchOutput?.Dispose();
        _cts.Dispose();
    }

    public event Action<ConnectionState>? ConnectionStateChanged;

    /// <summary>
    ///     Raised when the pipeline catches an exception while running. Gives
    ///     callers (CLI / App) a chance to surface the failure instead of having
    ///     it disappear into the reconnect loop.
    /// </summary>
    public event Action<Exception>? Error;

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        var attempt = 0;

        _output.Initialize();
        _touchOutput?.Initialize();

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
            catch (Exception ex)
            {
                // Connection or auth error — surface to listeners then fall through to reconnect.
                Error?.Invoke(ex);
            }

            if (ct.IsCancellationRequested) break;

            // Unexpected disconnect: emit pen-up + all-touch-released, wait with backoff, then retry.
            EmitPenUp();
            EmitTouchReleaseAll();
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

        var penStream = _transport.OpenStream(ReMarkable2Constants.PenDevicePath, ct);

        // Pen channels — evdev events at ~100 Hz; unbounded is cheap and
        // avoids mid-frame loss that would corrupt the next emitted PenFrame.
        var penEvdevChannel = Channel.CreateUnbounded<EvdevEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var penFrameChannel = Channel.CreateBounded<PenFrame>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        var tasks = new List<Task>(6)
        {
            EvdevParser.RunAsync(penStream.Reader, penEvdevChannel.Writer, ct),
            TabletStateMachine.RunAsync(penEvdevChannel.Reader, penFrameChannel.Writer, ct),
            PenOutputLoopAsync(penFrameChannel.Reader, ct)
        };

        if (_touchMapper is not null && _touchOutput is not null)
        {
            var touchStream = _transport.OpenStream(ReMarkable2Constants.TouchDevicePath, ct);

            var touchEvdevChannel = Channel.CreateUnbounded<EvdevEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

            var touchFrameChannel = Channel.CreateBounded<TouchFrame>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

            tasks.Add(EvdevParser.RunAsync(touchStream.Reader, touchEvdevChannel.Writer, ct));
            tasks.Add(TouchStateMachine.RunAsync(touchEvdevChannel.Reader, touchFrameChannel.Writer, ct));
            tasks.Add(TouchOutputLoopAsync(touchFrameChannel.Reader, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task PenOutputLoopAsync(ChannelReader<PenFrame> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
                _output.Send(_mapper.Map(frame));
        }
        catch (OperationCanceledException) { }
    }

    private async Task TouchOutputLoopAsync(ChannelReader<TouchFrame> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
                _touchOutput!.Send(_touchMapper!.Map(frame));
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
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private void EmitTouchReleaseAll()
    {
        if (_touchOutput is null) return;
        try
        {
            _touchOutput.ReleaseAll();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    public void Stop()
    {
        // CTS may already be disposed if RunAsync has finished; tolerate that.
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

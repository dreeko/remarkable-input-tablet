using System.Threading.Channels;
using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Evdev;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Core.Tablet;
using RemarkableTablet.Core.Transport;

namespace RemarkableTablet.Core.Pipeline;

/// <summary>
///     Wires all pipeline stages together and owns the CancellationTokenSource.
///     Pen pipeline:   SshTransport(event1) → EvdevParser → TabletStateMachine → CoordinateMapper → IOutputMode
///     Touch pipeline: SshTransport(event2) → EvdevParser → TouchStateMachine → TouchCoordinateMapper →
///     PenProximityGate → ITouchOutput
///     (touch wiring is optional — pen-only operation is unchanged)
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
    private readonly PenProximityGate _gate = new();

    // Serialises access to the touch sink: reached from both output loops.
    private readonly object _touchSink = new();
    private readonly CoordinateMapper _mapper;
    private readonly IOutputMode _output;
    private readonly DeviceProfile _profile;
    private readonly TouchOptions _touchOptions;

    private readonly TouchCoordinateMapper? _touchMapper;
    private readonly ITouchOutput? _touchOutput;

    private readonly SshTransport _transport;

    // Counters from state machines of earlier connections, so the totals survive
    // a reconnect.
    private TouchDiagnostics _retiredTouchStats = new(0, 0, 0);
    private TouchStateMachine? _touchStateMachine;

    public TabletPipeline(
        SshTransport transport,
        DeviceProfile profile,
        CoordinateMapper mapper,
        IOutputMode output)
        : this(transport, profile, mapper, output, null, null)
    {
    }

    public TabletPipeline(
        SshTransport transport,
        DeviceProfile profile,
        CoordinateMapper mapper,
        IOutputMode output,
        TouchCoordinateMapper? touchMapper,
        ITouchOutput? touchOutput,
        TouchOptions? touchOptions = null)
    {
        _transport = transport;
        _profile = profile;
        _output = output;
        _mapper = mapper;
        _touchMapper = touchMapper;
        _touchOutput = touchOutput;
        _touchOptions = touchOptions ?? TouchOptions.ForProfile(profile);

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

    /// <summary>
    ///     Palm-rejection and slot-pool counters, cumulative across reconnects.
    ///     All three should normally be low; a climbing
    ///     <see cref="TouchDiagnostics.DroppedContacts" /> means contacts are being
    ///     filtered or the slot pool is saturating, and a climbing
    ///     <see cref="TouchDiagnostics.StaleReleases" /> means the firmware is
    ///     abandoning contacts without releasing them.
    /// </summary>
    public TouchDiagnostics TouchStats
    {
        get
        {
            var sm = _touchStateMachine;
            return new TouchDiagnostics(
                _retiredTouchStats.DroppedContacts + (sm?.DroppedContacts ?? 0),
                _retiredTouchStats.StaleReleases + (sm?.StaleReleases ?? 0),
                _gate.CloseCount);
        }
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

        var penStream = _transport.OpenStream(_profile.PenDevicePath, ct);

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
            EvdevParser.RunAsync(penStream.Reader, penEvdevChannel.Writer, _profile.EventLayout, ct),
            TabletStateMachine.RunAsync(penEvdevChannel.Reader, penFrameChannel.Writer, ct),
            PenOutputLoopAsync(penFrameChannel.Reader, ct)
        };

        if (_touchMapper is not null && _touchOutput is not null)
        {
            var touchStream = _transport.OpenStream(_profile.TouchDevicePath, ct);

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

            // Held so its counters can be read for diagnostics; a fresh one per
            // connection, with the retired totals folded into _retiredTouchStats.
            var touchSm = new TouchStateMachine(_touchOptions);
            _touchStateMachine = touchSm;

            tasks.Add(EvdevParser.RunAsync(touchStream.Reader, touchEvdevChannel.Writer, _profile.EventLayout, ct));
            tasks.Add(touchSm.RunLoopAsync(touchEvdevChannel.Reader, touchFrameChannel.Writer, ct));
            tasks.Add(TouchOutputLoopAsync(touchFrameChannel.Reader, ct));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            RetireTouchStats();
        }
    }

    private void RetireTouchStats()
    {
        var sm = _touchStateMachine;
        if (sm is null) return;
        _retiredTouchStats = new TouchDiagnostics(
            _retiredTouchStats.DroppedContacts + sm.DroppedContacts,
            _retiredTouchStats.StaleReleases + sm.StaleReleases,
            0);
        _touchStateMachine = null;
    }

    private async Task PenOutputLoopAsync(ChannelReader<PenFrame> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
            {
                var mapped = _mapper.Map(frame);

                // Palm rejection is driven from here, not from the touch loop:
                // when the pen enters proximity the panel goes quiet, so the touch
                // loop may not run again until long after the release is needed.
                _gate.OnPenFrame(mapped);
                if (_touchOutput is not null && _gate.TakePendingRelease())
                    ReleaseTouchContacts();

                _output.Send(mapped);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TouchOutputLoopAsync(ChannelReader<TouchFrame> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct))
            {
                var mapped = _touchMapper!.Map(frame);

                // Second half of the gate: for a device that keeps reporting touch
                // while the pen is down, drop those frames here too. The release
                // itself is the pen loop's job — see PenProximityGate.
                var gated = _gate.Filter(mapped);
                if (gated is null) continue;

                lock (_touchSink)
                {
                    _touchOutput!.Send(gated);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     Drop every contact the touch sink is holding. Both loops can reach the
    ///     sink — the pen loop on gate closure, the touch loop on every frame — and
    ///     no <see cref="ITouchOutput" /> implementation is thread-safe (each keeps
    ///     per-slot state and writes a device handle), hence the shared lock.
    /// </summary>
    private void ReleaseTouchContacts()
    {
        if (_touchOutput is null) return;
        lock (_touchSink)
        {
            _touchOutput.ReleaseAll();
        }
    }

    private void EmitPenUp()
    {
        try
        {
            _output.Send(new MappedFrame(0, 0, 0, 0, 0, 0,
                false, false, false, false));
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private void EmitTouchReleaseAll()
    {
        try
        {
            ReleaseTouchContacts();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    public void Stop()
    {
        // CTS may already be disposed if RunAsync has finished; tolerate that.
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
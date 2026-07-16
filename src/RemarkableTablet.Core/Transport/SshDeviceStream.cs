using System.IO.Pipelines;
using Renci.SshNet;

namespace RemarkableTablet.Core.Transport;

/// <summary>
///     One `cat &lt;devicePath&gt;` stream on an existing SshClient. Decodes the
///     blocking OutputStream into a PipeReader on a thread-pool thread.
///     A single SshClient hosts multiple SshDeviceStreams concurrently — one
///     per evdev device (e.g. event1 for the pen, event2 for the touchscreen).
///     Lifecycle is owned by the parent SshTransport: <see cref="DisposeCommand" />
///     must be called before the SshClient is disconnected, otherwise the
///     blocking Read in PumpBlocking will not unblock and the pump task will
///     not complete. <see cref="AwaitPumpAsync" /> then waits for the pump
///     to finish so the writer is fully completed before the next connection.
/// </summary>
public sealed class SshDeviceStream
{
    private readonly SshCommand _command;
    private readonly Pipe _pipe;
    private readonly Task _pumpTask;

    internal SshDeviceStream(SshClient client, string devicePath, CancellationToken ct)
    {
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 64 * 1024,
            resumeWriterThreshold: 32 * 1024));

        _command = client.CreateCommand($"cat {devicePath}");
        _command.BeginExecute(null, null);

        _pumpTask = Task.Run(() => PumpBlocking(_pipe.Writer, ct));
    }

    public PipeReader Reader => _pipe.Reader;

    /// <summary>
    ///     Disposes the underlying SshCommand. This closes its OutputStream,
    ///     which is what unblocks the blocking <c>stream.Read()</c> inside
    ///     <see cref="PumpBlocking" />. Disconnecting the SSH client alone
    ///     does not reliably unblock it in SSH.NET. Swallows exceptions —
    ///     a misbehaving command should never block transport teardown.
    /// </summary>
    internal void DisposeCommand()
    {
        try
        {
            _command.Dispose();
        }
        catch
        {
            /* best-effort — we're tearing down anyway */
        }
    }

    /// <summary>
    ///     Awaits the pump task with a timeout. If the pump is wedged
    ///     (e.g. SSH.NET failed to propagate command Dispose to the
    ///     underlying PipeStream and Read is stuck), we'd rather move
    ///     on and let the orphaned task be GC'd than hang the GUI.
    /// </summary>
    internal async Task AwaitPumpAsync(TimeSpan timeout)
    {
        var task = _pumpTask.ContinueWith(_ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var winner = await Task.WhenAny(task, Task.Delay(timeout));
        // If timeout won, the pump task is still alive but unreachable
        // from our perspective; nothing useful to do. Caller proceeds.
        _ = winner;
    }

    private void PumpBlocking(PipeWriter writer, CancellationToken ct)
    {
        var stream = _command.OutputStream;
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            writer.Complete(fault);
        }
    }
}
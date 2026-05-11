using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using RemarkableTablet.Core.Devices;

namespace RemarkableTablet.Core.Evdev;

/// <summary>
///     Reads the raw evdev byte stream from a PipeReader and writes decoded
///     EvdevEvents to a channel. Runs as a long-lived async loop.
///     <para>
///         The byte layout of <c>struct input_event</c> depends on the device's
///         userspace bitness — 16 bytes on 32-bit ARM (rM2), 24 bytes on 64-bit
///         ARM (rMPP). The caller supplies an <see cref="EvdevLayout" /> with
///         the appropriate struct size and field offsets. The HHi tail is
///         identical on both ABIs; only the leading timeval differs.
///     </para>
///     All fields are little-endian.
/// </summary>
public static class EvdevParser
{
    public static async Task RunAsync(
        PipeReader reader,
        ChannelWriter<EvdevEvent> output,
        EvdevLayout layout,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAtLeastAsync(layout.StructSize, ct);
                var buffer = result.Buffer;

                while (buffer.Length >= layout.StructSize)
                {
                    var ev = Parse(buffer.Slice(0, layout.StructSize), layout);
                    buffer = buffer.Slice(layout.StructSize);
                    await output.WriteAsync(ev, ct);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            output.TryComplete();
            // Signal the pump (PipeWriter side) that no more reads will happen.
            // Without this, a pump blocked in FlushAsync waiting for the reader
            // to drain has no way to know the reader has gone away — it would
            // only unblock when the underlying SshCommand is disposed. Completing
            // the reader makes the next FlushAsync return IsCompleted=true so
            // the pump exits cleanly.
            try { reader.Complete(); }
            catch
            {
                /* idempotent — already completed */
            }
        }
    }

    private static EvdevEvent Parse(ReadOnlySequence<byte> slice, EvdevLayout layout)
    {
        // Hot path — no copy
        if (slice.IsSingleSegment)
            return ParseSpan(slice.FirstSpan, layout);

        Span<byte> scratch = stackalloc byte[layout.StructSize];
        slice.CopyTo(scratch);
        return ParseSpan(scratch, layout);
    }

    private static EvdevEvent ParseSpan(ReadOnlySpan<byte> span, EvdevLayout layout)
    {
        var type = BinaryPrimitives.ReadUInt16LittleEndian(span[layout.TypeOffset..]);
        var code = BinaryPrimitives.ReadUInt16LittleEndian(span[layout.CodeOffset..]);
        var value = BinaryPrimitives.ReadInt32LittleEndian(span[layout.ValueOffset..]);
        return new EvdevEvent(type, code, value);
    }
}
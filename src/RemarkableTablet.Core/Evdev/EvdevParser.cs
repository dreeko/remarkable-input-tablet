using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;

namespace RemarkableTablet.Core.Evdev;

/// <summary>
///     Reads the raw evdev byte stream from a PipeReader and writes decoded
///     EvdevEvents to a channel. Runs as a long-lived async loop.
///     On 32-bit ARM (rM2 / i.MX7D), input_event is 16 bytes:
///     [0..3]  uint32 sec
///     [4..7]  uint32 usec
///     [8..9]  uint16 type
///     [10..11] uint16 code
///     [12..15] int32 value
///     All fields are little-endian.
/// </summary>
public static class EvdevParser
{
    public const int EventSize = 16;

    public static async Task RunAsync(
        PipeReader reader,
        ChannelWriter<EvdevEvent> output,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAtLeastAsync(EventSize, ct);
                var buffer = result.Buffer;

                while (buffer.Length >= EventSize)
                {
                    var ev = Parse(buffer.Slice(0, EventSize));
                    buffer = buffer.Slice(EventSize);
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
            try { reader.Complete(); } catch { /* idempotent — already completed */ }
        }
    }

    private static EvdevEvent Parse(ReadOnlySequence<byte> slice)
    {
        // Hot path — no copy
        if (slice.IsSingleSegment)
            return ParseSpan(slice.FirstSpan);

        Span<byte> scratch = stackalloc byte[EventSize];
        slice.CopyTo(scratch);
        return ParseSpan(scratch);
    }

    private static EvdevEvent ParseSpan(ReadOnlySpan<byte> span)
    {
        // Skip sec (0..3) and usec (4..7) — not needed for injection
        var type = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var code = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        var value = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        return new EvdevEvent(type, code, value);
    }
}
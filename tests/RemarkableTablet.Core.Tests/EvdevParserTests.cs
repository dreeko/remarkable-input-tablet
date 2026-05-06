using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using RemarkableTablet.Core.Evdev;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class EvdevParserTests
{
    /// <summary>
    ///     Builds a synthetic 16-byte evdev event.
    ///     Layout: sec(4) usec(4) type(2) code(2) value(4) — little-endian
    /// </summary>
    private static byte[] MakeEvent(ushort type, ushort code, int value)
    {
        var buf = new byte[16];
        // sec/usec = 0
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), code);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), value);
        return buf;
    }

    [Fact]
    public async Task ParsesSingleEvent()
    {
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        await pipe.Writer.WriteAsync(MakeEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_X, 12345));
        pipe.Writer.Complete();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, CancellationToken.None);

        var events = new List<EvdevEvent>();
        await foreach (var ev in channel.Reader.ReadAllAsync())
            events.Add(ev);

        Assert.Single(events);
        Assert.Equal(EvdevTypes.EV_ABS, events[0].Type);
        Assert.Equal(EvdevCodes.ABS_X, events[0].Code);
        Assert.Equal(12345, events[0].Value);
    }

    [Fact]
    public async Task ParsesMultipleEventsInSequence()
    {
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        // Write a full frame: ABS_X, ABS_Y, ABS_PRESSURE, BTN_TOUCH, SYN_REPORT
        byte[] data =
        [
            .. MakeEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_X, 10000),
            .. MakeEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_Y, 8000),
            .. MakeEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_PRESSURE, 2048),
            .. MakeEvent(EvdevTypes.EV_KEY, EvdevCodes.BTN_TOUCH, 1),
            .. MakeEvent(EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0)
        ];

        await pipe.Writer.WriteAsync(data);
        pipe.Writer.Complete();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, CancellationToken.None);

        var events = new List<EvdevEvent>();
        await foreach (var ev in channel.Reader.ReadAllAsync())
            events.Add(ev);

        Assert.Equal(5, events.Count);
        Assert.Equal(EvdevCodes.SYN_REPORT, events[4].Code);
    }

    [Fact]
    public async Task HandlesChunkedReads()
    {
        // Simulate the SSH stream delivering bytes in small chunks
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        var fullEvent = MakeEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_PRESSURE, 999);

        // Write in 3-byte chunks
        for (var i = 0; i < fullEvent.Length; i += 3)
        {
            var len = Math.Min(3, fullEvent.Length - i);
            await pipe.Writer.WriteAsync(fullEvent.AsMemory(i, len));
            await pipe.Writer.FlushAsync();
        }

        pipe.Writer.Complete();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, CancellationToken.None);

        var events = new List<EvdevEvent>();
        await foreach (var ev in channel.Reader.ReadAllAsync())
            events.Add(ev);

        Assert.Single(events);
        Assert.Equal(999, events[0].Value);
    }

    [Fact]
    public async Task ParsesFixtureFileIfPresent()
    {
        const string fixturePath = "../../../../../fixtures/pen_capture.bin";
        if (!File.Exists(fixturePath))
        {
            // Skip if fixture not yet captured (Phase 0)
            return;
        }

        var bytes = await File.ReadAllBytesAsync(fixturePath);
        Assert.True(bytes.Length % EvdevParser.EventSize == 0,
            $"Fixture size {bytes.Length} is not a multiple of {EvdevParser.EventSize}. " +
            "If it's a multiple of 24, the device runs 64-bit userspace — update EventStructSize.");

        // Write concurrently with the parser — synchronous pre-write deadlocks for
        // large fixtures because Pipe's default pauseWriterThreshold is 65536 bytes.
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        var writeTask = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(bytes);
            pipe.Writer.Complete();
        });

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, CancellationToken.None);
        await writeTask;

        var count = 0;
        await foreach (var ev in channel.Reader.ReadAllAsync())
        {
            Assert.True(ev.Type <= 31, $"Unexpected event type {ev.Type}");
            count++;
        }

        Assert.True(count > 0, "Fixture produced no events");
    }
}
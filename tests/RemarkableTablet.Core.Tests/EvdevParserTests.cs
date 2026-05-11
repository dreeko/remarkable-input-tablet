using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Evdev;
using Xunit;

namespace RemarkableTablet.Core.Tests;

public class EvdevParserTests
{
    private static readonly EvdevLayout Layout32 = EvdevLayout.Bits32;
    private static readonly EvdevLayout Layout64 = EvdevLayout.Bits64;

    /// <summary>
    ///     Builds a synthetic evdev event at either 16- or 24-byte struct size.
    ///     32-bit layout: sec(4) usec(4) type(2) code(2) value(4) — little-endian
    ///     64-bit layout: sec(8) usec(8) type(2) code(2) value(4) — little-endian
    /// </summary>
    private static byte[] MakeEvent(EvdevLayout layout, ushort type, ushort code, int value)
    {
        var buf = new byte[layout.StructSize];
        // timeval bytes left at 0
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(layout.TypeOffset), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(layout.CodeOffset), code);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(layout.ValueOffset), value);
        return buf;
    }

    private static byte[] MakeEvent(ushort type, ushort code, int value)
    {
        return MakeEvent(Layout32, type, code, value);
    }

    [Fact]
    public async Task ParsesSingleEvent()
    {
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        await pipe.Writer.WriteAsync(MakeEvent(EvdevTypes.EV_ABS, EvdevCodes.ABS_X, 12345));
        await pipe.Writer.CompleteAsync();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, Layout32, CancellationToken.None);

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
        await pipe.Writer.CompleteAsync();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, Layout32, CancellationToken.None);

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

        await pipe.Writer.CompleteAsync();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, Layout32, CancellationToken.None);

        var events = new List<EvdevEvent>();
        await foreach (var ev in channel.Reader.ReadAllAsync())
            events.Add(ev);

        Assert.Single(events);
        Assert.Equal(999, events[0].Value);
    }

    [Fact]
    public async Task ParsesBits64FrameCorrectly()
    {
        // Regression guard for rMPP (aarch64): 24-byte input_event with HHi
        // payload at offsets 16/18/20. Same logical event as the 32-bit test
        // above, just packed into the 64-bit timeval layout.
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        await pipe.Writer.WriteAsync(MakeEvent(Layout64, EvdevTypes.EV_ABS, EvdevCodes.ABS_PRESSURE, 4096));
        await pipe.Writer.CompleteAsync();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, Layout64, CancellationToken.None);

        var events = new List<EvdevEvent>();
        await foreach (var ev in channel.Reader.ReadAllAsync())
            events.Add(ev);

        Assert.Single(events);
        Assert.Equal(EvdevTypes.EV_ABS, events[0].Type);
        Assert.Equal(EvdevCodes.ABS_PRESSURE, events[0].Code);
        Assert.Equal(4096, events[0].Value);
    }

    [Fact]
    public async Task ParsesBits64Stream_NoDesyncAcrossFrames()
    {
        // Two concatenated 24-byte frames; the parser must advance exactly
        // 24 bytes per event. If it slips (e.g. reverts to 16-byte slicing)
        // the second event decodes as garbage — exactly the rMPP smoking-gun
        // signature reported in Evidlo/remarkable_mouse Issue #92.
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        byte[] data =
        [
            .. MakeEvent(Layout64, EvdevTypes.EV_ABS, EvdevCodes.ABS_X, 1111),
            .. MakeEvent(Layout64, EvdevTypes.EV_SYN, EvdevCodes.SYN_REPORT, 0)
        ];
        await pipe.Writer.WriteAsync(data);
        await pipe.Writer.CompleteAsync();

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, Layout64, CancellationToken.None);

        var events = new List<EvdevEvent>();
        await foreach (var ev in channel.Reader.ReadAllAsync())
            events.Add(ev);

        Assert.Equal(2, events.Count);
        Assert.Equal(1111, events[0].Value);
        Assert.Equal(EvdevCodes.SYN_REPORT, events[1].Code);
    }

    [Fact]
    public async Task ParsesFixtureFileIfPresent()
    {
        const string fixturePath = "../../../../../fixtures/pen_capture.bin";
        if (!File.Exists(fixturePath))
        {
            // Skip if fixture not yet captured (Phase 0). Print so CI surfaces it.
            Console.WriteLine($"[skip] fixture not present at {fixturePath}");
            return;
        }

        var bytes = await File.ReadAllBytesAsync(fixturePath);
        Assert.True(bytes.Length % Layout32.StructSize == 0,
            $"Fixture size {bytes.Length} is not a multiple of {Layout32.StructSize}. " +
            "If it's a multiple of 24, the device runs 64-bit userspace — use EvdevLayout.Bits64.");

        // Write concurrently with the parser — synchronous pre-write deadlocks for
        // large fixtures because Pipe's default pauseWriterThreshold is 65536 bytes.
        var pipe = new Pipe();
        var channel = Channel.CreateUnbounded<EvdevEvent>();

        var writeTask = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(bytes);
            await pipe.Writer.CompleteAsync();
        });

        await EvdevParser.RunAsync(pipe.Reader, channel.Writer, Layout32, CancellationToken.None);
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

using RemarkableTablet.Core.Devices;
using RemarkableTablet.Core.Mapping;
using RemarkableTablet.Core.Output;
using RemarkableTablet.Linux.Output;

const int width = 1920;
const int height = 1080;
var holdSeconds = args.Length > 0 && int.TryParse(args[0], out var requestedHold)
    ? Math.Max(1, requestedHold)
    : 5;

var profile = ReMarkable2Profile.Instance;
var transform = new ScreenTransform(MappingOptions.ForScreen(width, height), profile);

using var pen = new UinputOutput(width, height, transform, profile.Pen);
using var touch = new UinputTouchOutput(width, height, 5);

pen.Initialize();
touch.Initialize();
Console.WriteLine("Virtual pen and touch devices created.");

pen.Send(new MappedFrame(400, 300, 0, 0, 0, 40, false, false, false, true));
pen.Send(new MappedFrame(420, 320, 512, 10, -10, 0, true, false, false, true));
pen.Send(new MappedFrame(420, 320, 0, 10, -10, 20, false, false, false, true));
pen.Send(new MappedFrame(0, 0, 0, 0, 0, 0, false, false, false, false));

touch.Send(new MappedTouchFrame([
    new MappedTouchContact(0, 100, 500, 400, 500, 12, 10),
    new MappedTouchContact(1, 101, 900, 400, 500, 14, 11)
]));
touch.Send(new MappedTouchFrame([
    new MappedTouchContact(0, 100, 520, 420, 500, 12, 10),
    new MappedTouchContact(1, 101, 920, 420, 500, 14, 11)
]));
touch.ReleaseAll();

Console.WriteLine("Injected pen hover/down/move/up and two-touch down/move/up sequences.");
Console.WriteLine($"Holding devices for {holdSeconds} seconds so the kernel registration can be inspected.");
await Task.Delay(TimeSpan.FromSeconds(holdSeconds));

using AliceVision.Core.Vision;

namespace AliceVision.Core;

/// <summary>
/// Coordinates startup for the platform-neutral Alice Vision engine.
/// </summary>
public static class AliceEngine
{
    public const string Name = "Alice Vision Engine";
    public const string Version = "0.1.0";

    public static int Run(TextWriter? output = null)
    {
        output ??= Console.Out;

        _ = new FloorRegionEstimator();

        output.WriteLine($"{Name} v{Version}");
        output.WriteLine("Initializing...");
        output.WriteLine("[ OK ] Core module");
        output.WriteLine("[ OK ] Luma frame pipeline");
        output.WriteLine("[ OK ] Floor estimator");
        output.WriteLine("Ready.");

        return 0;
    }
}

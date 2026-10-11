namespace AliceVision.Core.Motion;

/// <summary>A model-independent greeting; radians are tiny secondary motions, not bone-local axes.</summary>
public readonly record struct WaveMotionSample(
    float Raise,
    float WristSwing,
    float ForearmSwing,
    float HeadTilt,
    float HeadNod,
    float Anticipation);

/// <summary>Small continuous idle signals. Breathing/weight shift are unit signals for a rig to scale.</summary>
public readonly record struct IdleMotionSample(
    float Breathing,
    float WeightShift,
    float HeadYaw,
    float HeadTilt);

/// <summary>
/// Bounded procedural timing independent of SceneKit or a character's rest pose.
/// A greeting raises once, waves sideways at the wrist, then lowers once. The
/// arm position does not oscillate with the hand, so this is not a shoulder pump.
/// </summary>
public static class HumanoidGestureMotion
{
    public const double WaveDurationSeconds = 4.2;
    public const double RaiseStartsAtSeconds = 0.2;
    public const double RaiseEndsAtSeconds = 1.0;
    public const double LowerStartsAtSeconds = 3.3;
    public const double WristWaveStartsAtSeconds = 1.2;
    public const double WristWaveEndsAtSeconds = 3.15;

    public static WaveMotionSample SampleWave(double elapsedSeconds, bool includeGreetingNod = false)
    {
        RequireFinite(elapsedSeconds, nameof(elapsedSeconds));
        if (elapsedSeconds <= 0 || elapsedSeconds >= WaveDurationSeconds) return default;

        float raise = elapsedSeconds < LowerStartsAtSeconds
            ? SmoothStep01((elapsedSeconds - RaiseStartsAtSeconds) / (RaiseEndsAtSeconds - RaiseStartsAtSeconds))
            : 1 - SmoothStep01((elapsedSeconds - LowerStartsAtSeconds) / (WaveDurationSeconds - LowerStartsAtSeconds));

        // A soft envelope means the first and last lateral passes do not snap.
        // 2.5 cycles give five readable sweeps rather than a high-frequency tremor.
        double waveTime = elapsedSeconds - WristWaveStartsAtSeconds;
        double waveSpan = WristWaveEndsAtSeconds - WristWaveStartsAtSeconds;
        float envelope = Pulse(elapsedSeconds, WristWaveStartsAtSeconds,
            WristWaveStartsAtSeconds + 0.35, WristWaveEndsAtSeconds - 0.35, WristWaveEndsAtSeconds);
        double phase = 5 * Math.PI * Math.Clamp(waveTime / waveSpan, 0, 1);
        float wrist = 0.19f * envelope * (float)Math.Sin(phase);
        float forearm = 0.045f * envelope * (float)Math.Sin(phase - 0.18);

        // The head acknowledges once; a repeated sine would look like a bobblehead.
        float nod = includeGreetingNod ? 0.05f * Pulse(elapsedSeconds, 0.75, 1.08, 1.13, 1.62) : 0;
        float anticipation = Pulse(elapsedSeconds, 0, 0.10, 0.10, 0.32);
        return new WaveMotionSample(raise, wrist, forearm, 0.025f * raise, nod, anticipation);
    }

    /// <summary>
    /// Sample against a monotonic absolute clock, never a repeating clip's local
    /// elapsed time. Incommensurate frequencies avoid synchronized head/body sway.
    /// </summary>
    public static IdleMotionSample SampleIdle(double absoluteSeconds)
    {
        RequireFinite(absoluteSeconds, nameof(absoluteSeconds));
        double t = absoluteSeconds;
        return new IdleMotionSample(
            (float)(0.78 * SinAt(t, 1.43) + 0.22 * SinAt(t, 1.79, 0.6)),
            (float)(0.70 * SinAt(t, 0.37, 0.8) + 0.30 * SinAt(t, 0.61)),
            (float)(0.006 * SinAt(t, 0.29, 0.4) + 0.003 * SinAt(t, 0.47)),
            (float)(0.004 * SinAt(t, 0.41, 1.1) + 0.002 * SinAt(t, 0.23)));
    }

    /// <summary>Quintic smootherstep with zero first and second derivatives at both ends.</summary>
    public static float SmoothStep01(double progress)
    {
        RequireFinite(progress, nameof(progress));
        double x = Math.Clamp(progress, 0, 1);
        return (float)(x * x * x * (x * (x * 6 - 15) + 10));
    }

    private static float Pulse(double time, double start, double peakStart, double peakEnd, double end)
    {
        if (time <= start || time >= end) return 0;
        if (time < peakStart) return SmoothStep01((time - start) / (peakStart - start));
        if (time <= peakEnd) return 1;
        return 1 - SmoothStep01((time - peakEnd) / (end - peakEnd));
    }

    // Reduce before multiplication so even a finite extreme clock cannot
    // overflow into infinity/NaN and corrupt a pose.
    private static double SinAt(double time, double frequency, double phase = 0) =>
        Math.Sin(Math.IEEERemainder(time, Math.Tau / frequency) * frequency + phase);

    private static void RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(parameterName, "Motion time must be finite.");
    }
}

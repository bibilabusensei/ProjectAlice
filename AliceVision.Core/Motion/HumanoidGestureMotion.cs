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
        // The first forearm pass must also be outward. A negative phase lag
        // previously moved the wrist inward just as a greeting began.
        float forearm = 0.030f * envelope * (float)Math.Sin(phase);

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

    public static double Duration(string action) => action switch
    {
        "idle" or "walk" => double.PositiveInfinity, "dance" => 4.5,
        "bow" => 2.8, "nod" => 2.2, "look" => 3.4, "welcome" => 4.2, _ => WaveDurationSeconds
    };

    public static double TransitionDuration(string action) => action == "idle" ? 0.85 : 0.65;

    public static float ActivityEnvelope(double time, double duration) =>
        SmoothStep01(time / 0.65) * SmoothStep01((duration - time) / 0.75);

    public static float GesturePulse(double time, double start, double up, double hold, double end) =>
        Pulse(time, start, up, hold, end);

    /// <summary>Production head/torso/leg deltas, also used by the local preview.</summary>
    public static System.Numerics.Vector3 SampleBoneAngles(string bone, string action, double time, double clock)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        if (time >= Duration(action)) action = "idle";
        var idle = SampleIdle(clock);
        float x = 0, y = 0, z = 0;
        if (bone == "head") { x = 0.003f * idle.Breathing; y = idle.HeadYaw; z = idle.HeadTilt; }
        if (bone == "spine") { x = 0.005f * idle.Breathing; z = 0.003f * idle.WeightShift; }
        float gait = (float)Math.Sin(time * Math.Tau / 1.25);
        if (action == "walk")
        {
            // With +Z forward, negative X swings a down-pointing thigh
            // forward; positive X bends the knee backwards, never forwards.
            if (bone == "leftUpperLeg") x = -0.16f * gait;
            if (bone == "rightUpperLeg") x = 0.16f * gait;
            if (bone == "leftLowerLeg") x = 0.19f * MathF.Max(0, gait);
            if (bone == "rightLowerLeg") x = 0.19f * MathF.Max(0, -gait);
            if (bone == "leftFoot") x = 0.16f * gait - 0.19f * MathF.Max(0, gait);
            if (bone == "rightFoot") x = -0.16f * gait - 0.19f * MathF.Max(0, -gait);
        }
        if (action is "wave" or "wave_left" or "wave_right" or "greet")
        {
            var wave = SampleWave(time, action == "greet");
            float side = action == "wave_left" ? 1 : -1;
            if (bone == "head") { x += wave.HeadNod; z += side * 0.018f * wave.Raise; y += side * 0.035f * wave.Raise; }
            if (bone == "spine")
            {
                z -= side * 0.012f * wave.Raise; y += side * 0.020f * wave.Raise;
                x -= 0.007f * wave.Anticipation;
            }
        }
        if (action == "welcome")
        {
            float open = Pulse(time, 0.18, 1.18, 2.25, 3.75);
            if (bone == "spine") x -= 0.015f * open;
            if (bone == "head") x += 0.045f * Pulse(time, 0.90, 1.30, 1.40, 2.10);
        }
        if (action == "nod")
        {
            if (bone == "head") x += 0.13f * Pulse(time, 0.25, 0.80, 0.95, 1.85);
            if (bone == "neck") x += 0.025f * Pulse(time, 0.30, 0.85, 1.00, 1.90);
        }
        if (action == "bow")
        {
            float bend = Pulse(time, 0.20, 1.00, 1.30, 2.50);
            if (bone == "spine") x += 0.12f * bend;
            if (bone == "chest") x += 0.045f * bend;
            if (bone == "head") x += 0.045f * Pulse(time, 0.30, 1.08, 1.35, 2.55);
        }
        if (action == "look" && bone == "head")
            y += 0.18f * Pulse(time, 0.25, 0.85, 1.10, 1.65) - 0.13f * Pulse(time, 1.55, 2.20, 2.40, 3.20);
        if (action == "dance")
        {
            float envelope = ActivityEnvelope(time, 4.5), beat = (float)Math.Sin(time * 2.4);
            if (bone == "spine") z += 0.020f * beat * envelope;
            if (bone == "head") y += 0.018f * (float)Math.Sin(time * 2.4 - 0.4) * envelope;
            // No independent leg pistons while the feet are supposed to be planted.
        }
        return new(x, y, z);
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

using AliceVision.Core.Motion;

var tests = new (string Name, Action Run)[]
{
    ("wave has neutral start/end and clamps elapsed time", WaveEndpoints),
    ("arm raises and lowers exactly once with a fixed hold", OneArmRaise),
    ("all gesture signals remain in gentle limits", GestureLimits),
    ("wrist waves laterally in five soft passes", WristPasses),
    ("wrist wave happens only after the arm is raised", WristTiming),
    ("wave does not bob the shoulder during wrist motion", NoShoulderPumping),
    ("gesture endpoints have no velocity jump", EndpointVelocity),
    ("gesture phase boundaries are continuous with smooth joins", PhaseContinuity),
    ("greeting nod is one positive acknowledgement", SingleNod),
    ("plain wave does not force an unnecessary head nod", PlainWave),
    ("anticipation ends before the wrist wave", AnticipationTiming),
    ("quintic easing is bounded monotone and softly stopped", QuinticEasing),
    ("idle is continuous across former repeating clip boundary", ContinuousIdle),
    ("idle head and body signals are not synchronized", IdleIndependentSignals),
    ("idle amplitude remains bounded over long runtime", IdleLimits),
    ("motion samples are deterministic across render rates", DeterministicSamples),
    ("nonfinite clocks are rejected before producing bone rotations", NonfiniteTimes),
};

int failures = 0, count = 0;
foreach (var test in tests.Concat(ArmPoseTests.Cases))
{
    count++;
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
}
Console.WriteLine($"{count - failures}/{count} humanoid motion tests passed; these check timing/math, not device rendering.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}
static void Near(float actual, float expected, float tolerance = 0.0001f) => Check(MathF.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}.");
static float MaximumSignalDelta(WaveMotionSample a, WaveMotionSample b) => new[]
{
    MathF.Abs(a.Raise-b.Raise), MathF.Abs(a.WristSwing-b.WristSwing), MathF.Abs(a.ForearmSwing-b.ForearmSwing),
    MathF.Abs(a.HeadTilt-b.HeadTilt), MathF.Abs(a.HeadNod-b.HeadNod), MathF.Abs(a.Anticipation-b.Anticipation)
}.Max();
static float MaximumSecondDifference(WaveMotionSample a, WaveMotionSample b, WaveMotionSample c) => new[]
{
    MathF.Abs(a.Raise-2*b.Raise+c.Raise), MathF.Abs(a.WristSwing-2*b.WristSwing+c.WristSwing),
    MathF.Abs(a.ForearmSwing-2*b.ForearmSwing+c.ForearmSwing), MathF.Abs(a.HeadTilt-2*b.HeadTilt+c.HeadTilt),
    MathF.Abs(a.HeadNod-2*b.HeadNod+c.HeadNod), MathF.Abs(a.Anticipation-2*b.Anticipation+c.Anticipation)
}.Max();

static void WaveEndpoints()
{
    foreach (double t in new[] { -100.0, -0.01, 0, HumanoidGestureMotion.WaveDurationSeconds, 500 })
        Check(HumanoidGestureMotion.SampleWave(t, true) == default, "Greeting must return to a completely neutral delta.");
}
static void OneArmRaise()
{
    float previous = 0;
    for (double t = 0; t <= 1; t += 0.002)
    {
        float current = HumanoidGestureMotion.SampleWave(t).Raise;
        Check(current + 0.000001f >= previous, "Raising arm reversed direction."); previous = current;
    }
    Near(HumanoidGestureMotion.SampleWave(1).Raise, 1);
    for (double t = 1; t <= 3.3; t += 0.002) Near(HumanoidGestureMotion.SampleWave(t).Raise, 1);
    previous = 1;
    for (double t = 3.3; t <= 4.2; t += 0.002)
    {
        float current = HumanoidGestureMotion.SampleWave(t).Raise;
        Check(current <= previous + 0.000001f, "Lowering arm reversed direction."); previous = current;
    }
}
static void GestureLimits()
{
    for (double t = 0; t <= 4.3; t += 0.001)
    {
        var s = HumanoidGestureMotion.SampleWave(t, true);
        Check(s.Raise is >= 0 and <= 1 && s.Anticipation is >= 0 and <= 1);
        Check(MathF.Abs(s.WristSwing) <= 0.19f && MathF.Abs(s.ForearmSwing) <= 0.045f);
        Check(MathF.Abs(s.HeadTilt) <= 0.025f && s.HeadNod is >= 0 and <= 0.05f);
    }
}
static void WristPasses()
{
    int lastSign = 0, passes = 0;
    float maximum = 0;
    for (double t = 1.2; t < 3.15; t += 0.001)
    {
        float wrist = HumanoidGestureMotion.SampleWave(t).WristSwing;
        maximum = MathF.Max(maximum, MathF.Abs(wrist));
        if (MathF.Abs(wrist) < 0.0001f) continue;
        int sign = Math.Sign(wrist);
        if (lastSign != sign) passes++;
        lastSign = sign;
    }
    Check(passes == 5, $"Expected five side-to-side passes, got {passes}.");
    Check(maximum >= 0.16f, "Wave should remain visible, not become a micro-tremor.");
}
static void WristTiming()
{
    foreach (double t in new[] { 0.1, 0.7, 1.19, 3.15, 3.8, 4.1 })
    {
        Near(HumanoidGestureMotion.SampleWave(t).WristSwing, 0);
        Near(HumanoidGestureMotion.SampleWave(t).ForearmSwing, 0);
    }
}
static void NoShoulderPumping()
{
    for (double t = 1.2; t <= 3.15; t += 0.005) Near(HumanoidGestureMotion.SampleWave(t).Raise, 1);
}
static void EndpointVelocity()
{
    const double step = 0.0001;
    var start = HumanoidGestureMotion.SampleWave(0, true);
    var end = HumanoidGestureMotion.SampleWave(HumanoidGestureMotion.WaveDurationSeconds, true);
    Check(MaximumSignalDelta(start, HumanoidGestureMotion.SampleWave(step, true)) / step < 0.002);
    Check(MaximumSignalDelta(end, HumanoidGestureMotion.SampleWave(HumanoidGestureMotion.WaveDurationSeconds - step, true)) / step < 0.002);
}
static void PhaseContinuity()
{
    const double h = 0.0005;
    foreach (double t in new[] { 0.1, 0.2, 0.32, 0.75, 1.0, 1.08, 1.13, 1.2, 1.55, 1.62, 2.8, 3.15, 3.3 })
    {
        var a = HumanoidGestureMotion.SampleWave(t - h, true);
        var b = HumanoidGestureMotion.SampleWave(t, true);
        var c = HumanoidGestureMotion.SampleWave(t + h, true);
        Check(MaximumSignalDelta(a, c) < 0.01, $"Discontinuous pose around {t}s.");
        Check(MaximumSecondDifference(a, b, c) / (float)h < 0.02f, $"Velocity jumps around {t}s.");
    }
}
static void SingleNod()
{
    bool falling = false;
    float previous = 0, max = 0;
    for (double t = 0; t < 4.2; t += 0.001)
    {
        float nod = HumanoidGestureMotion.SampleWave(t, true).HeadNod;
        if (nod < previous - 0.000001f) falling = true;
        if (falling) Check(nod <= previous + 0.000001f, "Greeting nod repeated instead of acknowledging once.");
        max = MathF.Max(max, nod); previous = nod;
    }
    Near(max, 0.05f);
}
static void PlainWave()
{
    for (double t = 0; t < 4.2; t += 0.01) Near(HumanoidGestureMotion.SampleWave(t).HeadNod, 0);
}
static void AnticipationTiming()
{
    Check(HumanoidGestureMotion.SampleWave(0.1).Anticipation > 0.9f);
    for (double t = 0.32; t < 4.2; t += 0.01) Near(HumanoidGestureMotion.SampleWave(t).Anticipation, 0);
}
static void QuinticEasing()
{
    Near(HumanoidGestureMotion.SmoothStep01(-3), 0); Near(HumanoidGestureMotion.SmoothStep01(3), 1);
    Near(HumanoidGestureMotion.SmoothStep01(0.5), 0.5f);
    float previous = 0;
    for (double t = 0; t <= 1; t += 0.001)
    {
        float v = HumanoidGestureMotion.SmoothStep01(t); Check(v >= previous); previous = v;
    }
    Check(HumanoidGestureMotion.SmoothStep01(0.0001) / 0.0001 < 0.0001);
    Check((1 - HumanoidGestureMotion.SmoothStep01(0.9999)) / 0.0001 < 0.0001);
}
static void ContinuousIdle()
{
    var left = HumanoidGestureMotion.SampleIdle(3.9999);
    var right = HumanoidGestureMotion.SampleIdle(4.0001);
    Check(MathF.Abs(left.Breathing - right.Breathing) < 0.001);
    Check(MathF.Abs(left.HeadYaw - right.HeadYaw) < 0.0001);
    Check(HumanoidGestureMotion.SampleIdle(0) != HumanoidGestureMotion.SampleIdle(4), "Idle must not reset every four seconds.");
}
static void IdleIndependentSignals()
{
    var a = HumanoidGestureMotion.SampleIdle(2.2);
    var b = HumanoidGestureMotion.SampleIdle(5.7);
    Check(MathF.Abs(a.Breathing * b.WeightShift - b.Breathing * a.WeightShift) > 0.1);
}
static void IdleLimits()
{
    for (double t = 0; t < 7200; t += 0.17)
    {
        var s = HumanoidGestureMotion.SampleIdle(t);
        Check(MathF.Abs(s.Breathing) <= 1.000001f && MathF.Abs(s.WeightShift) <= 1.000001f);
        Check(MathF.Abs(s.HeadYaw) <= 0.009001f && MathF.Abs(s.HeadTilt) <= 0.006001f);
    }
    foreach (double t in new[] { double.MaxValue, -double.MaxValue })
    {
        var s = HumanoidGestureMotion.SampleIdle(t);
        Check(float.IsFinite(s.Breathing) && float.IsFinite(s.WeightShift) && float.IsFinite(s.HeadYaw) && float.IsFinite(s.HeadTilt));
    }
}
static void DeterministicSamples()
{
    foreach (double t in new[] { 0.23, 0.87, 1.42, 2.77, 3.8 })
    {
        var expected = HumanoidGestureMotion.SampleWave(t, true);
        for (int i = 0; i < 100; i++) Check(HumanoidGestureMotion.SampleWave(t, true) == expected);
    }
}
static void NonfiniteTimes()
{
    foreach (double time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        Reject(() => HumanoidGestureMotion.SampleWave(time));
        Reject(() => HumanoidGestureMotion.SampleIdle(time));
        Reject(() => HumanoidGestureMotion.SmoothStep01(time));
    }
    static void Reject(Action action)
    {
        try { action(); } catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Nonfinite motion input was accepted.");
    }
}

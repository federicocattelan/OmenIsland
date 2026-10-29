namespace OmenIsland.App.Views;

public readonly record struct CameraVector(float X, float Y, float Z);

public readonly record struct SurfacePoint(float ScreenX, float ScreenY, float Alpha, float Crest, bool Visible);

public static class TopographicField
{
    public const int LineCount = 48;
    public const int SamplesPerLine = 150;
    public const float PitchDegrees = -23.0f;
    public const float FovDegrees = 68.0f;
    public const float EyeX = 0.0f;
    public const float EyeY = 0.54f;
    public const float EyeZ = -0.32f;

    public const float XMin = -1.35f;
    public const float XMax = 1.35f;
    public const float ZNear = 0.12f;
    public const float ZFar = 3.4f;
    public const float CenterX = 0.0f;
    public const float CenterZ = 1.50f;
    public const float FieldRadius = 1.38f;

    private static readonly float CosPitch;
    private static readonly float SinPitch;
    private static readonly float FocalScale;

    static TopographicField()
    {
        var radians = PitchDegrees * MathF.PI / 180f;
        CosPitch = MathF.Cos(radians);
        SinPitch = MathF.Sin(radians);
        FocalScale = 1f / MathF.Tan(FovDegrees * MathF.PI / 360f);

        ViewForward = new CameraVector(0f, SinPitch, CosPitch);
        CameraEye = new CameraVector(EyeX, EyeY, EyeZ);
    }

    public static CameraVector ViewForward { get; }

    public static CameraVector CameraEye { get; }

    public static float WorldX(float normalizedX) =>
        XMin + (normalizedX * (XMax - XMin));

    public static float WorldZ(float normalizedZ) =>
        ZNear + (normalizedZ * (ZFar - ZNear));

    public static float RibbonFade(float z, float x = 0f)
    {
        var fadeNear = Math.Clamp((z - ZNear) / 0.16f, 0f, 1f);
        var fadeFar = Math.Clamp((ZFar - z) / 0.70f, 0f, 1f);
        var normalizedX = Math.Abs(x) / XMax;
        var fadeSide = Math.Clamp(1f - (normalizedX * normalizedX * normalizedX * normalizedX), 0f, 1f);
        return fadeNear * fadeFar * fadeSide;
    }

    private static readonly (float X, float Z, float Weight, float Rate, float Phase)[] Epicenters =
    [
        ( 0.00f,  1.50f, 1.00f, 1.3f, 0.00f), // Core center
        (-0.55f,  1.95f, 0.85f, 1.9f, 1.62f), // Back-left
        ( 0.60f,  1.90f, 0.85f, 2.4f, 3.24f), // Back-right
        (-0.65f,  1.05f, 0.80f, 1.7f, 4.86f), // Front-left
        ( 0.55f,  1.00f, 0.80f, 2.1f, 0.81f), // Front-right
        (-0.90f,  1.50f, 0.70f, 1.5f, 2.43f), // Far-left
        ( 0.85f,  1.45f, 0.70f, 2.7f, 4.05f), // Far-right
        ( 0.10f,  2.20f, 0.75f, 1.1f, 5.67f), // Deep-north
        (-0.15f,  0.80f, 0.75f, 2.0f, 1.25f)  // Near-south
    ];

    public static float SheetWave(float x, float z, float time)
    {
        var w1 = MathF.Sin((x * 2.2f) + (z * 2.0f) - (MathF.Cos(time * 1.5f) * 0.8f));
        var w2 = MathF.Sin((x * 1.4f) - (z * 1.5f) + (MathF.Sin(time * 1.1f) * 0.6f) + 0.3f);
        var w3 = MathF.Cos((x * 3.0f) + (z * 1.2f) - (time * 0.7f)) * 0.35f;
        return (w1 * 0.52f) + (w2 * 0.34f) + (w3 * 0.14f);
    }

    public static float AudioRipple(float x, float z, float time, float audio)
    {
        var total = 0f;
        for (var i = 0; i < Epicenters.Length; i++)
        {
            var node = Epicenters[i];

            // Smooth organic activation so different peaks surge in different areas of the sea
            var osc = MathF.Sin((time * node.Rate) + node.Phase);
            var weight = node.Weight * (0.50f + (0.50f * osc));

            var dx = x - node.X;
            var dz = z - node.Z;
            var dist = MathF.Sqrt((dx * dx) + (2.2f * dz * dz));

            // Peak surge generated at this epicenter
            var surge = MathF.Exp(-(dist * dist) / 0.15f) * 0.38f * audio;

            // Acoustic ripples propagating outward from this epicenter
            var outwardPhase = (dist * 15.0f) - (time * 12.0f);
            var decay = MathF.Exp(-dist / 0.65f) * (1.0f - MathF.Exp(-dist * 4.0f));
            var ripple = MathF.Sin(outwardPhase) * decay * 0.08f * audio;

            total += weight * (surge + ripple);
        }

        return total * 0.98f;
    }

    public static float MinorRhythmWaves(float x, float z, float rhythmPhase, float rhythmIntensity)
    {
        if (rhythmIntensity <= 0.001f)
        {
            return 0f;
        }

        // Elliptic coordinates relative to disk center (CenterX, CenterZ)
        var dx = x - CenterX;
        var dz = z - CenterZ;
        var r = MathF.Sqrt((dx * dx) + (1.6f * dz * dz));

        // Concentric acoustic ripples rolling outward from the core in sync with rhythm
        var concentric = MathF.Sin((r * 9.5f) - (rhythmPhase * 1.1f)) * MathF.Exp(-r * 0.55f);

        // Directional contour ridges undulating diagonally across lines
        var swell1 = MathF.Sin((x * 4.2f) + (z * 3.2f) - (rhythmPhase * 0.85f)) * 0.65f;
        var swell2 = MathF.Cos((x * 3.1f) - (z * 3.8f) + (rhythmPhase * 1.05f)) * 0.45f;

        // Harmonic ripple for fine contour detail
        var harmonic = MathF.Sin((x * 6.5f) - (rhythmPhase * 1.4f)) * MathF.Cos(z * 4.8f) * 0.25f;

        var combined = (concentric * 0.55f) + swell1 + swell2 + harmonic;

        // Controlled subtle amplitude: non-invasive, peaking around ~0.06 - 0.08
        var amplitude = Math.Clamp(rhythmIntensity, 0f, 1.5f) * 0.065f;

        return combined * amplitude;
    }

    public static float MelodyWaves(
        float x,
        float z,
        float melodyPhase,
        float melodyIntensity,
        float pitch = 0.5f)
    {
        if (melodyIntensity <= 0.001f)
        {
            return 0f;
        }

        var dx = x - CenterX;
        var dz = z - CenterZ;
        var r = MathF.Sqrt((dx * dx) + (1.6f * dz * dz));

        // Smooth harmonious ripples rolling outward from the core
        var radial = MathF.Sin((r * 6.5f) - melodyPhase) * MathF.Exp(-r * 0.55f);

        // Broad, graceful contour waves undulating across lines without high-frequency jitter
        var swell1 = MathF.Sin((x * 3.4f) + (z * 2.0f) - (melodyPhase * 0.85f)) * 0.70f;
        var swell2 = MathF.Cos((x * 2.2f) - (z * 2.6f) + (melodyPhase * 0.95f)) * 0.50f;

        // Gentle harmonic undertone for smooth natural curve
        var harmonic = MathF.Sin((x * 3.2f) - (z * 1.8f) - (melodyPhase * 1.05f)) * 0.20f;

        var combined = (radial * 0.60f) + swell1 + swell2 + harmonic;

        // Controlled melodic amplitude (~0.20 - 0.25)
        var amplitude = Math.Clamp(melodyIntensity, 0f, 1.8f) * 0.13f;

        return combined * amplitude;
    }

    public static float Height(
        float x,
        float z,
        float time,
        float audio,
        float rhythmPhase,
        float rhythmIntensity,
        float melodyIntensity,
        float melodyPhase,
        float melodyPitch = 0.5f,
        float globalAmplitude = 1f)
    {
        var baseElevation = SheetWave(x, z, time) * 0.075f * globalAmplitude;
        var minorWaves = MinorRhythmWaves(x, z, rhythmPhase, rhythmIntensity) * globalAmplitude;

        // In moments with beats, melody waves are gracefully overpowered by towering beat peaks
        var beatDampen = Math.Clamp(1.0f - (audio * 0.45f), 0.20f, 1.0f);
        var melodyElevation = MelodyWaves(x, z, melodyPhase, melodyIntensity * beatDampen, melodyPitch) * globalAmplitude;

        var audioElevation = AudioRipple(x, z, time, audio);
        return baseElevation + minorWaves + melodyElevation + audioElevation;
    }

    public static float Height(
        float x,
        float z,
        float time,
        float audio,
        float rhythmPhase,
        float rhythmIntensity,
        float globalAmplitude = 1f) =>
        Height(x, z, time, audio, rhythmPhase, rhythmIntensity, melodyIntensity: 0f, melodyPhase: 0f, melodyPitch: 0.5f, globalAmplitude);

    public static float Height(float x, float z, float time, float audio, float globalAmplitude = 1f) =>
        Height(x, z, time, audio, rhythmPhase: time * 2.0f, rhythmIntensity: 0f, melodyIntensity: 0f, melodyPhase: 0f, melodyPitch: 0.5f, globalAmplitude);

    public static SurfacePoint Project(float worldX, float worldY, float worldZ, float viewWidth, float viewHeight)
    {
        if (viewWidth < 1f || viewHeight < 1f)
        {
            return new SurfacePoint(0f, 0f, 0f, 0f, false);
        }

        var dx = worldX - EyeX;
        var dy = worldY - EyeY;
        var dz = worldZ - EyeZ;

        var cy = (dy * CosPitch) - (dz * SinPitch);
        var cz = (dy * SinPitch) + (dz * CosPitch);

        if (cz < 0.04f)
        {
            return new SurfacePoint(0f, 0f, 0f, 0f, false);
        }

        var aspect = viewWidth / viewHeight;
        var ndcX = (dx / cz) * (FocalScale / aspect);
        var ndcY = (cy / cz) * FocalScale;

        var screenX = (ndcX + 1f) * 0.5f * viewWidth;
        var screenY = (viewHeight * 0.68f) - (ndcY * (viewHeight * 0.44f));

        var alpha = RibbonFade(worldZ, worldX);
        var crest = Math.Clamp((worldY + 0.03f) / 0.35f, 0f, 1f);

        return new SurfacePoint(screenX, screenY, alpha, crest, true);
    }
}

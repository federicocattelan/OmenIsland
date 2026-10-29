using OmenIsland.App.Presentation;
using OmenIsland.App.Services.Media;
using OmenIsland.App.Views;

namespace OmenIsland.Tests;

public sealed class TopographicFieldTests
{
    [Fact]
    public void RibCountStaysInTheRequestedRange()
    {
        Assert.InRange(TopographicField.LineCount, 40, 100);
        Assert.True(TopographicField.SamplesPerLine >= 24);
    }

    [Fact]
    public void CameraSkimsTheSheet()
    {
        var forward = TopographicField.ViewForward;
        var pitch = MathF.Atan2(forward.Y, forward.Z) * 180f / MathF.PI;

        Assert.InRange(TopographicField.CameraEye.Y, 0.08f, 0.75f);
        Assert.True(forward.Y < 0f);
        Assert.InRange(pitch, -30f, -10f);
    }

    [Fact]
    public void SheetRecedesTowardAHorizon()
    {
        const float size = 220f;
        var near = TopographicField.Project(TopographicField.EyeX, 0f, 0.35f, size, size);
        var far = TopographicField.Project(TopographicField.EyeX, 0f, 3.0f, size, size);
        var left = TopographicField.Project(TopographicField.WorldX(0.08f), 0f, 1.0f, size, size);
        var right = TopographicField.Project(TopographicField.WorldX(0.92f), 0f, 1.0f, size, size);
        var crest = TopographicField.Project(TopographicField.EyeX, 0.16f, 1.0f, size, size);
        var floor = TopographicField.Project(TopographicField.EyeX, 0f, 1.0f, size, size);

        Assert.True(near.Visible && far.Visible, $"near {near} far {far}");
        Assert.True(far.ScreenY < near.ScreenY - 20f, $"far {far.ScreenY} near {near.ScreenY}");
        Assert.True(far.ScreenY < size * 0.65f, $"horizon {far.ScreenY}");
        Assert.True(near.ScreenY > size * 0.45f, $"foreground {near.ScreenY}");
        Assert.True(right.ScreenX - left.ScreenX > size * 0.5f, $"span {right.ScreenX - left.ScreenX}");
        Assert.True(crest.ScreenY < floor.ScreenY - 4f, $"crest {crest.ScreenY} floor {floor.ScreenY}");
    }

    [Fact]
    public void WavesRunAcrossTheWidthAndFadeAtBothEnds()
    {
        Assert.Equal(0f, TopographicField.RibbonFade(TopographicField.ZNear));
        Assert.Equal(0f, TopographicField.RibbonFade(TopographicField.ZFar));
        Assert.True(TopographicField.RibbonFade(1.5f) > TopographicField.RibbonFade(TopographicField.ZNear + 0.01f));
        Assert.True(TopographicField.RibbonFade(1.5f) > TopographicField.RibbonFade(TopographicField.ZFar - 0.01f));

        var min = float.MaxValue;
        var max = float.MinValue;
        var maxStep = 0f;
        var previous = TopographicField.SheetWave(TopographicField.XMin, 0.6f, 0.2f);
        for (var step = 0; step <= 24; step++)
        {
            var x = TopographicField.XMin + ((TopographicField.XMax - TopographicField.XMin) * step / 24f);
            var wave = TopographicField.SheetWave(x, 0.6f, 0.2f);
            min = MathF.Min(min, wave);
            max = MathF.Max(max, wave);
            maxStep = MathF.Max(maxStep, MathF.Abs(wave - previous));
            previous = wave;
        }

        Assert.True(max - min > 0.4f, $"range {min}..{max}");
        Assert.True(min < 0f && max > 0f, $"signed {min}..{max}");
        Assert.True(maxStep < 0.55f, $"step {maxStep}");
        Assert.NotEqual(
            TopographicField.SheetWave(0.4f, 0.6f, 0f),
            TopographicField.SheetWave(0.4f, 0.6f, 1.4f));

        var calm = MathF.Abs(TopographicField.AudioRipple(0.2f, 0.6f, 0.15f, 0.05f));
        var beat = MathF.Abs(TopographicField.AudioRipple(0.2f, 0.6f, 0.15f, 1.2f));
        Assert.True(beat > calm * 4f);

        float Peak(float z)
        {
            var peak = 0f;
            for (var step = 0; step <= 24; step++)
            {
                var x = TopographicField.XMin + ((TopographicField.XMax - TopographicField.XMin) * step / 24f);
                peak = MathF.Max(peak, MathF.Abs(TopographicField.Height(x, z, 0.3f, 0f, 1f)));
            }

            return peak;
        }

        var nearPeak = Peak(0.45f);
        var farPeak = Peak(2.4f);
        Assert.True(farPeak > nearPeak * 0.65f, $"near {nearPeak} far {farPeak}");
    }

    [Fact]
    public void MinorRhythmWavesStayNonInvasiveAndFollowRhythm()
    {
        var zero = TopographicField.MinorRhythmWaves(0f, 1.5f, 0f, 0f);
        Assert.Equal(0f, zero);

        var lowRhythm = TopographicField.MinorRhythmWaves(0f, 1.5f, 1.0f, 0.5f);
        var highRhythm = TopographicField.MinorRhythmWaves(0f, 1.5f, 1.0f, 1.0f);
        Assert.True(MathF.Abs(highRhythm) > MathF.Abs(lowRhythm));

        // Ensure non-invasive: peak height must not exceed 0.15f across phase angles
        for (float phase = 0f; phase < 6.28f; phase += 0.5f)
        {
            var height = TopographicField.MinorRhythmWaves(0.3f, 1.2f, phase, 1.0f);
            Assert.True(MathF.Abs(height) < 0.15f, $"Height {height} exceeded non-invasive limit");
        }
    }

    [Fact]
    public void MelodyWavesFollowIntensityAndDisplaceHeight()
    {
        var zero = TopographicField.MelodyWaves(0f, 1.5f, 0f, 0f);
        Assert.Equal(0f, zero);

        var lowMelody = TopographicField.MelodyWaves(0f, 1.5f, 1.0f, 0.4f);
        var highMelody = TopographicField.MelodyWaves(0f, 1.5f, 1.0f, 1.2f);
        Assert.True(MathF.Abs(highMelody) > MathF.Abs(lowMelody));

        // When no beat is playing, melody creates clear waves
        var melodyHeight = TopographicField.Height(
            0.1f, 1.4f, time: 1.0f, audio: 0f, rhythmPhase: 1.0f, rhythmIntensity: 0.5f,
            melodyIntensity: 1.0f, melodyPhase: 1.5f);
        var calmHeight = TopographicField.Height(
            0.1f, 1.4f, time: 1.0f, audio: 0f, rhythmPhase: 1.0f, rhythmIntensity: 0.5f,
            melodyIntensity: 0f, melodyPhase: 1.5f);
        Assert.NotEqual(melodyHeight, calmHeight);

        // When heavy beat strikes, beat elevation heavily dominates
        var beatElevation = MathF.Abs(TopographicField.AudioRipple(0f, 1.5f, 1.0f, 2.0f));
        var melodyOnly = MathF.Abs(TopographicField.MelodyWaves(0f, 1.5f, 1.0f, 1.0f));
        Assert.True(beatElevation > melodyOnly * 2.5f, $"beat {beatElevation} vs melody {melodyOnly}");
    }

    [Fact]
    public async Task VisualizerAndTerrainTogglesAreIndependent()
    {
        var model = new NowPlayingViewModel(new NowPlayingService());
        try
        {
            Assert.False(model.IsVisualizerOpen);
            Assert.False(model.IsTerrainOpen);
            Assert.False(model.ShowVisualizer);
            Assert.False(model.ShowTerrain);

            // Compact spectrum toggle
            model.ToggleVisualizerCommand.Execute(null);
            Assert.True(model.IsVisualizerOpen);
            Assert.False(model.IsTerrainOpen);

            model.ToggleVisualizerCommand.Execute(null);
            Assert.False(model.IsVisualizerOpen);
            Assert.False(model.IsTerrainOpen);

            // Cover terrain visualizer toggle
            model.ToggleTerrainCommand.Execute(null);
            Assert.False(model.IsVisualizerOpen);
            Assert.True(model.IsTerrainOpen);

            model.ToggleTerrainCommand.Execute(null);
            Assert.False(model.IsVisualizerOpen);
            Assert.False(model.IsTerrainOpen);
        }
        finally
        {
            await model.DisposeAsync();
        }
    }
}

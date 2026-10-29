using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using OmenIsland.App.Services;
using OmenIsland.App.Services.Media;

namespace OmenIsland.App.Views;

public sealed class TopographicRibbon : FrameworkElement
{
    private const float MuteGate = 0.0018f;

    public static readonly DependencyProperty IsCapturingProperty =
        DependencyProperty.Register(
            nameof(IsCapturing),
            typeof(bool),
            typeof(TopographicRibbon),
            new PropertyMetadata(false, OnCapturingChanged));

    public static readonly DependencyProperty TargetProcessIdProperty =
        DependencyProperty.Register(
            nameof(TargetProcessId),
            typeof(int),
            typeof(TopographicRibbon),
            new PropertyMetadata(0, OnTargetProcessIdChanged));

    public static readonly DependencyProperty LineBrushProperty =
        DependencyProperty.Register(
            nameof(LineBrush),
            typeof(Brush),
            typeof(TopographicRibbon),
            new FrameworkPropertyMetadata(
                Brushes.White,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnLineBrushChanged));

    private readonly object _gate = new();
    private float[] _ring = [];
    private float[] _real = [];
    private float[] _imag = [];
    private int _fftSize;
    private int _write;
    private float _bassCeiling = 0.01f;
    private float _bassFloor = 0.001f;
    private float _bassAverage;
    private float _intensity;
    private float _kick;
    private float _midCeiling = 0.01f;
    private float _rhythm;
    private float _rhythmPhase;
    private float _melody;
    private float _melodyPhase;
    private float _melodyPitch = 0.5f;
    private float _melodyAverage;
    private double _lastTickSeconds;
    private IWaveIn? _capture;
    private DispatcherTimer? _timer;
    private Stopwatch? _stopwatch;

    private Pen? _corePen;
    private Pen? _glowPen;
    private Brush? _vignetteBrush;
    private Color _lastAccentColor;
    private float _waveSurge;
    private float _lastSurgeLevel = -1f;

    public TopographicRibbon()
    {
        IsHitTestVisible = false;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        IsVisibleChanged += (_, _) => UpdateActivity();
        Loaded += (_, _) => UpdateActivity();
        Unloaded += (_, _) => Stop();

        InitVignette();
        UpdatePens(Colors.Magenta, 0f);
    }

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public int TargetProcessId
    {
        get => (int)GetValue(TargetProcessIdProperty);
        set => SetValue(TargetProcessIdProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(
            double.IsInfinity(availableSize.Width) ? 0 : Math.Max(0, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? 0 : Math.Max(0, availableSize.Height));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var time = (float)(_stopwatch?.Elapsed.TotalSeconds ?? 0d);
        var audio = Math.Clamp((_intensity * 0.20f) + (_kick * 1.25f), 0f, 2.6f);

        // Wave surge level: how much the waves are currently surging/elevated (0 = calm/onde basse, 1 = maximum peak)
        var targetSurge = Math.Clamp((_kick * 0.85f) + (_rhythm * 0.35f), 0f, 1f);
        if (targetSurge > _waveSurge)
        {
            _waveSurge = (_waveSurge * 0.55f) + (targetSurge * 0.45f);
        }
        else
        {
            _waveSurge = (_waveSurge * 0.88f) + (targetSurge * 0.12f);
        }

        var currentAccent = (LineBrush as SolidColorBrush)?.Color ?? Color.FromRgb(220, 60, 240);
        if (_corePen is null || _glowPen is null || currentAccent != _lastAccentColor || MathF.Abs(_waveSurge - _lastSurgeLevel) > 0.02f)
        {
            UpdatePens(currentAccent, _waveSurge);
        }

        // Smooth rotation around vertical axis Y matching reference.mp4
        var rotationAngle = time * 0.20f;
        var cosA = MathF.Cos(rotationAngle);
        var sinA = MathF.Sin(rotationAngle);

        var radius = TopographicField.FieldRadius;
        var centerX = TopographicField.CenterX;
        var centerZ = TopographicField.CenterZ;

        // Clip to rounded border matching album cover
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), 10, 10));

        // Solid black void background matching reference.mp4
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));

        if (_vignetteBrush is not null)
        {
            dc.PushOpacityMask(_vignetteBrush);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var line = 0; line < TopographicField.LineCount; line++)
            {
                var normU = line / (float)(TopographicField.LineCount - 1);
                var du = -radius + (2.0f * radius * normU);
                var maxV = MathF.Sqrt(MathF.Max(0.05f, (radius * radius) - (du * du * 0.72f)));
                var started = false;

                for (var sample = 0; sample < TopographicField.SamplesPerLine; sample++)
                {
                    var normV = sample / (float)(TopographicField.SamplesPerLine - 1);
                    var dv = -maxV + (2.0f * maxV * normV);

                    // Local coordinates on the mesh
                    var localX = du;
                    var localZ = centerZ + dv;

                    // Evaluate height in local coordinates (so peaks rotate with mesh)
                    var worldY = TopographicField.Height(localX, localZ, time, audio, _rhythmPhase, _rhythm, _melody, _melodyPhase, _melodyPitch, 1f);

                    // Rotate (localX, localZ) around (centerX, centerZ) in vertical axis Y
                    var worldX = centerX + ((du * cosA) - (dv * sinA));
                    var worldZ = centerZ + ((du * sinA) + (dv * cosA));

                    var pt = TopographicField.Project(worldX, worldY, worldZ, (float)width, (float)height);
                    if (!pt.Visible)
                    {
                        started = false;
                        continue;
                    }

                    var screenPoint = new Point(pt.ScreenX, pt.ScreenY);
                    if (!started)
                    {
                        ctx.BeginFigure(screenPoint, false, false);
                        started = true;
                    }
                    else
                    {
                        ctx.LineTo(screenPoint, true, true);
                    }
                }
            }
        }
        geometry.Freeze();

        if (_glowPen is not null)
        {
            dc.DrawGeometry(null, _glowPen, geometry);
        }

        if (_corePen is not null)
        {
            dc.DrawGeometry(null, _corePen, geometry);
        }

        if (_vignetteBrush is not null)
        {
            dc.Pop();
        }

        dc.Pop();
    }

    private void InitVignette()
    {
        var vignetteStops = new GradientStopCollection
        {
            new GradientStop(Colors.Transparent, 0.00),
            new GradientStop(Colors.Black, 0.06),
            new GradientStop(Colors.Black, 0.94),
            new GradientStop(Colors.Transparent, 1.00)
        };
        var vignette = new LinearGradientBrush(vignetteStops, new Point(0.0, 0.5), new Point(1.0, 0.5));
        vignette.Freeze();
        _vignetteBrush = vignette;
    }

    private void UpdatePens(Color accent, float surge)
    {
        _lastAccentColor = accent;
        _lastSurgeLevel = surge;

        var (hue, sat, _) = AccentColor.ToHsl(accent);
        var s = Math.Clamp(surge, 0f, 1f);

        // Vertical gradient stops:
        // - Base state (s -> 0, onde basse o quiete): rich, vivid, saturated accent gamma across all lines
        // - On beat (s -> 1, beat alti): the LIGHT EFFECT flares up on the crest and upper slopes with radiant light,
        //   while the foot and body maintain deep contrast with the black void background.

        // 1. Foot / near base (Y = 1.00): softly merges into the dark background
        var footLit = Math.Clamp(0.16 - (s * 0.05), 0.09, 0.22);
        var footSat = Math.Clamp(sat * (0.70 - (s * 0.10)), 0.35, 1.0);
        var footAlpha = (byte)Math.Clamp(75 - (s * 25), 40, 100);
        var footColor = AccentColor.FromHsl(hue, footSat, footLit, footAlpha);

        // 2. Lower body (Y = 0.78): coherent accent foundation
        var bodyLit = Math.Clamp(0.32 - (s * 0.08), 0.18, 0.40);
        var bodySat = Math.Clamp(sat * (0.88 - (s * 0.08)), 0.50, 1.0);
        var bodyAlpha = (byte)Math.Clamp(220 - (s * 15), 180, 255);
        var bodyColor = AccentColor.FromHsl(hue, bodySat, bodyLit, bodyAlpha);

        // 3. Mid-slope (Y = 0.52): main vibrant body of the wave
        var midLit = Math.Clamp(0.48 + (s * 0.06), 0.40, 0.62);
        var midSat = Math.Clamp(sat * (0.95 + (s * 0.07)), 0.70, 1.0);
        var midColor = AccentColor.FromHsl(hue, midSat, midLit);

        // 4. Upper ridge (Y = 0.25): brilliant electric layer, catching the beat light
        var upperLit = Math.Clamp(0.62 + (s * 0.16), 0.55, 0.85);
        var upperSat = Math.Clamp(sat * (0.95 + (s * 0.10)), 0.75, 1.0);
        var upperColor = AccentColor.FromHsl(hue, upperSat, upperLit);

        // 5. Crest tip (Y <= 0.08): radiant light crowning the peak on beats ("assume un colore piu chiaro man mano che i beat aumentano")
        var crestLit = Math.Clamp(0.72 + (s * 0.22), 0.65, 0.96);
        var crestSat = Math.Clamp(sat * (0.90 - (s * 0.25)), 0.40, 1.0);
        var crestColor = AccentColor.FromHsl(hue, crestSat, crestLit);

        var stops = new GradientStopCollection
        {
            new GradientStop(crestColor, 0.00),
            new GradientStop(upperColor, 0.26),
            new GradientStop(midColor, 0.52),
            new GradientStop(bodyColor, 0.78),
            new GradientStop(footColor, 1.00)
        };

        var coreThickness = 1.25 + (s * 0.35);
        var coreBrush = new LinearGradientBrush(stops, new Point(0.5, 0.0), new Point(0.5, 1.0));
        coreBrush.Freeze();
        _corePen = new Pen(coreBrush, coreThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        _corePen.Freeze();

        // Light effect (glow halo & aura): only on the upper peaks of the beat waves, fading downward to zero
        var glowCrestLit = Math.Clamp(0.50 + (s * 0.32), 0.45, 0.85);
        var glowMidLit = Math.Clamp(0.36 + (s * 0.24), 0.32, 0.62);
        var glowSat = Math.Min(1.0, sat * 1.15);

        var glowCrest = AccentColor.FromHsl(hue, glowSat, glowCrestLit, 255);
        var glowMid = AccentColor.FromHsl(hue, glowSat, glowMidLit, (byte)Math.Clamp(140 + (s * 90), 100, 230));
        var glowZero = AccentColor.FromHsl(hue, glowSat, glowMidLit, 0);

        var glowStops = new GradientStopCollection
        {
            new GradientStop(glowCrest, 0.00),
            new GradientStop(glowCrest, 0.18),
            new GradientStop(glowMid, 0.36),
            new GradientStop(glowZero, 0.52),
            new GradientStop(glowZero, 1.00)
        };

        var glowOpacity = Math.Clamp(0.10f + (s * 0.72f), 0.08f, 0.82f);
        var glowThickness = 2.0 + (s * 3.5);
        var glowBrush = new LinearGradientBrush(glowStops, new Point(0.5, 0.0), new Point(0.5, 1.0))
        {
            Opacity = glowOpacity
        };
        glowBrush.Freeze();
        _glowPen = new Pen(glowBrush, glowThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        _glowPen.Freeze();
    }

    private static Color Mix(Color color, Color target, float amount)
    {
        var a = (byte)Math.Clamp((color.A * (1f - amount)) + (target.A * amount), 0, 255);
        var r = (byte)Math.Clamp((color.R * (1f - amount)) + (target.R * amount), 0, 255);
        var g = (byte)Math.Clamp((color.G * (1f - amount)) + (target.G * amount), 0, 255);
        var b = (byte)Math.Clamp((color.B * (1f - amount)) + (target.B * amount), 0, 255);
        return Color.FromArgb(a, r, g, b);
    }

    private static Color ScaleRgb(Color color, float factor)
    {
        var r = (byte)Math.Clamp(color.R * factor, 0, 255);
        var g = (byte)Math.Clamp(color.G * factor, 0, 255);
        var b = (byte)Math.Clamp(color.B * factor, 0, 255);
        return Color.FromArgb(color.A, r, g, b);
    }

    private static void OnCapturingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((TopographicRibbon)sender).UpdateActivity();

    private static void OnTargetProcessIdChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TopographicRibbon ribbon && !Equals(args.OldValue, args.NewValue))
        {
            ribbon.UpdateActivity();
        }
    }

    private static void OnLineBrushChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TopographicRibbon ribbon && args.NewValue is SolidColorBrush brush)
        {
            ribbon.UpdatePens(brush.Color, ribbon._lastSurgeLevel >= 0f ? ribbon._lastSurgeLevel : 0f);
            ribbon.InvalidateVisual();
        }
    }

    private void UpdateActivity()
    {
        if (IsCapturing && IsVisible)
        {
            var currentTargetPid = (_capture as ProcessLoopbackCapture)?.TargetProcessId ?? 0;
            if (_timer is null || currentTargetPid != TargetProcessId)
            {
                Start();
            }
        }
        else if (_capture is not null || _timer is not null)
        {
            Stop();
        }
    }

    private void Start()
    {
        Stop();
        _bassCeiling = 0.01f;
        _bassFloor = 0.001f;
        _bassAverage = 0f;
        _intensity = 0f;
        _kick = 0f;
        _midCeiling = 0.01f;
        _rhythm = 0f;
        _rhythmPhase = 0f;
        _melody = 0f;
        _melodyPhase = 0f;
        _melodyPitch = 0.5f;
        _melodyAverage = 0f;
        _lastTickSeconds = 0d;

        var targetPid = TargetProcessId;
        if (targetPid > 0)
        {
            try
            {
                _capture = new ProcessLoopbackCapture(targetPid);
                _fftSize = ChooseFftSize(_capture.WaveFormat.SampleRate);
                _ring = new float[_fftSize];
                _real = new float[_fftSize];
                _imag = new float[_fftSize];
                _write = 0;
                _capture.DataAvailable += OnAudio;
                _capture.StartRecording();
            }
            catch
            {
                if (_capture is not null)
                {
                    _capture.DataAvailable -= OnAudio;
                    _capture.Dispose();
                    _capture = null;
                }

                _fftSize = 2048;
                _ring = new float[_fftSize];
                _real = new float[_fftSize];
                _imag = new float[_fftSize];
            }
        }
        else
        {
            _capture = null;
            _fftSize = 2048;
            _ring = new float[_fftSize];
            _real = new float[_fftSize];
            _imag = new float[_fftSize];
            _write = 0;
        }

        _stopwatch = Stopwatch.StartNew();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private void OnTick()
    {
        var totalSeconds = _stopwatch?.Elapsed.TotalSeconds ?? 0d;
        var dt = _lastTickSeconds > 0d ? (float)Math.Clamp(totalSeconds - _lastTickSeconds, 0.001d, 0.1d) : 0.016f;
        _lastTickSeconds = totalSeconds;

        UpdateAudio();

        // Wave cadence: continuous gentle flow accelerated by rhythm
        var tempoVelocity = 1.4f + (_rhythm * 1.6f);
        _rhythmPhase += dt * tempoVelocity;

        // Melody cadence: singing undulating flow moving in time with vocal phrasing
        var melodyVelocity = 1.6f + (_melody * 2.0f);
        _melodyPhase += dt * melodyVelocity;

        InvalidateVisual();
    }

    private void OnAudio(object? sender, WaveInEventArgs args)
    {
        var capture = _capture;
        if (capture is null || capture.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            return;
        }

        var channels = Math.Max(1, capture.WaveFormat.Channels);
        var frames = args.BytesRecorded / (4 * channels);
        lock (_gate)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var offset = frame * channels * 4;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToSingle(args.Buffer, offset + (channel * 4));
                }

                _ring[_write] = sum / channels;
                _write = (_write + 1) % _ring.Length;
            }
        }
    }

    private void UpdateAudio()
    {
        var capture = _capture;
        var fftSize = _fftSize;
        if (fftSize < 2 || _ring.Length != fftSize)
        {
            return;
        }

        int write;
        lock (_gate)
        {
            write = _write;
            for (var index = 0; index < fftSize; index++)
            {
                _real[index] = _ring[(write + index) % fftSize];
                _imag[index] = 0f;
            }
        }

        var signal = 0f;
        for (var index = 0; index < fftSize; index++)
        {
            var window = 0.5f - (0.5f * MathF.Cos((2f * MathF.PI * index) / (fftSize - 1)));
            _real[index] *= window;
            signal = MathF.Max(signal, MathF.Abs(_real[index]));
        }

        Transform(_real, _imag);

        var sampleRate = capture?.WaveFormat.SampleRate ?? 48000;
        var bassStart = HzToBin(32, fftSize, sampleRate);
        var bassEnd = Math.Max(bassStart + 1, HzToBin(115, fftSize, sampleRate));
        var midStart = Math.Max(bassEnd + 1, HzToBin(120, fftSize, sampleRate));
        var midEnd = Math.Min(HzToBin(2600, fftSize, sampleRate), (fftSize / 2) - 1);

        var bassEnergy = 0f;
        var bassCount = 0;
        var limit = Math.Min(bassEnd, fftSize / 2);
        for (var bin = bassStart; bin <= limit; bin++)
        {
            bassEnergy += MathF.Sqrt((_real[bin] * _real[bin]) + (_imag[bin] * _imag[bin]));
            bassCount++;
        }

        if (bassCount > 0)
        {
            bassEnergy /= bassCount;
        }

        var midEnergy = 0f;
        var midCount = 0;
        var weightedBinSum = 0f;
        var magnitudeSum = 0f;
        for (var bin = midStart; bin <= midEnd; bin++)
        {
            var mag = MathF.Sqrt((_real[bin] * _real[bin]) + (_imag[bin] * _imag[bin]));
            midEnergy += mag;
            weightedBinSum += bin * mag;
            magnitudeSum += mag;
            midCount++;
        }

        if (midCount > 0)
        {
            midEnergy /= midCount;
        }

        var pitchCentroid = magnitudeSum > 0.0001f ? (weightedBinSum / magnitudeSum) : midStart;
        var normalizedPitch = Math.Clamp((pitchCentroid - midStart) / (float)Math.Max(1, midEnd - midStart), 0f, 1f);
        _melodyPitch = (_melodyPitch * 0.82f) + (normalizedPitch * 0.18f);

        var broadEnergy = 0f;
        var broadCount = 0;
        for (var bin = bassStart; bin <= midEnd; bin++)
        {
            broadEnergy += MathF.Sqrt((_real[bin] * _real[bin]) + (_imag[bin] * _imag[bin]));
            broadCount++;
        }

        if (broadCount > 0)
        {
            broadEnergy /= broadCount;
        }

        if (signal < 0.0006f)
        {
            _kick *= 0.85f;
            _intensity *= 0.85f;
            _rhythm *= 0.85f;
            _melody *= 0.85f;
            _bassAverage *= 0.90f;
            _melodyAverage *= 0.90f;
        }
        else
        {
            // Dynamic AGC directly on the sub-bass band:
            if (bassEnergy > _bassCeiling)
            {
                _bassCeiling = bassEnergy;
            }
            else
            {
                _bassCeiling = Math.Max(0.002f, (_bassCeiling * 0.985f) + (bassEnergy * 0.015f));
            }

            if (bassEnergy < _bassFloor)
            {
                _bassFloor = bassEnergy;
            }
            else
            {
                _bassFloor = (_bassFloor * 0.985f) + (bassEnergy * 0.015f);
            }

            var dynamicRange = Math.Max(0.002f, _bassCeiling - _bassFloor);
            var normalizedBass = Math.Clamp((bassEnergy - _bassFloor) / dynamicRange, 0f, 1.8f);

            var bassDelta = MathF.Max(0f, normalizedBass - _bassAverage);
            _bassAverage = (_bassAverage * 0.86f) + (normalizedBass * 0.14f);

            // Proportional and smooth beat detection without jarring spikes
            if (bassDelta > 0.035f)
            {
                var targetKick = Math.Clamp((bassDelta - 0.035f) * 2.4f, 0f, 2.5f);
                _kick = (_kick * 0.35f) + (targetKick * 0.65f);
            }
            else
            {
                _kick *= 0.88f;
            }

            // Dynamic AGC on mids / rhythm (vocals, instruments, snares, groove)
            if (midEnergy > _midCeiling)
            {
                _midCeiling = midEnergy;
            }
            else
            {
                _midCeiling = Math.Max(0.001f, (_midCeiling * 0.988f) + (midEnergy * 0.012f));
            }

            var normalizedMid = Math.Clamp(midEnergy / Math.Max(0.001f, _midCeiling), 0f, 1.8f);
            var normBroad = Math.Clamp(broadEnergy / Math.Max(0.005f, _bassCeiling), 0f, 1.5f);

            _intensity = (_intensity * 0.70f) + (normBroad * 0.30f);

            // Continuous musical rhythm presence: sustained between beats, smoothly tracking track cadence
            var targetRhythm = Math.Clamp((normalizedMid * 0.65f) + (normBroad * 0.35f), 0f, 1.5f);
            _rhythm = (_rhythm * 0.80f) + (targetRhythm * 0.20f);

            // Responsive vocal and melody tracking for melodic waves between beats
            _melodyAverage = (_melodyAverage * 0.94f) + (normalizedMid * 0.06f);
            var targetMelody = Math.Clamp(normalizedMid * 1.15f, 0f, 1.8f);
            if (targetMelody > _melody)
            {
                _melody = (_melody * 0.72f) + (targetMelody * 0.28f);
            }
            else
            {
                _melody = (_melody * 0.88f) + (targetMelody * 0.12f);
            }
        }
    }

    private static int ChooseFftSize(int sampleRate)
    {
        var size = 2048;
        while (size < 8192 && sampleRate / (double)size > 15d)
        {
            size *= 2;
        }

        return size;
    }

    private static int HzToBin(int hz, int fftSize, int sampleRate) =>
        Math.Clamp((int)Math.Round(hz * fftSize / (double)Math.Max(1, sampleRate)), 1, (fftSize / 2) - 1);

    private static void Transform(float[] real, float[] imag)
    {
        var length = real.Length;
        for (int index = 1, reversed = 0; index < length; index++)
        {
            var bit = length >> 1;
            for (; (reversed & bit) != 0; bit >>= 1)
            {
                reversed ^= bit;
            }

            reversed ^= bit;
            if (index >= reversed)
            {
                continue;
            }

            (real[index], real[reversed]) = (real[reversed], real[index]);
            (imag[index], imag[reversed]) = (imag[reversed], imag[index]);
        }

        for (var size = 2; size <= length; size <<= 1)
        {
            var angle = -2d * Math.PI / size;
            var stepReal = Math.Cos(angle);
            var stepImag = Math.Sin(angle);
            for (var start = 0; start < length; start += size)
            {
                double turnReal = 1;
                double turnImag = 0;
                for (var offset = 0; offset < size / 2; offset++)
                {
                    var even = start + offset;
                    var odd = even + (size / 2);
                    var mixedReal = (real[odd] * turnReal) - (imag[odd] * turnImag);
                    var mixedImag = (real[odd] * turnImag) + (imag[odd] * turnReal);
                    real[odd] = (float)(real[even] - mixedReal);
                    imag[odd] = (float)(imag[even] - mixedImag);
                    real[even] = (float)(real[even] + mixedReal);
                    imag[even] = (float)(imag[even] + mixedImag);
                    var nextReal = (turnReal * stepReal) - (turnImag * stepImag);
                    turnImag = (turnReal * stepImag) + (turnImag * stepReal);
                    turnReal = nextReal;
                }
            }
        }
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _stopwatch?.Stop();
        _stopwatch = null;

        _bassCeiling = 0.01f;
        _bassFloor = 0.001f;
        _bassAverage = 0f;
        _intensity = 0f;
        _kick = 0f;
        _write = 0;
        _fftSize = 0;
        Array.Clear(_ring);
        Array.Clear(_real);
        Array.Clear(_imag);

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnAudio;
            try
            {
                _capture.StopRecording();
            }
            catch
            {
                // The device can already be stopped.
            }

            _capture.Dispose();
            _capture = null;
        }
    }
}

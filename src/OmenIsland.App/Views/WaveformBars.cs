using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using OmenIsland.App.Services.Media;

namespace OmenIsland.App.Views;

public sealed class WaveformBars : FrameworkElement
{
    private const int BarCount = 36;
    private const int MinimumHz = 50;
    private const int MaximumHz = 12000;
    private const float MuteGate = 0.0018f;

    public static readonly DependencyProperty IsCapturingProperty =
        DependencyProperty.Register(
            nameof(IsCapturing),
            typeof(bool),
            typeof(WaveformBars),
            new PropertyMetadata(false, OnCapturingChanged));

    public static readonly DependencyProperty TargetProcessIdProperty =
        DependencyProperty.Register(
            nameof(TargetProcessId),
            typeof(int),
            typeof(WaveformBars),
            new PropertyMetadata(0, OnTargetProcessIdChanged));

    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(
            nameof(BarBrush),
            typeof(Brush),
            typeof(WaveformBars),
            new FrameworkPropertyMetadata(
                Brushes.White,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly float[] _bars = new float[BarCount];
    private readonly object _gate = new();
    private float[] _ring = [];
    private int[] _edges = [];
    private float _envelope;
    private int _fftSize;
    private int _write;
    private IWaveIn? _capture;
    private DispatcherTimer? _timer;

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    public int TargetProcessId
    {
        get => (int)GetValue(TargetProcessIdProperty);
        set => SetValue(TargetProcessIdProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public WaveformBars()
    {
        Height = 20;
        IsHitTestVisible = false;
        Unloaded += (_, _) => Stop();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 20);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var brush = BarBrush ?? Brushes.White;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        float[] levels;
        float envelope;
        lock (_gate)
        {
            levels = (float[])_bars.Clone();
            envelope = _envelope;
        }

        var muted = envelope < MuteGate;
        var gap = 2d;
        var barWidth = Math.Max(1.5, (width - (gap * (levels.Length - 1))) / levels.Length);
        for (var index = 0; index < levels.Length; index++)
        {
            var amount = muted
                ? 0.08f
                : Math.Clamp(levels[index] / envelope, 0.1f, 1f);
            var barHeight = Math.Max(2d, amount * height);
            var x = index * (barWidth + gap);
            drawingContext.DrawRoundedRectangle(
                brush,
                null,
                new Rect(x, height - barHeight, barWidth, barHeight),
                1,
                1);
        }
    }

    private static void OnCapturingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((WaveformBars)sender).UpdateActivity();

    private static void OnTargetProcessIdChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WaveformBars bars && !Equals(args.OldValue, args.NewValue))
        {
            bars.UpdateActivity();
        }
    }

    private void UpdateActivity()
    {
        if (IsCapturing)
        {
            var currentTargetPid = (_capture as ProcessLoopbackCapture)?.TargetProcessId ?? 0;
            if (_timer is null || currentTargetPid != TargetProcessId)
            {
                Start();
            }
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        Stop();
        var targetPid = TargetProcessId;
        if (targetPid > 0)
        {
            try
            {
                _capture = new ProcessLoopbackCapture(targetPid);
                _fftSize = ChooseFftSize(_capture.WaveFormat.SampleRate);
                _edges = BandEdges(_fftSize, _capture.WaveFormat.SampleRate);
                _ring = new float[_fftSize];
                _write = 0;
                _capture.DataAvailable += OnAudio;
                _capture.StartRecording();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _timer.Tick += (_, _) => UpdateSpectrum();
                _timer.Start();
            }
            catch
            {
                Stop();
            }
        }
        else
        {
            _capture = null;
            _fftSize = 0;
            _edges = [];
            _ring = [];
            _write = 0;
            _envelope = 0;
            Array.Clear(_bars);
            InvalidateVisual();
        }
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

    private void UpdateSpectrum()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        var fftSize = _fftSize;
        var edges = _edges;
        if (fftSize < 2 || edges.Length != BarCount + 1 || _ring.Length != fftSize)
        {
            return;
        }

        var samples = new float[fftSize];
        int write;
        lock (_gate)
        {
            write = _write;
            for (var index = 0; index < fftSize; index++)
            {
                samples[index] = _ring[(write + index) % fftSize];
            }
        }

        var signal = 0f;
        for (var index = 0; index < samples.Length; index++)
        {
            var window = 0.5f - (0.5f * MathF.Cos((2f * MathF.PI * index) / (fftSize - 1)));
            samples[index] *= window;
            var level = Math.Abs(samples[index]);
            if (level > signal)
            {
                signal = level;
            }
        }

        var real = samples;
        var imag = new float[fftSize];
        Transform(real, imag);

        var next = new float[BarCount];
        var loudest = 0f;
        for (var band = 0; band < BarCount; band++)
        {
            var magnitude = 0f;
            var count = 0;
            for (var bin = edges[band]; bin < edges[band + 1]; bin++)
            {
                magnitude += MathF.Sqrt((real[bin] * real[bin]) + (imag[bin] * imag[bin]));
                count++;
            }

            next[band] = MathF.Log10(1f + (count == 0 ? 0f : magnitude / count));
            if (next[band] > loudest)
            {
                loudest = next[band];
            }
        }

        lock (_gate)
        {
            if (signal < MuteGate)
            {
                _envelope = 0;
            }
            else if (loudest > _envelope)
            {
                _envelope = (_envelope * 0.30f) + (loudest * 0.70f);
            }
            else
            {
                _envelope = (_envelope * 0.95f) + (loudest * 0.05f);
            }

            for (var band = 0; band < BarCount; band++)
            {
                var target = signal < MuteGate ? 0f : next[band];
                if (target > _bars[band])
                {
                    _bars[band] = (_bars[band] * 0.35f) + (target * 0.65f);
                }
                else
                {
                    _bars[band] = (_bars[band] * 0.72f) + (target * 0.28f);
                }
            }
        }

        InvalidateVisual();
    }

    private static int ChooseFftSize(int sampleRate)
    {
        var size = 2048;
        while (size < 65536 && sampleRate / (double)size > 3d)
        {
            size *= 2;
        }

        return size;
    }

    private static int[] BandEdges(int fftSize, int sampleRate)
    {
        var edges = new int[BarCount + 1];
        var limit = fftSize / 2;
        var rate = Math.Max(1, sampleRate);
        for (var band = 0; band <= BarCount; band++)
        {
            var hz = MinimumHz * Math.Pow(MaximumHz / (double)MinimumHz, band / (double)BarCount);
            edges[band] = (int)Math.Round(hz * fftSize / rate);
        }

        edges[0] = Math.Clamp(edges[0], 1, Math.Max(1, limit - BarCount));
        for (var band = 1; band < edges.Length; band++)
        {
            var minimum = edges[band - 1] + 1;
            var maximum = Math.Max(minimum, limit - (BarCount - band));
            edges[band] = Math.Clamp(Math.Max(edges[band], minimum), minimum, maximum);
        }

        return edges;
    }

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
        _envelope = 0;
        _write = 0;
        _fftSize = 0;
        _edges = [];
        Array.Clear(_bars);
        Array.Clear(_ring);
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

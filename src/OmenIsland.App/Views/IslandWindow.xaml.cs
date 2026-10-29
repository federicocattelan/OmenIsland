using System.ComponentModel;
using Microsoft.Win32;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OmenIsland.App.Services;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OmenIsland.App.Presentation;

namespace OmenIsland.App.Views;

public partial class IslandWindow : Window
{
    private static readonly Duration TransitionDuration =
        new(TimeSpan.FromMilliseconds(340));

    private readonly IslandViewModel _viewModel;
    private readonly IslandStateController _stateController;
    private readonly DispatcherTimer _enterTimer;
    private readonly DispatcherTimer _leaveTimer;
    private int _boundsAnimationGeneration;
    private bool _allowClose;
    private double _stableCompactWidth;
    private int _compactMode;
    private double _stableExpandedHeight;

    public IslandWindow(
        IslandViewModel viewModel,
        IslandStateController? stateController = null)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _stateController = stateController ?? new IslandStateController();
        DataContext = _viewModel;

        _enterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _enterTimer.Tick += (_, _) =>
        {
            _enterTimer.Stop();
            _stateController.PointerEntered();
        };

        _leaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(320)
        };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (!IslandSurface.IsMouseOver)
            {
                _stateController.Collapse();
            }
        };

        _stateController.StateChanged += (_, state) =>
        {
            if (state == IslandState.Idle)
            {
                _viewModel.Calendar.ResetToToday();
            }

            ApplyState(state, true);
        };
        _viewModel.Calendar.LayoutChanged += (_, _) =>
        {
            if (_stateController.State == IslandState.Expanded)
            {
                ApplyState(IslandState.Expanded, false);
            }
        };
        _viewModel.NowPlaying.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NowPlayingViewModel.IsActive) &&
                _stateController.State is IslandState.Compact or IslandState.Expanded)
            {
                ApplyState(_stateController.State, true);
            }
        };
        _viewModel.Calendar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CalendarViewModel.HasCompactEvent) &&
                _stateController.State == IslandState.Compact)
            {
                ApplyState(IslandState.Compact, true);
            }
        };

        Loaded += Window_Loaded;
        Closing += Window_Closing;
        SourceInitialized += Window_SourceInitialized;
        SizeChanged += (_, _) => UpdateCornerClip();
        IslandSurface.SizeChanged += (_, _) => UpdateCornerClip();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyShellColor();
            return;
        }

        Dispatcher.Invoke(ApplyShellColor);
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmDwmColorizationColorChanged)
        {
            ApplyShellColor();
        }

        return IntPtr.Zero;
    }

    public IslandState State => _stateController.State;

    public void Expand()
    {
        Show();
        Activate();
        _stateController.Activate();
    }

    public void Collapse() => _stateController.Collapse();

    public event EventHandler? SettingsRequested;

    public void PermitClose() => _allowClose = true;

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyState(IslandState.Idle, false);
        PositionSlider.PreviewMouseLeftButtonDown += (_, _) =>
            _viewModel.NowPlaying.BeginSeek();
        PositionSlider.PreviewMouseLeftButtonUp += (_, _) =>
            _viewModel.NowPlaying.EndSeek(PositionSlider.Value);
        PositionSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
                _viewModel.NowPlaying.EndSeek(PositionSlider.Value)));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _stateController.Collapse();
        }
    }

    private void IslandSurface_MouseEnter(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        _leaveTimer.Stop();

        if (_stateController.State == IslandState.Idle)
        {
            _enterTimer.Stop();
            _enterTimer.Start();
        }
    }

    private void IslandSurface_MouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        _enterTimer.Stop();

        if (_stateController.State is IslandState.Compact or IslandState.Expanded)
        {
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }
    }

    private void IslandSurface_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(
                e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<Slider>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _stateController.Activate();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _stateController.Collapse();
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_stateController.State == IslandState.Expanded)
        {
            _stateController.Collapse();
        }
    }

    private void ApplyState(IslandState state, bool animate)
    {
        RevealContent(state, animate);
        var compactWidth = state == IslandState.Compact ? MeasureCompactWidth() : 0;
        var workArea = SystemParameters.WorkArea;
        var dimensions = IslandLayoutCalculator.Calculate(
            state,
            workArea.Width,
            workArea.Height,
            compactWidth,
            _viewModel.NowPlaying.IsActive);
        if (state == IslandState.Expanded)
        {
            ExpandedView.Measure(new Size(dimensions.Width - 28, double.PositiveInfinity));
            var needed = Math.Ceiling(ExpandedView.DesiredSize.Height + 26);
            if (_stableExpandedHeight <= 0 || Math.Abs(needed - _stableExpandedHeight) >= 8)
            {
                _stableExpandedHeight = needed;
            }

            dimensions = new IslandDimensions(
                dimensions.Width,
                Math.Min(Math.Max(dimensions.Height, _stableExpandedHeight), workArea.Height - 24));
        }
        else
        {
            _stableExpandedHeight = 0;
        }

        var left = workArea.Left + (workArea.Width - dimensions.Width) / 2d;

        ApplyShellColor();
        IslandSurface.Background = state == IslandState.Idle
            ? System.Windows.Media.Brushes.Transparent
            : (System.Windows.Media.Brush)Resources["ShellBrush"];
        IslandSurface.BorderBrush = state == IslandState.Idle
            ? System.Windows.Media.Brushes.Transparent
            : (System.Windows.Media.Brush)Resources["ShellBrush"];
        IslandSurface.CornerRadius = state == IslandState.Idle
            ? new CornerRadius(0)
            : new CornerRadius(0, 0, 16, 16);

        Top = MonitorTopInDips();
        UpdateCornerClip();

        if (!animate)
        {
            StopBoundsAnimation();
            Width = dimensions.Width;
            Height = dimensions.Height;
            Left = left;
            PinToMonitorTop();
            return;
        }

        AnimateBounds(dimensions.Width, dimensions.Height, left);
    }

    private void UpdateCornerClip()
    {
        var width = IslandSurface.ActualWidth;
        var height = IslandSurface.ActualHeight;
        var radius = _stateController.State == IslandState.Idle
            ? 0
            : IslandSurface.CornerRadius.BottomLeft;
        if (width <= 0 || height <= 0 || radius <= 0)
        {
            RootHost.Clip = null;
            return;
        }

        RootHost.Clip = BottomRoundedClip(width, height, radius);
    }

    private static Geometry BottomRoundedClip(double width, double height, double radius)
    {
        var curve = Math.Min(radius, Math.Min(width / 2d, height / 2d));
        var figure = new PathFigure
        {
            StartPoint = new Point(0, 0),
            IsClosed = true
        };
        figure.Segments.Add(new LineSegment(new Point(width, 0), true));
        figure.Segments.Add(new LineSegment(new Point(width, height - curve), true));
        figure.Segments.Add(new ArcSegment(
            new Point(width - curve, height),
            new Size(curve, curve),
            0,
            false,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(new Point(curve, height), true));
        figure.Segments.Add(new ArcSegment(
            new Point(0, height - curve),
            new Size(curve, curve),
            0,
            false,
            SweepDirection.Clockwise,
            true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private double MeasureCompactWidth()
    {
        CompactView.Measure(new Size(double.PositiveInfinity, 88));
        var measured = Math.Ceiling(CompactView.DesiredSize.Width);
        var mode = (_viewModel.NowPlaying.IsActive ? 1 : 0)
            | (_viewModel.NowPlaying.ShowVisualizer ? 2 : 0)
            | (_viewModel.Calendar.HasCompactEvent ? 4 : 0);
        if (mode == _compactMode &&
            _stableCompactWidth > 0 &&
            Math.Abs(measured - _stableCompactWidth) < 8)
        {
            return _stableCompactWidth;
        }

        _compactMode = mode;
        _stableCompactWidth = measured;
        return measured;
    }

    private void RevealContent(IslandState state, bool animate)
    {
        FrameworkElement incoming = state switch
        {
            IslandState.Idle => IdleView,
            IslandState.Compact => CompactView,
            _ => ExpandedView
        };

        foreach (var view in new FrameworkElement[] { IdleView, CompactView, ExpandedView })
        {
            if (view == incoming)
            {
                continue;
            }

            view.BeginAnimation(OpacityProperty, null);
            view.Opacity = 0;
            view.Visibility = Visibility.Collapsed;
        }

        incoming.Visibility = Visibility.Visible;
        incoming.BeginAnimation(OpacityProperty, null);
        incoming.Opacity = 1;

        if (state == IslandState.Expanded && animate)
        {
            PlayArrangeAnimation();
            return;
        }

        ResetArrangeAnimation();

        if (!animate)
        {
            return;
        }

        incoming.Opacity = 0;
        var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220))
        {
            BeginTime = TimeSpan.FromMilliseconds(50),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(fade, 60);
        incoming.BeginAnimation(OpacityProperty, fade);
    }

    private void PlayArrangeAnimation()
    {
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
        var index = 0;

        foreach (var element in ArrangeElements())
        {
            PopInPlace(element, TimeSpan.FromMilliseconds(index * 18), duration, ease);
            index++;
        }
    }

    private void ResetArrangeAnimation()
    {
        foreach (var element in ArrangeElements())
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
        }
    }

    private IEnumerable<FrameworkElement> ArrangeElements()
    {
        yield return PlayerColumn;
        yield return ProfileEco;
        yield return ProfileQuiet;
        yield return ProfileDefault;
        yield return ProfilePerformance;
        yield return MetricCpu;
        yield return MetricGpu;
        yield return MetricRam;
        yield return FanRow;
        yield return CalendarColumn;
    }

    private static void PopInPlace(
        FrameworkElement element,
        TimeSpan delay,
        TimeSpan duration,
        IEasingFunction ease)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.82, 0.82);
        element.RenderTransform = scale;
        element.Opacity = 0;

        var fade = new DoubleAnimation(1, duration)
        {
            BeginTime = delay,
            EasingFunction = ease
        };
        var scaleX = new DoubleAnimation(1, duration)
        {
            BeginTime = delay,
            EasingFunction = ease
        };
        var scaleY = new DoubleAnimation(1, duration)
        {
            BeginTime = delay,
            EasingFunction = ease
        };
        Timeline.SetDesiredFrameRate(fade, 60);
        Timeline.SetDesiredFrameRate(scaleX, 60);
        Timeline.SetDesiredFrameRate(scaleY, 60);

        element.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void StopBoundsAnimation()
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
    }

    private void AnimateBounds(double width, double height, double left)
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

        void Animate(DependencyProperty property, double to)
        {
            var animation = new DoubleAnimation(to, TransitionDuration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            Timeline.SetDesiredFrameRate(animation, 60);
            BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        var generation = ++_boundsAnimationGeneration;
        Animate(WidthProperty, width);
        Animate(HeightProperty, height);
        var slide = new DoubleAnimation(left, TransitionDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(slide, 60);
        slide.Completed += (_, _) =>
        {
            if (generation == _boundsAnimationGeneration)
            {
                PinToMonitorTop();
            }
        };
        BeginAnimation(LeftProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private double MonitorTopInDips()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        var pixelTop = System.Windows.Forms.Screen.FromHandle(handle).Bounds.Top;
        var source = PresentationSource.FromVisual(this);
        var toDip = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        return pixelTop * toDip;
    }

    private void PinToMonitorTop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return;
        }

        var screenTop = System.Windows.Forms.Screen.FromHandle(handle).Bounds.Top;
        if (rect.Top == screenTop)
        {
            return;
        }

        SetWindowPos(
            handle,
            IntPtr.Zero,
            rect.Left,
            screenTop,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void ApplyShellColor()
    {
        var color = TaskbarColorReader.Read();
        var accent = AccentColor.ForSurface(color, TaskbarColorReader.ReadWallpaperAccent());
        var raised = Color.FromRgb(
            (byte)Math.Min(255, color.R + 22),
            (byte)Math.Min(255, color.G + 22),
            (byte)Math.Min(255, color.B + 22));
        var shell = PaintBrush(Resources, "ShellBrush", color);
        PaintBrush(Resources, "ShellRaisedBrush", raised);
        PaintBrush(Resources, "IslandActiveBrush", accent);
        PaintBrush(System.Windows.Application.Current.Resources, "IslandActiveBrush", accent);
        IdleLine.Background = shell;

        if (_stateController.State != IslandState.Idle)
        {
            IslandSurface.Background = shell;
            IslandSurface.BorderBrush = shell;
        }
    }

    private static SolidColorBrush PaintBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources.Contains(key) &&
            resources[key] is SolidColorBrush existing &&
            !existing.IsFrozen)
        {
            existing.Color = color;
            return existing;
        }

        var brush = new SolidColorBrush(color);
        resources[key] = brush;
        return brush;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WmDwmColorizationColorChanged = 0x0320;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectNative lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

}

using System.Windows;
using System.Windows.Controls;

namespace OmenIsland.App.Views;

public enum CompactTrackSlot
{
    Title,
    Artist,
    Controls,
    Elapsed,
    Duration
}

public sealed class CompactTrackLayout : Panel
{
    private const double StopGap = 4;

    public static readonly DependencyProperty SlotProperty =
        DependencyProperty.RegisterAttached(
            "Slot",
            typeof(CompactTrackSlot),
            typeof(CompactTrackLayout),
            new FrameworkPropertyMetadata(CompactTrackSlot.Title, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static void SetSlot(DependencyObject element, CompactTrackSlot value) =>
        element.SetValue(SlotProperty, value);

    public static CompactTrackSlot GetSlot(DependencyObject element) =>
        (CompactTrackSlot)element.GetValue(SlotProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : Math.Max(0, availableSize.Width);
        var plan = Plan(width);
        return new Size(width, plan.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var plan = Plan(finalSize.Width);
        ArrangeSlot(CompactTrackSlot.Title, new Rect(0, 0, plan.TitleWidth, plan.TitleHeight));
        ArrangeSlot(CompactTrackSlot.Artist, new Rect(0, plan.TitleHeight, plan.ArtistWidth, plan.ArtistHeight));
        ArrangeSlot(
            CompactTrackSlot.Controls,
            new Rect(plan.ControlsLeft, 0, plan.ControlWidth, plan.Height));
        ArrangeSlot(
            CompactTrackSlot.Elapsed,
            new Rect(finalSize.Width - plan.TimeWidth, 0, plan.TimeWidth, plan.TitleHeight));
        ArrangeSlot(
            CompactTrackSlot.Duration,
            new Rect(finalSize.Width - plan.TimeWidth, plan.TitleHeight, plan.TimeWidth, plan.ArtistHeight));
        return finalSize;
    }

    private TrackPlan Plan(double width)
    {
        var controls = Find(CompactTrackSlot.Controls);
        var elapsed = Find(CompactTrackSlot.Elapsed);
        var duration = Find(CompactTrackSlot.Duration);
        var controlWidth = MeasureWidth(controls, double.PositiveInfinity);
        var timeWidth = Math.Max(
            MeasureWidth(elapsed, double.PositiveInfinity),
            MeasureWidth(duration, double.PositiveInfinity));
        if (timeWidth > 0)
        {
            timeWidth = Math.Ceiling(timeWidth) + 2;
        }

        var reserved = controlWidth + timeWidth;
        if (controlWidth > 0 && timeWidth > 0)
        {
            reserved += StopGap;
        }

        var titleMax = Math.Max(0, width - reserved);
        var titleWidth = Math.Min(MeasureWidth(Find(CompactTrackSlot.Title), titleMax), titleMax);
        var stop = width - timeWidth - (controlWidth > 0 && timeWidth > 0 ? StopGap : 0);
        var free = Math.Max(0, stop - controlWidth - titleWidth);
        var controlsLeft = titleWidth + (free / 2);
        var artistMax = controlWidth > 0
            ? Math.Max(0, controlsLeft)
            : Math.Max(0, width - timeWidth);
        MeasureWidth(Find(CompactTrackSlot.Artist), artistMax);

        var titleHeight = HeightOf(CompactTrackSlot.Title);
        var artistHeight = HeightOf(CompactTrackSlot.Artist);
        var height = Math.Max(titleHeight + artistHeight, Math.Max(HeightOf(CompactTrackSlot.Controls), HeightOf(CompactTrackSlot.Elapsed) + HeightOf(CompactTrackSlot.Duration)));
        return new TrackPlan(titleWidth, titleHeight, artistMax, artistHeight, controlWidth, controlsLeft, timeWidth, Math.Max(height, 1));
    }

    private void ArrangeSlot(CompactTrackSlot slot, Rect rect)
    {
        var element = Find(slot);
        if (element is null || element.Visibility == Visibility.Collapsed)
        {
            return;
        }

        element.Arrange(rect);
    }

    private double HeightOf(CompactTrackSlot slot)
    {
        var element = Find(slot);
        return element is null || element.Visibility == Visibility.Collapsed ? 0 : element.DesiredSize.Height;
    }

    private static double MeasureWidth(UIElement? element, double width)
    {
        if (element is null || element.Visibility == Visibility.Collapsed)
        {
            return 0;
        }

        element.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private UIElement? Find(CompactTrackSlot slot)
    {
        foreach (UIElement child in Children)
        {
            if (GetSlot(child) == slot)
            {
                return child;
            }
        }

        return null;
    }

    private readonly record struct TrackPlan(
        double TitleWidth,
        double TitleHeight,
        double ArtistWidth,
        double ArtistHeight,
        double ControlWidth,
        double ControlsLeft,
        double TimeWidth,
        double Height);
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Globalization;

namespace MyAgent.Desktop;

public sealed class SlimScrollBar : ScrollBar
{
    private Thumb? _thumb;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_thumb is not null)
            _thumb.DragDelta -= OnThumbDrag;

        _thumb = GetTemplateChild("PART_Thumb") as Thumb;
        if (_thumb is not null)
            _thumb.DragDelta += OnThumbDrag;
    }

    private void OnThumbDrag(object sender, DragDeltaEventArgs e)
    {
        if (GetTemplateChild("PART_TrackArea") is not FrameworkElement track ||
            GetTemplateChild("PART_Thumb") is not FrameworkElement thumb) return;

        var vertical = Orientation == Orientation.Vertical;
        var travel = vertical
            ? track.ActualHeight - thumb.ActualHeight
            : track.ActualWidth - thumb.ActualWidth;
        if (travel <= 0) return;

        var delta = vertical ? e.VerticalChange : e.HorizontalChange;
        if (delta == 0) return;

        // 不能直接写 Value：Value 由 ScrollViewer 通过 TemplateBinding 单向驱动，
        // 写入本地值既不会滚动内容，又会切断绑定导致滚轮滚动后滑块不再跟随。
        // 正确做法是驱动宿主 ScrollViewer 的偏移量，Value/滑块位置再由绑定回填。
        if (TemplatedParent is not ScrollViewer owner) return;

        var change = delta / travel * Maximum;
        if (vertical)
            owner.ScrollToVerticalOffset(owner.VerticalOffset + change);
        else
            owner.ScrollToHorizontalOffset(owner.HorizontalOffset + change);
    }
}

public sealed class ThumbLengthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not double track || values[1] is not double viewport || values[2] is not double maximum)
            return 120d;
        if (track <= 0) return 120d;
        var len = maximum + viewport <= 0 ? track : track * viewport / (maximum + viewport);
        return Math.Max(120, Math.Min(track, len * 2.5));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ThumbOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double value || values[1] is not double maximum ||
            values[2] is not double track || values[3] is not double viewport)
            return 0d;
        if (track <= 0 || maximum <= 0) return 0d;
        var len = Math.Max(120, Math.Min(track, track * viewport / (maximum + viewport) * 2.5));
        var travel = Math.Max(0, track - len);
        return Math.Max(0, Math.Min(travel, value / maximum * travel));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
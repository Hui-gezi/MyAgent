using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MyAgent.Desktop;

/// <summary>
/// 细滚动条：外观由 App.xaml 的 SlimScrollBar 样式定制（标准 Track 结构），
/// 行为交给 WPF 原生 ScrollBar——滑块比例、拖拽、点击翻页均由 Track/ScrollViewer
/// 原生协作完成。
///
/// 唯一的定制是 ViewportSize 的 CoerceValue：内容很大时 Track 按比例算出的滑块
/// 只有几像素，Thumb.MinHeight 虽把绘制尺寸夹到 24px，但 Track 定位仍按未夹取的
/// 小尺寸算行程——贴底时滑块伸出轨道底部被裁/被输入区盖住，呈现"上圆角下平边"。
/// 这里反解不等式 size = v'/(max+v')*track ≥ min ⇒ v' ≥ min*max/(track-min)，
/// 把修正后的 viewport 喂给原生 Track，让尺寸与位置共用同一套数学，任何数据量
/// 下滑块都完整落在轨道内。不能改在模板里绑 Track.ViewportSize：ScrollBar 内部
/// 会用本地值直接覆写它，绑定会被切断。
/// </summary>
public sealed class SlimScrollBar : ScrollBar
{
    /// <summary>滑块最小长度（与模板 Thumb.MinHeight/MinWidth 一致）。</summary>
    private const double MinThumbLength = 24;

    /// <summary>模板中轨道 Grid 的外边距总和（垂直 Margin="0,4" / 水平 Margin="4,0"）。</summary>
    private const double TrackMargin = 8;

    static SlimScrollBar()
    {
        // null PropertyChangedCallback：保留基类把值转发给 Track 的原生回调，只追加纠偏。
        ViewportSizeProperty.OverrideMetadata(typeof(SlimScrollBar),
            new FrameworkPropertyMetadata(0.0, null, CoerceViewportSize));
        // 轨道长度 / 内容规模变化时重算纠偏值。
        MaximumProperty.OverrideMetadata(typeof(SlimScrollBar),
            new FrameworkPropertyMetadata(OnExtentChanged));
        OrientationProperty.OverrideMetadata(typeof(SlimScrollBar),
            new FrameworkPropertyMetadata(OnExtentChanged));
    }

    private static void OnExtentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.InvalidateProperty(ViewportSizeProperty);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateProperty(ViewportSizeProperty);
    }

    private static object CoerceViewportSize(DependencyObject d, object baseValue)
    {
        if (baseValue is not double viewport) return baseValue!;
        var bar = (SlimScrollBar)d;
        var outer = bar.Orientation == Orientation.Vertical ? bar.ActualHeight : bar.ActualWidth;
        var track = outer - TrackMargin;
        if (bar.Maximum <= 0 || track <= MinThumbLength) return viewport;
        var required = MinThumbLength * bar.Maximum / (track - MinThumbLength);
        return Math.Max(viewport, required);
    }
}

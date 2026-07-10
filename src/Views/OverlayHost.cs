using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DLsiteMedia.Views;

/// <summary>
/// 程序内模态覆盖层宿主：在所属窗口的 AdornerLayer 上叠加内容，并用嵌套消息泵阻塞到关闭，
/// 供各类程序内对话框（InAppDialog / DownTargetDialog 等）复用，避免弹出程序外窗口。
/// </summary>
internal static class OverlayHost
{
    /// <summary>
    /// 在 owner 所属窗口上模态显示 build 生成的覆盖层，阻塞到内容调用 close(result)。
    /// 返回所选结果；无法定位窗口 AdornerLayer 时返回 null（由调用方决定兜底方式）。
    /// </summary>
    public static bool? ShowModal(DependencyObject? owner, Func<Action<bool>, FrameworkElement> build)
    {
        var window = ResolveWindow(owner);
        var root = window?.Content as UIElement;
        var layer = root != null ? AdornerLayer.GetAdornerLayer(root) : null;
        if (layer == null || root == null)
            return null;

        var result = false;
        var frame = new DispatcherFrame();
        OverlayAdorner? adorner = null;

        void Close(bool value)
        {
            if (adorner == null)
                return;
            result = value;
            layer.Remove(adorner);
            adorner = null;
            frame.Continue = false;
        }

        var content = build(Close);
        adorner = new OverlayAdorner(root, content);
        layer.Add(adorner);
        content.Loaded += (_, _) => content.Focus();
        content.Focus();

        Dispatcher.PushFrame(frame);
        return result;
    }

    private static Window? ResolveWindow(DependencyObject? owner)
    {
        if (owner != null && Window.GetWindow(owner) is { } w)
            return w;
        return Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
               ?? Application.Current?.MainWindow;
    }

    /// <summary>把覆盖层铺满被装饰元素（整窗）的简易 Adorner。</summary>
    private sealed class OverlayAdorner : Adorner
    {
        private readonly VisualCollection _visuals;
        private readonly FrameworkElement _child;

        public OverlayAdorner(UIElement adornedElement, FrameworkElement child) : base(adornedElement)
        {
            _child = child;
            _visuals = new VisualCollection(this) { child };
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override Size MeasureOverride(Size constraint)
        {
            var size = AdornedElement is FrameworkElement fe
                ? new Size(fe.ActualWidth, fe.ActualHeight)
                : constraint;
            _child.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}

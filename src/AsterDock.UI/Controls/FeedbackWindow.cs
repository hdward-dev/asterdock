using System.Diagnostics;
using AsterDock.Contracts;
using AsterDock.UI.Services;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AsterDock.UI.Controls;

/// <summary>
/// Attaches the shared dialog and toast layer to a window the host did not create.
/// The layer is built before the window is shown and before the page is measured.
/// A control that already belongs to a layout manager is left where it is.
/// </summary>
public static class FeedbackWindow
{
    public static void Attach(Window window, IUserFeedbackService? feedback)
    {
        try
        {
            if (feedback is not UserFeedbackService service || window.Content is not Control content) return;
            if (ContainsLayer(content)) return;
            if (window.IsVisible || HasLayoutManager(content))
            {
                Log(new InvalidOperationException("窗口已经显示或页面已经布局，不再改挂内容"));
                return;
            }

            if (ReferenceEquals(window.Content, content)) window.Content = null;
            window.Content = CreateHost(content, service);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(exception);
        }
    }

    public static bool HasLayoutManager(Control control)
        => control.IsMeasureValid
           || control.IsArrangeValid
           || control.IsAttachedToVisualTree()
           || control.GetVisualParent() is not null;

    public static Control CreateHost(Control content, UserFeedbackService service)
    {
        var root = new Grid();
        root.Children.Add(content);
        var layer = new AdFeedbackLayer { Service = service };
        layer.SetValue(Panel.ZIndexProperty, 300);
        root.Children.Add(layer);
        return root;
    }

    private static bool ContainsLayer(Control? control) => control switch
    {
        null => false,
        AdFeedbackLayer => true,
        Panel panel => panel.Children.OfType<Control>().Any(ContainsLayer),
        ContentControl { Content: Control inner } => ContainsLayer(inner),
        Decorator { Child: Control child } => ContainsLayer(child),
        _ => false
    };

    private static void Log(Exception exception)
        => Trace.TraceError("星栈反馈层未能挂接: " + exception);
}

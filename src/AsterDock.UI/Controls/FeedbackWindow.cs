using System.Diagnostics;
using AsterDock.Contracts;
using AsterDock.UI.Services;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace AsterDock.UI.Controls;

/// <summary>
/// Attaches the shared dialog and toast layer to a window the host did not create.
/// The layer is built before the window content is measured. A control that already
/// belongs to a layout manager is detached first and moved only after that pass ends.
/// </summary>
public static class FeedbackWindow
{
    public static void Attach(Window window, IUserFeedbackService? feedback)
    {
        try
        {
            if (feedback is not UserFeedbackService service || window.Content is not Control content) return;
            if (ContainsLayer(content)) return;
            if (NeedsDeferredMove(content))
            {
                Adopt(content, placed =>
                {
                    if (ContainsLayer(window.Content as Control)) return;
                    window.Content = CreateHost(placed, service);
                });
                return;
            }

            // Content has not been measured. Wrap it before the window assigns a layout manager.
            window.Content = null;
            window.Content = CreateHost(content, service);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(exception);
        }
    }

    /// <summary>
    /// Moves <paramref name="view"/> onto a new parent after its current layout manager has released it.
    /// </summary>
    public static void Adopt(Control view, Action<Control> place, Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(place);
        try
        {
            DetachLogical(view);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(exception);
            failed?.Invoke(exception);
            return;
        }

        var defer = NeedsDeferredMove(view);
        var waits = 0;
        void Place()
        {
            try
            {
                if (view.Parent is not null) DetachLogical(view);
                if ((view.GetVisualParent() is not null || view.IsAttachedToVisualTree() || view.Parent is not null) && waits < 8)
                {
                    waits++;
                    Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
                    return;
                }

                if (view.Parent is not null || view.GetVisualParent() is not null || view.IsAttachedToVisualTree())
                {
                    var blocked = new InvalidOperationException("控件仍挂在原来的布局上，未能重新挂接");
                    Log(blocked);
                    failed?.Invoke(blocked);
                    return;
                }

                place(view);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log(exception);
                failed?.Invoke(exception);
            }
        }

        if (defer) Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
        else Place();
    }

    public static Control CreateHost(Control content, UserFeedbackService service)
    {
        var root = new Grid();
        root.Children.Add(content);
        var layer = new AdFeedbackLayer { Service = service };
        layer.SetValue(Panel.ZIndexProperty, 300);
        root.Children.Add(layer);
        return root;
    }

    private static bool NeedsDeferredMove(Control control)
        => control.IsAttachedToVisualTree() || control.GetVisualParent() is not null || control.IsMeasureValid;

    private static void DetachLogical(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl content when ReferenceEquals(content.Content, control):
                content.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
        }
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

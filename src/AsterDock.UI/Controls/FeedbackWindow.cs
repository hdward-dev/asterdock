using AsterDock.Contracts;
using AsterDock.UI.Services;
using Avalonia.Controls;

namespace AsterDock.UI.Controls;

/// <summary>
/// Attaches the shared dialog and toast layer to a window the host did not create.
/// </summary>
public static class FeedbackWindow
{
    public static void Attach(Window window, IUserFeedbackService? feedback)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (feedback is not UserFeedbackService service || ContainsLayer(window.Content as Control)) return;
        if (window.Content is not Control content) return;

        window.Content = null;
        var root = new Grid();
        root.Children.Add(content);
        var layer = new AdFeedbackLayer { Service = service };
        layer.SetValue(Panel.ZIndexProperty, 300);
        root.Children.Add(layer);
        window.Content = root;
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
}

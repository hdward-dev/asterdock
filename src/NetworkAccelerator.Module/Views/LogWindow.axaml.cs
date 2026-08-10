using Avalonia.Controls;

namespace NetworkAccelerator.Module.Views;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
    }

    public LogWindow(string log) : this() => LogTextBox.Text = log;
}

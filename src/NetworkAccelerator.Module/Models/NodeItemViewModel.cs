using NetworkAccelerator.Core.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetworkAccelerator.Module.Models;

public sealed class NodeItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _isMeasuringLatency;
    private int? _latency;

    public NodeItemViewModel(ProxyNode? node, bool automatic = false)
    {
        Node = node;
        IsAutomatic = automatic;
        Tag = automatic ? "自动选择" : node!.Tag;
        Name = Tag;
        Subtitle = automatic ? "智能选择当前延迟最低的节点" : $"{node!.Type.ToUpperInvariant()} · {FormatEndpoint(node)}";
        LocationMark = automatic ? "自" : GetLocationMark(Tag);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ProxyNode? Node { get; }
    public bool IsAutomatic { get; }
    public string Tag { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string LocationMark { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            SetField(ref _isSelected, value);
        }
    }
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }
    public int? Latency
    {
        get => _latency;
        set
        {
            if (!SetField(ref _latency, value)) return;
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(Quality));
            OnPropertyChanged(nameof(HasLatency));
            OnPropertyChanged(nameof(IsSlowLatency));
        }
    }
    public bool IsMeasuringLatency
    {
        get => _isMeasuringLatency;
        set => SetField(ref _isMeasuringLatency, value);
    }
    public string LatencyText => Latency is null ? "--" : $"{Latency} ms";
    public bool HasLatency => Latency is not null;
    public bool IsSlowLatency => Latency > 180;
    public double Quality => Latency switch
    {
        null => 0,
        <= 80 => 100,
        <= 150 => 72,
        <= 250 => 45,
        _ => 20
    };
    private static string FormatEndpoint(ProxyNode node) =>
        string.IsNullOrWhiteSpace(node.Server) ? "订阅节点" : $"{node.Server}:{node.ServerPort}";

    private static string GetLocationMark(string value)
    {
        if (value.Contains("香港") || value.Contains("HK", StringComparison.OrdinalIgnoreCase)) return "港";
        if (value.Contains("日本") || value.Contains("JP", StringComparison.OrdinalIgnoreCase)) return "日";
        if (value.Contains("新加坡") || value.Contains("SG", StringComparison.OrdinalIgnoreCase)) return "新";
        if (value.Contains("美国") || value.Contains("US", StringComparison.OrdinalIgnoreCase)) return "美";
        return value.Length == 0 ? "节" : value[..1].ToUpperInvariant();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

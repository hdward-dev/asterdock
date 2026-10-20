using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetworkAccelerator.Module.ViewModels;

public sealed class ServiceProbe : INotifyPropertyChanged
{
    private string _latencyText = "—";
    private string _level = "idle";

    public ServiceProbe(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public string LatencyText { get => _latencyText; private set => SetField(ref _latencyText, value); }
    public bool IsOk => _level == "ok";
    public bool IsSlow => _level == "slow";
    public bool IsDown => _level == "down";
    public bool IsIdle => _level == "idle";

    public void Apply(int? delay, bool tested)
    {
        if (!tested)
        {
            LatencyText = "—";
            SetLevel("idle");
            return;
        }

        if (delay is null or < 0)
        {
            LatencyText = "超时";
            SetLevel("down");
            return;
        }

        LatencyText = $"{delay.Value} ms";
        SetLevel(delay.Value <= 1000 ? "ok" : "slow");
    }

    public void Reset() => Apply(null, false);

    private void SetLevel(string level)
    {
        if (_level == level) return;
        _level = level;
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsSlow));
        OnPropertyChanged(nameof(IsDown));
        OnPropertyChanged(nameof(IsIdle));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

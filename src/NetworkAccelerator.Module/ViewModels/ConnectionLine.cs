using System.ComponentModel;
using System.Runtime.CompilerServices;
using NetworkAccelerator.Module.Controls;

namespace NetworkAccelerator.Module.ViewModels;

public sealed class ConnectionLine : INotifyPropertyChanged
{
    private string _source;
    private string _target;
    private string _protocol;
    private string _outbound;
    private string _download;
    private string _upload;
    private string _duration;
    private double _fade = 1;
    private double _shift;
    private double _fadeFrom;
    private double _fadeTo = 1;
    private double _shiftFrom;
    private double _shiftTo;
    private double _elapsed;
    private bool _running;
    private bool _leaving;
    private Action? _leaveCompleted;

    public ConnectionLine(string id, string source, string target, string protocol, string outbound, string download, string upload, string duration)
    {
        Id = id;
        _source = source;
        _target = target;
        _protocol = protocol;
        _outbound = outbound;
        _download = download;
        _upload = upload;
        _duration = duration;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public bool IsLeaving => _leaving;
    public string Source { get => _source; private set => SetField(ref _source, value); }
    public string Target { get => _target; private set => SetField(ref _target, value); }
    public string Protocol { get => _protocol; private set => SetField(ref _protocol, value); }
    public string Outbound { get => _outbound; private set => SetField(ref _outbound, value); }
    public string Download { get => _download; private set => SetField(ref _download, value); }
    public string Upload { get => _upload; private set => SetField(ref _upload, value); }
    public string Duration { get => _duration; private set => SetField(ref _duration, value); }
    public double Fade { get => _fade; private set => SetField(ref _fade, value); }
    public double Shift { get => _shift; private set => SetField(ref _shift, value); }

    public void CopyFrom(ConnectionLine other)
    {
        Source = other.Source;
        Target = other.Target;
        Protocol = other.Protocol;
        Outbound = other.Outbound;
        Download = other.Download;
        Upload = other.Upload;
        Duration = other.Duration;
    }

    public void BeginEnter()
    {
        if (DashboardMotion.ReduceMotion)
        {
            Fade = 1;
            Shift = 0;
            return;
        }

        _fadeFrom = 0;
        _fadeTo = 1;
        _shiftFrom = 8;
        _shiftTo = 0;
        Fade = 0;
        Shift = 8;
        Start();
    }

    public void BeginLeave(Action completed)
    {
        if (_leaving) return;
        _leaving = true;
        if (DashboardMotion.ReduceMotion)
        {
            completed();
            return;
        }

        _leaveCompleted = completed;
        _fadeFrom = Fade;
        _fadeTo = 0;
        _shiftFrom = Shift;
        _shiftTo = -6;
        Start();
    }

    private void Start()
    {
        _elapsed = 0;
        if (_running) return;
        _running = true;
        MotionClock.Subscribe(Tick);
    }

    private void Tick(TimeSpan elapsed)
    {
        _elapsed += elapsed.TotalMilliseconds;
        var duration = _leaving ? DashboardMotion.RowLeave.TotalMilliseconds : DashboardMotion.RowEnter.TotalMilliseconds;
        var t = Math.Clamp(_elapsed / duration, 0, 1);
        var eased = _leaving ? t * t * t : 1 - Math.Pow(1 - t, 3);
        Fade = _fadeFrom + (_fadeTo - _fadeFrom) * eased;
        Shift = _shiftFrom + (_shiftTo - _shiftFrom) * eased;
        if (t < 1) return;
        MotionClock.Unsubscribe(Tick);
        _running = false;
        var completed = _leaveCompleted;
        _leaveCompleted = null;
        completed?.Invoke();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

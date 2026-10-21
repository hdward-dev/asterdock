using NetworkAccelerator.Core.Services;

namespace NetworkAccelerator.Module.ViewModels;

public sealed class TrafficTrace
{
    private readonly TrafficSeriesBuffer _buffer = new();
    private int _revision;

    public event Action? Changed;

    public int Count => _buffer.Count;
    public int Revision => _revision;

    public double SampleAt(int index) => _buffer[index];

    public void Push(double value)
    {
        _buffer.Push(value);
        _revision++;
        Changed?.Invoke();
    }

    public void SeedWave(double peak)
    {
        _buffer.SeedWave(peak);
        _revision++;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_buffer.Count == 0) return;
        _buffer.Clear();
        _revision++;
        Changed?.Invoke();
    }
}

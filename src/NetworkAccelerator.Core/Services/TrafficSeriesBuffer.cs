namespace NetworkAccelerator.Core.Services;

/// <summary>
/// Fixed-capacity series for a traffic sparkline. New samples push older ones left.
/// </summary>
public sealed class TrafficSeriesBuffer
{
    public const int Capacity = 32;
    private readonly double[] _samples = new double[Capacity];
    private int _count;

    public int Count => _count;

    public double this[int index] => _samples[index];

    public void Clear() => _count = 0;

    public void Push(double value)
    {
        if (double.IsNaN(value) || value < 0) value = 0;
        if (_count < Capacity)
        {
            _samples[_count++] = value;
            return;
        }

        Array.Copy(_samples, 1, _samples, 0, Capacity - 1);
        _samples[Capacity - 1] = value;
    }

    public void SeedWave(double peak)
    {
        if (peak < 0 || double.IsNaN(peak)) peak = 0;
        _count = Capacity;
        for (var i = 0; i < Capacity; i++)
        {
            var phase = i / (double)(Capacity - 1);
            var wave = 0.42 + 0.58 * Math.Sin(phase * Math.PI);
            _samples[i] = peak * wave;
        }
    }
}

using NetworkAccelerator.Core.Models;

namespace NetworkAccelerator.Core.Services;

public sealed class RecentConnectionBuffer
{
    private readonly int _capacity;
    private readonly LinkedList<ConnectionRow> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private Dictionary<string, ConnectionRow> _previous = new(StringComparer.Ordinal);

    public RecentConnectionBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public IReadOnlyList<ConnectionRow> Items
    {
        get
        {
            lock (_items) return _items.ToArray();
        }
    }

    public void Observe(IReadOnlyList<ConnectionRow> live)
    {
        var current = new Dictionary<string, ConnectionRow>(StringComparer.Ordinal);
        foreach (var row in live)
        {
            if (!string.IsNullOrWhiteSpace(row.Id)) current[row.Id] = row;
        }

        lock (_items)
        {
            foreach (var previous in _previous.Values)
            {
                if (!current.ContainsKey(previous.Id)) Push(previous);
            }
            _previous = current;
        }
    }

    public void Clear()
    {
        lock (_items)
        {
            _items.Clear();
            _ids.Clear();
            _previous.Clear();
        }
    }

    private void Push(ConnectionRow row)
    {
        if (!_ids.Add(row.Id)) return;
        _items.AddFirst(row);
        while (_items.Count > _capacity)
        {
            var last = _items.Last;
            if (last is null) break;
            _ids.Remove(last.Value.Id);
            _items.RemoveLast();
        }
    }
}

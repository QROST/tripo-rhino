namespace Tripo.Bridge;

public sealed class IdempotencyCache<TValue>
    where TValue : class
{
    private readonly int _capacity;
    private readonly Dictionary<string, TValue> _values = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public IdempotencyCache(int capacity = 128)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public bool TryGet(string key, out TValue? value)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out value);
        }
    }

    public void Store(string key, TValue value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("An idempotency key is required.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (_values.ContainsKey(key))
            {
                _values[key] = value;
                return;
            }

            while (_values.Count >= _capacity)
            {
                string oldest = _order.Dequeue();
                _values.Remove(oldest);
            }

            _values.Add(key, value);
            _order.Enqueue(key);
        }
    }
}

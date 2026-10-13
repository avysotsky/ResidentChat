namespace ResidentChat.Core.Ingress;

/// <summary>
/// Process-local bounded replay window. NOT durable across restarts / multi-instance deploys.
/// Must be replaced with transactional persistent idempotency before side effects are enabled.
/// </summary>
public sealed class BoundedUpdateDeduplicator
{
    private readonly object _sync = new();
    private readonly HashSet<long> _seen = [];
    private readonly Queue<long> _order = new();
    private readonly int _capacity;

    public BoundedUpdateDeduplicator(int capacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public bool TryAccept(long updateId)
    {
        if (updateId < 0) return false;
        lock (_sync)
        {
            if (!_seen.Add(updateId)) return false;

            _order.Enqueue(updateId);
            if (_order.Count > _capacity)
                _seen.Remove(_order.Dequeue());
            return true;
        }
    }
}

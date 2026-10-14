namespace FileKakari;

/// <summary>Keeps suppression active until every overlapping operation has finished.</summary>
public sealed class OperationSuppression
{
    private int _count;
    public bool IsActive => Volatile.Read(ref _count) > 0;

    public IDisposable Enter()
    {
        Interlocked.Increment(ref _count);
        return new Lease(this);
    }

    private sealed class Lease(OperationSuppression owner) : IDisposable
    {
        private OperationSuppression? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) Interlocked.Decrement(ref current._count);
        }
    }
}

namespace PCBoost.Diagnostics.Monitoring;

/// <summary>Tampon circulaire de capacité fixe (non synchronisé : l'appelant verrouille). Aucune allocation à l'ajout.</summary>
internal sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    /// <summary>Élément à la position <paramref name="index"/> (0 = le plus ancien).</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_start + index) % _items.Length];
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }
}

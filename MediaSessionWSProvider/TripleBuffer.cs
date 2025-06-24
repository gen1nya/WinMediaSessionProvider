namespace MediaSessionWSProvider;

public class TripleBuffer<T>
{
    private readonly T[] _buffers = new T[3];
    private int _readIndex = 0;
    private int _writeIndex = 1;
    private int _standbyIndex = 2;
    private readonly object _lock = new();

    public TripleBuffer(Func<T> factory)
    {
        for (int i = 0; i < 3; i++) _buffers[i] = factory();
    }

    public T GetWriteBuffer() => _buffers[_writeIndex];

    public void Publish()
    {
        lock (_lock)
        {
            var tmp = _readIndex;
            _readIndex = _writeIndex;
            _writeIndex = _standbyIndex;
            _standbyIndex = tmp;
        }
    }

    public T GetReadBuffer() => _buffers[_readIndex];
}
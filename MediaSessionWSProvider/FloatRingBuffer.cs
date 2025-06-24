namespace MediaSessionWSProvider;

public class FloatRingBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _count;

    public int Count => _count;
    public int Capacity => _buffer.Length;

    public FloatRingBuffer(int size)
    {
        _buffer = new float[size];
    }

    public void Write(float[] data, int offset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            _buffer[_writePos] = data[offset + i];
            _writePos = (_writePos + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    public void ReadLatest(float[] dest)
    {
        if (_count < dest.Length)
            throw new InvalidOperationException("Not enough data");

        int start = (_writePos - dest.Length + _buffer.Length) % _buffer.Length;
        for (int i = 0; i < dest.Length; i++)
        {
            dest[i] = _buffer[(start + i) % _buffer.Length];
        }
    }
}
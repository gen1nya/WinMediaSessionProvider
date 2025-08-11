using NAudio.Dsp;
using System.Threading;

namespace NativeMediaSession;

public class FftPipeline : IDisposable
{
    private const double DbFloor = -80.0;
    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _columns;
    private readonly int _publishIntervalMs;
    private readonly FloatRingBuffer _sampleBuffer;
    private readonly float[] _fftInput;
    private readonly Complex[] _fftBuf;
    private readonly TripleBuffer<float[]> _spectrumBuffer;

    private Timer? _notifyTimer;
    private int[]? _startBin;
    private int[]? _endBin;
    private int _sampleRate;

    public event Action<float[]>? SpectrumAvailable;

    public int HopSize => _hopSize;
    public int Columns => _columns;

    // band centers copied from original FftService
    private static readonly double[] _bandCenters =
    {
        20, 31, 37, 42, 48, 53, 58, 63, 68, 73, 79, 84, 89, 95, 101, 107, 113, 119, 125, 132, 138, 145, 152, 159, 166, 174, 182,
        189, 197, 205, 214, 222, 231, 240, 249, 259, 268, 278, 288, 298, 308, 319, 330, 341, 353, 364, 376, 388, 401, 413, 426, 439,
        453, 466, 480, 495, 509, 524, 539, 555, 570, 587, 603, 620, 637, 654, 672, 690, 708, 727, 746, 766, 785, 806, 826, 847, 869,
        890, 912, 935, 958, 981, 1005, 1029, 1054, 1079, 1105, 1131, 1157, 1184, 1211, 1239, 1268, 1296, 1326, 1356, 1386, 1417, 1448,
        1480, 1513, 1546, 1579, 1613, 1648, 1683, 1719, 1755, 1792, 1830, 1868, 1907, 1946, 1986, 2027, 2069, 2111, 2153, 2197, 2241,
        2286, 2331, 2377, 2424, 2472, 2520, 2569, 2619, 2670, 2721, 2774, 2827, 2880, 2935, 2991, 3047, 3104, 3162, 3221, 3281, 3341,
        3403, 3465, 3529, 3593, 3658, 3724, 3791, 3860, 3929, 3999, 4070, 4142, 4215, 4289, 4365, 4441, 4519, 4597, 4677, 4758, 4839,
        4923, 5007, 5092, 5179, 5267, 5356, 5446, 5537, 5630, 5724, 5820, 5916, 6014, 6113, 6214, 6316, 6419, 6524, 6630, 6738, 6847,
        6957, 7069, 7183, 7298, 7414, 7532, 7652, 7773, 7896, 8020, 8146, 8274, 8403, 8534, 8667, 8802, 8938, 9076, 9215, 9357, 9500,
        9645, 9792, 9941, 10092, 10244, 10399, 10556, 10714, 10875, 11037, 11202, 11369, 11537, 11708, 11881, 12056, 12234, 12413, 12595,
        12779, 12965, 13153, 13344, 13537, 13733, 13931, 14131, 14334, 14539, 14747, 14957, 15170, 15385, 15603, 15824, 16047, 16273,
        16501, 16732, 16966, 17203, 17443, 17685, 17931, 18179, 18430, 18684, 18941, 19201, 19464, 19731, 20000
    };

    public FftPipeline(int fftSize, int hopSize, int columns, int publishIntervalMs)
    {
        if (columns > _bandCenters.Length)
            throw new ArgumentOutOfRangeException(nameof(columns));
        _fftSize = fftSize;
        _hopSize = hopSize;
        _columns = columns;
        _publishIntervalMs = publishIntervalMs;
        _sampleBuffer = new FloatRingBuffer(fftSize * 4);
        _fftInput = new float[fftSize];
        _fftBuf = new Complex[fftSize];
        _spectrumBuffer = new(() => new float[columns]);
    }

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = sampleRate;
        CalculateBins();
    }

    private void CalculateBins()
    {
        int halfBins = _fftSize / 2;
        double binWidth = _sampleRate / (double)_fftSize;
        _startBin = new int[_columns];
        _endBin = new int[_columns];
        for (int b = 0; b < _columns; b++)
        {
            double fLo = b == 0 ? 0 : Math.Sqrt(_bandCenters[b - 1] * _bandCenters[b]);
            double fHi = b == _columns - 1 ? _sampleRate / 2.0 - 1 : Math.Sqrt(_bandCenters[b] * _bandCenters[b + 1]);
            _startBin[b] = (int)Math.Floor(fLo / binWidth);
            _endBin[b] = (int)Math.Ceiling(fHi / binWidth);
            if (_startBin[b] < 0) _startBin[b] = 0;
            if (_endBin[b] > halfBins) _endBin[b] = halfBins;
            if (_endBin[b] <= _startBin[b]) _endBin[b] = _startBin[b] + 1;
        }
    }

    public void Start()
    {
        _notifyTimer = new Timer(_ => NotifySpectrum(), null, 0, _publishIntervalMs);
    }

    public void Stop()
    {
        _notifyTimer?.Dispose();
        _notifyTimer = null;
    }

    public void ProcessHop(float[] hop, int count)
    {
        _sampleBuffer.Write(hop, 0, count);
        if (_sampleBuffer.Count < _fftSize) return;

        _sampleBuffer.ReadLatest(_fftInput);
        for (int i = 0; i < _fftSize; i++)
        {
            float windowed = _fftInput[i] * (float)FastFourierTransform.HammingWindow(i, _fftSize);
            _fftBuf[i].X = windowed;
            _fftBuf[i].Y = 0;
        }
        FastFourierTransform.FFT(true, (int)Math.Log2(_fftSize), _fftBuf);

        var spectrum = _spectrumBuffer.GetWriteBuffer();
        double masterGain = 2;
        for (int b = 0; b < _columns; b++)
        {
            double sum = 0;
            int binCnt = 0;
            for (int j = _startBin![b]; j < _endBin![b]; j++, binCnt++)
            {
                double re = _fftBuf[j].X;
                double im = _fftBuf[j].Y;
                sum += Math.Sqrt(re * re + im * im);
            }
            double lin = binCnt > 0 ? sum / binCnt : 0;
            double db = 20 * Math.Log10(lin + 1e-20);
            double clamped = Math.Max(db, DbFloor);
            double norm = (double)(b + 10) / (_columns + 10);
            double gain = Math.Pow(norm, 0.35);
            spectrum[b] = (float)(((clamped - DbFloor) / -DbFloor) * gain * masterGain);
        }
        _spectrumBuffer.Publish();
    }

    private void NotifySpectrum()
    {
        float[] data = _spectrumBuffer.GetReadBuffer();
        float[] copy = new float[data.Length];
        Array.Copy(data, copy, data.Length);
        SpectrumAvailable?.Invoke(copy);
    }

    public void Dispose()
    {
        Stop();
    }
}

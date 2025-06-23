using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Timer = System.Threading.Timer;

namespace MediaSessionWSProvider;

public class FftService : IDisposable
{
    private const int FftLength = 2048;
    private const int Columns = 256;
    private const double DbFloor = -60.0;

    private readonly double[] _bandCenters =
    {
        20, 31, 37, 42, 48, 53, 58, 63, 68, 73, 79, 84, 89, 95, 101, 107, 113, 119, 125, 132, 138, 145, 152, 159, 166, 174, 182, 189, 197, 205, 214, 222, 231, 240, 249, 259, 268, 278, 288, 298, 308, 319, 330, 341, 353, 364, 376, 388, 401, 413, 426, 439, 453, 466, 480, 495, 509, 524, 539, 555, 570, 587, 603, 620, 637, 654, 672, 690, 708, 727, 746, 766, 785, 806, 826, 847, 869, 890, 912, 935, 958, 981, 1005, 1029, 1054, 1079, 1105, 1131, 1157, 1184, 1211, 1239, 1268, 1296, 1326, 1356, 1386, 1417, 1448, 1480, 1513, 1546, 1579, 1613, 1648, 1683, 1719, 1755, 1792, 1830, 1868, 1907, 1946, 1986, 2027, 2069, 2111, 2153, 2197, 2241, 2286, 2331, 2377, 2424, 2472, 2520, 2569, 2619, 2670, 2721, 2774, 2827, 2880, 2935, 2991, 3047, 3104, 3162, 3221, 3281, 3341, 3403, 3465, 3529, 3593, 3658, 3724, 3791, 3860, 3929, 3999, 4070, 4142, 4215, 4289, 4365, 4441, 4519, 4597, 4677, 4758, 4839, 4923, 5007, 5092, 5179, 5267, 5356, 5446, 5537, 5630, 5724, 5820, 5916, 6014, 6113, 6214, 6316, 6419, 6524, 6630, 6738, 6847, 6957, 7069, 7183, 7298, 7414, 7532, 7652, 7773, 7896, 8020, 8146, 8274, 8403, 8534, 8667, 8802, 8938, 9076, 9215, 9357, 9500, 9645, 9792, 9941, 10092, 10244, 10399, 10556, 10714, 10875, 11037, 11202, 11369, 11537, 11708, 11881, 12056, 12234, 12413, 12595, 12779, 12965, 13153, 13344, 13537, 13733, 13931, 14131, 14334, 14539, 14747, 14957, 15170, 15385, 15603, 15824, 16047, 16273, 16501, 16732, 16966, 17203, 17443, 17685, 17931, 18179, 18430, 18684, 18941, 19201, 19464, 19731, 20000
        /*20, 31.5, 50, 80, 125, 200, 315, 500,
        800, 1250, 2000, 3150, 5000, 8000, 12500, 20000*/
    };

    private readonly ILogger<FftService> _logger;
    private readonly SettingsService _settings;
    private readonly MMDeviceEnumerator _enumr = new();
    private readonly List<AudioDeviceInfo> _devices;

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private Timer? _notifyTimer;
    private readonly object _dataLock = new();
    private readonly float[] _latestSpectrum = new float[Columns];

    private int[]? _startBin;
    private int[]? _endBin;
    private int _sampleRate;
    private int _bytesPerSample;
    private int _channels;
    private int _bytesNeeded;
    private readonly Complex[] _fftBuf = new Complex[FftLength];

    private bool _enabled;

    public bool IsEnabled => _enabled;

    public record AudioDeviceInfo(string Name, MMDevice Device, DataFlow Flow);

    public AudioDeviceInfo? CurrentDevice { get; private set; }

    public event Action<float[]>? SpectrumAvailable;

    public FftService(ILogger<FftService> logger, SettingsService settings)
    {
        _logger = logger;
        _settings = settings;

        _devices = EnumerateDevices();
        if (_devices.Count > 0)
        {
            var savedId = _settings.Data.DeviceId;
            var dev = _devices.FirstOrDefault(d => d.Device.ID == savedId) ?? _devices[0];
            CurrentDevice = dev;
        }

        _enabled = _settings.Data.FftEnabled;
        if (_enabled)
        {
            StartCapture();
        }
    }

    public List<AudioDeviceInfo> GetDevices() => new(_devices);

    private List<AudioDeviceInfo> EnumerateDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            foreach (var d in _enumr.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDeviceInfo(d.FriendlyName, d, DataFlow.Render));
            foreach (var d in _enumr.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDeviceInfo(d.FriendlyName, d, DataFlow.Capture));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Ошибка при перечислении устройств");
        }
        return list;
    }

    public void SetDevice(AudioDeviceInfo info)
    {
        CurrentDevice = info;
        _settings.Data.DeviceId = info.Device.ID;
        _settings.Save();
        _logger.LogInformation("Selected device: {Device}", info.Name);
        if (_enabled)
            RestartCapture();
    }

    public void Enable(bool enable)
    {
        if (_enabled == enable) return;
        _enabled = enable;
        _settings.Data.FftEnabled = enable;
        _settings.Save();
        if (enable) StartCapture();
        else StopCapture();
    }

    private void RestartCapture()
    {
        StopCapture();
        StartCapture();
    }

    private void StartCapture()
    {
        if (CurrentDevice == null) return;
        try
        {
            _capture = CurrentDevice.Flow == DataFlow.Render
                ? new WasapiLoopbackCapture(CurrentDevice.Device)
                : new WasapiCapture(CurrentDevice.Device) { ShareMode = AudioClientShareMode.Shared };
            _buffer = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true };
            _capture.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _capture.StartRecording();

            _sampleRate = _capture.WaveFormat.SampleRate;
            _bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
            _channels = _capture.WaveFormat.Channels;
            _bytesNeeded = FftLength * _bytesPerSample * _channels;
            CalculateBins();

            _cts = new CancellationTokenSource();
            _worker = new Thread(() => WorkerLoop(_cts.Token)) { IsBackground = true };
            _worker.Start();

            _notifyTimer = new Timer(_ => NotifySpectrum(), null, 0, 33);

            _logger.LogInformation("FFT capture started");
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Ошибка старта захвата FFT");
            StopCapture();
        }
    }

    private void StopCapture()
    {
        try
        {
            _cts?.Cancel();
            _worker?.Join();
            _notifyTimer?.Dispose();
            _notifyTimer = null;
            if (_capture != null)
            {
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Ошибка остановки захвата FFT");
        }
    }

    private void CalculateBins()
    {
        int halfBins = FftLength / 2;
        double binWidth = _sampleRate / (double)FftLength;
        _startBin = new int[Columns];
        _endBin = new int[Columns];
        for (int b = 0; b < Columns; b++)
        {
            double fLo = b == 0 ? 0 : Math.Sqrt(_bandCenters[b - 1] * _bandCenters[b]);
            double fHi = b == Columns - 1 ? _sampleRate / 2.0 - 1 : Math.Sqrt(_bandCenters[b] * _bandCenters[b + 1]);
            _startBin[b] = (int)Math.Floor(fLo / binWidth);
            _endBin[b] = (int)Math.Ceiling(fHi / binWidth);
            if (_startBin[b] < 0) _startBin[b] = 0;
            if (_endBin[b] > halfBins) _endBin[b] = halfBins;
            if (_endBin[b] <= _startBin[b]) _endBin[b] = _startBin[b] + 1;
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        if (_buffer == null) return;
        while (!token.IsCancellationRequested)
        {
            if (_buffer.BufferedBytes < _bytesNeeded)
            {
                Thread.Sleep(5);
                continue;
            }

            var raw = new byte[_bytesNeeded];
            _buffer.Read(raw, 0, raw.Length);

            float maxSample = 0f;
            for (int i = 0; i < FftLength; i++)
            {
                int pos = i * _bytesPerSample * _channels;
                float sample = _bytesPerSample == 4 ? BitConverter.ToSingle(raw, pos)
                                                    : BitConverter.ToInt16(raw, pos) / 32768f;
                sample *= (float)FastFourierTransform.HammingWindow(i, FftLength);
                _fftBuf[i].X = sample;
                _fftBuf[i].Y = 0;
                float abs = Math.Abs(sample);
                if (abs > maxSample) maxSample = abs;
            }
            FastFourierTransform.FFT(true, (int)Math.Log2(FftLength), _fftBuf);

            var res = new float[Columns];
            for (int b = 0; b < Columns; b++)
            {
                double sum = 0;
                int binCnt = 0;
                for (int j = _startBin![b]; j < _endBin![b]; j++, binCnt++)
                {
                    var re = _fftBuf[j].X;
                    var im = _fftBuf[j].Y;
                    sum += Math.Sqrt(re * re + im * im);
                }
                double lin = binCnt > 0 ? sum / binCnt : 0;
                double db = 20 * Math.Log10(lin + 1e-20);
                double clamped = Math.Max(db, DbFloor);
                res[b] = (float)((clamped - DbFloor) / -DbFloor);
            }

            float maxRes = res.Max();
            //_logger.LogInformation("WorkerLoop buffer max {BufferMax}, FFT max {FftMax}", maxSample, maxRes);

            lock (_dataLock)
            {
                Array.Copy(res, _latestSpectrum, Columns);
            }
        }
    }

    private void NotifySpectrum()
    {
        float[] data = new float[Columns];
        lock (_dataLock)
        {
            Array.Copy(_latestSpectrum, data, Columns);
        }

        try
        {
            SpectrumAvailable?.Invoke(data);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Ошибка SpectrumAvailable обработчика");
        }
    }

    public void Dispose()
    {
        StopCapture();
        _enumr.Dispose();
    }
}

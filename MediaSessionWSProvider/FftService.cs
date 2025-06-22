using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace MediaSessionWSProvider;

public class FftService : IDisposable
{
    private const int FftLength = 1024;
    private const int Columns = 16;
    private const double DbFloor = -60.0;

    private readonly double[] _bandCenters =
    {
        20, 31.5, 50, 80, 125, 200, 315, 500,
        800, 1250, 2000, 3150, 5000, 8000, 12500, 20000
    };

    private readonly ILogger<FftService> _logger;
    private readonly MMDeviceEnumerator _enumr = new();

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

    public record AudioDeviceInfo(string Name, MMDevice Device, DataFlow Flow);

    public AudioDeviceInfo? CurrentDevice { get; private set; }

    public event Action<float[]>? SpectrumAvailable;

    public FftService(ILogger<FftService> logger)
    {
        _logger = logger;
    }

    public List<AudioDeviceInfo> GetDevices()
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
        _logger.LogInformation("Selected device: {Device}", info.Name);
        if (_enabled)
            RestartCapture();
    }

    public void Enable(bool enable)
    {
        if (_enabled == enable) return;
        _enabled = enable;
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

            for (int i = 0; i < FftLength; i++)
            {
                int pos = i * _bytesPerSample * _channels;
                float sample = _bytesPerSample == 4 ? BitConverter.ToSingle(raw, pos)
                                                    : BitConverter.ToInt16(raw, pos) / 32768f;
                sample *= (float)FastFourierTransform.HammingWindow(i, FftLength);
                _fftBuf[i].X = sample;
                _fftBuf[i].Y = 0;
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

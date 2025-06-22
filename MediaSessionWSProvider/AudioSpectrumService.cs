using System;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Dsp;
using Microsoft.Extensions.Logging;

namespace MediaSessionWSProvider;

public class AudioSpectrumService : IDisposable
{
    private readonly ILogger<AudioSpectrumService> _logger;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private Timer? _timer;
    private readonly Channel<float[]> _channel = Channel.CreateUnbounded<float[]>();

    public ChannelReader<float[]> SpectrumReader => _channel.Reader;

    public AudioSpectrumService(ILogger<AudioSpectrumService> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _capture = new WasapiLoopbackCapture();
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true
        };
        _capture.DataAvailable += (s, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _capture.RecordingStopped += (s, e) => _logger.LogInformation("Audio capture stopped");
        _capture.StartRecording();
        _timer = new Timer(ProcessBuffer, null, 0, 33);
        _logger.LogInformation("Audio capture started");
    }

    private void ProcessBuffer(object? state)
    {
        const int fftLength = 1024; // power of two
        if (_buffer == null) return;
        int bytesNeeded = fftLength * sizeof(float);
        if (_buffer.BufferedBytes < bytesNeeded) return;

        var bytes = new byte[bytesNeeded];
        _buffer.Read(bytes, 0, bytes.Length);

        Complex[] fftBuffer = new Complex[fftLength];
        for (int i = 0; i < fftLength; i++)
        {
            float sample = BitConverter.ToSingle(bytes, i * sizeof(float));
            sample *= (float)FastFourierTransform.HammingWindow(i, fftLength);
            fftBuffer[i] = new Complex(sample, 0);
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(fftLength), fftBuffer);

        var bands = new float[10];
        int binsPerBand = (fftLength / 2) / bands.Length;
        for (int b = 0; b < bands.Length; b++)
        {
            float sum = 0f;
            int start = b * binsPerBand;
            int end = start + binsPerBand;
            for (int j = start; j < end; j++)
            {
                var mag = (float)Math.Sqrt(fftBuffer[j].Real * fftBuffer[j].Real + fftBuffer[j].Imaginary * fftBuffer[j].Imaginary);
                sum += mag;
            }
            bands[b] = sum / binsPerBand;
        }

        _channel.Writer.TryWrite(bands);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_capture != null)
        {
            _capture.StopRecording();
            _capture.Dispose();
        }
    }
}

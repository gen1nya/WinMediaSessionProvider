using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Threading;

namespace NativeMediaSession;

public class AudioService : IDisposable
{
    private readonly string _deviceId;
    private readonly FftPipeline _pipeline;
    private readonly CallbackDispatcher _dispatcher;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private int _bytesPerSample;
    private int _channels;

    public event Action<float[]>? SpectrumAvailable;

    public AudioService(string deviceId, FftPipeline pipeline, CallbackDispatcher dispatcher)
    {
        _deviceId = deviceId;
        _pipeline = pipeline;
        _dispatcher = dispatcher;
        _pipeline.SpectrumAvailable += data => _dispatcher.Enqueue(() => SpectrumAvailable?.Invoke(data));
    }

    public int Start()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_deviceId);
            _capture = device.DataFlow == DataFlow.Render
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
            _buffer = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true };
            _capture.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _capture.StartRecording();

            _bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
            _channels = _capture.WaveFormat.Channels;
            _pipeline.SetSampleRate(_capture.WaveFormat.SampleRate);
            _pipeline.Start();

            _cts = new CancellationTokenSource();
            _worker = new Thread(() => WorkerLoop(_cts.Token)) { IsBackground = true };
            _worker.Start();
            return 0;
        }
        catch
        {
            Stop();
            return -2; // wasapi_error
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        if (_buffer == null) return;
        int hopSize = _pipeline.HopSize;
        int bytesPerHop = hopSize * _bytesPerSample * _channels;
        byte[] tempBytes = new byte[bytesPerHop];
        float[] hopSamples = new float[hopSize];
        while (!token.IsCancellationRequested)
        {
            if (_buffer.BufferedBytes < bytesPerHop)
            {
                Thread.Sleep(2);
                continue;
            }
            int bytesRead = _buffer.Read(tempBytes, 0, bytesPerHop);
            int samplesRead = bytesRead / (_bytesPerSample * _channels);
            if (samplesRead == 0) continue;
            for (int i = 0; i < samplesRead; i++)
            {
                int pos = i * _bytesPerSample * _channels;
                float sample = _bytesPerSample switch
                {
                    4 => BitConverter.ToSingle(tempBytes, pos),
                    2 => BitConverter.ToInt16(tempBytes, pos) / 32768f,
                    _ => 0f
                };
                hopSamples[i] = sample;
            }
            _pipeline.ProcessHop(hopSamples, samplesRead);
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _worker?.Join();
            _pipeline.Stop();
            if (_capture != null)
            {
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch { }
    }

    public void Dispose() => Stop();
}

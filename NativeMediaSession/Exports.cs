using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace NativeMediaSession;

public static class Exports
{
    private static bool _initialized;
    private static readonly MMDeviceEnumerator _enumerator = new();
    private static readonly CallbackDispatcher _dispatcher = new();
    private static string? _deviceId;
    private static FftConfig _config = new(4096, 256, 256, 32);
    private static AudioService? _audio;
    private static FftPipeline? _pipeline;
    private static readonly List<FftSubscription> _fftSubs = new();
    private static readonly MetadataCache _metadataCache = new();
    private static GsmtcService? _gsmtc;
    private static readonly List<MetadataSubscription> _metaSubs = new();

    private readonly record struct FftConfig(int FftSize, int HopSize, int Columns, int PublishIntervalMs);

    private unsafe struct FftSubscription
    {
        public delegate* unmanaged<float*, int, void*, void> Callback;
        public IntPtr User;
    }

    private unsafe struct MetadataSubscription
    {
        public delegate* unmanaged<byte*, int, void*, void> Callback;
        public IntPtr User;
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_init")]
    public static int Init()
    {
        _initialized = true;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_shutdown")]
    public static void Shutdown()
    {
        StopInternal();
        GsmtcStopInternal();
        _enumerator.Dispose();
        _dispatcher.Dispose();
        _metaSubs.Clear();
        _fftSubs.Clear();
        _initialized = false;
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_list_devices")]
    public static unsafe int ListDevices(byte** jsonUtf8, int* len)
    {
        if (jsonUtf8 == null || len == null) return -4; // invalid_param
        *jsonUtf8 = (byte*)0;
        *len = 0;
        try
        {
            var list = new List<object>();
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new { id = d.ID, name = d.FriendlyName, flow = "render" });
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new { id = d.ID, name = d.FriendlyName, flow = "capture" });
            string json = JsonSerializer.Serialize(list);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            byte* ptr = (byte*)Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, (IntPtr)ptr, bytes.Length);
            *len = bytes.Length;
            *jsonUtf8 = ptr;
            return 0;
        }
        catch
        {
            return -2; // wasapi_error
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_free")]
    public static unsafe void Free(void* p)
    {
        if (p != null)
            Marshal.FreeHGlobal((IntPtr)p);
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_set_device")]
    public static unsafe int SetDevice(byte* deviceIdUtf8)
    {
        if (deviceIdUtf8 == null) return -4;
        try
        {
            string id = Marshal.PtrToStringUTF8((IntPtr)deviceIdUtf8)!;
            _enumerator.GetDevice(id); // verify
            _deviceId = id;
            return 0;
        }
        catch
        {
            return -1; // no_device
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_set_fft_params")]
    public static int SetFftParams(int fftSize, int hopSize, int columns, int publishIntervalMs)
    {
        if (fftSize <= 0 || hopSize <= 0 || columns <= 0 || publishIntervalMs <= 0) return -4;
        if ((fftSize & (fftSize - 1)) != 0) return -4; // fftSize must be power of two
        if (columns > 256) return -4;
        _config = new FftConfig(fftSize, hopSize, columns, publishIntervalMs);
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_start")]
    public static int Start()
        => StartInternal();

    private static int StartInternal()
    {
        if (_audio != null) return -3;
        if (string.IsNullOrEmpty(_deviceId)) return -1;
        try
        {
            _pipeline = new FftPipeline(_config.FftSize, _config.HopSize, _config.Columns, _config.PublishIntervalMs);
            _audio = new AudioService(_deviceId!, _pipeline, _dispatcher);
            _audio.SpectrumAvailable += OnSpectrum;
            int res = _audio.Start();
            if (res != 0)
            {
                _audio.SpectrumAvailable -= OnSpectrum;
                _audio = null;
                _pipeline = null;
            }
            return res;
        }
        catch
        {
            _audio = null;
            _pipeline = null;
            return -2;
        }
    }

    private static unsafe void OnSpectrum(float[] data)
    {
        foreach (var sub in _fftSubs.ToArray())
        {
            int len = data.Length;
            int byteLen = len * sizeof(float);
            IntPtr buf = Marshal.AllocHGlobal(byteLen);
            unsafe
            {
                fixed (float* src = data)
                {
                    Buffer.MemoryCopy(src, (void*)buf, byteLen, byteLen);
                }
                sub.Callback((float*)buf, len, (void*)sub.User);
            }
            Marshal.FreeHGlobal(buf);
        }
    }

    private static unsafe void OnMetadata(FullMediaState state)
    {
        string json = JsonSerializer.Serialize(state);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        foreach (var sub in _metaSubs.ToArray())
        {
            IntPtr buf = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            sub.Callback((byte*)buf, bytes.Length, (void*)sub.User);
            Marshal.FreeHGlobal(buf);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_stop")]
    public static int Stop()
        => StopInternal();

    private static int StopInternal()
    {
        if (_audio == null) return 0;
        _audio.SpectrumAvailable -= OnSpectrum;
        _audio.Stop();
        _audio = null;
        _pipeline = null;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_restart")]
    public static int Restart()
    {
        int r = StopInternal();
        if (r < 0) return r;
        return StartInternal();
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_subscribe_fft")]
    public static unsafe void SubscribeFft(delegate* unmanaged<float*, int, void*, void> cb, void* user)
    {
        _fftSubs.Add(new FftSubscription { Callback = cb, User = (IntPtr)user });
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_unsubscribe_fft")]
    public static unsafe void UnsubscribeFft(delegate* unmanaged<float*, int, void*, void> cb)
    {
        _fftSubs.RemoveAll(s => s.Callback == cb);
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_subscribe_metadata")]
    public static unsafe void SubscribeMetadata(delegate* unmanaged<byte*, int, void*, void> cb, void* user)
    {
        _metaSubs.Add(new MetadataSubscription { Callback = cb, User = (IntPtr)user });
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_unsubscribe_metadata")]
    public static unsafe void UnsubscribeMetadata(delegate* unmanaged<byte*, int, void*, void> cb)
    {
        _metaSubs.RemoveAll(s => s.Callback == cb);
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_gsmtc_start")]
    public static int GsmtcStart()
    {
        if (_gsmtc != null) return -3;
        try
        {
            _gsmtc = new GsmtcService(_dispatcher, _metadataCache);
            _gsmtc.MetadataAvailable += OnMetadata;
            int res = _gsmtc.Start();
            if (res != 0)
            {
                _gsmtc.MetadataAvailable -= OnMetadata;
                _gsmtc = null;
            }
            return res;
        }
        catch
        {
            _gsmtc = null;
            return -5;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_gsmtc_stop")]
    public static int GsmtcStop()
        => GsmtcStopInternal();

    private static int GsmtcStopInternal()
    {
        if (_gsmtc == null) return 0;
        _gsmtc.MetadataAvailable -= OnMetadata;
        _gsmtc.Stop();
        _gsmtc = null;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "ms_gsmtc_get_last")]
    public static unsafe int GsmtcGetLast(byte** jsonUtf8, int* len)
    {
        if (jsonUtf8 == null || len == null) return -4;
        *jsonUtf8 = (byte*)0;
        *len = 0;
        var last = _metadataCache.Last;
        if (last == null) return 0;
        try
        {
            string json = JsonSerializer.Serialize(last);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            byte* ptr = (byte*)Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, (IntPtr)ptr, bytes.Length);
            *len = bytes.Length;
            *jsonUtf8 = ptr;
            return 0;
        }
        catch
        {
            return -5;
        }
    }
}

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DJIPA;

public sealed class SessionError(string message) : Exception(message) { }

/// <summary>麥克風（WASAPI 擷取）→ 訊號處理 → 輸出裝置（WASAPI 播放）。</summary>
public sealed class Session : IDisposable
{
    public Engine Engine { get; }
    public double LatencyMs { get; }
    public string? StopReason { get; private set; }
    public event Action<string>? Failed;

    readonly WasapiCapture capture;
    readonly WasapiOut output;
    readonly RingSource source;
    readonly int inChannels, bits;
    readonly bool isFloat;
    float[] mono = new float[4800];

    const int CaptureMs = 20, OutputMs = 30, TargetMs = 20;

    public Session(MMDevice input, MMDevice outputDevice)
    {
        try
        {
            capture = new WasapiCapture(input, true, CaptureMs);
            var f = capture.WaveFormat;
            inChannels = f.Channels;
            bits = f.BitsPerSample;
            isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                || (f is WaveFormatExtensible x && x.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            Engine = new Engine(f.SampleRate);

            var outFormat = outputDevice.AudioClient.MixFormat;
            source = new RingSource(f.SampleRate, outFormat.SampleRate, outFormat.Channels, TargetMs);
            output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, OutputMs);
            output.Init(source);
            LatencyMs = CaptureMs + TargetMs + OutputMs;

            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Failed?.Invoke("音訊裝置中斷：" + e.Exception.Message);
            };
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) Failed?.Invoke("輸出裝置中斷：" + e.Exception.Message);
            };
            capture.StartRecording();
            output.Play();
        }
        catch (UnauthorizedAccessException)
        {
            Dispose();
            throw new SessionError("沒有麥克風權限：請到 設定 → 隱私權與安全性 → 麥克風，開啟「讓桌面應用程式存取麥克風」");
        }
        catch (Exception e) when (e is not SessionError)
        {
            Dispose();
            throw new SessionError("無法開啟音訊裝置：" + e.Message);
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int bytesPer = bits / 8, frames = e.BytesRecorded / (bytesPer * inChannels);
        if (frames == 0) return;
        if (mono.Length < frames) mono = new float[frames];
        var b = e.Buffer;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < inChannels; c++)
            {
                int o = (i * inChannels + c) * bytesPer;
                sum += isFloat ? BitConverter.ToSingle(b, o)
                    : bits == 16 ? BitConverter.ToInt16(b, o) / 32768f
                    : bits == 24 ? ((b[o] << 8) | (b[o + 1] << 16) | (b[o + 2] << 24)) / 2147483648f
                    : BitConverter.ToInt32(b, o) / 2147483648f;
            }
            mono[i] = sum / inChannels;
        }
        Engine.Process(mono, frames);
        source.Write(mono, frames);
    }

    public void Dispose()
    {
        try { capture?.StopRecording(); } catch { }
        try { output?.Stop(); } catch { }
        capture?.Dispose();
        output?.Dispose();
    }

    /// <summary>單聲道環形緩衝；輸出端以線性內插轉取樣率，並吸收兩個裝置的時脈漂移。</summary>
    sealed class RingSource : ISampleProvider
    {
        readonly float[] ring;
        readonly object gate = new();
        readonly double step;           // 每個輸出幀前進的輸入幀數
        readonly int channels, target, max;
        long writePos, readWhole;
        double frac;
        bool primed;
        float last;

        public WaveFormat WaveFormat { get; }

        public RingSource(int inRate, int outRate, int outChannels, int targetMs)
        {
            ring = new float[inRate]; // 1 秒
            step = (double)inRate / outRate;
            channels = outChannels;
            target = inRate * targetMs / 1000;
            max = target * 3;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, outChannels);
        }

        public void Write(float[] data, int count)
        {
            lock (gate)
                for (int i = 0; i < count; i++) ring[writePos++ % ring.Length] = data[i];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            lock (gate)
            {
                long avail = writePos - readWhole;
                if (avail > max) { readWhole = writePos - target; avail = target; } // 太滿：丟掉舊的，降低延遲
                if (!primed && avail >= target) primed = true;
                for (int f = 0; f < frames; f++)
                {
                    float v = 0;
                    if (primed && writePos - readWhole >= 2)
                    {
                        float a = ring[readWhole % ring.Length], b = ring[(readWhole + 1) % ring.Length];
                        v = a + (b - a) * (float)frac;
                        frac += step;
                        int adv = (int)frac;
                        frac -= adv;
                        readWhole += adv;
                    }
                    else if (primed) primed = false; // 空了：重新累積再播，避免斷斷續續
                    last = v;
                    for (int c = 0; c < channels; c++)
                        buffer[offset + f * channels + c] = c < 2 ? v : 0f;
                }
            }
            return frames * channels;
        }
    }
}

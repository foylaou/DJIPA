namespace DJIPA;

/// <summary>低頻切除、噪音閘門、自動嘯叫抑制（由 Effects.c 移植）。</summary>
public sealed class Effects
{
    public const int Fft = 4096;
    public const int Notches = 6;
    const float HpHz = 100f, GateFloor = 0.063f, NotchQ = 20f, NotchFirstDb = -18f, NotchDeepDb = -30f;
    const int HowlRounds = 8;

    struct Biquad
    {
        public float B0, B1, B2, A1, A2, Z1, Z2;
        public void Set(float b0, float b1, float b2, float a0, float a1, float a2)
        { B0 = b0 / a0; B1 = b1 / a0; B2 = b2 / a0; A1 = a1 / a0; A2 = a2 / a0; }
        public float Run(float x)
        {
            float y = B0 * x + Z1;
            Z1 = B1 * x - A1 * y + Z2;
            Z2 = B2 * x - A2 * y;
            return y;
        }
    }

    readonly float rate;
    // 跨執行緒的控制參數
    public volatile bool HpOn, GateOn, HowlOn;
    public volatile float GateThreshold = 0.0018f;
    public readonly float[] NotchFreq = new float[Notches];
    readonly float[] notchDb = new float[Notches];
    public volatile bool GateOpened;

    Biquad hp;
    readonly Biquad[] notch = new Biquad[Notches];
    readonly float[] curFreq = new float[Notches], curDb = new float[Notches];
    float envelope, gateGain = 1f;
    int hold;
    readonly float envRelease, gateAttack, gateRelease;

    readonly float[] ring = new float[Fft];
    long written;

    float candFreq, candPower;
    int candCount, nextSlot;
    long analyzed;
    readonly float[] re = new float[Fft], im = new float[Fft], p = new float[Fft / 2];

    public Effects(double sampleRate)
    {
        rate = (float)sampleRate;
        float w = 2 * MathF.PI * HpHz / rate, c = MathF.Cos(w), alpha = MathF.Sin(w) / (2 * 0.7071f);
        hp.Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        envRelease = MathF.Exp(-1f / (0.050f * rate));
        gateAttack = 1 - MathF.Exp(-1f / (0.003f * rate));
        gateRelease = 1 - MathF.Exp(-1f / (0.150f * rate));
    }

    static void SetNotch(ref Biquad q, float rate, float freq, float db)
    {
        float w = 2 * MathF.PI * freq / rate, c = MathF.Cos(w), alpha = MathF.Sin(w) / (2 * NotchQ);
        float a = MathF.Pow(10, db / 40);
        q.Set(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a);
    }

    public float Process(float x)
    {
        float h = hp.Run(x);
        if (HpOn) x = h;

        for (int i = 0; i < Notches; i++)
        {
            float f = Volatile.Read(ref NotchFreq[i]), db = Volatile.Read(ref notchDb[i]);
            if (f != curFreq[i] || db != curDb[i])
            {
                if (f != curFreq[i]) notch[i].Z1 = notch[i].Z2 = 0;
                curFreq[i] = f; curDb[i] = db;
                if (f > 0) SetNotch(ref notch[i], rate, f, db);
            }
            if (f > 0) x = notch[i].Run(x);
        }

        float mag = MathF.Abs(x);
        envelope = mag > envelope ? mag : envelope * envRelease;
        float target = 1f;
        if (GateOn)
        {
            float th = GateThreshold;
            if (envelope >= th || (hold > 0 && envelope >= th * 0.63f)) hold = (int)(0.300f * rate);
            else if (hold > 0) hold--;
            if (hold == 0) target = GateFloor;
        }
        if (target == 1f) GateOpened = true;
        gateGain += (target > gateGain ? gateAttack : gateRelease) * (target - gateGain);
        x *= gateGain;

        long n = Volatile.Read(ref written);
        Volatile.Write(ref ring[n % Fft], x);
        Volatile.Write(ref written, n + 1);
        return x;
    }

    public void ClearNotches()
    {
        for (int i = 0; i < Notches; i++) Volatile.Write(ref NotchFreq[i], 0f);
        candCount = 0; nextSlot = 0;
    }

    static void DoFft(float[] re, float[] im)
    {
        for (int i = 1, j = 0; i < Fft; i++)
        {
            int bit = Fft >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j |= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= Fft; len <<= 1)
        {
            float a = -2 * MathF.PI / len;
            for (int k = 0; k < len / 2; k++)
            {
                float wr = MathF.Cos(a * k), wi = MathF.Sin(a * k);
                for (int i = k; i < Fft; i += len)
                {
                    int j = i + len / 2;
                    float tr = re[j] * wr - im[j] * wi, ti = re[j] * wi + im[j] * wr;
                    re[j] = re[i] - tr; im[j] = im[i] - ti;
                    re[i] += tr; im[i] += ti;
                }
            }
        }
    }

    float NearPower(float bin)
    {
        int c = (int)(bin + 0.5f);
        float best = 0;
        for (int k = c - 2; k <= c + 2; k++)
            if (k > 0 && k < Fft / 2 && p[k] > best) best = p[k];
        return best;
    }

    void AddNotch(float freq)
    {
        for (int i = 0; i < Notches; i++)
        {
            float f = Volatile.Read(ref NotchFreq[i]);
            if (f > 0 && MathF.Abs(f - freq) < f / NotchQ) { Volatile.Write(ref notchDb[i], NotchDeepDb); return; }
        }
        int slot = nextSlot;
        nextSlot = (slot + 1) % Notches;
        Volatile.Write(ref notchDb[slot], NotchFirstDb);
        Volatile.Write(ref NotchFreq[slot], freq);
    }

    /// <summary>嘯叫特徵：單一、突出、幾乎沒有泛音、頻率固定。非音訊執行緒約每 50 ms 呼叫。</summary>
    public void Analyze()
    {
        long n = Volatile.Read(ref written);
        if (!HowlOn || n < Fft || n == analyzed) { candCount = 0; return; }
        analyzed = n;
        for (int i = 0; i < Fft; i++)
        {
            float w = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / Fft);
            re[i] = Volatile.Read(ref ring[(n + i) % Fft]) * w;
            im[i] = 0;
        }
        DoFft(re, im);

        float binHz = rate / Fft, total = 0;
        int lo = (int)(150 / binHz), hi = (int)(12000 / binHz), peak = lo;
        if (hi > Fft / 2 - 2) hi = Fft / 2 - 2;
        for (int k = 1; k < Fft / 2; k++) p[k] = re[k] * re[k] + im[k] * im[k];
        for (int k = lo; k <= hi; k++) { total += p[k]; if (p[k] > p[peak]) peak = k; }
        float mean = total / (hi - lo + 1);

        float amplitude = MathF.Sqrt(p[peak]) * 4 / Fft;
        bool howl = amplitude > 0.006f
            && p[peak] > mean * 100
            && peak > lo && peak < hi
            && p[peak] > 30 * p[peak - 6 > 0 ? peak - 6 : 1]
            && p[peak] > 30 * p[peak + 6 < Fft / 2 ? peak + 6 : peak];

        float freq = 0;
        if (howl)
        {
            float a = MathF.Log(p[peak - 1] + 1e-20f), b = MathF.Log(p[peak] + 1e-20f), c = MathF.Log(p[peak + 1] + 1e-20f);
            float d = a - 2 * b + c;
            float bin = peak + (d != 0 ? 0.5f * (a - c) / d : 0);
            freq = bin * binHz;
            howl = NearPower(bin * 2) < p[peak] / 100
                && NearPower(bin * 3) < p[peak] / 100
                && NearPower(bin / 2) < p[peak] / 100;
        }
        if (!howl) { candCount = 0; return; }
        if (candCount > 0 && MathF.Abs(freq - candFreq) < binHz * 1.5f && p[peak] > candPower / 4) candCount++;
        else { candCount = 1; candPower = p[peak]; }
        candFreq = freq;
        if (candCount >= HowlRounds) { AddNotch(freq); candCount = 0; }
    }
}

/// <summary>音量平滑、峰值限幅與表頭，對應 AudioCore.c 的 pa_process。</summary>
public sealed class Engine
{
    public readonly Effects Fx;
    public volatile float Gain, Ceiling = 0.5f;
    public volatile bool Muted;
    float inPeak, outPeak;
    volatile bool limited;
    long callbacks;
    float smoothGain, limiterGain = 1f;
    readonly float rampCoef, releaseCoef;

    public Engine(double sampleRate)
    {
        Fx = new Effects(sampleRate);
        rampCoef = (float)(1.0 - Math.Exp(-1.0 / (0.030 * sampleRate)));
        releaseCoef = (float)(1.0 - Math.Exp(-1.0 / (0.250 * sampleRate)));
    }

    public float TakeInputPeak() => Interlocked.Exchange(ref inPeak, 0f);
    public float TakeOutputPeak() => Interlocked.Exchange(ref outPeak, 0f);
    public bool TakeLimited() { bool v = limited; limited = false; return v; }
    public long Callbacks => Interlocked.Read(ref callbacks);

    /// <summary>mono：已混成單聲道的輸入；結果就地寫回。</summary>
    public void Process(float[] buf, int count)
    {
        Interlocked.Increment(ref callbacks);
        float target = Muted ? 0f : Gain, ceiling = Ceiling;
        float ip = 0, op = 0;
        bool lim = false;
        for (int i = 0; i < count; i++)
        {
            float v = buf[i];
            if (!float.IsFinite(v)) v = 0;
            ip = MathF.Max(ip, MathF.Abs(v));
            v = Fx.Process(v);
            smoothGain += rampCoef * (target - smoothGain);
            v *= smoothGain;
            float mag = MathF.Abs(v);
            float needed = mag > ceiling ? ceiling / mag : 1f;
            if (needed < limiterGain) { limiterGain = needed; lim = true; }
            else limiterGain += releaseCoef * (needed - limiterGain);
            v *= limiterGain;
            v = MathF.Max(-ceiling, MathF.Min(ceiling, v));
            op = MathF.Max(op, MathF.Abs(v));
            buf[i] = v;
        }
        if (ip > Volatile.Read(ref inPeak)) Volatile.Write(ref inPeak, ip);
        if (op > Volatile.Read(ref outPeak)) Volatile.Write(ref outPeak, op);
        if (lim) limited = true;
    }
}

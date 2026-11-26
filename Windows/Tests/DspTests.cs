using DJIPA;
using Xunit;

namespace DJIPA.Tests;

/// <summary>離線測試訊號處理（不會發出聲音），對應 macOS 版 Tests/process_test.c。</summary>
public class EngineTests
{
    const int Rate = 48000;

    static Engine NewEngine(float gain = 1f, float ceiling = 0.9f) =>
        new(Rate) { Gain = gain, Ceiling = ceiling };

    static float Sine(double hz, double amp, int i) => (float)(amp * Math.Sin(2 * Math.PI * hz * i / Rate));

    /// <summary>每 10 ms 一個區塊送進引擎，每 50 ms 分析一次，回傳最後 0.1 秒的輸出峰值。</summary>
    static float Feed(Engine e, Func<int, float> signal, double seconds, int start = 0)
    {
        var buf = new float[480];
        int blocks = (int)(seconds * 100);
        float tail = 0;
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < 480; i++) buf[i] = signal(start + b * 480 + i);
            e.Process(buf, 480);
            if (b % 5 == 4) e.Fx.Analyze();
            if (b >= blocks - 10) tail = Math.Max(tail, buf.Max(Math.Abs));
        }
        return tail;
    }

    static int NotchCount(Engine e) => e.Fx.NotchFreq.Count(f => f > 0);

    // ---------- 增益、限幅、靜音 ----------

    [Fact]
    public void BelowCeiling_PassesAtOriginalLevel()
    {
        var e = NewEngine(1f, 0.5f);
        Feed(e, i => i % 2 == 0 ? -0.25f : 0.25f, 0.2);
        Assert.InRange(Feed(e, i => i % 2 == 0 ? -0.25f : 0.25f, 0.2), 0.249f, 0.251f);
        Assert.False(e.TakeLimited());
    }

    [Fact]
    public void SuddenLoud_NeverExceedsCeiling_AndReportsLimited()
    {
        var e = NewEngine(1f, 0.5f);
        var buf = new float[4800];
        for (int i = 0; i < buf.Length; i++) buf[i] = i % 2 == 0 ? -1f : 1f;
        e.Process(buf, buf.Length);
        Assert.All(buf, v => Assert.True(Math.Abs(v) <= 0.5f + 1e-6f));
        Assert.True(e.TakeLimited());
        Assert.False(e.TakeLimited()); // 讀取後歸零
    }

    [Fact]
    public void HighGain_StillClampedToCeiling()
    {
        var e = NewEngine(8f, 0.5f);
        Assert.True(Feed(e, i => i % 2 == 0 ? -1f : 1f, 0.5) <= 0.5f + 1e-6f);
    }

    [Fact]
    public void Muted_FadesToSilence()
    {
        var e = NewEngine(8f, 0.5f);
        Feed(e, i => i % 2 == 0 ? -1f : 1f, 0.2);
        e.Muted = true;
        Assert.True(Feed(e, i => i % 2 == 0 ? -1f : 1f, 0.5) < 0.001f); // 淡出 0.4 秒後應靜音
    }

    [Fact]
    public void ZeroGain_IsSilent_AtStart()
    {
        var e = new Engine(Rate) { Ceiling = 0.9f }; // 預設增益 0：開始時不會突然出聲
        Assert.True(Feed(e, i => Sine(1000, 0.5, i), 0.2) < 0.001f);
    }

    [Fact]
    public void NonFiniteInput_BecomesSilence()
    {
        var e = NewEngine();
        var buf = new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.1f };
        e.Process(buf, buf.Length);
        Assert.All(buf, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void Peaks_AreReportedAndReset()
    {
        var e = NewEngine(1f, 0.9f);
        Feed(e, i => Sine(1000, 0.3, i), 0.3);
        Assert.InRange(e.TakeInputPeak(), 0.29f, 0.31f);
        Assert.True(e.TakeOutputPeak() > 0.2f);
        Assert.Equal(0f, e.TakeInputPeak());
        Assert.Equal(0f, e.TakeOutputPeak());
    }

    [Fact]
    public void Callbacks_CountEachProcessCall()
    {
        var e = NewEngine();
        long before = e.Callbacks;
        e.Process(new float[480], 480);
        e.Process(new float[480], 480);
        Assert.Equal(before + 2, e.Callbacks);
    }

    // ---------- 低頻切除 ----------

    [Fact]
    public void HighPass_AttenuatesRumble_PassesSpeech()
    {
        var e = NewEngine();
        e.Fx.HpOn = true;
        Assert.True(Feed(e, i => Sine(30, 0.2, i), 1) < 0.03f);
        Assert.InRange(Feed(e, i => Sine(1000, 0.2, i), 1), 0.19f, 0.21f);
    }

    [Fact]
    public void HighPass_Off_PassesRumble()
    {
        var e = NewEngine();
        Assert.InRange(Feed(e, i => Sine(30, 0.2, i), 1), 0.19f, 0.21f);
    }

    // ---------- 噪音閘門 ----------

    [Fact]
    public void Gate_ClosesBelowThreshold_OpensOnSpeech_ClosesAgain()
    {
        var e = NewEngine();
        e.Fx.GateOn = true;
        e.Fx.GateThreshold = 0.01f;
        Assert.True(Feed(e, i => Sine(1000, 0.001, i), 2) < 0.0001f);       // 低於門檻：衰減 24 dB
        Assert.True(Feed(e, i => Sine(1000, 0.2, i), 0.5) > 0.19f);         // 講話：打開
        Assert.True(Feed(e, i => Sine(1000, 0.001, i), 2) < 0.0001f);       // 停止後關閉
    }

    [Fact]
    public void Gate_ReportsOpened_OnlyWhenOpen()
    {
        var e = NewEngine();
        e.Fx.GateOn = true;
        e.Fx.GateThreshold = 0.01f;
        Feed(e, i => Sine(1000, 0.001, i), 2);
        e.Fx.GateOpened = false;
        Feed(e, i => Sine(1000, 0.001, i), 0.1);
        Assert.False(e.Fx.GateOpened);
        Feed(e, i => Sine(1000, 0.2, i), 0.1);
        Assert.True(e.Fx.GateOpened);
    }

    // ---------- 自動嘯叫抑制 ----------

    [Fact]
    public void Howl_VoiceWithHarmonicsAndNoise_AreNotNotched()
    {
        var e = NewEngine();
        e.Fx.HowlOn = true;
        Feed(e, i => Sine(220, 0.2, i) + Sine(440, 0.12, i) + Sine(660, 0.08, i), 3);
        Assert.Equal(0, NotchCount(e));
        var rng = new Random(42);
        Feed(e, _ => (float)(rng.NextDouble() - 0.5) * 0.3f, 3);
        Assert.Equal(0, NotchCount(e));
    }

    [Fact]
    public void Howl_SustainedTone_IsDetectedAndAttenuated()
    {
        var e = NewEngine();
        e.Fx.HowlOn = true;
        float after = Feed(e, i => Sine(1234, 0.2, i), 3);
        Assert.True(NotchCount(e) >= 1);
        Assert.InRange(e.Fx.NotchFreq[0], 1229f, 1239f);
        Assert.True(after < 0.05f); // 至少 -12 dB（原 0.2）
    }

    [Fact]
    public void Howl_ClearNotches_Removes_AndOffDoesNotDetect()
    {
        var e = NewEngine();
        e.Fx.HowlOn = true;
        Feed(e, i => Sine(1234, 0.2, i), 3);
        Assert.True(NotchCount(e) >= 1);
        e.Fx.ClearNotches();
        Assert.Equal(0, NotchCount(e));

        e.Fx.HowlOn = false;
        Feed(e, i => Sine(1234, 0.2, i), 2);
        Assert.Equal(0, NotchCount(e));
    }

    [Fact]
    public void Howl_MoreThanSixTones_KeepsAtMostSixNotches()
    {
        var e = NewEngine();
        e.Fx.HowlOn = true;
        foreach (var hz in new[] { 700, 1234, 1900, 2500, 3100, 3900, 4700, 5600 })
            Feed(e, i => Sine(hz, 0.2, i), 2.5);
        Assert.InRange(NotchCount(e), 1, Effects.Notches);
    }
}

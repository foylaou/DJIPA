using System.Drawing.Drawing2D;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DJIPA;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            using var en = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
                foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    Console.WriteLine($"{(flow == DataFlow.Capture ? "輸入" : "輸出")}: {d.FriendlyName}");
            return;
        }
        // --selftest：增益為 0（靜音）跑 4 秒，檢查回呼是否持續、DSP 是否正常
        if (args.Contains("--selftest"))
        {
            using var en = new MMDeviceEnumerator();
            var i = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault();
            var o = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault();
            if (i == null || o == null) { Console.WriteLine("no device"); return; }
            using var s = new Session(i, o);
            Thread.Sleep(1000);
            long a = s.Engine.Callbacks; Thread.Sleep(3000);
            Console.WriteLine($"callbacks/s={(s.Engine.Callbacks - a) / 3.0:F1} inPeak={s.Engine.TakeInputPeak():F4} outPeak={s.Engine.TakeOutputPeak():F4} latency={s.LatencyMs}ms");
            return;
        }
        using var mutex = new Mutex(true, "local.djipa.windows", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        // --snapshot <png>：把面板畫成圖片檢查版面，不啟動音訊
        int si = Array.IndexOf(args, "--snapshot");
        if (si >= 0 && si + 1 < args.Length)
        {
            using var f = new MainForm(false);
            f.Snapshot(args[si + 1]);
            return;
        }
        Application.Run(new MainForm(args.Contains("--show")));
    }
}

sealed class Settings
{
    public string InputId { get; set; } = "";
    public string OutputId { get; set; } = "";
    public double VolumeDB { get; set; } = -12;
    public double CeilingDB { get; set; } = -6;
    public bool HighPass { get; set; } = true;
    public bool Gate { get; set; } = true;
    public double GateDB { get; set; } = -55;
    public bool Howl { get; set; } = true;

    static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DJIPA", "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

/// <summary>0..1 的音量表，可畫一條標記線。</summary>
sealed class Meter : Control
{
    public double Level { get => level; set { level = value; Invalidate(); } }
    public double? Marker { get => marker; set { marker = value; Invalidate(); } }
    double level; double? marker;
    public Meter() { DoubleBuffered = true; Height = 10; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(225, 225, 230));
        int w = (int)(Width * Math.Clamp(level, 0, 1));
        var color = level > 0.95 ? Color.Crimson : level > 0.8 ? Color.Orange : Color.SeaGreen;
        using (var b = new SolidBrush(color)) g.FillRectangle(b, 0, 0, w, Height);
        if (marker is double m)
        {
            int x = (int)(Width * Math.Clamp(m, 0, 1));
            using var pen = new Pen(Color.FromArgb(60, 60, 70), 2);
            g.DrawLine(pen, x, 0, x, Height);
        }
    }
}

sealed class MainForm : Form
{
    static readonly Color Brand = Color.FromArgb(37, 99, 235);

    readonly Settings s = Settings.Load();
    readonly MMDeviceEnumerator enumerator = new();
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };
    readonly Notifier notifier;

    List<MMDevice> inputs = new(), outputs = new();
    Session? session;
    bool muted, autoSelecting;
    double inputLevel, outputLevel, inputDB = -90;
    bool gateOpen, limiting;
    DateTime limitingUntil, lastProgress;
    long lastCallbacks;
    List<int> notches = new();
    string message = "";
    DateTime lastShown = DateTime.MinValue;

    readonly ComboBox inCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox outCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button startBtn = new() { Text = "開始擴音" };
    readonly Button muteBtn = new() { Text = "靜音", Enabled = false };
    readonly Meter inMeter = new(), outMeter = new();
    readonly TrackBar volume = Slider(-40, 18), ceiling = Slider(-24, -1), gateTh = Slider(-70, -20);
    readonly Label volLabel = new(), ceilLabel = new(), gateLabel = new();
    readonly CheckBox hpBox = new() { Text = "低頻切除（濾掉 100 Hz 以下）", AutoSize = true };
    readonly CheckBox gateBox = new() { Text = "噪音閘門", AutoSize = true };
    readonly CheckBox howlBox = new() { Text = "自動嘯叫抑制", AutoSize = true };
    readonly Label gateInfo = new() { AutoSize = true, ForeColor = Color.Gray };
    readonly Label howlInfo = new() { AutoSize = true, ForeColor = Color.Gray };
    readonly Button clearBtn = new() { Text = "清除", Visible = false, AutoSize = true };
    readonly Label status = new() { AutoSize = true };
    readonly Label msgLabel = new() { AutoSize = true, ForeColor = Color.Crimson, MaximumSize = new Size(320, 0) };
    readonly Label warnLabel = new() { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(320, 0) };
    readonly Label limitLabel = new() { AutoSize = true, ForeColor = Color.DarkOrange, Text = "限幅中：聲音超過上限，已自動壓低", Visible = false };

    static TrackBar Slider(int min, int max) => new()
    { Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Height = 26 };

    public MainForm(bool showAtStart)
    {
        Text = "現場擴音";
        Icon = new Icon(typeof(MainForm).Assembly.GetManifestResourceStream("AppIcon.ico")!); // 內嵌資源，單一檔案發佈也讀得到
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Microsoft JhengHei UI", 9f);
        ClientSize = new Size(344, 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout();
        LoadControls();

        tray.Icon = Icon;
        tray.Text = "現場擴音";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("開啟面板", null, (_, _) => ShowPanel());
        menu.Items.Add("結束", null, (_, _) => { Stop(); tray.Visible = false; Application.Exit(); });
        tray.ContextMenuStrip = menu;

        notifier = new Notifier(() => BeginInvoke(RefreshDevices));
        enumerator.RegisterEndpointNotificationCallback(notifier);
        RefreshDevices();

        timer.Tick += (_, _) => Tick();
        Deactivate += (_, _) => { if ((DateTime.Now - lastShown).TotalMilliseconds > 300) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        if (showAtStart) Shown += (_, _) => ShowPanel();
        Load += (_, _) => { if (!showAtStart) { Opacity = 0; BeginInvoke(() => { Hide(); Opacity = 1; }); } };
    }

    // ---------- 版面 ----------
    void BuildLayout()
    {
        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            Dock = DockStyle.Top, Padding = new Padding(12), MaximumSize = new Size(344, 0)
        };
        Controls.Add(root);
        const int W = 318;

        FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        Control Titled(string title, Control c, int width = W)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            p.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 0, 0, 1) });
            c.Width = width; p.Controls.Add(c);
            return p;
        }
        Control SliderRow(string title, TrackBar t, Label val)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
            var head = new Panel { Width = W, Height = 18 };
            head.Controls.Add(new Label { Text = title, AutoSize = true, Left = 0 });
            val.AutoSize = true; val.Anchor = AnchorStyles.Top | AnchorStyles.Right; val.Left = W - 70; val.Width = 70; val.TextAlign = ContentAlignment.TopRight;
            head.Controls.Add(val);
            p.Controls.Add(head);
            t.Width = W; p.Controls.Add(t);
            return p;
        }

        var title = new Label { Text = "現場擴音", Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), AutoSize = true };
        var head = new Panel { Width = W, Height = 34 };
        head.Controls.Add(title);
        status.Left = W - 90; status.Top = 8; status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        head.Controls.Add(status);
        root.Controls.Add(head);

        root.Controls.Add(Titled("麥克風", inCombo));
        root.Controls.Add(Titled("輸出", outCombo));
        warnLabel.Text = "內建喇叭容易嘯叫"; warnLabel.Visible = false;
        root.Controls.Add(warnLabel);

        var buttons = Row();
        foreach (var b in new[] { startBtn, muteBtn })
        { b.FlatStyle = FlatStyle.Flat; b.Height = 38; b.Font = new Font(Font, FontStyle.Bold); b.FlatAppearance.BorderSize = 0; }
        startBtn.Width = 196; muteBtn.Width = 116;
        startBtn.BackColor = Brand; startBtn.ForeColor = Color.White;
        muteBtn.BackColor = Color.FromArgb(225, 225, 230);
        startBtn.Click += (_, _) => { if (session == null) Start(); else Stop(); };
        muteBtn.Click += (_, _) => { muted = !muted; Apply(); UpdateButtons(); };
        buttons.Controls.Add(startBtn); buttons.Controls.Add(muteBtn);
        root.Controls.Add(buttons);

        root.Controls.Add(Titled("麥克風音量", inMeter));
        root.Controls.Add(Titled("輸出音量", outMeter));
        root.Controls.Add(SliderRow("音量", volume, volLabel));
        root.Controls.Add(SliderRow("輸出上限", ceiling, ceilLabel));
        root.Controls.Add(limitLabel);

        root.Controls.Add(new Label { AutoSize = false, Height = 1, Width = W, BackColor = Color.LightGray, Margin = new Padding(0, 4, 0, 6) });
        root.Controls.Add(hpBox);
        root.Controls.Add(gateBox);
        root.Controls.Add(gateInfo);
        root.Controls.Add(SliderRow("門檻", gateTh, gateLabel));
        root.Controls.Add(howlBox);
        var howlRow = Row();
        howlRow.Controls.Add(howlInfo); howlRow.Controls.Add(clearBtn);
        root.Controls.Add(howlRow);
        root.Controls.Add(msgLabel);

        foreach (var c in new Control[] { inCombo, outCombo }) c.Font = Font;
        inCombo.SelectedIndexChanged += (_, _) => SelectionChanged();
        outCombo.SelectedIndexChanged += (_, _) => SelectionChanged();
        foreach (var t in new[] { volume, ceiling, gateTh }) t.ValueChanged += (_, _) => ControlsChanged();
        foreach (var c in new[] { hpBox, gateBox, howlBox }) c.CheckedChanged += (_, _) => ControlsChanged();
        clearBtn.Click += (_, _) => { session?.Engine.Fx.ClearNotches(); notches.Clear(); };

        Layout += (_, _) => { };
        root.SizeChanged += (_, _) => ClientSize = new Size(344, root.Height);
    }

    bool loading;

    void LoadControls()
    {
        loading = true;
        volume.Value = (int)s.VolumeDB; ceiling.Value = (int)s.CeilingDB; gateTh.Value = (int)s.GateDB;
        hpBox.Checked = s.HighPass; gateBox.Checked = s.Gate; howlBox.Checked = s.Howl;
        loading = false;
        ControlsChanged();
    }

    void ControlsChanged()
    {
        if (loading) return;
        s.VolumeDB = volume.Value; s.CeilingDB = ceiling.Value; s.GateDB = gateTh.Value;
        s.HighPass = hpBox.Checked; s.Gate = gateBox.Checked; s.Howl = howlBox.Checked;
        volLabel.Text = $"{volume.Value} dB"; ceilLabel.Text = $"{ceiling.Value} dB"; gateLabel.Text = $"{gateTh.Value} dB";
        gateTh.Visible = gateLabel.Visible = gateBox.Checked;
        gateTh.Parent!.Visible = gateBox.Checked;
        s.Save();
        Apply();
    }

    // ---------- 裝置 ----------
    void RefreshDevices()
    {
        MMDevice[] Get(DataFlow f) => enumerator.EnumerateAudioEndPoints(f, DeviceState.Active).ToArray();
        var newIn = Get(DataFlow.Capture).ToList();
        var newOut = Get(DataFlow.Render).ToList();
        if (session != null && (!newIn.Any(d => d.ID == s.InputId) || !newOut.Any(d => d.ID == s.OutputId)))
        {
            Stop();
            message = "裝置已拔除，擴音已停止";
            UpdateUi();
        }
        if (session != null) return; // 擴音中不動選單
        inputs = newIn; outputs = newOut;
        autoSelecting = true;
        Fill(inCombo, inputs);
        Fill(outCombo, outputs);
        var pickIn = inputs.FirstOrDefault(d => d.ID == s.InputId)
            ?? inputs.FirstOrDefault(d => d.FriendlyName.Contains("Wireless Mic") || d.FriendlyName.Contains("DJI"));
        var pickOut = outputs.FirstOrDefault(d => d.ID == s.OutputId)
            ?? outputs.FirstOrDefault(d => IsDisplayAudio(d.FriendlyName));
        inCombo.SelectedIndex = pickIn != null ? inputs.IndexOf(pickIn) : -1;
        outCombo.SelectedIndex = pickOut != null ? outputs.IndexOf(pickOut) : -1;
        autoSelecting = false;
        SyncSelection();
        UpdateButtons();
    }

    // 不自動選內建喇叭以免嘯叫
    static bool IsDisplayAudio(string n) =>
        new[] { "HDMI", "DisplayPort", "Display Audio", "NVIDIA High Definition Audio", "AMD High Definition Audio" }
            .Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase))
        && !n.Contains("Realtek", StringComparison.OrdinalIgnoreCase);

    static void Fill(ComboBox box, List<MMDevice> devs)
    {
        box.Items.Clear();
        foreach (var d in devs) box.Items.Add(d.FriendlyName);
    }

    MMDevice? Input => inCombo.SelectedIndex >= 0 && inCombo.SelectedIndex < inputs.Count ? inputs[inCombo.SelectedIndex] : null;
    MMDevice? Output => outCombo.SelectedIndex >= 0 && outCombo.SelectedIndex < outputs.Count ? outputs[outCombo.SelectedIndex] : null;

    void SyncSelection()
    {
        warnLabel.Visible = Output is { } o && (o.FriendlyName.Contains("Speakers", StringComparison.OrdinalIgnoreCase)
            || o.FriendlyName.Contains("喇叭") || o.FriendlyName.Contains("Realtek", StringComparison.OrdinalIgnoreCase));
    }

    void SelectionChanged()
    {
        if (autoSelecting) return;
        s.InputId = Input?.ID ?? s.InputId; s.OutputId = Output?.ID ?? s.OutputId;
        s.Save();
        SyncSelection();
        UpdateButtons();
    }

    // ---------- 開始／停止 ----------
    void Start()
    {
        if (session != null) return;
        if (Input is not { } i || Output is not { } o) { message = "請先選擇麥克風與輸出裝置"; return; }
        try { session = new Session(i, o); }
        catch (SessionError e) { message = e.Message; UpdateUi(); return; }
        session.Failed += m => BeginInvoke(() => { Stop(); message = m + "，擴音已停止"; UpdateUi(); });
        muted = false; message = "";
        lastCallbacks = 0; lastProgress = DateTime.Now;
        Apply();
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        timer.Start();
        UpdateButtons();
    }

    void Stop()
    {
        timer.Stop();
        session?.Dispose();
        session = null;
        SetThreadExecutionState(ES_CONTINUOUS);
        inputLevel = outputLevel = 0; limiting = gateOpen = false; inputDB = -90; notches.Clear();
        UpdateUi();
        UpdateButtons();
    }

    void Apply()
    {
        if (session == null) return;
        var e = session.Engine;
        e.Gain = (float)Math.Pow(10, s.VolumeDB / 20);
        e.Ceiling = (float)Math.Pow(10, s.CeilingDB / 20);
        e.Muted = muted;
        e.Fx.HpOn = s.HighPass;
        e.Fx.GateThreshold = (float)Math.Pow(10, s.GateDB / 20);
        e.Fx.GateOn = s.Gate;
        e.Fx.HowlOn = s.Howl;
        if (!s.Howl) e.Fx.ClearNotches();
    }

    void Tick()
    {
        if (session == null) return;
        var e = session.Engine;
        static double Level(float peak, double prev) =>
            Math.Max(Math.Max(Math.Min((20 * Math.Log10(Math.Max(peak, 1e-6)) + 60) / 60, 1), prev - 0.05), 0);
        float inPeak = e.TakeInputPeak();
        inputLevel = Level(inPeak, inputLevel);
        inputDB = Math.Max(20 * Math.Log10(Math.Max(inPeak, 0.00003)), inputDB - 1);
        gateOpen = e.Fx.GateOpened; e.Fx.GateOpened = false;
        if (!muted) e.Fx.Analyze(); // 靜音時不分析，避免把靜音中的持續音當成嘯叫
        notches = e.Fx.NotchFreq.Select(f => (int)Math.Round(f)).Where(f => f > 0).ToList();
        outputLevel = Level(e.TakeOutputPeak(), outputLevel);
        if (e.TakeLimited()) limitingUntil = DateTime.Now.AddSeconds(0.5);
        limiting = DateTime.Now < limitingUntil;

        long count = e.Callbacks;
        if (count != lastCallbacks) { lastCallbacks = count; lastProgress = DateTime.Now; }
        else if ((DateTime.Now - lastProgress).TotalSeconds > 2)
        {
            Stop();
            message = "音訊中斷，擴音已停止，請再按一次開始";
            UpdateUi();
            return;
        }
        UpdateUi();
    }

    void UpdateButtons()
    {
        bool running = session != null;
        startBtn.Text = running ? "停止" : "開始擴音";
        startBtn.BackColor = running ? Color.Crimson : Brand;
        startBtn.Enabled = running || (Input != null && Output != null);
        muteBtn.Enabled = running;
        muteBtn.Text = muted ? "取消靜音" : "靜音";
        muteBtn.BackColor = muted ? Color.Orange : Color.FromArgb(225, 225, 230);
        muteBtn.ForeColor = muted ? Color.White : Color.Black;
        inCombo.Enabled = outCombo.Enabled = !running;
        status.Text = !running ? "● 已停止" : muted ? "● 靜音中" : "● 擴音中";
        status.ForeColor = !running ? Color.Gray : muted ? Color.DarkOrange : Color.SeaGreen;
        tray.Text = "現場擴音：" + status.Text.Substring(2);
        UpdateUi();
    }

    void UpdateUi()
    {
        inMeter.Level = inputLevel; inMeter.Marker = s.Gate ? (s.GateDB + 60) / 60 : null;
        outMeter.Level = outputLevel; outMeter.Marker = (s.CeilingDB + 60) / 60;
        limitLabel.Visible = limiting;
        bool running = session != null;
        gateInfo.Text = !s.Gate || !running ? "沒講話時降低音量" : $"輸入 {inputDB:0} dB，{(gateOpen ? "開啟" : "關閉")}";
        howlInfo.Text = !s.Howl || !running ? "削掉持續的尖銳單音"
            : notches.Count == 0 ? "尚未偵測到嘯叫" : $"已抑制：{string.Join("、", notches.Select(n => n + " Hz"))}";
        clearBtn.Visible = s.Howl && notches.Count > 0;
        msgLabel.Text = message; msgLabel.Visible = message != "";
    }

    // ---------- 面板顯示 ----------
    void TogglePanel() { if (Visible) Hide(); else ShowPanel(); }

    void ShowPanel()
    {
        RefreshDevices();
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 8, wa.Bottom - Height - 8);
        lastShown = DateTime.Now;
        Show();
        Activate();
    }

    public void Snapshot(string path)
    {
        Opacity = 0; Show(); Application.DoEvents();
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        foreach (Control c in Controls) c.DrawToBitmap(bmp, c.Bounds);
        bmp.Save(path);
        Hide();
        tray.Visible = false;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;

    sealed class Notifier(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

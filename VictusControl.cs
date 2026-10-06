// VictusControl - Lightweight Tray & Omen-Flyout Utility for HP Victus Laptops
// Direct HP BIOS WMI communication (root\wmi, hpqBIntM). Zero background services.
// Compatible with the built-in Windows C# compiler (C# 5, .NET Framework 4.0+).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

enum EffectMode
{
    Static = 0,
    Breathing = 1,
    Rainbow = 2
}

enum ThermalProfile
{
    Quiet = 0,
    Balanced = 1,
    Performance = 2
}

static class Hp
{
    const string Ns = @"root\wmi";
    static readonly object Sync = new object();
    static ManagementScope _scope;
    static ManagementObject _inst;
    static ManagementClass _inCls;
    static byte[] _templateData;

    static void Connect()
    {
        if (_inst != null) return;
        _scope = new ManagementScope(Ns);
        _scope.Connect();
        using (ManagementClass cls = new ManagementClass(_scope, new ManagementPath("hpqBIntM"), null))
        {
            _inCls = new ManagementClass(_scope, new ManagementPath("hpqBDataIn"), null);
            foreach (ManagementObject o in cls.GetInstances()) { _inst = o; break; }
            if (_inst == null) throw new Exception("hpqBIntM WMI class not found");
        }
    }

    public static void ResetConnection()
    {
        lock (Sync)
        {
            try { if (_inst != null) _inst.Dispose(); } catch { }
            try { if (_inCls != null) _inCls.Dispose(); } catch { }
            _inst = null;
            _inCls = null;
            _scope = null;
            _templateData = null;
        }
    }

    static ManagementBaseObject InvokeInternal(uint command, uint type, byte[] data)
    {
        Connect();
        using (ManagementObject inObj = _inCls.CreateInstance())
        {
            inObj["Sign"] = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
            inObj["Command"] = command;
            inObj["CommandType"] = type;
            inObj["Size"] = (uint)128;
            inObj["hpqBData"] = data;

            using (ManagementBaseObject inParams = _inst.GetMethodParameters("hpqBIOSInt128"))
            {
                inParams["InData"] = inObj;
                using (ManagementBaseObject outParams = _inst.InvokeMethod("hpqBIOSInt128", inParams, null))
                {
                    return (ManagementBaseObject)outParams["OutData"];
                }
            }
        }
    }

    static byte[] Pad(byte[] src)
    {
        byte[] b = new byte[128];
        if (src != null) Array.Copy(src, b, Math.Min(128, src.Length));
        return b;
    }

    public static string SetColor(byte r, byte g, byte b)
    {
        lock (Sync)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_templateData == null)
                    {
                        ManagementBaseObject o = InvokeInternal(0x20009, 2, new byte[128]);
                        uint rcRead = Convert.ToUInt32(o["rwReturnCode"]);
                        if (rcRead != 0) return "Failed to read color (Code " + rcRead + ")";
                        _templateData = Pad((byte[])o["Data"]);
                    }

                    _templateData[25] = r;
                    _templateData[26] = g;
                    _templateData[27] = b;

                    ManagementBaseObject w = InvokeInternal(0x20009, 3, _templateData);
                    uint rc = Convert.ToUInt32(w["rwReturnCode"]);
                    if (rc != 0) return "Failed to write color (Code " + rc + ")";
                    return null;
                }
                catch (Exception ex)
                {
                    ResetConnection();
                    if (attempt == 1) return ex.Message;
                    Thread.Sleep(200);
                }
            }
            return "WMI error";
        }
    }

    public static void SetThermalProfile(ThermalProfile profile)
    {
        lock (Sync)
        {
            try
            {
                byte mode;
                switch (profile)
                {
                    case ThermalProfile.Quiet: mode = 3; break;
                    case ThermalProfile.Performance: mode = 1; break;
                    default: mode = 0; break;
                }
                byte[] data = new byte[128];
                data[0] = mode;
                InvokeInternal(0x20008, 0x27, data);
            }
            catch { }
        }
    }

    public static void SetMaxFan(bool enabled)
    {
        lock (Sync)
        {
            try
            {
                byte[] data = new byte[128];
                data[0] = (byte)(enabled ? 1 : 0);
                InvokeInternal(0x20008, 0x26, data);
            }
            catch { }
        }
    }
}

static class WindowsPower
{
    [DllImport("PowrProf.dll")]
    static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    static readonly Guid GuidBalanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
    static readonly Guid GuidHighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    static readonly Guid GuidPowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

    public static void ApplyScheme(ThermalProfile profile)
    {
        try
        {
            Guid target;
            switch (profile)
            {
                case ThermalProfile.Quiet: target = GuidPowerSaver; break;
                case ThermalProfile.Performance: target = GuidHighPerf; break;
                default: target = GuidBalanced; break;
            }
            PowerSetActiveScheme(IntPtr.Zero, ref target);
        }
        catch { }
    }
}

class HotkeyWindow : NativeWindow, IDisposable
{
    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_NOREPEAT = 0x4000;
    const uint VK_V = 0x56;
    const int HOTKEY_ID = 9119;

    readonly Action _action;

    public HotkeyWindow(Action action)
    {
        _action = action;
        CreateHandle(new CreateParams());
        RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_V);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
        {
            if (_action != null) _action();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
        DestroyHandle();
    }
}

class FlyoutForm : Form
{
    readonly TrayApp _app;

    Button _btnPower;
    Button[] _colorBtns;
    Button _btnCustomColor;
    Button[] _brightBtns;
    Button[] _effectBtns;
    Button[] _timeoutBtns;
    Button[] _thermalBtns;
    Button _btnFanAuto;
    Button _btnFanMax;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    public FlyoutForm(TrayApp app)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 26);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);
        Size = new Size(360, 485);

        BuildUi();
    }

    void BuildUi()
    {
        int y = 14;

        // Header
        Label lblTitle = new Label();
        lblTitle.Text = "Victus Control";
        lblTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        lblTitle.ForeColor = Color.White;
        lblTitle.Location = new Point(16, y);
        lblTitle.AutoSize = true;
        Controls.Add(lblTitle);

        _btnPower = CreatePillButton("● Licht AN", 240, y - 2, 104, 28);
        _btnPower.Click += delegate
        {
            _app.TogglePower();
            RefreshState();
        };
        Controls.Add(_btnPower);

        y += 40;
        Controls.Add(CreateSeparator(16, y, 328));
        y += 10;

        // Section: FARBE
        Controls.Add(CreateHeaderLabel("FARBE", 16, y));
        y += 22;

        int cx = 16;
        _colorBtns = new Button[TrayApp.PresetColors.Length];
        for (int i = 0; i < TrayApp.PresetColors.Length; i++)
        {
            Color c = TrayApp.PresetColors[i];
            Button b = new Button();
            b.Size = new Size(24, 24);
            b.Location = new Point(cx, y);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Cursor = Cursors.Hand;
            b.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Brush br = new SolidBrush(c)) e.Graphics.FillEllipse(br, 1, 1, 22, 22);
                bool sel = (_app.CurrentEffect != EffectMode.Rainbow &&
                            _app.CurrentColor.R == c.R && _app.CurrentColor.G == c.G && _app.CurrentColor.B == c.B);
                if (sel)
                {
                    using (Pen p = new Pen(Color.White, 2f)) e.Graphics.DrawEllipse(p, 2, 2, 20, 20);
                }
            };
            b.Click += delegate
            {
                _app.SetColor(c);
                RefreshState();
            };
            _colorBtns[i] = b;
            Controls.Add(b);
            cx += 28;
        }

        _btnCustomColor = new Button();
        _btnCustomColor.Text = "+";
        _btnCustomColor.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _btnCustomColor.ForeColor = Color.LightGray;
        _btnCustomColor.BackColor = Color.FromArgb(42, 42, 46);
        _btnCustomColor.Size = new Size(24, 24);
        _btnCustomColor.Location = new Point(cx + 4, y);
        _btnCustomColor.FlatStyle = FlatStyle.Flat;
        _btnCustomColor.FlatAppearance.BorderSize = 0;
        _btnCustomColor.Cursor = Cursors.Hand;
        _btnCustomColor.Click += delegate
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = _app.CurrentColor;
                dlg.FullOpen = true;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _app.SetColor(dlg.Color);
                    RefreshState();
                }
            }
        };
        Controls.Add(_btnCustomColor);

        y += 36;

        // Section: HELLIGKEIT
        Controls.Add(CreateHeaderLabel("HELLIGKEIT", 16, y));
        y += 22;
        int[] bLevels = TrayApp.BrightnessLevels;
        _brightBtns = new Button[bLevels.Length];
        int bw = 76;
        for (int i = 0; i < bLevels.Length; i++)
        {
            int lvl = bLevels[i];
            Button b = CreatePillButton(lvl + " %", 16 + i * (bw + 8), y, bw, 26);
            b.Click += delegate
            {
                _app.SetBrightness(lvl);
                RefreshState();
            };
            _brightBtns[i] = b;
            Controls.Add(b);
        }

        y += 36;

        // Section: EFFEKT
        Controls.Add(CreateHeaderLabel("EFFEKT", 16, y));
        y += 22;
        string[] effNames = { "Statisch", "Atmung", "Regenbogen" };
        EffectMode[] effModes = { EffectMode.Static, EffectMode.Breathing, EffectMode.Rainbow };
        _effectBtns = new Button[effNames.Length];
        int ew = 104;
        for (int i = 0; i < effNames.Length; i++)
        {
            EffectMode em = effModes[i];
            Button b = CreatePillButton(effNames[i], 16 + i * (ew + 8), y, ew, 26);
            b.Click += delegate
            {
                _app.SetEffect(em);
                RefreshState();
            };
            _effectBtns[i] = b;
            Controls.Add(b);
        }

        y += 36;

        // Section: TIMEOUT
        Controls.Add(CreateHeaderLabel("TIMEOUT (INAKTIVITÄT)", 16, y));
        y += 22;
        int[] toSecs = { 0, 30, 60, 300, 600 };
        string[] toLabels = { "Aus", "30s", "1m", "5m", "10m" };
        _timeoutBtns = new Button[toSecs.Length];
        int tw = 58;
        for (int i = 0; i < toSecs.Length; i++)
        {
            int sec = toSecs[i];
            Button b = CreatePillButton(toLabels[i], 16 + i * (tw + 9), y, tw, 26);
            b.Click += delegate
            {
                _app.SetTimeout(sec);
                RefreshState();
            };
            _timeoutBtns[i] = b;
            Controls.Add(b);
        }

        y += 36;

        // Section: LEISTUNGSPROFIL
        Controls.Add(CreateHeaderLabel("LEISTUNGSPROFIL", 16, y));
        y += 22;
        string[] thNames = { "Leise (Eco)", "Ausgeglichen", "Leistung" };
        ThermalProfile[] thModes = { ThermalProfile.Quiet, ThermalProfile.Balanced, ThermalProfile.Performance };
        _thermalBtns = new Button[thNames.Length];
        int pw = 104;
        for (int i = 0; i < thNames.Length; i++)
        {
            ThermalProfile tp = thModes[i];
            Button b = CreatePillButton(thNames[i], 16 + i * (pw + 8), y, pw, 26);
            b.Click += delegate
            {
                _app.SetThermalProfile(tp);
                RefreshState();
            };
            _thermalBtns[i] = b;
            Controls.Add(b);
        }

        y += 36;

        // Section: LÜFTER
        Controls.Add(CreateHeaderLabel("LÜFTERSTEUERUNG", 16, y));
        y += 22;
        _btnFanAuto = CreatePillButton("Auto-Lüfter", 16, y, 160, 26);
        _btnFanAuto.Click += delegate
        {
            _app.SetMaxFan(false);
            RefreshState();
        };
        Controls.Add(_btnFanAuto);

        _btnFanMax = CreatePillButton("Max Lüfter (100 %)", 184, y, 160, 26);
        _btnFanMax.Click += delegate
        {
            _app.SetMaxFan(true);
            RefreshState();
        };
        Controls.Add(_btnFanMax);
    }

    Label CreateHeaderLabel(string text, int x, int y)
    {
        Label lbl = new Label();
        lbl.Text = text;
        lbl.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        lbl.ForeColor = Color.FromArgb(140, 140, 148);
        lbl.Location = new Point(x, y);
        lbl.AutoSize = true;
        return lbl;
    }

    Control CreateSeparator(int x, int y, int width)
    {
        Panel p = new Panel();
        p.Location = new Point(x, y);
        p.Size = new Size(width, 1);
        p.BackColor = Color.FromArgb(45, 45, 50);
        return p;
    }

    Button CreatePillButton(string text, int x, int y, int width, int height)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(width, height);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.Font = new Font("Segoe UI", 8.5f);
        b.Cursor = Cursors.Hand;
        return b;
    }

    public void RefreshState()
    {
        // Power Button
        if (_app.CurrentPowerOn)
        {
            _btnPower.Text = "● Licht AN";
            _btnPower.BackColor = Color.FromArgb(32, 110, 56);
            _btnPower.ForeColor = Color.White;
        }
        else
        {
            _btnPower.Text = "○ Licht AUS";
            _btnPower.BackColor = Color.FromArgb(60, 60, 65);
            _btnPower.ForeColor = Color.FromArgb(200, 100, 100);
        }

        // Color circles refresh
        foreach (Button b in _colorBtns) b.Invalidate();

        // Brightness Buttons
        int[] bLevels = TrayApp.BrightnessLevels;
        for (int i = 0; i < bLevels.Length; i++)
        {
            bool sel = (_app.CurrentBrightness == bLevels[i]);
            StyleTogglePill(_brightBtns[i], sel);
        }

        // Effect Buttons
        EffectMode[] effModes = { EffectMode.Static, EffectMode.Breathing, EffectMode.Rainbow };
        for (int i = 0; i < effModes.Length; i++)
        {
            bool sel = (_app.CurrentEffect == effModes[i]);
            StyleTogglePill(_effectBtns[i], sel);
        }

        // Timeout Buttons
        int[] toSecs = { 0, 30, 60, 300, 600 };
        for (int i = 0; i < toSecs.Length; i++)
        {
            bool sel = (_app.CurrentTimeoutSec == toSecs[i]);
            StyleTogglePill(_timeoutBtns[i], sel);
        }

        // Thermal Profile Buttons
        ThermalProfile[] thModes = { ThermalProfile.Quiet, ThermalProfile.Balanced, ThermalProfile.Performance };
        for (int i = 0; i < thModes.Length; i++)
        {
            bool sel = (_app.CurrentThermal == thModes[i]);
            StyleTogglePill(_thermalBtns[i], sel);
        }

        // Fan Buttons
        StyleTogglePill(_btnFanAuto, !_app.CurrentMaxFan);
        StyleTogglePill(_btnFanMax, _app.CurrentMaxFan);
    }

    void StyleTogglePill(Button b, bool active)
    {
        if (active)
        {
            b.BackColor = Color.FromArgb(0, 120, 215); // Windows 11 Accent Blue
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        }
        else
        {
            b.BackColor = Color.FromArgb(38, 38, 42);
            b.ForeColor = Color.FromArgb(210, 210, 215);
            b.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (Pen p = new Pen(Color.FromArgb(50, 50, 56), 1))
        {
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void ToggleFlyout()
    {
        if (Visible)
        {
            Hide();
        }
        else
        {
            RefreshState();
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Left = wa.Right - Width - 16;
            Top = wa.Bottom - Height - 16;
            Show();
            Activate();
            BringToFront();
        }
    }
}

class TrayApp : ApplicationContext
{
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Control ui = new Control();
    readonly string cfgPath;
    readonly string legacyCfgPath;

    Color currentColor = Color.White;
    int currentBrightness = 100;
    int currentTimeoutSec = 60;
    EffectMode currentEffect = EffectMode.Static;
    bool currentPowerOn = true;
    ThermalProfile currentThermal = ThermalProfile.Balanced;
    bool currentMaxFan = false;

    public Color CurrentColor { get { return currentColor; } }
    public int CurrentBrightness { get { return currentBrightness; } }
    public int CurrentTimeoutSec { get { return currentTimeoutSec; } }
    public EffectMode CurrentEffect { get { return currentEffect; } }
    public bool CurrentPowerOn { get { return currentPowerOn; } }
    public ThermalProfile CurrentThermal { get { return currentThermal; } }
    public bool CurrentMaxFan { get { return currentMaxFan; } }

    volatile bool isExiting = false;
    volatile bool forceUpdate = true;
    Thread workerThread;

    FlyoutForm flyout;
    HotkeyWindow hotkeyWin;
    ManagementEventWatcher omenWatcher;
    IntPtr hookId = IntPtr.Zero;
    HookProc hookProc;

    public static readonly string[] Names = { "Weiss", "Rot", "Orange", "Gelb", "Gruen", "Cyan", "Blau", "Lila", "Pink" };
    public static readonly Color[] PresetColors = {
        Color.FromArgb(255,255,255), Color.FromArgb(255,0,0), Color.FromArgb(255,100,0),
        Color.FromArgb(255,220,0), Color.FromArgb(0,255,0), Color.FromArgb(0,255,255),
        Color.FromArgb(0,80,255), Color.FromArgb(160,0,255), Color.FromArgb(255,0,150)
    };

    ToolStripMenuItem[] colorItems;
    ToolStripMenuItem effectStaticItem;
    ToolStripMenuItem effectBreatheItem;
    ToolStripMenuItem effectRainbowItem;
    ToolStripMenuItem[] brightnessItems;
    ToolStripMenuItem[] timeoutItems;
    ToolStripMenuItem[] thermalItems;
    ToolStripMenuItem maxFanItem;
    ToolStripMenuItem powerItem;

    public static readonly int[] BrightnessLevels = { 25, 50, 75, 100 };
    public static readonly int[] TimeoutSecondsList = { 0, 30, 60, 120, 300, 600 };
    public static readonly string[] TimeoutLabels = { "Nie (Deaktiviert)", "30 Sekunden", "1 Minute", "2 Minuten", "5 Minuten", "10 Minuten" };

    public TrayApp()
    {
        IntPtr h = ui.Handle;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "VictusControl");
        Directory.CreateDirectory(dir);
        cfgPath = Path.Combine(dir, "settings.txt");
        legacyCfgPath = Path.Combine(Path.Combine(appData, "VictusKbd"), "settings.txt");
        LoadSettings();

        flyout = new FlyoutForm(this);

        ContextMenuStrip menu = CreateMenu();
        tray.ContextMenuStrip = menu;
        tray.Text = "Victus Control (Omen-Taste oder Klick)";
        UpdateTrayIcon();
        tray.Visible = true;

        // Linksklick auf Tray öffnet Flyout Bubble
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleFlyout();
            }
        };

        // Hotkey Ctrl + Alt + V
        try { hotkeyWin = new HotkeyWindow(delegate { ToggleFlyout(); }); } catch { }

        // WMI Event Watcher fuer Omen-Taste (hpqBEvnt)
        SetupWmiOmenWatcher();

        // Low-Level Keyboard Hook fuer Omen-Taste / F24 / AppLaunch
        SetupKeyboardHook();

        SystemEvents.PowerModeChanged += delegate(object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Hp.ResetConnection();
                forceUpdate = true;
                Hp.SetThermalProfile(currentThermal);
                Hp.SetMaxFan(currentMaxFan);
                WindowsPower.ApplyScheme(currentThermal);
            }
        };
        SystemEvents.SessionSwitch += delegate(object s, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                Hp.ResetConnection();
                forceUpdate = true;
            }
        };

        // Profile beim Start initialisieren
        Hp.SetThermalProfile(currentThermal);
        Hp.SetMaxFan(currentMaxFan);
        WindowsPower.ApplyScheme(currentThermal);

        workerThread = new Thread(WorkerLoop);
        workerThread.IsBackground = true;
        workerThread.Start();
    }

    void SetupWmiOmenWatcher()
    {
        try
        {
            ManagementScope scope = new ManagementScope(@"root\wmi");
            scope.Connect();
            EventQuery q = new EventQuery("SELECT * FROM hpqBEvnt");
            omenWatcher = new ManagementEventWatcher(scope, q);
            omenWatcher.EventArrived += delegate
            {
                ui.BeginInvoke((MethodInvoker)delegate { ToggleFlyout(); });
            };
            omenWatcher.Start();
        }
        catch { }
    }

    void SetupKeyboardHook()
    {
        try
        {
            hookProc = HookCallback;
            using (System.Diagnostics.Process curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (System.Diagnostics.ProcessModule curModule = curProcess.MainModule)
            {
                hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }
        catch { }
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            // 0x87 = VK_F24, 0xB4 = VK_LAUNCH_APP1, 0xB5 = VK_LAUNCH_APP2
            if (vkCode == 0x87 || vkCode == 0xB4 || vkCode == 0xB5)
            {
                ui.BeginInvoke((MethodInvoker)delegate { ToggleFlyout(); });
                return (IntPtr)1; // Event abfangen
            }
        }
        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    public void ToggleFlyout()
    {
        if (flyout != null) flyout.ToggleFlyout();
    }

    ContextMenuStrip CreateMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Opening += delegate { UpdateMenuChecks(); };

        ToolStripMenuItem quickItem = new ToolStripMenuItem("Schnellsteuerung (Omen-Taste)...");
        quickItem.Font = new Font(menu.Font, FontStyle.Bold);
        quickItem.Click += delegate { ToggleFlyout(); };
        menu.Items.Add(quickItem);

        menu.Items.Add(new ToolStripSeparator());

        colorItems = new ToolStripMenuItem[Names.Length];
        for (int i = 0; i < Names.Length; i++)
        {
            Color c = PresetColors[i];
            ToolStripMenuItem item = new ToolStripMenuItem(Names[i]);
            item.Click += delegate { SetColor(c); };
            colorItems[i] = item;
            menu.Items.Add(item);
        }

        ToolStripMenuItem custom = new ToolStripMenuItem("Eigene Farbe...");
        custom.Click += delegate
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = currentColor;
                dlg.FullOpen = true;
                if (dlg.ShowDialog() == DialogResult.OK) SetColor(dlg.Color);
            }
        };
        menu.Items.Add(custom);

        menu.Items.Add(new ToolStripSeparator());

        // Effekt
        ToolStripMenuItem effectMenu = new ToolStripMenuItem("Effekt");
        effectStaticItem = new ToolStripMenuItem("Statisch", null, delegate { SetEffect(EffectMode.Static); });
        effectBreatheItem = new ToolStripMenuItem("Atmung (Breathing)", null, delegate { SetEffect(EffectMode.Breathing); });
        effectRainbowItem = new ToolStripMenuItem("Farbwechsel (Regenbogen)", null, delegate { SetEffect(EffectMode.Rainbow); });
        effectMenu.DropDownItems.Add(effectStaticItem);
        effectMenu.DropDownItems.Add(effectBreatheItem);
        effectMenu.DropDownItems.Add(effectRainbowItem);
        menu.Items.Add(effectMenu);

        // Helligkeit
        ToolStripMenuItem brightMenu = new ToolStripMenuItem("Helligkeit");
        brightnessItems = new ToolStripMenuItem[BrightnessLevels.Length];
        for (int i = 0; i < BrightnessLevels.Length; i++)
        {
            int lvl = BrightnessLevels[i];
            ToolStripMenuItem bItem = new ToolStripMenuItem(lvl + " %");
            bItem.Click += delegate { SetBrightness(lvl); };
            brightnessItems[i] = bItem;
            brightMenu.DropDownItems.Add(bItem);
        }
        menu.Items.Add(brightMenu);

        // Timeout
        ToolStripMenuItem timeoutMenu = new ToolStripMenuItem("Timeout (Inaktivität)");
        timeoutItems = new ToolStripMenuItem[TimeoutSecondsList.Length];
        for (int i = 0; i < TimeoutSecondsList.Length; i++)
        {
            int sec = TimeoutSecondsList[i];
            ToolStripMenuItem tItem = new ToolStripMenuItem(TimeoutLabels[i]);
            tItem.Click += delegate { SetTimeout(sec); };
            timeoutItems[i] = tItem;
            timeoutMenu.DropDownItems.Add(tItem);
        }
        menu.Items.Add(timeoutMenu);

        // Leistungsprofil
        ToolStripMenuItem thermalMenu = new ToolStripMenuItem("Leistungsprofil");
        string[] thNames = { "Leise (Eco)", "Ausgeglichen", "Leistung (Gaming)" };
        ThermalProfile[] thModes = { ThermalProfile.Quiet, ThermalProfile.Balanced, ThermalProfile.Performance };
        thermalItems = new ToolStripMenuItem[thNames.Length];
        for (int i = 0; i < thNames.Length; i++)
        {
            ThermalProfile tp = thModes[i];
            ToolStripMenuItem thItem = new ToolStripMenuItem(thNames[i]);
            thItem.Click += delegate { SetThermalProfile(tp); };
            thermalItems[i] = thItem;
            thermalMenu.DropDownItems.Add(thItem);
        }
        menu.Items.Add(thermalMenu);

        // Max Fan
        maxFanItem = new ToolStripMenuItem("Max Lüfter (100 %)");
        maxFanItem.Click += delegate { SetMaxFan(!currentMaxFan); };
        menu.Items.Add(maxFanItem);

        menu.Items.Add(new ToolStripSeparator());

        powerItem = new ToolStripMenuItem("Licht an");
        powerItem.Click += delegate { TogglePower(); };
        menu.Items.Add(powerItem);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem exit = new ToolStripMenuItem("Beenden");
        exit.Click += delegate
        {
            isExiting = true;
            if (omenWatcher != null) { try { omenWatcher.Stop(); omenWatcher.Dispose(); } catch { } }
            if (hookId != IntPtr.Zero) { try { UnhookWindowsHookEx(hookId); } catch { } }
            if (hotkeyWin != null) { try { hotkeyWin.Dispose(); } catch { } }
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        };
        menu.Items.Add(exit);

        UpdateMenuChecks();
        return menu;
    }

    void UpdateMenuChecks()
    {
        for (int i = 0; i < Names.Length; i++)
        {
            Color p = PresetColors[i];
            colorItems[i].Checked = (currentEffect != EffectMode.Rainbow &&
                                     currentColor.R == p.R && currentColor.G == p.G && currentColor.B == p.B);
        }

        effectStaticItem.Checked = (currentEffect == EffectMode.Static);
        effectBreatheItem.Checked = (currentEffect == EffectMode.Breathing);
        effectRainbowItem.Checked = (currentEffect == EffectMode.Rainbow);

        for (int i = 0; i < BrightnessLevels.Length; i++)
        {
            brightnessItems[i].Checked = (currentBrightness == BrightnessLevels[i]);
        }

        for (int i = 0; i < TimeoutSecondsList.Length; i++)
        {
            timeoutItems[i].Checked = (currentTimeoutSec == TimeoutSecondsList[i]);
        }

        for (int i = 0; i < thermalItems.Length; i++)
        {
            thermalItems[i].Checked = (currentThermal == (ThermalProfile)i);
        }

        maxFanItem.Checked = currentMaxFan;
        powerItem.Checked = currentPowerOn;
    }

    public void SetColor(Color c)
    {
        currentColor = c;
        if (currentEffect == EffectMode.Rainbow) currentEffect = EffectMode.Static;
        forceUpdate = true;
        UpdateTrayIcon();
        SaveSettings();
    }

    public void SetEffect(EffectMode mode)
    {
        currentEffect = mode;
        forceUpdate = true;
        SaveSettings();
    }

    public void SetBrightness(int b)
    {
        currentBrightness = b;
        forceUpdate = true;
        UpdateTrayIcon();
        SaveSettings();
    }

    public void SetTimeout(int sec)
    {
        currentTimeoutSec = sec;
        forceUpdate = true;
        SaveSettings();
    }

    public void SetThermalProfile(ThermalProfile profile)
    {
        currentThermal = profile;
        Hp.SetThermalProfile(profile);
        WindowsPower.ApplyScheme(profile);
        SaveSettings();
    }

    public void SetMaxFan(bool enabled)
    {
        currentMaxFan = enabled;
        Hp.SetMaxFan(enabled);
        SaveSettings();
    }

    public void TogglePower()
    {
        currentPowerOn = !currentPowerOn;
        forceUpdate = true;
        UpdateTrayIcon();
        SaveSettings();
    }

    void UpdateTrayIcon()
    {
        Icon old = tray.Icon;
        tray.Icon = MakeIcon(currentColor, currentPowerOn, currentBrightness);
        if (old != null) old.Dispose();
    }

    static uint GetIdleMilliseconds()
    {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (GetLastInputInfo(ref lii))
        {
            return (uint)Environment.TickCount - lii.dwTime;
        }
        return 0;
    }

    static Color ScaleColor(Color baseCol, int brightnessPercent)
    {
        if (brightnessPercent <= 0) return Color.Black;
        double scale = Math.Max(0.0, Math.Min(100.0, brightnessPercent)) / 100.0;
        int r = (int)Math.Round(baseCol.R * scale);
        int g = (int)Math.Round(baseCol.G * scale);
        int b = (int)Math.Round(baseCol.B * scale);
        if (baseCol.R > 0 && r == 0 && brightnessPercent > 0) r = 1;
        if (baseCol.G > 0 && g == 0 && brightnessPercent > 0) g = 1;
        if (baseCol.B > 0 && b == 0 && brightnessPercent > 0) b = 1;
        return Color.FromArgb(Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
    }

    static Color ColorFromHsv(double hue, double saturation, double value)
    {
        while (hue < 0) hue += 360;
        while (hue >= 360) hue -= 360;

        int hi = Convert.ToInt32(Math.Floor(hue / 60.0)) % 6;
        double f = hue / 60.0 - Math.Floor(hue / 60.0);

        double v = value * 255.0;
        int vi = (int)Math.Min(255, Math.Max(0, Math.Round(v)));
        int p = (int)Math.Min(255, Math.Max(0, Math.Round(v * (1.0 - saturation))));
        int q = (int)Math.Min(255, Math.Max(0, Math.Round(v * (1.0 - f * saturation))));
        int t = (int)Math.Min(255, Math.Max(0, Math.Round(v * (1.0 - (1.0 - f) * saturation))));

        if (hi == 0) return Color.FromArgb(vi, t, p);
        if (hi == 1) return Color.FromArgb(q, vi, p);
        if (hi == 2) return Color.FromArgb(p, vi, t);
        if (hi == 3) return Color.FromArgb(p, q, vi);
        if (hi == 4) return Color.FromArgb(t, p, vi);
        return Color.FromArgb(vi, p, q);
    }

    void WorkerLoop()
    {
        bool lastAppliedPower = true;
        Color lastAppliedColor = Color.Empty;

        while (!isExiting)
        {
            try
            {
                uint idleMs = GetIdleMilliseconds();
                bool isIdle = (currentTimeoutSec > 0) && (idleMs >= (uint)(currentTimeoutSec * 1000));
                bool shouldBeOn = currentPowerOn && !isIdle;

                if (!shouldBeOn)
                {
                    if (lastAppliedPower || forceUpdate)
                    {
                        forceUpdate = false;
                        Hp.SetColor(0, 0, 0);
                        lastAppliedPower = false;
                        lastAppliedColor = Color.FromArgb(0, 0, 0);
                    }
                    Thread.Sleep(200);
                    continue;
                }

                // Power is ON and NOT idle
                if (!lastAppliedPower)
                {
                    lastAppliedPower = true;
                    forceUpdate = true;
                }

                if (currentEffect == EffectMode.Static)
                {
                    Color target = ScaleColor(currentColor, currentBrightness);
                    if (forceUpdate || target != lastAppliedColor)
                    {
                        forceUpdate = false;
                        Hp.SetColor(target.R, target.G, target.B);
                        lastAppliedColor = target;
                    }
                    Thread.Sleep(200);
                }
                else if (currentEffect == EffectMode.Breathing)
                {
                    double t = (Environment.TickCount % 3500) / 3500.0 * 2.0 * Math.PI;
                    double sine = 0.5 + 0.5 * Math.Sin(t);
                    double factor = 0.06 + 0.94 * (sine * sine);
                    int effBright = (int)Math.Max(1, Math.Round(currentBrightness * factor));
                    Color c = ScaleColor(currentColor, effBright);
                    Hp.SetColor(c.R, c.G, c.B);
                    lastAppliedColor = c;
                    forceUpdate = false;
                    Thread.Sleep(60);
                }
                else if (currentEffect == EffectMode.Rainbow)
                {
                    double hue = ((Environment.TickCount % 6000) / 6000.0) * 360.0;
                    Color c = ColorFromHsv(hue, 1.0, currentBrightness / 100.0);
                    Hp.SetColor(c.R, c.G, c.B);
                    lastAppliedColor = c;
                    forceUpdate = false;
                    Thread.Sleep(60);
                }
            }
            catch (ThreadAbortException)
            {
                return;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }

    void LoadSettings()
    {
        try
        {
            string fileToRead = null;
            if (File.Exists(cfgPath)) fileToRead = cfgPath;
            else if (File.Exists(legacyCfgPath)) fileToRead = legacyCfgPath;

            if (fileToRead != null)
            {
                string[] lines = File.ReadAllLines(fileToRead);
                foreach (string line in lines)
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int idx = s.IndexOf('=');
                    if (idx < 0) continue;
                    string key = s.Substring(0, idx).Trim().ToLowerInvariant();
                    string val = s.Substring(idx + 1).Trim();

                    if (key == "color")
                    {
                        string[] p = val.Split(',');
                        if (p.Length == 3)
                            currentColor = Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
                    }
                    else if (key == "brightness")
                    {
                        int b;
                        if (int.TryParse(val, out b)) currentBrightness = b;
                    }
                    else if (key == "timeout")
                    {
                        int to;
                        if (int.TryParse(val, out to)) currentTimeoutSec = to;
                    }
                    else if (key == "effect")
                    {
                        int eff;
                        if (int.TryParse(val, out eff)) currentEffect = (EffectMode)eff;
                    }
                    else if (key == "power")
                    {
                        bool pow;
                        if (bool.TryParse(val, out pow)) currentPowerOn = pow;
                    }
                    else if (key == "thermal")
                    {
                        int th;
                        if (int.TryParse(val, out th)) currentThermal = (ThermalProfile)th;
                    }
                    else if (key == "maxfan")
                    {
                        bool mf;
                        if (bool.TryParse(val, out mf)) currentMaxFan = mf;
                    }
                }
            }
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            string content = string.Format(
                "color={0},{1},{2}\r\nbrightness={3}\r\ntimeout={4}\r\neffect={5}\r\npower={6}\r\nthermal={7}\r\nmaxfan={8}\r\n",
                currentColor.R, currentColor.G, currentColor.B,
                currentBrightness,
                currentTimeoutSec,
                (int)currentEffect,
                currentPowerOn,
                (int)currentThermal,
                currentMaxFan
            );
            File.WriteAllText(cfgPath, content);
        }
        catch { }
    }

    Icon MakeIcon(Color c, bool powerOn, int brightness)
    {
        using (Bitmap bmp = new Bitmap(16, 16))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Color fill = powerOn ? ScaleColor(c, brightness) : Color.FromArgb(50, 50, 50);
                using (Brush br = new SolidBrush(fill)) g.FillEllipse(br, 1, 1, 14, 14);
                using (Pen p = new Pen(powerOn ? Color.Gray : Color.FromArgb(180, 50, 50))) g.DrawEllipse(p, 1, 1, 14, 14);
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return ic;
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (Mutex m = new Mutex(true, "VictusControlTray", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new TrayApp());
        }
    }
}

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
}

static class DisplayHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    static DimmerOverlay _overlay;

    public static bool SetExternalBrightness(int brightnessPercent, out string modeUsed)
    {
        bool ddcWorked = false;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
            {
                // The primary monitor always sits at (0,0). On most Victus laptops that is the
                // built-in panel, which must not be changed. Heuristic: see README.
                if (rc.left == 0 && rc.top == 0) return true;

                uint count = 0;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) && count > 0)
                {
                    PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            IntPtr hPhys = arr[i].hPhysicalMonitor;
                            if (SetMonitorBrightness(hPhys, (uint)brightnessPercent))
                            {
                                ddcWorked = true;
                            }
                            else if (SetVCPFeature(hPhys, 0x10, (uint)brightnessPercent))
                            {
                                ddcWorked = true;
                            }
                        }
                        DestroyPhysicalMonitors(count, arr);
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        if (ddcWorked)
        {
            if (_overlay != null && _overlay.Visible) _overlay.Hide();
            modeUsed = "Hardware (DDC/CI)";
            return true;
        }

        // Fallback: Click-through software dimmer overlay
        ApplySoftwareDimmer(brightnessPercent);
        modeUsed = "Software Dimmer (DDC/CI not responding)";
        return false;
    }

    static void ApplySoftwareDimmer(int brightnessPercent)
    {
        Screen extScreen = null;
        foreach (Screen s in Screen.AllScreens)
        {
            if (!s.Primary) { extScreen = s; break; }
        }

        if (extScreen == null)
        {
            if (_overlay != null && _overlay.Visible) _overlay.Hide();
            return;
        }

        if (brightnessPercent >= 100)
        {
            if (_overlay != null && _overlay.Visible) _overlay.Hide();
            return;
        }

        if (_overlay == null || _overlay.IsDisposed)
        {
            _overlay = new DimmerOverlay();
        }

        _overlay.Bounds = extScreen.Bounds;
        double opacity = ((100.0 - brightnessPercent) / 90.0) * 0.82;
        _overlay.Opacity = Math.Max(0.04, Math.Min(0.85, opacity));
        if (!_overlay.Visible)
        {
            _overlay.Show();
        }
    }
}

class DimmerOverlay : Form
{
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOACTIVATE = 0x08000000;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public DimmerOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }
}

class DarkSlider : Control
{
    int _value = 100;
    bool _isDragging = false;

    public int Value
    {
        get { return _value; }
        set
        {
            int snapped = (int)(Math.Round(Math.Max(10, Math.Min(100, value)) / 10.0, MidpointRounding.AwayFromZero) * 10);
            if (_value != snapped)
            {
                _value = snapped;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler ValueChanged;

    public DarkSlider()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
        Height = 22;
    }

    void UpdateFromMouse(int mouseX)
    {
        int pad = 10;
        int trackWidth = Width - 2 * pad;
        if (trackWidth <= 0) return;
        double pct = (double)(mouseX - pad) / trackWidth;
        pct = Math.Max(0.0, Math.Min(1.0, pct));
        int val = (int)(Math.Round((10.0 + pct * 90.0) / 10.0, MidpointRounding.AwayFromZero) * 10);
        Value = val;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            UpdateFromMouse(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.Button == MouseButtons.Left)
        {
            UpdateFromMouse(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _isDragging = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = 10;
        int trackWidth = Width - 2 * pad;
        int cy = Height / 2;

        // Background Track
        using (Pen pTrack = new Pen(Color.FromArgb(48, 48, 54), 4f))
        {
            pTrack.StartCap = LineCap.Round;
            pTrack.EndCap = LineCap.Round;
            e.Graphics.DrawLine(pTrack, pad, cy, pad + trackWidth, cy);
        }

        double pct = (_value - 10) / 90.0;
        int thumbX = pad + (int)Math.Round(pct * trackWidth);

        // Filled Active Track (Accent Blue)
        if (thumbX > pad)
        {
            using (Pen pActive = new Pen(Color.FromArgb(0, 120, 215), 4f))
            {
                pActive.StartCap = LineCap.Round;
                pActive.EndCap = LineCap.Round;
                e.Graphics.DrawLine(pActive, pad, cy, thumbX, cy);
            }
        }

        // Thumb Circle
        int thumbRadius = 7;
        using (Brush brThumb = new SolidBrush(Color.White))
        {
            e.Graphics.FillEllipse(brThumb, thumbX - thumbRadius, cy - thumbRadius, thumbRadius * 2, thumbRadius * 2);
        }
        using (Pen pThumb = new Pen(Color.FromArgb(0, 120, 215), 2f))
        {
            e.Graphics.DrawEllipse(pThumb, thumbX - thumbRadius, cy - thumbRadius, thumbRadius * 2, thumbRadius * 2);
        }
    }
}

class FlyoutForm : Form
{
    readonly TrayApp _app;

    Button _btnPower;
    Button[] _colorBtns;
    Button _btnCustomColor;
    Label _lblBrightVal;
    DarkSlider _brightSlider;
    Button[] _brightBtns;
    Button[] _effectBtns;
    Button[] _timeoutBtns;

    Label _lblExtBrightVal;
    DarkSlider _extBrightSlider;
    Label _lblExtStatus;

    static readonly int[] QuickBrightness = { 20, 50, 80, 100 };

    static readonly Font FontToggleNormal = new Font("Segoe UI", 8.5f, FontStyle.Regular);
    static readonly Font FontToggleBold = new Font("Segoe UI", 8.5f, FontStyle.Bold);

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
        Size = new Size(360, 470);

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

        _btnPower = CreatePillButton("● Light ON", 236, y - 2, 108, 28);
        _btnPower.Click += delegate
        {
            _app.TogglePower();
            RefreshState();
        };
        Controls.Add(_btnPower);

        y += 40;
        Controls.Add(CreateSeparator(16, y, 328));
        y += 10;

        // Section: COLOR
        Controls.Add(CreateHeaderLabel("COLOR", 16, y));
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

        // Section: KEYBOARD BRIGHTNESS
        Controls.Add(CreateHeaderLabel("KEYBOARD BRIGHTNESS (10% STEPS)", 16, y));

        _lblBrightVal = new Label();
        _lblBrightVal.Text = _app.CurrentBrightness + " %";
        _lblBrightVal.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _lblBrightVal.ForeColor = Color.FromArgb(0, 150, 255);
        _lblBrightVal.Location = new Point(280, y - 2);
        _lblBrightVal.Size = new Size(64, 18);
        _lblBrightVal.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(_lblBrightVal);

        y += 20;

        _brightSlider = new DarkSlider();
        _brightSlider.Location = new Point(16, y);
        _brightSlider.Size = new Size(328, 22);
        _brightSlider.Value = _app.CurrentBrightness;
        _brightSlider.ValueChanged += delegate
        {
            int val = _brightSlider.Value;
            _lblBrightVal.Text = val + " %";
            _app.SetBrightness(val);
            RefreshQuickBrightBtns();
        };
        Controls.Add(_brightSlider);

        y += 26;

        _brightBtns = new Button[QuickBrightness.Length];
        int bw = 76;
        for (int i = 0; i < QuickBrightness.Length; i++)
        {
            int lvl = QuickBrightness[i];
            Button b = CreatePillButton(lvl + " %", 16 + i * (bw + 8), y, bw, 24);
            b.Click += delegate
            {
                _brightSlider.Value = lvl;
                _app.SetBrightness(lvl);
                RefreshState();
            };
            _brightBtns[i] = b;
            Controls.Add(b);
        }

        y += 34;

        // Section: EFFECT
        Controls.Add(CreateHeaderLabel("EFFECT", 16, y));
        y += 22;
        string[] effNames = { "Static", "Breathing", "Rainbow" };
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

        // Section: TIMEOUT (IDLE)
        Controls.Add(CreateHeaderLabel("TIMEOUT (IDLE)", 16, y));
        y += 22;
        int[] toSecs = { 0, 30, 60, 120, 300, 600 };
        string[] toLabels = { "Off", "30s", "1m", "2m", "5m", "10m" };
        _timeoutBtns = new Button[toSecs.Length];
        int tw = 48;
        for (int i = 0; i < toSecs.Length; i++)
        {
            int sec = toSecs[i];
            Button b = CreatePillButton(toLabels[i], 16 + i * (tw + 6), y, tw, 26);
            b.Click += delegate
            {
                _app.SetTimeout(sec);
                RefreshState();
            };
            _timeoutBtns[i] = b;
            Controls.Add(b);
        }

        y += 36;
        Controls.Add(CreateSeparator(16, y, 328));
        y += 10;

        // Section: EXTERNAL DISPLAY (TEST)
        Controls.Add(CreateHeaderLabel("EXTERNAL DISPLAY (HDMI / TYPE-C)", 16, y));

        _lblExtBrightVal = new Label();
        _lblExtBrightVal.Text = _app.CurrentExtDisplayBrightness + " %";
        _lblExtBrightVal.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _lblExtBrightVal.ForeColor = Color.FromArgb(0, 210, 140);
        _lblExtBrightVal.Location = new Point(280, y - 2);
        _lblExtBrightVal.Size = new Size(64, 18);
        _lblExtBrightVal.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(_lblExtBrightVal);

        y += 20;

        _extBrightSlider = new DarkSlider();
        _extBrightSlider.Location = new Point(16, y);
        _extBrightSlider.Size = new Size(328, 22);
        _extBrightSlider.Value = _app.CurrentExtDisplayBrightness;
        _extBrightSlider.ValueChanged += delegate
        {
            int val = _extBrightSlider.Value;
            _lblExtBrightVal.Text = val + " %";
            string mode;
            DisplayHelper.SetExternalBrightness(val, out mode);
            _app.SetExtDisplayBrightness(val);
            if (_lblExtStatus != null) _lblExtStatus.Text = "Mode: " + mode;
        };
        Controls.Add(_extBrightSlider);

        y += 24;

        _lblExtStatus = new Label();
        _lblExtStatus.Text = "Mode: Auto (DDC/CI or Overlay)";
        _lblExtStatus.Font = new Font("Segoe UI", 7.5f);
        _lblExtStatus.ForeColor = Color.FromArgb(120, 120, 130);
        _lblExtStatus.Location = new Point(16, y);
        _lblExtStatus.Size = new Size(328, 16);
        Controls.Add(_lblExtStatus);
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

    void RefreshQuickBrightBtns()
    {
        for (int i = 0; i < QuickBrightness.Length; i++)
        {
            bool sel = (_app.CurrentBrightness == QuickBrightness[i]);
            StyleTogglePill(_brightBtns[i], sel);
        }
    }

    public void RefreshState()
    {
        // Power Button
        if (_app.CurrentPowerOn)
        {
            _btnPower.Text = "● Light ON";
            _btnPower.BackColor = Color.FromArgb(32, 110, 56);
            _btnPower.ForeColor = Color.White;
        }
        else
        {
            _btnPower.Text = "○ Light OFF";
            _btnPower.BackColor = Color.FromArgb(60, 60, 65);
            _btnPower.ForeColor = Color.FromArgb(200, 100, 100);
        }

        // Color circles refresh
        foreach (Button b in _colorBtns) b.Invalidate();

        // Keyboard Brightness Slider & Value Label
        if (_brightSlider != null)
        {
            _brightSlider.Value = _app.CurrentBrightness;
            if (_lblBrightVal != null) _lblBrightVal.Text = _app.CurrentBrightness + " %";
        }
        RefreshQuickBrightBtns();

        // External Display Slider
        if (_extBrightSlider != null)
        {
            _extBrightSlider.Value = _app.CurrentExtDisplayBrightness;
            if (_lblExtBrightVal != null) _lblExtBrightVal.Text = _app.CurrentExtDisplayBrightness + " %";
        }

        // Effect Buttons
        EffectMode[] effModes = { EffectMode.Static, EffectMode.Breathing, EffectMode.Rainbow };
        for (int i = 0; i < effModes.Length; i++)
        {
            bool sel = (_app.CurrentEffect == effModes[i]);
            StyleTogglePill(_effectBtns[i], sel);
        }

        // Timeout Buttons
        int[] toSecs = { 0, 30, 60, 120, 300, 600 };
        for (int i = 0; i < toSecs.Length; i++)
        {
            bool sel = (_app.CurrentTimeoutSec == toSecs[i]);
            StyleTogglePill(_timeoutBtns[i], sel);
        }
    }

    void StyleTogglePill(Button b, bool active)
    {
        if (active)
        {
            b.BackColor = Color.FromArgb(0, 120, 215); // Windows 11 Accent Blue
            b.ForeColor = Color.White;
            b.Font = FontToggleBold;
        }
        else
        {
            b.BackColor = Color.FromArgb(38, 38, 42);
            b.ForeColor = Color.FromArgb(210, 210, 215);
            b.Font = FontToggleNormal;
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

    readonly NotifyIcon tray = new NotifyIcon();
    readonly object stateLock = new object();
    readonly Control ui = new Control();
    readonly string cfgPath;
    readonly string legacyCfgPath;

    Color currentColor = Color.White;
    int currentBrightness = 100;
    int currentTimeoutSec = 60;
    EffectMode currentEffect = EffectMode.Static;
    bool currentPowerOn = true;
    int currentExtDisplayBrightness = 100;

    public Color CurrentColor { get { return currentColor; } }
    public int CurrentBrightness { get { return currentBrightness; } }
    public int CurrentTimeoutSec { get { return currentTimeoutSec; } }
    public EffectMode CurrentEffect { get { return currentEffect; } }
    public bool CurrentPowerOn { get { return currentPowerOn; } }
    public int CurrentExtDisplayBrightness { get { return currentExtDisplayBrightness; } }

    volatile bool isExiting = false;
    volatile bool forceUpdate = true;
    Thread workerThread;

    FlyoutForm flyout;
    ManagementEventWatcher omenWatcher;

    public static readonly string[] Names = { "White", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "Pink" };
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
    ToolStripMenuItem powerItem;

    public static readonly int[] BrightnessLevels = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
    public static readonly int[] TimeoutSecondsList = { 0, 30, 60, 120, 300, 600 };
    public static readonly string[] TimeoutLabels = { "Disabled (Never)", "30 Seconds", "1 Minute", "2 Minutes", "5 Minutes", "10 Minutes" };

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
        tray.Text = "Victus Control (Omen Key or Click)";
        UpdateTrayIcon();
        tray.Visible = true;

        // Left click on tray toggles Flyout
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleFlyout();
            }
        };

        // Omen key is the only keyboard trigger: WMI event hpqBEvnt (one event per press)
        SetupWmiOmenWatcher();

        SystemEvents.PowerModeChanged += delegate(object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Hp.ResetConnection();
                forceUpdate = true;
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

    public void ToggleFlyout()
    {
        if (flyout != null) flyout.ToggleFlyout();
    }

    ContextMenuStrip CreateMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Opening += delegate { UpdateMenuChecks(); };

        ToolStripMenuItem quickItem = new ToolStripMenuItem("Quick Control (Omen Key)...");
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

        ToolStripMenuItem custom = new ToolStripMenuItem("Custom Color...");
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

        // Effect
        ToolStripMenuItem effectMenu = new ToolStripMenuItem("Effect");
        effectStaticItem = new ToolStripMenuItem("Static", null, delegate { SetEffect(EffectMode.Static); });
        effectBreatheItem = new ToolStripMenuItem("Breathing", null, delegate { SetEffect(EffectMode.Breathing); });
        effectRainbowItem = new ToolStripMenuItem("Rainbow", null, delegate { SetEffect(EffectMode.Rainbow); });
        effectMenu.DropDownItems.Add(effectStaticItem);
        effectMenu.DropDownItems.Add(effectBreatheItem);
        effectMenu.DropDownItems.Add(effectRainbowItem);
        menu.Items.Add(effectMenu);

        // Brightness (10% to 100%)
        ToolStripMenuItem brightMenu = new ToolStripMenuItem("Brightness");
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
        ToolStripMenuItem timeoutMenu = new ToolStripMenuItem("Timeout (Idle)");
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

        menu.Items.Add(new ToolStripSeparator());

        powerItem = new ToolStripMenuItem("Light on");
        powerItem.Click += delegate { TogglePower(); };
        menu.Items.Add(powerItem);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
        exit.Click += delegate
        {
            isExiting = true;
            if (omenWatcher != null) { try { omenWatcher.Stop(); omenWatcher.Dispose(); } catch { } }
            if (flyout != null) { try { flyout.Dispose(); } catch { } }
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

        powerItem.Checked = currentPowerOn;
    }

    public void SetColor(Color c)
    {
        lock (stateLock)
        {
            currentColor = c;
            if (currentEffect == EffectMode.Rainbow) currentEffect = EffectMode.Static;
        }
        forceUpdate = true;
        UpdateTrayIcon();
        SaveSettings();
    }

    public void SetEffect(EffectMode mode)
    {
        lock (stateLock) { currentEffect = mode; }
        forceUpdate = true;
        SaveSettings();
    }

    public void SetBrightness(int b)
    {
        lock (stateLock) { currentBrightness = b; }
        forceUpdate = true;
        UpdateTrayIcon();
        SaveSettings();
    }

    public void SetExtDisplayBrightness(int b)
    {
        lock (stateLock) { currentExtDisplayBrightness = b; }
        SaveSettings();
    }

    public void SetTimeout(int sec)
    {
        lock (stateLock) { currentTimeoutSec = sec; }
        forceUpdate = true;
        SaveSettings();
    }

    public void TogglePower()
    {
        lock (stateLock) { currentPowerOn = !currentPowerOn; }
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
                // Take a consistent snapshot of the settings for this frame
                bool powerOn;
                Color color;
                int brightness, timeoutSec;
                EffectMode effect;
                lock (stateLock)
                {
                    powerOn = currentPowerOn;
                    color = currentColor;
                    brightness = currentBrightness;
                    timeoutSec = currentTimeoutSec;
                    effect = currentEffect;
                }

                uint idleMs = GetIdleMilliseconds();
                bool isIdle = (timeoutSec > 0) && (idleMs >= (uint)(timeoutSec * 1000));
                bool shouldBeOn = powerOn && !isIdle;

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

                if (effect == EffectMode.Static)
                {
                    Color target = ScaleColor(color, brightness);
                    if (forceUpdate || target != lastAppliedColor)
                    {
                        forceUpdate = false;
                        Hp.SetColor(target.R, target.G, target.B);
                        lastAppliedColor = target;
                    }
                    Thread.Sleep(200);
                }
                else if (effect == EffectMode.Breathing)
                {
                    double t = (Environment.TickCount % 3500) / 3500.0 * 2.0 * Math.PI;
                    double sine = 0.5 + 0.5 * Math.Sin(t);
                    double factor = 0.06 + 0.94 * (sine * sine);
                    int effBright = (int)Math.Max(1, Math.Round(brightness * factor));
                    Color c = ScaleColor(color, effBright);
                    // Only talk to WMI when the color actually changed
                    if (forceUpdate || c != lastAppliedColor)
                    {
                        Hp.SetColor(c.R, c.G, c.B);
                        lastAppliedColor = c;
                    }
                    forceUpdate = false;
                    Thread.Sleep(60);
                }
                else if (effect == EffectMode.Rainbow)
                {
                    double hue = ((Environment.TickCount % 6000) / 6000.0) * 360.0;
                    Color c = ColorFromHsv(hue, 1.0, brightness / 100.0);
                    if (forceUpdate || c != lastAppliedColor)
                    {
                        Hp.SetColor(c.R, c.G, c.B);
                        lastAppliedColor = c;
                    }
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
                    else if (key == "extbright")
                    {
                        int eb;
                        if (int.TryParse(val, out eb)) currentExtDisplayBrightness = eb;
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
                "color={0},{1},{2}\r\nbrightness={3}\r\ntimeout={4}\r\neffect={5}\r\npower={6}\r\nextbright={7}\r\n",
                currentColor.R, currentColor.G, currentColor.B,
                currentBrightness,
                currentTimeoutSec,
                (int)currentEffect,
                currentPowerOn,
                currentExtDisplayBrightness
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
            Icon tmp = Icon.FromHandle(h);
            Icon ic = (Icon)tmp.Clone();
            tmp.Dispose();
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

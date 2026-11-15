// MonitorLock - keeps the windows on locked monitors where you put them,
// even when another monitor changes resolution (e.g. a game going fullscreen at 1920x1080).
// Each monitor can be locked or unlocked on its own by clicking the padlock on it in the panel
// (left-click the tray icon). Positions are stored relative to the monitor, so when Windows moves
// a monitor in the desktop layout the windows are put back on it. Windows are moved without
// activating them, so a fullscreen game keeps focus. Everything starts unlocked.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int val, int size);
    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200, SWP_ASYNC = 0x4000;
    public const int SW_SHOWNOACTIVATE = 4, SW_MAXIMIZE = 3;
    public const int WM_DISPLAYCHANGE = 0x7E;
}

class Saved
{
    public IntPtr Hwnd; public string Title;
    public int RelX, RelY, W, H; public bool Maximized;
}

class MonitorState
{
    public string Device;
    public Rectangle LastBounds;
    public List<Saved> Windows = new List<Saved>();
}

class LockForm : Form
{
    NotifyIcon tray;
    readonly Dictionary<string, MonitorState> locks = new Dictionary<string, MonitorState>();
    Timer poll, settle;
    int settleRuns;
    DateTime lastChange = DateTime.MinValue;
    ControlPanelForm panelForm;
    static readonly string logPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "MonitorLock.log");

    public LockForm()
    {
        ShowInTaskbar = false; WindowState = FormWindowState.Minimized; FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0; Size = new Size(1, 1);
        tray = new NotifyIcon { Icon = Art.TrayIcon(false), Visible = true, Text = "MonitorLock (nothing locked)" };
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
        BuildMenu();
        poll = new Timer { Interval = 1000 }; poll.Tick += (s, e) => Poll(); poll.Start();
        settle = new Timer { Interval = 400 }; settle.Tick += (s, e) => { RestoreAll("settle", false); if (++settleRuns >= 5) settle.Stop(); };
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); if (!IsHandleCreated) CreateHandle(); }

    public static Screen Find(string dev) { if (dev == null) return null; foreach (var s in Screen.AllScreens) if (s.DeviceName == dev) return s; return null; }
    static string Short(string dev) { return dev.Replace("\\\\.\\DISPLAY", "Screen "); }

    // ---------- public API for the panel ----------
    public bool IsLocked(string dev) { return dev != null && locks.ContainsKey(dev); }
    public int WindowCount(string dev) { MonitorState m; return locks.TryGetValue(dev, out m) ? m.Windows.Count : 0; }
    public int LockedCount { get { return locks.Count; } }
    public int TotalWindows { get { int n = 0; foreach (var m in locks.Values) n += m.Windows.Count; return n; } }
    public void Toggle(string dev) { if (IsLocked(dev)) Unlock(dev); else { var s = Find(dev); if (s != null) Lock(s); } }
    public void RestoreNow() { RestoreAll("manual", false); }
    public void ExitApp() { tray.Visible = false; Application.Exit(); }

    void BuildMenu()
    {
        var m = tray.ContextMenuStrip; m.Items.Clear();
        foreach (var s in Screen.AllScreens)
        {
            var dev = s.DeviceName;
            var it = new ToolStripMenuItem(string.Format("Lock {0} ({1}x{2}{3})", Short(dev), s.Bounds.Width, s.Bounds.Height, s.Primary ? ", main" : ""), null, (o, e) => Toggle(dev));
            it.Checked = IsLocked(dev);
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        var r = new ToolStripMenuItem("Restore now", null, (o, e) => RestoreNow()); r.Enabled = locks.Count > 0; m.Items.Add(r);
        m.Items.Add(new ToolStripMenuItem("Open MonitorLock", null, (o, e) => ShowPanel()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Exit", null, (o, e) => ExitApp()));
    }

    void ShowPanel()
    {
        if (panelForm == null || panelForm.IsDisposed) panelForm = new ControlPanelForm(this);
        panelForm.RefreshView();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        panelForm.StartPosition = FormStartPosition.Manual;
        panelForm.Location = new Point(wa.Right - panelForm.Width - 12, wa.Bottom - panelForm.Height - 12);
        panelForm.Show(); panelForm.Activate();
    }

    void UpdateTray()
    {
        tray.Icon = Art.TrayIcon(locks.Count > 0);
        tray.Text = locks.Count == 0 ? "MonitorLock (nothing locked)" : Trunc(string.Format("MonitorLock: {0} screen(s), {1} windows locked", locks.Count, TotalWindows));
        if (panelForm != null && !panelForm.IsDisposed && panelForm.Visible) panelForm.RefreshView();
    }
    static string Trunc(string s) { return s.Length > 63 ? s.Substring(0, 63) : s; }

    // ---------- window discovery ----------
    static bool Cloaked(IntPtr h) { int v; return Native.DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0; }

    // top-level, visible, titled app windows whose centre is inside bounds
    static List<Saved> WindowsOn(Rectangle b, HashSet<IntPtr> skip)
    {
        var list = new List<Saved>();
        Native.EnumWindows((h, l) =>
        {
            if ((skip != null && skip.Contains(h)) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Cloaked(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            var cls = new StringBuilder(256); Native.GetClassName(h, cls, 256); string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;
            Native.RECT r; if (!Native.GetWindowRect(h, out r)) return true;
            if (!b.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) return true;
            var t = new StringBuilder(256); Native.GetWindowText(h, t, 256); if (t.Length == 0) return true;
            list.Add(new Saved { Hwnd = h, Title = t.ToString(), RelX = r.Left - b.Left, RelY = r.Top - b.Top, W = r.Right - r.Left, H = r.Bottom - r.Top, Maximized = Native.IsZoomed(h) });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    HashSet<IntPtr> Tracked() { var set = new HashSet<IntPtr>(); foreach (var m in locks.Values) foreach (var w in m.Windows) set.Add(w.Hwnd); return set; }

    void Lock(Screen s)
    {
        var st = new MonitorState { Device = s.DeviceName, LastBounds = s.Bounds };
        locks[s.DeviceName] = st;
        var skip = Tracked();
        st.Windows = WindowsOn(s.Bounds, skip);
        Log("locked " + s.DeviceName + " " + s.Bounds + ": " + string.Join(" | ", st.Windows.ConvertAll(w => w.Title).ToArray()));
        UpdateTray();
    }

    void Unlock(string dev) { if (locks.Remove(dev)) { Log("unlocked " + dev); UpdateTray(); } }

    // ---------- watching for changes ----------
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == Native.WM_DISPLAYCHANGE && locks.Count > 0) { Log("WM_DISPLAYCHANGE"); Kick(); }
    }

    void Kick() { lastChange = DateTime.Now; RestoreAll("change", false); settleRuns = 0; settle.Stop(); settle.Start(); }

    void Poll()
    {
        if (locks.Count == 0) return;
        bool changed = false;
        foreach (var st in locks.Values)
        {
            var s = Find(st.Device); if (s == null) continue;
            if (s.Bounds != st.LastBounds) { Log("bounds changed " + st.Device + " " + st.LastBounds + " -> " + s.Bounds); st.LastBounds = s.Bounds; changed = true; }
        }
        if (changed) { Kick(); return; }
        if ((DateTime.Now - lastChange).TotalSeconds < 5) return; // still settling after a layout change
        Learn();
    }

    // screens are stable: follow the user's own moves, drop windows dragged off a locked screen, pick up new ones
    void Learn()
    {
        bool dirty = false;
        foreach (var st in locks.Values)
        {
            var s = Find(st.Device); if (s == null) continue;
            var b = s.Bounds;
            int before = st.Windows.Count;
            st.Windows.RemoveAll(w => !Native.IsWindow(w.Hwnd));
            st.Windows.RemoveAll(w =>
            {
                if (Native.IsIconic(w.Hwnd)) return false;
                Native.RECT r; Native.GetWindowRect(w.Hwnd, out r);
                if (!b.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) { Log("released (moved off " + st.Device + "): " + w.Title); return true; }
                w.Maximized = Native.IsZoomed(w.Hwnd);
                if (!w.Maximized) { w.RelX = r.Left - b.Left; w.RelY = r.Top - b.Top; w.W = r.Right - r.Left; w.H = r.Bottom - r.Top; }
                return false;
            });
            var added = WindowsOn(b, Tracked());
            foreach (var w in added) Log("adopted on " + st.Device + ": " + w.Title);
            st.Windows.AddRange(added);
            if (st.Windows.Count != before || added.Count > 0) dirty = true;
        }
        if (dirty) UpdateTray();
    }

    void RestoreAll(string why, bool unused)
    {
        foreach (var st in locks.Values)
        {
            var s = Find(st.Device); if (s == null) continue;
            var b = s.Bounds; int moved = 0;
            st.Windows.RemoveAll(w => !Native.IsWindow(w.Hwnd));
            foreach (var w in st.Windows)
            {
                if (Native.IsIconic(w.Hwnd)) continue;
                int x = b.Left + w.RelX, y = b.Top + w.RelY;
                Native.RECT r; Native.GetWindowRect(w.Hwnd, out r);
                bool onTarget = b.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
                if (w.Maximized)
                {
                    if (onTarget && Native.IsZoomed(w.Hwnd)) continue;
                    Native.ShowWindow(w.Hwnd, Native.SW_SHOWNOACTIVATE);
                    Native.SetWindowPos(w.Hwnd, IntPtr.Zero, b.Left + 50, b.Top + 50, Math.Min(w.W, b.Width - 100), Math.Min(w.H, b.Height - 100), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                    Native.ShowWindow(w.Hwnd, Native.SW_MAXIMIZE);
                }
                else
                {
                    if (onTarget && Math.Abs(r.Left - x) < 2 && Math.Abs(r.Top - y) < 2) continue;
                    Native.SetWindowPos(w.Hwnd, IntPtr.Zero, x, y, w.W, w.H, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNC);
                }
                moved++;
            }
            if (moved > 0) Log(why + ": restored " + moved + " window(s) on " + st.Device + " " + b);
        }
    }

    public static void Log(string m)
    {
        try
        {
            var fi = new FileInfo(logPath);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + Environment.NewLine);
        }
        catch { }
    }
}

class ControlPanelForm : Form
{
    readonly LockForm owner;
    Label status; CheckBox startup; MonitorMap map;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public ControlPanelForm(LockForm o)
    {
        owner = o;
        Text = "MonitorLock"; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false; TopMost = true; Font = new Font("Segoe UI", 9.5f); Icon = Art.TrayIcon(true);
        ClientSize = new Size(300, 222);
        BackColor = Color.FromArgb(32, 32, 32); ForeColor = Color.FromArgb(230, 230, 230);

        status = new Label { Location = new Point(12, 12), Size = new Size(276, 20) };
        map = new MonitorMap(owner) { Location = new Point(12, 38), Size = new Size(276, 140) };
        map.Toggled += dev => { owner.Toggle(dev); RefreshView(); };
        startup = new CheckBox { Text = "Start with Windows", Location = new Point(12, 192), AutoSize = true };
        startup.CheckedChanged += (s, e) => SetStartup(startup.Checked);
        Controls.AddRange(new Control[] { status, map, startup });

        Deactivate += (s, e) => Hide();
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void RefreshView()
    {
        int n = owner.LockedCount;
        status.Text = n == 0 ? "○ Nothing locked – click a padlock" : string.Format("● {0} screen{1} locked ({2} windows)", n, n == 1 ? "" : "s", owner.TotalWindows);
        status.ForeColor = n > 0 ? Art.Green : Color.FromArgb(170, 170, 170);
        map.Invalidate();
        startup.Checked = IsStartup();
    }

    static bool IsStartup()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("MonitorLock") != null;
    }
    static void SetStartup(bool on)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k == null) return;
            if (on) k.SetValue("MonitorLock", "\"" + Application.ExecutablePath + "\"");
            else if (k.GetValue("MonitorLock") != null) k.DeleteValue("MonitorLock");
        }
    }
}

// Mini map of the monitors in their real arrangement, each with its own padlock button.
class MonitorMap : Control
{
    readonly LockForm owner;
    public event Action<string> Toggled;
    readonly List<KeyValuePair<string, RectangleF>> buttons = new List<KeyValuePair<string, RectangleF>>();
    string hover;

    public MonitorMap(LockForm o) { owner = o; DoubleBuffered = true; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Color.FromArgb(32, 32, 32));
        buttons.Clear();
        var all = Screen.AllScreens; if (all.Length == 0) return;
        Rectangle u = all[0].Bounds; foreach (var s in all) u = Rectangle.Union(u, s.Bounds);
        float pad = 4, scale = Math.Min((Width - 2 * pad) / u.Width, (Height - 2 * pad) / u.Height);
        float ox = (Width - u.Width * scale) / 2, oy = (Height - u.Height * scale) / 2;
        foreach (var s in all)
        {
            var r = new RectangleF(ox + (s.Bounds.X - u.X) * scale + 3, oy + (s.Bounds.Y - u.Y) * scale + 3, s.Bounds.Width * scale - 6, s.Bounds.Height * scale - 6);
            bool locked = owner.IsLocked(s.DeviceName);
            using (var fill = new SolidBrush(locked ? Color.FromArgb(44, 58, 48) : Color.FromArgb(46, 46, 46)))
            using (var pen = new Pen(locked ? Art.Green : Color.FromArgb(95, 95, 95), locked ? 2f : 1f))
            { g.FillRectangle(fill, r); g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height); }

            // padlock button in the middle of the monitor
            float bs = 34; var btn = new RectangleF(r.X + (r.Width - bs) / 2, r.Y + (r.Height - bs) / 2 - 8, bs, bs);
            buttons.Add(new KeyValuePair<string, RectangleF>(s.DeviceName, btn));
            bool hot = hover == s.DeviceName;
            using (var bfill = new SolidBrush(hot ? Color.FromArgb(70, 70, 70) : Color.FromArgb(56, 56, 56)))
            using (var bpen = new Pen(hot ? Color.FromArgb(150, 150, 150) : Color.FromArgb(85, 85, 85)))
            using (var path = Art.Rounded(btn, 7))
            { g.FillPath(bfill, path); g.DrawPath(bpen, path); }
            using (var lk = Art.Padlock(!locked, locked ? Art.Green : Color.FromArgb(185, 185, 185), 20))
                g.DrawImage(lk, btn.X + (bs - 20) / 2, btn.Y + (bs - 20) / 2, 20, 20);

            string label = s.DeviceName.Replace("\\\\.\\DISPLAY", "") + (s.Primary ? " • main" : "");
            if (locked) { int n = owner.WindowCount(s.DeviceName); label += string.Format(" · {0} win", n); }
            using (var f = new Font("Segoe UI", 8f))
            using (var br = new SolidBrush(Color.FromArgb(195, 195, 195)))
            {
                var sz = g.MeasureString(label, f);
                if (sz.Width > r.Width - 4) { label = label.Replace(" • main", ""); sz = g.MeasureString(label, f); }
                g.DrawString(label, f, br, r.X + (r.Width - sz.Width) / 2, btn.Bottom + 4);
            }
        }
    }

    string HitButton(Point p) { foreach (var b in buttons) if (b.Value.Contains(p)) return b.Key; return null; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitButton(e.Location);
        Cursor = h != null ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { if (hover != null) { hover = null; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        var h = HitButton(e.Location);
        if (h != null && Toggled != null) Toggled(h);
    }
}

// Hand-drawn icons (GDI+), so the tool needs no image files.
static class Art
{
    public static readonly Color Green = Color.FromArgb(120, 210, 140);

    // padlock: shackle arc + body + keyhole. open = shackle lifted and swung right
    public static Bitmap Padlock(bool open, Color c, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        using (var pen = new Pen(c, Math.Max(1.6f, size / 9f)))
        using (var br = new SolidBrush(c))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size;
            var body = new RectangleF(s * 0.18f, s * 0.46f, s * 0.64f, s * 0.46f);
            float sw = s * 0.42f, sx = s * 0.29f;
            if (!open)
            {
                float sy = s * 0.12f;
                g.DrawArc(pen, sx, sy, sw, s * 0.48f, 180, 180);
                g.DrawLine(pen, sx, sy + s * 0.24f, sx, body.Top);
                g.DrawLine(pen, sx + sw, sy + s * 0.24f, sx + sw, body.Top);
            }
            else
            {
                // same shackle, lifted straight up: left leg stays in the body, right leg pulled out with a clear gap
                float sy = s * 0.0f;
                g.DrawArc(pen, sx, sy, sw, s * 0.44f, 180, 180);
                g.DrawLine(pen, sx, sy + s * 0.22f, sx, body.Top);
                g.DrawLine(pen, sx + sw, sy + s * 0.22f, sx + sw, sy + s * 0.30f);
            }
            using (var path = Rounded(body, s * 0.08f)) g.FillPath(br, path);
            using (var hole = new SolidBrush(Color.FromArgb(55, 55, 55)))
                g.FillEllipse(hole, s * 0.44f, s * 0.6f, s * 0.12f, s * 0.12f);
        }
        return bmp;
    }

    // tray: small monitor with a padlock badge; green when anything is locked, grey when not
    public static Icon TrayIcon(bool locked)
    {
        int s = 32;
        using (var bmp = new Bitmap(s, s))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(235, 235, 235), 2.6f))
                {
                    g.DrawRectangle(pen, 2, 4, 22, 16);
                    g.DrawLine(pen, 13, 20, 13, 25);
                    g.DrawLine(pen, 7, 26, 19, 26);
                }
                using (var lockBmp = Padlock(!locked, locked ? Green : Color.FromArgb(150, 150, 150), 18)) g.DrawImage(lockBmp, 14, 13, 18, 18);
            }
            IntPtr h = bmp.GetHicon();
            var ico = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return ico;
        }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2
        bool created;
        using (var mx = new System.Threading.Mutex(true, "MonitorLock_single_instance", out created))
        {
            if (!created) return;
            // never die silently: log any error and keep running
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LockForm.Log("UI error (kept running): " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LockForm.Log("FATAL: " + e.ExceptionObject);
            Application.ApplicationExit += (s, e) => LockForm.Log("exiting normally");
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => LockForm.Log("Windows session ending: " + e.Reason);
            LockForm.Log("started (everything unlocked)");
            Application.EnableVisualStyles();
            Application.Run(new LockForm());
        }
    }
}

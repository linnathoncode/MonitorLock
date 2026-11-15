// MonitorLock - keeps the windows on a chosen monitor where you locked them,
// even when another monitor changes resolution (e.g. a game going fullscreen at 1920x1080).
// Tray icon: "Lock layout on <monitor>" saves positions relative to that monitor;
// on any display change the windows are put back relative to the monitor's new position.
// Windows are moved without activating them, so a fullscreen game keeps focus.
using System;
using System.Collections.Generic;
using System.Drawing;
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
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200, SWP_ASYNC = 0x4000;
    public const int SW_SHOWNOACTIVATE = 4, SW_SHOWMAXNOACTIVE = 3; // SW_MAXIMIZE(3) used with care
    public const int WM_DISPLAYCHANGE = 0x7E;
}

class Saved
{
    public IntPtr Hwnd; public string Title;
    public int RelX, RelY, W, H; public bool Maximized;
}

class LockForm : Form
{
    NotifyIcon tray;
    string lockedDevice;           // e.g. \\.\DISPLAY2
    List<Saved> saved = new List<Saved>();
    Rectangle lastBounds;
    Timer poll, settle;
    int settleRuns;
    string logPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "MonitorLock.log");

    public LockForm()
    {
        ShowInTaskbar = false; WindowState = FormWindowState.Minimized; FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0; Size = new Size(1, 1);
        tray = new NotifyIcon { Icon = Art.TrayIcon(false), Visible = true, Text = "MonitorLock (unlocked)" };
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
        BuildMenu();
        poll = new Timer { Interval = 1000 }; poll.Tick += (s, e) => CheckBounds(); poll.Start();
        settle = new Timer { Interval = 400 }; settle.Tick += (s, e) => { Restore("settle"); if (++settleRuns >= 5) settle.Stop(); };
        // auto-lock the first non-primary monitor on start, after a short delay so windows exist
        var t = new Timer { Interval = 15000 }; t.Tick += (s, e) => { t.Stop(); if (lockedDevice == null) { var m = FirstSecondary(); if (m != null) LockMonitor(m); } }; t.Start();
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); if (!IsHandleCreated) CreateHandle(); }

    static Screen FirstSecondary() { foreach (var s in Screen.AllScreens) if (!s.Primary) return s; return null; }
    static Screen Find(string dev) { foreach (var s in Screen.AllScreens) if (s.DeviceName == dev) return s; return null; }

    void BuildMenu()
    {
        var m = tray.ContextMenuStrip; m.Items.Clear();
        foreach (var s in Screen.AllScreens)
        {
            var scr = s;
            string label = string.Format("Lock layout on {0} ({1}x{2}{3})", scr.DeviceName.Replace("\\\\.\\", ""), scr.Bounds.Width, scr.Bounds.Height, scr.Primary ? ", main" : "");
            var it = new ToolStripMenuItem(label, null, (o, e) => LockMonitor(scr));
            if (scr.DeviceName == lockedDevice) it.Checked = true;
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        var r = new ToolStripMenuItem("Restore now", null, (o, e) => Restore("manual")); r.Enabled = lockedDevice != null; m.Items.Add(r);
        var u = new ToolStripMenuItem("Unlock", null, (o, e) => Unlock()); u.Enabled = lockedDevice != null; m.Items.Add(u);
        m.Items.Add(new ToolStripMenuItem("Open MonitorLock", null, (o, e) => ShowPanel()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Exit", null, (o, e) => { tray.Visible = false; Application.Exit(); }));
    }

    // ---- small control panel (left-click the tray icon) ----
    ControlPanelForm panelForm;
    public string LockedDevice { get { return lockedDevice; } }
    public List<Saved> SavedWindows { get { return saved; } }
    public DateTime LastRestore = DateTime.MinValue;
    public int LastRestoreCount;
    public void LockOn(Screen s) { LockMonitor(s); }
    public void LockSecondary() { var s = FirstSecondary(); if (s != null) LockMonitor(s); }
    public void RestoreNow() { Restore("manual"); }
    public void Unlock() { lockedDevice = null; saved.Clear(); tray.Text = "MonitorLock (unlocked)"; tray.Icon = Art.TrayIcon(false); Log("unlocked"); }
    public void ExitApp() { tray.Visible = false; Application.Exit(); }
    void ShowPanel()
    {
        if (panelForm == null || panelForm.IsDisposed) panelForm = new ControlPanelForm(this);
        panelForm.RefreshView();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        panelForm.StartPosition = FormStartPosition.Manual;
        panelForm.Location = new Point(wa.Right - panelForm.Width - 12, wa.Bottom - panelForm.Height - 12);
        panelForm.Show(); panelForm.Activate();
    }

    static bool Cloaked(IntPtr h) { int v; return Native.DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0; }

    void LockMonitor(Screen scr)
    {
        lockedDevice = scr.DeviceName; lastBounds = scr.Bounds; saved.Clear();
        var b = scr.Bounds;
        Native.EnumWindows((h, l) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || Cloaked(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            var cls = new StringBuilder(256); Native.GetClassName(h, cls, 256);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;
            Native.RECT r; if (!Native.GetWindowRect(h, out r)) return true;
            var cx = (r.Left + r.Right) / 2; var cy = (r.Top + r.Bottom) / 2;
            if (!b.Contains(cx, cy)) return true;
            var t = new StringBuilder(256); Native.GetWindowText(h, t, 256);
            if (t.Length == 0) return true;
            saved.Add(new Saved { Hwnd = h, Title = t.ToString(), RelX = r.Left - b.Left, RelY = r.Top - b.Top, W = r.Right - r.Left, H = r.Bottom - r.Top, Maximized = Native.IsZoomed(h) });
            return true;
        }, IntPtr.Zero);
        tray.Text = Trunc(string.Format("MonitorLock: {0} windows locked on {1}", saved.Count, lockedDevice.Replace("\\\\.\\", "")));
        tray.Icon = Art.TrayIcon(true);
        Log("locked " + lockedDevice + " " + b + ": " + string.Join(" | ", saved.ConvertAll(s => s.Title).ToArray()));
        tray.ShowBalloonTip(2500, "MonitorLock", saved.Count + " windows locked on this monitor.", ToolTipIcon.Info);
    }

    static string Trunc(string s) { return s.Length > 63 ? s.Substring(0, 63) : s; }

    void CheckBounds()
    {
        if (lockedDevice == null) return;
        var s = Find(lockedDevice); if (s == null) return;
        if (s.Bounds != lastBounds) { Log("bounds changed " + lastBounds + " -> " + s.Bounds); lastBounds = s.Bounds; Kick(); }
        else
        {
            Restore("poll", true);
            // pick up windows opened on this monitor since locking (only when screens have been stable for 10 s)
            if ((DateTime.Now - lastChange).TotalSeconds > 10) Adopt(s.Bounds);
        }
    }

    DateTime lastChange = DateTime.MinValue;
    void Kick() { lastChange = DateTime.Now; Restore("change"); settleRuns = 0; settle.Stop(); settle.Start(); }

    void Adopt(Rectangle b)
    {
        var known = new HashSet<IntPtr>(); foreach (var w in saved) known.Add(w.Hwnd);
        Native.EnumWindows((h, l) =>
        {
            if (known.Contains(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Cloaked(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            var cls = new StringBuilder(256); Native.GetClassName(h, cls, 256); string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;
            Native.RECT r; if (!Native.GetWindowRect(h, out r)) return true;
            if (!b.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) return true;
            var t = new StringBuilder(256); Native.GetWindowText(h, t, 256); if (t.Length == 0) return true;
            saved.Add(new Saved { Hwnd = h, Title = t.ToString(), RelX = r.Left - b.Left, RelY = r.Top - b.Top, W = r.Right - r.Left, H = r.Bottom - r.Top, Maximized = Native.IsZoomed(h) });
            Log("adopted: " + t);
            return true;
        }, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == Native.WM_DISPLAYCHANGE && lockedDevice != null) { Log("WM_DISPLAYCHANGE"); Kick(); }
    }

    void Restore(string why, bool onlyIfMoved = false)
    {
        if (lockedDevice == null) return;
        var s = Find(lockedDevice); if (s == null) return;
        var b = s.Bounds; int moved = 0;
        saved.RemoveAll(w => !Native.IsWindow(w.Hwnd));
        foreach (var w in saved)
        {
            if (Native.IsIconic(w.Hwnd)) continue;
            int x = b.Left + w.RelX, y = b.Top + w.RelY;
            Native.RECT r; Native.GetWindowRect(w.Hwnd, out r);
            bool zoomed = Native.IsZoomed(w.Hwnd);
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            bool onTarget = b.Contains(cx, cy);
            if (onlyIfMoved)
            {
                // only act if the window left its monitor (Windows shifted it); user moves within the monitor are kept
                if (onTarget) { if (!zoomed) { w.RelX = r.Left - b.Left; w.RelY = r.Top - b.Top; w.W = r.Right - r.Left; w.H = r.Bottom - r.Top; } w.Maximized = zoomed; continue; }
            }
            else if (onTarget && Math.Abs(r.Left - x) < 2 && Math.Abs(r.Top - y) < 2) continue;
            if (w.Maximized)
            {
                if (zoomed && onTarget) continue;
                Native.ShowWindow(w.Hwnd, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(w.Hwnd, IntPtr.Zero, b.Left + 50, b.Top + 50, Math.Min(w.W, b.Width - 100), Math.Min(w.H, b.Height - 100), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                Native.ShowWindow(w.Hwnd, Native.SW_SHOWMAXNOACTIVE);
            }
            else
            {
                Native.SetWindowPos(w.Hwnd, IntPtr.Zero, x, y, w.W, w.H, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNC);
            }
            moved++;
        }
        if (moved > 0) { Log(why + ": restored " + moved + " window(s) to " + b); LastRestore = DateTime.Now; LastRestoreCount = moved; }
    }

    void Log(string m)
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
    Label status; Button toggle; CheckBox startup; MonitorMap map;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public ControlPanelForm(LockForm o)
    {
        owner = o;
        Text = "MonitorLock"; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false; TopMost = true; Font = new Font("Segoe UI", 9.5f); Icon = Art.TrayIcon(true);
        ClientSize = new Size(240, 196);
        BackColor = Color.FromArgb(32, 32, 32); ForeColor = Color.FromArgb(230, 230, 230);

        status = new Label { Location = new Point(12, 10), Size = new Size(216, 20) };
        map = new MonitorMap { Location = new Point(12, 34), Size = new Size(216, 76) };
        map.Picked += dev =>
        {
            // picking another monitor while locked moves the lock to it right away
            if (owner.LockedDevice != null && owner.LockedDevice != dev) { var s = MonitorMap.Find(dev); if (s != null) owner.LockOn(s); }
            RefreshView();
        };
        toggle = Btn("", 12, 120, (s, e) =>
        {
            if (owner.LockedDevice == null) { var sc = MonitorMap.Find(map.Selected); if (sc != null) owner.LockOn(sc); }
            else owner.Unlock();
            RefreshView();
        });
        var restore = Btn("Restore", 124, 120, (s, e) => { owner.RestoreNow(); RefreshView(); });
        restore.Image = Art.Restore(ForeColor, 16);
        startup = new CheckBox { Text = "Start with Windows", Location = new Point(12, 164), AutoSize = true };
        startup.CheckedChanged += (s, e) => SetStartup(startup.Checked);
        Controls.AddRange(new Control[] { status, map, toggle, restore, startup });

        Deactivate += (s, e) => Hide();
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    Button Btn(string t, int x, int y, EventHandler h)
    {
        var b = new Button { Text = t, Location = new Point(x, y), Size = new Size(104, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 55, 55), ForeColor = ForeColor };
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80); b.Click += h;
        b.TextImageRelation = TextImageRelation.ImageBeforeText; b.ImageAlign = ContentAlignment.MiddleCenter; b.TextAlign = ContentAlignment.MiddleCenter;
        return b;
    }

    public void RefreshView()
    {
        bool locked = owner.LockedDevice != null;
        status.Text = locked ? "● Locked (" + owner.SavedWindows.Count + " windows)" : "○ Unlocked – pick a screen";
        status.ForeColor = locked ? Color.FromArgb(120, 210, 140) : Color.FromArgb(170, 170, 170);
        toggle.Text = locked ? "Unlock" : "Lock";
        var old = toggle.Image;
        toggle.Image = Art.Padlock(locked, ForeColor, 16); // open padlock = the button unlocks
        if (old != null) old.Dispose();
        map.LockedDevice = owner.LockedDevice;
        if (locked) map.Selected = owner.LockedDevice;
        else if (MonitorMap.Find(map.Selected) == null) { var s = MonitorMap.FirstSecondaryOrPrimary(); map.Selected = s == null ? null : s.DeviceName; }
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

// Mini map of the monitors in their real arrangement; click one to pick it.
class MonitorMap : Control
{
    public string Selected, LockedDevice;
    public event Action<string> Picked;
    readonly List<KeyValuePair<string, RectangleF>> hit = new List<KeyValuePair<string, RectangleF>>();

    public MonitorMap() { DoubleBuffered = true; Cursor = Cursors.Hand; }

    public static Screen Find(string dev) { if (dev == null) return null; foreach (var s in Screen.AllScreens) if (s.DeviceName == dev) return s; return null; }
    public static Screen FirstSecondaryOrPrimary() { foreach (var s in Screen.AllScreens) if (!s.Primary) return s; return Screen.PrimaryScreen; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : Color.FromArgb(32, 32, 32));
        hit.Clear();
        var all = Screen.AllScreens; if (all.Length == 0) return;
        Rectangle u = all[0].Bounds; foreach (var s in all) u = Rectangle.Union(u, s.Bounds);
        float pad = 4, scale = Math.Min((Width - 2 * pad) / u.Width, (Height - 2 * pad) / u.Height);
        float ox = (Width - u.Width * scale) / 2, oy = (Height - u.Height * scale) / 2;
        int n = 1;
        foreach (var s in all)
        {
            var r = new RectangleF(ox + (s.Bounds.X - u.X) * scale + 2, oy + (s.Bounds.Y - u.Y) * scale + 2, s.Bounds.Width * scale - 4, s.Bounds.Height * scale - 4);
            hit.Add(new KeyValuePair<string, RectangleF>(s.DeviceName, r));
            bool sel = s.DeviceName == Selected, locked = s.DeviceName == LockedDevice;
            var green = Color.FromArgb(120, 210, 140);
            using (var fill = new SolidBrush(sel ? Color.FromArgb(58, 68, 62) : Color.FromArgb(48, 48, 48)))
            using (var pen = new Pen(locked ? green : sel ? Color.FromArgb(200, 200, 200) : Color.FromArgb(90, 90, 90), sel || locked ? 2f : 1f))
            {
                g.FillRectangle(fill, r); g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }
            string label = s.DeviceName.Replace("\\\\.\\DISPLAY", "") + (s.Primary ? " • main" : "");
            using (var f = new Font("Segoe UI", 8f))
            using (var br = new SolidBrush(Color.FromArgb(200, 200, 200)))
            {
                var sz = g.MeasureString(label, f);
                g.DrawString(label, f, br, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2 - (locked ? 6 : 0));
            }
            if (locked) using (var lk = Art.Padlock(false, green, 12)) g.DrawImage(lk, r.X + (r.Width - 12) / 2, r.Y + r.Height / 2 + 2, 12, 12);
            n++;
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        foreach (var h in hit) if (h.Value.Contains(e.Location)) { Selected = h.Key; Invalidate(); if (Picked != null) Picked(h.Key); return; }
    }
}

// Hand-drawn icons (GDI+), so the tool needs no image files.
static class Art
{
    static readonly Color Accent = Color.FromArgb(120, 210, 140);

    // padlock: shackle arc + body + keyhole. open = shackle lifted and swung right
    public static Bitmap Padlock(bool open, Color c, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        using (var pen = new Pen(c, Math.Max(1.6f, size / 9f)))
        using (var br = new SolidBrush(c))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float s = size;
            var body = new RectangleF(s * 0.18f, s * 0.46f, s * 0.64f, s * 0.46f);
            float sw = s * 0.42f, sx = s * 0.29f, sy = open ? s * 0.04f : s * 0.12f;
            if (open) sx += s * 0.16f;
            g.DrawArc(pen, sx, sy, sw, s * 0.48f, 180, 180);
            g.DrawLine(pen, sx + sw, sy + s * 0.24f, sx + sw, body.Top);
            if (!open) g.DrawLine(pen, sx, sy + s * 0.24f, sx, body.Top);
            using (var path = Rounded(body, s * 0.08f)) g.FillPath(br, path);
            using (var hole = new SolidBrush(Color.FromArgb(55, 55, 55)))
                g.FillEllipse(hole, s * 0.44f, s * 0.6f, s * 0.12f, s * 0.12f);
        }
        return bmp;
    }

    // restore: circular arrow
    public static Bitmap Restore(Color c, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        using (var pen = new Pen(c, Math.Max(1.6f, size / 9f)))
        using (var br = new SolidBrush(c))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float s = size, m = s * 0.16f;
            g.DrawArc(pen, m, m, s - 2 * m, s - 2 * m, -60, 290);
            // arrow head at the arc start (angle -60 deg), pointing clockwise
            double a = -60 * Math.PI / 180; float r = (s - 2 * m) / 2, cx = s / 2, cy = s / 2;
            float px = cx + (float)(r * Math.Cos(a)), py = cy + (float)(r * Math.Sin(a));
            float h = s * 0.26f;
            g.FillPolygon(br, new[] { new PointF(px + h * 0.55f, py - h * 0.55f), new PointF(px + h * 0.6f, py + h * 0.45f), new PointF(px - h * 0.45f, py + h * 0.05f) });
        }
        return bmp;
    }

    // tray: small monitor with a padlock badge; green when locked, grey when not
    public static Icon TrayIcon(bool locked)
    {
        int s = 32;
        using (var bmp = new Bitmap(s, s))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var frame = Color.FromArgb(235, 235, 235);
                using (var pen = new Pen(frame, 2.6f))
                {
                    g.DrawRectangle(pen, 2, 4, 22, 16);
                    g.DrawLine(pen, 13, 20, 13, 25);
                    g.DrawLine(pen, 7, 26, 19, 26);
                }
                var badge = locked ? Accent : Color.FromArgb(150, 150, 150);
                using (var lockBmp = Padlock(!locked, badge, 18)) g.DrawImage(lockBmp, 14, 13, 18, 18);
            }
            IntPtr h = bmp.GetHicon();
            var ico = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return ico;
        }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath(); float d = rad * 2;
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
            Application.EnableVisualStyles();
            Application.Run(new LockForm());
        }
    }
}

// UsageBarOverlay（额度悬浮条）：把 Claude Code 的 5 小时 / 本周额度贴在 Claude 桌面端输入框正上方。
//
// Claude 桌面端要等会话激活才启动 Claude Code 后台，插件画的额度条在那之前不出现。
// 这个程序常驻后台：Claude 窗口在，就用 UI Automation 找到输入框，把一条和插件
// 额度条一模一样的悬浮条贴在它上方；插件的额度条出现后自动让位，Claude 最小化 /
// 关闭时跟着消失。数据来自插件 usage-bar 写在它数据文件夹里的 usage.json。
//
// 编译（无需 SDK）：sh build.sh，见仓库 README

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz,
        IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    public const int GWLP_HWNDPARENT = -8;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public const int ULW_ALPHA = 2;
    public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
}

// 一次 UI Automation 扫描得出的结果：悬浮条该不该出现、放在哪
internal sealed class Layout
{
    public IntPtr Window;
    public Native.RECT WindowRect;   // 扫描时 Claude 窗口的位置，窗口平移时据此跟着平移
    public bool Show;
    public int Left, Width, Bottom;  // 屏幕物理像素：悬浮条的左边、宽度、底边（高度按内容一行或两行）
    public float Scale;
    public double? WeeklyFromApp;    // Claude 自带用量按钮上的周额度百分比
    public string Why;
}

internal sealed class Limit
{
    public double Percent;
    public DateTime? ResetsAt;
}

internal sealed class Usage
{
    public Limit FiveHour, SevenDay;
}

internal static class Theme
{
    // 取自 Claude 桌面端深色主题下插件额度条的实际像素
    public static readonly Color Box = Color.FromArgb(0x25, 0x25, 0x25);
    public static readonly Color Dim = Color.FromArgb(0xA1, 0xA1, 0xA1);
    public static readonly Color Bright = Color.FromArgb(0xF0, 0xF0, 0xF0);
    public static readonly Color Green = Color.FromArgb(0x46, 0xA7, 0x58);
    public static readonly Color Amber = Color.FromArgb(0xFF, 0xC5, 0x3D);
    public static readonly Color Red = Color.FromArgb(0xE5, 0x48, 0x4D);

    public static Color Tint(double pct)
    {
        return pct >= 90 ? Red : pct >= 70 ? Amber : Green;
    }
}

internal static class Painter
{
    static readonly string[] Week = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    // 一行放得下两组就一行（高 40），放不下就像插件额度条那样折成两行
    public const float RowStep = 26;

    public static int Rows(int width, float s, Usage u)
    {
        if (u == null) return 1;
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Microsoft YaHei UI", 14 * s, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var bold = new Font("Microsoft YaHei UI", 14 * s, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float x = 9 * s;
            Group(g, false, "5小时", u.FiveHour, false, font, bold, s, ref x, 0);
            x += 26 * s;
            Group(g, false, "本周", u.SevenDay, true, font, bold, s, ref x, 0);
            return x + 9 * s <= width ? 1 : 2;
        }
    }

    public static int Height(int rows, float s)
    {
        return (int)Math.Round((40 + (rows - 1) * RowStep) * s);
    }

    // 画出整条悬浮条（含圆角外透明部分），尺寸 = box 大小
    public static Bitmap Draw(Size size, float s, Usage u, int rows)
    {
        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float r = 10 * s;
            using (var path = RoundRect(new RectangleF(0, 0, size.Width, size.Height), r))
            using (var b = new SolidBrush(Theme.Box))
                g.FillPath(b, path);

            using (var font = new Font("Microsoft YaHei UI", 14 * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bold = new Font("Microsoft YaHei UI", 14 * s, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                float x = 9 * s;
                float mid = 20 * s;
                if (u == null)
                {
                    Text(g, true, "额度：等待第一次读数", font, Theme.Dim, ref x, mid);
                }
                else
                {
                    Group(g, true, "5小时", u.FiveHour, false, font, bold, s, ref x, mid);
                    if (rows > 1) { x = 9 * s; mid += RowStep * s; }
                    else x += 26 * s;
                    Group(g, true, "本周", u.SevenDay, true, font, bold, s, ref x, mid);
                }
            }
        }
        return bmp;
    }

    static void Group(Graphics g, bool paint, string label, Limit l, bool withDay, Font font, Font bold, float s, ref float x, float mid)
    {
        if (l == null)
        {
            Text(g, paint, label + " 暂无数据", font, Theme.Dim, ref x, mid);
            return;
        }
        bool expired = l.ResetsAt.HasValue && l.ResetsAt.Value <= DateTime.Now;
        double pct = expired ? 0 : l.Percent;

        Text(g, paint, label, font, Theme.Dim, ref x, mid);
        x += 4 * s;
        Bar(g, paint, pct, s, ref x, mid);
        x += 4 * s;
        Text(g, paint, Pct(pct), bold, Theme.Bright, ref x, mid);
        x += 4 * s;
        string reset = expired ? "已重置" : ResetText(l.ResetsAt, withDay);
        if (reset.Length > 0) Text(g, paint, reset, font, Theme.Dim, ref x, mid);
    }

    static string Pct(double p)
    {
        return (Math.Round(p * 10) / 10).ToString("0.#") + "%";
    }

    static string ResetText(DateTime? at, bool withDay)
    {
        if (!at.HasValue) return "";
        var d = at.Value;
        string t = d.ToString("HH:mm");
        return withDay ? Week[(int)d.DayOfWeek] + " " + t + " 重置" : t + " 重置";
    }

    // 和插件里 █ / ░ 字符画出来的样子一致：实心格 10px，斜纹格约 13.9px
    static void Bar(Graphics g, bool paint, double pct, float s, ref float x, float mid)
    {
        int filled = (int)Math.Max(0, Math.Min(10, Math.Round(pct / 10)));
        Color c = Theme.Tint(pct);
        float fillW = 10f * s * filled;
        float hatchW = 13.9f * s * (10 - filled);
        if (!paint) { x += fillW + hatchW; return; }

        if (fillW > 0)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(c))
                g.FillRectangle(b, x, mid - 7 * s, fillW, 13 * s);
            // █ 字符之间有一道细缝
            using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                for (int i = 1; i < filled; i++)
                    g.FillRectangle(b, x + 10f * s * i - 0.5f * s, mid - 7 * s, Math.Max(1f, s), 13 * s);
            g.SmoothingMode = old;
        }

        if (hatchW > 0)
        {
            var clip = new RectangleF(x + fillW, mid - 8 * s, hatchW, 16 * s);
            var state = g.Save();
            g.SetClip(clip);
            using (var pen = new Pen(Color.FromArgb(200, c), Math.Max(1f, s)))
            {
                float step = 7 * s;
                for (float lx = clip.Left - clip.Height; lx < clip.Right; lx += step)
                    g.DrawLine(pen, lx, clip.Top, lx + clip.Height, clip.Bottom);
            }
            g.Restore(state);
        }
        x += fillW + hatchW;
    }

    static void Text(Graphics g, bool paint, string text, Font font, Color color, ref float x, float mid)
    {
        var fmt = StringFormat.GenericTypographic;
        var size = g.MeasureString(text, font, PointF.Empty, fmt);
        if (paint)
        {
            float y = mid - size.Height / 2f;
            using (var b = new SolidBrush(color))
                g.DrawString(text, font, b, x, y, fmt);
        }
        x += size.Width;
    }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

internal static class UsageFile
{
    // 插件 usage-bar 把最新读数写在它的数据文件夹里：
    //   %USERPROFILE%\.claude\plugins\data\usage-bar-<插件源名>\usage.json
    // （设了 CLAUDE_CONFIG_DIR 时在那个目录下）。有多份时取最新的。
    public static string Find()
    {
        string best = null;
        DateTime bestTime = DateTime.MinValue;
        var roots = new List<string>();
        string cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(cfg)) roots.Add(cfg);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
        foreach (var root in roots)
        {
            try
            {
                string data = Path.Combine(root, "plugins", "data");
                if (!Directory.Exists(data)) continue;
                foreach (var dir in Directory.GetDirectories(data, "usage-bar*"))
                {
                    string f = Path.Combine(dir, "usage.json");
                    if (!File.Exists(f)) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
            }
            catch { }
        }
        return best;
    }

    public static Usage Read()
    {
        try
        {
            string PathName = Find();
            if (PathName == null) return null;
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(PathName, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null) return null;
            return new Usage { FiveHour = ToLimit(root, "fiveHour"), SevenDay = ToLimit(root, "sevenDay") };
        }
        catch { return null; }
    }

    static Limit ToLimit(Dictionary<string, object> root, string key)
    {
        object o;
        if (!root.TryGetValue(key, out o)) return null;
        var d = o as Dictionary<string, object>;
        if (d == null) return null;
        var l = new Limit();
        object v;
        if (d.TryGetValue("percentUsed", out v) && v != null) l.Percent = Convert.ToDouble(v);
        if (d.TryGetValue("resetsAt", out v) && v is string)
        {
            DateTime t;
            if (DateTime.TryParse((string)v, null, System.Globalization.DateTimeStyles.RoundtripKind, out t))
                l.ResetsAt = t.ToLocalTime();
        }
        return l;
    }
}

// 找 Claude 主窗口，并用 UI Automation 量出输入框的位置
internal static class Finder
{
    public static IntPtr FindClaude()
    {
        IntPtr found = IntPtr.Zero;
        var cls = new StringBuilder(64);
        var title = new StringBuilder(64);
        Native.EnumWindows((h, l) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            cls.Length = 0; Native.GetClassName(h, cls, 64);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            title.Length = 0; Native.GetWindowText(h, title, 64);
            if (title.ToString() != "Claude") return true;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    if (!string.Equals(p.ProcessName, "Claude", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static bool Usable(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
        int cloaked;
        if (Native.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return false;
        return true;
    }

    public static bool Force;

    // Claude 弹出的菜单、下拉列表、对话框：出现时悬浮条让开
    static readonly Condition Popups = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
        new PropertyCondition(AutomationElement.LocalizedControlTypeProperty, "dialog"),
        new PropertyCondition(AutomationElement.LocalizedControlTypeProperty, "对话框"));
    static readonly Condition PromptCond = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.NameProperty, "Prompt"));
    static readonly Condition BandCond = new PropertyCondition(AutomationElement.NameProperty, "5小时");
    static readonly Condition ButtonCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

    static Rectangle R(AutomationElement e)
    {
        var b = e.Current.BoundingRectangle;
        if (b.IsEmpty) return Rectangle.Empty;
        return new Rectangle((int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Width), (int)Math.Round(b.Height));
    }

    // 输入框下方的结构（Claude 桌面端 Code 页）：
    //   停靠区 approval-dock ── [周额度提醒等提示条] [插件额度条] [输入框外框 bg-surface-3] [模型/用量按钮行]
    // 悬浮条放在停靠区里最上面那一块的上方：输入框变高、带图片、有提示条都不会被挡住
    public static Layout Scan(IntPtr hwnd)
    {
        var lay = new Layout { Window = hwnd };
        Native.GetWindowRect(hwnd, out lay.WindowRect);
        float s = Native.GetDpiForWindow(hwnd) / 96f;
        if (s <= 0) s = 1;
        lay.Scale = s;

        var root = AutomationElement.FromHandle(hwnd);
        AutomationElement prompt = null;
        Rectangle pr = Rectangle.Empty;
        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, PromptCond))
        {
            var r = R(e);
            if (r.Width > 0 && r.Height > 0 && (prompt == null || r.Y > pr.Y)) { prompt = e; pr = r; }
        }
        if (prompt == null) { lay.Why = "没找到输入框"; return lay; }

        var walker = TreeWalker.RawViewWalker;
        AutomationElement composer = null, dock = null;
        var up = prompt;
        for (int i = 0; i < 14 && up != null; i++)
        {
            up = walker.GetParent(up);
            if (up == null) break;
            string cls = up.Current.ClassName ?? "";
            if (composer == null && cls.StartsWith("bg-surface-3")) composer = up;
            if (cls.Contains("approval-dock")) { dock = up; break; }
        }
        if (composer == null || dock == null) { lay.Why = "没找到输入框外框"; return lay; }
        var cr = R(composer);

        if (!Force && dock.FindFirst(TreeScope.Descendants, BandCond) != null) { lay.Why = "插件额度条已显示"; return lay; }

        var popup = root.FindFirst(TreeScope.Descendants, Popups);
        if (popup != null)
        {
            string what = "";
            try { what = popup.Current.LocalizedControlType + " " + popup.Current.Name; } catch { }
            lay.Why = "弹出层 " + what;
            return lay;
        }

        // 输入框里以 / 或 @ 开头时会弹出命令 / 文件菜单，悬浮条让开
        try
        {
            object vp;
            if (prompt.TryGetCurrentPattern(ValuePattern.Pattern, out vp))
            {
                string v = (((ValuePattern)vp).Current.Value ?? "").TrimStart();
                if (v.StartsWith("/") || v.StartsWith("@")) { lay.Why = "命令菜单"; return lay; }
            }
        }
        catch { }

        // 停靠区里最上面一块（提示条 / 审批卡片 / 输入框外框）的顶边
        int top = cr.Top;
        for (var c = walker.GetFirstChild(dock); c != null; c = walker.GetNextSibling(c))
        {
            Rectangle r;
            try { r = R(c); } catch { continue; }
            if (r.Height < 8 * s || r.Width < cr.Width * 0.5 || r.Top >= cr.Top) continue;
            if (r.Top < top) top = r.Top;
        }

        // Claude 自己的用量按钮（模型名右边的小圆环）带有最新的周额度
        try
        {
            foreach (AutomationElement b in dock.FindAll(TreeScope.Descendants, ButtonCond))
            {
                string n = b.Current.Name ?? "";
                if (!n.StartsWith("Usage:")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(n, @"Weekly[^:]*:\s*([0-9.]+)%");
                double v;
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                    lay.WeeklyFromApp = v;
                break;
            }
        }
        catch { }

        lay.Left = cr.Left;
        lay.Width = cr.Width;
        lay.Bottom = top - (int)Math.Round(7 * s);
        var wr = lay.WindowRect;
        if (lay.Width < 200 * s || lay.Left < wr.Left || lay.Left + lay.Width > wr.Right || lay.Bottom - 40 * s < wr.Top || lay.Bottom > wr.Bottom)
        {
            lay.Why = "位置不在窗口内";
            return lay;
        }
        lay.Show = true;
        lay.Why = "显示";
        return lay;
    }
}

internal sealed class Bar : Form
{
    readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 50 };
    readonly AutoResetEvent _wake = new AutoResetEvent(true);
    readonly bool _debug;
    IntPtr _claude, _owner;
    int _lastFind;
    Layout _layout;
    Usage _usage;
    DateTime _usageStamp = DateTime.MinValue;
    int _lastMinute = -1;
    bool _visible;
    Rectangle _shownAt;
    int _rows = 1;
    string _rowsKey;
    Size _drawnSize;
    float _drawnScale;
    string _drawnKey;
    volatile IntPtr _scanTarget;

    public Bar(bool debug)
    {
        _debug = debug;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "UsageBarOverlay";
        _tick.Tick += (o, e) => Tick();
        var t = new Thread(ScanLoop) { IsBackground = true, Name = "uia" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void SetVisibleCore(bool value)
    {
        // 窗口句柄要有，但由我们自己决定何时显示
        if (!IsHandleCreated) { CreateHandle(); _tick.Start(); }
        base.SetVisibleCore(false);
    }

    void Log(string s)
    {
        if (!_debug) return;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "UsageBarOverlay.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n"); } catch { }
    }

    void ScanLoop()
    {
        while (true)
        {
            IntPtr h = _scanTarget;
            // Claude 在前台时 0.7 秒量一次，在后台时 2 秒一次
            _wake.WaitOne(h != IntPtr.Zero && Native.GetForegroundWindow() == h ? 450 : 2000);
            h = _scanTarget;
            if (h == IntPtr.Zero || !Finder.Usable(h)) continue;
            Layout lay;
            try { lay = Finder.Scan(h); }
            catch (Exception ex) { lay = new Layout { Window = h, Why = "扫描出错 " + ex.GetType().Name }; }
            try { BeginInvoke(new Action(() => Apply(lay))); } catch { return; }
        }
    }

    void Apply(Layout lay)
    {
        if (lay.Window != _claude) return;
        if (_debug && (_layout == null || _layout.Why != lay.Why || _layout.Left != lay.Left || _layout.Bottom != lay.Bottom || _layout.Width != lay.Width))
            Log(lay.Why + " left=" + lay.Left + " bottom=" + lay.Bottom + " width=" + lay.Width + " weekly=" + lay.WeeklyFromApp
                + " win=" + lay.WindowRect.Left + "," + lay.WindowRect.Top + "," + lay.WindowRect.Right + "," + lay.WindowRect.Bottom + " s=" + lay.Scale);
        _layout = lay;
        Place();
    }

    void Tick()
    {
        int now = Environment.TickCount;
        if (now - _lastFind > 1000 && (!Native.IsWindow(_claude) || !Finder.Usable(_claude)))
        {
            _lastFind = now;
            var h = Finder.FindClaude();
            if (h != _claude) { _claude = h; _layout = null; _scanTarget = h; _wake.Set(); }
        }

        if (!Finder.Usable(_claude)) { Hide2(); return; }

        if (_owner != _claude)
        {
            Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, _claude);
            _owner = _claude;
        }

        // 数据文件：每 2 秒看一次有没有更新；每分钟重画一次（处理重置时间已过）
        if (now % 2000 < 60) RefreshUsage();
        if (DateTime.Now.Minute != _lastMinute) { _lastMinute = DateTime.Now.Minute; _drawnKey = null; }

        Place();
    }

    void RefreshUsage()
    {
        try
        {
            string f = UsageFile.Find();
            var t = f != null ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
            if (t != _usageStamp) { _usageStamp = t; _usage = UsageFile.Read(); _drawnKey = null; }
        }
        catch { }
    }

    void Place()
    {
        var lay = _layout;
        if (lay == null || !lay.Show || !Finder.Usable(_claude)) { Hide2(); return; }

        Native.RECT wr;
        Native.GetWindowRect(_claude, out wr);
        int dw = (wr.Right - wr.Left) - (lay.WindowRect.Right - lay.WindowRect.Left);
        int dh = (wr.Bottom - wr.Top) - (lay.WindowRect.Bottom - lay.WindowRect.Top);
        if (dw != 0 || dh != 0)
        {
            // 窗口大小变了，输入框会重排：先藏起来等下一次扫描
            Hide2();
            _wake.Set();
            return;
        }
        if (_usage == null && _usageStamp == DateTime.MinValue) RefreshUsage();
        var usage = Effective(lay);
        string key = lay.Width + "|" + lay.Scale + "|" + _usageStamp.Ticks + "|" + _lastMinute + "|" + lay.WeeklyFromApp;
        if (key != _rowsKey) { _rows = Painter.Rows(lay.Width, lay.Scale, usage); _rowsKey = key; }
        int h = Painter.Height(_rows, lay.Scale);
        var box = new Rectangle(lay.Left, lay.Bottom - h, lay.Width, h);
        box.Offset(wr.Left - lay.WindowRect.Left, wr.Top - lay.WindowRect.Top);
        if (box.Top < wr.Top) { Hide2(); return; }

        if (!_visible || box != _shownAt || key != _drawnKey)
        {
            if (key != _drawnKey || box.Size != _drawnSize || lay.Scale != _drawnScale)
                PaintAt(box, lay.Scale, usage);
            else
                MoveTo(box);
            _drawnKey = key; _drawnSize = box.Size; _drawnScale = lay.Scale;
            _shownAt = box;
        }
        if (!_visible)
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            _visible = true;
        }
        KeepJustAboveClaude();
    }

    // 只比 Claude 高一层：别的程序盖住 Claude 时，悬浮条也被一起盖住
    void KeepJustAboveClaude()
    {
        IntPtr above = Native.GetWindow(_claude, 3 /* GW_HWNDPREV */);
        while (above != IntPtr.Zero && above != Handle && !Native.IsWindowVisible(above))
            above = Native.GetWindow(above, 3);
        if (above == Handle) return;
        IntPtr after = above;
        if (above == IntPtr.Zero || (Native.GetWindowLong(above, -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) != 0)
            after = IntPtr.Zero; // HWND_TOP：Claude 已是最上面的普通窗口
        Native.SetWindowPos(Handle, after, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // 文件里的读数；Claude 自带用量按钮上的周额度更新时以它为准
    Usage Effective(Layout lay)
    {
        if (!lay.WeeklyFromApp.HasValue) return _usage;
        var u = new Usage { FiveHour = _usage != null ? _usage.FiveHour : null };
        var old = _usage != null ? _usage.SevenDay : null;
        u.SevenDay = new Limit { Percent = lay.WeeklyFromApp.Value, ResetsAt = old != null ? old.ResetsAt : null };
        if (u.SevenDay.ResetsAt.HasValue && u.SevenDay.ResetsAt.Value <= DateTime.Now) u.SevenDay.ResetsAt = null;
        return u;
    }

    void PaintAt(Rectangle box, float s, Usage usage)
    {
        using (var bmp = Painter.Draw(box.Size, s, usage, _rows))
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(mem, hb);
            var size = new Native.SIZE(box.Width, box.Height);
            var src = new Native.POINT(0, 0);
            var dst = new Native.POINT(box.Left, box.Top);
            var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
            Native.SelectObject(mem, old);
            Native.DeleteObject(hb);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    void MoveTo(Rectangle box)
    {
        var dst = new Native.POINT(box.Left, box.Top);
        var size = new Native.SIZE(box.Width, box.Height);
        var src = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, IntPtr.Zero, ref src, 0, ref blend, Native.ULW_ALPHA);
    }

    void Hide2()
    {
        if (!_visible) return;
        Native.ShowWindow(Handle, Native.SW_HIDE);
        _visible = false;
    }
}

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool aware = Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); // 每显示器 DPI 感知 v2：坐标一律物理像素
        if (Array.IndexOf(args, "--debug") >= 0)
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "UsageBarOverlay.log"), "dpi-aware=" + aware + " err=" + Marshal.GetLastWin32Error() + Environment.NewLine);

        // --snapshot <png> [scale] [width] [5小时% 本周%]：把当前数据（或给定的示例百分比）画成图片，用于核对样式和做截图
        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float s = args.Length >= 3 ? float.Parse(args[2], inv) : 1f;
            int w = args.Length >= 4 ? int.Parse(args[3]) : (int)(769 * s);
            var u = UsageFile.Read();
            if (args.Length >= 6)
            {
                var now = DateTime.Now;
                var monday = now.Date.AddDays(((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7).AddHours(11);
                if (monday <= now) monday = monday.AddDays(7);
                u = new Usage
                {
                    FiveHour = new Limit { Percent = double.Parse(args[4], inv), ResetsAt = now.Date.AddHours(now.Hour + 3).AddMinutes(10) },
                    SevenDay = new Limit { Percent = double.Parse(args[5], inv), ResetsAt = monday },
                };
            }
            int rows = Painter.Rows(w, s, u);
            using (var bmp = Painter.Draw(new Size(w, Painter.Height(rows, s)), s, u, rows))
            using (var canvas = new Bitmap(bmp.Width + 30, bmp.Height + 20))
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.FromArgb(0x19, 0x19, 0x19));
                g.DrawImage(bmp, 15, 10);
                canvas.Save(args[1], ImageFormat.Png);
            }
            return;
        }

        bool created;
        using (var mutex = new Mutex(true, "Local\\ClaudeUsageOverlay", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Finder.Force = Array.IndexOf(args, "--force") >= 0;
            Application.Run(new Bar(Array.IndexOf(args, "--debug") >= 0));
        }
    }
}

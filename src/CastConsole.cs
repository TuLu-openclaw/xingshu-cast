// 星枢投屏 · 桌面客户端（C# WinForms 单文件）
// 设计语言对齐星枢破甲工具的华为皮肤：白底、纯黑主按钮、4px 直角、1px #dedede 细线、大字号设备地址。
//
// 相对旧版 PowerShell 界面的关键修复（多开黑屏 / 窗口无效）：
//   1. 不再用 -WindowStyle Hidden 启动 scrcpy —— 那会把 scrcpy 自己的窗口一并隐藏，表现为"黑屏/无窗口"；
//   2. 窗口就绪判定改成「进程存活 + MainWindowHandle != 0 + 标题匹配」，并延长到 15s；
//   3. 逐窗启动、失败自动降级重试一次（码率/帧率/分辨率都降一档，中端机四路编码吃不消会直接黑）；
//   4. 只杀本程序启动的 scrcpy，「关闭窗口」按钮才做全清，避免多开时互相杀；
//   5. 多开位置按工作区物理像素均分，进程声明 DPI 感知，避免 125%/150% 缩放下窗口跑到屏幕外。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XingshuCast
{
    internal static class Palette
    {
        public static readonly Color Bg = Color.FromArgb(0xEF, 0xF1, 0xF4);   // 画布浅灰，卡片才是白，层次才分得开
        public static readonly Color Surface = Color.FromArgb(0xF1, 0xF3, 0xF5);
        public static readonly Color CardBg = Color.White;
        public static readonly Color Line = Color.FromArgb(0xDE, 0xDE, 0xDE);
        public static readonly Color Text = Color.Black;
        public static readonly Color Muted = Color.FromArgb(0x5C, 0x5C, 0x5C);
        public static readonly Color Dim = Color.FromArgb(0x9A, 0x9A, 0x9A);
        public static readonly Color Ok = Color.FromArgb(0x12, 0x70, 0x3F);
        public static readonly Color Err = Color.FromArgb(0xC0, 0x2B, 0x2B);
        public static readonly Color Mono = Color.FromArgb(0x47, 0x55, 0x69);
    }

    internal static class Fonts
    {
        public static Font Hero, Title, H2, Body, Small, Btn, Mono;

        public static void Load(string root)
        {
            string family = "Microsoft YaHei UI";
            try
            {
                var pfc = new PrivateFontCollection();
                var dirs = new[] { Path.Combine(root, "fonts"), root };
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "HarmonyOS_Sans_SC_*.ttf"))
                        pfc.AddFontFile(f);
                }
                if (pfc.Families.Length > 0) family = pfc.Families[0].Name;
                _keep = pfc; // 不能丢引用，否则字体回退
            }
            catch { }

            Hero = new Font(family, 20f, FontStyle.Bold, GraphicsUnit.Pixel);
            Title = new Font(family, 16f, FontStyle.Bold, GraphicsUnit.Pixel);
            H2 = new Font(family, 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            Body = new Font(family, 13f, FontStyle.Regular, GraphicsUnit.Pixel);
            Small = new Font(family, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            Btn = new Font(family, 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            Mono = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static PrivateFontCollection _keep;
    }

    internal static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        public struct DEVMODE
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        /// <summary>真实物理分辨率（DPI 虚拟化环境下 Screen.WorkingArea 会虚高，不能用）。</summary>
        public static void PhysicalScreen(out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, -1, ref dm))
                {
                    w = dm.dmPelsWidth;
                    h = dm.dmPelsHeight;
                }
            }
            catch { }
            if (w <= 0 || h <= 0) { w = SystemInformation.PrimaryMonitorSize.Width; h = SystemInformation.PrimaryMonitorSize.Height; }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    }

    /// <summary>圆角边框面板：1px 细线 + 4/8px 圆角，和界面皮肤一致。</summary>
    internal sealed class Card : Panel
    {
        public int Radius = 8;
        public Card()
        {
            DoubleBuffered = true;
            BackColor = Palette.CardBg;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Palette.Line, 1))
            using (var path = Round(Radius, Width - 1, Height - 1))
                e.Graphics.DrawPath(pen, path);
        }
        internal static GraphicsPath Round(int r, int w, int h)
        {
            var p = new GraphicsPath();
            if (r <= 0 || w <= 0 || h <= 0) { p.AddRectangle(new Rectangle(0, 0, Math.Max(1, w), Math.Max(1, h))); return p; }
            int d = Math.Min(r * 2, Math.Min(w, h));
            p.AddArc(0, 0, d, d, 180, 90);
            p.AddArc(w - d, 0, d, d, 270, 90);
            p.AddArc(w - d, h - d, d, d, 0, 90);
            p.AddArc(0, h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>统一风格的按钮：黑实底 / 白底细线，4px 直角。</summary>
    internal sealed class HButton : Button
    {
        public bool Primary;
        public HButton(string text, bool primary, int height = 44)
        {
            Primary = primary;
            Text = text;
            Font = Fonts.Btn;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = primary ? 0 : 1;
            FlatAppearance.BorderColor = Palette.Line;
            BackColor = primary ? Palette.Text : Palette.CardBg;
            ForeColor = primary ? Color.White : Palette.Text;
            Height = height;
            Cursor = Cursors.Default;
            TabStop = false;
            SetStyle(ControlStyles.ResizeRedraw, true);
            MouseEnter += (s, e) => { if (Enabled) BackColor = primary ? Color.FromArgb(0x1C, 0x1C, 0x1E) : Palette.Surface; };
            MouseLeave += (s, e) => { if (Enabled) BackColor = primary ? Palette.Text : Palette.CardBg; };
            Resize += (s, e) => Region = new Region(Card.Round(4, Width, Height));
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            BackColor = !Enabled ? Color.FromArgb(0xA8, 0xA8, 0xA8)
                : (Primary ? Palette.Text : Palette.CardBg);
            ForeColor = Primary ? Color.White : Palette.Text;
        }
    }

    internal sealed class MainForm : Form
    {
        // ---------- 路径 ----------
        /// <summary>
        /// 工具链根目录：exe 和 platform-tools / scrcpy 放一起时就是 exe 目录；
        /// exe 被单独拷到桌面也能往回找到（否则就报「adb 调用失败：系统找不到指定的文件」）。
        /// </summary>
        private static readonly string Root = ResolveToolsRoot();
        private static readonly string Adb = Path.Combine(Root, "platform-tools", "adb.exe");
        private static readonly string Scrcpy = ResolveScrcpy();
        /// <summary>配置与日志放用户目录：程序放 Program Files 时也有写权限。</summary>
        private static readonly string DataDir = EnsureDataDir();
        private static readonly string CfgPath = Path.Combine(DataDir, "launcher.json");
        private static readonly string LogPath = Path.Combine(DataDir, "launcher.log");

        private static string EnsureDataDir()
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XingshuCast");
            try { Directory.CreateDirectory(d); } catch { return AppDomain.CurrentDomain.BaseDirectory; }
            return d;
        }

        private static string ResolveToolsRoot()
        {
            var bases = new List<string>();
            var exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            bases.Add(exeDir);
            try { var p1 = Directory.GetParent(exeDir); if (p1 != null) bases.Add(p1.FullName); } catch { }
            try { var p2 = Directory.GetParent(exeDir); if (p2 != null && p2.Parent != null) bases.Add(p2.Parent.FullName); } catch { }
            bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XingshuCast"));
            bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "deepseek-harness", "default-workspace", "android-tools"));

            foreach (var b in bases)
            {
                try
                {
                    if (!Directory.Exists(b)) continue;
                    if (File.Exists(Path.Combine(b, "platform-tools", "adb.exe"))) return b;
                    // 也可能 adb 在子目录里
                    foreach (var d in Directory.GetDirectories(b, "android-tools"))
                        if (File.Exists(Path.Combine(d, "platform-tools", "adb.exe"))) return d;
                }
                catch { }
            }
            return exeDir;
        }

        private static string ResolveScrcpy()
        {
            var direct = Path.Combine(Root, "scrcpy.exe");
            if (File.Exists(direct)) return direct;
            try
            {
                foreach (var d in Directory.GetDirectories(Root, "scrcpy*"))
                {
                    var exe = Path.Combine(d, "scrcpy.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
            return Path.Combine(Root, "scrcpy.exe");
        }

        private const int Port = 5555;

        private sealed class DevInfo
        {
            public string Serial = "";
            public string Kind = "USB";     // USB / 无线
            public string Model = "";
            public bool Online;
            public override string ToString()
            {
                var m = string.IsNullOrEmpty(Model) ? Serial : Model;
                return Kind + " · " + m + "  (" + Serial + ")";
            }
        }

        // ---------- 状态 ----------
        private string _curIp = "";
        private string _serial = "";                                  // 当前选中的 adb 目标（USB 序列号或 IP:端口）
        private readonly List<DevInfo> _devices = new List<DevInfo>();
        private readonly Dictionary<string, string> _apps = new Dictionary<string, string>();
        private readonly List<Process> _spawned = new List<Process>();
        private string _lastIp = "";
        private readonly List<string> _extras = new List<string>();
        public string ForcePkg = "";   // 自检用：强制指定包名
        private bool _busy;
        private bool _restrictedPrompted;
        public static bool NoDialogs;   // 自检时不要弹窗

        // ---------- 控件 ----------
        private Label _dot, _stateText, _addr, _devLine, _foot;
        private HButton _btnMain, _btnConnect, _btnScan, _btnClose, _btnMirror, _btnIndep;
        private TextBox _txtIp, _txtLog;
        private ComboBox _cmbDev;
        private HButton _btnAll, _btnUsbWifi;
        private ComboBox _cmbApp;
        private NumericUpDown _numCount;
        private Card _deviceCard, _multiCard, _logCard;

        public MainForm()
        {
            LoadConfig();
            BuildUi();
            Shown += (s, e) =>
            {
                try
                {
                    int pw, ph;
                    Native.PhysicalScreen(out pw, out ph);
                    int maxW = Math.Max(500, pw - 80);
                    int maxH = Math.Max(560, ph - 100);   // 留出任务栏与标题
                    if (Width > maxW || Height > maxH)
                    {
                        Size = new Size(Math.Min(Width, maxW), Math.Min(Height, maxH));
                        Region = new Region(Card.Round(8, Width, Height));
                    }
                    Log("屏幕 " + pw + "x" + ph + " · 缩放 " + (DeviceDpi * 100 / 96) + "% · 窗口 " + Width + "x" + Height);
                }
                catch { }
                // DPI 环境下 WinForms 会把窗体按系统缩放缩小，但子控件不跟着放大 → 右侧被裁掉。
                // 这里按实际 ClientSize 把整个控件树等比放大回设计尺寸（560×830），窗口再补差量。
                if (ClientSize.Width != 560 || ClientSize.Height != 830)
                {
                    float sx = 560f / Math.Max(1, ClientSize.Width);
                    float sy = 830f / Math.Max(1, ClientSize.Height);
                    SuspendLayout();
                    Scale(new SizeF(sx, sy));
                    ResumeLayout(true);
                    Size = new Size(560 + (Width - ClientSize.Width), 830 + (Height - ClientSize.Height));
                }
                Region = new Region(Card.Round(8, Width, Height));
                InitAsync();
            };

            AllowDrop = true;
            DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += async (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                foreach (var f in files)
                    await InstallApkAsync(f);
            };

            var timer = new System.Windows.Forms.Timer { Interval = 3000 };
            timer.Tick += (s, e) => { if (!_busy) RefreshState(); };
            timer.Start();
        }

        // ================= 界面 =================
        private void BuildUi()
        {
            Text = "星枢投屏";
            AutoScaleMode = AutoScaleMode.Dpi;    // 跟随系统缩放：125%/150% 下整体等比放大
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Palette.Bg;
            Size = new Size(560, 830);            // 设计尺寸（96 DPI 基准）
            MinimumSize = new Size(520, 560);
            Region = new Region(Card.Round(8, Width, Height));
            Resize += (s, e) => Region = new Region(Card.Round(8, Width, Height));
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(0, wa.Right - Width - 60), Math.Max(0, wa.Top + 40));

            // 顶栏
            var bar = new Panel { BackColor = Palette.CardBg, Bounds = new Rectangle(0, 0, Width, 64) };
            bar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Palette.Line, 1))
                    e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };
            bar.MouseDown += DragWindow;
            Controls.Add(bar);

            var mark = new Label
            {
                Text = "枢", Font = Fonts.H2, ForeColor = Color.White, BackColor = Palette.Text,
                TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(24, 19, 26, 26)
            };
            bar.Controls.Add(mark);

            var title = new Label { Text = "星枢投屏", Font = Fonts.Title, ForeColor = Palette.Text, AutoSize = true, Location = new Point(60, 22) };
            title.MouseDown += DragWindow;
            bar.Controls.Add(title);

            var sub = new Label { Text = "无线投屏控制台", Font = Fonts.Small, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(134, 25) };
            sub.MouseDown += DragWindow;
            bar.Controls.Add(sub);

            var btnMin = new Label { Text = "—", Font = Fonts.Body, ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(472, 17, 30, 30) };
            btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
            bar.Controls.Add(btnMin);

            var btnQuit = new Label { Text = "✕", Font = Fonts.Body, ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(506, 17, 30, 30) };
            btnQuit.Click += (s, e) => Close();
            bar.Controls.Add(btnQuit);

            // 主体
            var body = new Panel { Bounds = new Rectangle(0, 64, Width, Height - 64), BackColor = Palette.Bg, AutoScroll = true };
            Controls.Add(body);
            body.Resize += (s, e) => { };

            const int pad = 24;
            int cw = Width - pad * 2;

            // —— 设备卡 ——
            _deviceCard = new Card { Bounds = new Rectangle(pad, 20, cw, 266) };
            body.Controls.Add(_deviceCard);

            _dot = new Label { Text = "●", Font = new Font("Segoe UI", 9f), ForeColor = Palette.Dim, AutoSize = true, Location = new Point(20, 18) };
            _deviceCard.Controls.Add(_dot);

            _stateText = new Label { Text = "未连接", Font = Fonts.Btn, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(38, 17) };
            _deviceCard.Controls.Add(_stateText);

            _addr = new Label
            {
                Text = "等待连接", Font = Fonts.Hero, ForeColor = Palette.Dim, AutoSize = true,
                Location = new Point(18, 44)
            };
            _deviceCard.Controls.Add(_addr);

            _devLine = new Label { Text = "未连接设备 · 无线调试", Font = Fonts.Small, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(20, 82) };
            _deviceCard.Controls.Add(_devLine);

            _deviceCard.Controls.Add(new Label { Text = "设备", Font = Fonts.Small, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(20, 108) });
            _cmbDev = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Fonts.Body,
                BackColor = Palette.CardBg, ForeColor = Palette.Text,
                Bounds = new Rectangle(58, 104, cw - 78, 26), DropDownWidth = 380
            };
            _cmbDev.SelectedIndexChanged += (s, e) =>
            {
                var d = _cmbDev.SelectedItem as DevInfo;
                if (d == null) return;
                _serial = d.Serial;
                if (d.Serial.Contains(":"))
                {
                    _curIp = d.Serial.Split(':')[0];
                    _addr.Text = d.Serial;
                    _addr.ForeColor = Palette.Text;
                }
                else
                {
                    _curIp = "";
                    _addr.Text = d.Serial;
                    _addr.ForeColor = Palette.Text;
                }
                _txtIp.Text = d.Serial;
                if (!string.IsNullOrEmpty(d.Model)) _devLine.Text = d.Model + " · " + (d.Kind == "USB" ? "USB 连接" : "无线连接");
                SetState("已连接", Palette.Ok);
            };
            _deviceCard.Controls.Add(_cmbDev);

            _btnMain = new HButton("连接并投屏", true, 48) { Bounds = new Rectangle(18, 142, cw - 36, 48) };
            _btnMain.Click += async (s, e) => await DoConnectAsync(true);
            _deviceCard.Controls.Add(_btnMain);

            int bw = (cw - 36 - 16) / 3;
            _btnConnect = new HButton("仅连接", false) { Bounds = new Rectangle(18, 212, bw, 44) };
            _btnConnect.Click += async (s, e) => await DoConnectAsync(false);
            _deviceCard.Controls.Add(_btnConnect);

            _btnScan = new HButton("刷新设备", false) { Bounds = new Rectangle(18 + bw + 8, 212, bw, 44) };
            _btnScan.Click += async (s, e) => await RescanAsync();
            _deviceCard.Controls.Add(_btnScan);

            _btnClose = new HButton("关闭窗口", false) { Bounds = new Rectangle(18 + (bw + 8) * 2, 212, bw, 44) };
            _btnClose.Click += (s, e) => CloseAllWindows();
            _deviceCard.Controls.Add(_btnClose);

            // —— 地址 + 多开 ——
            _multiCard = new Card { Bounds = new Rectangle(pad, 300, cw, 292) };
            body.Controls.Add(_multiCard);

            _multiCard.Controls.Add(new Label { Text = "设备地址", Font = Fonts.Btn, ForeColor = Palette.Text, AutoSize = true, Location = new Point(20, 18) });
            var ipHint = new Label { Text = "手机 IP:5555（手机 → 无线调试里看）", Font = Fonts.Small, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(cw - 92, 19) };
            _multiCard.Controls.Add(ipHint);

            _txtIp = new TextBox
            {
                Font = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel),
                BorderStyle = BorderStyle.FixedSingle, BackColor = Palette.Bg, ForeColor = Palette.Text,
                Bounds = new Rectangle(20, 44, cw - 40, 34),
                Text = string.IsNullOrEmpty(_lastIp) ? "" : _lastIp + ":" + Port
            };
            _multiCard.Controls.Add(_txtIp);
            var ipLine = new Panel { BackColor = Palette.Line, Bounds = new Rectangle(20, 78, cw - 40, 1) };
            _multiCard.Controls.Add(ipLine);

            _multiCard.Controls.Add(new Label { Text = "多窗口投屏", Font = Fonts.Btn, ForeColor = Palette.Text, AutoSize = true, Location = new Point(20, 96) });
            _multiCard.Controls.Add(new Label { Text = "自动唤醒 · 逐窗校验", Font = Fonts.Small, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(112, 97) });

            _cmbApp = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, Font = Fonts.Body,
                BackColor = Palette.Bg, ForeColor = Palette.Text,
                Bounds = new Rectangle(20, 122, cw - 40 - 108, 34)
            };
            _cmbApp.Text = "连接后读取应用";
            _cmbApp.DropDownWidth = 340;
            _multiCard.Controls.Add(_cmbApp);

            _numCount = new NumericUpDown
            {
                Minimum = 1, Maximum = 4, Value = 2, Font = Fonts.Body,
                BorderStyle = BorderStyle.FixedSingle, BackColor = Palette.Bg, ForeColor = Palette.Text,
                Bounds = new Rectangle(cw - 20 - 92, 122, 92, 34), TextAlign = HorizontalAlignment.Center
            };
            _multiCard.Controls.Add(_numCount);

            _btnUsbWifi = new HButton("USB 转无线", false) { Bounds = new Rectangle(20, 172, (cw - 48) / 2, 40) };
            _btnUsbWifi.Click += (s, e) => UsbToWifi();
            _multiCard.Controls.Add(_btnUsbWifi);

            _btnAll = new HButton("全部投屏", false) { Bounds = new Rectangle(20 + (cw - 48) / 2 + 8, 172, (cw - 48) / 2, 40) };
            _btnAll.Click += async (s, e) => await StartAllAsync();
            _multiCard.Controls.Add(_btnAll);

            _btnMirror = new HButton("主屏镜像", false) { Bounds = new Rectangle(20, 222, (cw - 48) / 2, 44) };
            _btnMirror.Click += async (s, e) => await StartMultiAsync(false);
            _multiCard.Controls.Add(_btnMirror);

            _btnIndep = new HButton("独立实例", true) { Bounds = new Rectangle(20 + (cw - 48) / 2 + 8, 222, (cw - 48) / 2, 44) };
            _btnIndep.Click += async (s, e) => await StartMultiAsync(true);
            _multiCard.Controls.Add(_btnIndep);

            _foot = new Label
            {
                Text = "主屏镜像 = 同一块手机屏复制到多个窗口    ·    独立实例 = 每个窗口各自一块虚拟屏",
                Font = Fonts.Small, ForeColor = Palette.Muted,
                Bounds = new Rectangle(22, 272, cw - 44, 18)
            };
            _multiCard.Controls.Add(_foot);

            // —— 日志 ——
            _logCard = new Card { Bounds = new Rectangle(pad, 606, cw, 158) };
            body.Controls.Add(_logCard);

            _logCard.Controls.Add(new Label { Text = "运行日志", Font = Fonts.Btn, ForeColor = Palette.Text, AutoSize = true, Location = new Point(20, 18) });
            _logCard.Controls.Add(new Label { Text = "最近 200 行", Font = Fonts.Small, ForeColor = Palette.Muted, AutoSize = true, Location = new Point(cw - 90, 19) });

            var logBg = new Panel { BackColor = Color.FromArgb(0x0B, 0x0B, 0x0C), Bounds = new Rectangle(16, 44, cw - 32, 100) };
            _logCard.Controls.Add(logBg);

            _txtLog = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                Font = Fonts.Mono, BackColor = Color.FromArgb(0x0B, 0x0B, 0x0C), ForeColor = Color.FromArgb(0xCF, 0xD2, 0xD6),
                Bounds = new Rectangle(10, 10, logBg.Width - 20, logBg.Height - 20)
            };
            logBg.Controls.Add(_txtLog);

            // 底部说明
            body.Controls.Add(new Label
            {
                Text = "星枢[未来科技] · 投屏控制台 1.0",
                Font = Fonts.Small, ForeColor = Palette.Dim, AutoSize = true, Location = new Point(pad + 2, 806 - 64)
            });
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        // ================= 配置 =================
        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(CfgPath)) return;
                var txt = File.ReadAllText(CfgPath, Encoding.UTF8);
                var m = Regex.Match(txt, "\"last\"\\s*:\\s*\"([^\"]*)\"");
                if (m.Success) _lastIp = m.Groups[1].Value;
                foreach (Match e in Regex.Matches(txt, "\"([0-9]{1,3}(?:\\.[0-9]{1,3}){3})\""))
                    if (!_extras.Contains(e.Groups[1].Value)) _extras.Add(e.Groups[1].Value);
            }
            catch { }
        }
        private void SaveConfig()
        {
            try
            {
                var sb = new StringBuilder("{\"last\":\"" + _lastIp + "\",\"extras\":[");
                sb.Append(string.Join(",", _extras.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Select(x => "\"" + x + "\"")));
                sb.Append("]}");
                File.WriteAllText(CfgPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
        private void SaveIp(string ip)
        {
            _lastIp = ip;
            if (!_extras.Contains(ip)) _extras.Insert(0, ip);
            SaveConfig();
        }

        // ================= 日志 / 状态 =================
        private void Log(string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
            if (_txtLog.InvokeRequired)
            {
                _txtLog.BeginInvoke(new Action(() => AppendLog(line)));
                return;
            }
            AppendLog(line);
        }
        private void AppendLog(string line)
        {
            _txtLog.AppendText(line + Environment.NewLine);
            var lines = _txtLog.Lines;
            if (lines.Length > 200)
                _txtLog.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - 200));
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        private readonly Dictionary<string, DateTime> _logOnce = new Dictionary<string, DateTime>();
        /// <summary>同一条消息 5 秒内只写一次，避免刷屏。</summary>
        private void LogOnce(string key, string msg)
        {
            lock (_logOnce)
            {
                DateTime last;
                if (_logOnce.TryGetValue(key, out last) && (DateTime.Now - last).TotalSeconds < 5) return;
                _logOnce[key] = DateTime.Now;
            }
            Log(msg);
        }

        private void SetState(string text, Color color)
        {
            if (_stateText.InvokeRequired) { _stateText.BeginInvoke(new Action(() => SetState(text, color))); return; }
            _stateText.Text = text;
            _stateText.ForeColor = color == Palette.Dim ? Palette.Muted : color;
            _dot.ForeColor = color;
        }
        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnMain.Enabled = !busy;
            _btnConnect.Enabled = !busy;
            _btnScan.Enabled = !busy;
            _btnMirror.Enabled = !busy;
            _btnIndep.Enabled = !busy;
        }

        // ================= adb =================
        /// <summary>当前目标：USB = 序列号，无线 = IP:5555。</summary>
        private string Target()
        {
            if (!string.IsNullOrEmpty(_serial)) return _serial;
            if (!string.IsNullOrEmpty(_curIp)) return _curIp + ":" + Port;
            return "";
        }

        private string AdbOut(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(Adb)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Root
                };
                var sb = new StringBuilder();
                foreach (var a in args)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    if (a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0)
                        sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                    else
                        sb.Append(a);
                }
                psi.Arguments = sb.ToString();
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return (o + e).Trim();
                }
            }
            catch (Exception ex) { LogOnce("adb:" + ex.Message, "adb 调用失败：" + ex.Message); return ""; }
        }

        private bool TestConnected(string ip)
        {
            var d = AdbOut("devices");
            return Regex.IsMatch(d, Regex.Escape(ip + ":" + Port) + "\\s+device", RegexOptions.Multiline);
        }

        private string GetSerial(string ip)
        {
            return AdbOut("-s", ip + ":" + Port, "shell", "getprop", "ro.serialno").Trim();
        }

        private bool ConnectTo(string ip)
        {
            AdbOut("connect", ip + ":" + Port);
            Thread.Sleep(450);
            if (!TestConnected(ip)) return false;
            var sn = GetSerial(ip);
            if (!string.IsNullOrEmpty(sn) && sn.Length < 4)
            {
                Log(ip + " 上有其他设备，跳过");
                AdbOut("disconnect", ip + ":" + Port);
                return false;
            }
            return true;
        }

        // ================= 发现设备 =================
        private async Task<string> DiscoverAsync()
        {
            if (!File.Exists(Adb))
            {
                Log("找不到 adb.exe：请把 星枢投屏.exe 放回带 platform-tools 与 scrcpy 的目录（当前找的是 " + Root + "）");
                SetState("缺少 adb", Palette.Err);
                return null;
            }
            var cands = new List<string>();
            var manual = Regex.Replace(_txtIp.Text.Trim(), ":\\d+$", "").Trim();
            if (!string.IsNullOrWhiteSpace(manual)) cands.Add(manual);
            if (!string.IsNullOrEmpty(_lastIp)) cands.Add(_lastIp);
            cands.AddRange(_extras);
            cands = cands.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

            foreach (var ip in cands)
            {
                Log("尝试 " + ip);
                if (await Task.Run(() => ConnectTo(ip))) return ip;
            }

            Log("尝试 mDNS 发现…");
            var md = await Task.Run(() => AdbOut("mdns", "services"));
            foreach (Match m in Regex.Matches(md, "([0-9]{1,3}\\.){3}[0-9]{1,3}"))
            {
                var ip = m.Value;
                if (cands.Contains(ip)) continue;
                Log("mDNS 发现 " + ip);
                if (await Task.Run(() => ConnectTo(ip))) return ip;
            }

            var hosts = ArpHosts();
            if (hosts.Count > 0)
            {
                Log("扫描局域网活跃主机（" + hosts.Count + " 个）…");
                var open = await ScanPortsAsync(hosts, 350);
                foreach (var ip in open)
                {
                    if (cands.Contains(ip)) continue;
                    if (await Task.Run(() => ConnectTo(ip))) return ip;
                }
            }

            var prefixes = LocalPrefixes();
            foreach (var p in prefixes)
            {
                Log("扫描网段 " + p + ".0/24 的 " + Port + "…");
                var list = Enumerable.Range(1, 254).Select(i => p + "." + i).ToList();
                var open = await ScanPortsAsync(list, 250);
                foreach (var ip in open)
                {
                    if (cands.Contains(ip)) continue;
                    if (await Task.Run(() => ConnectTo(ip))) return ip;
                }
            }
            return null;
        }

        private static async Task<List<string>> ScanPortsAsync(List<string> hosts, int timeoutMs)
        {
            var found = new HashSet<string>();
            var gate = new SemaphoreSlim(64);
            var tasks = hosts.Select(async h =>
            {
                await gate.WaitAsync();
                try
                {
                    using (var c = new TcpClient())
                    {
                        var t = c.ConnectAsync(h, Port);
                        if (await Task.WhenAny(t, Task.Delay(timeoutMs)) == t && c.Connected)
                            lock (found) found.Add(h);
                    }
                }
                catch { }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
            return found.ToList();
        }

        private static List<string> ArpHosts()
        {
            var res = new List<string>();
            try
            {
                var psi = new ProcessStartInfo("arp", "-a") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    foreach (Match m in Regex.Matches(outp, "([0-9]{1,3}\\.){3}[0-9]{1,3}"))
                        if (!Regex.IsMatch(m.Value, "^(127|169\\.254|255|224)\\.")) res.Add(m.Value);
                }
            }
            catch { }
            return res.Distinct().ToList();
        }

        private static List<string> LocalPrefixes()
        {
            var res = new List<string>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        var ip = ua.Address.ToString();
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        if (Regex.IsMatch(ip, "^(127|169\\.254)\\.")) continue;
                        var parts = ip.Split('.');
                        res.Add(parts[0] + "." + parts[1] + "." + parts[2]);
                    }
                }
            }
            catch { }
            return res.Distinct().ToList();
        }

        // ================= 流程 =================
        private async void InitAsync()
        {
            Log("工作目录：" + Root);
            if (!Environment.Is64BitOperatingSystem)
            {
                Log("【注意】这台电脑是 32 位 Windows：投屏内核 scrcpy 只有 64 位版本，无法投屏。");
                SetState("需要 64 位系统", Palette.Err);
            }
            if (!File.Exists(Adb))
            {
                Log("【缺少 adb】按这个位置找过：" + Root);
                Log("请把 星枢投屏.exe 放回 android-tools 目录（与 platform-tools、scrcpy-win64-* 同级）");
                SetState("缺少 adb.exe", Palette.Err);
                SetBusy(false);
                return;
            }
            if (!File.Exists(Scrcpy)) Log("警告：找不到 scrcpy.exe（" + Scrcpy + "）");
            await Task.Run(() => AdbOut("start-server"));
            // adb 起来后设备列表不一定立刻就有，轮询几次再判定
            for (int i = 0; i < 4; i++)
            {
                RefreshState();
                if (!string.IsNullOrEmpty(_curIp)) break;
                await Task.Delay(1200);
            }
            // 已在线的设备：直接读应用列表（编码已修正，缓存也按 UTF-8 写）
            RefreshDeviceList();
            if (!string.IsNullOrEmpty(Target())) await LoadAppsAsync();
            else Log("未连接设备：应用列表连接后再读");
            Log("就绪");
        }

        private List<DevInfo> ScanDevices()
        {
            var list = new List<DevInfo>();
            var outp = AdbOut("devices", "-l");
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith("*")) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var d = new DevInfo { Serial = parts[0], Online = parts[1] == "device" };
                if (!d.Online) continue;                       // offline / unauthorized 不列出来
                if (d.Serial.StartsWith("127.") || d.Serial.StartsWith("198.18.")) continue;   // 回环 / VPN 虚拟网段
                d.Kind = d.Serial.Contains(":") ? "无线" : "USB";
                foreach (var p2 in parts)
                {
                    if (p2.StartsWith("model:")) d.Model = p2.Substring(6).Replace('_', ' ');
                }
                list.Add(d);
            }
            return list;
        }

        public void RefreshDeviceList()
        {
            var devs = ScanDevices();
            var selected = _serial;
            _devices.Clear();
            _devices.AddRange(devs);

            if (_cmbDev.InvokeRequired)
            {
                _cmbDev.BeginInvoke(new Action(() => RefreshDeviceList()));
                return;
            }
            _cmbDev.BeginUpdate();
            _cmbDev.Items.Clear();
            foreach (var d in devs) _cmbDev.Items.Add(d);
            _cmbDev.EndUpdate();

            if (devs.Count == 0)
            {
                _serial = "";
                SetState("未连接", Palette.Dim);
                _addr.Text = "等待连接";
                _addr.ForeColor = Palette.Dim;
                return;
            }
            int idx = devs.FindIndex(x => x.Serial == selected);
            _cmbDev.SelectedIndex = idx >= 0 ? idx : 0;
        }

        private void RefreshState()
        {
            RefreshDeviceList();
        }

        private void UpdateUi(string ip)
        {
            if (_addr.InvokeRequired) { _addr.BeginInvoke(new Action(() => UpdateUi(ip))); return; }
            if (!string.IsNullOrEmpty(ip))
            {
                _curIp = ip;
                _addr.Text = ip + ":" + Port;
                _addr.ForeColor = Palette.Text;
                _txtIp.Text = ip + ":" + Port;
                SetState("已连接", Palette.Ok);
                UpdateDeviceNameAsync(ip);
            }
            else
            {
                _curIp = "";
                _addr.Text = "等待连接";
                _addr.ForeColor = Palette.Dim;
                SetState("未连接", Palette.Dim);
            }
        }

        /// <summary>设备型号从手机读取，别在界面上写死某台机器。</summary>
        private async Task UpdateDeviceNameAsync(string ip)
        {
            try
            {
                var model = await Task.Run(() => AdbOut("-s", Target(), "shell", "getprop", "ro.product.model").Trim());
                if (!string.IsNullOrEmpty(model))
                {
                    if (_devLine.InvokeRequired) { _devLine.BeginInvoke(new Action(() => _devLine.Text = model + " · 无线调试")); }
                    else _devLine.Text = model + " · 无线调试";
                }
            }
            catch { }
        }

        private async Task DoConnectAsync(bool mirror)
        {
            if (_busy) return;
            if (!string.IsNullOrEmpty(Target()) && mirror)
            {
                // 已经有选中的设备：直接投屏，不再扫网段
                SetState("已连接", Palette.Ok);
                StartMirror(_curIp);
                return;
            }
            SetBusy(true);
            SetState("正在查找设备…", Palette.Text);
            try
            {
                var ip = await DiscoverAsync();
                if (!string.IsNullOrEmpty(ip))
                {
                    SaveIp(ip);
                    UpdateUi(ip);
                    Log("连接成功：" + ip + ":" + Port);
                    await LoadAppsAsync();
                    if (mirror) StartMirror(ip);
                }
                else
                {
                    UpdateUi(null);
                    SetState("未找到设备", Palette.Err);
                    Log("没找到设备：确认手机已开无线调试、和电脑在同一网络");
                }
            }
            catch (Exception ex) { SetState("出错", Palette.Err); Log("出错：" + ex.Message); }
            finally { SetBusy(false); }
        }

        private async Task RescanAsync()
        {
            Log("—— 重新扫描 ——");
            await Task.Run(() => AdbOut("disconnect"));
            await DoConnectAsync(false);
        }

        private async Task LoadAppsAsync()
        {
            if (string.IsNullOrEmpty(_curIp)) return;
            Log("读取应用列表…");
            var tmp = Path.Combine(Root, "apps.txt");
            bool need = true;
            try
            {
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 500 &&
                    (DateTime.Now - File.GetLastWriteTime(tmp)).TotalHours < 12) need = false;
            }
            catch { }

            if (need)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo(Scrcpy)
                        {
                            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root,
                            RedirectStandardOutput = true, RedirectStandardError = true,
                            StandardOutputEncoding = new UTF8Encoding(false),   // scrcpy 输出 UTF-8，按 GBK 解就会变乱码
                            StandardErrorEncoding = new UTF8Encoding(false)
                        };
                        psi.Arguments = "-s \"" + _curIp + ":" + Port + "\" --list-apps";
                        using (var p = Process.Start(psi))
                        {
                            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                            p.WaitForExit(20000);
                            File.WriteAllText(tmp, outp, Encoding.UTF8);
                        }
                    }
                    catch (Exception ex) { Log("读取应用失败：" + ex.Message); }
                });
            }

            _apps.Clear();
            var items = new List<string>();
            try
            {
                if (File.Exists(tmp))
                {
                    string rawTxt = File.ReadAllText(tmp, Encoding.UTF8);
                    // 旧缓存可能是 GBK 写的 / 被按 GBK 解过一遍：出现替换符或典型乱码就连同 GBK 版本比较
                    if (rawTxt.IndexOf('\uFFFD') >= 0 || LooksMojibake(rawTxt))
                    {
                        string gbk = File.ReadAllText(tmp, Encoding.Default);
                        if (LooksMojibake(gbk) == false) rawTxt = gbk;
                    }
                    foreach (var line in rawTxt.Replace("\r", "").Split('\n'))
                    {
                        var m = Regex.Match(line, "^\\s*[-*]\\s+(.+?)\\s{2,}([A-Za-z][A-Za-z0-9_.]*)\\s*$");
                        if (!m.Success) continue;
                        var name = m.Groups[1].Value.Trim();
                        var pkg = m.Groups[2].Value.Trim();
                        if (_apps.ContainsKey(name)) continue;
                        _apps[name] = pkg;
                        items.Add(name);
                    }
                }
            }
            catch { }

            if (_cmbApp.InvokeRequired) { _cmbApp.BeginInvoke(new Action(() => FillApps(items))); return; }
            FillApps(items);
        }

        /// <summary>典型 UTF-8→GBK 乱码字符，命中多就判为乱码。</summary>
        private static bool LooksMojibake(string s)
        {
            const string bad = "鍢锛撴缁涓嶅煄潗鎴戜滑鏄粈涔堢殑浜嗗ソ閿欒";
            int hit = 0;
            foreach (var ch in s) if (bad.IndexOf(ch) >= 0) hit++;
            return hit >= 3;
        }

        private void FillApps(List<string> items)
        {
            _cmbApp.Items.Clear();
            if (items.Count == 0) { _cmbApp.Text = "未读到应用"; Log("未读到应用"); return; }
            _cmbApp.Items.AddRange(items.ToArray());
            _cmbApp.SelectedIndex = 0;
            Log("已读取 " + items.Count + " 个应用");
        }

        // ================= scrcpy =================
        private static RectangleF MultiLayout(int count, out int y)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = 12;
            int top = 80;
            int cw = (wa.Width - 60 - (count - 1) * gap) / count;
            int ch = (int)(cw * 1280.0 / 720.0);
            int maxH = wa.Height - top - 40;
            if (ch > maxH) { ch = maxH; cw = (int)(ch * 720.0 / 1280.0); }
            y = top;
            return new RectangleF(30, top, cw, ch);
        }

        /// <summary>启动一个 scrcpy；不给 -WindowStyle Hidden，隐藏会让 scrcpy 窗口也消失（黑屏错觉）。</summary>
        private Process LaunchScrcpy(string args)
        {
            var psi = new ProcessStartInfo(Scrcpy)
            {
                UseShellExecute = false,
                CreateNoWindow = true,           // 只隐藏控制台；scrcpy 自己的 SDL 窗口照常显示
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),   // scrcpy 输出 UTF-8；按 GBK 解会把中文路径搞成乱码
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            // 参数整体传，避免引号被再次解析
            psi.Arguments = args;
            var p = Process.Start(psi);
            try
            {
                p.OutputDataReceived += (s, e) => HandleScrcpyLine(e.Data);
                p.ErrorDataReceived += (s, e) => HandleScrcpyLine(e.Data);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch { }
            lock (_spawned) { _spawned.Add(p); }
            return p;
        }

        /// <summary>scrcpy 输出可能是 GBK 也可能是 UTF-8，逐行判断后修回可读中文。</summary>
        private static string FixLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            const string bad = "鍢锛撴缁涓嶅煄潗鎴戜滑鏄粈涔堢殑浜嗗ソ閿欒";
            int hit = 0;
            foreach (var ch in s) if (bad.IndexOf(ch) >= 0) hit++;
            if (hit < 2 && s.IndexOf('\uFFFD') < 0) return s;
            try
            {
                // 把被当成 GBK 读进来的 UTF-8 字节还原
                var bytes = Encoding.Default.GetBytes(s);
                var fixedText = Encoding.UTF8.GetString(bytes);
                const string bad2 = "鍢锛撴缁涓嶅煄潗鎴戜滑鏄粈涔堢殑浜嗗ソ閿欒";
                int hit2 = 0;
                foreach (var ch in fixedText) if (bad2.IndexOf(ch) >= 0) hit2++;
                return hit2 < hit ? fixedText : s;
            }
            catch { return s; }
        }

        private void HandleScrcpyLine(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var line = FixLine(raw.Trim());
            Log("scrcpy: " + line);

            if (line.IndexOf("INSTALL_FAILED_USER_RESTRICTED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LogOnce("apk-restricted",
                    "装 APK 被手机拦了：已在手机上打开「开发者选项」，把「USB 安装」打开即可（只需一次）。");
                OpenDeveloperSettings();
                if (!_restrictedPrompted && !NoDialogs)
                {
                    _restrictedPrompted = true;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            var r = MessageBox.Show(
                                "手机拒绝了安装（INSTALL_FAILED_USER_RESTRICTED）。" + Environment.NewLine + Environment.NewLine +
                                "已经帮你把手机的「开发者选项」打开了：找到「USB 安装」并打开（只需一次）。" + Environment.NewLine +
                                "小米要账号已登录、插着 SIM 卡，这一项才会显示。" + Environment.NewLine + Environment.NewLine +
                                "开好了点「重试」，我立刻重装一次，之后拖 apk 就是全自动。" + Environment.NewLine +
                                "不想开也行：包已经推到手机 Download，到手机上点它装。",
                                "星枢投屏", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                            if (r == DialogResult.OK) RetryPendingInstall();
                        }));
                    }
                    catch { }
                }
            }
            else if (line.StartsWith("ERROR: Failed to install ", StringComparison.OrdinalIgnoreCase) ||
                     line.IndexOf("Failed to install ", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"Failed to install (.+\.apk)");
                if (m.Success)
                {
                    _pendingApk = m.Groups[1].Value.Trim();
                    var apk = _pendingApk;
                    Task.Run(() =>
                    {
                        if (HasRoot() && RootInstall(apk, Path.GetFileName(apk))) return;
                        PushApkFallback(apk);
                    });
                }
            }

            if (line.IndexOf("INSTALL_FAILED_INSUFFICIENT_STORAGE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LogOnce("apk-storage", "装 APK 失败：手机存储空间不足。");
            }
        }

        /// <summary>adb install 被手机拦时，把包推到 /sdcard/Download，让用户在手机上点装。</summary>
        private void PushApkFallback(string apkPath)
        {
            try
            {
                if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath)) return;
                if (string.IsNullOrEmpty(Target())) return;
                LogOnce("apk-push:" + apkPath, "改用兜底方式：把安装包推到手机 Download 目录…");
                Task.Run(() =>
                {
                    var name = Path.GetFileName(apkPath);
                    var push = AdbOut("-s", Target(), "push", apkPath, "/sdcard/Download/" + name);
                    if (push.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log("推包失败：" + FixLine(push.Replace("\n", " ").Trim()));
                        return;
                    }
                    // 顺带把系统安装器拉起来（部分机型支持直接弹安装界面）
                    AdbOut("-s", Target(), "shell", "am", "start", "-a", "android.intent.action.VIEW",
                        "-d", "content://com.android.externalstorage.documents/document/primary%3ADownload%2F" + Uri.EscapeDataString(name),
                        "-t", "application/vnd.android.package-archive");
                    Log("已推到手机：Download/" + name);
                    Log("现在到手机「文件管理 → Download」点它安装即可（这条路不受 USB 安装限制）。");
                });
            }
            catch { }
        }

        private string _pendingApk = "";

        /// <summary>在手机上直接打开「开发者选项」页，省得用户到处找。</summary>
        private void OpenDeveloperSettings()
        {
            try
            {
                if (string.IsNullOrEmpty(Target())) return;
                AdbOut("-s", Target(), "shell", "am", "start", "-a", "android.settings.APPLICATION_DEVELOPMENT_SETTINGS");
                Log("已在手机上打开「开发者选项」：往下找「USB 安装」打开（只需一次）。");
            }
            catch { }
        }

        /// <summary>用户开好开关后，立刻重装刚才那个包。</summary>
        private void RetryPendingInstall()
        {
            if (string.IsNullOrEmpty(_pendingApk)) { Log("没有待安装的包"); return; }
            var apk = _pendingApk;
            Task.Run(() =>
            {
                Log("重试安装：" + Path.GetFileName(apk));
                if (HasRoot() && RootInstall(apk, Path.GetFileName(apk))) { _pendingApk = ""; return; }
                var outp = AdbOut("-s", Target(), "install", "-r", apk);
                if (outp.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("✔ 安装成功：" + Path.GetFileName(apk) + "（以后拖进去就是全自动）");
                    _pendingApk = "";
                }
                else
                {
                    Log("还是被拦：" + FixLine(outp.Replace("\n", " ").Trim()));
                    Log("那就用手机上的包：文件管理 → Download → 点它安装。");
                }
            });
        }

        private bool? _hasRoot;

        /// <summary>手机是否有 root（su 能用）。有的话安装走 su，MIUI 拦不住。</summary>
        private bool HasRoot()
        {
            if (_hasRoot.HasValue) return _hasRoot.Value;
            try
            {
                var outp = AdbOut("-s", Target(), "shell", "su -c id");
                _hasRoot = outp.IndexOf("uid=0", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { _hasRoot = false; }
            if (_hasRoot.Value) Log("检测到手机有 root：安装会走 su 通道，MIUI 的 USB 安装限制不再挡路。");
            return _hasRoot.Value;
        }

        /// <summary>用 root 装：push 到 /data/local/tmp 再 su pm install。</summary>
        public bool RootInstallPublic(string apk) { return RootInstall(apk, System.IO.Path.GetFileName(apk)); }

        private bool RootInstall(string apkPath, string label)
        {
            try
            {
                if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath)) return false;
                var remote = "/data/local/tmp/xingshu_" + DateTime.Now.Ticks + ".apk";
                var push = AdbOut("-s", Target(), "push", apkPath, remote);
                if (push.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("root 安装：推包失败 " + FixLine(push.Replace("\n", " ").Trim()));
                    return false;
                }
                var outp = AdbOut("-s", Target(), "shell", "su -c 'pm install -r " + remote + "'");
                AdbOut("-s", Target(), "shell", "su -c 'rm -f " + remote + "'");
                if (outp.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("✔ " + label + " 已用 root 安装成功（自动，无需任何手机操作）");
                    return true;
                }
                Log("root 安装未成功：" + FixLine(outp.Replace("\n", " ").Trim()));
                return false;
            }
            catch (Exception ex)
            {
                Log("root 安装异常：" + ex.Message);
                return false;
            }
        }

        private bool WaitWindow(Process p, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    p.Refresh();
                    if (p.HasExited) return false;
                    if (p.MainWindowHandle != IntPtr.Zero) return true;
                }
                catch { return false; }
                Thread.Sleep(250);
            }
            return false;
        }

        private bool LaunchChecked(string args, string label, Rectangle? place = null)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Process p = null;
                try
                {
                    p = LaunchScrcpy(args);
                    if (WaitWindow(p, 15000))
                    {
                        if (place.HasValue)
                        {
                            var r = place.Value;
                            try { Native.MoveWindow(p.MainWindowHandle, r.X, r.Y, r.Width, r.Height, true); } catch { }
                            Thread.Sleep(160);
                        }
                        Log(label + " 已就绪");
                        return true;
                    }
                    Log(label + (attempt == 0 ? " 启动超时，降级重试" : " 仍失败"));
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    Thread.Sleep(800);
                }
                catch (Exception ex)
                {
                    Log(label + " 启动异常：" + ex.Message);
                    Thread.Sleep(600);
                }
            }
            return false;
        }

        private void WakeDevice()
        {
            AdbOut("shell", "input", "keyevent", "KEYCODE_WAKEUP");
            AdbOut("shell", "svc", "power", "stayon", "true");
            AdbOut("shell", "settings", "put", "system", "screen_off_timeout", "1800000");
        }

        private void StartMirror(string ip)
        {
            if (!File.Exists(Scrcpy)) { Log("找不到 scrcpy.exe"); return; }
            WakeDevice();
            var args = "-s " + (string.IsNullOrEmpty(_serial) ? ip + ":" + Port : _serial) + " --window-title \"星枢投屏\" --stay-awake --max-fps 30 --video-bit-rate 8M --no-audio";
            LaunchChecked(args, "投屏窗口");
        }

        public async Task StartMultiAsync(bool independent)
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(Target())) { Log("还没连上设备，先点「仅连接」或选一台设备"); return; }
            if (!File.Exists(Scrcpy)) { Log("找不到 scrcpy.exe"); return; }

            int count = (int)_numCount.Value;
            string pkg = "";
            if (independent)
            {
                // 独立实例 = 每窗一块独立虚拟屏（各自可点、互不影响）；选了应用就顺手拉起来
                if (!string.IsNullOrEmpty(ForcePkg)) pkg = ForcePkg;
                else
                {
                    var name = _cmbApp.Text.Trim();
                    if (!string.IsNullOrEmpty(name)) _apps.TryGetValue(name, out pkg);
                }
                if (string.IsNullOrEmpty(pkg)) Log("未选应用：起空虚拟屏，进去自己点");
            }

            if (independent)
            {
                var sdkTxt = AdbOut("-s", Target(), "shell", "getprop", "ro.build.version.sdk").Trim();
                int sdk = 0;
                int.TryParse(sdkTxt, out sdk);
                if (sdk > 0 && sdk < 31)
                {
                    Log("这台手机是 Android " + sdk + "（低于 12），不支持「独立实例」：虚拟屏是 Android 12 才有的能力。");
                    Log("改用「主屏镜像」，或换台 Android 12 以上的机器。");
                    return;
                }
                if (count > 2)
                    Log("提示：多数手机同时只能开 2 块虚拟屏，超过的部分可能白屏；建议一次 2 个。");
            }

            SetBusy(true);
            try
            {
                Log((independent ? "独立实例：" + _cmbApp.Text : "主屏镜像") + " × " + count);
                WakeDevice();
                if (independent) KillSpawned();
                int y;
                MultiLayout(count, out y);
                var box = MultiLayout(count, out y);

                int ok = 0;
                for (int i = 1; i <= count; i++)
                {
                    int x = (int)box.X + (i - 1) * ((int)box.Width + 12);
                    int w = (int)box.Width;
                    int h = (int)box.Height;

                    // 第 2 个窗口起降一档码率/帧率：多路同时编码，中端机扛不住会直接黑屏
                    int fps = i == 1 ? 30 : 20;
                    string br = i == 1 ? "4M" : "2M";
                    int maxSize = i == 1 ? 0 : 1024;

                    string title = independent ? _cmbApp.Text + " #" + i : "主屏 #" + i;
                    string args = independent
                        ? "-s " + Target() + " --new-display=720x1280" +
                          (string.IsNullOrEmpty(pkg) ? "" : " --start-app=" + pkg) +
                          " --window-title \"" + title + "\" --max-fps " + fps +
                          " --video-bit-rate " + br + " --no-audio --video-codec=h264"
                        : "-s " + Target() + " --window-title \"" + title + "\" --stay-awake" +
                          " --max-fps " + fps + " --video-bit-rate " + br + " --no-audio" +
                          (maxSize > 0 ? " --max-size " + maxSize : "");

                    var rect = new Rectangle(x, y, w, h);
                    if (await Task.Run(() => LaunchChecked(args, (independent ? "实例 #" : "窗口 #") + i, rect))) ok++;
                    await Task.Delay(independent ? 4000 : 1200);
                }
                Log("完成：" + ok + " / " + count + " 个" + (independent ? "实例" : "窗口") + "正常");
                if (ok < count) Log("有窗口没起来：确认手机端 scrcpy-server 未被拦截，或把数量降到 2 试试");
            }
            catch (Exception ex) { Log("出错：" + ex.Message); }
            finally { SetBusy(false); }
        }

        /// <summary>自检：不显示主界面，直接按界面同一套参数起 N 个窗口，返回成功的个数。</summary>
        public int RunSelfTest(int count)
        {
            var dev = AdbOut("devices");
            var m = Regex.Match(dev, "([0-9]{1,3}(?:\\.[0-9]{1,3}){3}:\\d+)\\s+device");
            if (!m.Success) { Log("自检：没有可用设备"); return 0; }
            _curIp = m.Groups[1].Value.Split(':')[0];
            Log("自检开始：设备 " + _curIp + "，窗口数 " + count);
            WakeDevice();

            int y;
            var box = MultiLayout(count, out y);
            int ok = 0;
            for (int i = 1; i <= count; i++)
            {
                int x = (int)box.X + (i - 1) * ((int)box.Width + 12);
                int w = (int)box.Width;
                int h = (int)box.Height;
                int fps = i == 1 ? 30 : 20;
                string br = i == 1 ? "4M" : "2M";
                int maxSize = i == 1 ? 0 : 1024;
                string title = "主屏 #" + i;
                string args = "-s " + _curIp + ":" + Port + " --window-title \"" + title + "\" --stay-awake" +
                              " --max-fps " + fps + " --video-bit-rate " + br + " --no-audio" +
                              (maxSize > 0 ? " --max-size " + maxSize : "");
                Log("自检窗口 #" + i + " 目标位置 " + x + "," + y + " 尺寸 " + w + "x" + h);
                if (LaunchChecked(args, "自检窗口 #" + i, new Rectangle(x, y, w, h))) ok++;
                Thread.Sleep(1500);
            }
            Log("自检结束：成功 " + ok + " / " + count);
            Thread.Sleep(6000);   // 留 6 秒给外面截图取证
            return ok;
        }

        /// <summary>USB 连接时把手机切到无线调试（tcpip 5555），之后可拔线。</summary>
        private void UsbToWifi()
        {
            if (string.IsNullOrEmpty(_serial) || _serial.Contains(":"))
            {
                Log("请先在设备下拉里选中一台 USB 连接的手机");
                return;
            }
            Log("把 " + _serial + " 切到无线模式（tcpip 5555）…");
            var outp = AdbOut("-s", _serial, "tcpip", Port.ToString());
            Log(FixLine(outp.Replace("\n", " ").Trim()));
            var ip = AdbOut("-s", _serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0");
            var m = System.Text.RegularExpressions.Regex.Match(ip, @"inet\s+([0-9.]+)/");
            if (m.Success)
            {
                var addr = m.Groups[1].Value + ":" + Port;
                Log("手机无线地址：" + addr + "（拔线后用它连，或直接扫）");
                _txtIp.Text = addr;
            }
            else
            {
                Log("没读到手机 WiFi IP：到「设置 → WLAN」里看一眼，填到地址栏");
            }
        }

        /// <summary>把 APK 拖到窗口上，直接 adb install 到手机。</summary>
        public async Task InstallApkAsync(string path)
        {
            if (!File.Exists(path)) return;
            if (!path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                Log("只支持拖 APK 安装：" + Path.GetFileName(path));
                return;
            }
            if (string.IsNullOrEmpty(Target())) { Log("还没连上设备，先点「仅连接」或选一台设备"); return; }
            Log("安装 " + Path.GetFileName(path) + " …");
            var outp = await Task.Run(() => AdbOut("-s", Target(), "install", "-r", path));
            if (outp.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                Log("安装成功：" + Path.GetFileName(path));
            else
            {
                Log("安装失败：" + FixLine(outp.Replace("\n", " ").Trim()));
                if (HasRoot() && RootInstall(path, Path.GetFileName(path))) return;
                if (outp.IndexOf("USER_RESTRICTED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _pendingApk = path;
                    PushApkFallback(path);
                    OpenDeveloperSettings();
                    Log("处理：手机「开发者选项 → USB 安装」打开后点重试；或到手机 Download 里点包安装。");
                }
            }
        }

        /// <summary>多设备：所有在线设备各开一个镜像窗口，按数量横向平铺。</summary>
        public async Task StartAllAsync()
        {
            if (_busy) return;
            var devs = ScanDevices().Where(d => d.Online).ToList();
            if (devs.Count == 0) { Log("没有在线设备"); return; }
            if (!File.Exists(Scrcpy)) { Log("找不到 scrcpy.exe"); return; }

            SetBusy(true);
            try
            {
                Log("全部投屏：共 " + devs.Count + " 台设备");
                WakeDevice();
                var wa = Screen.PrimaryScreen.WorkingArea;
                int gap = 10, top = 70;
                int cw = Math.Max(180, (wa.Width - 40 - (devs.Count - 1) * gap) / devs.Count);
                int ch = Math.Min(wa.Height - top - 40, (int)(cw * 1280.0 / 720.0));

                int ok = 0;
                for (int i = 0; i < devs.Count; i++)
                {
                    var d = devs[i];
                    int x = 20 + i * (cw + gap);
                    string title = (string.IsNullOrEmpty(d.Model) ? d.Serial : d.Model) + " · " + (i + 1);
                    string args = "-s " + d.Serial + " --window-title \"" + title + "\" --stay-awake" +
                                  " --max-fps 30 --video-bit-rate 4M --no-audio" +
                                  (i > 0 ? " --max-size 1024" : "");
                    if (await Task.Run(() => LaunchChecked(args, d.Serial, new Rectangle(x, top, cw, ch)))) ok++;
                    await Task.Delay(600);
                }
                Log("全部投屏完成：" + ok + " / " + devs.Count);
            }
            catch (Exception ex) { Log("出错：" + ex.Message); }
            finally { SetBusy(false); }
        }

        /// <summary>只结束本程序启动过的 scrcpy（清虚拟屏残留，不动别人的窗口）。</summary>
        private void KillSpawned()
        {
            lock (_spawned)
            {
                foreach (var p in _spawned)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }
                _spawned.Clear();
            }
            Thread.Sleep(1200);
        }

        private void CloseAllWindows()
        {
            int n = 0;
            lock (_spawned)
            {
                foreach (var p in _spawned.ToList())
                {
                    try
                    {
                        if (!p.HasExited) { p.Kill(); n++; }
                    }
                    catch { }
                }
                _spawned.Clear();
            }
            try
            {
                foreach (var p in Process.GetProcessesByName("scrcpy"))
                {
                    try { p.Kill(); n++; } catch { }
                }
            }
            catch { }
            if (n > 0) Log("已关闭 " + n + " 个投屏窗口");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关程序不杀投屏窗口，交给「关闭窗口」按钮，避免误杀
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 先试 per-monitor v2（高分屏 125%/150%/200% 才不会糊/不会缩成一小块），失败退回系统级
            try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例：重复启动直接把已有窗口拉到前面
            bool created;
            using (var mutex = new Mutex(true, "XingshuCast.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("星枢投屏已经在运行了。", "星枢投屏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Fonts.Load(AppDomain.CurrentDomain.BaseDirectory);

                if (args.Length > 0 && args[0] == "--install-test")
                {
                    MainForm.NoDialogs = true;
                    var f4 = new MainForm();
                    f4.RefreshDeviceList();
                    var apk = args.Length > 1 ? args[1] : "";
                    var ok = f4.RootInstallPublic(apk);
                    Thread.Sleep(1000);
                    Environment.Exit(ok ? 0 : 1);
                }
                if (args.Length > 0 && args[0] == "--self-test-indep")
                {
                    int n = 2;
                    if (args.Length > 1) int.TryParse(args[1], out n);
                    var f3 = new MainForm();
                    f3.ForcePkg = args.Length > 2 ? args[2] : "com.miui.calculator";
                    f3.RefreshDeviceList();
                    f3.StartMultiAsync(true).GetAwaiter().GetResult();
                    Thread.Sleep(25000);
                    Environment.Exit(0);
                }
                if (args.Length > 0 && args[0] == "--self-test-all")
                {
                    var f2 = new MainForm();
                    f2.RefreshDeviceList();
                    f2.StartAllAsync().GetAwaiter().GetResult();
                    Environment.Exit(0);
                }
                if (args.Length > 0 && args[0] == "--self-test")
                {
                    int want = 2;
                    if (args.Length > 1) int.TryParse(args[1], out want);
                    var form = new MainForm();
                    int got = form.RunSelfTest(Math.Max(1, Math.Min(4, want)));
                    Thread.Sleep(20000);   // 留 20 秒给取证（窗口不会被管道断开带走）
                    Console.Error.WriteLine("SELFTEST_RESULT ok=" + got + " want=" + want);
                    Environment.Exit(got == want ? 0 : 1);
                }

                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }
}

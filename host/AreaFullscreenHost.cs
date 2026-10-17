// Full PIP 도우미 프로그램 (Chrome 네이티브 메시징 호스트).
//
// Chrome 창은 탭바/주소창/테두리를 스스로 그리기 때문에 바깥에서 지울 수 없다.
// 그래서 지정 영역 크기의 테두리 없는 창(액자)을 만들고, 그 안에 Chrome 팝업 창을 자식으로 넣은 뒤
// 웹 화면 부분만 액자에 딱 맞게 위치를 잡는다. 액자 밖으로 나간 주소창/테두리는 잘려서 보이지 않는다.
//
// 확장앱과 연결(connectNative)이 유지되는 동안 계속 실행되며, 연결이 끊기면 모든 창을 풀어주고 종료한다.
//
// 요청 (모두 id 를 붙여 보내면 같은 id 로 응답)
//   {"cmd":"foreground"}                              → 지금 앞에 있는 Chrome 창의 보이는 영역(실제 픽셀)
//   {"cmd":"embed","key":"...","rect":{...},"area":0.25,"ratio":1.78,"topmost":false,"title":"..."}
//                                                     → 제목에 key 가 들어간 Chrome 창을 액자에 넣는다.
//                                                       area 가 있으면 모니터 면적의 그 비율 크기로 rect 가운데에, 없으면 rect 그대로.
//   {"cmd":"release","key":"..."}                     → 액자에서 꺼내고 액자를 닫는다.
// 알림 (id 없음)
//   {"event":"closed","key":"..."}                    → 사용자가 액자를 닫음(Alt+F4 등)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program
{
    const string ChromeWindowClass = "Chrome_WidgetWin_1";
    const string ChromeContentClass = "Chrome_RenderWidgetHostHWND";

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    const uint MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public IntPtr Extra; }
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int SW_HIDE = 0, SW_RESTORE = 9;
    const int WM_GETICON = 0x007F;
    const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCPAINT = 0x0085, WM_NCACTIVATE = 0x0086, WM_NCLBUTTONDBLCLK = 0x00A3;
    const int WM_NCUAHDRAWCAPTION = 0x00AE, WM_NCUAHDRAWFRAME = 0x00AF;
    const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    const int HTCAPTION = 2, HTTRANSPARENT = -1;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const int WM_SYSCOMMAND = 0x0112, SC_DRAGMOVE = 0xF012;  // SC_MOVE | HTCAPTION: 마우스로 창 이동 시작
    const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    // 이 프로그램이 직접 만든 마우스 입력 표시. 훅에서 이 표시가 붙은 입력은 건드리지 않는다.
    static readonly IntPtr OwnInputTag = new IntPtr(0x41465346);
    const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000;
    const int SM_CXFRAME = 32, SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const uint VK_ESCAPE = 0x1B;
    const int GCLP_HICON = -14;
    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_ASYNCWINDOWPOS = 0x4000;
    const uint GA_PARENT = 1, GA_ROOT = 2;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    // 화면 배율 100% 기준 크기. 실제 픽셀은 모니터 배율에 맞춰 늘린다.
    const int BorderDip = 6;          // 크기 조절용 테두리 두께
    static readonly Color BorderLineColor = Color.FromArgb(225, 225, 225);  // 테두리 바깥쪽의 얇은 선 색
    const int ControlsDip = 72;       // 아래쪽 동영상 컨트롤(재생바) 영역 — 여기서는 드래그로 창을 옮기지 않는다
    const int DragThresholdDip = 5;   // 이만큼 움직여야 클릭이 아니라 드래그로 본다

    static int Scale(IntPtr hwnd, int dip)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? dip : (int)Math.Round(dip * dpi / 96.0);
    }

    class Embedded
    {
        public string Key;
        public IntPtr Chrome;
        public FrameForm Frame;
        public bool Released;
        // Chrome 창 테두리에서 웹 화면까지의 거리 (주소창 높이 등)
        public int DL, DT, DR, DB;
    }

    // 액자 창: 검은 바탕이고, 가장자리를 끌어 크기를 바꿀 수 있다.
    // Chrome 창은 안쪽 패널(Inner)에 넣어 그 크기로 잘리게 하고, 가장자리 테두리(Border)는 액자가 직접 마우스를 받는다.
    // PowerToys FancyZones·Windows 스냅이 일반 창으로 다루도록 크기 조절 가능한 창 스타일을 주되,
    // 제목줄/창 테두리(비클라이언트 영역)는 없애서 화면에는 보이지 않게 한다.
    class FrameForm : Form
    {
        public readonly Panel Inner = new InnerPanel { BackColor = Color.Black };

        public FrameForm()
        {
            Controls.Add(Inner);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        // 가장자리에 얇은 밝은 선을 그려 모든 방향의 테두리가 똑같이 보이게 한다.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int t = Math.Max(1, Program.Scale(Handle, 1)), w = ClientSize.Width, h = ClientSize.Height;
            using (var brush = new SolidBrush(BorderLineColor))
            {
                e.Graphics.FillRectangle(brush, 0, 0, w, t);
                e.Graphics.FillRectangle(brush, 0, h - t, w, t);
                e.Graphics.FillRectangle(brush, 0, 0, t, h);
                e.Graphics.FillRectangle(brush, w - t, 0, t, h);
            }
        }

        public int Border { get { return Program.Scale(Handle, BorderDip); } }

        public void LayoutInner()
        {
            int b = Border;
            Inner.Bounds = new Rectangle(b, b, Math.Max(1, ClientSize.Width - 2 * b), Math.Max(1, ClientSize.Height - 2 * b));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11 의 둥근 모서리와 창 테두리 색을 끈다. (Windows 10 에서는 무시된다)
            int value = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, 4);
            value = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref value, 4);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE:
                    // 비클라이언트 영역을 0 으로 만들어 창 전체를 클라이언트 영역으로 쓴다.
                    // 최대화되면 Windows 가 창을 테두리 두께만큼 화면 밖으로 키우므로 그만큼 안으로 줄인다.
                    // (wParam 이 0 이든 아니든 lParam 의 첫 RECT 가 창 영역이다)
                    if (IsZoomed(Handle))
                    {
                        var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                        uint dpi = GetDpiForWindow(Handle);
                        int pad = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                        int fx = GetSystemMetricsForDpi(SM_CXFRAME, dpi) + pad, fy = GetSystemMetricsForDpi(SM_CYFRAME, dpi) + pad;
                        r.Left += fx; r.Top += fy; r.Right -= fx; r.Bottom -= fy;
                        Marshal.StructureToPtr(r, m.LParam, false);
                    }
                    m.Result = IntPtr.Zero;
                    return;
                case WM_NCLBUTTONDBLCLK:
                    return;  // 더블클릭으로 최대화되지 않게 한다.
                case WM_NCPAINT:
                case WM_NCUAHDRAWCAPTION:
                case WM_NCUAHDRAWFRAME:
                    return;  // 창 테두리를 그리지 않는다. (그리면 가장자리가 회색 틀로 덮인다)
                case WM_NCACTIVATE:
                    m.Result = new IntPtr(1);  // 활성/비활성 전환은 허용하되 테두리는 다시 그리지 않는다.
                    return;
                case WM_NCHITTEST:
                    long lp = m.LParam.ToInt64();
                    Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    int b = Border;
                    bool left = p.X < b, right = p.X >= ClientSize.Width - b;
                    bool top = p.Y < b, bottom = p.Y >= ClientSize.Height - b;
                    // 가운데(평소엔 Chrome 화면이 덮고 있음)는 제목줄처럼 취급해 끌면 창이 움직이게 한다.
                    int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17
                            : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : HTCAPTION;
                    m.Result = new IntPtr(hit);
                    return;
            }
            base.WndProc(ref m);
        }
    }

    // 안쪽 패널은 마우스를 받지 않고 액자에 넘긴다.
    class InnerPanel : Panel
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(HTTRANSPARENT); return; }
            base.WndProc(ref m);
        }
    }

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly Dictionary<string, Embedded> Embeds = new Dictionary<string, Embedded>();
    static readonly object WriteLock = new object();
    static Stream stdout;
    static Form ui;  // 화면에 보이지 않는, UI 스레드로 작업을 넘기기 위한 창

    [STAThread]
    static int Main()
    {
        // 모니터 배율(125% 등)과 상관없이 실제 픽셀 좌표로 다루기 위해 필요하다.
        try { SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2); } catch (EntryPointNotFoundException) { }

        // 예상하지 못한 오류는 %TEMP%\AreaFullscreenHost.log 에 남긴다.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject);

        stdout = Console.OpenStandardOutput();
        ui = new Form();
        IntPtr unused = ui.Handle;  // 보이지 않게 핸들만 만든다.

        var reader = new Thread(ReadLoop) { IsBackground = true };
        reader.Start();

        var watch = new System.Windows.Forms.Timer { Interval = 500 };
        watch.Tick += (s, e) => CheckEmbeds();
        watch.Start();

        overlay = new DragOverlay();
        IntPtr overlayHandle = overlay.Handle;  // 숨긴 채 핸들만 만들어 둔다.
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, MouseHookProc, GetModuleHandle(null), 0);
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, KeyboardHookProc, GetModuleHandle(null), 0);

        Application.Run();

        // 훅을 남긴 채 끝나면 종료 중에 훅이 불려 프로그램이 비정상 종료되므로 먼저 해제한다.
        watch.Stop();
        UnhookWindowsHookEx(mouseHook);
        UnhookWindowsHookEx(keyboardHook);
        return 0;
    }

    static void Log(object error)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "AreaFullscreenHost.log"),
                DateTime.Now.ToString("s") + " " + error + Environment.NewLine);
        }
        catch (IOException) { }
    }

    // 확장앱의 요청을 하나씩 처리한다. 연결이 끊기면 모든 창을 풀어주고 종료한다.
    static void ReadLoop()
    {
        Stream stdin = Console.OpenStandardInput();
        while (true)
        {
            string request = ReadMessage(stdin);
            if (request == null)
            {
                ui.Invoke(new Action(() => { ReleaseAll(); Application.Exit(); }));
                return;
            }

            Dictionary<string, object> msg = null;
            Dictionary<string, object> result;
            try
            {
                msg = Json.Deserialize<Dictionary<string, object>>(request);
                result = Handle(msg);
            }
            catch (Exception e)
            {
                result = Fail((e.InnerException ?? e).Message);
            }
            if (msg != null && msg.ContainsKey("id")) result["id"] = msg["id"];
            Send(result);
        }
    }

    static Dictionary<string, object> Handle(Dictionary<string, object> msg)
    {
        string cmd = (string)msg["cmd"];
        if (cmd == "foreground") return Foreground();
        if (cmd == "embed")
        {
            var rect = (Dictionary<string, object>)msg["rect"];
            RECT target;
            target.Left = Convert.ToInt32(rect["left"]);
            target.Top = Convert.ToInt32(rect["top"]);
            target.Right = Convert.ToInt32(rect["right"]);
            target.Bottom = Convert.ToInt32(rect["bottom"]);
            bool topmost = msg.ContainsKey("topmost") && Convert.ToBoolean(msg["topmost"]);
            string title = msg.ContainsKey("title") ? Convert.ToString(msg["title"]) : "Full PIP";
            if (msg.ContainsKey("area"))
            {
                double ratio = msg.ContainsKey("ratio") ? Convert.ToDouble(msg["ratio"]) : 16.0 / 9;
                target = InitialRect(target, Convert.ToDouble(msg["area"]), ratio);
            }
            return Embed((string)msg["key"], target, topmost, title);
        }
        if (cmd == "release")
        {
            string key = (string)msg["key"];
            ui.Invoke(new Action(() => Release(key)));
            return Ok();
        }
        return Fail("unknown cmd: " + cmd);
    }

    static Dictionary<string, object> Foreground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (ClassName(hwnd) != ChromeWindowClass) return Fail("foreground window is not Chrome");
        var result = Ok();
        result["rect"] = ToDict(VisibleRect(hwnd));
        return result;
    }

    // 영상 창의 처음 위치/크기: 모니터 작업 영역 면적의 area 배 크기로, 동영상 비율(ratio)에 맞춰
    // 원래 Chrome 창(around)의 가운데에 놓는다. 화면 밖으로 나가지 않게 한다.
    static RECT InitialRect(RECT around, double area, double ratio)
    {
        if (area <= 0 || area >= 1) return around;
        ratio = Math.Max(0.3, Math.Min(4.0, ratio));

        var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(MonitorFromRect(ref around, MONITOR_DEFAULTTONEAREST), ref info);
        RECT work = info.Work;

        double height = Math.Sqrt((double)work.Width * work.Height * area / ratio);
        double width = height * ratio;
        if (width > work.Width) { width = work.Width; height = width / ratio; }
        if (height > work.Height) { height = work.Height; width = height * ratio; }

        int w = (int)Math.Round(width), h = (int)Math.Round(height);
        int left = (around.Left + around.Right - w) / 2, top = (around.Top + around.Bottom - h) / 2;
        left = Math.Max(work.Left, Math.Min(work.Right - w, left));
        top = Math.Max(work.Top, Math.Min(work.Bottom - h, top));

        RECT r;
        r.Left = left; r.Top = top; r.Right = left + w; r.Bottom = top + h;
        return r;
    }

    static Dictionary<string, object> Embed(string key, RECT target, bool topmost, string title)
    {
        IntPtr chrome = WaitFor(() => FindChromeWindow(key), 3000);
        if (chrome == IntPtr.Zero) return Fail("popup window not found");
        if (IsZoomed(chrome)) ShowWindow(chrome, SW_RESTORE);

        // 액자 창을 만들고 Chrome 창을 그 안에 넣는다. (Chrome 창의 스타일은 건드리지 않아야 화면이 제대로 그려진다)
        FrameForm frame = null;
        ui.Invoke(new Action(() =>
        {
            frame = new FrameForm
            {
                MinimumSize = new Size(240, 135),
                FormBorderStyle = FormBorderStyle.None,
                BackColor = Color.Black,
                StartPosition = FormStartPosition.Manual,
                AutoScaleMode = AutoScaleMode.None,
                TopMost = topmost,
                Text = title,
                Bounds = new Rectangle(target.Left, target.Top, target.Width, target.Height),
            };
            IntPtr icon = ChromeIcon(chrome);
            if (icon != IntPtr.Zero) frame.Icon = Icon.FromHandle(icon);
            frame.Show();
            SetWindowPos(frame.Handle, topmost ? HWND_TOPMOST : HWND_TOP,
                target.Left, target.Top, target.Width, target.Height, SWP_SHOWWINDOW);
            frame.LayoutInner();
            SetParent(chrome, frame.Inner.Handle);
        }));

        var embedded = new Embedded { Key = key, Chrome = chrome, Frame = frame };
        frame.FormClosing += (s, e) =>
        {
            if (embedded.Released) return;
            // 사용자가 닫음(ESC, Alt+F4): Chrome 창을 꺼내고 확장앱에 알린다.
            embedded.Released = true;
            if (Embeds.ContainsKey(key) && Embeds[key] == embedded) Embeds.Remove(key);
            Unparent(embedded);
            Send(Event("closed", key));
        };
        ui.Invoke(new Action(() => { Release(key); Embeds[key] = embedded; }));

        // 웹 화면 부분이 액자 안쪽(테두리 제외)과 정확히 겹치도록 Chrome 창의 위치/크기를 잡는다.
        // 크기를 바꾸면 Chrome 이 레이아웃을 다시 잡으므로 맞을 때까지 몇 번 반복한다.
        // (모니터 배율 때문에 1px 정도는 어긋날 수 있다)
        int border = 0;
        ui.Invoke(new Action(() => border = frame.Border));
        RECT inner = target;
        inner.Left += border; inner.Top += border; inner.Right -= border; inner.Bottom -= border;

        RECT window = new RECT(), content = new RECT();
        for (int i = 0; i < 5; i++)
        {
            IntPtr contentHwnd = WaitFor(() => FindContent(chrome), 2000);
            if (contentHwnd == IntPtr.Zero) return Fail("content area not found");

            GetWindowRect(chrome, out window);
            GetWindowRect(contentHwnd, out content);
            embedded.DL = content.Left - window.Left;
            embedded.DT = content.Top - window.Top;
            embedded.DR = window.Right - content.Right;
            embedded.DB = window.Bottom - content.Bottom;
            if (Fits(content, inner)) break;

            PlaceChrome(embedded, inner.Width, inner.Height, SWP_NOZORDER | SWP_SHOWWINDOW);
            Thread.Sleep(200);
        }

        ui.Invoke(new Action(() =>
        {
            // 이후 사용자가 액자 크기를 바꾸면 Chrome 창도 따라간다.
            frame.SizeChanged += (s, e) => LayoutChrome(embedded);
            Activate(frame.Handle);
            SetFocus(chrome);
        }));

        var result = Ok();
        result["content"] = ToDict(content);
        result["matched"] = Fits(content, inner);
        return result;
    }

    // Windows 는 다른 프로그램이 앞에 있을 때 창을 앞으로 가져오는 것을 막으므로,
    // 잠시 그 창의 입력에 붙어서 가져온다. (ESC 로 닫으려면 영상 창이 앞에 있어야 한다)
    static void Activate(IntPtr hwnd)
    {
        uint me = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        bool attached = foregroundThread != 0 && foregroundThread != me && AttachThreadInput(me, foregroundThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, foregroundThread, false);
    }

    static void LayoutChrome(Embedded e)
    {
        e.Frame.LayoutInner();
        AlignChrome(e);
    }

    // Chrome 의 웹 화면이 액자 안쪽(Inner)과 정확히 겹치도록 맞춘다.
    // 주소창 높이 등은 모니터 배율이 바뀌면(다른 모니터로 옮기면) 달라지므로 매번 새로 잰다.
    // 어긋난 채 두면 위쪽에 주소창 일부가 하얀 띠처럼 보인다.
    static void AlignChrome(Embedded e)
    {
        IntPtr content = FindContent(e.Chrome);
        if (content == IntPtr.Zero) return;
        RECT window, view;
        GetWindowRect(e.Chrome, out window);
        GetWindowRect(content, out view);
        e.DL = view.Left - window.Left;
        e.DT = view.Top - window.Top;
        e.DR = window.Right - view.Right;
        e.DB = window.Bottom - view.Bottom;

        Rectangle inner = e.Frame.Inner.RectangleToScreen(e.Frame.Inner.ClientRectangle);
        RECT target;
        target.Left = inner.Left; target.Top = inner.Top; target.Right = inner.Right; target.Bottom = inner.Bottom;
        if (Fits(view, target)) return;

        PlaceChrome(e, inner.Width, inner.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    // 모니터 배율 때문에 Chrome 이 크기를 1px 정도 반올림하면 웹 화면 가장자리에 Chrome 창 테두리가
    // 한 줄 비쳐 보인다. 그래서 웹 화면을 액자 안쪽보다 사방 Overlap 만큼 크게 잡아 넘치는 부분이 잘리게 한다.
    const int Overlap = 2;

    static void PlaceChrome(Embedded e, int innerWidth, int innerHeight, uint flags)
    {
        SetWindowPos(e.Chrome, IntPtr.Zero, -e.DL - Overlap, -e.DT - Overlap,
            innerWidth + 2 * Overlap + e.DL + e.DR, innerHeight + 2 * Overlap + e.DT + e.DB, flags);
    }

    // 웹 화면이 안쪽을 빈틈없이 덮고, 넘치는 정도도 Overlap 근처인지 본다.
    static bool Fits(RECT view, RECT inner)
    {
        RECT target = inner;
        target.Left -= Overlap; target.Top -= Overlap; target.Right += Overlap; target.Bottom += Overlap;
        return view.Left <= inner.Left && view.Top <= inner.Top && view.Right >= inner.Right && view.Bottom >= inner.Bottom
            && Near(view, target);
    }

    // 영상 창 마우스 처리
    //  - 영상 창 어디든 누르면 영상 창을 맨 앞으로 가져온다.
    //  - 동영상 위를 끌면 창을 옮긴다. (PIP 처럼)
    //    누르는 순간에는 클릭인지 드래그인지 알 수 없으므로 일단 Chrome 에 전달하지 않고,
    //    움직이지 않고 떼면 그때 클릭을 다시 만들어 Chrome 에 보낸다.
    //  - 끌기 시작하면 Windows 기본 창 이동으로 넘긴다. 그래야 Windows 화면 가장자리 스냅·스냅 레이아웃과
    //    PowerToys FancyZones 가 동작한다. 마우스는 Chrome 창 쪽에 묶여 있으므로, 커서 아래에 거의 투명한
    //    작은 창(DragOverlay)을 띄우고 거기에 마우스 누름을 만들어 보내 이 프로그램이 마우스를 잡게 한 뒤
    //    창 이동을 시작한다. 넘기기 전(또는 넘기지 못한 경우)에는 직접 창을 옮긴다.
    static IntPtr mouseHook;
    static readonly HookProc MouseHookProc = OnMouse;  // GC 되지 않도록 붙잡아 둔다.
    static Embedded dragEmbedded;
    static POINT dragStart;
    static Point dragFrameStart;
    static bool dragging;       // 클릭이 아니라 끌기로 판정됨
    static bool injectedDown;   // 넘기기용 마우스 누름을 만들어 보냄 (실제 버튼 떼기는 막지 말아야 한다)
    static bool systemMoving;   // Windows 기본 창 이동 중
    static DragOverlay overlay;

    // 커서 아래에 잠깐 띄우는 거의 투명한 창. 마우스 누름을 받으면 액자 창의 Windows 기본 창 이동을 시작한다.
    class DragOverlay : Form
    {
        public DragOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Opacity = 0.01;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
            if (m.Msg == WM_LBUTTONDOWN) { HideOverlay(); BeginSystemMove(); return; }
            base.WndProc(ref m);
        }
    }

    // 훅 안에서 예외가 나면 프로그램 전체가 죽으므로, 문제가 생기면 입력을 그대로 넘긴다.
    static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        try { return HandleMouse(code, wParam, lParam); }
        catch (Exception) { dragEmbedded = null; return CallNextHookEx(mouseHook, code, wParam, lParam); }
    }

    static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        try { return HandleKey(code, wParam, lParam); }
        catch (Exception) { return CallNextHookEx(keyboardHook, code, wParam, lParam); }
    }

    static IntPtr HandleMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(mouseHook, code, wParam, lParam);
        var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        if (info.Extra == OwnInputTag) return CallNextHookEx(mouseHook, code, wParam, lParam);
        if (dragEmbedded != null && dragEmbedded.Frame.IsDisposed) dragEmbedded = null;

        int msg = wParam.ToInt32();
        if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
        {
            bool onVideo;
            Embedded embedded = EmbeddedAt(info.Pt, out onVideo);
            if (embedded != null)
            {
                ui.BeginInvoke(new Action(() => BringToFront(embedded)));
                if (msg == WM_LBUTTONDOWN && onVideo)
                {
                    dragEmbedded = embedded;
                    dragStart = info.Pt;
                    dragFrameStart = embedded.Frame.Location;
                    dragging = injectedDown = systemMoving = false;
                    return new IntPtr(1);
                }
            }
        }
        else if (msg == WM_MOUSEMOVE && dragEmbedded != null && !systemMoving)
        {
            IntPtr frame = dragEmbedded.Frame.Handle;
            int dx = info.Pt.X - dragStart.X, dy = info.Pt.Y - dragStart.Y;
            int threshold = Scale(frame, DragThresholdDip);
            if (!dragging && (Math.Abs(dx) > threshold || Math.Abs(dy) > threshold))
            {
                dragging = true;
                Embedded embedded = dragEmbedded;
                ui.BeginInvoke(new Action(() => HandOffToSystemMove(embedded)));
            }
            if (dragging)
                SetWindowPos(frame, IntPtr.Zero, dragFrameStart.X + dx, dragFrameStart.Y + dy, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        else if (msg == WM_LBUTTONUP && dragEmbedded != null)
        {
            bool wasDragging = dragging, passThrough = injectedDown;
            dragEmbedded = null;
            if (injectedDown) ui.BeginInvoke(new Action(HideOverlay));
            if (!wasDragging) { ui.BeginInvoke(new Action(ReplayClick)); return new IntPtr(1); }
            // 만들어 보낸 누름이 있으면 실제 떼기를 그대로 보내야 버튼이 눌린 상태로 남지 않고, 창 이동도 끝난다.
            if (!passThrough) return new IntPtr(1);
        }
        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    // 커서 아래에 투명한 창을 띄우고 그 위에 마우스 누름을 만들어 보낸다. (이어서 DragOverlay 가 받는다)
    static void HandOffToSystemMove(Embedded embedded)
    {
        if (dragEmbedded != embedded || embedded.Frame.IsDisposed) return;  // 그사이 버튼을 뗀 경우
        Point cursor = Cursor.Position;
        int size = Scale(embedded.Frame.Handle, 64);
        SetWindowPos(overlay.Handle, HWND_TOPMOST, cursor.X - size / 2, cursor.Y - size / 2, size, size,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        injectedDown = true;
        SendMouse(MOUSEEVENTF_LEFTDOWN);
    }

    // WinForms 를 거치지 않고 띄웠으므로 숨길 때도 직접 숨긴다.
    static void HideOverlay()
    {
        ShowWindow(overlay.Handle, SW_HIDE);
    }

    static void BeginSystemMove()
    {
        Embedded embedded = dragEmbedded;
        if (embedded == null || !dragging || embedded.Frame.IsDisposed) return;
        systemMoving = true;
        ReleaseCapture();
        // 버튼을 뗄 때까지 여기서 돌아오지 않는다.
        SendMessage(embedded.Frame.Handle, WM_SYSCOMMAND, new IntPtr(SC_DRAGMOVE), IntPtr.Zero);
        systemMoving = false;
        dragEmbedded = null;
    }

    // pt 가 어느 영상 창 위인지, 그리고 끌어서 옮길 수 있는 동영상 화면 위인지
    // (액자 테두리와 아래쪽 동영상 컨트롤 영역 제외) 알려 준다.
    static Embedded EmbeddedAt(POINT pt, out bool onVideo)
    {
        onVideo = false;
        IntPtr hwnd = WindowFromPoint(pt);
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        foreach (var embedded in Embeds.Values)
        {
            FrameForm frame = embedded.Frame;
            if (frame.IsDisposed || root != frame.Handle) continue;
            RECT r;
            GetWindowRect(frame.Handle, out r);
            onVideo = hwnd != frame.Handle && pt.Y < r.Bottom - Scale(frame.Handle, ControlsDip);
            return embedded;
        }
        return null;
    }

    static void BringToFront(Embedded embedded)
    {
        if (embedded.Frame.IsDisposed) return;
        Activate(embedded.Frame.Handle);
        SetFocus(embedded.Chrome);
    }

    static void ReplayClick()
    {
        SendMouse(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
    }

    static void SendMouse(params uint[] flags)
    {
        var inputs = new INPUT[flags.Length];
        for (int i = 0; i < flags.Length; i++)
        {
            inputs[i].Mouse.Flags = flags[i];
            inputs[i].Mouse.Extra = OwnInputTag;
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    // 영상 창이 앞에 있을 때 ESC 를 누르면 바로 닫는다.
    // (웹 페이지에 키보드 포커스가 없어도 동작하도록 여기서 직접 받는다)
    // 액자를 닫으면 FormClosing 에서 Chrome 창을 꺼내고 확장앱에 알려, 탭이 원래 창으로 돌아간다.
    static IntPtr keyboardHook;
    static readonly HookProc KeyboardHookProc = OnKey;  // GC 되지 않도록 붙잡아 둔다.

    static IntPtr HandleKey(int code, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (code >= 0 && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
        {
            var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            if (info.VkCode == VK_ESCAPE)
            {
                IntPtr foreground = GetForegroundWindow();
                foreach (var embedded in Embeds.Values)
                {
                    if (embedded.Frame.IsDisposed || embedded.Frame.Handle != foreground) continue;
                    FrameForm frame = embedded.Frame;
                    ui.BeginInvoke(new Action(() => { if (!frame.IsDisposed) frame.Close(); }));
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    // 아래는 UI 스레드에서만 호출한다.

    static void Release(string key)
    {
        Embedded embedded;
        if (!Embeds.TryGetValue(key, out embedded)) return;
        Embeds.Remove(key);
        embedded.Released = true;
        Unparent(embedded);
        embedded.Frame.Close();
    }

    static void ReleaseAll()
    {
        foreach (string key in new List<string>(Embeds.Keys)) Release(key);
    }

    // 액자를 닫기 전에 Chrome 창을 꺼내 둔다. (그대로 닫으면 Chrome 창도 함께 파괴된다)
    static void Unparent(Embedded embedded)
    {
        if (!IsInFrame(embedded)) return;
        RECT r;
        GetWindowRect(embedded.Chrome, out r);
        SetParent(embedded.Chrome, IntPtr.Zero);
        SetWindowPos(embedded.Chrome, HWND_TOP, r.Left, r.Top, r.Width, r.Height, SWP_SHOWWINDOW);
    }

    // Chrome 쪽에서 창이 닫히면(탭이 다른 창으로 옮겨지는 등) 빈 액자를 정리한다.
    // 웹 화면 위치가 어긋났으면 다시 맞춘다. (다른 모니터로 옮겨 배율이 바뀐 경우 등)
    static void CheckEmbeds()
    {
        foreach (var embedded in new List<Embedded>(Embeds.Values))
        {
            if (IsInFrame(embedded)) { LayoutChrome(embedded); continue; }
            Embeds.Remove(embedded.Key);
            embedded.Released = true;
            embedded.Frame.Close();
            Send(Event("closed", embedded.Key));
        }
    }

    // Chrome 창은 WS_CHILD 스타일이 아니라서 GetParent 는 부모 대신 소유자를 돌려준다. 실제 부모는 GetAncestor 로 본다.
    static bool IsInFrame(Embedded embedded)
    {
        return !embedded.Frame.IsDisposed && IsWindow(embedded.Chrome) && GetAncestor(embedded.Chrome, GA_PARENT) == embedded.Frame.Inner.Handle;
    }

    static IntPtr FindChromeWindow(string key)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && ClassName(hwnd) == ChromeWindowClass && WindowText(hwnd).Contains(key))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // 창 안에서 가장 큰 웹 화면 영역을 찾는다.
    static IntPtr FindContent(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        long bestArea = 0;
        EnumChildWindows(parent, (hwnd, _) =>
        {
            RECT r;
            if (IsWindowVisible(hwnd) && ClassName(hwnd) == ChromeContentClass && GetWindowRect(hwnd, out r))
            {
                long area = (long)r.Width * r.Height;
                if (area > bestArea) { bestArea = area; found = hwnd; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr ChromeIcon(IntPtr hwnd)
    {
        IntPtr icon = SendMessage(hwnd, WM_GETICON, new IntPtr(1), IntPtr.Zero);  // ICON_BIG
        return icon != IntPtr.Zero ? icon : GetClassLongPtr(hwnd, GCLP_HICON);
    }

    static IntPtr WaitFor(Func<IntPtr> find, int timeoutMs)
    {
        for (int waited = 0; ; waited += 50)
        {
            IntPtr hwnd = find();
            if (hwnd != IntPtr.Zero || waited >= timeoutMs) return hwnd;
            Thread.Sleep(50);
        }
    }

    // Windows 10/11 의 보이지 않는 크기 조절 테두리를 뺀, 눈에 보이는 창 영역.
    static RECT VisibleRect(IntPtr hwnd)
    {
        RECT r;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0)
            GetWindowRect(hwnd, out r);
        return r;
    }

    static bool Near(RECT a, RECT b)
    {
        return Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1
            && Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;
    }

    static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(1024);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static Dictionary<string, object> ToDict(RECT r)
    {
        return new Dictionary<string, object> { { "left", r.Left }, { "top", r.Top }, { "right", r.Right }, { "bottom", r.Bottom } };
    }

    static Dictionary<string, object> Ok()
    {
        return new Dictionary<string, object> { { "ok", true } };
    }

    static Dictionary<string, object> Fail(string error)
    {
        return new Dictionary<string, object> { { "ok", false }, { "error", error } };
    }

    static Dictionary<string, object> Event(string name, string key)
    {
        return new Dictionary<string, object> { { "event", name }, { "key", key } };
    }

    // 네이티브 메시징 형식: 4바이트(리틀 엔디언) 길이 + UTF-8 JSON
    static string ReadMessage(Stream s)
    {
        byte[] length = ReadExact(s, 4);
        if (length == null) return null;
        byte[] body = ReadExact(s, BitConverter.ToInt32(length, 0));
        return body == null ? null : Encoding.UTF8.GetString(body);
    }

    static byte[] ReadExact(Stream s, int n)
    {
        var buffer = new byte[n];
        for (int offset = 0; offset < n; )
        {
            int read = s.Read(buffer, offset, n - offset);
            if (read <= 0) return null;
            offset += read;
        }
        return buffer;
    }

    static void Send(Dictionary<string, object> msg)
    {
        byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(msg));
        lock (WriteLock)
        {
            try
            {
                stdout.Write(BitConverter.GetBytes(body.Length), 0, 4);
                stdout.Write(body, 0, body.Length);
                stdout.Flush();
            }
            catch (IOException) { }  // 확장앱 연결이 이미 끊긴 경우
        }
    }
}

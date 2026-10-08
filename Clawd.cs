// Claw'd desktop pet: a transparent always-on-top WPF window that walks along
// the taskbar, perches on window tops, hangs from window and screen edges,
// codes, reads, drinks coffee and talks in short speech bubbles.
// Build with build.ps1 (uses the csc.exe that ships with Windows, C# 5).
//
// A running pet also takes commands from a second launch:
//   Clawd.exe --say "text"     show a speech bubble
//   Clawd.exe --state code     any name from Pet.StateNames, or hang | cling
//   Clawd.exe --drop 800,600   let go of it with its feet at that screen pixel
// Launching with no arguments toggles the pet on and off.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("kernel32.dll")] public static extern uint GetTickCount();
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object acc);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const int VK_LBUTTON = 1;
    public const uint GA_ROOT = 2;
    public const uint OBJID_CARET = 0xFFFFFFF8;
}

enum St
{
    Fall, Idle, Walk, Sit, Sleep, Wave, Drag, Hang, Cling, Code, Think, Dance,
    Coffee, Read, Stretch, Celebrate, Music, Search, Dizzy, Love,
    Watch, Game, Phone, Exercise, Eat,
    // Being stroked, knocked off its feet, running after the ball, bringing
    // it back, and hiding in a game of hide-and-seek.
    Pet, Trip, Chase, Fetch, Hide,
    // Running from the mouse in a game of tag, and waiting for the signal in
    // the reaction game.
    Flee, Ready
}

enum BallSt { None, Held, Free, Rest, Carried }

// A diary of drops, for working out why the pet did not end up where it was
// put. Off unless the CLAWD_LOG environment variable names a file.
static class Log
{
    static readonly string Path = Environment.GetEnvironmentVariable("CLAWD_LOG");
    static readonly object Gate = new object();

    public static void Write(string format, params object[] args)
    {
        if (string.IsNullOrEmpty(Path)) return;
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + string.Format(CultureInfo.InvariantCulture, format, args) + "\n";
        lock (Gate)
        {
            try { File.AppendAllText(Path, line, Encoding.UTF8); }
            catch (IOException) { }
        }
    }

    public static string Box(Rect r)
    {
        return r.IsEmpty ? "(empty)" : string.Format(CultureInfo.InvariantCulture, "({0:0},{1:0} {2:0}x{3:0})", r.X, r.Y, r.Width, r.Height);
    }
}

// What the person at the computer is doing, as far as the pet can tell.
enum Ctx { None, Music, Video, Coding, Gaming, Chatting, Away }

enum EyeKind { Open, Closed, Happy, Wide, Squint, Dizzy }

struct WinInfo
{
    public IntPtr H;
    public Rect R;
    public bool Zoomed;
}

// Watches, from a background thread, what the person is doing: which media is
// playing, which app is in front, and how long the mouse and keyboard have
// been still. Nothing it reads leaves this process.
class Activity
{
    static readonly string[] MusicApps = { "spotify", "music", "aimp", "foobar", "winamp", "itunes", "deezer", "tidal" };
    static readonly string[] MusicSites = { "youtube music", "яндекс музыка", "yandex music", "soundcloud", "spotify", "apple music", "vk музыка", "deezer", "bandcamp" };
    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "browser", "brave", "vivaldi" };
    static readonly string[] Players = { "vlc", "mpv", "mpc-hc64", "mpc-hc", "wmplayer", "potplayermini64", "video.ui" };
    static readonly string[] Coders = { "code", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "clion64", "windowsterminal", "cmd", "powershell", "pwsh", "claude", "cursor", "sublime_text", "notepad++", "robloxstudiobeta", "unity", "godot" };
    // Words in a title or channel name that mark a song rather than a film.
    static readonly string[] SongWords = { "official video", "official audio", "official music", "music video", "lyric", "feat.", "ft.", "prod.", "remix", "- topic", "vevo", "клип", "премьера", "альбом", "audio" };
    static readonly string[] Chats = { "discord", "telegram", "whatsapp", "slack", "ms-teams", "teams", "viber", "signal" };

    public volatile Ctx Context = Ctx.None;
    public volatile string Title = "";
    public volatile string Artist = "";
    // The small hours, when the pet nags about bedtime.
    public volatile bool Late;

    // True while keys are being pressed: several inputs in a row with the
    // mouse standing still. Which keys is never looked at.
    public volatile bool Typing;

    GlobalSystemMediaTransportControlsSessionManager media;
    readonly int myPid = Process.GetCurrentProcess().Id;
    readonly object caretGate = new object();
    Rect caret = Rect.Empty;

    public Activity()
    {
        Thread thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Start();
        Thread typing = new Thread(CaretLoop);
        typing.IsBackground = true;
        typing.Start();
    }

    // Where the text cursor is while the person types, in screen pixels;
    // false when the app in front does not say.
    public bool TryCaret(out Rect rect)
    {
        lock (caretGate)
        {
            rect = caret;
            return !rect.IsEmpty;
        }
    }

    void CaretLoop()
    {
        uint lastInput = 0;
        Native.POINT lastCursor = new Native.POINT();
        int streak = 0;
        while (true)
        {
            Native.LASTINPUTINFO input = new Native.LASTINPUTINFO();
            input.cbSize = 8;
            Native.GetLastInputInfo(ref input);
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            bool keys = input.dwTime != lastInput && cursor.X == lastCursor.X && cursor.Y == lastCursor.Y;
            streak = keys ? Math.Min(8, streak + 2) : Math.Max(0, streak - 1);
            lastInput = input.dwTime;
            lastCursor = cursor;
            Typing = streak >= 5;

            Rect found = Rect.Empty;
            if (Typing)
            {
                // The app in front may be busy or closing; then there is simply no caret to dodge.
                try { found = ReadCaret(); }
                catch (Exception) { }
            }
            lock (caretGate) caret = found;
            Thread.Sleep(200);
        }
    }

    // Classic apps report the caret through the window system; browsers and
    // apps built on them only through the accessibility interface.
    Rect ReadCaret()
    {
        IntPtr front = Native.GetForegroundWindow();
        uint pid;
        uint thread = Native.GetWindowThreadProcessId(front, out pid);
        if (front == IntPtr.Zero || pid == myPid) return Rect.Empty;

        Native.GUITHREADINFO info = new Native.GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
        if (!Native.GetGUIThreadInfo(thread, ref info)) return Rect.Empty;
        Native.RECT rc = info.rcCaret;
        if (info.hwndCaret != IntPtr.Zero && (rc.Right > rc.Left || rc.Bottom > rc.Top))
        {
            Native.POINT corner;
            corner.X = rc.Left;
            corner.Y = rc.Top;
            Native.ClientToScreen(info.hwndCaret, ref corner);
            return new Rect(corner.X, corner.Y, Math.Max(1, rc.Right - rc.Left), Math.Max(1, rc.Bottom - rc.Top));
        }

        object found;
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        IntPtr focus = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : front;
        if (Native.AccessibleObjectFromWindow(focus, Native.OBJID_CARET, ref iid, out found) != 0) return Rect.Empty;
        Accessibility.IAccessible acc = found as Accessibility.IAccessible;
        if (acc == null) return Rect.Empty;
        int left, top, width, height;
        acc.accLocation(out left, out top, out width, out height, 0);
        if ((left == 0 && top == 0) || (width <= 0 && height <= 0)) return Rect.Empty;
        return new Rect(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    void Loop()
    {
        while (true)
        {
            // Apps exit and media sessions close mid-query; the next poll starts clean.
            try { Poll(); }
            catch (Exception) { media = null; }
            Thread.Sleep(2000);
        }
    }

    static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
    {
        while (op.Status == Windows.Foundation.AsyncStatus.Started) Thread.Sleep(10);
        return op.GetResults();
    }

    static bool Has(string[] words, string text)
    {
        for (int i = 0; i < words.Length; i++)
            if (text.Contains(words[i])) return true;
        return false;
    }

    static string ProcessOf(IntPtr window)
    {
        uint pid;
        Native.GetWindowThreadProcessId(window, out pid);
        if (pid == 0) return "";
        try { return Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch (ArgumentException) { return ""; }
    }

    static bool AnyWindowTitleHas(string[] words)
    {
        bool found = false;
        StringBuilder sb = new StringBuilder(256);
        Native.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!Native.IsWindowVisible(h)) return true;
            sb.Length = 0;
            Native.GetWindowText(h, sb, 256);
            if (!Has(words, sb.ToString().ToLowerInvariant())) return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    static bool Fullscreen(IntPtr window)
    {
        Native.RECT r;
        if (window == IntPtr.Zero || !Native.GetWindowRect(window, out r)) return false;
        Native.POINT centre;
        centre.X = (r.Left + r.Right) / 2;
        centre.Y = (r.Top + r.Bottom) / 2;
        Native.MONITORINFO info = new Native.MONITORINFO();
        info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
        if (!Native.GetMonitorInfo(Native.MonitorFromPoint(centre, 2), ref info)) return false;
        Native.RECT m = info.rcMonitor;
        if (r.Left != m.Left || r.Top != m.Top || r.Right != m.Right || r.Bottom != m.Bottom) return false;
        StringBuilder cls = new StringBuilder(64);
        Native.GetClassName(window, cls, 64);
        string name = cls.ToString();
        return name != "Progman" && name != "WorkerW";
    }

    bool Playing(out string app, out string title, out string artist, out double seconds)
    {
        app = title = artist = "";
        seconds = 0;
        if (media == null) media = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        foreach (GlobalSystemMediaTransportControlsSession session in media.GetSessions())
        {
            if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            GlobalSystemMediaTransportControlsSessionMediaProperties props = Wait(session.TryGetMediaPropertiesAsync());
            app = (session.SourceAppUserModelId ?? "").ToLowerInvariant();
            title = props.Title ?? "";
            artist = props.Artist ?? "";
            GlobalSystemMediaTransportControlsSessionTimelineProperties time = session.GetTimelineProperties();
            seconds = (time.EndTime - time.StartTime).TotalSeconds;
            return true;
        }
        return false;
    }

    void Poll()
    {
        Native.LASTINPUTINFO input = new Native.LASTINPUTINFO();
        input.cbSize = 8;
        Native.GetLastInputInfo(ref input);
        double idle = (Native.GetTickCount() - input.dwTime) / 1000.0;

        IntPtr front = Native.GetForegroundWindow();
        string proc = ProcessOf(front);
        bool viewer = Array.IndexOf(Browsers, proc) >= 0 || Array.IndexOf(Players, proc) >= 0;

        string app, title, artist;
        double seconds;
        bool playing = Playing(out app, out title, out artist, out seconds);

        Ctx c = Ctx.None;
        if (proc != "" && proc != "explorer" && !viewer && Fullscreen(front)) c = Ctx.Gaming;
        else if (playing)
        {
            // A browser plays both. It counts as watching only while the browser
            // is in front and what plays does not look like a song: no music
            // site open, nothing song-like in the title, longer than a track.
            bool song = (seconds > 0 && seconds < 400) || Has(SongWords, (title + " " + artist).ToLowerInvariant());
            bool watching = Has(Browsers, app) && Array.IndexOf(Browsers, proc) >= 0 && app.Contains(proc)
                && !song && !AnyWindowTitleHas(MusicSites);
            c = (Has(Players, app) || watching) && !Has(MusicApps, app) ? Ctx.Video : Ctx.Music;
        }
        else if (idle > 180) c = Ctx.Away;
        else if (Array.IndexOf(Coders, proc) >= 0) c = Ctx.Coding;
        else if (Array.IndexOf(Chats, proc) >= 0) c = Ctx.Chatting;

        int hour = DateTime.Now.Hour;
        Late = hour >= 1 && hour < 5;
        Title = playing ? title : "";
        Artist = playing ? artist : "";
        Context = c;
    }
}

delegate void ElementFound(AutomationElement element, Rect rect);

// Finds the interface component under a point (a button, a picture, a link on
// a web page) through UI Automation, and keeps reading where it is so the pet
// can ride it as the page scrolls. All of it runs on a background thread:
// another app can take its time answering.
class ElementTracker
{
    readonly Dispatcher ui;
    readonly int myPid = Process.GetCurrentProcess().Id;
    readonly object gate = new object();
    readonly AutoResetEvent wake = new AutoResetEvent(false);

    AutomationElement tracked;
    Rect trackedRect;
    bool alive;

    bool pending;
    Point[] probes;
    Rect windowRect;
    ElementFound callback;

    public ElementTracker(Dispatcher dispatcher)
    {
        ui = dispatcher;
        Thread thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Start();
    }

    // Looks under each point in turn, in screen pixels, and reports the first
    // component found (or none) on the UI thread.
    public void Lookup(Point[] points, Rect window, ElementFound found)
    {
        lock (gate)
        {
            probes = points;
            windowRect = window;
            callback = found;
            pending = true;
        }
        wake.Set();
    }

    public void Track(AutomationElement element, Rect rect)
    {
        lock (gate)
        {
            tracked = element;
            trackedRect = rect;
            alive = true;
        }
        wake.Set();
    }

    public void Stop()
    {
        lock (gate) tracked = null;
    }

    public bool Alive
    {
        get { lock (gate) return tracked != null && alive; }
    }

    // Where the tracked component is now, in screen pixels; false once it is
    // gone or scrolled out of view.
    public bool TryGet(out Rect rect)
    {
        lock (gate)
        {
            rect = trackedRect;
            return tracked != null && alive;
        }
    }

    // The component at the point, or its nearest ancestor big enough to hold
    // on to; null for a whole page, a whole window, or the pet itself.
    AutomationElement Find(Point at, Rect window, out Rect rect)
    {
        rect = Rect.Empty;
        AutomationElement e = AutomationElement.FromPoint(at);
        for (int depth = 0; e != null && depth < 6; depth++)
        {
            AutomationElement.AutomationElementInformation info = e.Current;
            Rect r = info.BoundingRectangle;
            string name = info.Name ?? "";
            if (window.Width > 1)
                Log.Write("  probe ({0:0},{1:0}) level {2}: {3} '{4}' {5}", at.X, at.Y, depth,
                    info.ControlType.ProgrammaticName.Replace("ControlType.", ""), name.Length > 30 ? name.Substring(0, 30) : name, Log.Box(r));
            if (info.ProcessId == myPid)
            {
                Log.Write("    -> that is the pet itself");
                return null;
            }
            bool whole = info.ControlType == ControlType.Window || info.ControlType == ControlType.Pane || info.ControlType == ControlType.Document
                || (r.Width > window.Width * 0.7 && r.Height > window.Height * 0.7);
            if (whole)
            {
                if (window.Width > 1) Log.Write("    -> a whole window or page, not a component");
                return null;
            }
            if (!r.IsEmpty && r.Width >= 40 && r.Height >= 14)
            {
                // Layout boxes (a page column, a side panel) are components
                // only on paper: sitting "on" one means sitting anywhere. A
                // box counts when it is the size of a card or a row; a real
                // control (a button, a link, a picture) may be bigger.
                ControlType type = info.ControlType;
                bool box = type == ControlType.Group || type == ControlType.Custom || type == ControlType.List || type == ControlType.Tree
                    || type == ControlType.Table || type == ControlType.DataGrid || type == ControlType.Text || type == ControlType.Tab;
                double area = r.Width * r.Height;
                if (box ? (r.Height > 300 || area > 200000) : area > 700000)
                {
                    if (window.Width > 1) Log.Write("    -> too big to be something to sit on");
                    return null;
                }
                rect = r;
                return e;
            }
            e = TreeWalker.ControlViewWalker.GetParent(e);
        }
        return null;
    }

    void Loop()
    {
        // The very first query loads the automation machinery and can take
        // seconds; spend them now rather than on the first drop.
        try { AutomationElement.FromPoint(new Point(1, 1)); }
        catch (Exception) { }

        while (true)
        {
            ElementFound found = null;
            Point[] points = null;
            Rect window = Rect.Empty;
            AutomationElement current;
            lock (gate)
            {
                if (pending)
                {
                    pending = false;
                    found = callback;
                    points = probes;
                    window = windowRect;
                }
                current = tracked;
            }

            if (found != null)
            {
                AutomationElement hit = null;
                Rect hitRect = Rect.Empty;
                // An app that is closing or not answering simply has nothing to hold.
                try
                {
                    // A browser answers the first question about a spot roughly
                    // (the whole page) and only the second one precisely, so
                    // every spot is asked about twice.
                    for (int i = 0; i < points.Length * 2 && hit == null; i++)
                    {
                        Point at = points[i / 2];
                        if (i % 2 == 1) Thread.Sleep(30);
                        hit = Find(at, window, out hitRect);
                        // A page caught mid-scroll can answer with a component that is no longer there.
                        if (hit != null && !hitRect.Contains(at)) hit = null;
                    }
                }
                catch (Exception) { hit = null; }
                ui.BeginInvoke(new Action(delegate { found(hit, hitRect); }));
            }

            if (current != null)
            {
                bool ok = false;
                Rect r = Rect.Empty;
                try
                {
                    AutomationElement.AutomationElementInformation info = current.Current;
                    r = info.BoundingRectangle;
                    ok = !info.IsOffscreen && !r.IsEmpty && r.Width > 0;
                }
                catch (Exception) { }
                lock (gate)
                {
                    if (tracked == current)
                    {
                        alive = ok;
                        if (ok) trackedRect = r;
                    }
                }
            }
            wake.WaitOne(current != null ? 25 : 500);
        }
    }
}

// The ball of the fetch game: a tiny window of its own, since it flies far
// from the pet. Grabbing it with the mouse is reported to the pet, which
// runs the physics.
class BallWindow : Window
{
    public const double Radius = 15;

    class Face : FrameworkElement
    {
        static readonly string[] Shape = { ".###.", "#####", "#####", "#####", ".###." };

        public Face()
        {
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        protected override void OnRender(DrawingContext dc)
        {
            Brush fill = new SolidColorBrush(Color.FromRgb(245, 200, 80));
            Brush dark = new SolidColorBrush(Color.FromRgb(196, 150, 50));
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 5; c++)
                    if (Shape[r][c] == '#') dc.DrawRectangle(r == 4 || c == 4 ? dark : fill, null, new Rect(c * 6, r * 6, 6, 6));
            dc.DrawRectangle(Brushes.White, null, new Rect(6, 6, 6, 6));
        }
    }

    public BallWindow(Action grabbed, Action dismissed)
    {
        Title = "Claw'd ball";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Radius * 2;
        Height = Radius * 2;
        Cursor = Cursors.Hand;
        Content = new Face();
        SourceInitialized += delegate
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        };
        MouseLeftButtonDown += delegate { grabbed(); };
        MouseRightButtonUp += delegate { dismissed(); };
    }
}

// Draws the sprite on a grid of Cols x Rows units with the feet on the bottom
// edge, centred under a strip that holds the speech bubble. Details (shading,
// eyes, props, particles) sit on a grid of half units.
class PetView : FrameworkElement
{
    public const int Cols = 18;
    public const int Rows = 14;
    // Row of the grid that lines up with a window's top edge while hanging.
    public const int GripRow = 3;
    public const double WindowWidth = 280;
    public const double BubbleStrip = 40;

    public double U = 6;
    public St State = St.Fall;
    // Seconds since launch, and since the current state began.
    public double T, ST;
    public bool Blink;
    public int Look;
    public int Dir = 1;
    // Which side the gripped edge is on while clinging: +1 right, -1 left.
    public int Side = 1;
    public bool Climbing;
    // 1 right after landing, easing to 0.
    public double Squash;
    // Which way the pet was knocked over: +1 right, -1 left.
    public int TripDir = 1;
    // Hiding: only what sticks out above the edge it hides behind is drawn.
    public bool Peek;
    // A card held up over the head: 0 rock, 1 scissors, 2 paper, 3 the
    // "now!" of the reaction game; -1 for none.
    public int Sign = -1;
    public const double PeekRows = 8;
    public const double TripSeconds = 2.2;
    public string Text;
    // Draws the bubble under the feet, for when the strip above is off screen.
    public bool Below;
    // The monitor's horizontal span in this element's coordinates.
    public double ScreenMinX = 0, ScreenMaxX = WindowWidth;

    static readonly Brush Body = Frozen(215, 119, 87);
    static readonly Brush Shade = Frozen(184, 95, 66);
    static readonly Brush Light = Frozen(232, 154, 124);
    static readonly Brush Blush = Frozen(232, 96, 96);
    static readonly Brush Eye = Frozen(26, 25, 21);
    static readonly Brush White = Frozen(255, 255, 255);
    static readonly Brush Zed = Frozen(240, 196, 178);
    static readonly Brush Lid = Frozen(196, 196, 200);
    static readonly Brush LidLight = Frozen(226, 226, 230);
    static readonly Brush LidDark = Frozen(120, 120, 128);
    static readonly Brush Paper = Frozen(250, 249, 245);
    static readonly Brush Blue = Frozen(74, 111, 165);
    static readonly Brush BlueDark = Frozen(50, 78, 120);
    static readonly Brush Yellow = Frozen(245, 200, 80);
    static readonly Brush Pink = Frozen(240, 110, 140);
    static readonly Brush Green = Frozen(110, 190, 120);
    static readonly Brush Brown = Frozen(90, 56, 40);
    static readonly Brush Sky = Frozen(170, 215, 240);
    static readonly Brush Red = Frozen(220, 70, 60);
    static readonly Brush Tan = Frozen(205, 160, 100);
    static readonly Brush Corn = Frozen(250, 235, 180);
    static readonly Brush[] Confetti = { Yellow, Pink, Green, Sky, Body, Paper };

    // Claude's serif has no Cyrillic, so Russian text falls through to Georgia.
    static readonly Typeface Font = new Typeface(new FontFamily("Anthropic Serif Text, Georgia"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static readonly string[] Heart = { ".#.#.", "#####", ".###.", "..#.." };
    static readonly string[] Note = { ".###", ".#..", ".#..", "##..", "##.." };
    static readonly string[] Star = { ".#.", "###", ".#." };
    static readonly string[] Angle = { "..#.#..", ".#...#.", "#.....#", ".#...#.", "..#.#.." };
    static readonly string[] Ring = { ".###.", "#...#", "#...#", "#...#", ".###." };
    static readonly string[] Cross = { "#.#", ".#.", "#.#" };
    static readonly string[] Message = { "####", "####", ".#.." };
    static readonly string[] Ball = { ".##.", "####", "####", ".##." };
    static readonly string[][] Signs = {
        new string[] { ".#####.", "#######", "#######", "#######", "#######", ".#####." },
        new string[] { "#.....#", ".#...#.", "..#.#..", "...#...", "##.#.##", "##...##" },
        new string[] { "#######", "#.....#", "#.###.#", "#.....#", "#.###.#", "#######" },
        new string[] { "..###..", "..###..", "..###..", "..###..", ".......", "..###.." } };

    static Brush Frozen(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public PetView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
    }

    void R(DrawingContext dc, Brush b, double x, double y, double w, double h)
    {
        dc.DrawRectangle(b, null, new Rect(x * U, y * U, w * U, h * U));
    }

    // A small bitmap of half-unit blocks, '#' filled.
    void G(DrawingContext dc, Brush b, double x, double y, string[] rows)
    {
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                if (rows[r][c] == '#') R(dc, b, x + c * 0.5, y + r * 0.5, 0.5, 0.5);
    }

    void Px(DrawingContext dc, Brush b, double x, double y, double w, double h)
    {
        dc.DrawRectangle(b, null, new Rect(x, y, w, h));
    }

    protected override void OnRender(DrawingContext dc)
    {
        DrawBubble(dc);
        if (Peek) dc.PushClip(new RectangleGeometry(new Rect(0, 0, WindowWidth, BubbleStrip + PeekRows * U)));
        dc.PushTransform(new TranslateTransform((WindowWidth - Cols * U) / 2, BubbleStrip));
        // The magnifying glass leads the way, so searching leftwards mirrors
        // the sprite; so does carrying the ball back.
        bool mirror = (State == St.Search || State == St.Fetch) && Dir < 0;
        if (mirror) dc.PushTransform(new ScaleTransform(-1, 1, Cols * U / 2, 0));

        // Knocked over, it tips onto its side in three steps, lies there and
        // tips back up the same way.
        bool tipped = State == St.Trip;
        if (tipped)
        {
            double k = Math.Min(1, Math.Min(ST / 0.15, (TripSeconds - ST) / 0.3));
            k = Math.Round(Math.Max(0, k) * 3) / 3;
            dc.PushTransform(new TranslateTransform(-TripDir * 4 * U * k, -7 * U * k));
            dc.PushTransform(new RotateTransform(TripDir * 90 * k, Cols * U / 2, Rows * U));
        }
        DrawSprite(dc);
        if (tipped)
        {
            dc.Pop();
            dc.Pop();
        }
        if (mirror) dc.Pop();
        dc.Pop();
        if (Peek) dc.Pop();
    }

    // A pixel-art bubble: stepped corners, a border one block thick and a
    // three-step tail, all on a grid of P-sized blocks.
    void DrawBubble(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text)) return;
        const double P = 3;
        FormattedText ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font, 13.5, Eye);
        ft.MaxTextWidth = 210;
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;

        double w = Math.Ceiling(Math.Max(30, ft.Width + 22) / P) * P, h = 30;
        double mid = Math.Round(WindowWidth / 2);
        double cx = Math.Max(ScreenMinX + w / 2 + 2, Math.Min(ScreenMaxX - w / 2 - 2, mid));
        cx = Math.Max(w / 2, Math.Min(WindowWidth - w / 2, cx));
        double x = Math.Round(cx - w / 2);
        double y = Below ? BubbleStrip + Rows * U + 3 * P + 1 : BubbleStrip - 3 * P - h;

        Px(dc, Eye, x + P, y, w - 2 * P, h);
        Px(dc, Eye, x, y + P, w, h - 2 * P);
        Px(dc, Paper, x + 2 * P, y + P, w - 4 * P, h - 2 * P);
        Px(dc, Paper, x + P, y + 2 * P, w - 2 * P, h - 4 * P);

        // The tail steps away from the bubble, towards the pet.
        double tail = Math.Max(x + 5 * P, Math.Min(x + w - 5 * P, mid));
        double step = Below ? -P : P;
        double row = Below ? y - P : y + h;
        Px(dc, Eye, tail - 3 * P, row, 6 * P, P);
        Px(dc, Eye, tail - 2 * P, row + step, 4 * P, P);
        Px(dc, Eye, tail - P, row + 2 * step, 2 * P, P);
        Px(dc, Paper, tail - 2 * P, row - step, 4 * P, P);
        Px(dc, Paper, tail - 2 * P, row, 4 * P, P);
        Px(dc, Paper, tail - P, row + step, 2 * P, P);

        dc.DrawText(ft, new Point(Math.Round(x + (w - ft.Width) / 2), Math.Round(y + (h - ft.Height) / 2)));
    }

    void Limb(DrawingContext dc, double x, double y, double w, double h)
    {
        R(dc, Body, x, y, w, h);
        R(dc, Shade, x, y + h - 0.5, w, 0.5);
    }

    void Torso(DrawingContext dc, double x, double y, double w, double h)
    {
        R(dc, Body, x, y, w, h);
        R(dc, Light, x, y, w - 0.5, 0.5);
        R(dc, Shade, x + w - 0.5, y, 0.5, h);
        R(dc, Shade, x, y + h - 0.5, w, 0.5);
    }

    void DrawEye(DrawingContext dc, EyeKind kind, double x, double y)
    {
        if (kind == EyeKind.Closed) R(dc, Eye, x - 0.25, y + 1.3, 1.5, 0.5);
        else if (kind == EyeKind.Happy)
        {
            R(dc, Eye, x, y + 0.5, 1, 0.5);
            R(dc, Eye, x - 0.5, y + 1, 0.5, 0.5);
            R(dc, Eye, x + 1, y + 1, 0.5, 0.5);
        }
        else if (kind == EyeKind.Wide)
        {
            R(dc, Eye, x - 0.25, y - 0.25, 1.5, 2.5);
            R(dc, White, x - 0.25, y - 0.25, 0.5, 0.5);
        }
        else if (kind == EyeKind.Squint) R(dc, Eye, x, y + 0.8, 1, 1.2);
        else if (kind == EyeKind.Dizzy) G(dc, Eye, x - 0.25, y + 0.25, Cross);
        else
        {
            R(dc, Eye, x, y, 1, 2);
            R(dc, White, x, y, 0.5, 0.5);
        }
    }

    // A particle drifting upwards and fading; phase runs 0 to 1.
    void Rising(DrawingContext dc, Brush b, string[] glyph, double x, double y, double phase, double height)
    {
        dc.PushOpacity(1 - phase);
        G(dc, b, x + Math.Round(Math.Sin(phase * 6) * 1.2) / 2, y - Math.Round(phase * height * 2) / 2, glyph);
        dc.Pop();
    }

    void DrawSprite(DrawingContext dc)
    {
        bool sitting = State == St.Sit || State == St.Sleep || State == St.Code || State == St.Read || State == St.Coffee
            || State == St.Watch || State == St.Game || State == St.Eat;
        bool armsUp = State == St.Hang || State == St.Drag || State == St.Fall;
        int phase = (int)(T * 8) % 2;
        int beat = (int)(T * 4) % 2;
        int slow = (int)(T * 2.5) % 2;
        if (State == St.Search) phase = beat;
        if (State == St.Chase || State == St.Flee) phase = (int)(T * 14) % 2;
        bool walking = State == St.Walk || State == St.Search || State == St.Chase || State == St.Fetch || State == St.Flee;

        // Breathing lifts the top of the body by one pixel.
        double breath = 0;
        if (State == St.Idle || State == St.Wave || State == St.Hang || State == St.Think || State == St.Cling || State == St.Love || sitting)
        {
            double speed = State == St.Sleep ? 1.2 : 2.4;
            if (Math.Sin(T * speed) > 0.4) breath = 1.0 / U;
        }
        if (walking && phase == 1) breath = 1.0 / U;
        if (State == St.Dance && beat == 1) breath = 1;
        if (State == St.Music && slow == 1) breath = 0.5;

        // A stretch grows the body upwards, holds, then lets go.
        double stretch = 0;
        if (State == St.Stretch)
        {
            double p = ST < 0.6 ? ST / 0.6 : ST < 1.8 ? 1 : Math.Max(0, 1 - (ST - 1.8) / 0.5);
            stretch = Math.Round(p * 3) / 2;
        }
        double squash = Math.Round(Squash * 3) / 2;
        bool sipping = State == St.Coffee && ST % 3.5 > 2.6;
        bool munching = (State == St.Watch && ST % 2.5 > 2) || (State == St.Eat && ST % 2 > 1.4);
        // Jumping jacks: every other beat is the star pose.
        bool jack = State == St.Exercise && (int)(ST * 3) % 2 == 1;

        // Sitting down takes three frames: standing, crouched, seated.
        int sitStep = State == St.Sit && ST < 0.3 ? (int)(ST / 0.1) : 2;
        double bodyTop = (sitting ? 4 + sitStep : 4) - breath - stretch + squash;
        double bodyH = 8 + breath + stretch - squash;
        // A stroking hand presses the head down a little on every pass.
        if (State == St.Pet && beat == 0)
        {
            bodyTop += 0.5;
            bodyH -= 0.5;
        }
        double lean = 0;
        if (State == St.Dance) lean = beat == 0 ? -1 : 1;
        if (State == St.Music) lean = slow == 0 ? -0.5 : 0.5;
        if (State == St.Dizzy) lean = Math.Round(Math.Sin(T * 6)) * 0.5;
        double bodyX = 3 + lean - (squash > 0 ? 0.5 : 0);
        double bodyW = 12 + (squash > 0 ? 1 : 0);

        double jump = State == St.Celebrate ? -Math.Round(Math.Abs(Math.Sin(ST * 7)) * 5) / 2 : 0;
        if (jack) jump = -1;
        dc.PushTransform(new TranslateTransform(0, jump * U));

        int[] legX = { 4, 6, 11, 13 };
        if (sitting && sitStep == 2)
        {
            Limb(dc, 1, 13, 2, 1);
            Limb(dc, 15, 13, 2, 1);
        }
        else if (sitting)
        {
            for (int i = 0; i < 4; i++) Limb(dc, legX[i], 12 + sitStep, 1, 2 - sitStep);
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                if (State == St.Hang || State == St.Cling)
                {
                    double rate = State == St.Cling && Climbing ? 9 : 2.6;
                    double sway = Math.Round(Math.Sin(T * rate + (i < 2 ? 0 : 0.6)));
                    R(dc, Body, legX[i], 12, 1, 1);
                    Limb(dc, legX[i] + sway, 13, 1, 1);
                    continue;
                }
                double len = 2;
                if (walking && i % 2 == phase) len = 1;
                if ((State == St.Drag || State == St.Trip) && i % 2 == (int)(T * 10) % 2) len = 1;
                if (State == St.Dance && i % 2 == beat) len = 1;
                if (State == St.Music && i == 3 && slow == 1) len = 1.5;
                if (State == St.Celebrate && jump < -1) len = 1.5;
                Limb(dc, legX[i] + (jack ? (i < 2 ? -1 : 1) : 0), 12, 1, len);
            }
        }

        Torso(dc, bodyX, bodyTop, bodyW, bodyH);

        if (armsUp || jack || (State == St.Stretch && stretch >= 1) || (State == St.Ready && Sign == 3))
        {
            Limb(dc, 1, bodyTop - 2, 2, 4);
            Limb(dc, 15, bodyTop - 2, 2, 4);
        }
        else if (State == St.Celebrate)
        {
            Limb(dc, 1, bodyTop - 2 - (phase == 0 ? 1 : 0), 2, 4);
            Limb(dc, 15, bodyTop - 2 - (phase == 1 ? 1 : 0), 2, 4);
        }
        else if (State == St.Cling)
        {
            // Both hands reach for the edge; the far arm hangs loose.
            int grab = Climbing ? (int)(T * 5) % 2 : 0;
            double nearX = Side > 0 ? 15 : 0;
            Limb(dc, nearX, bodyTop + (grab == 0 ? 0 : 1), 3, 2);
            Limb(dc, nearX, bodyTop + (grab == 0 ? 5 : 4), 3, 2);
            Limb(dc, Side > 0 ? 1 : 15, bodyTop + 4, 2, 2);
        }
        else if (State == St.Wave)
        {
            int wave = (int)(T * 7) % 2;
            Limb(dc, 1, bodyTop + 4, 2, 2);
            Limb(dc, 15, bodyTop + (wave == 0 ? -1 : 0), 2, 3);
        }
        else if (State == St.Dance)
        {
            Limb(dc, 1 + lean, bodyTop + (beat == 0 ? -1 : 4), 2, beat == 0 ? 3 : 2);
            Limb(dc, 15 + lean, bodyTop + (beat == 1 ? -1 : 4), 2, beat == 1 ? 3 : 2);
        }
        else if (State == St.Fetch)
        {
            Limb(dc, 1, bodyTop + 4 - (phase == 0 ? 0.5 : 0), 2, 2);
            Limb(dc, 15, bodyTop + 3, 2, 2);
        }
        else if (State == St.Code || State == St.Walk || State == St.Chase || State == St.Flee)
        {
            // Typing, or swinging while walking: the arms move in turn.
            Limb(dc, 1, bodyTop + 4 - (phase == 0 ? 0.5 : 0), 2, 2);
            Limb(dc, 15, bodyTop + 4 - (phase == 1 ? 0.5 : 0), 2, 2);
        }
        else if (State == St.Think)
        {
            Limb(dc, 1, bodyTop + 4, 2, 2);
            Limb(dc, 13, bodyTop + 5, 4, 1.5);
        }
        else if (State == St.Coffee || State == St.Watch || State == St.Eat)
        {
            Limb(dc, 1, bodyTop + 4, 2, 2);
            Limb(dc, 15, bodyTop + (sipping || munching ? 1.5 : 4), 2, 2);
        }
        else if (State == St.Read || State == St.Game)
        {
            // Holding a book still, or mashing a gamepad.
            double tap = State == St.Game ? 0.5 : 0;
            Limb(dc, 2, bodyTop + 4.5 - (phase == 0 ? tap : 0), 2, 2);
            Limb(dc, 14, bodyTop + 4.5 - (phase == 1 ? tap : 0), 2, 2);
        }
        else if (State == St.Phone)
        {
            Limb(dc, 1, bodyTop + 4, 2, 2);
            Limb(dc, 15, bodyTop + 3.5 - (phase == 0 ? 0.5 : 0), 2, 2);
        }
        else if (State == St.Music)
        {
            Limb(dc, 1 + lean, bodyTop + 4 - (slow == 0 ? 0.5 : 0), 2, 2);
            Limb(dc, 15 + lean, bodyTop + 4 - (slow == 1 ? 0.5 : 0), 2, 2);
        }
        else if (State == St.Search || State == St.Love)
        {
            Limb(dc, 1, bodyTop + (State == St.Love ? 3 : 4), 2, 2);
            Limb(dc, 15, bodyTop + 3, 2, 2);
        }
        else
        {
            Limb(dc, 1 + lean, bodyTop + 4, 2, 2);
            Limb(dc, 15 + lean, bodyTop + 4, 2, 2);
        }

        EyeKind eyes = EyeKind.Open;
        if (State == St.Drag || State == St.Fall) eyes = EyeKind.Wide;
        if (State == St.Wave || State == St.Dance || State == St.Celebrate || State == St.Love || State == St.Music) eyes = EyeKind.Happy;
        if (State == St.Code || State == St.Search) eyes = EyeKind.Squint;
        if (State == St.Dizzy) eyes = EyeKind.Dizzy;
        if (State == St.Watch) eyes = ST % 8 > 6.5 ? EyeKind.Happy : EyeKind.Wide;
        if (State == St.Game) eyes = EyeKind.Squint;
        if (State == St.Eat && munching) eyes = EyeKind.Happy;
        if (State == St.Pet || State == St.Chase || State == St.Fetch) eyes = EyeKind.Happy;
        if (State == St.Trip) eyes = EyeKind.Dizzy;
        if (State == St.Flee) eyes = EyeKind.Wide;
        if (State == St.Ready) eyes = Sign == 3 ? EyeKind.Wide : EyeKind.Squint;
        if (State == St.Sleep || sipping || stretch > 0 || (Blink && eyes == EyeKind.Open)) eyes = EyeKind.Closed;

        double eyeDy = State == St.Code || State == St.Read ? 0.3 : State == St.Phone ? 0.5 : State == St.Think ? -0.8 : 0;
        double eyeDx = State == St.Search || State == St.Phone ? 1 : Look;
        if (State == St.Game) eyeDx = (int)(T * 3) % 3 - 1;
        if (State == St.Read) eyeDx = Math.Round((T * 0.7 % 1 - 0.5) * 2) / 2;
        DrawEye(dc, eyes, 5 + eyeDx + lean, bodyTop + 2 + eyeDy);
        DrawEye(dc, eyes, 12 + eyeDx + lean, bodyTop + 2 + eyeDy);

        if (State == St.Love || State == St.Wave || State == St.Celebrate || State == St.Pet)
        {
            R(dc, Blush, 3.5 + lean, bodyTop + 4.5, 1.5, 0.5);
            R(dc, Blush, 13 + lean, bodyTop + 4.5, 1.5, 0.5);
        }
        // A round mouth for a yawn or a fright.
        if (stretch >= 1 || State == St.Drag || State == St.Fall || State == St.Trip) R(dc, Eye, 8.5, bodyTop + 4.5, 1, 1);

        // The tongue sticks out when the game gets serious.
        if (State == St.Game) R(dc, Pink, 8.5, bodyTop + 4.5, 1, 0.5);

        DrawProps(dc, bodyTop, lean, sipping, munching);
        dc.Pop();
        DrawParticles(dc, bodyTop);
    }

    void DrawProps(DrawingContext dc, double bodyTop, double lean, bool sipping, bool munching)
    {
        if (State == St.Code)
        {
            // A laptop seen from behind, its logo pulsing on the lid.
            R(dc, LidDark, 4.5, 10.5, 9, 3.5);
            R(dc, Lid, 5, 11, 8, 2.5);
            R(dc, LidLight, 5, 11, 8, 0.5);
            if ((int)(T * 2) % 2 == 0) G(dc, Body, 8.25, 11.5, Star);
            else R(dc, Body, 8.75, 12, 0.5, 0.5);
            R(dc, LidDark, 3.5, 13.5, 11, 0.5);
        }
        else if (State == St.Read)
        {
            // An open book held up, cover towards the viewer.
            R(dc, Paper, 4.5, 10.5, 9, 0.5);
            R(dc, Blue, 4, 11, 10, 2.5);
            R(dc, BlueDark, 8.75, 11, 0.5, 2.5);
            R(dc, BlueDark, 4, 13, 10, 0.5);
            R(dc, Paper, 5, 11.75, 2.5, 0.5);
            R(dc, Paper, 10.5, 11.75, 2.5, 0.5);
            if (T % 4 < 0.25) R(dc, Paper, 8.5, 9.5, 1, 1);
        }
        else if (State == St.Coffee)
        {
            double y = bodyTop + (sipping ? 0 : 2.5);
            R(dc, Paper, 15.5, y, 2, 2);
            R(dc, Brown, 15.5, y, 2, 0.5);
            R(dc, Lid, 15.5, y + 1.5, 2, 0.5);
            R(dc, Paper, 17.5, y + 0.5, 0.5, 1);
        }
        else if (State == St.Music)
        {
            R(dc, Eye, 3 + lean, bodyTop - 1, 12, 0.5);
            R(dc, Eye, 3 + lean, bodyTop - 1, 0.5, 1.5);
            R(dc, Eye, 14.5 + lean, bodyTop - 1, 0.5, 1.5);
            R(dc, Blue, 2 + lean, bodyTop + 0.5, 1.5, 3);
            R(dc, BlueDark, 2 + lean, bodyTop + 3, 1.5, 0.5);
            R(dc, Blue, 14.5 + lean, bodyTop + 0.5, 1.5, 3);
            R(dc, BlueDark, 14.5 + lean, bodyTop + 3, 1.5, 0.5);
        }
        else if (State == St.Search)
        {
            dc.PushOpacity(0.6);
            R(dc, Sky, 16, bodyTop + 0.5, 1.5, 1.5);
            dc.Pop();
            G(dc, Eye, 15.5, bodyTop, Ring);
            R(dc, Eye, 16.5, bodyTop + 2.5, 0.5, 1);
        }
        else if (State == St.Fetch)
        {
            G(dc, Yellow, 16, bodyTop + 1.5, Ball);
            R(dc, White, 16.5, bodyTop + 2, 0.5, 0.5);
        }
        else if (State == St.Watch)
        {
            // A striped bucket of popcorn, and a piece on its way up.
            R(dc, Paper, 3.5, 11, 4, 3);
            R(dc, Red, 4, 11, 0.5, 3);
            R(dc, Red, 5.5, 11, 0.5, 3);
            R(dc, Red, 7, 11, 0.5, 3);
            R(dc, Corn, 3.5, 10.5, 4, 0.5);
            if (munching) R(dc, Corn, 15.5, bodyTop + 1, 1, 0.5);
        }
        else if (State == St.Game)
        {
            R(dc, LidDark, 5.5, 11, 7, 2);
            R(dc, LidDark, 5, 12, 1.5, 1.5);
            R(dc, LidDark, 11.5, 12, 1.5, 1.5);
            R(dc, Paper, 6.5, 11.5, 0.5, 1.5);
            R(dc, Paper, 6, 12, 1.5, 0.5);
            R(dc, Pink, 11, 11.5, 0.5, 0.5);
            R(dc, Green, 10.5, 12, 0.5, 0.5);
            R(dc, Sky, 11.5, 12, 0.5, 0.5);
        }
        else if (State == St.Phone)
        {
            R(dc, Eye, 15, bodyTop + 0.5, 2, 3.5);
            R(dc, Sky, 15.5, bodyTop + 1, 1, 2.5);
        }
        else if (State == St.Eat)
        {
            // A cookie that gets shorter with every bite.
            double y = bodyTop + (munching ? 0.5 : 3);
            double left = Math.Max(0.5, 2 - 0.5 * (int)(ST / 2));
            R(dc, Tan, 15.5, y, left, 1.5);
            R(dc, Brown, 15.5, y + 0.5, 0.5, 0.5);
        }
        else if (State == St.Sleep)
        {
            // A nightcap with a pompom.
            R(dc, Blue, 4.5, bodyTop - 1.5, 9, 1.5);
            R(dc, Blue, 7, bodyTop - 2.5, 7, 1);
            R(dc, Blue, 14, bodyTop - 2.5, 1.5, 2);
            R(dc, Paper, 3, bodyTop - 0.5, 12, 1);
            R(dc, Paper, 14.5, bodyTop - 0.5, 1.5, 1.5);
        }
    }

    void DrawParticles(DrawingContext dc, double bodyTop)
    {
        if (Sign >= 0)
        {
            R(dc, Eye, 6.25, bodyTop - 5.75, 5.5, 5);
            R(dc, Paper, 6.75, bodyTop - 5.25, 4.5, 4);
            G(dc, Sign == 3 ? Red : Eye, 7.25, bodyTop - 4.75, Signs[Sign]);
        }
        if (State == St.Sleep)
        {
            int shown = (int)(T * 1.2) % 4;
            for (int i = 0; i < shown; i++)
            {
                double s = 0.4 + 0.15 * i;
                double zx = 13.5 + i * 1.2;
                double zy = 2 - i * 1.5;
                R(dc, Zed, zx, zy, 3 * s, s);
                R(dc, Zed, zx + s, zy + s, s, s);
                R(dc, Zed, zx, zy + 2 * s, 3 * s, s);
            }
        }
        else if (State == St.Code) Rising(dc, Paper, Angle, 13.5, 4.5, T * 0.5 % 1, 4);
        else if (State == St.Coffee)
        {
            if (ST % 3.5 <= 2.6)
                for (int k = 0; k < 2; k++)
                    Rising(dc, White, new string[] { "#" }, 16 + k * 0.8, bodyTop + 2, (T * 0.8 + k * 0.5) % 1, 3);
        }
        else if (State == St.Music)
        {
            Rising(dc, Paper, Note, 15.5, bodyTop - 2, T * 0.45 % 1, 4);
            Rising(dc, Paper, Note, 0.5, bodyTop - 2, (T * 0.45 + 0.5) % 1, 4);
        }
        else if (State == St.Love || State == St.Pet)
        {
            for (int k = 0; k < 3; k++)
                Rising(dc, Pink, Heart, 3.5 + k * 4.5, bodyTop - 2.5, (ST * 0.7 + k / 3.0) % 1, 4);
        }
        else if (State == St.Dizzy)
        {
            for (int k = 0; k < 3; k++)
            {
                double a = T * 4 + k * 2.09;
                G(dc, Yellow, Math.Round((8.25 + 5 * Math.Cos(a)) * 2) / 2, Math.Round((bodyTop - 2.5 + Math.Sin(a)) * 2) / 2, Star);
            }
        }
        else if (State == St.Phone) Rising(dc, Paper, Message, 14.5, bodyTop - 0.5, T * 0.6 % 1, 3.5);
        else if (State == St.Exercise) R(dc, Sky, 16.5, Math.Round((bodyTop + T * 2 % 1 * 3) * 2) / 2, 0.5, 1);
        else if (State == St.Celebrate)
        {
            for (int k = 0; k < 12; k++)
            {
                double p = (ST * 0.9 + k * 0.13) % 1;
                double cx = k * 37 % 18 + Math.Sin(p * 8 + k);
                dc.PushOpacity(1 - p * 0.6);
                R(dc, Confetti[k % Confetti.Length], Math.Round(cx * 2) / 2, Math.Round((-5 + p * 17) * 2) / 2, 0.5, 0.5);
                dc.Pop();
            }
        }
    }
}

class Pet : Window
{
    // Russian first, English second: the Claude font only has Latin letters.
    static readonly string[][] IdleLines = {
        new string[] { "хм...", "как дела?", "кодим?", "ещё один коммит?", "git push?", "не забудь сохраниться", "попей воды", "скучно...", "что делаешь?" },
        new string[] { "hmm...", "how's it going?", "shall we code?", "one more commit?", "git push?", "remember to save", "drink some water", "bored...", "whatcha doing?" } };
    static readonly string[][] CodeLines = {
        new string[] { "пишу код...", "почти готово", "ой, баг", "работает!", "npm install...", "тесты зелёные", "рефакторю", "кто это писал?", "а, это я писал", "компилируется..." },
        new string[] { "writing code...", "almost done", "oops, a bug", "it works!", "npm install...", "tests are green", "refactoring", "who wrote this?", "oh, I wrote this", "compiling..." } };
    static readonly string[][] GrabLines = {
        new string[] { "эй!", "уии!", "поставь на место!", "куда?!", "высоко!" },
        new string[] { "hey!", "wheee!", "put me down!", "where to?!", "too high!" } };
    static readonly string[][] HangLines = {
        new string[] { "держусь!", "не урони", "высоко...", "тут красиво", "лапки устали" },
        new string[] { "hanging on!", "don't drop me", "so high...", "nice view", "my arms hurt" } };
    static readonly string[][] ClickLines = {
        new string[] { "привет!", "хи", "щекотно", "чего?", "я тут", "ага?" },
        new string[] { "hi!", "hehe", "that tickles", "what?", "I'm here", "yes?" } };
    static readonly string[][] SleepLines = {
        new string[] { "я не спал!", "ещё 5 минут...", "а? что?" },
        new string[] { "I wasn't asleep!", "5 more minutes...", "huh? what?" } };
    static readonly string[][] CoffeeLines = {
        new string[] { "кофе...", "горячо!", "ммм", "без кофе никак" },
        new string[] { "coffee...", "hot!", "mmm", "can't work without it" } };
    static readonly string[][] ReadLines = {
        new string[] { "интересно...", "ого", "так вот оно что", "читаю доки" },
        new string[] { "interesting...", "whoa", "so that's how", "reading the docs" } };
    static readonly string[][] MusicLines = {
        new string[] { "ла-ла-ла", "люблю этот трек", "туц-туц" },
        new string[] { "la-la-la", "love this track", "boots and cats" } };
    static readonly string[][] LoveLines = {
        new string[] { "ты хороший", "обнимашки!", "люблю тебя" },
        new string[] { "you're nice", "hugs!", "love you" } };
    static readonly string[][] FoundLines = {
        new string[] { "нашёл!", "хм, ничего", "тут был баг", "ага!" },
        new string[] { "found it!", "hm, nothing", "a bug was here", "aha!" } };
    static readonly string[][] MiscLines = {
        new string[] { "залез!", "ой...", "идея!", "*зевает*", "ура!", "нет окна", "там нет места", "с возвращением!", "пора размяться!", "поздно уже, иди спать" },
        new string[] { "made it!", "ouch...", "idea!", "*yawns*", "hooray!", "no window", "no room there", "welcome back!", "time to stretch!", "it's late, go to bed" } };
    // {0} is the title of what is playing, {1} who made it.
    static readonly string[][] NowPlayingLines = {
        new string[] { "♪ {0}", "о, {1}!", "люблю {1}", "хороший трек", "сделай погромче" },
        new string[] { "♪ {0}", "oh, {1}!", "I love {1}", "good track", "turn it up" } };
    static readonly string[][] WatchLines = {
        new string[] { "что смотрим?", "о, {1}", "{0}", "интересно...", "подвинься, не видно", "дай попкорн" },
        new string[] { "what are we watching?", "oh, {1}", "{0}", "interesting...", "move, I can't see", "pass the popcorn" } };
    static readonly string[][] GameLines = {
        new string[] { "давай, давай!", "я тоже играю", "gg", "ещё катку?", "сзади!" },
        new string[] { "go, go!", "I'm playing too", "gg", "one more round?", "behind you!" } };
    static readonly string[][] ChatLines = {
        new string[] { "кому пишешь?", "передавай привет", "я тоже в чате", "печатает..." },
        new string[] { "who are you texting?", "say hi from me", "I'm chatting too", "typing..." } };
    static readonly string[][] PetLines = {
        new string[] { "мрр", "ещё!", "приятно...", "не останавливайся", "хороший человек" },
        new string[] { "purr", "more!", "that's nice...", "don't stop", "good human" } };
    static readonly string[][] TripLines = {
        new string[] { "ой!", "эй, ноги!", "за что?!", "подножка!" },
        new string[] { "ouch!", "hey, my legs!", "what for?!", "tripped!" } };
    static readonly string[][] SorryLines = {
        new string[] { "ой, извини", "мешаю? отхожу", "прости, пиши-пиши", "уже ушёл" },
        new string[] { "oops, sorry", "in the way? moving", "sorry, keep typing", "I'm gone" } };
    // Lines of the two games; {0} is a number.
    static readonly string[][] PlayLines = {
        new string[] { "кидай!", "поймал!", "ещё!", "наигрался", "прячусь! не смотри", "пссс", "я был тут!", "нашёл! за {0} сек", "уже {0}!",
            "не догонишь!", "поймал! за {0} сек", "не поймал!", "{0} раз!", "жди...", "ЖМИ!", "{0} мс!", "рано!", "заснул?",
            "ничья", "я выиграл!", "ты выиграл", "набивай! кликай по мячу", "рекорд!" },
        new string[] { "throw it!", "got it!", "again!", "that's enough", "hiding! don't look", "psst", "I was here!", "found me! {0} sec", "that's {0}!",
            "can't catch me!", "caught! {0} sec", "too slow!", "{0} hits!", "wait...", "NOW!", "{0} ms!", "too early!", "asleep?",
            "a draw", "I win!", "you win", "keep it up! click the ball", "a record!" } };
    static readonly string[][] SignNames = {
        new string[] { "камень", "ножницы", "бумага" },
        new string[] { "rock", "scissors", "paper" } };
    static readonly string[][] SeatLines = {
        new string[] { "присел", "удобно тут", "хорошее место", "посижу тут", "моё место" },
        new string[] { "sat down", "comfy here", "nice spot", "I'll sit here", "my spot" } };
    static readonly string[][] WorkLines = {
        new string[] { "о, работаем!", "я тоже покодю", "помочь с кодом?" },
        new string[] { "oh, we're working!", "I'll code too", "need help with code?" } };

    // Every state a command or the menu can ask for, with its menu label.
    static readonly string[,] StateNames = {
        { "code", "Кодить" }, { "think", "Думать" }, { "read", "Читать" }, { "coffee", "Пить кофе" },
        { "music", "Слушать музыку" }, { "dance", "Танцевать" }, { "search", "Искать баг" },
        { "watch", "Смотреть видео" }, { "game", "Играть" }, { "phone", "Переписываться" }, { "eat", "Есть печеньку" },
        { "exercise", "Зарядка" }, { "stretch", "Потянуться" }, { "celebrate", "Праздновать" }, { "love", "Обнимашки" },
        { "sleep", "Спать" }, { "sit", "Сидеть" }, { "idle", "Гулять" } };

    public static readonly string CommandFile = Path.Combine(Path.GetTempPath(), "clawd-pet-command.txt");

    readonly PetView view = new PetView();
    readonly Random rng = new Random();
    readonly Stopwatch clock = new Stopwatch();
    readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
    List<WinInfo> wins = new List<WinInfo>();

    double u = 6, W, H;
    double sx = 1, sy = 1;
    // Feet point: horizontal centre and the line the feet stand on, in DIPs.
    double x, y, vx, vy;
    St state = St.Fall;
    double stateT, stateDur, t, lastFrame, frameDt, nextDraw;
    double nextBlink = 2, blinkEnd, nextLook = 1.5, nextScan;
    double walkTarget;
    int dir = 1;
    double hopT = -1;
    double squash;

    // The window the pet stands on, hangs from or clings to; zero for the
    // taskbar and for the edge of the screen.
    IntPtr surf = IntPtr.Zero;
    Rect surfRect;
    Rect ground;
    bool ignoreWindows = true;
    bool sitOnLand;
    bool thrown;
    IntPtr lastForeground = IntPtr.Zero;

    int clingSide = 1;
    int climbDir;
    double nextClimb;

    string bubble;
    double bubbleEnd;
    double nextChat = 8;
    bool chatty = true;
    bool english;

    // What the person is doing, and whether the pet joins in.
    readonly Activity activity = new Activity();
    bool follow = true;
    Ctx ctx = Ctx.None;
    string ctxTitle = "";
    double nextExercise = 2700;

    // Stroking and tripping: where the mouse was a frame ago, and how many
    // times it has changed direction over the head.
    double lastCx, lastCy, sweepX, sweepY, lastStroke, nextDodge;
    int strokes, strokeSign;

    // The fetch game. The ball's position is its centre, in DIPs.
    BallWindow ball;
    BallSt ballSt = BallSt.None;
    double bx, by, bvx, bvy, ballPrevX, ballPrevY, ballSince;
    bool wantBall;
    int fetched;

    // Keeping the ball in the air: every click near it knocks it back up.
    bool juggling, wasDown;
    int juggleCount, juggleBest;

    // Tag: the pet runs from the mouse until it is clicked.
    bool tagPending;
    double tagStart, dashUntil;

    // The reaction game: 0 while waiting for the signal, 1 once it is up.
    int readyPhase;
    double goAt, bestReaction;

    // Rock, paper, scissors: the card it holds up, and the score.
    int cardSign = -1, myWins, itsWins;
    double cardUntil;

    // Hide-and-seek: 0 while it vanishes, 1 once it peeks from its spot.
    int hidePhase;
    double hideStart, nextHint;

    // A component of some app's interface the pet is holding on to.
    ElementTracker tracker;
    bool onElement;
    // How far below the component's top edge the pet sits.
    double seatOffset;
    int dropTicket;
    double dropX, dropY, dropT, nextWarmUp, hoverUntil;

    // While carried and while asking what lies underneath, the pet's window
    // lets hit-testing pass through: otherwise the lookup finds the pet itself.
    IntPtr self = IntPtr.Zero;
    bool seeThrough;
    double seeThroughOff;

    bool pressed, dragging;
    double pressX, pressY, dragOffX, dragOffY, dragPrevX, dragPrevY;

    public Pet()
    {
        Title = "Claw'd";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        Cursor = Cursors.Hand;
        Content = view;
        tracker = new ElementTracker(Dispatcher);
        SetSize(6);
        BuildMenu();

        SourceInitialized += delegate
        {
            self = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(self, Native.GWL_EXSTYLE);
            Native.SetWindowLong(self, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
        };
        Loaded += OnLoaded;
        MouseLeftButtonDown += delegate
        {
            double cx, cy;
            Cursor_(out cx, out cy);
            pressed = true;
            pressX = cx;
            pressY = cy;
        };
    }

    void SetSize(double unit)
    {
        u = unit;
        view.U = unit;
        W = PetView.Cols * unit;
        H = PetView.Rows * unit;
        Width = PetView.WindowWidth;
        Height = H + 2 * PetView.BubbleStrip;
    }

    bool Grounded
    {
        get { return state != St.Hang && state != St.Cling && state != St.Fall && state != St.Drag; }
    }

    void BuildMenu()
    {
        ContextMenu menu = new ContextMenu();
        menu.Items.Add(Item("Сесть на панель задач", delegate
        {
            sitOnLand = true;
            ignoreWindows = true;
            if (Grounded && surf == IntPtr.Zero) Set(St.Sit, 30);
            else StartFall();
        }));
        menu.Items.Add(Item("Сесть на активное окно", delegate { AttachToForeground(false); }));
        menu.Items.Add(Item("Повиснуть на активном окне", delegate { AttachToForeground(true); }));
        menu.Items.Add(Item("Прицепиться к краю экрана", delegate { ClingToScreen(); }));
        menu.Items.Add(new Separator());

        MenuItem anims = new MenuItem();
        anims.Header = "Анимации";
        for (int i = 0; i < StateNames.GetLength(0); i++)
        {
            string name = StateNames[i, 0];
            anims.Items.Add(Item(StateNames[i, 1], delegate { Command("--state", name); }));
        }
        menu.Items.Add(anims);

        MenuItem games = new MenuItem();
        games.Header = "Игры";
        games.Items.Add(Item("Мячик", delegate { StartBall(); }));
        games.Items.Add(Item("Набивание мяча", delegate { StartJuggle(); }));
        games.Items.Add(Item("Прятки", delegate { StartHide(); }));
        games.Items.Add(Item("Догонялки", delegate { StartTag(); }));
        games.Items.Add(Item("Реакция", delegate { StartReaction(); }));
        MenuItem cards = new MenuItem();
        cards.Header = "Камень, ножницы, бумага";
        cards.Items.Add(Item("Камень", delegate { PlayCards(0); }));
        cards.Items.Add(Item("Ножницы", delegate { PlayCards(1); }));
        cards.Items.Add(Item("Бумага", delegate { PlayCards(2); }));
        games.Items.Add(cards);
        games.Items.Add(new Separator());
        games.Items.Add(Item("Убрать мячик", delegate { EndBall(); }));
        menu.Items.Add(games);

        MenuItem talk = new MenuItem();
        talk.Header = "Болтать";
        talk.IsCheckable = true;
        talk.IsChecked = true;
        talk.Click += delegate { chatty = talk.IsChecked; };
        menu.Items.Add(talk);
        MenuItem watch = new MenuItem();
        watch.Header = "Повторять за мной";
        watch.IsCheckable = true;
        watch.IsChecked = true;
        watch.Click += delegate { follow = watch.IsChecked; };
        menu.Items.Add(watch);
        MenuItem lang = new MenuItem();
        lang.Header = "По-английски (шрифт Claude)";
        lang.IsCheckable = true;
        lang.Click += delegate { english = lang.IsChecked; };
        menu.Items.Add(lang);
        MenuItem size = new MenuItem();
        size.Header = "Размер";
        size.Items.Add(Item("Маленький", delegate { SetSize(4); }));
        size.Items.Add(Item("Средний", delegate { SetSize(6); }));
        size.Items.Add(Item("Большой", delegate { SetSize(8); }));
        menu.Items.Add(size);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Убрать Claw'd", delegate { Close(); }));
        ContextMenu = menu;
    }

    static MenuItem Item(string header, Action run)
    {
        MenuItem item = new MenuItem();
        item.Header = header;
        item.Click += delegate { run(); };
        return item;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PresentationSource src = PresentationSource.FromVisual(this);
        if (src != null && src.CompositionTarget != null)
        {
            sx = src.CompositionTarget.TransformToDevice.M11;
            sy = src.CompositionTarget.TransformToDevice.M22;
        }

        // Drop in from above the screen onto the taskbar.
        Rect wa = SystemParameters.WorkArea;
        x = wa.Left + wa.Width * (0.3 + 0.4 * rng.NextDouble());
        y = wa.Top;
        ground = wa;
        Place(true);

        clock.Start();
        // One step per frame the screen draws, so motion matches the refresh rate.
        CompositionTarget.Rendering += delegate { Tick(); };
    }

    // Runs under a lock shared by every launch, so a command being queued
    // never meets the pet reading the queue.
    public static void WithCommandFile(Action work)
    {
        using (Mutex mutex = new Mutex(false, "ClawdDesktopPet_File"))
        {
            try { mutex.WaitOne(2000); }
            catch (AbandonedMutexException) { }
            try { work(); }
            catch (IOException) { }
            finally { mutex.ReleaseMutex(); }
        }
    }

    // Runs the commands later launches queued.
    public void RunCommandFile()
    {
        string[] lines = new string[0];
        WithCommandFile(delegate
        {
            lines = File.ReadAllLines(CommandFile, Encoding.UTF8);
            File.Delete(CommandFile);
        });
        for (int i = 0; i < lines.Length; i++)
        {
            int tab = lines[i].IndexOf('\t');
            if (tab > 0) Command(lines[i].Substring(0, tab), lines[i].Substring(tab + 1));
        }
    }

    void Command(string verb, string arg)
    {
        if (verb == "--say")
        {
            Say(arg, 4);
            return;
        }
        if (verb == "--drop")
        {
            // Lets go of the pet with its feet at a screen pixel, as a drag would.
            string[] at = arg.Split(',');
            double px, py;
            if (at.Length != 2 || !double.TryParse(at[0], out px) || !double.TryParse(at[1], out py)) return;
            x = px / sx;
            y = py / sy;
            vx = 0;
            vy = 0;
            LetFall();
            return;
        }
        if (verb != "--state" || state == St.Drag) return;
        if (arg == "ball") StartBall();
        else if (arg == "throw")
        {
            // A throw from where the ball lies, as if flung with the mouse.
            StartBall();
            bvx = 700;
            bvy = -800;
            ThrowBall();
        }
        else if (arg == "hide") StartHide();
        else if (arg == "pet" && Grounded) Set(St.Pet, 2.5);
        else if (arg == "trip" && Grounded) Set(St.Trip, PetView.TripSeconds);
        else if (arg == "juggle") StartJuggle();
        else if (arg == "tag") StartTag();
        else if (arg == "react") StartReaction();
        else if (arg == "rock") PlayCards(0);
        else if (arg == "scissors") PlayCards(1);
        else if (arg == "paper") PlayCards(2);
        if (arg == "ball" || arg == "throw" || arg == "hide" || arg == "pet" || arg == "trip" || arg == "juggle" || arg == "tag"
            || arg == "react" || arg == "rock" || arg == "scissors" || arg == "paper") return;
        if (arg == "hang") AttachToForeground(true);
        else if (arg == "cling") ClingToScreen();

        if (!Grounded) return;
        if (arg == "code") Set(St.Code, 12 + rng.NextDouble() * 10);
        else if (arg == "think") Set(St.Think, 8);
        else if (arg == "dance") Set(St.Dance, 6);
        else if (arg == "read") Set(St.Read, 12 + rng.NextDouble() * 8);
        else if (arg == "coffee") Set(St.Coffee, 9 + rng.NextDouble() * 6);
        else if (arg == "music") Set(St.Music, 8 + rng.NextDouble() * 5);
        else if (arg == "stretch") Set(St.Stretch, 2.4);
        else if (arg == "celebrate") Set(St.Celebrate, 3);
        else if (arg == "love") Set(St.Love, 2.8);
        else if (arg == "dizzy") Set(St.Dizzy, 2.5);
        else if (arg == "watch") Set(St.Watch, 15 + rng.NextDouble() * 10);
        else if (arg == "game") Set(St.Game, 15 + rng.NextDouble() * 10);
        else if (arg == "phone") Set(St.Phone, 8 + rng.NextDouble() * 6);
        else if (arg == "eat") Set(St.Eat, 7);
        else if (arg == "exercise") Set(St.Exercise, 5);
        else if (arg == "search") StartSearch(double.NaN, double.NaN);
        else if (arg == "sleep") Set(St.Sleep, 60);
        else if (arg == "sit") Set(St.Sit, 30);
        else if (arg == "idle") Set(St.Idle, 0);
    }

    void Say(string text, double seconds)
    {
        bubble = text;
        bubbleEnd = t + seconds;
    }

    string Pick(string[][] lines)
    {
        string[] set = lines[english ? 1 : 0];
        return set[rng.Next(set.Length)];
    }

    string Misc(int index)
    {
        return MiscLines[english ? 1 : 0][index];
    }

    void Cursor_(out double cx, out double cy)
    {
        Native.POINT p;
        Native.GetCursorPos(out p);
        cx = p.X / sx;
        cy = p.Y / sy;
    }

    Rect ToDip(Native.RECT r)
    {
        return new Rect(r.Left / sx, r.Top / sy, Math.Max(0, r.Right - r.Left) / sx, Math.Max(0, r.Bottom - r.Top) / sy);
    }

    Rect MonitorAt(double px, double py, bool workArea)
    {
        Native.POINT p;
        p.X = (int)(px * sx);
        p.Y = (int)(py * sy);
        IntPtr mon = Native.MonitorFromPoint(p, 2);
        Native.MONITORINFO info = new Native.MONITORINFO();
        info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
        if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref info)) return ToDip(workArea ? info.rcWork : info.rcMonitor);
        return SystemParameters.WorkArea;
    }

    bool IsCandidate(IntPtr h)
    {
        if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        uint pid;
        Native.GetWindowThreadProcessId(h, out pid);
        if (pid == myPid) return false;
        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE)) != 0) return false;
        int cloaked;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;
        StringBuilder cls = new StringBuilder(64);
        Native.GetClassName(h, cls, 64);
        string name = cls.ToString();
        return name != "Progman" && name != "WorkerW" && name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd";
    }

    Rect RectOf(IntPtr h)
    {
        Native.RECT r;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, 16) != 0) Native.GetWindowRect(h, out r);
        return ToDip(r);
    }

    // Top-level windows in z-order, topmost first.
    void ScanWindows()
    {
        List<WinInfo> list = new List<WinInfo>();
        Native.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsCandidate(h)) return true;
            WinInfo w = new WinInfo();
            w.H = h;
            w.R = RectOf(h);
            if (w.R.Width < 100 || w.R.Height < 40) return true;
            w.Zoomed = Native.IsZoomed(h);
            list.Add(w);
            return true;
        }, IntPtr.Zero);
        wins = list;

        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && IsCandidate(fg)) lastForeground = fg;
    }

    // Whether the window is what the mouse would hit at the point. The pet's
    // own body can cover the point, so the spots just beside it count too.
    bool VisibleAt(IntPtr h, double px, double py)
    {
        double[] xs = { px, px - W / 2 - 4, px + W / 2 + 4 };
        for (int i = 0; i < xs.Length; i++)
        {
            Native.POINT p;
            p.X = (int)(xs[i] * sx);
            p.Y = (int)(py * sy);
            IntPtr hit = Native.WindowFromPoint(p);
            if (hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GA_ROOT) == h) return true;
        }
        return false;
    }

    bool CanPerch(WinInfo w)
    {
        return !w.Zoomed && w.R.Top - H >= SystemParameters.VirtualScreenTop;
    }

    bool FindLanding(double px, double y0, double y1, out WinInfo best)
    {
        best = new WinInfo();
        bool found = false;
        for (int i = 0; i < wins.Count; i++)
        {
            WinInfo w = wins[i];
            if (!CanPerch(w)) continue;
            if (px < w.R.Left + 3 * u || px > w.R.Right - 3 * u) continue;
            if (w.R.Top < y0 - 0.5 || w.R.Top > y1) continue;
            if (found && w.R.Top >= best.R.Top) continue;
            if (!VisibleAt(w.H, px, w.R.Top + 3)) continue;
            best = w;
            found = true;
        }
        return found;
    }

    // The edge the hands hold: the window's top, or the top of the screen for
    // a window that reaches past it (a maximized one).
    double HangEdge(Rect r)
    {
        Rect m = MonitorAt(x, r.Top + 10, false);
        return Math.Max(r.Top, m.Top);
    }

    void SnapToHang()
    {
        Rect m = MonitorAt(x, surfRect.Top + 10, false);
        double edge = Math.Max(surfRect.Top, m.Top);
        y = Math.Max(edge - PetView.GripRow * u, m.Top) + H;
    }

    bool FindGrip(double px, double handsY, out WinInfo best)
    {
        best = new WinInfo();
        for (int i = 0; i < wins.Count; i++)
        {
            WinInfo w = wins[i];
            if (px < w.R.Left + 2 * u || px > w.R.Right - 2 * u) continue;
            double edge = HangEdge(w.R);
            if (handsY < edge - 6 * u || handsY > edge + 9 * u) continue;
            if (!VisibleAt(w.H, px, edge + 3)) continue;
            best = w;
            return true;
        }
        return false;
    }

    bool FindSide(double px, double midY, out WinInfo best, out int side)
    {
        best = new WinInfo();
        side = 0;
        for (int i = 0; i < wins.Count; i++)
        {
            WinInfo w = wins[i];
            if (w.Zoomed || midY < w.R.Top || midY > w.R.Bottom) continue;
            int s = 0;
            if (Math.Abs(px + W / 2 - w.R.Left) < 5 * u) s = 1;
            else if (Math.Abs(px - W / 2 - w.R.Right) < 5 * u) s = -1;
            if (s == 0) continue;
            double edge = s > 0 ? w.R.Left : w.R.Right;
            if (!VisibleAt(w.H, edge + s * 4, midY)) continue;
            best = w;
            side = s;
            return true;
        }
        return false;
    }

    // Follows the window the pet is attached to; false when it is gone.
    bool TrackSurface(bool allowZoomed)
    {
        if (!Native.IsWindow(surf) || !Native.IsWindowVisible(surf) || Native.IsIconic(surf)) return false;
        Rect r;
        if (onElement)
        {
            if (!tracker.TryGet(out r)) return false;
            r = new Rect(r.X / sx, r.Y / sy, r.Width / sx, r.Height / sy);
        }
        else
        {
            if (!allowZoomed && Native.IsZoomed(surf)) return false;
            r = RectOf(surf);
        }
        x += r.Left - surfRect.Left;
        surfRect = r;
        return true;
    }

    void Set(St s, double duration)
    {
        state = s;
        stateT = 0;
        stateDur = duration;
        view.Look = 0;
        if (s == St.Code || s == St.Coffee || s == St.Read || s == St.Game || s == St.Phone) nextChat = t + 2;
        if (s == St.Music || s == St.Watch) nextChat = t + 6;
    }

    void SeeThrough(bool on)
    {
        if (on == seeThrough || self == IntPtr.Zero) return;
        seeThrough = on;
        int ex = Native.GetWindowLong(self, Native.GWL_EXSTYLE);
        Native.SetWindowLong(self, Native.GWL_EXSTYLE, on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
    }

    void LetGo()
    {
        surf = IntPtr.Zero;
        onElement = false;
        seatOffset = 0;
        tracker.Stop();
    }

    void StartFall()
    {
        LetGo();
        Set(St.Fall, 0);
    }

    void Land(double impact)
    {
        vx = 0;
        vy = 0;
        ignoreWindows = false;
        squash = 1;
        if (thrown && impact > 1500)
        {
            Set(St.Dizzy, 2.5);
            Say(Misc(1), 2);
        }
        else if (sitOnLand) Set(St.Sit, 30);
        else Set(St.Idle, 0.8 + rng.NextDouble());
        sitOnLand = false;
        thrown = false;
        if (wantBall && ballSt != BallSt.None) Set(St.Chase, 30);
        if (tagPending) BeginTag();
    }

    void StartCling(IntPtr window, Rect r, int side)
    {
        LetGo();
        surf = window;
        surfRect = r;
        clingSide = side;
        climbDir = 0;
        nextClimb = t + 1.5;
        vx = 0;
        vy = 0;
        Set(St.Cling, 0);
        Say(Pick(HangLines), 2.5);
    }

    void StartHang(IntPtr window, Rect r)
    {
        LetGo();
        surf = window;
        surfRect = r;
        vx = 0;
        vy = 0;
        SnapToHang();
        Set(St.Hang, 0);
        Say(Pick(HangLines), 2.5);
    }

    // Walks slowly towards a spot with the magnifying glass; NaN bounds mean
    // the current surface's.
    void StartSearch(double lo, double hi)
    {
        if (double.IsNaN(lo))
        {
            if (surf != IntPtr.Zero)
            {
                lo = surfRect.Left + 7 * u;
                hi = surfRect.Right - 7 * u;
            }
            else
            {
                lo = ground.Left + W / 2;
                hi = ground.Right - W / 2;
            }
        }
        double reach = 30 * u * (0.5 + rng.NextDouble());
        dir = x < (lo + hi) / 2 ? 1 : -1;
        if (rng.NextDouble() < 0.3) dir = -dir;
        walkTarget = Math.Max(lo, Math.Min(hi, x + dir * reach));
        Set(St.Search, 14);
    }

    void AttachToForeground(bool hang)
    {
        IntPtr h = lastForeground;
        if (h == IntPtr.Zero || !Native.IsWindow(h) || Native.IsIconic(h))
        {
            Say(Misc(5), 2.5);
            return;
        }
        Rect r = RectOf(h);
        double lo = r.Left + W / 2, hi = r.Right - W / 2;
        double nx = x < lo || x > hi ? (hi > lo ? hi - 4 * u : (r.Left + r.Right) / 2) : x;
        if (hang)
        {
            x = nx;
            StartHang(h, r);
            return;
        }
        if (Native.IsZoomed(h) || r.Top - H < SystemParameters.VirtualScreenTop)
        {
            Say(Misc(6), 2.5);
            return;
        }
        LetGo();
        x = nx;
        surf = h;
        surfRect = r;
        y = r.Top;
        vx = 0;
        vy = 0;
        Set(St.Sit, 30);
    }

    // Asks what interface component lies under the pet; false when there is
    // no app window there to ask. The answer takes a moment, so the pet hangs
    // in the air for a beat before it starts to fall.
    bool RequestElement()
    {
        Native.POINT middle;
        middle.X = (int)(x * sx);
        middle.Y = (int)((y - H / 2) * sy);
        IntPtr root = Native.GetAncestor(Native.WindowFromPoint(middle), Native.GA_ROOT);
        Native.RECT wr;
        if (root == IntPtr.Zero || !IsCandidate(root) || !Native.GetWindowRect(root, out wr))
        {
            Log.Write("  no app window under the pet");
            return false;
        }

        int ticket = ++dropTicket;
        dropX = x;
        dropY = y;
        dropT = t;
        hoverUntil = t + 0.3;
        Rect window = new Rect(wr.Left, wr.Top, Math.Max(1, wr.Right - wr.Left), Math.Max(1, wr.Bottom - wr.Top));
        // Where the feet are first, then a little lower (dropped just above
        // an edge), then the middle of the body.
        Point[] probes = { new Point(middle.X, (y - 1) * sy), new Point(middle.X, (y + 3 * u) * sy), new Point(middle.X, middle.Y) };
        tracker.Lookup(probes, window, delegate(AutomationElement e, Rect r) { OnElement(ticket, root, window, e, r); });
        return true;
    }

    // The answer to RequestElement: on a component the pet sits down, right
    // where it was put; with none it tries the window and screen edges.
    void OnElement(int ticket, IntPtr root, Rect window, AutomationElement element, Rect px)
    {
        if (ticket != dropTicket) return;
        hoverUntil = 0;
        if (state == St.Drag) return;
        SeeThrough(false);

        // The feet can dangle over a neighbouring window; only the dropped-on one counts.
        if (element == null || !window.Contains(new Point(px.X + px.Width / 2, px.Y + px.Height / 2)))
        {
            Log.Write("  no component after {0:0.00}s", t - dropT);
            if (state == St.Fall && t - dropT < 0.6)
            {
                x = dropX;
                y = dropY;
                ResolveDrop();
            }
            return;
        }
        if (t - dropT > 3)
        {
            Log.Write("  component found too late ({0:0.00}s), ignored", t - dropT);
            return;
        }

        Rect r = new Rect(px.X / sx, px.Y / sy, px.Width / sx, px.Height / sy);
        // Close to the top edge it sits on the edge; anywhere else it sits
        // exactly where it was dropped, kept low enough to stay on screen.
        double depth = dropY - r.Top;
        double seat = Math.Abs(depth) <= 0.35 * H ? 0 : depth;
        double floor = MonitorAt(dropX, dropY - H / 2, false).Top + H;
        if (r.Top + seat < floor) seat = floor - r.Top;
        Log.Write("  SIT on {0} after {1:0.00}s, {2:0}px below its top", Log.Box(px), t - dropT, seat * sy);

        LetGo();
        surf = root;
        surfRect = r;
        seatOffset = seat;
        x = Math.Max(r.Left, Math.Min(r.Right, dropX));
        y = r.Top + seat;
        vx = 0;
        vy = 0;
        thrown = false;
        Set(St.Sit, 25);
        Say(Pick(SeatLines), 2.5);
        onElement = true;
        tracker.Track(element, px);
    }

    void ClingToScreen()
    {
        Rect m = MonitorAt(x, y - H / 2, false);
        int side = x > (m.Left + m.Right) / 2 ? 1 : -1;
        y = Math.Min(y, m.Top + m.Height * 0.55);
        if (y < m.Top + H) y = m.Top + H;
        StartCling(IntPtr.Zero, new Rect(), side);
    }

    static bool IsSitting(St s)
    {
        return s == St.Sit || s == St.Sleep || s == St.Code || s == St.Read || s == St.Coffee || s == St.Watch || s == St.Game || s == St.Eat;
    }

    // ---- The mouse over the pet: stroking the head, sweeping the legs.

    void TickTouch(double dt, double cx, double cy, bool down)
    {
        double dx = cx - lastCx, dy = cy - lastCy;
        lastCx = cx;
        lastCy = cy;
        // The sweep speed is averaged over a few frames: one frame alone is
        // too jumpy to tell a flick from an ordinary move.
        if (dt > 0)
        {
            sweepX = 0.6 * sweepX + 0.4 * dx / dt;
            sweepY = 0.6 * sweepY + 0.4 * dy / dt;
        }
        if (down || !Grounded || state == St.Trip || state == St.Hide || state == St.Chase || state == St.Fetch || state == St.Flee || state == St.Ready)
        {
            strokes = 0;
            return;
        }

        double head = y - H + (IsSitting(state) ? 6 : 4) * u;
        bool overHead = Math.Abs(cx - x) < 8 * u && cy > head - 5 * u && cy < head + 3 * u;
        if (overHead)
        {
            // A stroke is a change of direction; three of them in a row is petting.
            int sign = dx > 0.5 ? 1 : dx < -0.5 ? -1 : 0;
            if (sign != 0 && sign != strokeSign)
            {
                strokeSign = sign;
                strokes = t - lastStroke > 0.9 ? 1 : strokes + 1;
                lastStroke = t;
            }
            if (strokes >= 3)
            {
                strokes = 2;
                if (state == St.Pet) stateDur = stateT + 1.2;
                else
                {
                    Set(St.Pet, 1.6);
                    Say(Pick(PetLines), 2);
                }
            }
        }

        bool atLegs = !IsSitting(state) && state != St.Pet && Math.Abs(cx - x) < 8 * u && cy > y - 2.5 * u && cy < y + 1.5 * u;
        // Only a sharp sideways flick knocks it over; moving the mouse past its feet does not.
        if (atLegs && Math.Abs(sweepX) > 2000 && Math.Abs(sweepX) > 2 * Math.Abs(sweepY))
        {
            view.TripDir = sweepX > 0 ? 1 : -1;
            Set(St.Trip, PetView.TripSeconds);
            Say(Pick(TripLines), 2);
        }
    }

    // Gets out of the way when the text being typed is under the pet.
    void TickTyping()
    {
        if (!activity.Typing || t < nextDodge || state == St.Drag || state == St.Fall || state == St.Hide) return;
        Rect c;
        if (!activity.TryCaret(out c)) return;
        double px = (c.X + c.Width / 2) / sx, py = (c.Y + c.Height / 2) / sy;
        if (px < x - W / 2 - 20 || px > x + W / 2 + 20 || py < y - H - 10 || py > y + 10) return;

        nextDodge = t + 6;
        Rect m = MonitorAt(x, y - H / 2, true);
        int away = x >= px ? 1 : -1;
        if (x + away * 260 > m.Right || x + away * 260 < m.Left) away = -away;
        Log.Write("DODGE the text caret at ({0:0},{1:0})", c.X, c.Y);
        StartFall();
        vx = away * 460;
        vy = -560;
        ignoreWindows = true;
        thrown = false;
        hoverUntil = 0;
        Say(Pick(SorryLines), 3);
    }

    // ---- Fetch.

    string Play(int index)
    {
        return PlayLines[english ? 1 : 0][index];
    }

    // Keepy-uppy: the ball drops from above the pet and has to be clicked
    // back up before it lands.
    void StartJuggle()
    {
        if (state == St.Hide) EndHide();
        if (ball == null) ball = new BallWindow(delegate { GrabBall(); }, delegate { EndBall(); });
        if (state == St.Chase || state == St.Fetch) Set(St.Idle, 1);
        bx = x;
        by = y - H - 8 * u;
        bvx = 0;
        bvy = -700;
        ballSt = BallSt.Free;
        ballSince = t;
        wantBall = false;
        juggling = true;
        juggleCount = 0;
        PlaceBall();
        ball.Show();
        Say(Play(21), 3);
    }

    void StartBall()
    {
        if (state == St.Hide) EndHide();
        juggling = false;
        if (ball == null) ball = new BallWindow(delegate { GrabBall(); }, delegate { EndBall(); });
        Rect wa = MonitorAt(x, y - H / 2, true);
        bx = Math.Max(wa.Left + 30, Math.Min(wa.Right - 30, x + (x < (wa.Left + wa.Right) / 2 ? 14 : -14) * u));
        by = wa.Bottom - BallWindow.Radius;
        bvx = 0;
        bvy = 0;
        ballSt = BallSt.Rest;
        ballSince = t;
        wantBall = false;
        PlaceBall();
        ball.Show();
        Say(Play(0), 3);
    }

    void EndBall()
    {
        if (ballSt == BallSt.None) return;
        ballSt = BallSt.None;
        wantBall = false;
        juggling = false;
        ball.Hide();
        if (state == St.Chase || state == St.Fetch) Set(St.Idle, 1);
    }

    void GrabBall()
    {
        if (ballSt == BallSt.None || ballSt == BallSt.Carried || juggling) return;
        ballSt = BallSt.Held;
        ballPrevX = bx;
        ballPrevY = by;
        bvx = 0;
        bvy = 0;
    }

    // The ball has left the hand: the pet goes after it, jumping down first
    // if it sits somewhere high.
    void ThrowBall()
    {
        ballSt = BallSt.Free;
        ballSince = t;
        wantBall = true;
        bvx = Math.Max(-1800, Math.Min(1800, bvx));
        bvy = Math.Max(-1800, Math.Min(1800, bvy));
        if (state == St.Drag || state == St.Fall) return;
        if (state == St.Hide) EndHide();
        if (!Grounded || surf != IntPtr.Zero)
        {
            StartFall();
            ignoreWindows = true;
        }
        else Set(St.Chase, 30);
    }

    void PlaceBall()
    {
        ball.Left = Math.Round((bx - BallWindow.Radius) * sx) / sx;
        ball.Top = Math.Round((by - BallWindow.Radius) * sy) / sy;
    }

    void TickBall(double dt, double cx, double cy, bool down)
    {
        if (ballSt == BallSt.None) return;
        Rect wa = MonitorAt(bx, Math.Max(by - 1, SystemParameters.VirtualScreenTop), true);
        double floor = wa.Bottom - BallWindow.Radius;

        if (juggling)
        {
            // A click anywhere near the ball counts: it is a small, fast target.
            if (down && !wasDown && Math.Abs(cx - bx) < 50 && Math.Abs(cy - by) < 50)
            {
                bvx = Math.Max(-420, Math.Min(420, (bx - cx) * 22)) + (rng.NextDouble() - 0.5) * 140;
                bvy = -1000;
                juggleCount++;
                Say(juggleCount.ToString(), 1);
            }
            if (Grounded) view.Look = bx > x + 2 * u ? 1 : bx < x - 2 * u ? -1 : 0;
        }
        wasDown = down;

        if (ballSt == BallSt.Held)
        {
            bx = cx;
            by = cy;
            if (dt > 0)
            {
                bvx = 0.7 * bvx + 0.3 * (bx - ballPrevX) / dt;
                bvy = 0.7 * bvy + 0.3 * (by - ballPrevY) / dt;
            }
            ballPrevX = bx;
            ballPrevY = by;
            if (!down) ThrowBall();
        }
        else if (ballSt == BallSt.Free)
        {
            bvy = Math.Min(2600, bvy + (juggling ? 1300 : 2200) * dt);
            bx += bvx * dt;
            by += bvy * dt;
            if (juggling && by >= floor)
            {
                // It touched the ground: the round is over, the ball stays to be thrown.
                juggling = false;
                bool record = juggleCount > juggleBest && juggleCount > 1;
                juggleBest = Math.Max(juggleBest, juggleCount);
                Say(string.Format(Play(12), juggleCount) + (record ? " " + Play(22) : ""), 3.5);
                if (record && Grounded) Set(St.Celebrate, 2.5);
            }
            if (bx < wa.Left + BallWindow.Radius || bx > wa.Right - BallWindow.Radius)
            {
                bx = Math.Max(wa.Left + BallWindow.Radius, Math.Min(wa.Right - BallWindow.Radius, bx));
                bvx = -bvx * 0.7;
            }
            if (by >= floor)
            {
                by = floor;
                bvy = Math.Abs(bvy) > 140 ? -bvy * 0.55 : 0;
                bvx *= Math.Max(0, 1 - 3 * dt);
                if (bvy == 0 && Math.Abs(bvx) < 25)
                {
                    bvx = 0;
                    ballSt = BallSt.Rest;
                }
            }
        }
        else if (ballSt == BallSt.Rest && !wantBall && t - ballSince > 60)
        {
            Say(Play(3), 3);
            EndBall();
            return;
        }

        bool shown = ballSt != BallSt.Carried;
        if (shown != ball.IsVisible)
        {
            if (shown) ball.Show();
            else ball.Hide();
        }
        if (shown) PlaceBall();
    }

    // ---- Tag.

    void StartTag()
    {
        if (state == St.Drag || state == St.Fall) return;
        if (state == St.Hide) EndHide();
        EndBall();
        // It needs room to run, so it comes down to the taskbar first.
        if (surf != IntPtr.Zero || !Grounded)
        {
            tagPending = true;
            StartFall();
            ignoreWindows = true;
        }
        else BeginTag();
    }

    void BeginTag()
    {
        tagPending = false;
        tagStart = t;
        dashUntil = 0;
        Set(St.Flee, 30);
        Say(Play(9), 2.5);
    }

    // ---- The reaction game.

    void StartReaction()
    {
        if (!Grounded) return;
        if (state == St.Hide) EndHide();
        readyPhase = 0;
        goAt = t + 2 + rng.NextDouble() * 4;
        Set(St.Ready, 20);
        Say(Play(13), 20);
    }

    // ---- Rock, paper, scissors. A sign beats the one after it: 0 rock,
    // 1 scissors, 2 paper.

    void PlayCards(int mine)
    {
        int its = rng.Next(3);
        cardSign = its;
        cardUntil = t + 3.5;
        string result = Play(18);
        if ((mine + 1) % 3 == its)
        {
            myWins++;
            result = Play(20);
        }
        else if ((its + 1) % 3 == mine)
        {
            itsWins++;
            result = Play(19);
        }
        Say(string.Format("{0}! {1} {2}:{3}", SignNames[english ? 1 : 0][its], result, itsWins, myWins), 3.5);
    }

    // ---- Hide-and-seek.

    void StartHide()
    {
        if (state == St.Drag || state == St.Fall) return;
        EndBall();
        Set(St.Hide, 80);
        hidePhase = 0;
        Say(Play(4), 1.6);
    }

    // Picks a spot to peek from: over the top edge of some window, or over
    // the taskbar.
    void HideSomewhere()
    {
        LetGo();
        List<WinInfo> tops = new List<WinInfo>();
        for (int i = 0; i < wins.Count; i++)
        {
            WinInfo w = wins[i];
            if (!w.Zoomed && w.R.Width > W + 4 * u && w.R.Top - 9 * u >= SystemParameters.VirtualScreenTop) tops.Add(w);
        }
        bool behindWindow = tops.Count > 0 && rng.NextDouble() < 0.7;
        for (int attempt = 0; behindWindow && attempt < 6; attempt++)
        {
            WinInfo w = tops[rng.Next(tops.Count)];
            double px = w.R.Left + W / 2 + rng.NextDouble() * (w.R.Width - W);
            if (!VisibleAt(w.H, px, w.R.Top + 3)) continue;
            surf = w.H;
            surfRect = w.R;
            x = px;
            y = w.R.Top + (PetView.Rows - PetView.PeekRows) * u;
            return;
        }
        ground = SystemParameters.WorkArea;
        x = ground.Left + W + rng.NextDouble() * Math.Max(1, ground.Width - 2 * W);
        y = ground.Bottom + (PetView.Rows - PetView.PeekRows) * u;
    }

    // Steps out from behind its cover and stands on it.
    void EndHide()
    {
        Opacity = 1;
        if (hidePhase == 1) y = surf != IntPtr.Zero ? surfRect.Top : ground.Bottom;
        hidePhase = 0;
        Set(St.Idle, 1.5);
    }

    void TickHide()
    {
        if (hidePhase == 0)
        {
            // Says its line, vanishes, and a moment later is somewhere else.
            if (stateT > 1.6 && Opacity > 0) Opacity = 0;
            if (stateT < 3.4) return;
            ScanWindows();
            HideSomewhere();
            hidePhase = 1;
            hideStart = t;
            nextHint = t + 14;
            bubbleEnd = 0;
            Opacity = 1;
            return;
        }

        double sunk = (PetView.Rows - PetView.PeekRows) * u;
        if (surf != IntPtr.Zero)
        {
            // Its cover moved away or got covered itself: find another.
            if (!TrackSurface(false) || !VisibleAt(surf, x, surfRect.Top + 3))
            {
                HideSomewhere();
                return;
            }
            y = surfRect.Top + sunk;
        }
        else y = ground.Bottom + sunk;

        if (t >= nextLook)
        {
            view.Look = rng.Next(3) - 1;
            nextLook = t + 0.8 + rng.NextDouble() * 1.5;
        }
        if (t >= nextHint)
        {
            Say(Play(5), 1.5);
            nextHint = t + 12;
        }
        if (t - hideStart > 60)
        {
            EndHide();
            Say(Play(6), 3);
        }
    }

    void Click()
    {
        if (state == St.Hide)
        {
            if (hidePhase == 0) return;
            double seconds = t - hideStart;
            EndHide();
            Say(string.Format(Play(7), Math.Round(seconds)), 3.5);
            Set(St.Celebrate, 2.5);
            return;
        }
        if (state == St.Flee)
        {
            Say(string.Format(Play(10), Math.Round(t - tagStart)), 3.5);
            Set(St.Dizzy, 2);
            return;
        }
        if (state == St.Ready)
        {
            if (readyPhase == 0) Say(Play(16), 2.5);
            else
            {
                double ms = Math.Round((t - goAt) * 1000);
                bool record = bestReaction == 0 || ms < bestReaction;
                if (record) bestReaction = ms;
                Say(string.Format(Play(15), ms) + (record ? " " + Play(22) : ""), 3.5);
            }
            Set(St.Wave, 1.5);
            return;
        }
        if (state == St.Fall || state == St.Trip) return;
        if (state == St.Hang || state == St.Cling)
        {
            Say(Pick(HangLines), 2.5);
            return;
        }
        // A second click while it is still waving earns a hug.
        if (state == St.Wave || state == St.Love)
        {
            Say(Pick(LoveLines), 2.5);
            Set(St.Love, 2.8);
            return;
        }
        Say(state == St.Sleep ? Pick(SleepLines) : Pick(ClickLines), 2.5);
        hopT = 0;
        Set(St.Wave, 1.8);
    }

    void BeginDrag(double cx, double cy)
    {
        dragging = true;
        dragOffX = x - cx;
        dragOffY = y - cy;
        dragPrevX = x;
        dragPrevY = y;
        vx = 0;
        vy = 0;
        LetGo();
        SeeThrough(true);
        hopT = -1;
        Set(St.Drag, 0);
        Say(Pick(GrabLines), 2);
    }

    void EndDrag()
    {
        dragging = false;
        thrown = true;
        vx = Math.Max(-1500, Math.Min(1500, vx));
        vy = Math.Max(-1500, Math.Min(1500, vy));
        LetFall();
    }

    // Lets go of the pet where it is: onto a component if one is under it,
    // else onto a window or screen edge, else into a fall.
    void LetFall()
    {
        Log.Write("DROP feet at ({0:0},{1:0}), head at ({0:0},{2:0})", x * sx, y * sy, (y - H) * sy);
        ScanWindows();
        ignoreWindows = false;
        StartFall();
        SeeThrough(true);
        seeThroughOff = t + 3;
        if (RequestElement()) return;
        SeeThrough(false);
        ResolveDrop();
    }

    // Catches a window's top or side edge, or the edge of the screen, if the
    // pet was let go next to one.
    void ResolveDrop()
    {
        double midY = y - H / 2;
        WinInfo w;
        int side;
        if (FindGrip(x, y - H + PetView.GripRow * u, out w))
        {
            Log.Write("  HANG from the top of a window {0}", Log.Box(w.R));
            StartHang(w.H, w.R);
            return;
        }
        if (FindSide(x, midY, out w, out side))
        {
            Log.Write("  CLING to the side of a window {0}", Log.Box(w.R));
            StartCling(w.H, w.R, side);
            return;
        }
        Rect m = MonitorAt(x, midY, false);
        if (x + W / 2 >= m.Right - 4 * u || x - W / 2 <= m.Left + 4 * u)
        {
            Log.Write("  CLING to the edge of the screen");
            if (y < m.Top + H) y = m.Top + H;
            StartCling(IntPtr.Zero, new Rect(), x + W / 2 >= m.Right - 4 * u ? 1 : -1);
            return;
        }
        Log.Write("  FALL: nothing to hold here");
    }

    static string Shorten(string text)
    {
        text = text.Trim();
        return text.Length <= 18 ? text : text.Substring(0, 17).TrimEnd() + "…";
    }

    // Something to say about what the person is doing; null when there is nothing.
    string ContextLine()
    {
        string[][] lines = ctx == Ctx.Music ? NowPlayingLines : ctx == Ctx.Video ? WatchLines : ctx == Ctx.Gaming ? GameLines
            : ctx == Ctx.Chatting ? ChatLines : ctx == Ctx.Coding ? WorkLines : null;
        if (lines == null) return null;
        string title = Shorten(activity.Title), artist = Shorten(activity.Artist);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string line = Pick(lines);
            if ((line.Contains("{0}") && title == "") || (line.Contains("{1}") && artist == "")) continue;
            return string.Format(line, title, artist);
        }
        return null;
    }

    // The animation that goes with what the person is doing.
    bool ContextState(out St s, out double duration)
    {
        s = St.Idle;
        duration = 12 + rng.NextDouble() * 10;
        if (ctx == Ctx.Music) s = St.Music;
        else if (ctx == Ctx.Video) s = St.Watch;
        else if (ctx == Ctx.Gaming) s = St.Game;
        else if (ctx == Ctx.Chatting) s = St.Phone;
        else if (ctx == Ctx.Coding) s = St.Code;
        else if (ctx == Ctx.Away)
        {
            s = St.Sleep;
            duration = 600;
        }
        else return false;
        return true;
    }

    // Picks up a change in what the person is doing, or a new track or video.
    void UpdateContext()
    {
        // In the air or in the middle of a game there is no reacting; the
        // change is picked up afterwards.
        if (state == St.Drag || state == St.Fall || state == St.Hide || state == St.Chase || state == St.Fetch || state == St.Flee || state == St.Ready) return;
        Ctx now = activity.Context;
        string title = activity.Title;
        if (now == ctx && title == ctxTitle) return;
        bool sameActivity = now == ctx;
        bool back = ctx == Ctx.Away;
        ctx = now;
        ctxTitle = title;

        if (back && state == St.Sleep)
        {
            Say(Misc(7), 3);
            hopT = 0;
            Set(St.Wave, 1.8);
            return;
        }
        string line = ContextLine();
        if (chatty && line != null) Say(line, 3.5);
        St s;
        double duration;
        if (Grounded && !sameActivity && ContextState(out s, out duration)) Set(s, duration);
    }

    void ChooseNext(double lo, double hi)
    {
        St joined;
        double joinedFor;
        if (follow && ContextState(out joined, out joinedFor) && (ctx == Ctx.Away || rng.NextDouble() < 0.6))
        {
            Set(joined, joinedFor);
            return;
        }
        double roll = rng.NextDouble();
        bool roomy = hi - lo > 6 * u;
        if (roll < 0.26 && roomy)
        {
            walkTarget = lo + rng.NextDouble() * (hi - lo);
            if (Math.Abs(walkTarget - x) < 4 * u) walkTarget = x < (lo + hi) / 2 ? hi : lo;
            dir = walkTarget > x ? 1 : -1;
            Set(St.Walk, 20);
        }
        else if (roll < 0.33 && roomy) StartSearch(lo, hi);
        else if (roll < 0.41) Set(St.Sit, 6 + rng.NextDouble() * 9);
        else if (roll < 0.54) Set(St.Code, 10 + rng.NextDouble() * 10);
        else if (roll < 0.61) Set(St.Read, 10 + rng.NextDouble() * 8);
        else if (roll < 0.68) Set(St.Coffee, 8 + rng.NextDouble() * 6);
        else if (roll < 0.74) Set(St.Music, 7 + rng.NextDouble() * 5);
        else if (roll < 0.79) Set(St.Think, 4 + rng.NextDouble() * 3);
        else if (roll < 0.84) Set(St.Dance, 4 + rng.NextDouble() * 3);
        else if (roll < 0.89)
        {
            Set(St.Stretch, 2.4);
            Say(Misc(3), 2);
        }
        else if (roll < 0.91) Set(St.Wave, 1.8);
        else if (roll < 0.95) Set(St.Eat, 7);
        else Set(St.Idle, 2 + rng.NextDouble() * 4);
    }

    void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.05, now - lastFrame);
        lastFrame = now;
        frameDt = dt;
        t += dt;
        stateT += dt;
        squash = Math.Max(0, squash - dt / 0.22);

        if (t >= nextScan)
        {
            ScanWindows();
            nextScan = t + 0.15;
        }

        if (t >= nextBlink)
        {
            blinkEnd = t + 0.13;
            nextBlink = t + 2.5 + rng.NextDouble() * 3;
        }

        bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
        double cx, cy;
        Cursor_(out cx, out cy);
        if (pressed)
        {
            // Hidden, any touch finds it; there is no picking it up.
            // In tag and in the reaction game the press itself is the catch.
            if (!down || state == St.Hide || state == St.Flee || state == St.Ready)
            {
                pressed = false;
                Click();
            }
            else if (Math.Abs(cx - pressX) + Math.Abs(cy - pressY) > 4)
            {
                pressed = false;
                BeginDrag(cx, cy);
            }
        }

        // Never stay untouchable if an answer fails to come back.
        if (seeThrough && state != St.Drag && t >= seeThroughOff) SeeThrough(false);
        TickBall(dt, cx, cy, down);
        TickTouch(dt, cx, cy, down);
        TickTyping();
        if (follow) UpdateContext();
        if (t >= nextExercise)
        {
            nextExercise = t + 2700;
            if (Grounded && ctx != Ctx.Away && state != St.Hide && state != St.Chase && state != St.Fetch && state != St.Flee && state != St.Ready)
            {
                Set(St.Exercise, 5);
                Say(Misc(8), 3.5);
            }
        }

        if (state == St.Drag) TickDrag(dt, cx, cy, down);
        else if (state == St.Fall) TickFall(dt);
        else if (state == St.Hang) TickHang();
        else if (state == St.Cling) TickCling(dt);
        else if (state == St.Hide) TickHide();
        else TickGround(dt);

        if (chatty && t >= nextChat && t >= bubbleEnd)
        {
            bool busy = true;
            if (state == St.Code) Say(Pick(CodeLines), 3);
            else if (state == St.Coffee) Say(Pick(CoffeeLines), 3);
            else if (state == St.Read) Say(Pick(ReadLines), 3);
            else if (state == St.Music) Say(ctx == Ctx.Music ? ContextLine() : Pick(MusicLines), 3);
            else if (state == St.Watch) Say(ctx == Ctx.Video ? ContextLine() : Pick(WatchLines).Replace("{0}", "...").Replace("{1}", "!"), 3);
            else if (state == St.Game) Say(Pick(GameLines), 3);
            else if (state == St.Phone) Say(Pick(ChatLines), 3);
            else
            {
                busy = false;
                if (state == St.Hang || state == St.Cling) Say(Pick(HangLines), 3);
                else if (activity.Late && rng.NextDouble() < 0.4) Say(Misc(9), 3.5);
                else if (state == St.Idle || state == St.Walk || state == St.Sit) Say(Pick(IdleLines), 3);
            }
            nextChat = t + (busy ? 5 + rng.NextDouble() * 4 : 20 + rng.NextDouble() * 35);
        }

        // The window follows every frame; the sprite itself is pixel animation
        // and is redrawn at a lower rate, or at once when something switches.
        string text = t < bubbleEnd ? bubble : null;
        if (state == St.Think) text = new string('.', 1 + (int)(stateT * 2) % 3);
        bool blink = t < blinkEnd;
        bool redraw = t >= nextDraw || state != view.State || text != view.Text || blink != view.Blink;
        Place(redraw);
        if (!redraw) return;
        nextDraw = t + 1.0 / 24;
        view.State = state;
        view.T = t;
        view.ST = stateT;
        view.Blink = blink;
        view.Dir = dir;
        view.Side = clingSide;
        view.Climbing = climbDir != 0;
        view.Squash = squash;
        view.Peek = state == St.Hide && hidePhase == 1;
        view.Sign = state == St.Ready && readyPhase == 1 ? 3 : t < cardUntil ? cardSign : -1;
        view.Text = text;
        view.InvalidateVisual();
    }

    void TickDrag(double dt, double cx, double cy, bool down)
    {
        x = cx + dragOffX;
        y = cy + dragOffY;
        if (dt > 0)
        {
            vx = 0.7 * vx + 0.3 * (x - dragPrevX) / dt;
            vy = 0.7 * vy + 0.3 * (y - dragPrevY) / dt;
        }
        dragPrevX = x;
        dragPrevY = y;

        // A browser only starts describing its page once something asks, so
        // ask while the pet is still in the air; by the drop it has an answer.
        if (t >= nextWarmUp)
        {
            nextWarmUp = t + 0.4;
            Point under = new Point(x * sx, (y - 1) * sy);
            tracker.Lookup(new Point[] { under }, new Rect(0, 0, 1, 1), delegate { });
        }
        if (!down && dragging) EndDrag();
    }

    void TickFall(double dt)
    {
        if (t < hoverUntil) return;
        vy = Math.Min(2600, vy + 2200 * dt);
        double ny = y + vy * dt;
        x += vx * dt;
        vx *= Math.Max(0, 1 - 2 * dt);
        double left = SystemParameters.VirtualScreenLeft + W / 2;
        double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - W / 2;
        x = Math.Max(left, Math.Min(right, x));

        WinInfo w;
        Rect wa = MonitorAt(x, Math.Max(ny - 1, SystemParameters.VirtualScreenTop), true);
        if (vy >= 0 && !ignoreWindows && FindLanding(x, y, ny, out w) && w.R.Top < wa.Bottom - 2 * u)
        {
            y = w.R.Top;
            surf = w.H;
            surfRect = w.R;
            Land(vy);
        }
        else if (ny >= wa.Bottom)
        {
            y = wa.Bottom;
            ground = wa;
            surf = IntPtr.Zero;
            x = Math.Max(wa.Left + W / 2, Math.Min(wa.Right - W / 2, x));
            Land(vy);
        }
        else y = ny;
    }

    void TickHang()
    {
        if (!TrackSurface(true) || !VisibleAt(surf, x, HangEdge(surfRect) + 3))
        {
            StartFall();
            return;
        }
        double lo = surfRect.Left + 2 * u, hi = surfRect.Right - 2 * u;
        x = hi > lo ? Math.Max(lo, Math.Min(hi, x)) : (surfRect.Left + surfRect.Right) / 2;
        SnapToHang();
    }

    void TickCling(double dt)
    {
        double edge, yMin, yMax;
        bool onWindow = surf != IntPtr.Zero;
        if (onWindow)
        {
            double oldTop = surfRect.Top;
            if (!TrackSurface(false))
            {
                StartFall();
                return;
            }
            y += surfRect.Top - oldTop;
            edge = clingSide > 0 ? surfRect.Left : surfRect.Right;
            yMin = surfRect.Top + H * 0.4;
            yMax = Math.Min(surfRect.Bottom, MonitorAt(edge, y - 1, true).Bottom);
            if (y > yMax) y = yMax;
            if (!VisibleAt(surf, edge + clingSide * 4, y - H / 2))
            {
                StartFall();
                return;
            }
        }
        else
        {
            Rect m = MonitorAt(x, y - H / 2, false);
            edge = clingSide > 0 ? m.Right : m.Left;
            yMin = m.Top + H;
            yMax = MonitorAt(x, y - H / 2, true).Bottom;
        }
        x = edge - clingSide * W / 2;

        if (t >= nextClimb)
        {
            double roll = rng.NextDouble();
            climbDir = roll < 0.45 ? 0 : roll < 0.85 ? -1 : 1;
            nextClimb = t + 2 + rng.NextDouble() * 3;
        }
        y += climbDir * 5 * u * dt;
        if (y >= yMax)
        {
            y = yMax;
            climbDir = 0;
        }
        if (y > yMin) return;

        // At the top of a window the pet pulls itself up onto it.
        if (onWindow && surfRect.Top - H >= SystemParameters.VirtualScreenTop)
        {
            x = clingSide > 0 ? surfRect.Left + 7 * u : surfRect.Right - 7 * u;
            y = surfRect.Top;
            Set(St.Idle, 1.5);
            Say(Misc(0), 2.5);
        }
        else
        {
            y = yMin;
            climbDir = 0;
        }
    }

    void TickGround(double dt)
    {
        double lo, hi;
        if (surf != IntPtr.Zero)
        {
            if (!TrackSurface(false) || !VisibleAt(surf, x, surfRect.Top + seatOffset + 3))
            {
                Log.Write("  LOST the seat: {0}", onElement && !tracker.Alive ? "the component is gone or scrolled out of view" : "the window is gone or covered");
                StartFall();
                return;
            }
            y = surfRect.Top + seatOffset;
            lo = surfRect.Left + 7 * u;
            hi = surfRect.Right - 7 * u;
        }
        else
        {
            ground = MonitorAt(x, y - 1, true);
            y = ground.Bottom;
            lo = ground.Left + W / 2;
            hi = ground.Right - W / 2;
        }
        if (hi > lo) x = Math.Max(lo, Math.Min(hi, x));
        else if (onElement) x = (surfRect.Left + surfRect.Right) / 2;

        if (state == St.Idle)
        {
            if (t >= nextLook)
            {
                view.Look = rng.Next(3) - 1;
                nextLook = t + 1 + rng.NextDouble() * 2;
            }
            if (stateT > stateDur) ChooseNext(lo, hi);
        }
        else if (state == St.Walk || state == St.Search)
        {
            x += dir * (state == St.Walk ? 9 : 4) * u * dt;
            view.Look = dir;
            bool arrived = dir > 0 ? x >= walkTarget : x <= walkTarget;
            if (arrived || x <= lo || x >= hi || stateT > stateDur)
            {
                if (state == St.Search) Say(Pick(FoundLines), 2.5);
                Set(St.Idle, 1 + rng.NextDouble() * 3);
            }
        }
        else if (state == St.Flee)
        {
            double mx, my;
            Cursor_(out mx, out my);
            double gap = x - mx;
            if (t < dashUntil) x += dir * 110 * u * dt;
            else if (Math.Abs(gap) < 45 * u && Math.Abs(my - (y - H / 2)) < 60 * u)
            {
                dir = gap >= 0 ? 1 : -1;
                // Cornered, it darts back past the mouse instead.
                if ((dir > 0 && x >= hi - 2) || (dir < 0 && x <= lo + 2))
                {
                    dir = -dir;
                    dashUntil = t + 0.8;
                }
                x += dir * 55 * u * dt;
            }
            view.Look = mx > x ? 1 : -1;
            if (stateT > stateDur)
            {
                Say(Play(11), 3);
                Set(St.Celebrate, 2.5);
            }
        }
        else if (state == St.Ready)
        {
            if (readyPhase == 0 && t >= goAt)
            {
                readyPhase = 1;
                hopT = 0;
                Say(Play(14), 3);
            }
            else if (readyPhase == 1 && t > goAt + 3)
            {
                Say(Play(17), 2.5);
                Set(St.Idle, 1);
            }
        }
        else if (state == St.Chase)
        {
            // Runs to the ball and picks it up once it is low enough to reach.
            dir = bx >= x ? 1 : -1;
            view.Look = dir;
            if (Math.Abs(bx - x) > 3 * u) x += dir * 48 * u * dt;
            else if (by > y - 6 * u && ballSt != BallSt.Held)
            {
                double cx, cy;
                Cursor_(out cx, out cy);
                ballSt = BallSt.Carried;
                wantBall = false;
                fetched++;
                walkTarget = hi > lo ? Math.Max(lo, Math.Min(hi, cx)) : x;
                dir = walkTarget >= x ? 1 : -1;
                Say(Play(1), 1.5);
                Set(St.Fetch, 20);
            }
            if (ballSt == BallSt.None || stateT > stateDur) Set(St.Idle, 1);
        }
        else if (state == St.Fetch)
        {
            // Carries the ball to where the mouse was and puts it down.
            x += dir * 16 * u * dt;
            view.Look = dir;
            bx = x + dir * 8 * u;
            by = y - 5 * u;
            bool arrived = dir > 0 ? x >= walkTarget : x <= walkTarget;
            if (arrived || x <= lo || x >= hi || stateT > stateDur)
            {
                Rect wa = MonitorAt(x, y - 1, true);
                bx = Math.Max(wa.Left + 30, Math.Min(wa.Right - 30, x + dir * 10 * u));
                by = wa.Bottom - BallWindow.Radius;
                ballSt = BallSt.Rest;
                ballSince = t;
                Say(fetched % 3 == 0 ? string.Format(Play(8), fetched) : Play(2), 2.5);
                hopT = 0;
                Set(St.Wave, 1.5);
            }
        }
        else if (state == St.Sit)
        {
            if (stateT > stateDur)
            {
                if (rng.NextDouble() < 0.4) Set(St.Sleep, 20 + rng.NextDouble() * 40);
                else Set(St.Idle, 1 + rng.NextDouble() * 2);
            }
        }
        else if (stateT > stateDur)
        {
            if (state == St.Think && rng.NextDouble() < 0.5) Say(Misc(2), 2);
            if (state == St.Celebrate) Say(Misc(4), 2);
            Set(St.Idle, 1 + rng.NextDouble() * 2);
        }
    }

    void Place(bool layout)
    {
        double hop = 0;
        if (hopT >= 0)
        {
            hopT += frameDt;
            double p = hopT / 0.4;
            if (p >= 1) hopT = -1;
            else hop = -4 * (4 * u) * p * (1 - p);
        }
        double left = x - PetView.WindowWidth / 2;
        if (layout)
        {
            Rect m = MonitorAt(x, y - H / 2, false);
            view.ScreenMinX = m.Left - left;
            view.ScreenMaxX = m.Right - left;
            view.Below = y - H - PetView.BubbleStrip < m.Top - 1;
        }
        double newLeft = Math.Round(left * sx) / sx;
        double newTop = Math.Round((y - H - PetView.BubbleStrip + hop) * sy) / sy;
        if (newLeft != Left) Left = newLeft;
        if (newTop != Top) Top = newTop;
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool isFirst, unused;
        EventWaitHandle toggle = new EventWaitHandle(false, EventResetMode.AutoReset, "ClawdDesktopPet_Toggle", out isFirst);
        EventWaitHandle command = new EventWaitHandle(false, EventResetMode.AutoReset, "ClawdDesktopPet_Command", out unused);

        // With arguments this is a message for the running pet, never a new pet.
        if (args.Length > 0)
        {
            if (!isFirst && args.Length >= 2)
            {
                Pet.WithCommandFile(delegate
                {
                    File.AppendAllText(Pet.CommandFile, args[0] + "\t" + args[1] + "\n", Encoding.UTF8);
                });
                command.Set();
            }
            return;
        }

        // A second plain launch asks the running pet to leave, so one button toggles it.
        if (!isFirst)
        {
            toggle.Set();
            return;
        }

        Application app = new Application();
        Pet pet = new Pet();
        Thread waiter = new Thread(delegate()
        {
            WaitHandle[] handles = { toggle, command };
            while (true)
            {
                if (WaitHandle.WaitAny(handles) == 0)
                {
                    app.Dispatcher.BeginInvoke(new Action(delegate { app.Shutdown(); }));
                    return;
                }
                app.Dispatcher.BeginInvoke(new Action(pet.RunCommandFile));
            }
        });
        waiter.IsBackground = true;
        waiter.Start();
        app.Run(pet);
    }
}

// Hallow Gear 1.3 - notifications while gaming, your way.
//   Per alert type (low, very low, charging, fully charged, wired / wireless) and optionally per device: hold until after the game
//   (1.2's behaviour), the Hallow Overlay line only, Gear's own pop-up on another monitor, or a Windows notification anyway.
//   The pop-up never takes focus: WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, shown with SW_SHOWNOACTIVATE, WM_MOUSEACTIVATE
//   answered with MA_NOACTIVATE, nothing in it can take keyboard focus, no taskbar button. It goes to the monitor without the game (or
//   a chosen one) and never to the game's monitor unless "same as the game" is picked.
//   Sound: Windows' own, a short Hallow sound, or silent - outside games and during games separately. Gear's own sound is made quieter
//   in its own samples before it plays on the default device: it never changes the game's or Windows' volume.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HallowGear
{
    // what one alert does while a game runs
    public static class GameModes
    {
        public const string Hold = "hold", Overlay = "overlay", Popup = "popup", Toast = "toast";
        public static readonly string[] All = { Hold, Overlay, Popup, Toast };
        public static readonly string[] Types = { "low", "critical", "charging", "full", "wired" };
        public static bool Valid(string m) { return All.Contains(m ?? ""); }
        public static string TypeOf(string alertKind) { return alertKind == "wireless" ? "wired" : alertKind; }
        public static string TypeLabel(string t)
        {
            switch (t) { case "low": return "Low battery"; case "critical": return "Very low battery"; case "charging": return "Charging started"; case "full": return "Fully charged"; case "wired": return "Wired / wireless"; }
            return t;
        }
        public static string Label(string m)
        {
            switch (m) { case Hold: return "Hold until after"; case Overlay: return "Overlay line only"; case Popup: return "Pop-up, other screen"; case Toast: return "Windows notification"; }
            return "As set above";
        }
        // the single-monitor fallback of "pop-up on another monitor": hold (Overlay line, then after the game), Overlay only, Windows, or the pop-up on that screen
        public static readonly string[] Fallbacks = { Hold, Overlay, Toast, "same" };
        public static string FallbackLabel(string f)
        {
            switch (f) { case Hold: return "Overlay line, then after the game"; case Overlay: return "Overlay line only"; case Toast: return "Windows notification"; case "same": return "Pop-up on that screen"; }
            return f;
        }
        public static string Default(string type) { return type == "critical" ? Popup : Hold; }
    }

    public enum Way { Toast, Hold, Overlay, Popup, PopupSame }
    public class Routed { public GearAlert A; public Way Way; }

    public static class GameRoute
    {
        // device + type beats device (all types) beats the type's setting
        public static string ModeFor(GearSettings s, string key, string alertKind)
        {
            var t = GameModes.TypeOf(alertKind); string m;
            if (key != null && s.DeviceGame != null)
            {
                if (s.DeviceGame.TryGetValue(key + "|" + t, out m) && GameModes.Valid(m)) return m;
                if (s.DeviceGame.TryGetValue(key, out m) && GameModes.Valid(m)) return m;
            }
            switch (t)
            {
                case "low": m = s.GameLow; break; case "critical": m = s.GameCritical; break; case "charging": m = s.GameCharging; break;
                case "full": m = s.GameFull; break; case "wired": m = s.GameWired; break; default: m = GameModes.Hold; break;
            }
            return GameModes.Valid(m) ? m : GameModes.Default(t);
        }
        // popupScreen: a monitor the pop-up may use right now (another monitor than the game's, a chosen one, or the game's when "same" is picked)
        public static Way Decide(GearSettings s, string key, string alertKind, bool game, bool popupScreen)
        {
            if (!game) return Way.Toast;
            var m = ModeFor(s, key, alertKind);
            if (m == GameModes.Overlay) return Way.Overlay;
            if (m == GameModes.Toast) return Way.Toast;
            if (m == GameModes.Popup)
            {
                if (popupScreen) return Way.Popup;
                switch (s.PopupFallback) { case GameModes.Overlay: return Way.Overlay; case GameModes.Toast: return Way.Toast; case "same": return Way.PopupSame; default: return Way.Hold; }
            }
            return Way.Hold;
        }
    }

    // ---------------- monitors
    public class Mon
    {
        public string Device; public int Number; public bool Primary; public IntPtr Handle;
        public int X, Y, W, H;          // the whole monitor (physical pixels)
        public int WX, WY, WW, WH;      // its work area (without the taskbar)
        public string Label { get { return "Screen " + Number + (Primary ? " (main)" : "") + "  " + W + " \u00D7 " + H; } }
        public static Mon Make(string device, int x, int y, int w, int h, bool primary)
        {
            return new Mon { Device = device, Number = NumberOf(device), Primary = primary, X = x, Y = y, W = w, H = h, WX = x, WY = y, WW = w, WH = h - (primary ? 48 : 0) };
        }
        public static int NumberOf(string device)
        {
            var d = device ?? ""; int i = d.Length; while (i > 0 && char.IsDigit(d[i - 1])) i--;
            int n; return i < d.Length && int.TryParse(d.Substring(i), out n) ? n : 0;
        }
        public bool Contains(int x, int y) { return x >= X && x < X + W && y >= Y && y < Y + H; }
    }

    public static class Monitors
    {
        // the pop-up's monitor, or null = none may be used (one monitor, the chosen one missing, every other one holds the game)
        //   pref: "auto" (the monitor without the game), "same" (the game's monitor - only when the user picks it), or a device name
        //   game: the game's monitor (null = unknown: then the main monitor counts as the game's)
        public static Mon Choose(IList<Mon> mons, Mon game, string pref)
        {
            if (mons == null || mons.Count == 0) return null;
            if (pref == "same") return (game != null ? mons.FirstOrDefault(m => m.Device == game.Device) : null) ?? mons.FirstOrDefault(m => m.Primary) ?? mons[0];
            if (mons.Count < 2) return null;
            Func<Mon, bool> notGame = m => game == null ? !m.Primary : m.Device != game.Device;
            if (!string.IsNullOrEmpty(pref) && pref != "auto")
            {
                var chosen = mons.FirstOrDefault(m => m.Device == pref);
                if (chosen == null) return null;          // the chosen monitor isn't connected: fall back
                if (notGame(chosen)) return chosen;       // the game is elsewhere
                // the game is on the chosen monitor: never there - another one instead
            }
            var others = mons.Where(notGame).ToList();
            if (others.Count == 0) return null;
            return others.FirstOrDefault(m => m.Primary) ?? others.OrderBy(m => m.Number).First();
        }
        // a monitor by a point / handle
        public static Mon At(IList<Mon> mons, IntPtr hmon) { return hmon == IntPtr.Zero ? null : mons.FirstOrDefault(m => m.Handle == hmon); }

        // where the index-th pop-up of a stack goes (physical pixels): the corner of the work area, stacked away from it
        public static Point Place(Mon m, string corner, int w, int h, IList<int> heightsBefore, int margin, int gap)
        {
            bool right = corner == null || corner.EndsWith("right"), bottom = corner != null && corner.StartsWith("bottom");
            int x = right ? m.WX + m.WW - margin - w : m.WX + margin;
            int off = 0; if (heightsBefore != null) foreach (var hb in heightsBefore) off += hb + gap;
            int y = bottom ? m.WY + m.WH - margin - h - off : m.WY + margin + off;
            return new Point(x, y);
        }

        // ---- the real monitors (EnumDisplayMonitors), and which one shows the game
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        delegate bool MonEnum(IntPtr h, IntPtr hdc, ref RECT r, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonEnum f, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        const int MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;

        public static List<Mon> All()
        {
            var list = new List<Mon>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
                {
                    var mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    if (GetMonitorInfo(h, ref mi))
                        list.Add(new Mon { Handle = h, Device = mi.szDevice, Number = Mon.NumberOf(mi.szDevice), Primary = (mi.dwFlags & 1) != 0,
                            X = mi.rcMonitor.L, Y = mi.rcMonitor.T, W = mi.rcMonitor.R - mi.rcMonitor.L, H = mi.rcMonitor.B - mi.rcMonitor.T,
                            WX = mi.rcWork.L, WY = mi.rcWork.T, WW = mi.rcWork.R - mi.rcWork.L, WH = mi.rcWork.B - mi.rcWork.T });
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list.OrderBy(m => m.Number).ToList();
        }
        // the game's window (a known game's process main window), else the foreground window -> its monitor
        public static Mon GameMonitor(IList<Mon> mons, IEnumerable<string> gameExes)
        {
            try
            {
                foreach (var n in gameExes ?? new string[0])
                {
                    var bare = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
                    Process[] ps = null;
                    try
                    {
                        ps = Process.GetProcessesByName(bare);
                        foreach (var p in ps) { var w = p.MainWindowHandle; if (w != IntPtr.Zero && IsWindowVisible(w)) { var m = At(mons, MonitorFromWindow(w, MONITOR_DEFAULTTONULL)); if (m != null) return m; } }
                    }
                    catch { }
                    finally { if (ps != null) foreach (var p in ps) p.Dispose(); }
                }
                var fg = GetForegroundWindow();
                if (fg != IntPtr.Zero) return At(mons, MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST));
            }
            catch { }
            return null;
        }
        // tests / --snap: "DEVICE x y w h [primary]" per line
        public static List<Mon> Parse(string text)
        {
            var list = new List<Mon>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var p = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); int x, y, w, h;
                if (p.Length < 5 || !int.TryParse(p[1], out x) || !int.TryParse(p[2], out y) || !int.TryParse(p[3], out w) || !int.TryParse(p[4], out h)) continue;
                list.Add(Mon.Make(p[0], x, y, w, h, p.Length > 5 && p[5] == "primary"));
            }
            return list;
        }
    }

    // ---------------- sound
    public static class GearSound
    {
        public const string Windows = "windows", Hallow = "hallow", Silent = "silent";
        public static readonly string[] Normal = { Windows, Hallow, Silent }, Game = { Silent, Hallow, Windows };
        public static string Label(string s, bool game)
        {
            if (s == Windows) return "Windows sound"; if (s == Hallow) return game ? "Quiet Hallow sound" : "Hallow sound"; return "Silent";
        }
        public static string Valid(string s, string fallback) { return s == Windows || s == Hallow || s == Silent ? s : fallback; }
        // the toast's <audio>: Windows' default notification sound, or silent (the Hallow sound is played by Gear itself, the toast stays quiet)
        public static string ToastAudio(string sound)
        {
            return sound == Windows ? "<audio src=\"ms-winsoundevent:Notification.Default\"/>" : sound == null ? "" : "<audio silent=\"true\"/>";
        }
        public static string WindowsFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav"); } }

        // a short two-note chime, 16-bit mono PCM WAV, its loudness already scaled (volume 0..100)
        public static byte[] Chime(int volume)
        {
            const int rate = 44100; double amp = 0.55 * Math.Max(0, Math.Min(100, volume)) / 100.0;
            var notes = new[] { new[] { 1318.5, 0.085 }, new[] { 1975.5, 0.16 } };
            var samples = new List<short>();
            foreach (var n in notes)
            {
                int count = (int)(rate * n[1]);
                for (int i = 0; i < count; i++)
                {
                    double t = (double)i / rate, env = Math.Min(1.0, i / (rate * 0.004)) * Math.Exp(-t * 18);
                    double v = Math.Sin(2 * Math.PI * n[0] * t) * 0.8 + Math.Sin(2 * Math.PI * n[0] * 2 * t) * 0.2;
                    samples.Add((short)Math.Round(Math.Max(-1, Math.Min(1, v * env * amp)) * 32767));
                }
            }
            return Wav(samples.ToArray(), rate, 1);
        }
        public static byte[] Wav(short[] samples, int rate, int channels)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            int data = samples.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data); foreach (var s in samples) w.Write(s);
            w.Flush(); return ms.ToArray();
        }
        // a 16-bit PCM WAV with its samples scaled to volume % (null when it isn't one)
        public static byte[] Scale(byte[] wav, int volume)
        {
            try
            {
                if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
                var o = (byte[])wav.Clone(); int pos = 12; bool pcm16 = false; double f = Math.Max(0, Math.Min(100, volume)) / 100.0;
                while (pos + 8 <= o.Length)
                {
                    var id = Encoding.ASCII.GetString(o, pos, 4); int len = BitConverter.ToInt32(o, pos + 4); int body = pos + 8;
                    if (len < 0 || body + len > o.Length) len = o.Length - body;
                    if (id == "fmt ") pcm16 = BitConverter.ToInt16(o, body) == 1 && BitConverter.ToInt16(o, body + 14) == 16;
                    if (id == "data")
                    {
                        if (!pcm16) return null;
                        for (int i = body; i + 1 < body + len; i += 2) { var s = (short)Math.Round(BitConverter.ToInt16(o, i) * f); o[i] = (byte)(s & 0xFF); o[i + 1] = (byte)((s >> 8) & 0xFF); }
                        return o;
                    }
                    pos = body + len + (len & 1);
                }
            }
            catch { }
            return null;
        }
        public static int Peak(byte[] wav)
        {
            int peak = 0; if (wav == null || wav.Length <= 44) return 0;
            for (int i = 44; i + 1 < wav.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));
            return peak;
        }
        // what a pop-up / Hallow-sound toast plays: the chime, or Windows' notification sound made quieter; null = nothing
        public static byte[] For(string sound, int volume)
        {
            if (sound == Hallow) return Chime(volume);
            if (sound == Windows) { try { var f = WindowsFile; var s = File.Exists(f) ? Scale(File.ReadAllBytes(f), volume) : null; return s ?? Chime(volume); } catch { return Chime(volume); } }
            return null;
        }
        static System.Media.SoundPlayer player;   // kept so the sound isn't collected while it plays
        // plays on the default audio device (PlaySound, async). Never called by tests or --snap.
        public static void Play(byte[] wav)
        {
            if (wav == null || GearSettings.TestDir != null || Snap.On) return;
            try { var p = new System.Media.SoundPlayer(new MemoryStream(wav)); p.Load(); player = p; p.Play(); } catch { }
        }
    }

    // ---------------- the pop-up: Gear's own small notification window that never takes focus
    public sealed class GearPopup : IDisposable
    {
        public const int WS_POPUP = unchecked((int)0x80000000), WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_APPWINDOW = 0x40000;
        public const int ExStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, SW_SHOWNOACTIVATE = 4, GWL_EXSTYLE = -20, GWL_STYLE = -16;
        const int SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        readonly HwndSource src; public IntPtr Handle { get { return src.Handle; } }
        public Mon Monitor; public DateTime Born = DateTime.Now; public bool Closed; public string Id;
        public event Action Dismissed;
        // created hidden (no WS_VISIBLE), on the given monitor so it gets that monitor's DPI
        public GearPopup(FrameworkElement content, int x, int y)
        {
            var p = new HwndSourceParameters("Hallow Gear") { WindowStyle = WS_POPUP, ExtendedWindowStyle = ExStyle, PositionX = x, PositionY = y, Width = 1, Height = 1 };
            src = new HwndSource(p) { SizeToContent = SizeToContent.WidthAndHeight };
            src.AddHook(Hook);
            src.RootVisual = content;
        }
        static IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }   // a click doesn't activate it (the game keeps focus)
            return IntPtr.Zero;
        }
        public int ExStyleNow { get { return GetWindowLong(Handle, GWL_EXSTYLE); } }
        public int StyleNow { get { return GetWindowLong(Handle, GWL_STYLE); } }
        public bool Visible { get { return IsWindowVisible(Handle); } }
        public int PixelWidth { get { RECT r; GetWindowRect(Handle, out r); return r.R - r.L; } }
        public int PixelHeight { get { RECT r; GetWindowRect(Handle, out r); return r.B - r.T; } }
        public void MoveTo(int x, int y) { SetWindowPos(Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER); }
        // shown without activating: SW_SHOWNOACTIVATE, topmost, no focus change
        public void ShowNoActivate() { ShowWindow(Handle, SW_SHOWNOACTIVATE); SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | 0x2 | SWP_NOACTIVATE | SWP_NOOWNERZORDER); }
        public void Close() { if (Closed) return; Closed = true; try { src.Dispose(); } catch { } var h = Dismissed; if (h != null) h(); }
        public void Dispose() { Close(); }

        // ---- what it looks like: the 1.2 toast in the Hallow look (theme brushes, so light / dark follow Gear's look)
        public static FrameworkElement Card(string kind, string state, string title, string text, Action dismiss, Action open)
        {
            var card = new Border { Width = 360, Background = Ui.R("Panel"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Focusable = false, Cursor = Cursors.Arrow };
            var stripe = new Border { Width = 3, Background = Ui.R(state == "low" || state == "critical" ? "Red" : state == "charging" || state == "full" ? "Green" : "AccentBrush"), CornerRadius = new CornerRadius(6, 0, 0, 6) };
            var dock = new DockPanel(); DockPanel.SetDock(stripe, Dock.Left); dock.Children.Add(stripe);
            var sp = new StackPanel { Margin = new Thickness(14, 10, 12, 12) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var x = new TextBlock { Text = "\u2715", Foreground = Ui.R("Muted"), FontSize = 12, Padding = new Thickness(6, 0, 2, 4), ToolTip = "Dismiss", Cursor = Cursors.Hand, Focusable = false };
            if (dismiss != null) x.MouseLeftButtonUp += (s, e) => { e.Handled = true; dismiss(); };
            DockPanel.SetDock(x, Dock.Right); head.Children.Add(x);
            try { head.Children.Add(new Image { Source = Ui.AppIcon(), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }); } catch { }
            head.Children.Add(new TextBlock { Text = "HALLOW", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Ui.R("AccentBrush"), FontFamily = Ui.Head, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = " GEAR", FontSize = 11.5, FontWeight = FontWeights.Light, Foreground = Ui.R("TextSoft"), FontFamily = Ui.Head, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            var row = new DockPanel();
            if (kind != null) { var icon = DeviceIcons.Element(kind, state ?? "normal", 44, Program.Light, Program.AccentHex); icon.Margin = new Thickness(0, 1, 12, 0); icon.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); }
            var txt = new StackPanel();
            txt.Children.Add(new TextBlock { Text = title ?? "", Foreground = Ui.R("Text"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            txt.Children.Add(new TextBlock { Text = text ?? "", Foreground = Ui.R("TextSoft"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var link = new TextBlock { Text = "Open Hallow Gear", Foreground = Ui.R("AccentBrush"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Focusable = false };
            if (open != null) link.MouseLeftButtonUp += (s, e) => { e.Handled = true; open(); };   // a deliberate click: Gear's window opens (and takes focus)
            txt.Children.Add(link);
            row.Children.Add(txt); sp.Children.Add(row); dock.Children.Add(sp);
            card.Child = dock;
            if (dismiss != null) card.MouseLeftButtonUp += (s, e) => dismiss();   // a click anywhere else dismisses it - without activating anything
            TextElement.SetFontFamily(card, (FontFamily)Application.Current.FindResource("BodyFont"));
            return card;
        }
        // the big number "Identify" flashes on a monitor
        public static FrameworkElement Number(int n, string label)
        {
            var sp = new StackPanel { Margin = new Thickness(36, 18, 36, 22) };
            sp.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 96, FontWeight = FontWeights.Bold, Foreground = Ui.R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = Ui.Head });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Ui.R("TextSoft"), HorizontalAlignment = HorizontalAlignment.Center });
            return new Border { Background = Ui.R("Panel"), BorderBrush = Ui.R("AccentBrush"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Child = sp, Focusable = false };
        }
    }

    // up to 3 pop-ups stacked in the chosen corner; each hides after N seconds
    public class PopupStack
    {
        public readonly List<GearPopup> Live = new List<GearPopup>();
        public const int Max = 3;
        // make: builds the content, given the action that closes this pop-up
        public void Show(Func<Action, FrameworkElement> make, Mon m, string corner, int seconds)
        {
            while (Live.Count >= Max) Live[0].Close();
            GearPopup p = null; var content = make(() => { if (p != null) p.Close(); });
            p = new GearPopup(content, m.WX + 20, m.WY + 20) { Monitor = m };
            p.Dismissed += () => { Live.Remove(p); Reflow(corner); };
            Live.Add(p); Reflow(corner); p.ShowNoActivate();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, seconds)) };
            t.Tick += (s, e) => { t.Stop(); p.Close(); }; t.Start();
        }
        void Reflow(string corner)
        {
            var heights = new List<int>(); int margin = 16, gap = 10;
            for (int i = Live.Count - 1; i >= 0; i--)   // the newest sits in the corner, older ones stack away from it
            {
                var p = Live[i]; if (p.Closed) continue;
                double scale = Math.Max(1.0, p.PixelWidth / 362.0);
                var pt = Monitors.Place(p.Monitor, corner, p.PixelWidth, p.PixelHeight, heights, (int)(margin * scale), (int)(gap * scale));
                p.MoveTo((int)pt.X, (int)pt.Y); heights.Add(p.PixelHeight);
            }
        }
        public void CloseAll() { foreach (var p in Live.ToList()) p.Close(); }
        // Settings' "Identify": a number on every monitor for 2.5 s (only when the user clicks it)
        public static void Identify(IList<Mon> mons)
        {
            foreach (var m in mons)
            {
                var p = new GearPopup(GearPopup.Number(m.Number, m.Label), m.X + 40, m.Y + 40) { Monitor = m };
                p.MoveTo(m.X + (m.W - p.PixelWidth) / 2, m.Y + (m.H - p.PixelHeight) / 2); p.ShowNoActivate();
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) }; t.Tick += (s, e) => { t.Stop(); p.Close(); }; t.Start();
            }
        }
    }
}

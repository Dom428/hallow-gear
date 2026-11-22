// Hallow Gear 1.2 - notifications with the device's icon.
//   1. a real Windows toast (Windows.UI.Notifications) with the icon as appLogoOverride. Toasts need an AppUserModelID on Gear's
//      Start menu shortcut: Gear puts System.AppUserModel.ID = "Hallow.Gear" on its own shortcut (per user, no admin) when it starts.
//   2. no toasts (no shortcut / ID yet, older Windows): the tray balloon with the icon as its large icon (Shell_NotifyIcon, NIIF_USER |
//      NIIF_LARGE_ICON) - from Gear's own tray icon, or, when Hallow Hub hosts Gear's icon, Hub's "notify" (text only) as the last resort.
//   Exactly one of them shows an alert; switched off in Windows Settings (for Gear or for all apps) means nothing is shown.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace HallowGear
{
    // one notification: what it says, and which icon (device kind + state; Kind null = no device icon, Windows shows Gear's)
    public class Note
    {
        public string Title, Text, Kind, State, Key; public string LogoFile, IcoFile;
        public string Sound;   // 1.3: "windows" (Windows' notification sound), "silent", "hallow" (the toast is silent, Gear plays its own); null = no <audio> element
        public string Id { get { return (Title ?? "") + "\n" + (Text ?? ""); } }
    }

    public enum ToastState { On, Off, Unavailable }   // Off = the user switched Gear's (or all) notifications off in Windows
    public enum NoteRoute { Toast, Balloon, Hub, None }

    // which way a notification goes, and the fallbacks when one fails - each alert goes exactly one way
    public class NoteSender
    {
        public Func<ToastState> Toasts = () => ToastState.Unavailable;
        public Func<bool> HubHosts = () => false;
        public Func<Note, bool> ShowToast = n => false, SendHub = n => false;
        public Func<Note, bool> ShowBalloon = n => false;
        public readonly List<string> Log = new List<string>();
        readonly Dictionary<string, DateTime> recent = new Dictionary<string, DateTime>();
        public Func<DateTime> Now = () => DateTime.Now;

        public static NoteRoute Choose(ToastState toasts, bool hubHosts)
        {
            if (toasts == ToastState.On) return NoteRoute.Toast;
            if (toasts == ToastState.Off) return NoteRoute.None;
            return hubHosts ? NoteRoute.Hub : NoteRoute.Balloon;
        }
        // returns the way it went
        public NoteRoute Send(Note n)
        {
            if (!IsNew(n)) { Add("duplicate, not shown: " + n.Title); return NoteRoute.None; }
            ToastState ts; try { ts = Toasts(); } catch { ts = ToastState.Unavailable; }
            bool hub; try { hub = HubHosts(); } catch { hub = false; }
            var route = Choose(ts, hub);
            if (route == NoteRoute.Toast) { if (Try(ShowToast, n)) return Done(NoteRoute.Toast, n); route = Choose(ToastState.Unavailable, hub); }
            if (route == NoteRoute.Hub) { if (Try(SendHub, n)) return Done(NoteRoute.Hub, n); route = NoteRoute.Balloon; }
            if (route == NoteRoute.Balloon && Try(ShowBalloon, n)) return Done(NoteRoute.Balloon, n);
            return Done(NoteRoute.None, n);
        }
        // the same alert twice within 30 s is the same alert (1.3: Gear's pop-up asks this too, so a pop-up and a toast never both show one alert)
        public bool IsNew(Note n)
        {
            var now = Now();
            lock (recent)
            {
                foreach (var k in recent.Where(kv => (now - kv.Value).TotalSeconds > 30).Select(kv => kv.Key).ToList()) recent.Remove(k);
                if (recent.ContainsKey(n.Id)) return false;
                recent[n.Id] = now; return true;
            }
        }
        static bool Try(Func<Note, bool> f, Note n) { try { return f(n); } catch { return false; } }
        NoteRoute Done(NoteRoute r, Note n) { Add(r.ToString().ToLowerInvariant() + ": " + n.Title); return r; }
        void Add(string s) { lock (Log) { Log.Add(s); if (Log.Count > 50) Log.RemoveAt(0); } }
    }

    public static class Toasts
    {
        public const string Aumid = "Hallow.Gear";
        public const string Attribution = "Hallow Gear";

        // the toast's XML (ToastGeneric): title, text, attribution, the icon as appLogoOverride; clicking opens Gear on that device
        public static string Xml(Note n)
        {
            var sb = new StringBuilder();
            sb.Append("<toast launch=\"").Append(Esc(Launch(n.Key))).Append("\" activationType=\"foreground\">");
            sb.Append("<visual><binding template=\"ToastGeneric\">");
            sb.Append("<text hint-maxLines=\"1\">").Append(Esc(n.Title)).Append("</text>");
            sb.Append("<text>").Append(Esc(n.Text)).Append("</text>");
            sb.Append("<text placement=\"attribution\">").Append(Esc(Attribution)).Append("</text>");
            if (!string.IsNullOrEmpty(n.LogoFile)) sb.Append("<image placement=\"appLogoOverride\" src=\"").Append(Esc(FileUri(n.LogoFile))).Append("\" alt=\"").Append(Esc(Alt(n))).Append("\"/>");
            sb.Append("</binding></visual>").Append(GearSound.ToastAudio(n.Sound)).Append("</toast>");
            return sb.ToString();
        }
        static string Esc(string s) { return SecurityElement.Escape(s ?? "") ?? ""; }
        public static string FileUri(string path) { return new Uri(Path.GetFullPath(path)).AbsoluteUri; }
        static string Alt(Note n) { return (n.Kind ?? "device") + (n.State != null && n.State != "normal" ? " " + n.State : ""); }
        public static string Launch(string key) { return string.IsNullOrEmpty(key) ? "gear" : "gear device=" + Uri.EscapeDataString(key); }
        public static string KeyFromLaunch(string args)
        {
            var a = args ?? ""; int i = a.IndexOf("device=", StringComparison.Ordinal);
            return i < 0 ? null : Uri.UnescapeDataString(a.Substring(i + 7).Trim());
        }
        // a newer toast for the same device replaces the older one in the notification centre
        public static string Tag(Note n)
        {
            unchecked { int h = 17; foreach (var ch in n.Key ?? n.Title ?? "") h = h * 31 + ch; return "g" + ((uint)h).ToString("x8"); }
        }

        // the icon matches Windows' own colour mode (toasts follow it, not Gear's look)
        public static bool WindowsLight()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) { var v = k == null ? null : k.GetValue("SystemUsesLightTheme"); return v is int && (int)v != 0; } }
            catch { return false; }
        }
        public static bool Windows10OrLater()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) { var v = k == null ? null : k.GetValue("CurrentMajorVersionNumber"); return v is int && (int)v >= 10; } }
            catch { return false; }
        }
        public static string StartMenuShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Hallow Gear.lnk"); } }

        // toasts can be shown when Gear's Start menu shortcut carries the ID and Windows says notifications are on for it
        public static ToastState State(string shortcut = null)
        {
            try
            {
                if (!Windows10OrLater()) return ToastState.Unavailable;
                var lnk = shortcut ?? StartMenuShortcut;
                if (!File.Exists(lnk) || ShellLink.GetAumid(lnk) != Aumid) return ToastState.Unavailable;
                // 1.2.1: until Gear has shown its first toast, Windows has no settings entry for the ID and asking throws "element not found"
                // (0x80070490) - the shortcut with the ID is what toasts need, so that counts as on
                string s; try { s = WinToast.Setting(Aumid); } catch (Exception e) { if (NotYetKnown(e)) return ToastState.On; throw; }
                if (s == "Enabled") return ToastState.On;
                if (s == "DisabledForApplication" || s == "DisabledForUser" || s == "DisabledByGroupPolicy") return ToastState.Off;
                return ToastState.Unavailable;
            }
            catch { return ToastState.Unavailable; }
        }
        public static bool NotYetKnown(Exception e) { return e != null && (e.HResult == unchecked((int)0x80070490) || NotYetKnown(e.InnerException)); }
        // at start: put the ID on Gear's own Start menu shortcut (only if it points at this exe and doesn't have it yet)
        public static string EnsureShortcutId(string exe, string shortcut = null)
        {
            var lnk = shortcut ?? StartMenuShortcut;
            try
            {
                if (!File.Exists(lnk)) return "no shortcut";
                var target = ShellLink.GetTarget(lnk);
                if (!string.Equals(Path.GetFullPath(target ?? ""), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return "shortcut points elsewhere";
                if (ShellLink.GetAumid(lnk) == Aumid) return "ok";
                ShellLink.SetAumid(lnk, Aumid); return ShellLink.GetAumid(lnk) == Aumid ? "set" : "not set";
            }
            catch (Exception e) { return "error " + e.Message; }
        }

        // ---------------- the tray balloon with a large icon (WinForms' ShowBalloonTip can't give it one)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATAW
        {
            public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage; public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATAW d);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")] static extern IntPtr LoadImage(IntPtr hinst, string name, int type, int cx, int cy, int flags);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        const int NIM_MODIFY = 1, NIF_INFO = 0x10, NIIF_USER = 4, NIIF_LARGE_ICON = 0x20, NIIF_NOSOUND = 0x10, IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, SM_CXICON = 11;
        static readonly Dictionary<string, IntPtr> icons = new Dictionary<string, IntPtr>();   // loaded once per file, kept for the session (a few at most)
        public static int BalloonFlags(bool haveIcon, bool silent = false) { return (haveIcon ? NIIF_USER | NIIF_LARGE_ICON : 0) | (silent ? NIIF_NOSOUND : 0); }
        // on the UI thread; false = couldn't (the caller then uses ShowBalloonTip)
        public static bool BalloonWithIcon(WF.NotifyIcon tray, string title, string text, string icoFile, bool silent = false)
        {
            try
            {
                var t = typeof(WF.NotifyIcon);
                var idF = t.GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var winF = t.GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var win = winF == null ? null : winF.GetValue(tray) as WF.NativeWindow;
                if (idF == null || win == null || win.Handle == IntPtr.Zero || icoFile == null || !File.Exists(icoFile)) return false;
                IntPtr h;
                if (!icons.TryGetValue(icoFile, out h)) { int cx = Math.Max(32, GetSystemMetrics(SM_CXICON)); h = LoadImage(IntPtr.Zero, icoFile, IMAGE_ICON, cx, cx, LR_LOADFROMFILE); if (h != IntPtr.Zero) icons[icoFile] = h; }
                if (h == IntPtr.Zero) return false;
                var d = new NOTIFYICONDATAW { hWnd = win.Handle, uID = (int)idF.GetValue(tray), uFlags = NIF_INFO, szTip = "", szInfo = Cut(text, 255), szInfoTitle = Cut(title, 63), uTimeoutOrVersion = 6000, dwInfoFlags = BalloonFlags(true, silent), hBalloonIcon = h };
                d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATAW));
                return Shell_NotifyIcon(NIM_MODIFY, ref d);
            }
            catch { return false; }
        }
        static string Cut(string s, int n) { s = s ?? ""; return s.Length > n ? s.Substring(0, n) : s; }

        // ---------------- a picture of the toasts (Windows 11 dark style), for the README and for showing without popping real ones
        public static FrameworkElement Preview(IList<Note> notes, ImageSource appIcon)
        {
            Func<string, Brush> B = h => new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
            var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
            var stack = new StackPanel { Margin = new Thickness(24) };
            foreach (var n in notes)
            {
                var card = new Border { Width = 364, Background = B("#2C2C2C"), BorderBrush = B("#3D3D3D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 16), Margin = new Thickness(0, 0, 0, 12) };
                var sp = new StackPanel();
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
                var dots = new TextBlock { Text = "\u2022\u2022\u2022", Foreground = B("#9A9A9A"), FontSize = 11, Margin = new Thickness(0, 0, 14, 0) };
                var x = new TextBlock { Text = "\u2715", Foreground = B("#9A9A9A"), FontSize = 11 };
                DockPanel.SetDock(x, Dock.Right); DockPanel.SetDock(dots, Dock.Right); head.Children.Add(x); head.Children.Add(dots);
                if (appIcon != null) head.Children.Add(new Image { Source = appIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new TextBlock { Text = "Hallow Gear", Foreground = B("#E0E0E0"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(head);
                var row = new DockPanel();
                if (!string.IsNullOrEmpty(n.LogoFile) && File.Exists(n.LogoFile))
                {
                    var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(n.LogoFile); bi.EndInit();
                    var logo = new Image { Source = bi, Width = 48, Height = 48, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
                    RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality); DockPanel.SetDock(logo, Dock.Left); row.Children.Add(logo);
                }
                var txt = new StackPanel();
                txt.Children.Add(new TextBlock { Text = n.Title, Foreground = B("#FFFFFF"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = font });
                txt.Children.Add(new TextBlock { Text = n.Text, Foreground = B("#C8C8C8"), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), FontFamily = font });
                txt.Children.Add(new TextBlock { Text = Attribution, Foreground = B("#9A9A9A"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), FontFamily = font });
                row.Children.Add(txt); sp.Children.Add(row);
                card.Child = sp; stack.Children.Add(card);
            }
            var root = new Border { Background = B("#202020"), Child = stack };
            TextElement.SetFontFamily(root, font);
            return root;
        }
        public static void SavePng(FrameworkElement el, string path, double scale)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); var sz = el.DesiredSize;
            el.Arrange(new Rect(0, 0, sz.Width, sz.Height)); el.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(sz.Width * scale), (int)Math.Ceiling(sz.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var f = File.Create(path)) enc.Save(f);
        }
        // the alerts in the preview come from the real alert rules (same texts as the toasts)
        public static List<Note> PreviewNotes(Func<string, string, string> logo)
        {
            var list = new List<Note>(); var r = new AlertRules(); var t0 = DateTime.Now;
            Action<GearDevice, Action<GearDevice>, string> run = (d, change, want) =>
            {
                var a = new BatteryAlerts(); a.Update(d, r, t0); change(d);
                var hit = a.Update(d, r, t0.AddMinutes(1)).FirstOrDefault(x => x.Kind == want);
                if (hit == null) return;
                var k = DeviceIcons.KindOf(d.Kind); var st = DeviceIcons.StateOf(hit.Kind);
                list.Add(new Note { Title = hit.Title, Text = hit.Text, Kind = k, State = st, Key = d.Key, LogoFile = logo(k, st) });
            };
            Func<string, string, string, bool, int, GearDevice> D = (key, kind, name, bt, pct) => new GearDevice { Key = key, Kind = kind, Name = name, Bluetooth = bt, Battery = pct, Via = bt ? "Bluetooth" : "LIGHTSPEED receiver" };
            run(D("mouse:pro x superlight 2", "mouse", "PRO X SUPERLIGHT 2", false, 35), d => d.Battery = 20, "low");
            run(D("mouse:pro x superlight 2", "mouse", "PRO X SUPERLIGHT 2", false, 35), d => d.Battery = 9, "critical");
            run(D("mouse:pro x superlight 2", "mouse", "PRO X SUPERLIGHT 2", false, 18), d => { d.Charging = true; d.Battery = 18; }, "charging");
            run(D("headset:astro a50", "headset", "ASTRO A50", false, 40), d => d.Battery = 19, "low");
            run(D("headset:astro a50", "headset", "ASTRO A50", false, 96), d => { d.Full = true; d.Battery = 100; d.External = true; }, "full");
            run(D("bt:0a1b2c3d4e01", "earbuds", "Galaxy Buds2 Pro", true, 30), d => d.Battery = 15, "low");
            run(D("bt:0a1b2c3d4e02", "controller", "Xbox Wireless Controller", true, 30), d => d.Battery = 20, "low");
            return list;
        }
    }

    // the WinRT toast calls - kept in one class (and out of methods that run on older Windows, where these types don't exist)
    public static class WinToast
    {
        static readonly List<object> alive = new List<object>();   // toasts stay referenced so their Activated (click) still arrives
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Setting(string aumid) { return Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(aumid).Setting.ToString(); }
        // builds the toast without showing it (tests use this)
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object Create(string xml, string tag, Action<string> clicked)
        {
            var doc = new Windows.Data.Xml.Dom.XmlDocument(); doc.LoadXml(xml);
            var t = new Windows.UI.Notifications.ToastNotification(doc) { Tag = tag, Group = "gear" };
            if (clicked != null) t.Activated += (s, e) => { var a = e as Windows.UI.Notifications.ToastActivatedEventArgs; try { clicked(a != null ? a.Arguments : ""); } catch { } };
            return t;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string XmlOf(object toast) { return ((Windows.UI.Notifications.ToastNotification)toast).Content.GetXml(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Show(string aumid, object toast)
        {
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(aumid).Show((Windows.UI.Notifications.ToastNotification)toast);
            lock (alive) { alive.Add(toast); if (alive.Count > 12) alive.RemoveAt(0); }
        }
    }

    // the System.AppUserModel.ID of a .lnk (IShellLinkW + IPropertyStore), per user, no admin
    public static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW { void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags); }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            void GetCount(out uint count); void GetAt(uint i, out PropertyKey key);
            void GetValue([In] ref PropertyKey key, out PropVariant v); void SetValue([In] ref PropertyKey key, [In] ref PropVariant v); void Commit();
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct PropertyKey { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt; ushort r1, r2, r3; public IntPtr p; IntPtr p2; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant v);
        static PropertyKey AumidKey = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
        const ushort VT_LPWSTR = 31; const int STGM_READ = 0, STGM_READWRITE = 2;

        public static string GetTarget(string lnk)
        {
            var o = new CShellLink();
            try { ((IPersistFile)o).Load(lnk, STGM_READ); var sb = new StringBuilder(1024); ((IShellLinkW)o).GetPath(sb, sb.Capacity, IntPtr.Zero, 0); return sb.ToString(); }
            finally { Marshal.ReleaseComObject(o); }
        }
        public static string GetAumid(string lnk)
        {
            var o = new CShellLink();
            try
            {
                ((IPersistFile)o).Load(lnk, STGM_READ);
                PropVariant v; ((IPropertyStore)o).GetValue(ref AumidKey, out v);
                try { return v.vt == VT_LPWSTR ? Marshal.PtrToStringUni(v.p) : null; } finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(o); }
        }
        public static void SetAumid(string lnk, string aumid)
        {
            var o = new CShellLink();
            try
            {
                ((IPersistFile)o).Load(lnk, STGM_READWRITE);
                var v = new PropVariant { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(aumid) };
                try { var ps = (IPropertyStore)o; ps.SetValue(ref AumidKey, ref v); ps.Commit(); } finally { PropVariantClear(ref v); }
                ((IPersistFile)o).Save(lnk, true);
            }
            finally { Marshal.ReleaseComObject(o); }
        }
    }
}

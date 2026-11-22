// Hallow Gear - battery, charging and wired / wireless for your Logitech gear: a LIGHTSPEED mouse (HID++ 2.0), the ASTRO A50 (Gen 5)
// headset (its base station's documented battery GET) and wired keyboards (present / wired). It only READS: every message it can
// send is on a read-only allow-list (Hidpp.cs, A50.cs). It runs next to Logitech G HUB without getting in its way (shared handles,
// a few small reads a minute), polls gently at Below Normal priority, and while a game runs it doesn't ask the mouse anything -
// it only listens - and holds its alerts (a low battery goes to Hallow Overlay as a quiet line instead).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Hallow Gear")]
[assembly: AssemblyProduct("Hallow Gear")]
[assembly: AssemblyDescription("Battery and charging of your Logitech gear and Bluetooth devices")]
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace HallowGear
{
    public static class Program
    {
        public const string Version = "1.2.1";
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [STAThread]
        public static void Main(string[] args)
        {
            int ci = Array.IndexOf(args, "--cmd");
            if (ci >= 0) { Environment.Exit(Family.RunCmd(ci + 1 < args.Length ? args[ci + 1] : "")); return; }
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            int pi = Array.IndexOf(args, "--probe");
            if (pi >= 0) { Environment.Exit(Probe(pi + 1 < args.Length ? args[pi + 1] : null)); return; }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length)
            {
                Snap.On = true; GearSettings.Frozen = true;
                var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(sa);
                SnapAll(args[si + 1]); return;
            }
            var leaf = GearSettings.TestDir != null ? "Test" + Path.GetFileName(GearSettings.TestDir.TrimEnd('\\')) : "";
            bool created;
            var mutex = new Mutex(true, @"Local\HallowGear" + leaf, out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowGearShow" + leaf);
            if (!created) { showEvent.Set(); return; }
            Crash.Install();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            app.DispatcherUnhandledException += (s, e) => { Crash.Write(e.Exception); e.Handled = true; Environment.Exit(1); };
            var c = new Controller(args.Contains("--tray"));
            new Thread(() => { while (showEvent.WaitOne()) app.Dispatcher.BeginInvoke(new Action(() => c.ShowMain(null))); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        public static string AccentHex = Accent.Default;
        public static bool Light; public static bool? ForceLight;
        public static void LoadTheme(Application app, GearSettings st = null)
        {
            if (st == null) st = GearSettings.Load();
            AccentHex = Accent.Current(st.UseHallowAccent);
            Light = ForceLight ?? Look.Current(st);
            var x = Res("HallowGear.Theme.xaml"); if (Light) x = Look.ToLight(x);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Accent.ApplyTo(x, Light ? Look.DarkAccent(AccentHex) : Ui.Readable(AccentHex))));
        }
        static string Res(string name) { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) using (var r = new StreamReader(s)) return r.ReadToEnd(); }

        // --probe <log file>: ONE read-only pass over the real gear (names and battery), printed and written to the file. Refuses while a game runs.
        static int Probe(string file)
        {
            var sb = new StringBuilder();
            Action<string> P = l => { sb.AppendLine(l); try { Console.WriteLine(l); } catch { } };
            P("Hallow Gear " + Version + " read-only probe, " + DateTime.Now.ToString("s"));
            var game = GameWatch.Running(new List<string>());
            if (game != null) { P("A game is running (" + game + ") - nothing was read."); Write(file, sb); return 3; }
            var s = GearSettings.Load(); GearSettings.Frozen = true;   // the probe never saves settings
            using (var src = new LogiSource(s))
            {
                var list = src.Read(true, true, true);
                foreach (var d in list)
                    P(string.Format("{0,-9} {1,-28} battery {2,-5} state {3,-14} via {4}{5}{6}", d.Kind, d.Name, d.Battery.HasValue ? d.Battery + "%" : "-", d.State, d.Via,
                        d.Source != null ? "  [" + d.Source + (d.Estimated ? ", estimated" : "") + "]" : "", d.Note != null ? "  (" + d.Note + ")" : ""));
                P("--- log (every request sent is an allow-listed read):");
                foreach (var l in src.Log) P(l);
                foreach (var l in src.SentSummary()) P("sent: " + l);
            }
            // 1.1: Bluetooth - paired devices and the battery Windows knows (no pairing, no connecting; nothing is kept listening)
            P("--- Bluetooth (paired devices, read-only):");
            using (var bt = new BtSource { Listen = false })
            {
                var entries = bt.Read();
                if (entries.Count == 0) P("no paired Bluetooth devices");
                foreach (var e in entries)
                {
                    var d = BtProto.ToDevice(e, null, DateTime.Now);
                    P(string.Format("{0,-10} {1,-28} battery {2,-5} {3,-13} {4}{5}", e.Kind, e.Name, e.Battery.HasValue ? e.Battery + "%" : "-", e.Connected ? "connected" : "not connected",
                        (e.Le ? "LE " : "") + (e.Classic ? "classic " : "") + (e.Source != null ? "[" + e.Source + "]" : ""), d == null ? "  (no battery reported - no card)" : ""));
                }
                foreach (var l in bt.Log) P(l);
            }
            Write(file, sb); return 0;
        }
        static void Write(string file, StringBuilder sb) { if (file != null) try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { } }

        // --snap <folder>: every page with made-up gear, dark and light ("-light")
        static void SnapAll(string dir)
        {
            foreach (var light in new[] { false, true }) { ForceLight = light; LoadTheme(Application.Current); SnapPass(dir, light ? "-light" : ""); }
        }
        static void SnapPass(string dir, string sfx)
        {
            Func<string, string> F = n => Path.Combine(dir, n + sfx + ".png");
            var c = new Controller(true, true); var fake = (FakeSource)c.Source;
            c.ReadNow(true, true, true, true);
            var w = new MainWindow(c);
            foreach (var page in MainWindow.Pages.Select(p => p[0])) { w.Show(page); Snap.Save(w, F(page), 940, 0); }
            // the mouse on its cable and charging, the headset low and undocked, a game running with the held alert
            fake.Devices[0].Wired = true; fake.Devices[0].Charging = true; fake.Devices[0].Battery = 71; fake.Devices[0].External = true; fake.Devices[0].Via = "USB cable + LIGHTSPEED receiver";
            fake.Devices[1].Full = false; fake.Devices[1].External = false; fake.Devices[1].Battery = 9;
            c.SnapGame("Delta Force"); c.ReadNow(true, true, false);
            w.Show("devices"); Snap.Save(w, F("devices-game"), 940, 0);
            // asleep and not connected
            fake.Reset(); fake.Devices[0].Asleep = true; fake.Devices[0].Note = "Asleep or switched off - move it to wake it."; fake.Devices[1].Connected = false; fake.Devices[1].Battery = null; fake.Devices[1].Note = "The base station isn't plugged in.";
            c.SnapGame(null); c.SetBtHidden("bt:0a1b2c3d4e02", true); c.ReadNow(true, true, false, true);
            w.Show("devices"); Snap.Save(w, F("devices-asleep"), 940, 0);
            w.Show("settings"); Snap.Save(w, F("settings-hidden"), 940, 0);
            Snap.Save(CrashNotice.Make(Path.Combine(dir, "example.txt")), F("crash"), 440, 0);
            if (sfx.Length == 0) SnapNotes(dir);
        }
        // 1.2: the toasts as Windows 11 (dark) shows them, and every device icon in dark and light - nothing is shown on screen
        public static void SnapNotes(string dir)
        {
            var icons = Path.Combine(dir, "icons");
            var notes = Toasts.PreviewNotes((k, s) => DeviceIcons.PngFile(k, s, 96, false, AccentHex, icons));
            System.Windows.Media.ImageSource app = null;
            try { using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location)) app = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch { }
            Toasts.SavePng(Toasts.Preview(notes, app), Path.Combine(dir, "notifications.png"), 2);
            Toasts.SavePng(IconSheet(), Path.Combine(dir, "device-icons.png"), 1);
        }
        static FrameworkElement IconSheet()
        {
            var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            foreach (var light in new[] { false, true })
            {
                var g = new System.Windows.Controls.StackPanel { Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#F6F4FA" : "#14101B")), Margin = new Thickness(0) };
                foreach (var k in DeviceIcons.Kinds)
                {
                    var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 6) };
                    foreach (var s in DeviceIcons.States) { var e = DeviceIcons.Element(k, s, 48, light, AccentHex); e.Margin = new Thickness(6); row.Children.Add(e); }
                    g.Children.Add(row);
                }
                sp.Children.Add(g);
            }
            return sp;
        }
    }

    // which game runs: DeltaForceClient-Win64-Shipping.exe, Discovery-d.exe, Settings' extra names, or Hallow Hub's "playing"
    public static class GameWatch
    {
        public static readonly string[] Known = { "DeltaForceClient-Win64-Shipping", "Discovery-d" };
        public static string Running(IEnumerable<string> extra)
        {
            foreach (var n in Known.Concat(extra ?? new string[0]))
            {
                var bare = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
                Process[] ps = null;
                try { ps = Process.GetProcessesByName(bare); if (ps.Length > 0) return bare == "DeltaForceClient-Win64-Shipping" ? "Delta Force" : bare == "Discovery-d" ? "Discovery" : bare; }
                catch { }
                finally { if (ps != null) foreach (var p in ps) p.Dispose(); }
            }
            var hub = HubStatus.Playing(Family.DataDir("Hub"));
            return hub.Count > 0 ? hub[0] : null;
        }
    }
    public static class HubStatus
    {
        public static List<string> Playing(string hubDir)
        {
            var r = new List<string>();
            try
            {
                var f = Path.Combine(hubDir, "status.json"); if (!File.Exists(f)) return r;
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f)); object v;
                if (d == null || !d.TryGetValue("running", out v) || !(v is bool) || !(bool)v) return r;
                if (d.TryGetValue("playing", out v) && v is System.Collections.IEnumerable && !(v is string))
                    foreach (var x in (System.Collections.IEnumerable)v)
                    {
                        if (x is string) r.Add((string)x);
                        else { var o = x as Dictionary<string, object>; object n; if (o != null && o.TryGetValue("name", out n) && n is string) r.Add((string)n); }
                    }
            }
            catch { }
            return r;
        }
    }
}

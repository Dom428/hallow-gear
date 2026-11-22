// Hallow Gear - the app: the reading schedule (gentle: every 60 s on mains power, every 5 min on battery, on device arrival / removal;
// during a game the mouse is only listened to and the headset is read at most every 2 min), alerts (held during games; a low battery
// goes to Hallow Overlay), history, the tray / Hallow icon, the family pipe, status.json, accent / light-dark, updates, crash notice.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace HallowGear
{
    public class Controller
    {
        public readonly GearSettings S;
        public readonly bool SnapMode; public readonly IGearSource Source;
        readonly Dispatcher ui;
        MainWindow main; WF.NotifyIcon tray; bool hubHosts, hubLists, quitting, balloonOwn;
        public List<GearDevice> Devices = new List<GearDevice>();
        public readonly History Hist; readonly BatteryAlerts alerts = new BatteryAlerts(); public readonly AlertRouter Router = new AlertRouter();
        public string Game; public DateTime? LastRead; public string ReadNote;
        public readonly List<string[]> Shown = new List<string[]>();   // notifications this session: time, title, text
        readonly List<string> log = new List<string>();
        public event Action Changed;
        DateTime lastMouseAsk = DateTime.MinValue, lastHeadsetAsk = DateTime.MinValue, lastRescan = DateTime.MinValue, historySaved = DateTime.Now;
        readonly AutoResetEvent kick = new AutoResetEvent(false); volatile bool wantRescan = true, wantAll; volatile bool readBusy;
        FileSystemWatcher accentWatch; DeviceWatch devWatch;
        public int Notifications;

        public Controller(bool trayOnly, bool snapMode = false)
        {
            SnapMode = snapMode; ui = Dispatcher.CurrentDispatcher;
            if (snapMode) { S = new GearSettings(); S.Fix(); } else S = GearSettings.Load();
            bool fake = snapMode || GearSettings.Fake;
            Source = fake ? (IGearSource)new FakeSource() : new LogiSource(S);
            Hist = snapMode ? History.Fake(DateTime.Now) : History.Load();
            Source.Heard += (k, b) => ui.BeginInvoke(new Action(() => Heard(k, b)));
            Source.Link += (k, up) => ui.BeginInvoke(new Action(() => LinkChanged(k, up)));
            if (snapMode) return;
            Family.Serve("Gear", Command);
            new Thread(Worker) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "gear reader" }.Start();
            var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) }; tick.Tick += (s, e) => Tick(); tick.Start();
            if (!GearSettings.Headless)
            {
                SetupTray();
                var ht = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; ht.Tick += (s, e) => CheckHost(); ht.Start(); CheckHost();
                accentWatch = Accent.Watch(() => ui.BeginInvoke(new Action(ApplyAccent)));
                var lt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; lt.Tick += (s, e) => { if (main != null) ApplyAccent(); }; lt.Start();
                try { devWatch = new DeviceWatch(() => ui.BeginInvoke(new Action(DeviceChanged))); } catch { }
                if (GearSettings.TestDir == null || Environment.GetEnvironmentVariable("HALLOW_GEAR_API") != null)
                {
                    var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
                    upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(6); DateTime last; if (!DateTime.TryParse(S.LastUpdateCheck, out last) || (DateTime.Now - last).TotalHours >= 24) CheckForUpdate(false); };
                    upd.Start();
                }
                ui.BeginInvoke(new Action(ShowCrashNotice), DispatcherPriority.ApplicationIdle);
            }
            Game = GameNow(); WriteStatus();
            Tick();
            if (!trayOnly && !GearSettings.Headless) ShowMain(null);
        }
        void Log(string s) { lock (log) { log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s); if (log.Count > 100) log.RemoveAt(0); } }

        // ---------------- the schedule
        public static bool OnBattery() { try { return WF.SystemInformation.PowerStatus.PowerLineStatus == WF.PowerLineStatus.Offline; } catch { return false; } }
        public int IntervalSeconds { get { return OnBattery() ? Math.Max(300, S.PollSeconds) : S.PollSeconds; } }
        public string Polling { get { return Game != null ? "game" : OnBattery() ? "on-battery" : "normal"; } }
        string snapGame;
        string GameNow() { return SnapMode ? snapGame : GearSettings.Fake ? (File.Exists(Path.Combine(GearSettings.Dir, "test-game.txt")) ? File.ReadAllText(Path.Combine(GearSettings.Dir, "test-game.txt")).Trim() : null) : GameWatch.Running(S.ExtraGames); }
        public void SnapGame(string g) { snapGame = g; SetGame(g); }
        void Tick()
        {
            if (quitting) return;
            SetGame(GameNow());
            var now = DateTime.Now; int iv = IntervalSeconds;
            bool mouseDue = (now - lastMouseAsk).TotalSeconds >= (Game != null ? Math.Max(120, iv) : iv) && (Game == null || S.ReadMouseInGames);
            bool headDue = (now - lastHeadsetAsk).TotalSeconds >= (Game != null ? Math.Max(120, iv) : iv);
            if ((now - lastRescan).TotalMinutes >= 10 && Game == null) wantRescan = true;
            if (mouseDue || headDue || wantRescan || wantAll) kick.Set();
            if (Hist.Dirty && (now - historySaved).TotalMinutes >= 5) { historySaved = now; Hist.Save(); }
        }
        void Worker()
        {
            while (!quitting)
            {
                kick.WaitOne(TimeSpan.FromSeconds(30));
                if (quitting) break;
                var now = DateTime.Now; int iv = IntervalSeconds; bool all = wantAll; wantAll = false;
                bool game = Game != null;
                bool askMice = all || (now - lastMouseAsk).TotalSeconds >= (game ? Math.Max(120, iv) : iv) - 2 && (!game || S.ReadMouseInGames);
                bool askHead = all || (now - lastHeadsetAsk).TotalSeconds >= (game ? Math.Max(120, iv) : iv) - 2;
                bool rescan = wantRescan; wantRescan = false;
                if (!askMice && !askHead && !rescan) continue;
                ReadNow(askMice, askHead, rescan);
            }
        }
        // one read (on the reader thread, or directly in --snap / tests); results applied on the UI thread
        public void ReadNow(bool askMice, bool askHead, bool rescan)
        {
            List<GearDevice> list = null; readBusy = true;
            try { list = Source.Read(askMice, askHead, rescan); }
            catch (Exception e) { Log("read failed: " + e.Message); }
            finally { readBusy = false; }
            var now = DateTime.Now;
            if (askMice) lastMouseAsk = now; if (askHead) lastHeadsetAsk = now; if (rescan) lastRescan = now;
            if (list == null) return;
            Action apply = () => Apply(list, now);
            if (SnapMode || ui.CheckAccess()) apply(); else ui.Invoke(apply);
        }
        public void Forget(string key) { S.KnownGear.Remove(key); Devices.RemoveAll(x => x.Key == key && !x.Connected); S.Save(); WriteStatus(); Fire(); }
        public void RefreshNow() { wantAll = true; wantRescan = true; kick.Set(); }
        void DeviceChanged() { Log("devices changed"); wantRescan = true; var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; t.Tick += (s, e) => { t.Stop(); if (Game == null) { lastMouseAsk = DateTime.MinValue; lastHeadsetAsk = DateTime.MinValue; } kick.Set(); }; t.Start(); }

        void Apply(List<GearDevice> list, DateTime now)
        {
            // a paused device keeps what we heard passively since the last read
            foreach (var d in list.Where(x => x.Paused))
            {
                var old = Devices.FirstOrDefault(x => x.Key == d.Key);
                if (old != null && old.Updated.HasValue && (!d.Updated.HasValue || old.Updated > d.Updated)) { d.Battery = old.Battery; d.Charging = old.Charging; d.Full = old.Full; d.Updated = old.Updated; d.Source = old.Source; d.Estimated = old.Estimated; d.Asleep = old.Asleep; }
            }
            // gear seen before but not here now: "not connected" (until Forget)
            bool changedKnown = false;
            foreach (var d in list.Where(x => !S.KnownGear.ContainsKey(x.Key))) { S.KnownGear[d.Key] = d.Kind + "|" + d.Name; changedKnown = true; }
            foreach (var kv in S.KnownGear.ToList())
            {
                if (list.Any(x => x.Key == kv.Key)) continue;
                var bar = kv.Value.IndexOf('|'); var kind = bar > 0 ? kv.Value.Substring(0, bar) : "device"; var name = bar > 0 ? kv.Value.Substring(bar + 1) : kv.Value;
                var old = Devices.FirstOrDefault(x => x.Key == kv.Key);
                list.Add(new GearDevice { Key = kv.Key, Kind = kind, Name = name, Connected = false, HasBattery = kind != "keyboard", Updated = old != null ? old.Updated : null,
                    Via = kind == "headset" ? "base station (USB)" : kind == "keyboard" ? "USB cable" : "LIGHTSPEED receiver",
                    Note = kind == "headset" ? "The base station isn't plugged in." : kind == "keyboard" ? "Not plugged in." : "Its receiver isn't plugged in." });
            }
            if (changedKnown && !SnapMode) S.Save();
            Devices = list; LastRead = now;
            foreach (var d in list) Fresh(d, now);
            WriteStatus(); Fire();
        }
        // a device has new values: history, alerts
        void Fresh(GearDevice d, DateTime now)
        {
            if (d.HasBattery && d.Battery.HasValue && d.Connected && !d.Asleep && d.Updated.HasValue && (now - d.Updated.Value).TotalMinutes < 5) Hist.Add(d.Key, d.Battery.Value, d.Updated.Value);
            if (d.Paused && !(d.Updated.HasValue && (now - d.Updated.Value).TotalMinutes < 5)) return;
            var a = alerts.Update(d, AlertRules.From(S), now);
            if (a.Count == 0) return;
            foreach (var x in a) Log("alert: " + x);
            var show = Router.Route(a, Game != null);
            foreach (var x in show) Notify(x.Title, x.Text);
            if (Game != null && a.Any(x => x.Low)) OverlayLine(a.Last(x => x.Low).Title);
        }
        void Heard(string key, BatteryReading b)
        {
            var d = Devices.FirstOrDefault(x => x.Key == key); if (d == null) return;
            var was = d.Battery; d.Apply(b, DateTime.Now); d.Paused = false;
            Log("heard " + key + ": " + b);
            Fresh(d, DateTime.Now);
            WriteStatus(); Fire();
        }
        void LinkChanged(string key, bool up)
        {
            var d = Devices.FirstOrDefault(x => x.Key == key); if (d == null) return;
            d.Asleep = !up; if (!up) { d.Charging = false; d.Note = "Asleep or switched off - move it to wake it."; } else d.Note = null;
            Log(key + " link " + (up ? "up" : "down"));
            if (up && Game == null) { lastMouseAsk = DateTime.MinValue; var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; t.Tick += (s, e) => { t.Stop(); kick.Set(); }; t.Start(); }
            WriteStatus(); Fire();
        }
        void SetGame(string g)
        {
            if (g == Game) return;
            var was = Game; Game = g; Log(g != null ? "game: " + g : "game closed");
            if (g == null && was != null)
            {
                foreach (var a in Router.GameEnded(DateTime.Now)) Notify(a.Title, a.Text);
                lastMouseAsk = DateTime.MinValue; kick.Set();   // catch up on the mouse now
            }
            WriteStatus(); Fire();
        }
        void Fire() { var h = Changed; if (h != null) h(); }

        // Hallow Overlay 1.20+ reads the held alert from status.json; an older Overlay gets a 2.5 s "flash" instead
        void OverlayLine(string text)
        {
            if (SnapMode || GearSettings.Fake) { Log("overlay: " + text); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var r = Family.Send("Overlay", "ping", 1500); if (r == null || !r.StartsWith("ok {")) return;
                    var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.Substring(3)); object v; Version ver;
                    if (d != null && d.TryGetValue("version", out v) && Version.TryParse(v as string ?? "", out ver) && ver >= new Version(1, 20)) return;
                    Family.Send("Overlay", "flash " + text, 2000);
                }
                catch { }
            });
        }

        // ---------------- window
        public void ShowMain(string page)
        {
            if (GearSettings.Headless || SnapMode) return;
            if (main == null) { ApplyAccent(); main = new MainWindow(this); }
            if (page != null) main.Show(page);
            main.Show(); if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal; main.Activate();
        }
        public void MainClosed()
        {
            main = null;
            if (!S.TrayHintShown && !quitting) { S.TrayHintShown = true; S.Save(); Notify("Hallow Gear is still here", "It keeps an eye on your gear's battery from the tray. Right-click the icon to quit."); }
        }
        public void Quit()
        {
            if (quitting) return; quitting = true; kick.Set();
            try { Hist.Save(); } catch { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (devWatch != null) devWatch.Dispose();
            WriteStatus(false);
            try { Source.Dispose(); } catch { }
            if (main != null) main.Exiting = true;
            Application.Current.Shutdown();
        }
        public void Say(string text) { if (main != null) main.Say(text); }
        public bool Busy { get { return readBusy; } }

        // ---------------- settings
        const string RunName = "HallowGear";
        string RunFile { get { return Path.Combine(GearSettings.Dir, "test-autostart.txt"); } }
        public bool StartsWithWindows()
        {
            if (GearSettings.TestDir != null || SnapMode) return File.Exists(RunFile);
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue(RunName) != null; } catch { return false; }
        }
        public void SetStartWithWindows(bool on)
        {
            if (SnapMode) return;
            if (GearSettings.TestDir != null) { if (on) File.WriteAllText(RunFile, "\"HallowGear.exe\" --tray"); else if (File.Exists(RunFile)) File.Delete(RunFile); return; }
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (on) k.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"); else k.DeleteValue(RunName, false); } } catch { }
        }
        public void SaveSettings() { S.Fix(); S.Save(); WriteStatus(); }

        // ---------------- tray, the Hallow icon (Hub hosts it), notifications, accent, updates, crash notice
        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "Hallow Gear", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Open Hallow Gear"); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); open.Click += (s, e) => ShowMain("devices"); menu.Items.Add(open);
            foreach (var it in MenuItems().Cast<Dictionary<string, object>>())
            {
                var id = (string)it["id"]; if (id == "-") { menu.Items.Add(new WF.ToolStripSeparator()); continue; }
                var mi = new WF.ToolStripMenuItem((string)it["label"]); mi.Click += (s, e) => Invoke(id); menu.Items.Add(mi);
                if (id == "alerts") menu.Opening += (s, e) => mi.Checked = S.AlertLow;
            }
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowMain(null); };
            Changed += () => { try { tray.Text = TrayText(); } catch { } };
        }
        public string TrayText()
        {
            var parts = Devices.Where(d => d.HasBattery && d.Battery.HasValue && d.Connected).Select(d => BatteryAlerts.Word(d) + " " + d.Battery + "%" + (d.Charging ? "+" : "")).ToList();
            var t = "Hallow Gear" + (parts.Count > 0 ? " - " + string.Join(", ", parts) : "");
            return t.Length > 63 ? t.Substring(0, 63) : t;
        }
        public void CheckHost()
        {
            if (tray == null) return;
            if (!S.UseHallowIcon) { SetHosted(false, false); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool on = false, lists = false;
                try { var r = Family.Send("Hub", "tray-host", 1500); if (r != null && r.StartsWith("ok {")) HubHosting(r.Substring(3), out on, out lists); } catch { }
                ui.BeginInvoke(new Action(() => SetHosted(on && S.UseHallowIcon, lists)));
            });
        }
        // Hub's "tray-host": on = Hub's icon shows; lists = Hub's menu has Gear ("apps" lists it, or "generic":true). Gear hides its icon only then.
        public static void HubHosting(string json, out bool on, out bool lists)
        {
            on = false; lists = false;
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); object v;
                on = d != null && d.TryGetValue("on", out v) && v is bool && (bool)v;
                if (!on) return;
                if (d.TryGetValue("generic", out v) && v is bool && (bool)v) { lists = true; return; }
                if (d.TryGetValue("apps", out v) && v is System.Collections.IEnumerable && !(v is string))
                    foreach (var a in (System.Collections.IEnumerable)v) if (a is string && string.Equals((string)a, "Gear", StringComparison.OrdinalIgnoreCase)) lists = true;
            }
            catch { on = false; lists = false; }
        }
        void SetHosted(bool on, bool lists) { hubHosts = on; hubLists = on && lists; if (tray != null && !balloonOwn) tray.Visible = !hubLists; }
        // a notification (Windows shows it as a toast): from Hallow Hub's icon when Hub hosts Gear's, otherwise from Gear's own tray icon
        public void Notify(string title, string text)
        {
            Notifications++; Log("notify: " + title);
            lock (Shown) { Shown.Add(new[] { DateTime.Now.ToString("HH:mm"), title, text }); if (Shown.Count > 30) Shown.RemoveAt(0); }
            if (tray == null || GearSettings.TestDir != null || SnapMode) return;
            if (!hubHosts) { tray.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.None); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Family.Error(Family.Send("Hub", "notify Gear\t" + title + "\t" + text, 2000), "no answer") == null) return;
                ui.BeginInvoke(new Action(() => { balloonOwn = true; tray.Visible = true; tray.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.None); var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; t.Tick += (s, e) => { t.Stop(); balloonOwn = false; SetHosted(hubHosts, hubLists); }; t.Start(); }));
            });
        }
        public void ApplyAccent()
        {
            if (SnapMode) return;
            var a = Accent.Current(S.UseHallowAccent); var light = Look.Current(S);
            if (a == Program.AccentHex && light == Program.Light) return;
            Program.LoadTheme(Application.Current, S); Log("look: " + (Program.Light ? "light" : "dark") + ", accent " + Program.AccentHex);
            if (main != null) { main.Rebuild(); Ui.TitleBar(main); }
        }
        public void SetLook(string look) { S.Look = look == "light" || look == "dark" ? look : "hallow"; S.Save(); ApplyAccent(); WriteStatus(); }
        GhRelease pending; public GhRelease PendingUpdate { get { return pending; } }
        public void CheckForUpdate(bool manual)
        {
            if (manual) Say("Checking GitHub for a newer Hallow Gear...");
            new Thread(() =>
            {
                var r = Updater.Latest();
                ui.BeginInvoke(new Action(() =>
                {
                    S.LastUpdateCheck = DateTime.Now.ToString("o"); S.Save();
                    if (r == null) { if (manual) Say("Couldn't reach GitHub - try again later."); return; }
                    if (!Updater.IsNewer(r)) { if (manual) Say("You have the newest version (v" + Program.Version + ")."); return; }
                    pending = r; if (main != null) main.ShowUpdate("Update to v" + r.Version.ToString(2));
                    Say("Hallow Gear v" + r.Version.ToString(2) + " is out - click Update.");
                }));
            }) { IsBackground = true }.Start();
        }
        public void InstallUpdate()
        {
            if (pending == null) return;
            if (!Dialogs.Confirm(main, "Install Hallow Gear v" + pending.Version.ToString(2) + "?", "It downloads from GitHub, checks the file and puts the new version in place. Your settings and history stay.", "Update")) return;
            var r = pending; Say("Updating...");
            new Thread(() => { var err = Updater.Install(r); ui.BeginInvoke(new Action(() => Say(err ?? "Installing the update..."))); }) { IsBackground = true }.Start();
        }
        void ShowCrashNotice()
        {
            var f = Crash.Last(); if (f == null) return;
            var name = Path.GetFileName(f); if (name == S.LastCrashShown) return;
            S.LastCrashShown = name; S.Save();
            if (Game != null) return;   // never over a game
            CrashNotice.Make(f).Show();
        }

        // ---------------- family link: status.json and the pipe
        public static Dictionary<string, object> DeviceJson(GearDevice d)
        {
            return new Dictionary<string, object> {
                { "kind", d.Kind }, { "name", d.Name }, { "battery", d.HasBattery ? (object)d.Battery : null }, { "state", d.State }, { "wired", d.Wired },
                { "charging", d.Charging }, { "full", d.Full }, { "connected", d.Connected }, { "asleep", d.Asleep }, { "estimated", d.Estimated },
                { "paused", d.Paused }, { "updated", d.Updated.HasValue ? Family.Time(d.Updated.Value) : null } };
        }
        public Dictionary<string, object> StatusData(bool running)
        {
            var d = new Dictionary<string, object> { { "app", "Gear" }, { "version", Program.Version }, { "updated", Family.Time(DateTime.Now) }, { "running", running } };
            d["devices"] = Devices.Select(x => (object)DeviceJson(x)).ToList();
            d["lowAt"] = S.Low1; d["veryLowAt"] = S.Low2;
            d["low"] = Devices.Where(x => x.HasBattery && x.Battery.HasValue && !x.Charging && !x.Full && x.Connected && x.Battery <= S.Low1).Select(x => x.Name).ToList();
            d["game"] = Game; d["polling"] = Polling; d["held"] = Router.Held.Count;
            var line = Game != null ? Router.OverlayLine : null;
            d["alert"] = line == null ? null : new Dictionary<string, object> { { "text", line.Title }, { "level", line.Kind }, { "device", line.Key }, { "time", Family.Time(line.At) } };
            d["lastRead"] = LastRead.HasValue ? Family.Time(LastRead.Value) : null;
            d["startsWithWindows"] = StartsWithWindows(); d["look"] = Program.Light ? "light" : "dark";
            d["lastCrash"] = Crash.LastTime() == null ? null : Family.Time(Crash.LastTime().Value);
            if (GearSettings.TestDir != null) { lock (log) d["log"] = log.ToArray(); d["notifications"] = Notifications; }
            return d;
        }
        public void WriteStatus(bool running = true) { if (!SnapMode) Family.WriteStatus(Family.Json(StatusData(running))); }
        string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "ping": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Gear" }, { "version", Program.Version } });
                case "status": { var d = OnUiValue(() => StatusData(true)); if (d == null) return "error no answer in time"; return "ok " + Family.Json(d); }
                case "open": return OnUi(() => { var p = arg.Length > 0 ? MainWindow.PageId(arg) : null; if (arg.Length > 0 && p == null) return "error no page called " + arg; ShowMain(p); return "ok"; });
                case "menu": return OnUi(() => "ok " + Family.Json(new Dictionary<string, object> { { "app", "Gear" }, { "items", MenuItems() } }));
                case "invoke": return OnUi(() => Invoke(arg));
                case "quit": new Thread(() => { Thread.Sleep(300); ui.BeginInvoke(new Action(Quit)); }) { IsBackground = true }.Start(); return "ok";
            }
            if (GearSettings.TestDir != null && cmd.StartsWith("test-")) return OnUi(() => TestCommand(cmd, arg));
            return "error unknown command";
        }
        string OnUi(Func<string> f)
        {
            string r = "error no answer in time";
            var op = ui.BeginInvoke(new Action(() => { try { r = f(); } catch (Exception e) { r = "error " + Family.OneLine(e.Message); } }));
            return op.Wait(TimeSpan.FromSeconds(5)) == DispatcherOperationStatus.Completed ? r : "error no answer in time";
        }
        T OnUiValue<T>(Func<T> f) where T : class
        {
            T r = null; var op = ui.BeginInvoke(new Action(() => { try { r = f(); } catch { } }));
            return op.Wait(TimeSpan.FromSeconds(5)) == DispatcherOperationStatus.Completed ? r : null;
        }
        public List<object> MenuItems()
        {
            var items = new List<object>();
            foreach (var d in Devices.Where(x => x.HasBattery).Take(4))
                items.Add(new Dictionary<string, object> { { "id", "open-devices" }, { "label", d.Name + ": " + (d.Battery.HasValue ? d.Battery + " %" : "-") + " \u00B7 " + d.StateLabel }, { "enabled", false } });
            if (items.Count > 0) items.Add(new Dictionary<string, object> { { "id", "-" } });
            items.Add(new Dictionary<string, object> { { "id", "refresh" }, { "label", "Refresh now" } });
            items.Add(new Dictionary<string, object> { { "id", "alerts" }, { "label", "Low battery alerts" }, { "checked", S.AlertLow } });
            items.Add(new Dictionary<string, object> { { "id", "settings" }, { "label", "Settings" } });
            return items;
        }
        string Invoke(string id)
        {
            switch (id)
            {
                case "refresh": RefreshNow(); return "ok";
                case "alerts": S.AlertLow = !S.AlertLow; SaveSettings(); Fire(); return "ok";
                case "settings": ShowMain("settings"); return "ok";
                case "devices": case "open-devices": ShowMain("devices"); return "ok";
                case "about": ShowMain("about"); return "ok";
            }
            return "error unknown item";
        }
        // developer tests only (HALLOW_GEAR_TESTDIR + HALLOW_GEAR_FAKE): made-up devices, no windows
        string TestCommand(string cmd, string arg)
        {
            var fake = Source as FakeSource;
            switch (cmd)
            {
                case "test-autostart": SetStartWithWindows(arg == "on"); return "ok " + StartsWithWindows();
                case "test-read": ReadNow(true, true, false); return "ok " + Devices.Count;
                case "test-notes": lock (Shown) return "ok " + Family.Json(Shown.Select(n => n[1]).ToList());
            }
            if (fake == null) return "error unknown test command";
            switch (cmd)
            {
                case "test-battery":
                    {   // test-battery <index> <percent> [charging|full|wired]
                        var p = arg.Split(' '); var d = fake.Devices[int.Parse(p[0])];
                        d.Battery = int.Parse(p[1]); d.Charging = p.Contains("charging"); d.Full = p.Contains("full"); d.Wired = p.Contains("wired"); d.Updated = DateTime.Now;
                        ReadNow(true, true, false); return "ok";
                    }
                case "test-hear": { var p = arg.Split(' '); fake.Hear(p[0], new BatteryReading { Percent = int.Parse(p[1]), Source = "test" }); return "ok"; }
                case "test-game": { var f = Path.Combine(GearSettings.Dir, "test-game.txt"); if (arg.Length == 0 || arg == "off") { if (File.Exists(f)) File.Delete(f); } else File.WriteAllText(f, arg); SetGame(GameNow()); return "ok " + (Game ?? "none"); }
                case "test-asks": return "ok " + Family.Json(new Dictionary<string, object> { { "reads", fake.Reads }, { "mouse", fake.MouseAsks }, { "headset", fake.HeadsetAsks } });
            }
            return "error unknown test command";
        }
    }

    // device arrival / removal (WM_DEVICECHANGE reaches a hidden top-level window)
    public sealed class DeviceWatch : WF.NativeWindow, IDisposable
    {
        readonly Action changed; DateTime last = DateTime.MinValue;
        public DeviceWatch(Action changed) { this.changed = changed; CreateHandle(new WF.CreateParams { Caption = "HallowGearDeviceWatch" }); }
        protected override void WndProc(ref WF.Message m)
        {
            if (m.Msg == 0x0219) { var w = m.WParam.ToInt64(); if (w == 0x0007 || w == 0x8000 || w == 0x8004) { if ((DateTime.Now - last).TotalMilliseconds > 500) { last = DateTime.Now; try { changed(); } catch { } } } }
            base.WndProc(ref m);
        }
        public void Dispose() { try { DestroyHandle(); } catch { } }
    }
}

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
    // 1.3.1: what the reader asks and when - one pure decision, so the tests can run it against a made-up clock
    public class ReadPlan
    {
        public bool Mice, Gentle, Head, Rescan, Bt;
        public bool Any { get { return Mice || Gentle || Head || Rescan || Bt; } }
        public override string ToString() { return (Mice ? "mice " : "") + (Gentle ? "gentle " : "") + (Head ? "headset " : "") + (Rescan ? "rescan " : "") + (Bt ? "bluetooth" : "").Trim(); }
    }
    public class ReadSchedule
    {
        public const int GentleSeconds = 300, GameHeadSeconds = 120, BtSeconds = 300;
        public DateTime LastMouse = DateTime.MinValue, LastHead = DateTime.MinValue, LastBt = DateTime.MinValue, LastGentle = DateTime.MinValue;
        public bool StartAskDone;   // the first read of this session has happened (the "once when Gear starts" gentle read, if a game was running)
        // all = Refresh now (the user asked); slack = a couple of seconds early is fine on the reader thread
        public ReadPlan Plan(DateTime now, int interval, bool game, bool all, bool rescan, string gameAsk, bool bluetooth, int slack)
        {
            var p = new ReadPlan { Rescan = rescan };
            p.Mice = all || (!game && (now - LastMouse).TotalSeconds >= interval - slack);   // during a game the mice are never fully read (only the gentle read)
            p.Head = all || (now - LastHead).TotalSeconds >= (game ? Math.Max(GameHeadSeconds, interval) : interval) - slack;
            if (game && !p.Mice)
                p.Gentle = (!StartAskDone && (gameAsk == "start" || gameAsk == "5min")) || (gameAsk == "5min" && (now - LastGentle).TotalSeconds >= GentleSeconds - slack);
            p.Bt = bluetooth && !game && (all || (now - LastBt).TotalSeconds >= BtSeconds - slack);
            return p;
        }
        public void Done(ReadPlan p, DateTime now)
        {
            if (p.Mice) LastMouse = now; if (p.Head) LastHead = now; if (p.Bt) LastBt = now; if (p.Gentle) LastGentle = now;
            if (p.Mice || p.Gentle || p.Head || p.Rescan) StartAskDone = true;
        }
    }

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
        DateTime lastRescan = DateTime.MinValue, historySaved = DateTime.Now;
        public readonly ReadSchedule Sched = new ReadSchedule();   // 1.3.1
        readonly AutoResetEvent kick = new AutoResetEvent(false); volatile bool wantRescan = true, wantAll, wantList; volatile bool readBusy;
        // 1.3.1: remembered gear (devices.json) - shown at once on start as "last known", kept up to date (saved at most once a minute, and on quit)
        public readonly List<RememberedDevice> Remembered; DateTime rememberedSaved = DateTime.MinValue; bool rememberedDirty;
        FileSystemWatcher accentWatch; DeviceWatch devWatch;
        public int Notifications;
        // 1.2: notifications with the device icon - a Windows toast, or the tray balloon with a large icon, or Hub's notify (Toasts.cs)
        public readonly NoteSender Sender = new NoteSender(); ToastState toastState = ToastState.Unavailable; DateTime toastChecked = DateTime.MinValue; public string ShortcutNote;
        // 1.1: Bluetooth devices (what Windows knows): read at most every 5 minutes, never during a game (notifications still arrive)
        public readonly IBtSource Bt; public const int BtSeconds = 300;
        DateTime btReadAt = DateTime.MinValue; List<BtEntry> btLast = new List<BtEntry>();
        // 1.3: while gaming - Gear's own pop-up on another monitor (never takes focus), and what tests saw of it
        public readonly PopupStack Popups = new PopupStack(); public readonly List<string> PopupLog = new List<string>();
        public IEnumerable<GearDevice> VisibleDevices { get { return Devices.Where(d => !S.BtHidden.Contains(d.Key)).ToList(); } }

        public Controller(bool trayOnly, bool snapMode = false)
        {
            SnapMode = snapMode; ui = Dispatcher.CurrentDispatcher;
            if (snapMode) { S = new GearSettings(); S.Fix(); } else S = GearSettings.Load();
            bool fake = snapMode || GearSettings.Fake;
            Source = fake ? (IGearSource)new FakeSource() : new LogiSource(S);
            Hist = snapMode ? History.Fake(DateTime.Now) : History.Load();
            Source.Heard += (k, b) => ui.BeginInvoke(new Action(() => Heard(k, b)));
            Source.Link += (k, up) => ui.BeginInvoke(new Action(() => LinkChanged(k, up)));
            Source.Found += () => { wantList = true; kick.Set(); };   // 1.3.1: the receiver announced a device: list again (nothing asked)
            Remembered = snapMode ? new List<RememberedDevice>() : RememberedGear.Load();
            Devices = Remembered.Select(RememberedGear.ToCard).ToList();   // shown at once, "last known", until fresh data arrives
            Bt = fake ? (IBtSource)new FakeBtSource() : new BtSource();
            Bt.Heard += (k, b) => ui.BeginInvoke(new Action(() => Heard(k, b)));
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
                SetupNotes();
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
            if ((now - lastRescan).TotalMinutes >= 10 && Game == null) wantRescan = true;
            if (Sched.Plan(now, iv, Game != null, wantAll, wantRescan, S.GameAsk, S.Bluetooth, 0).Any || wantList) kick.Set();
            if (Hist.Dirty && (now - historySaved).TotalMinutes >= 5) { historySaved = now; Hist.Save(); }
            if (rememberedDirty && (now - rememberedSaved).TotalSeconds >= 60) SaveRemembered(now);
            if (tray != null && (now - toastChecked).TotalMinutes >= 5) ThreadPool.QueueUserWorkItem(_ => { var was = toastState; ToastNow(); if (was != toastState) ui.BeginInvoke(new Action(() => WriteStatus())); });   // 1.2.1: status.json's "toasts" stays current
        }
        void Worker()
        {
            while (!quitting)
            {
                kick.WaitOne(TimeSpan.FromSeconds(30));
                if (quitting) break;
                var now = DateTime.Now; bool all = wantAll; wantAll = false; bool rescan = wantRescan; wantRescan = false; bool list = wantList; wantList = false;
                var p = Sched.Plan(now, IntervalSeconds, Game != null, all, rescan, S.GameAsk, S.Bluetooth, 2);
                if (!p.Any && !list) continue;
                ReadNow(p.Mice, p.Head, p.Rescan, p.Bt, p.Gentle, list);
            }
        }
        // one read (on the reader thread, or directly in --snap / tests); results applied on the UI thread.
        // askBt: also read the Bluetooth devices (only when nothing else is asked, the Logitech gear isn't touched at all)
        // 1.3.1 gentle: a game runs and the gentle battery read is due (see ReadSchedule); listOnly: list the gear again, asking nothing
        public void ReadNow(bool askMice, bool askHead, bool rescan, bool askBt = false, bool gentle = false, bool listOnly = false)
        {
            List<GearDevice> list = null; List<BtEntry> bt = null; bool logi = askMice || askHead || rescan || gentle || listOnly; readBusy = true;
            if (gentle) Log("gentle read (" + S.GameAsk + "): the battery only, 1-2 short messages per receiver device");
            try { if (logi) list = Source.Read(askMice, askHead, rescan, gentle); }
            catch (Exception e) { Log("read failed: " + e.Message); }
            try { if (askBt && S.Bluetooth) bt = Bt.Read(); }
            catch (Exception e) { Log("bluetooth read failed: " + e.Message); }
            finally { readBusy = false; }
            var now = DateTime.Now;
            Sched.Done(new ReadPlan { Mice = askMice, Head = askHead, Rescan = rescan, Bt = askBt, Gentle = gentle }, now); if (rescan) lastRescan = now;
            if (list == null && bt == null) return;
            Action apply = () => Apply(list, bt, now);
            if (SnapMode || ui.CheckAccess()) apply(); else ui.Invoke(apply);
        }
        public void Forget(string key) { S.KnownGear.Remove(key); Devices.RemoveAll(x => x.Key == key && !x.Connected); S.Save(); if (RememberedGear.Forget(Remembered, key)) SaveRemembered(DateTime.Now); WriteStatus(); Fire(); }
        public void RefreshNow() { wantAll = true; wantRescan = true; kick.Set(); }
        // 1.2.2: the pipe's "refresh" (Hallow Remote / Hub): the same read-only queries as Refresh now, at most once every 30 s,
        // and never during a game (then nothing is asked at all - not even the headset). The reply says whether a read was started.
        public const int RefreshGapSeconds = 30;
        DateTime lastPipeRefresh = DateTime.MinValue; readonly object refreshLock = new object();
        public Dictionary<string, object> PipeRefresh(DateTime now)
        {
            lock (refreshLock)
            {
                string reason = null; int retry = 0;
                if (Game != null) reason = "game";
                else if ((now - lastPipeRefresh).TotalSeconds < RefreshGapSeconds) { reason = "too-soon"; retry = (int)Math.Ceiling(RefreshGapSeconds - (now - lastPipeRefresh).TotalSeconds); }
                if (reason == null) { lastPipeRefresh = now; Log("refresh (pipe)"); RefreshNow(); }
                return new Dictionary<string, object> { { "refreshing", reason == null }, { "reason", reason }, { "retryIn", retry }, { "gapSeconds", RefreshGapSeconds },
                    { "lastRead", LastRead.HasValue ? Family.Time(LastRead.Value) : null } };
            }
        }
        // 1.2.2: what status.json calls a device - never a Bluetooth address. Bluetooth keys ("bt:<address>") become "bt-" + 12 hex digits
        // of SHA-256(this install's random salt + key): the same device keeps its id, but the id can't be turned back into the address.
        public string PublicId(string key)
        {
            if (key == null || !key.StartsWith("bt:")) return key;
            if (string.IsNullOrEmpty(S.IdSalt)) lock (refreshLock) if (string.IsNullOrEmpty(S.IdSalt)) { S.IdSalt = Guid.NewGuid().ToString("N"); S.Save(); }
            return OpaqueId(S.IdSalt, key);
        }
        public static string OpaqueId(string salt, string key)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
            {
                var b = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(salt + "|" + key));
                return "bt-" + string.Concat(b.Take(6).Select(x => x.ToString("x2")));
            }
        }
        void DeviceChanged() { Log("devices changed"); wantRescan = true; var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; t.Tick += (s, e) => { t.Stop(); if (Game == null) { Sched.LastMouse = DateTime.MinValue; Sched.LastHead = DateTime.MinValue; } kick.Set(); }; t.Start(); }

        void Apply(List<GearDevice> list, List<BtEntry> bt, DateTime now)
        {
            if (list == null) list = Devices.Where(x => !x.Bluetooth).Select(x => x.Clone()).ToList();   // only Bluetooth was read this time
            if (bt != null) { btLast = bt; btReadAt = now; }
            // a paused device keeps what we heard passively since the last read
            foreach (var d in list.Where(x => x.Paused))
            {
                var old = Devices.FirstOrDefault(x => x.Key == d.Key);
                if (old != null && old.Updated.HasValue && (!d.Updated.HasValue || old.Updated > d.Updated))
                {
                    d.Battery = old.Battery; d.Charging = old.Charging; d.Full = old.Full; d.Updated = old.Updated; d.Source = old.Source; d.Estimated = old.Estimated; d.Asleep = old.Asleep;
                    d.LastKnown = old.LastKnown; d.Waiting = d.Waiting && (old.LastKnown || old.Waiting);   // 1.3.1: still "last known" / still waiting until something fresh is heard
                }
            }
            bool changedKnown = false;
            foreach (var d in list.Where(x => !S.KnownGear.ContainsKey(x.Key) && !x.Provisional)) { S.KnownGear[d.Key] = d.Kind + "|" + d.Name; changedKnown = true; }
            // 1.3.1: remembered gear that isn't in this read: during a game, its last known card (the read didn't ask); otherwise "not connected" below
            if (Game != null)
                foreach (var r in Remembered.Where(r => !list.Any(x => x.Key == r.Key || (r.SlotKey != null && x.SlotKey == r.SlotKey))).ToList()) list.Add(RememberedGear.ToCard(r));
            // gear seen before but not here now: "not connected" (until Forget) - 1.3.1: with its last known battery
            foreach (var kv in S.KnownGear.ToList())
            {
                if (list.Any(x => x.Key == kv.Key)) continue;
                var bar = kv.Value.IndexOf('|'); var kind = bar > 0 ? kv.Value.Substring(0, bar) : "device"; var name = bar > 0 ? kv.Value.Substring(bar + 1) : kv.Value;
                var old = Devices.FirstOrDefault(x => x.Key == kv.Key); var mem = Remembered.FirstOrDefault(r => r.Key == kv.Key);
                var nc = new GearDevice { Key = kv.Key, Kind = kind, Name = name, Connected = false, HasBattery = kind != "keyboard", Updated = old != null ? old.Updated : null,
                    Via = kind == "headset" ? "base station (USB)" : kind == "keyboard" ? "USB cable" : "LIGHTSPEED receiver",
                    Note = kind == "headset" ? "The base station isn't plugged in." : kind == "keyboard" ? "Not plugged in." : "Its receiver isn't plugged in." };
                if (mem != null && mem.HasBattery && mem.Battery.HasValue) { var c = RememberedGear.ToCard(mem); nc.Battery = c.Battery; nc.Updated = c.Updated; nc.LastSeen = c.LastSeen; nc.LastKnown = true; nc.Estimated = c.Estimated; if (mem.Via != null) nc.Via = mem.Via; }
                list.Add(nc);
            }
            if (changedKnown && !SnapMode) S.Save();
            list = GearMerge.WithBluetooth(list, S.Bluetooth ? BtCards() : new List<GearDevice>());
            Devices = list; LastRead = now;
            foreach (var d in list) Fresh(d, now);
            Remember(list, now);
            WriteStatus(); Fire();
        }
        // 1.3.1: devices.json follows what is shown (saved at most once a minute, and on quit)
        void Remember(IEnumerable<GearDevice> shown, DateTime now)
        {
            if (SnapMode) return;
            if (RememberedGear.Update(Remembered, shown, now)) rememberedDirty = true;
            if (rememberedDirty && (rememberedSaved == DateTime.MinValue || (now - rememberedSaved).TotalSeconds >= 60)) SaveRemembered(now);
        }
        void SaveRemembered(DateTime now) { rememberedSaved = now; rememberedDirty = false; RememberedGear.Save(Remembered.ToList()); }
        // 1.3.1: what the banner says about asking during a game
        public string GameAskLine()
        {
            switch (S.GameAsk)
            {
                case "never": return "the mouse is only listened to, never asked";
                case "5min": return "the mouse is asked for its battery every 5 min (1-2 short messages), otherwise only listened to";
            }
            return Sched.StartAskDone && Sched.LastGentle != DateTime.MinValue ? "the mouse was asked for its battery once when Hallow Gear started, now it's only listened to" : "the mouse is only listened to (it was read before the game)";
        }
        // 1.1: the Bluetooth cards from the last Bluetooth read (a battery heard since then wins; during a game they're marked paused)
        List<GearDevice> BtCards()
        {
            var cards = new List<GearDevice>(); bool changed = false;
            foreach (var e in btLast)
            {
                if (e.Address == null) continue;
                var key = BtProto.Key(e.Address); string rem; S.BtKnown.TryGetValue(key, out rem);
                var d = BtProto.ToDevice(e, rem, btReadAt); if (d == null) continue;
                var old = Devices.FirstOrDefault(x => x.Key == key);
                bool heard = old != null && old.Updated.HasValue && old.Updated > btReadAt && old.Battery.HasValue;
                if (heard) { d.Battery = old.Battery; d.Updated = old.Updated; d.Source = old.Source; d.Connected = true; d.LastSeen = old.Updated; d.Note = null; }
                d.Paused = Game != null && !heard && d.Connected;
                if (d.Battery.HasValue) { var r = BtProto.Remember(d); if (rem != r) { S.BtKnown[key] = r; changed = true; } }
                cards.Add(d);
            }
            if (changed && !SnapMode) S.Save();
            return cards;
        }
        public void SetBluetooth(bool on)
        {
            S.Bluetooth = on; SaveSettings();
            if (!on) { try { Bt.Stop(); } catch { } btLast = new List<BtEntry>(); Devices = Devices.Where(x => !x.Bluetooth).ToList(); WriteStatus(); Fire(); }
            else { Sched.LastBt = DateTime.MinValue; kick.Set(); }
        }
        public void SetBtHidden(string key, bool hidden) { S.BtHidden.Remove(key); if (hidden) S.BtHidden.Add(key); SaveSettings(); Fire(); }
        public void SetBtAlerts(string key, bool on) { S.BtNoAlerts.Remove(key); if (!on) S.BtNoAlerts.Add(key); SaveSettings(); Fire(); }
        public bool AlertsOn(GearDevice d) { return !S.BtNoAlerts.Contains(d.Key) && !S.BtHidden.Contains(d.Key); }

        // a device has new values: history, alerts
        void Fresh(GearDevice d, DateTime now)
        {
            if (d.LastKnown) return;   // 1.3.1: remembered values are shown, never alerted on or added to the history again
            if (d.HasBattery && d.Battery.HasValue && d.Connected && !d.Asleep && d.Updated.HasValue && (now - d.Updated.Value).TotalMinutes < 5) Hist.Add(d.Key, d.Battery.Value, d.Updated.Value);
            if (d.Paused && !(d.Updated.HasValue && (now - d.Updated.Value).TotalMinutes < 5)) return;
            var a = alerts.Update(d, AlertRules.From(S), now);
            if (a.Count == 0) return;
            if (!AlertsOn(d)) { foreach (var x in a) Log("alert off for " + d.Key + ": " + x); return; }   // 1.1: hidden, or its alerts switched off
            foreach (var x in a) Log("alert: " + x);
            // 1.3: during a game each alert goes the way Settings > While I'm gaming says (per type, per device)
            Mon target = null; bool looked = false;
            Func<GearAlert, Way> decide = x => { if (!looked) { looked = true; target = PopupScreen(false); } return GameRoute.Decide(S, x.Key, x.Kind, true, target != null); };
            var ways = Router.RouteWays(a, Game != null, decide);
            foreach (var r in ways) { if (Game != null) Log("game route: " + r.A.Kind + " -> " + r.Way.ToString().ToLowerInvariant()); Deliver(r, target); }
            var line = ways.LastOrDefault(r => (r.Way == Way.Hold && r.A.Low) || r.Way == Way.Overlay);
            if (Game != null && line != null) OverlayLine(line.A.Title);
        }
        void Deliver(Routed r, Mon target)
        {
            if (r.Way == Way.Toast) Notify(r.A.Title, r.A.Text, r.A, Game != null);
            else if (r.Way == Way.Popup) ShowPopup(r.A, target);
            else if (r.Way == Way.PopupSame) ShowPopup(r.A, PopupScreen(true));
        }
        // ---- 1.3: monitors (tests and --snap use made-up layouts), the game's monitor, the pop-up's monitor
        public const string SnapMonitors = "\\\\.\\DISPLAY1 0 0 2560 1440 primary\n\\\\.\\DISPLAY2 2560 0 1920 1080";
        public List<Mon> Layout()
        {
            if (SnapMode) return Monitors.Parse(SnapMonitors);
            if (GearSettings.TestDir != null) { var f = Path.Combine(GearSettings.Dir, "test-monitors.txt"); return Monitors.Parse(File.Exists(f) ? File.ReadAllText(f) : "\\\\.\\DISPLAY1 0 0 1920 1080 primary"); }
            return Monitors.All();
        }
        Mon GameMonitor(List<Mon> mons)
        {
            if (SnapMode || GearSettings.TestDir != null)
            {
                var f = Path.Combine(GearSettings.Dir, "test-game-monitor.txt"); var dev = !SnapMode && File.Exists(f) ? File.ReadAllText(f).Trim() : null;
                return mons.FirstOrDefault(m => m.Device == dev) ?? mons.FirstOrDefault(m => m.Primary);
            }
            return Monitors.GameMonitor(mons, GameWatch.Known.Concat(S.ExtraGames));
        }
        // the pop-up's monitor now (null = none may be used); same = the game's monitor (only for "same as the game")
        public Mon PopupScreen(bool same) { var mons = Layout(); return Monitors.Choose(mons, GameMonitor(mons), same ? "same" : S.PopupMonitor); }
        void ShowPopup(GearAlert a, Mon mon)
        {
            if (mon == null) { Notify(a.Title, a.Text, a, Game != null); return; }
            var d = Devices.FirstOrDefault(x => x.Key == a.Key);
            var n = new Note { Title = a.Title, Text = a.Text, Key = a.Key, Kind = a.Key == "summary" ? null : DeviceIcons.KindOf(d != null ? d.Kind : null), State = DeviceIcons.StateOf(a.Kind) };
            if (!Sender.IsNew(n)) { Log("duplicate, not shown: " + a.Title); return; }
            Notifications++; Log("popup: " + a.Title + " on " + mon.Device + " " + S.PopupCorner);
            lock (PopupLog) { PopupLog.Add(a.Title + " @ " + mon.Device + " " + S.PopupCorner); if (PopupLog.Count > 30) PopupLog.RemoveAt(0); }
            if (SnapMode || GearSettings.TestDir != null || tray == null) return;   // tests never show a window
            var key = a.Key;
            try { Popups.Show(close => GearPopup.Card(n.Kind, n.State, n.Title, n.Text, close, () => { close(); OpenDevice(key); }), mon, S.PopupCorner, S.PopupSeconds); }
            catch (Exception e) { Log("popup failed: " + Family.OneLine(e.Message)); Notify(a.Title, a.Text, a, Game != null); return; }
            GearSound.Play(GearSound.For(S.SoundGame, S.GameVolume));
        }
        // Settings: "Try a pop-up" / "Identify" - only when the user clicks them
        public string TryPopup()
        {
            if (SnapMode || GearSettings.TestDir != null) return null;
            var mon = PopupScreen(false) ?? PopupScreen(true); if (mon == null) return null;
            Popups.Show(close => GearPopup.Card("mouse", "critical", "Mouse battery very low: 9%", "PRO X SUPERLIGHT 2 is at 9%. Plug in its cable or put it on its charger.", close, () => { close(); ShowMain("devices"); }), mon, S.PopupCorner, S.PopupSeconds);
            GearSound.Play(GearSound.For(S.SoundGame, S.GameVolume));
            return mon.Label;
        }
        public void Identify() { if (!SnapMode && GearSettings.TestDir == null) PopupStack.Identify(Layout()); }
        public void SetDeviceGame(string key, string type, string mode)
        {
            var k = type == null ? key : key + "|" + type;
            if (GameModes.Valid(mode)) S.DeviceGame[k] = mode; else S.DeviceGame.Remove(k);
            SaveSettings();
        }
        void Heard(string key, BatteryReading b)
        {
            var d = Devices.FirstOrDefault(x => x.Key == key); if (d == null) return;
            var was = d.Battery; d.Apply(b, DateTime.Now); d.Paused = false;
            Log("heard " + key + ": " + b);
            Fresh(d, DateTime.Now); Remember(new[] { d }, DateTime.Now);
            WriteStatus(); Fire();
        }
        // --snap: the 1.3.1 "waiting" card (a game started before Gear: the mouse is paired, its last known battery is shown)
        public void SnapWaiting()
        {
            var m = SnapMode ? Devices.FirstOrDefault(x => x.Kind == "mouse") : null; if (m == null) return;
            m.Waiting = true; m.LastKnown = true; m.Paused = true; m.Wired = false; m.Charging = false; m.Full = false; m.External = false; m.Via = "LIGHTSPEED receiver"; m.Battery = 66; m.Updated = DateTime.Now.AddMinutes(-12); m.Source = null;
            Fire();
        }
        void LinkChanged(string key, bool up)
        {
            var d = Devices.FirstOrDefault(x => x.Key == key); if (d == null) return;
            d.Asleep = !up; if (!up) { d.Charging = false; d.Note = "Asleep or switched off - move it to wake it."; } else d.Note = null;
            Log(key + " link " + (up ? "up" : "down"));
            if (up && Game == null) { Sched.LastMouse = DateTime.MinValue; var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; t.Tick += (s, e) => { t.Stop(); kick.Set(); }; t.Start(); }
            WriteStatus(); Fire();
        }
        void SetGame(string g)
        {
            if (g == Game) return;
            var was = Game; Game = g; Log(g != null ? "game: " + g : "game closed");
            if (g == null && was != null)
            {
                foreach (var a in Router.GameEnded(DateTime.Now)) Notify(a.Title, a.Text, a);
                Sched.LastMouse = DateTime.MinValue; kick.Set();   // catch up on the mouse now
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
            try { if (rememberedDirty) SaveRemembered(DateTime.Now); } catch { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (devWatch != null) devWatch.Dispose();
            WriteStatus(false);
            try { Source.Dispose(); } catch { }
            try { Bt.Dispose(); } catch { }
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
            var parts = VisibleDevices.Where(d => d.HasBattery && d.Battery.HasValue && d.Connected).Select(d => BatteryAlerts.Word(d) + " " + d.Battery + "%" + (d.Charging ? "+" : "")).ToList();
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
        // 1.2: a notification with the device's icon. One way only (NoteSender): a Windows toast when Gear's shortcut has its ID and
        // notifications are on; otherwise the tray balloon with the icon - Gear's own, or Hallow Hub's text-only "notify" when Hub hosts Gear's icon.
        public void Notify(string title, string text, GearAlert a = null, bool inGame = false)
        {
            Notifications++; Log("notify: " + title + (inGame ? " (in game, sound " + S.SoundGame + ")" : ""));
            lock (Shown) { Shown.Add(new[] { DateTime.Now.ToString("HH:mm"), title, text }); if (Shown.Count > 30) Shown.RemoveAt(0); }
            if (tray == null || GearSettings.TestDir != null || SnapMode) return;
            var n = MakeNote(title, text, a, Toasts.WindowsLight());
            n.Sound = inGame ? S.SoundGame : S.SoundNormal; int vol = inGame ? S.GameVolume : S.SoundVolume;   // 1.3
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var r = Sender.Send(n); Log("shown as: " + r.ToString().ToLowerInvariant());
                if ((r == NoteRoute.Toast || r == NoteRoute.Balloon) && n.Sound == GearSound.Hallow) GearSound.Play(GearSound.Chime(vol));   // the toast itself is silent
            });
        }
        // the device (from the alert's key) -> its icon files
        public Note MakeNote(string title, string text, GearAlert a, bool light)
        {
            var n = new Note { Title = title, Text = text };
            if (a == null || a.Key == "summary") return n;
            var d = Devices.FirstOrDefault(x => x.Key == a.Key);
            n.Key = a.Key; n.Kind = DeviceIcons.KindOf(d != null ? d.Kind : null); n.State = DeviceIcons.StateOf(a.Kind);
            try { n.LogoFile = DeviceIcons.PngFile(n.Kind, n.State, 96, light, Program.AccentHex); n.IcoFile = DeviceIcons.IcoFile(n.Kind, n.State, light, Program.AccentHex); }
            catch (Exception e) { Log("icon: " + Family.OneLine(e.Message)); }
            return n;
        }
        public string ToastWord { get { return toastState == ToastState.On ? "on" : toastState == ToastState.Off ? "off" : "fallback"; } }
        ToastState ToastNow()
        {
            if ((DateTime.Now - toastChecked).TotalSeconds >= 60) { toastChecked = DateTime.Now; var s = Toasts.State(); if (s != toastState) Log("toasts: " + s); toastState = s; }
            return toastState;
        }
        void SetupNotes()
        {
            Sender.Toasts = ToastNow;
            Sender.HubHosts = () => hubHosts;
            Sender.ShowToast = n => { WinToast.Show(Toasts.Aumid, WinToast.Create(Toasts.Xml(n), Toasts.Tag(n), args => ui.BeginInvoke(new Action(() => OpenDevice(Toasts.KeyFromLaunch(args)))))); return true; };
            Sender.SendHub = n => Family.Error(Family.Send("Hub", "notify Gear\t" + n.Title + "\t" + n.Text, 2000), "no answer") == null;
            Sender.ShowBalloon = n => { bool ok = false; var op = ui.BeginInvoke(new Action(() => ok = Balloon(n))); op.Wait(TimeSpan.FromSeconds(5)); return ok; };
            // Gear's own Start menu shortcut gets the AppUserModelID toasts need (per user; only Gear's shortcut, only if it points at this exe)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ShortcutNote = Toasts.EnsureShortcutId(Assembly.GetExecutingAssembly().Location); Log("shortcut id: " + ShortcutNote);
                toastChecked = DateTime.MinValue; ToastNow(); ui.BeginInvoke(new Action(() => WriteStatus()));
            });
        }
        // Gear's own tray icon (shown for a few seconds when Hub hosts it and hides it), the device icon as the balloon's large icon
        bool Balloon(Note n)
        {
            if (tray == null) return false;
            if (hubLists)
            {
                balloonOwn = true; tray.Visible = true;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; t.Tick += (s, e) => { t.Stop(); balloonOwn = false; SetHosted(hubHosts, hubLists); }; t.Start();
            }
            if (!Toasts.BalloonWithIcon(tray, n.Title, n.Text, n.IcoFile, n.Sound != null && n.Sound != GearSound.Windows)) tray.ShowBalloonTip(6000, n.Title, n.Text, WF.ToolTipIcon.None);
            return true;
        }
        // clicking a toast opens Gear on that device
        public void OpenDevice(string key)
        {
            ShowMain("devices");
            if (main != null && key != null) main.FocusDevice(key);
        }        public void ApplyAccent()
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
                    pending = r; if (main != null) main.ShowUpdate("Update to v" + Updater.Show(r.Version));
                    Say("Hallow Gear v" + Updater.Show(r.Version) + " is out - click Update.");
                }));
            }) { IsBackground = true }.Start();
        }
        public void InstallUpdate()
        {
            if (pending == null) return;
            if (!Dialogs.Confirm(main, "Install Hallow Gear v" + Updater.Show(pending.Version) + "?", "It downloads from GitHub, checks the file and puts the new version in place. Your settings and history stay.", "Update")) return;
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
        // 1.0 fields unchanged (Hallow Overlay 1.20+ reads them); 1.1 adds "bluetooth", "lastSeen", "alerts" and the kinds earbuds / controller / other
        // 1.2.2 adds "id" (opaque for Bluetooth devices, see PublicId; null when the caller didn't give one)
        public static Dictionary<string, object> DeviceJson(GearDevice d, bool alerts = true, string id = null)
        {
            return new Dictionary<string, object> { { "id", id },
                { "kind", d.Kind }, { "name", d.Name }, { "battery", d.HasBattery ? (object)d.Battery : null }, { "state", d.State }, { "wired", d.Wired },
                { "charging", d.Charging }, { "full", d.Full }, { "connected", d.Connected }, { "asleep", d.Asleep }, { "estimated", d.Estimated },
                { "paused", d.Paused }, { "updated", d.Updated.HasValue ? Family.Time(d.Updated.Value) : null },
                { "bluetooth", d.Bluetooth }, { "lastSeen", d.LastSeen.HasValue ? Family.Time(d.LastSeen.Value) : null }, { "alerts", alerts },
                { "lastKnown", d.LastKnown }, { "waiting", d.Waiting } };   // 1.3.1: remembered values (not fresh); paired, nothing heard yet during a game
        }
        public Dictionary<string, object> StatusData(bool running)
        {
            var d = new Dictionary<string, object> { { "app", "Gear" }, { "version", Program.Version }, { "updated", Family.Time(DateTime.Now) }, { "running", running } };
            var visible = VisibleDevices.ToList();   // hidden Bluetooth devices aren't listed (so Overlay doesn't show them either)
            d["devices"] = visible.Select(x => (object)DeviceJson(x, AlertsOn(x), PublicId(x.Key))).ToList();
            d["lowAt"] = S.Low1; d["veryLowAt"] = S.Low2;
            d["bluetooth"] = S.Bluetooth; d["hiddenDevices"] = Devices.Count - visible.Count;
            d["low"] = visible.Where(x => x.HasBattery && x.Battery.HasValue && !x.Charging && !x.Full && x.Connected && x.Battery <= S.Low1).Select(x => x.Name).ToList();
            d["game"] = Game; d["polling"] = Polling; d["held"] = Router.Held.Count; d["gameAsk"] = S.GameAsk;   // 1.3.1
            var line = Game != null ? Router.OverlayLine : null;
            d["alert"] = line == null ? null : new Dictionary<string, object> { { "text", line.Title }, { "level", line.Kind }, { "device", PublicId(line.Key) }, { "time", Family.Time(line.At) },
                { "deviceName", DeviceName(line.Key) } };   // 1.2.2: "device" is the device's opaque id (never a Bluetooth address); deviceName added
            d["lastRead"] = LastRead.HasValue ? Family.Time(LastRead.Value) : null;
            d["startsWithWindows"] = StartsWithWindows(); d["look"] = Program.Light ? "light" : "dark";
            d["toasts"] = ToastWord;   // 1.2: "on" (Windows toasts with the device icon), "fallback" (tray balloon / Hub), "off" (switched off in Windows)
            d["lastCrash"] = Crash.LastTime() == null ? null : Family.Time(Crash.LastTime().Value);
            if (GearSettings.TestDir != null) { lock (log) d["log"] = log.ToArray(); d["notifications"] = Notifications; }
            return d;
        }
        string DeviceName(string key) { var x = Devices.FirstOrDefault(z => z.Key == key); return x != null ? x.Name : null; }
        public void WriteStatus(bool running = true) { if (!SnapMode) Family.WriteStatus(Family.Json(StatusData(running))); }
        string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "ping": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Gear" }, { "version", Program.Version } });
                case "status": { var d = OnUiValue(() => StatusData(true)); if (d == null) return "error no answer in time"; return "ok " + Family.Json(d); }
                case "open": return OnUi(() => { var p = arg.Length > 0 ? MainWindow.PageId(arg) : null; if (arg.Length > 0 && p == null) return "error no page called " + arg; ShowMain(p); return "ok"; });
                case "refresh": return "ok " + Family.Json(PipeRefresh(DateTime.Now));   // 1.2.2
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
            foreach (var d in VisibleDevices.Where(x => x.HasBattery).Take(5))
                items.Add(new Dictionary<string, object> { { "id", "open-devices" }, { "label", d.Name + ": " + Fmt.Pct(d.Battery, "-") + " \u00B7 " + d.StateLabel }, { "enabled", false } });
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
                case "test-read": ReadNow(true, true, false, Game == null); return "ok " + Devices.Count;
                case "test-bt-switch": SetBluetooth(arg == "on"); if (arg == "on") ReadNow(false, false, false, true); return "ok " + Devices.Count;
                case "test-bt-hide": { var p = arg.Split(' '); SetBtHidden(p[0], p.Length > 1 && p[1] == "on"); WriteStatus(); return "ok"; }
                case "test-bt-alerts": { var p = arg.Split(' '); SetBtAlerts(p[0], p.Length > 1 && p[1] == "on"); return "ok"; }
                case "test-notes": lock (Shown) return "ok " + Family.Json(Shown.Select(n => n[1]).ToList());
                case "test-popups": lock (PopupLog) return "ok " + Family.Json(PopupLog.ToList());
                case "test-monitors": File.WriteAllText(Path.Combine(GearSettings.Dir, "test-monitors.txt"), arg.Replace(";", "\n")); return "ok " + Layout().Count;
                case "test-game-monitor": File.WriteAllText(Path.Combine(GearSettings.Dir, "test-game-monitor.txt"), arg); return "ok";
                case "test-set":
                    {   // test-set <GearSettings field> <value> (1.3 tests: the game routes, sound)
                        var p = arg.Split(new[] { ' ' }, 2); var f = typeof(GearSettings).GetField(p[0]); if (f == null) return "error no field";
                        f.SetValue(S, f.FieldType == typeof(int) ? (object)int.Parse(p[1]) : p.Length > 1 ? p[1] : null); SaveSettings(); return "ok";
                    }
                case "test-device-game":
                    {   // test-device-game <device key (may contain spaces)>[|<type>] <mode>
                        var sp = arg.LastIndexOf(' '); if (sp < 0) return "error usage"; var k = arg.Substring(0, sp); var bar = k.IndexOf('|');
                        SetDeviceGame(bar < 0 ? k : k.Substring(0, bar), bar < 0 ? null : k.Substring(bar + 1), arg.Substring(sp + 1)); return "ok";
                    }
                case "test-route":
                    {   // test-route <device key (may contain spaces)> <alert kind>
                        var sp = arg.LastIndexOf(' '); if (sp < 0) return "error usage"; var mon = PopupScreen(false);
                        return "ok " + GameRoute.Decide(S, arg.Substring(0, sp), arg.Substring(sp + 1), Game != null, mon != null).ToString().ToLowerInvariant() + (mon != null ? " " + mon.Device : "");
                    }
                case "test-note-files":
                    {   // 1.2: the icon files the latest device alert would use (written to <test dir>\icons; nothing is shown)
                        var last = Router.Held.LastOrDefault(); var key = arg.Length > 0 ? arg : last != null ? last.Key : null; if (key == null) return "error no device";
                        var n = MakeNote("t", "t", new GearAlert { Key = key, Kind = "low" }, false);
                        return "ok " + Family.Json(new Dictionary<string, object> { { "kind", n.Kind }, { "state", n.State }, { "logo", n.LogoFile }, { "ico", n.IcoFile }, { "xml", Toasts.Xml(n) } });
                    }
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
                case "test-bt":
                    {   // test-bt <index> <percent> [off]  (a made-up Bluetooth device's battery / connection, then a Bluetooth read)
                        var fb = Bt as FakeBtSource; if (fb == null) return "error no fake bluetooth";
                        var p = arg.Split(' '); var e = fb.Entries[int.Parse(p[0])]; e.Battery = int.Parse(p[1]); e.Connected = !p.Contains("off");
                        ReadNow(false, false, false, Game == null); return "ok";
                    }
                case "test-bt-hear": { var fb = Bt as FakeBtSource; var p = arg.Split(' '); fb.Hear(p[0], int.Parse(p[1])); return "ok"; }
                case "test-bt-reads": return "ok " + ((FakeBtSource)Bt).Reads;
                case "test-asks": return "ok " + Family.Json(new Dictionary<string, object> { { "reads", fake.Reads }, { "mouse", fake.MouseAsks }, { "headset", fake.HeadsetAsks }, { "gentle", fake.GentleAsks } });
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

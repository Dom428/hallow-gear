// Hallow Gear - the gear: what's plugged in (Logitech VID 046D HID collections), what each device says about its battery, and the
// merge of a LIGHTSPEED mouse that's also on its USB cable ("wired (charging)"). All reads; see Hidpp.cs / A50.cs for the allow-lists.
// While a game runs nothing is ASKED of the mouse or keyboard (unless Settings says so) - their reports are only listened to.
// 1.1: any HID++ 2.0 device on any receiver slot 1-6 (LIGHTSPEED, Unifying, Bolt) - mouse, keyboard, headset... - gets a card of its own
// kind, and so does a Logitech device that talks HID++ on its own USB cable or straight over Bluetooth (device index 0xFF).
using System;
using System.Collections.Generic;
using System.Linq;

namespace HallowGear
{
    public class GearDevice
    {
        public string Key, Kind, Name, Via, Source, Note;
        public int? Battery; public bool Charging, Full, External, Wired, Connected = true, Asleep, HasBattery = true, Estimated, Paused;
        public DateTime? Updated;
        public bool Bluetooth; public DateTime? LastSeen;   // 1.1: a card from Windows' Bluetooth devices (not HID++); when it was last connected
        public GearDevice Clone() { return (GearDevice)MemberwiseClone(); }
        public string State
        {
            get
            {
                if (!Connected) return "not-connected";
                if (Asleep) return "asleep";
                if (!HasBattery) return "wired";
                if (Full) return "full";
                if (Charging) return "charging";
                if (Wired) return "wired";
                return Battery.HasValue ? "on-battery" : "wireless";
            }
        }
        public string StateLabel
        {
            get
            {
                if (Bluetooth) return State == "not-connected" ? "Not connected" : State == "asleep" ? "Asleep" : "Connected \u00B7 Bluetooth";
                switch (State)
                {
                    case "not-connected": return "Not connected";
                    case "asleep": return "Asleep";
                    case "full": return Wired ? "Fully charged \u00B7 wired" : Kind == "headset" && External ? "Fully charged \u00B7 docked" : "Fully charged";
                    case "charging": return Wired ? "Charging \u00B7 wired" : Kind == "headset" ? "Charging \u00B7 docked" : "Charging";
                    case "wired": return HasBattery ? "Wired" : "Wired \u00B7 connected";
                    case "on-battery": return Via == "Bluetooth" ? "On battery \u00B7 Bluetooth" : "On battery \u00B7 wireless";
                }
                return "Wireless";
            }
        }
        public void Apply(BatteryReading b, DateTime now)
        {
            if (b == null) return;
            if (b.Percent.HasValue) Battery = b.Percent;
            Charging = b.Charging; Full = b.Full; External = b.External; Estimated = b.Estimated; Source = b.Source; Updated = now; HasBattery = true; Asleep = false; Connected = true;
            if (Bluetooth) LastSeen = now;
        }
    }

    public interface IGearSource : IDisposable
    {
        // askMice: HID++ questions to mice / keyboards allowed now; askHeadset: the A50 battery GET allowed now; rescan: enumerate again
        List<GearDevice> Read(bool askMice, bool askHeadset, bool rescan);
        event Action<string, BatteryReading> Heard;   // a battery report heard without asking (device key, reading)
        event Action<string, bool> Link;              // a receiver said a device's link went up / down
        List<string> Log { get; }
    }

    public static class GearMerge
    {
        public static string KeyFor(string kind, string name) { return (kind ?? "device") + ":" + (name ?? "").Trim().ToLowerInvariant(); }
        // the same mouse seen on the receiver and on its own cable -> one card, wired
        public static List<GearDevice> Merge(IEnumerable<GearDevice> all)
        {
            var outList = new List<GearDevice>();
            foreach (var d in all)
            {
                var same = outList.FirstOrDefault(x => x.Key == d.Key);
                if (same == null) { outList.Add(d.Clone()); continue; }
                if (!d.Wired && !same.Wired)
                {   // 1.1: two wireless devices with the same name - a second identical device (another slot / receiver) gets its own card,
                    // unless one of them can't be reached (a multi-host device seen on two links: keep the one that answers)
                    bool dUp = d.Connected && !d.Asleep, sUp = same.Connected && !same.Asleep;
                    if (dUp && sUp) { var c2 = d.Clone(); int n = 2; while (outList.Any(x => x.Key == d.Key + "#" + n)) n++; c2.Key = d.Key + "#" + n; c2.Name = d.Name + " (" + n + ")"; outList.Add(c2); }
                    else if (dUp) outList[outList.IndexOf(same)] = d.Clone();
                    continue;
                }
                var wired = d.Wired ? d : same.Wired ? same : null; var other = wired == d ? same : d;
                var m = (wired ?? same).Clone();
                m.Wired = wired != null; m.Connected = d.Connected || same.Connected; m.Asleep = false;
                if (!m.Battery.HasValue && other.Battery.HasValue)
                { m.Battery = other.Battery; m.Charging = other.Charging; m.Full = other.Full; m.External = other.External; m.Updated = other.Updated; m.Source = other.Source; m.Estimated = other.Estimated; m.HasBattery = true; }
                if (other.HasBattery) m.HasBattery = true;
                m.Paused = d.Paused && same.Paused;
                m.Via = "USB cable + " + (other.Via != null && other.Via.EndsWith("receiver") ? other.Via : "LIGHTSPEED receiver");
                outList[outList.IndexOf(same)] = m;
            }
            return outList.OrderBy(x => x.Bluetooth ? (x.Connected ? 1 : 2) : 0).ThenBy(x => x.Kind == "mouse" ? 0 : x.Kind == "headset" ? 1 : x.Kind == "keyboard" ? 2 : 3).ThenBy(x => x.Name).ToList();
        }
        // 1.1: a Logitech device that talks HID++ straight over Bluetooth is read through HID++ - its Windows Bluetooth card is dropped
        public static List<GearDevice> WithBluetooth(List<GearDevice> logi, IEnumerable<GearDevice> bt)
        {
            var names = new HashSet<string>(logi.Where(x => x.Via == "Bluetooth").Select(x => (x.Name ?? "").Trim().ToLowerInvariant()));
            return Merge(logi.Concat(bt.Where(x => !names.Contains((x.Name ?? "").Trim().ToLowerInvariant()))));
        }
    }

    // the real thing: Logitech receivers (HID++ 2.0 per slot), Logitech HID++ devices on a cable or on Bluetooth (index 0xFF) and the A50
    public sealed class LogiSource : IGearSource
    {
        public event Action<string, BatteryReading> Heard;
        public event Action<string, bool> Link;
        readonly List<string> log = new List<string>(); public List<string> Log { get { return log; } }
        readonly GearSettings s;
        List<HidInfo> infos; readonly Dictionary<string, Holder> holders = new Dictionary<string, Holder>();
        static readonly HashSet<ushort> ReceiverPids = new HashSet<ushort> { 0xC52B, 0xC52F, 0xC531, 0xC532, 0xC534, 0xC537, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548, 0xC54D };
        static readonly HashSet<ushort> Skip = new HashSet<ushort> { 0xC232 };   // G HUB's virtual keyboard
        // where the HID collections come from and how one is opened (the tests put fake ones here; nothing else changes)
        public Func<List<HidInfo>> Enumerator = () => HidNative.Enumerate(0x046D);
        public Func<HidInfo, IReportPort> Opener = i => HidPort.Open(i);

        class Slot { public HidppDevice Dev; public string Name; public int? Type; public GearDevice Last; public bool Seen; }
        class Holder
        {
            public string Id; public ushort Pid; public IReportPort Long, Short; public bool Receiver, ScannedAll; public string Via; public Dictionary<int, Slot> Slots = new Dictionary<int, Slot>();
            public A50Reader A50; public IReportPort A50Port; public GearDevice A50Last;
            public void Close() { if (Long != null) Long.Dispose(); if (Short != null) Short.Dispose(); if (A50Port != null) A50Port.Dispose(); Long = Short = A50Port = null; }
        }
        public LogiSource(GearSettings settings) { s = settings; }
        void L(string m) { lock (log) { log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + m); if (log.Count > 200) log.RemoveAt(0); } }

        static bool IsHidpp(HidInfo i) { return (i.UsagePage == 0xFF00 || i.UsagePage == 0xFF43) && (i.InLen == 7 || i.InLen == 20); }
        // a HID collection that Windows' Bluetooth stack made (LE: the HID service 0x1812; classic: 0x1124)
        public static bool IsBluetoothPath(string path)
        {
            var p = (path ?? "").ToLowerInvariant();
            return p.Contains("{00001812-0000-1000-8000-00805f9b34fb}") || p.Contains("{00001124-0000-1000-8000-00805f9b34fb}") || p.Contains("vid&02046d") || p.Contains("vid&0002046d");
        }
        public static string ReceiverName(ushort pid, string product)
        {
            var p = product ?? "";
            if (pid == 0xC548 || p.IndexOf("bolt", StringComparison.OrdinalIgnoreCase) >= 0) return "Bolt receiver";
            if (pid == 0xC52B || pid == 0xC532 || p.IndexOf("unifying", StringComparison.OrdinalIgnoreCase) >= 0) return "Unifying receiver";
            return "LIGHTSPEED receiver";
        }

        public List<GearDevice> Read(bool askMice, bool askHeadset, bool rescan)
        {
            var now = DateTime.Now;
            if (infos == null || rescan)
            {
                infos = Enumerator() ?? new List<HidInfo>();
                // close what isn't there any more
                foreach (var k in holders.Keys.ToList()) if (!infos.Any(i => Group(i) == k)) { holders[k].Close(); holders.Remove(k); }
                foreach (var h in holders.Values) h.ScannedAll = false;
                L("enumerated " + infos.Count + " Logitech HID collections");
            }
            var found = new List<GearDevice>();
            foreach (var g in infos.GroupBy(Group))
            {
                var first = g.First(); if (Skip.Contains(first.Pid)) continue;
                try
                {
                    if (first.Pid == A50Proto.PidGen5) { var d = ReadA50(g.Key, g.ToList(), askHeadset, now); if (d != null) found.Add(d); continue; }
                    var hp = g.Where(IsHidpp).ToList();
                    bool receiver = ReceiverPids.Contains(first.Pid) || g.Any(i => (i.Product ?? "").IndexOf("receiver", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (receiver) { if (hp.Count > 0) found.AddRange(ReadReceiver(g.Key, first.Pid, ReceiverName(first.Pid, g.Select(i => i.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))), hp, askMice, now)); continue; }
                    var cd = ReadCorded(g.Key, g.ToList(), hp, askMice, now); if (cd != null) found.Add(cd);
                }
                catch (Exception e) { L("read " + first.Pid.ToString("X4") + " failed: " + e.Message); }
            }
            return GearMerge.Merge(found);
        }
        // one physical device = its product id (+ Bluetooth or not: the same pid can't be both, but keep them apart anyway)
        static string Group(HidInfo i) { return i.Pid.ToString("X4") + (IsBluetoothPath(i.Path) ? "-bt" : ""); }

        Holder Get(string id, ushort pid, IEnumerable<HidInfo> hp)
        {
            Holder h; if (holders.TryGetValue(id, out h) && (h.Long != null || h.Short != null || h.A50Port != null)) return h;
            if (h == null) holders[id] = h = new Holder { Id = id, Pid = pid };
            foreach (var i in hp)
            {
                if (i.InLen == 20 && h.Long == null) h.Long = Opener(i);
                else if (i.InLen == 7 && h.Short == null) h.Short = Opener(i);
            }
            var hh = h;
            Action<byte[]> listen = r => Listen(hh, r);
            if (h.Long != null) h.Long.Report += listen; if (h.Short != null) h.Short.Report += listen;
            L(string.Format("opened {0}: long {1}, short {2}", id, h.Long != null, h.Short != null));
            return h;
        }
        void Listen(Holder h, byte[] r)
        {
            foreach (var kv in h.Slots.ToList())
            {
                var sl = kv.Value; if (sl.Last == null || sl.Dev == null) continue;
                var b = Hidpp.Passive(r, (byte)kv.Key, sl.Dev.BatteryFeature, sl.Dev.BatteryIndex, sl.Dev.SocSupported);
                if (b != null) { var cb = Heard; if (cb != null) cb(sl.Last.Key, b); }
                var up = Hidpp.LinkUp(r, (byte)kv.Key);
                if (up.HasValue && h.Receiver) { var lk = Link; if (lk != null) lk(sl.Last.Key, up.Value); }
            }
        }
        // a slot's kind: what the device said (getDeviceType), else what it said in an earlier session, else "mouse"
        string KindOf(Slot sl, string slotKey)
        {
            if (sl.Type.HasValue) return Hidpp.TypeName(sl.Type.Value);
            string k; return s.KnownSlotKinds != null && s.KnownSlotKinds.TryGetValue(slotKey, out k) && !string.IsNullOrEmpty(k) ? k : "mouse";
        }

        IEnumerable<GearDevice> ReadReceiver(string id, ushort pid, string via, List<HidInfo> hp, bool ask, DateTime now)
        {
            var h = Get(id, pid, hp); h.Receiver = true; h.Via = via;
            if (h.Long == null && h.Short == null) yield break;
            var slots = !h.ScannedAll && ask ? Enumerable.Range(1, 6).ToList() : h.Slots.Keys.Concat(KnownSlots(pid)).Distinct().OrderBy(x => x).ToList();
            foreach (var n in slots)
            {
                Slot sl; if (!h.Slots.TryGetValue(n, out sl)) h.Slots[n] = sl = new Slot { Dev = new HidppDevice(h.Long, h.Short, (byte)n) };
                var slotKey = pid.ToString("X4") + ":" + n;
                string known; s.KnownSlots.TryGetValue(slotKey, out known);
                if (!ask)
                {   // a game is running: what we knew, marked paused
                    if (sl.Last != null) { var c = sl.Last.Clone(); c.Paused = true; yield return c; }
                    else if (known != null) { var k = KindOf(sl, slotKey); yield return new GearDevice { Key = GearMerge.KeyFor(k, known), Kind = k, Name = known, Via = via, Paused = true, Note = "Not read yet - waiting for the game to close." }; }
                    continue;
                }
                int ver = sl.Dev.Ping(); int err = sl.Dev.LastError;
                if (ver >= 2)
                {
                    if (sl.Name == null) { sl.Name = sl.Dev.Name() ?? known ?? "Logitech device"; sl.Type = sl.Dev.DeviceType(); }
                    var kind = KindOf(sl, slotKey);
                    var d = new GearDevice { Key = GearMerge.KeyFor(kind, sl.Name), Kind = kind, Name = sl.Name, Via = via };
                    var b = sl.Dev.Battery();
                    if (b != null) d.Apply(b, now);
                    else if (sl.Dev.BatteryFeature == 0 && sl.Dev.LastError >= 0) { d.HasBattery = false; d.Note = "It doesn't report a battery."; }
                    else if (sl.Last != null && sl.Last.Battery.HasValue) { d.Battery = sl.Last.Battery; d.Updated = sl.Last.Updated; d.Source = sl.Last.Source; d.Note = "The last battery read didn't answer."; }
                    sl.Last = d; sl.Seen = true;
                    bool save = false;
                    if (!s.KnownSlots.ContainsKey(slotKey) || s.KnownSlots[slotKey] != sl.Name) { s.KnownSlots[slotKey] = sl.Name; save = true; }
                    if (sl.Type.HasValue && (!s.KnownSlotKinds.ContainsKey(slotKey) || s.KnownSlotKinds[slotKey] != kind)) { s.KnownSlotKinds[slotKey] = kind; save = true; }
                    if (save) s.Save();
                    L(string.Format("{0} slot {1}: HID++ {2}, {3} '{4}', battery {5}", via, n, ver, kind, sl.Name, b == null ? "none" : b.ToString()));
                    yield return d;
                }
                else
                {
                    if (err == 8 && known == null) { h.Slots.Remove(n); continue; }   // an empty slot
                    var name = sl.Name ?? known; if (name == null) { if (err != 8) L("slot " + n + ": no answer (" + Hidpp.ErrorName(err) + ")"); h.Slots.Remove(n); continue; }
                    var d = Offline(sl.Last, KindOf(sl, slotKey), name, via);
                    sl.Last = d; L("slot " + n + ": '" + name + "' " + Hidpp.ErrorName(err));
                    yield return d;
                }
            }
            if (ask) h.ScannedAll = true;
        }
        IEnumerable<int> KnownSlots(ushort pid)
        {
            var pre = pid.ToString("X4") + ":"; int n;
            return s.KnownSlots.Keys.Where(k => k.StartsWith(pre)).Select(k => int.TryParse(k.Substring(pre.Length), out n) ? n : 0).Where(x => x >= 1 && x <= 6).ToList();
        }

        // a Logitech device on its own: on its USB cable, or (1.1) straight over Bluetooth. HID++ device index 0xFF.
        GearDevice ReadCorded(string id, List<HidInfo> all, List<HidInfo> hp, bool ask, DateTime now)
        {
            var product = all.Select(i => i.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            bool kbd = all.Any(i => i.UsagePage == 1 && i.Usage == 6), mouse = all.Any(i => i.UsagePage == 1 && i.Usage == 2);
            bool bt = all.Any(i => IsBluetoothPath(i.Path));
            Holder h = null; Slot sl = null; bool ports = false;
            if (hp.Count > 0)
            {
                h = Get(id, all[0].Pid, hp); ports = h.Long != null || h.Short != null;
                if (!h.Slots.TryGetValue(0xFF, out sl)) h.Slots[0xFF] = sl = new Slot { Dev = new HidppDevice(h.Long, h.Short, 0xFF) };
                if (ask && sl.Name == null && ports && sl.Dev.Ping() >= 2) { sl.Name = sl.Dev.Name(); sl.Type = sl.Dev.DeviceType(); }
            }
            var kind = sl != null && sl.Type.HasValue ? Hidpp.TypeName(sl.Type.Value) : kbd ? "keyboard" : mouse ? "mouse" : null;
            if (kind == null) return null;
            var name = (sl != null ? sl.Name : null) ?? product ?? ("Logitech " + kind);
            var d = new GearDevice { Key = GearMerge.KeyFor(kind, name), Kind = kind, Name = name, Wired = !bt, Via = bt ? "Bluetooth" : "USB cable" };
            bool talks = sl != null && sl.Name != null && ports;   // it answered HID++
            if (!talks)
            {   // no HID++: a keyboard is just there (no battery); a mouse keeps what it said before
                if (kind == "keyboard" && !bt) { d.HasBattery = false; d.Note = "A wired keyboard - no battery."; if (sl != null) sl.Last = d; return d; }
                if (sl != null && sl.Last != null) { d = sl.Last.Clone(); d.Paused = !ask && d.HasBattery; }
            }
            else if (ask)
            {
                var b = sl.Dev.Battery();
                if (b != null) d.Apply(b, now);
                else if (sl.Dev.BatteryFeature == 0 && sl.Dev.LastError >= 0) { d.HasBattery = false; if (kind == "keyboard" && !bt) d.Note = "A wired keyboard - no battery."; }
                else if (sl.Last != null && sl.Last.Battery.HasValue) { d.Battery = sl.Last.Battery; d.Updated = sl.Last.Updated; d.Source = sl.Last.Source; }
                L(string.Format("{0} {1}: {2} '{3}', battery {4}", bt ? "bluetooth" : "corded", id, kind, name, b == null ? "none" : b.ToString()));
            }
            else if (sl.Last != null) { d = sl.Last.Clone(); d.Paused = d.HasBattery; }
            d.Wired = !bt;
            if (sl != null) sl.Last = d;
            return d;
        }

        GearDevice ReadA50(string id, List<HidInfo> all, bool ask, DateTime now)
        {
            var ctl = all.FirstOrDefault(A50Proto.IsControl);
            Holder h; if (!holders.TryGetValue(id, out h)) holders[id] = h = new Holder { Id = id, Pid = A50Proto.PidGen5 };
            if (h.A50Port == null && ctl != null)
            {
                h.A50Port = Opener(ctl);
                if (h.A50Port != null)
                {
                    h.A50 = new A50Reader(h.A50Port); var hh = h;
                    h.A50Port.Report += r => { var b = A50Proto.Battery(r); if (b != null) { var cb = Heard; if (cb != null) cb("headset:astro a50", b); } };
                }
                L("A50 control interface: " + (ctl == null ? "not found" : ctl.ToString()) + ", opened " + (h.A50Port != null));
            }
            var d = h.A50Last != null ? h.A50Last.Clone() : new GearDevice { Key = "headset:astro a50", Kind = "headset", Name = "ASTRO A50", Via = "base station (USB)" };
            d.Connected = true; d.Paused = false;
            if (h.A50 == null) { d.Battery = null; d.Note = "The base station is connected, but its control interface couldn't be opened, so the battery isn't known."; }
            else if (ask)
            {
                var b = h.A50.Battery();
                if (b != null) { d.Apply(b, now); d.Note = null; }
                else d.Note = "The base station didn't answer the battery question this time.";
                L("A50 battery: " + (b == null ? "no answer" : b.ToString()));
            }
            h.A50Last = d;
            return d;
        }
        // a paired device that doesn't answer (the receiver says 0x08 or 0x09, or nothing comes back): asleep or switched off - never "not
        // connected" (that's for gear whose receiver / cable isn't there). It keeps its last battery reading.
        public static GearDevice Offline(GearDevice last, string kind, string name, string via = "LIGHTSPEED receiver")
        {
            var d = last != null ? last.Clone() : new GearDevice { Key = GearMerge.KeyFor(kind, name), Kind = kind, Name = name, Via = via };
            d.Asleep = true; d.Connected = true; d.Charging = false; d.Full = false; d.Paused = false;
            d.Note = "Asleep or switched off - " + (kind == "mouse" ? "move it" : kind == "keyboard" ? "press a key" : "switch it on") + " to wake it.";
            return d;
        }
        public List<string> SentSummary() { return SentLog.Summary(); }
        public void Dispose() { foreach (var h in holders.Values) h.Close(); holders.Clear(); }
    }

    // made-up gear for --snap and the tests (never opens a device)
    public sealed class FakeSource : IGearSource
    {
        public event Action<string, BatteryReading> Heard;
        public event Action<string, bool> Link;
        public List<string> Log { get { return new List<string>(); } }
        public List<GearDevice> Devices = new List<GearDevice>();
        public int Reads, MouseAsks, HeadsetAsks;
        public FakeSource() { Reset(); }
        public void Reset()
        {
            var t = DateTime.Now.AddMinutes(-1);
            Devices = new List<GearDevice> {
                new GearDevice { Key = "mouse:pro x superlight 2", Kind = "mouse", Name = "PRO X SUPERLIGHT 2", Via = "LIGHTSPEED receiver", Battery = 64, Updated = t, Source = "HID++ Unified Battery (0x1004)" },
                new GearDevice { Key = "headset:astro a50", Kind = "headset", Name = "ASTRO A50", Via = "base station (USB)", Battery = 100, Full = true, External = true, Updated = t, Source = "ASTRO A50 base station (battery GET 0x06)" },
                new GearDevice { Key = "keyboard:g713", Kind = "keyboard", Name = "G713", Via = "USB cable", Wired = true, HasBattery = false, Note = "A wired keyboard - no battery." } };
        }
        public List<GearDevice> Read(bool askMice, bool askHeadset, bool rescan)
        {
            Reads++; if (askMice) MouseAsks++; if (askHeadset) HeadsetAsks++;
            return Devices.Select(d => { var c = d.Clone(); if (!askMice && c.Kind != "headset" && c.HasBattery) c.Paused = true; return c; }).ToList();
        }
        public void Hear(string key, BatteryReading b) { var h = Heard; if (h != null) h(key, b); }
        public void SayLink(string key, bool up) { var h = Link; if (h != null) h(key, up); }
        public void Dispose() { }
    }
}

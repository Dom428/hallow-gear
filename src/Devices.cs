// Hallow Gear - the gear: what's plugged in (Logitech VID 046D HID collections), what each device says about its battery, and the
// merge of a LIGHTSPEED mouse that's also on its USB cable ("wired (charging)"). All reads; see Hidpp.cs / A50.cs for the allow-lists.
// While a game runs nothing is ASKED of the mouse or keyboard (unless Settings says so) - their reports are only listened to.
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
                switch (State)
                {
                    case "not-connected": return "Not connected";
                    case "asleep": return "Asleep";
                    case "full": return Wired ? "Fully charged \u00B7 wired" : Kind == "headset" && External ? "Fully charged \u00B7 docked" : "Fully charged";
                    case "charging": return Wired ? "Charging \u00B7 wired" : Kind == "headset" ? "Charging \u00B7 docked" : "Charging";
                    case "wired": return HasBattery ? "Wired" : "Wired \u00B7 connected";
                    case "on-battery": return "On battery \u00B7 wireless";
                }
                return "Wireless";
            }
        }
        public void Apply(BatteryReading b, DateTime now)
        {
            if (b == null) return;
            if (b.Percent.HasValue) Battery = b.Percent;
            Charging = b.Charging; Full = b.Full; External = b.External; Estimated = b.Estimated; Source = b.Source; Updated = now; HasBattery = true; Asleep = false; Connected = true;
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
                var wired = d.Wired ? d : same.Wired ? same : null; var other = wired == d ? same : d;
                var m = (wired ?? same).Clone();
                m.Wired = wired != null; m.Connected = d.Connected || same.Connected; m.Asleep = false;
                if (!m.Battery.HasValue && other.Battery.HasValue)
                { m.Battery = other.Battery; m.Charging = other.Charging; m.Full = other.Full; m.External = other.External; m.Updated = other.Updated; m.Source = other.Source; m.Estimated = other.Estimated; m.HasBattery = true; }
                if (other.HasBattery) m.HasBattery = true;
                m.Paused = d.Paused && same.Paused;
                m.Via = "USB cable + LIGHTSPEED receiver";
                outList[outList.IndexOf(same)] = m;
            }
            return outList.OrderBy(x => x.Kind == "mouse" ? 0 : x.Kind == "headset" ? 1 : x.Kind == "keyboard" ? 2 : 3).ThenBy(x => x.Name).ToList();
        }
    }

    // the real thing: Logitech receivers (HID++ 2.0 per slot), corded Logitech HID++ devices (index 0xFF) and the A50 base station
    public sealed class LogiSource : IGearSource
    {
        public event Action<string, BatteryReading> Heard;
        public event Action<string, bool> Link;
        readonly List<string> log = new List<string>(); public List<string> Log { get { return log; } }
        readonly GearSettings s;
        List<HidInfo> infos; readonly Dictionary<string, Holder> holders = new Dictionary<string, Holder>();
        static readonly HashSet<ushort> ReceiverPids = new HashSet<ushort> { 0xC52B, 0xC52F, 0xC531, 0xC532, 0xC534, 0xC537, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548, 0xC54D };
        static readonly HashSet<ushort> Skip = new HashSet<ushort> { 0xC232 };   // G HUB's virtual keyboard

        class Slot { public HidppDevice Dev; public string Name; public int? Type; public GearDevice Last; public bool Seen; }
        class Holder
        {
            public string Id; public ushort Pid; public HidPort Long, Short; public bool Receiver, ScannedAll; public Dictionary<int, Slot> Slots = new Dictionary<int, Slot>();
            public A50Reader A50; public HidPort A50Port; public GearDevice A50Last;
            public void Close() { if (Long != null) Long.Dispose(); if (Short != null) Short.Dispose(); if (A50Port != null) A50Port.Dispose(); Long = Short = A50Port = null; }
        }
        public LogiSource(GearSettings settings) { s = settings; }
        void L(string m) { lock (log) { log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + m); if (log.Count > 200) log.RemoveAt(0); } }

        static bool IsHidpp(HidInfo i) { return (i.UsagePage == 0xFF00 || i.UsagePage == 0xFF43) && (i.InLen == 7 || i.InLen == 20); }

        public List<GearDevice> Read(bool askMice, bool askHeadset, bool rescan)
        {
            var now = DateTime.Now;
            if (infos == null || rescan)
            {
                infos = HidNative.Enumerate(0x046D);
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
                    if (receiver) { if (hp.Count > 0) found.AddRange(ReadReceiver(g.Key, first.Pid, hp, askMice, now)); continue; }
                    var cd = ReadCorded(g.Key, g.ToList(), hp, askMice, now); if (cd != null) found.Add(cd);
                }
                catch (Exception e) { L("read " + first.Pid.ToString("X4") + " failed: " + e.Message); }
            }
            return GearMerge.Merge(found);
        }
        // one physical device = its product id + USB interface set (collections of one device share the pid)
        static string Group(HidInfo i) { return i.Pid.ToString("X4"); }

        Holder Get(string id, ushort pid, IEnumerable<HidInfo> hp)
        {
            Holder h; if (holders.TryGetValue(id, out h) && (h.Long != null || h.Short != null || h.A50Port != null)) return h;
            if (h == null) holders[id] = h = new Holder { Id = id, Pid = pid };
            foreach (var i in hp)
            {
                if (i.InLen == 20 && h.Long == null) h.Long = HidPort.Open(i);
                else if (i.InLen == 7 && h.Short == null) h.Short = HidPort.Open(i);
            }
            var hh = h;
            Action<byte[]> listen = r => Listen(hh, r);
            if (h.Long != null) h.Long.Report += listen; if (h.Short != null) h.Short.Report += listen;
            L(string.Format("opened {0:X4}: long {1}, short {2}", pid, h.Long != null, h.Short != null));
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

        IEnumerable<GearDevice> ReadReceiver(string id, ushort pid, List<HidInfo> hp, bool ask, DateTime now)
        {
            var h = Get(id, pid, hp); h.Receiver = true;
            if (h.Long == null && h.Short == null) yield break;
            var slots = !h.ScannedAll && ask ? Enumerable.Range(1, 6).ToList() : h.Slots.Keys.Concat(KnownSlots(pid)).Distinct().ToList();
            foreach (var n in slots)
            {
                Slot sl; if (!h.Slots.TryGetValue(n, out sl)) h.Slots[n] = sl = new Slot { Dev = new HidppDevice(h.Long, h.Short, (byte)n) };
                string known; s.KnownSlots.TryGetValue(pid.ToString("X4") + ":" + n, out known);
                if (!ask)
                {   // a game is running: what we knew, marked paused
                    if (sl.Last != null) { var c = sl.Last.Clone(); c.Paused = true; yield return c; }
                    else if (known != null) yield return new GearDevice { Key = GearMerge.KeyFor("mouse", known), Kind = "mouse", Name = known, Via = "LIGHTSPEED receiver", Paused = true, Note = "Not read yet - waiting for the game to close." };
                    continue;
                }
                int ver = sl.Dev.Ping(); int err = sl.Dev.LastError;
                if (ver >= 2)
                {
                    if (sl.Name == null) { sl.Name = sl.Dev.Name() ?? known ?? "Logitech device"; sl.Type = sl.Dev.DeviceType(); }
                    var kind = sl.Type.HasValue ? Hidpp.TypeName(sl.Type.Value) : "mouse";
                    var d = new GearDevice { Key = GearMerge.KeyFor(kind, sl.Name), Kind = kind, Name = sl.Name, Via = "LIGHTSPEED receiver" };
                    var b = sl.Dev.Battery();
                    if (b != null) d.Apply(b, now);
                    else if (sl.Dev.BatteryFeature == 0 && sl.Dev.LastError >= 0) { d.HasBattery = false; d.Note = "It doesn't report a battery."; }
                    else if (sl.Last != null && sl.Last.Battery.HasValue) { d.Battery = sl.Last.Battery; d.Updated = sl.Last.Updated; d.Source = sl.Last.Source; d.Note = "The last battery read didn't answer."; }
                    sl.Last = d; sl.Seen = true;
                    var key = pid.ToString("X4") + ":" + n; if (!s.KnownSlots.ContainsKey(key) || s.KnownSlots[key] != sl.Name) { s.KnownSlots[key] = sl.Name; s.Save(); }
                    L(string.Format("slot {0}: HID++ {1}, {2} '{3}', battery {4}", n, ver, kind, sl.Name, b == null ? "none" : b.ToString()));
                    yield return d;
                }
                else
                {
                    if (err == 8 && known == null) { h.Slots.Remove(n); continue; }   // an empty slot
                    var name = sl.Name ?? known; if (name == null) { if (err != 8) L("slot " + n + ": no answer (" + Hidpp.ErrorName(err) + ")"); h.Slots.Remove(n); continue; }
                    var kind = sl.Type.HasValue ? Hidpp.TypeName(sl.Type.Value) : "mouse";
                    var d = Offline(sl.Last, kind, name);
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

        GearDevice ReadCorded(string id, List<HidInfo> all, List<HidInfo> hp, bool ask, DateTime now)
        {
            var product = all.Select(i => i.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            bool kbd = all.Any(i => i.UsagePage == 1 && i.Usage == 6), mouse = all.Any(i => i.UsagePage == 1 && i.Usage == 2);
            Holder h = null; Slot sl = null;
            if (hp.Count > 0)
            {
                h = Get(id, all[0].Pid, hp);
                if (!h.Slots.TryGetValue(0xFF, out sl)) h.Slots[0xFF] = sl = new Slot { Dev = new HidppDevice(h.Long, h.Short, 0xFF) };
                if (ask && sl.Name == null && (h.Long != null || h.Short != null) && sl.Dev.Ping() >= 2) { sl.Name = sl.Dev.Name(); sl.Type = sl.Dev.DeviceType(); }
            }
            var kind = sl != null && sl.Type.HasValue ? Hidpp.TypeName(sl.Type.Value) : kbd ? "keyboard" : mouse ? "mouse" : null;
            if (kind == null) return null;
            var name = (sl != null ? sl.Name : null) ?? product ?? ("Logitech " + kind);
            var d = new GearDevice { Key = GearMerge.KeyFor(kind, name), Kind = kind, Name = name, Wired = true, Via = "USB cable" };
            if (kind == "keyboard") { d.HasBattery = false; d.Note = "A wired keyboard - no battery."; if (sl != null) sl.Last = d; return d; }
            if (sl != null && ask && (h.Long != null || h.Short != null))
            {
                var b = sl.Dev.Battery();
                if (b != null) d.Apply(b, now); else if (sl.Dev.BatteryFeature == 0 && sl.Dev.LastError >= 0) d.HasBattery = false;
                L(string.Format("corded {0:X4}: {1} '{2}', battery {3}", all[0].Pid, kind, name, b == null ? "none" : b.ToString()));
            }
            else if (sl != null && sl.Last != null) { d = sl.Last.Clone(); d.Paused = !ask; }
            d.Wired = true;
            if (sl != null) sl.Last = d;
            return d;
        }

        GearDevice ReadA50(string id, List<HidInfo> all, bool ask, DateTime now)
        {
            var ctl = all.FirstOrDefault(A50Proto.IsControl);
            Holder h; if (!holders.TryGetValue(id, out h)) holders[id] = h = new Holder { Id = id, Pid = A50Proto.PidGen5 };
            if (h.A50Port == null && ctl != null)
            {
                h.A50Port = HidPort.Open(ctl);
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
        public static GearDevice Offline(GearDevice last, string kind, string name)
        {
            var d = last != null ? last.Clone() : new GearDevice { Key = GearMerge.KeyFor(kind, name), Kind = kind, Name = name, Via = "LIGHTSPEED receiver" };
            d.Asleep = true; d.Connected = true; d.Charging = false; d.Full = false; d.Paused = false;
            d.Note = "Asleep or switched off - move it to wake it.";
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

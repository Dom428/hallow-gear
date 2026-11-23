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
    // how every percentage reads in Gear: "45%" (no space); no value = the dash or text given
    public static class Fmt
    {
        public static string Pct(int? v, string none) { return v.HasValue ? v.Value + "%" : none; }
        public static string Pct(int v) { return v + "%"; }
    }

    public class GearDevice
    {
        public string Key, Kind, Name, Via, Source, Note;
        public int? Battery; public bool Charging, Full, External, Wired, Connected = true, Asleep, HasBattery = true, Estimated, Paused;
        public DateTime? Updated;
        public bool Bluetooth; public DateTime? LastSeen;   // 1.1: a card from Windows' Bluetooth devices (not HID++); when it was last connected
        // 1.3.1: LastKnown = the battery / state shown are remembered from earlier (devices.json), not fresh; Waiting = paired (the receiver says so)
        // but nothing heard yet while a game runs; SlotKey = "receiverPid:slot"; Provisional = named by the receiver, not by the device itself yet
        public bool LastKnown, Waiting, Provisional; public string SlotKey;
        public const string WaitingText = "waiting for its battery report (game running)";
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
            LastKnown = false; Waiting = false;
            if (Bluetooth) LastSeen = now;
        }
    }

    public interface IGearSource : IDisposable
    {
        // askMice: HID++ questions to mice / keyboards allowed now; askHeadset: the A50 battery GET allowed now; rescan: enumerate again
        // 1.3.1 gentle: a game runs and the one gentle battery read is due (1-2 short messages per paired receiver device; nothing else asked)
        List<GearDevice> Read(bool askMice, bool askHeadset, bool rescan, bool gentle = false);
        event Action<string, BatteryReading> Heard;   // a battery report heard without asking (device key, reading)
        event Action<string, bool> Link;              // a receiver said a device's link went up / down
        event Action Found;                           // 1.3.1: a receiver announced a device it hadn't been asked about (0x41) - list again
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
        public event Action Found;
        readonly List<string> log = new List<string>(); public List<string> Log { get { return log; } }
        readonly GearSettings s;
        public int ReceiverReplyMs = 600, DeviceReplyMs = 1200;   // tests make these short
        List<HidInfo> infos; readonly Dictionary<string, Holder> holders = new Dictionary<string, Holder>();
        static readonly HashSet<ushort> ReceiverPids = new HashSet<ushort> { 0xC52B, 0xC52F, 0xC531, 0xC532, 0xC534, 0xC537, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548, 0xC54D };
        static readonly HashSet<ushort> Skip = new HashSet<ushort> { 0xC232 };   // G HUB's virtual keyboard
        // where the HID collections come from and how one is opened (the tests put fake ones here; nothing else changes)
        public Func<List<HidInfo>> Enumerator = () => HidNative.Enumerate(0x046D);
        public Func<HidInfo, IReportPort> Opener = i => HidPort.Open(i);

        class Slot
        {
            public HidppDevice Dev; public string Name; public int? Type; public GearDevice Last; public bool Seen;
            public bool GentleDone;                                             // 1.3.1: the one gentle read of this session was made
            public ushort ListenFeature; public byte ListenIndex; public bool ListenSoc = true;   // 1.3.1: remembered battery feature - only to LISTEN
        }
        class Holder
        {
            public string Id; public ushort Pid; public IReportPort Long, Short; public bool Receiver, ScannedAll; public string Via; public Dictionary<int, Slot> Slots = new Dictionary<int, Slot>();
            public Dictionary<int, PairingInfo> Paired = new Dictionary<int, PairingInfo>(); public bool PairingRead;   // 1.3.1: what the receiver says is paired
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

        public List<GearDevice> Read(bool askMice, bool askHeadset, bool rescan, bool gentle = false)
        {
            var now = DateTime.Now;
            if (infos == null || rescan)
            {
                infos = Enumerator() ?? new List<HidInfo>();
                // close what isn't there any more
                foreach (var k in holders.Keys.ToList()) if (!infos.Any(i => Group(i) == k)) { holders[k].Close(); holders.Remove(k); }
                foreach (var h in holders.Values) { h.ScannedAll = false; h.PairingRead = false; }
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
                    if (receiver) { if (hp.Count > 0) found.AddRange(ReadReceiver(g.Key, first.Pid, ReceiverName(first.Pid, g.Select(i => i.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))), hp, askMice, gentle && !askMice, now).ToList()); continue; }
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
                // 1.3.1: until the device's battery feature is found this session, a battery feature remembered from an earlier session is used - to listen only
                bool remembered = sl.Dev.BatteryIndex == 0 && sl.ListenIndex != 0;
                var b = Hidpp.Passive(r, (byte)kv.Key, remembered ? sl.ListenFeature : sl.Dev.BatteryFeature, remembered ? sl.ListenIndex : sl.Dev.BatteryIndex, remembered ? sl.ListenSoc : sl.Dev.SocSupported);
                if (b != null) { var cb = Heard; if (cb != null) cb(sl.Last.Key, b); }
                var up = Hidpp.LinkUp(r, (byte)kv.Key);
                if (up.HasValue && h.Receiver) { var lk = Link; if (lk != null) lk(sl.Last.Key, up.Value); }
            }
            // 1.3.1: the receiver announces a device (0x41) it wasn't asked about: remember it as paired and list again (nothing is asked)
            var con = h.Receiver ? Hidpp.FromConnection(r) : null;
            if (con != null && con.Wpid != 0)
            {
                bool isNew;
                lock (h.Paired) { Slot have; isNew = !h.Paired.ContainsKey(con.Slot) && !(h.Slots.TryGetValue(con.Slot, out have) && have.Last != null); if (isNew) h.Paired[con.Slot] = con; }
                if (isNew) { L("receiver: " + con); var f = Found; if (f != null) f(); }
            }
        }
        // a slot's kind: what the device said (getDeviceType), else what the receiver says now (1.3.1), else what it said in an earlier session, else "mouse"
        string KindOf(Slot sl, string slotKey, PairingInfo pi = null)
        {
            if (sl.Type.HasValue) return Hidpp.TypeName(sl.Type.Value);
            if (pi != null && pi.Kind != null) return pi.Kind;
            string k; return s.KnownSlotKinds != null && s.KnownSlotKinds.TryGetValue(slotKey, out k) && !string.IsNullOrEmpty(k) ? k : "mouse";
        }
        // 1.3.1: ask the RECEIVER (not the devices) what is paired in slots 1-6 - register 0xB5 reads it answers itself. Once per session / rescan.
        void ReadPairing(Holder h)
        {
            h.PairingRead = true;
            var rx = new HidppReceiver(h.Long, h.Short) { ReplyMs = ReceiverReplyMs };
            int found = 0, errors = 0;
            for (int n = 1; n <= 6; n++)
            {
                var pi = rx.Paired(n);
                if (pi != null) { lock (h.Paired) h.Paired[n] = pi; found++; L(h.Via + " " + pi); }
                else if (rx.LastError < 0) { errors++; if (n == 1) break; }   // no answer at all: the receiver doesn't do this - don't wait 6 times
            }
            L(h.Via + ": pairing info read from the receiver - " + found + " paired" + (errors > 0 ? " (no answer to " + errors + ")" : "") + " - " + rx.Sent.Count + " register reads (0xB5) answered by the receiver itself");
        }
        // 1.3.1: remembers a slot's battery feature (to listen to it in a later session, never to ask with it)
        void RememberBattery(string slotKey, HidppDevice dev, ushort wpid)
        {
            if (dev.BatteryIndex == 0) return;
            var v = dev.BatteryFeature.ToString("X4") + " " + dev.BatteryIndex + " " + (dev.SocSupported ? 1 : 0) + " " + wpid.ToString("X4");
            string old; if (s.KnownSlotBattery.TryGetValue(slotKey, out old) && old == v) return;
            s.KnownSlotBattery[slotKey] = v; s.Save();
        }
        void LoadListen(Slot sl, string slotKey, PairingInfo pi)
        {
            string v; if (sl.ListenIndex != 0 || !s.KnownSlotBattery.TryGetValue(slotKey, out v)) return;
            var p = v.Split(' '); ushort f, w = 0; byte i;
            if (p.Length < 3 || !ushort.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out f) || !byte.TryParse(p[1], out i) || i == 0 || i >= 0x80) return;
            if (p.Length > 3) ushort.TryParse(p[3], System.Globalization.NumberStyles.HexNumber, null, out w);
            if (pi != null && w != 0 && pi.Wpid != w) return;   // another device is paired there now
            sl.ListenFeature = f; sl.ListenIndex = i; sl.ListenSoc = p[2] == "1";
        }

        IEnumerable<GearDevice> ReadReceiver(string id, ushort pid, string via, List<HidInfo> hp, bool ask, bool gentle, DateTime now)
        {
            var h = Get(id, pid, hp); h.Receiver = true; h.Via = via;
            if (h.Long == null && h.Short == null) yield break;
            // 1.3.1: during a game, learn what is paired from the receiver itself (once), so a device never read before still gets its card
            if (!ask && !h.PairingRead) ReadPairing(h);
            List<int> paired; lock (h.Paired) paired = h.Paired.Keys.ToList();
            var slots = !h.ScannedAll && ask ? Enumerable.Range(1, 6).ToList() : h.Slots.Keys.Concat(KnownSlots(pid)).Concat(paired).Distinct().OrderBy(x => x).ToList();
            // 1.3.1: a receiver that doesn't tell its pairings and nothing known: the gentle read looks in slots 1-6 (the receiver itself answers
            // "empty" for an empty slot, so only a paired device gets the 1-2 messages)
            bool gentleScan = !ask && gentle && slots.Count == 0;
            if (gentleScan) slots = Enumerable.Range(1, 6).ToList();
            foreach (var n in slots)
            {
                Slot sl; if (!h.Slots.TryGetValue(n, out sl)) h.Slots[n] = sl = new Slot { Dev = new HidppDevice(h.Long, h.Short, (byte)n) { ReplyMs = DeviceReplyMs } };
                var slotKey = pid.ToString("X4") + ":" + n;
                string known; s.KnownSlots.TryGetValue(slotKey, out known);
                PairingInfo pi; lock (h.Paired) h.Paired.TryGetValue(n, out pi);
                if (!ask)
                {   // a game is running: nothing is asked - except (1.3.1) the one gentle battery read when it's due, for a device the receiver says is paired
                    LoadListen(sl, slotKey, pi);
                    var k = KindOf(sl, slotKey, pi);
                    var name = sl.Name ?? known ?? (pi != null ? pi.Name : null) ?? ("Logitech " + k);
                    if (gentle && !sl.GentleDone && (pi != null || known != null || sl.Last != null || gentleScan))
                    {
                        sl.GentleDone = true;
                        var b = sl.Dev.GentleBattery(); int err = sl.Dev.LastError;
                        if (b == null && gentleScan && (err == 8 || err < 0)) { h.Slots.Remove(n); continue; }   // empty (or silent) slot
                        L(string.Format("{0} slot {1}: gentle read in a game ({2}) -> {3}", via, n, string.Join(", ", sl.Dev.Sent.Skip(Math.Max(0, sl.Dev.Sent.Count - 2)).Select(x => x.Substring(x.LastIndexOf(' ') + 1))), b != null ? b.ToString() : Hidpp.ErrorName(err)));
                        if (b != null)
                        {
                            var d = sl.Last != null ? sl.Last.Clone() : new GearDevice { Key = GearMerge.KeyFor(k, name), Kind = k, Name = name, Via = via, Provisional = sl.Name == null && known == null };
                            d.SlotKey = slotKey; d.Note = null; d.Paused = false; d.Apply(b, now);
                            sl.Last = d; RememberBattery(slotKey, sl.Dev, pi != null ? pi.Wpid : (ushort)0);
                            yield return d; continue;
                        }
                        if (err == 8 || err == 9) { var off = Offline(sl.Last, k, name, via); off.SlotKey = slotKey; off.Provisional = sl.Name == null && known == null; sl.Last = off; yield return off; continue; }
                    }
                    if (sl.Last != null) { var c = sl.Last.Clone(); c.Paused = true; yield return c; continue; }
                    if (known == null && pi == null) continue;
                    var w = new GearDevice { Key = GearMerge.KeyFor(k, name), Kind = k, Name = name, Via = via, Paused = true, Waiting = true, SlotKey = slotKey, Provisional = known == null };
                    sl.Last = w;   // so its battery reports are heard (and the receiver's link notifications) - with the remembered battery feature, if any
                    yield return w.Clone();
                    continue;
                }
                int ver = sl.Dev.Ping(); int err2 = sl.Dev.LastError;
                if (ver >= 2)
                {
                    if (sl.Name == null) { sl.Name = sl.Dev.Name() ?? known ?? (pi != null ? pi.Name : null) ?? "Logitech device"; sl.Type = sl.Dev.DeviceType(); }
                    var kind = KindOf(sl, slotKey, pi);
                    var d = new GearDevice { Key = GearMerge.KeyFor(kind, sl.Name), Kind = kind, Name = sl.Name, Via = via, SlotKey = slotKey };
                    var b = sl.Dev.Battery();
                    if (b != null) d.Apply(b, now);
                    else if (sl.Dev.BatteryFeature == 0 && sl.Dev.LastError >= 0) { d.HasBattery = false; d.Note = "It doesn't report a battery."; }
                    else if (sl.Last != null && sl.Last.Battery.HasValue) { d.Battery = sl.Last.Battery; d.Updated = sl.Last.Updated; d.Source = sl.Last.Source; d.Note = "The last battery read didn't answer."; }
                    sl.Last = d; sl.Seen = true;
                    bool save = false;
                    if (!s.KnownSlots.ContainsKey(slotKey) || s.KnownSlots[slotKey] != sl.Name) { s.KnownSlots[slotKey] = sl.Name; save = true; }
                    if (sl.Type.HasValue && (!s.KnownSlotKinds.ContainsKey(slotKey) || s.KnownSlotKinds[slotKey] != kind)) { s.KnownSlotKinds[slotKey] = kind; save = true; }
                    if (save) s.Save();
                    RememberBattery(slotKey, sl.Dev, pi != null ? pi.Wpid : (ushort)0);
                    L(string.Format("{0} slot {1}: HID++ {2}, {3} '{4}', battery {5}", via, n, ver, kind, sl.Name, b == null ? "none" : b.ToString()));
                    yield return d;
                }
                else
                {
                    if (err2 == 8 && known == null && pi == null) { h.Slots.Remove(n); continue; }   // an empty slot
                    var name = sl.Name ?? known ?? (pi != null ? pi.Name : null);
                    if (name == null) { if (err2 != 8) L("slot " + n + ": no answer (" + Hidpp.ErrorName(err2) + ")"); h.Slots.Remove(n); continue; }
                    var d = Offline(sl.Last, KindOf(sl, slotKey, pi), name, via); d.SlotKey = slotKey; d.Provisional = sl.Name == null && known == null;
                    sl.Last = d; L("slot " + n + ": '" + name + "' " + Hidpp.ErrorName(err2));
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
        public event Action Found;
        public List<string> Log { get { return new List<string>(); } }
        public List<GearDevice> Devices = new List<GearDevice>();
        public int Reads, MouseAsks, HeadsetAsks, GentleAsks;
        public FakeSource() { Reset(); }
        public void Reset()
        {
            var t = DateTime.Now.AddMinutes(-1);
            Devices = new List<GearDevice> {
                new GearDevice { Key = "mouse:pro x superlight 2", Kind = "mouse", Name = "PRO X SUPERLIGHT 2", Via = "LIGHTSPEED receiver", Battery = 64, Updated = t, Source = "HID++ Unified Battery (0x1004)" },
                new GearDevice { Key = "headset:astro a50", Kind = "headset", Name = "ASTRO A50", Via = "base station (USB)", Battery = 100, Full = true, External = true, Updated = t, Source = "ASTRO A50 base station (battery GET 0x06)" },
                new GearDevice { Key = "keyboard:g713", Kind = "keyboard", Name = "G713", Via = "USB cable", Wired = true, HasBattery = false, Note = "A wired keyboard - no battery." } };
        }
        public List<GearDevice> Read(bool askMice, bool askHeadset, bool rescan, bool gentle = false)
        {
            Reads++; if (askMice) MouseAsks++; if (askHeadset) HeadsetAsks++; if (gentle && !askMice) GentleAsks++;
            return Devices.Select(d => { var c = d.Clone(); if (!askMice && !(gentle && c.Kind == "mouse") && c.Kind != "headset" && c.HasBattery) c.Paused = true; return c; }).ToList();
        }
        public void Hear(string key, BatteryReading b) { var h = Heard; if (h != null) h(key, b); }
        public void SayFound() { var f = Found; if (f != null) f(); }
        public void SayLink(string key, bool up) { var h = Link; if (h != null) h(key, up); }
        public void Dispose() { }
    }
}

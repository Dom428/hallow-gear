// Hallow Gear 1.1 - the battery of Bluetooth devices, from what Windows already exposes. READ-ONLY, and nothing is ever paired,
// connected or changed:
//   * classic Bluetooth (earbuds, headsets): Windows keeps the battery it hears (hands-free AT commands) on the device node as the
//     property DEVPKEY_Bluetooth_Battery {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2 - the value Settings > Bluetooth shows. Read with
//     SetupAPI (SetupDiGetClassDevs / SetupDiEnumDeviceInfo / SetupDiGetDeviceProperty). Windows sets the same property for many
//     Bluetooth LE devices too.
//   * Bluetooth LE devices with the standard Battery Service (0x180F, Battery Level 0x2A19), when Windows has no battery property for
//     them: WinRT Windows.Devices.Bluetooth (BluetoothLEDevice / GattDeviceService / GattCharacteristic). Only devices that are already
//     PAIRED. A live (uncached) read only while the device is CONNECTED; otherwise only Windows' cached value (no radio traffic, so no
//     connection is started and no pairing prompt can appear). Battery Level notifications are LISTENED to (ValueChanged) - Hallow
//     Gear never writes the subscription descriptor (CCCD) itself, because that is a write to the device: it hears the notifications
//     the device already sends (Windows subscribes to the battery of most LE devices).
// The tests scan the compiled code: the only Windows.* (WinRT) calls are on a fixed read list, and no SetupDi / CM_ "set" function is
// imported. Charging isn't reported by either source, so Hallow Gear never shows it for Bluetooth devices.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace HallowGear
{
    // what was found about one paired Bluetooth device (by its address)
    public class BtEntry
    {
        public string Address, Name, Kind, Source; public bool Le, Classic, Connected; public uint? Cod; public ushort? Appearance;
        public int? Battery; public DateTime? LastSeen; public bool CanNotify;
        public override string ToString() { return (Name ?? "?") + " [" + (Le ? "LE" : "") + (Classic ? "classic" : "") + "] " + (Kind ?? "?") + " " + (Connected ? "connected" : "not connected") + " battery " + (Battery.HasValue ? Battery + "%" : "-") + (Source != null ? " (" + Source + ")" : ""); }
    }

    public static class BtProto
    {
        public static readonly Guid BatteryService = new Guid("0000180f-0000-1000-8000-00805f9b34fb"), BatteryLevel = new Guid("00002a19-0000-1000-8000-00805f9b34fb");
        public const string WindowsSource = "Windows (Bluetooth battery)", GattSource = "Bluetooth Battery Service (0x180F)";
        public const uint DEVPROP_TYPE_BYTE = 0x03, DEVPROP_TYPE_UINT32 = 0x07, DEVPROP_TYPE_BOOLEAN = 0x11, DEVPROP_TYPE_FILETIME = 0x10, DEVPROP_TYPE_STRING = 0x12;

        // Battery Level (0x2A19): one byte, 0..100 percent
        public static int? Level(byte[] v) { return v == null || v.Length < 1 || v[0] > 100 ? (int?)null : v[0]; }
        // DEVPKEY_Bluetooth_Battery: a DEVPROP_TYPE_BYTE 0..100 (a UINT32 is accepted too)
        public static int? PnpBattery(uint type, byte[] buf, int len)
        {
            if (buf == null) return null;
            if (type == DEVPROP_TYPE_BYTE && len >= 1 && buf.Length >= 1) return buf[0] <= 100 ? (int?)buf[0] : null;
            if (type == DEVPROP_TYPE_UINT32 && len >= 4 && buf.Length >= 4) { var v = BitConverter.ToUInt32(buf, 0); return v <= 100 ? (int?)(int)v : null; }
            return null;
        }
        // the 12-hex-digit address in a PnP instance id ("BTHENUM\DEV_AABBCCDDEEFF\...", "BTHENUM\{uuid}_...\7&..&0&AABBCCDDEEFF_C00000000",
        // "BTHLE\DEV_AABBCCDDEEFF\...") or a WinRT id ("Bluetooth#Bluetooth00:11:22:33:44:55-aa:bb:cc:dd:ee:ff", "BluetoothLE#BluetoothLE...")
        public static string Address(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var m = Regex.Match(id, @"-((?:[0-9a-f]{2}:){5}[0-9a-f]{2})$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Replace(":", "").ToLowerInvariant();
            m = Regex.Match(id, @"DEV_([0-9a-f]{12})(?![0-9a-f])", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            m = Regex.Match(id, @"&([0-9a-f]{12})_C[0-9a-f]+$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }
        public static string Address(ulong a) { return a == 0 ? null : a.ToString("x12"); }
        public static string Key(string address) { return "bt:" + address; }

        // what the name says (earbuds can't be told from headphones by their class)
        public static string NameHint(string name)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (Regex.IsMatch(n, @"\bbuds|earbud|airpods|\bpods\b|\bwf-|in-ear|\bbeats (fit|studio buds)|earfun")) return "earbuds";
            if (Regex.IsMatch(n, @"controller|gamepad|dualsense|dualshock|joy-con|\bxbox\b|8bitdo")) return "controller";
            if (Regex.IsMatch(n, @"headset|headphone|\bwh-|\bqc\d|quietcomfort|\bmax\b")) return "headset";
            if (Regex.IsMatch(n, @"keyboard|\bkeys\b")) return "keyboard";
            if (Regex.IsMatch(n, @"\bmouse\b|\bmx (master|anywhere)")) return "mouse";
            return null;
        }
        static string Refine(string kind, string name)
        {
            var hint = NameHint(name);
            if (kind == "headset" && hint == "earbuds") return "earbuds";
            if (kind == "other" && hint != null) return hint;
            return kind;
        }
        // classic Class of Device: bits 8-12 major class, bits 2-7 minor class
        public static string KindFromCod(uint cod, string name)
        {
            uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F; string k = "other";
            if (major == 0x04) { if (minor == 1 || minor == 2 || minor == 6) k = "headset"; }               // wearable headset, hands-free, headphones
            else if (major == 0x05)
            {
                uint kb = (minor >> 4) & 3, sub = minor & 0x0F;
                if (sub == 1 || sub == 2) k = "controller";                                                      // joystick, gamepad
                else if (kb == 1 || kb == 3) k = "keyboard"; else if (kb == 2) k = "mouse";                      // keyboard / combo, pointing
            }
            return Refine(k, name);
        }
        // LE GAP Appearance: category = value >> 6 (0x0F = HID, 0x25 = hearing aid, 0x29... audio sink / hearables)
        public static string KindFromAppearance(ushort a, string name)
        {
            string k = "other";
            switch (a)
            {
                case 0x03C1: k = "keyboard"; break;
                case 0x03C2: k = "mouse"; break;
                case 0x03C3: case 0x03C4: k = "controller"; break;   // joystick, gamepad
                case 0x0941: k = "earbuds"; break;                   // earbud
                case 0x0942: case 0x0943: case 0x0944: k = "headset"; break;   // headset, headphones, neck band
            }
            if (k == "other" && (a >> 6) == 0x29) k = "headset";
            return Refine(k, name);
        }
        public static string Word(string kind)
        {
            switch (kind) { case "earbuds": return "Earbuds"; case "controller": return "Controller"; case "headset": return "Headset"; case "keyboard": return "Keyboard"; case "mouse": return "Mouse"; }
            return "Device";
        }

        // the card for one entry. remembered = "kind|name|percent|lastSeen" from an earlier session (or null).
        // null = no card (a paired device that has never told Windows its battery: phones, speakers, PCs...)
        public static GearDevice ToDevice(BtEntry e, string remembered, DateTime now)
        {
            string rKind = null, rName = null; int? rPct = null; DateTime? rSeen = null;
            if (remembered != null)
            {
                var p = remembered.Split('|');
                if (p.Length > 0 && p[0].Length > 0) rKind = p[0]; if (p.Length > 1 && p[1].Length > 0) rName = p[1];
                int n; if (p.Length > 2 && int.TryParse(p[2], out n) && n >= 0 && n <= 100) rPct = n;
                DateTime t; if (p.Length > 3 && DateTime.TryParse(p[3], out t)) rSeen = t;
            }
            if (!e.Battery.HasValue && remembered == null) return null;
            var name = !string.IsNullOrWhiteSpace(e.Name) ? e.Name.Trim() : rName ?? "Bluetooth device";
            var kind = e.Kind ?? rKind ?? Refine("other", name);
            var d = new GearDevice { Key = Key(e.Address), Kind = kind, Name = name, Via = "Bluetooth", Bluetooth = true, Wired = false, Connected = e.Connected, HasBattery = true };
            d.Battery = e.Battery ?? rPct; d.Source = e.Battery.HasValue ? e.Source : null;
            d.LastSeen = e.Connected ? now : e.LastSeen.HasValue && (!rSeen.HasValue || e.LastSeen > rSeen) ? e.LastSeen : rSeen;
            if (e.Connected && e.Battery.HasValue) d.Updated = now; else d.Updated = d.LastSeen;
            if (!e.Connected) d.Note = d.Battery.HasValue ? "Not connected right now - the battery is the last one Windows heard." : "Not connected right now.";
            else if (!e.Battery.HasValue) d.Note = "Connected, but it hasn't told Windows its battery yet.";
            return d;
        }
        public static string Remember(GearDevice d)
        {
            return d.Kind + "|" + (d.Name ?? "").Replace("|", "/") + "|" + (d.Battery.HasValue ? d.Battery.ToString() : "") + "|" + (d.LastSeen.HasValue ? d.LastSeen.Value.ToString("s") : "");
        }
    }

    public interface IBtSource : IDisposable
    {
        List<BtEntry> Read();                          // one pass: paired devices and what Windows knows of their battery
        void Stop();                                   // stop listening (the Bluetooth switch went off)
        event Action<string, BatteryReading> Heard;    // a Battery Level notification (device key, reading)
        List<string> Log { get; }
    }

    // SetupAPI: device properties, read-only (no SetupDiSet* / CM_Set* is even imported)
    public static class BtPnp
    {
        const int DIGCF_ALLCLASSES = 0x4;
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA d, StringBuilder id, int size, out int need);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, byte[] buf, int size, out int need, int flags);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList")] static extern bool BtDestroyList(IntPtr set);

        static readonly DEVPROPKEY Battery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static readonly DEVPROPKEY FriendlyName = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static readonly DEVPROPKEY ClassOfDevice = new DEVPROPKEY("2bd67d8b-8beb-48d5-87e0-6cda3428040a", 10);
        static readonly DEVPROPKEY LastConnected = new DEVPROPKEY("2bd67d8b-8beb-48d5-87e0-6cda3428040a", 11);
        static readonly DEVPROPKEY Connected = new DEVPROPKEY("83da6326-97a6-4088-9453-a1923f573b29", 15);

        static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY k, out uint type, out int len)
        {
            var buf = new byte[512]; type = 0; len = 0;
            if (!SetupDiGetDevicePropertyW(set, ref d, ref k, out type, buf, buf.Length, out len, 0)) { len = 0; return null; }
            return buf;
        }
        // every device node Windows has for paired Bluetooth devices (present or not): their address, name, class, battery property
        public static void Scan(Dictionary<string, BtEntry> map, Action<string> log)
        {
            foreach (var en in new[] { "BTHENUM", "BTHLE" })
            {
                var set = SetupDiGetClassDevsW(IntPtr.Zero, en, IntPtr.Zero, DIGCF_ALLCLASSES);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) continue;
                int nodes = 0;
                try
                {
                    for (int i = 0; i < 1024; i++)
                    {
                        var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                        if (!SetupDiEnumDeviceInfo(set, i, ref d)) break;
                        var sb = new StringBuilder(512); int need;
                        if (!SetupDiGetDeviceInstanceIdW(set, ref d, sb, sb.Capacity, out need)) continue;
                        var id = sb.ToString(); var addr = BtProto.Address(id); if (addr == null) continue;
                        nodes++;
                        BtEntry e; if (!map.TryGetValue(addr, out e)) map[addr] = e = new BtEntry { Address = addr };
                        if (en == "BTHLE") e.Le = true; else e.Classic = true;
                        bool root = id.IndexOf("\\DEV_", StringComparison.OrdinalIgnoreCase) >= 0;
                        uint t; int len; byte[] b;
                        b = Prop(set, ref d, Battery, out t, out len);
                        var pct = BtProto.PnpBattery(t, b, len); if (pct.HasValue) { e.Battery = pct; e.Source = BtProto.WindowsSource; }
                        if (root || e.Name == null)
                        {
                            b = Prop(set, ref d, FriendlyName, out t, out len);
                            if (b != null && t == BtProto.DEVPROP_TYPE_STRING && len >= 2) { var s = Encoding.Unicode.GetString(b, 0, len).TrimEnd('\0').Trim(); if (s.Length > 0 && (root || e.Name == null)) e.Name = s; }
                        }
                        b = Prop(set, ref d, ClassOfDevice, out t, out len); if (b != null && t == BtProto.DEVPROP_TYPE_UINT32 && len >= 4) e.Cod = BitConverter.ToUInt32(b, 0);
                        b = Prop(set, ref d, Connected, out t, out len); if (b != null && t == BtProto.DEVPROP_TYPE_BOOLEAN && len >= 1 && b[0] != 0) e.Connected = true;
                        b = Prop(set, ref d, LastConnected, out t, out len);
                        if (b != null && t == BtProto.DEVPROP_TYPE_FILETIME && len >= 8) { try { var ft = DateTime.FromFileTime(BitConverter.ToInt64(b, 0)); if (!e.LastSeen.HasValue || ft > e.LastSeen) e.LastSeen = ft; } catch { } }
                    }
                }
                finally { BtDestroyList(set); }
                if (log != null) log(en + ": " + nodes + " device nodes");
            }
        }
    }

    // the real thing (see the header)
    public sealed class BtSource : IBtSource
    {
        public event Action<string, BatteryReading> Heard;
        readonly List<string> log = new List<string>(); public List<string> Log { get { return log; } }
        class Sub { public BluetoothLEDevice Dev; public GattCharacteristic Ch; public TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> H; }
        readonly Dictionary<string, Sub> subs = new Dictionary<string, Sub>();
        public bool Listen = true;   // --probe: false (nothing is held after the pass)
        void L(string m) { lock (log) { log.Add(DateTime.Now.ToString("HH:mm:ss") + " bluetooth: " + m); if (log.Count > 200) log.RemoveAt(0); } }

        static T Wait<T>(IAsyncOperation<T> op, int ms = 5000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (op.Status == AsyncStatus.Started)
            {
                if (DateTime.UtcNow > end) { try { op.Cancel(); } catch { } throw new TimeoutException(); }
                Thread.Sleep(15);
            }
            if (op.Status != AsyncStatus.Completed) throw new InvalidOperationException("WinRT: " + op.Status);
            return op.GetResults();
        }
        static byte[] Bytes(IBuffer b)
        {
            if (b == null || b.Length == 0) return new byte[0];
            var r = DataReader.FromBuffer(b); var o = new byte[b.Length]; r.ReadBytes(o); return o;
        }

        public List<BtEntry> Read()
        {
            var map = new Dictionary<string, BtEntry>();
            try { BtPnp.Scan(map, L); } catch (Exception e) { L("device properties failed: " + e.Message); }
            try { Classic(map); } catch (Exception e) { L("classic list failed: " + e.Message); }
            try { LowEnergy(map); } catch (Exception e) { L("LE list failed: " + e.Message); }
            foreach (var e in map.Values)
                if (e.Kind == null) e.Kind = e.Appearance.HasValue && e.Appearance.Value != 0 ? BtProto.KindFromAppearance(e.Appearance.Value, e.Name) : e.Cod.HasValue ? BtProto.KindFromCod(e.Cod.Value, e.Name) : BtProto.KindFromAppearance(0, e.Name);
            foreach (var k in subs.Keys.ToList()) if (!map.ContainsKey(k) || !map[k].Connected) Drop(k);
            foreach (var e in map.Values) L(e.ToString());
            return map.Values.ToList();
        }
        // paired classic devices: name, class of device, connected (FromIdAsync doesn't connect)
        void Classic(Dictionary<string, BtEntry> map)
        {
            var list = Wait(DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)));
            foreach (var di in list)
            {
                BluetoothDevice dev = null;
                try
                {
                    dev = Wait(BluetoothDevice.FromIdAsync(di.Id)); if (dev == null) continue;
                    var addr = BtProto.Address(dev.BluetoothAddress) ?? BtProto.Address(di.Id); if (addr == null) continue;
                    BtEntry e; if (!map.TryGetValue(addr, out e)) map[addr] = e = new BtEntry { Address = addr };
                    e.Classic = true; if (!string.IsNullOrWhiteSpace(dev.Name)) e.Name = dev.Name;
                    e.Cod = dev.ClassOfDevice.RawValue; e.Connected = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch (Exception x) { L("classic " + di.Name + ": " + x.Message); }
                finally { if (dev != null) dev.Dispose(); }
            }
        }
        // paired LE devices: name, appearance, connected; the Battery Service only when Windows has no battery property for it
        void LowEnergy(Dictionary<string, BtEntry> map)
        {
            var list = Wait(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)));
            foreach (var di in list)
            {
                BluetoothLEDevice dev = null; bool keep = false;
                try
                {
                    dev = Wait(BluetoothLEDevice.FromIdAsync(di.Id)); if (dev == null) continue;
                    var addr = BtProto.Address(dev.BluetoothAddress) ?? BtProto.Address(di.Id); if (addr == null) continue;
                    BtEntry e; if (!map.TryGetValue(addr, out e)) map[addr] = e = new BtEntry { Address = addr };
                    e.Le = true; if (!string.IsNullOrWhiteSpace(dev.Name)) e.Name = dev.Name;
                    e.Appearance = dev.Appearance.RawValue; e.Connected = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    if (e.Battery.HasValue && e.Source == BtProto.WindowsSource) continue;   // Windows already knows it: no GATT at all
                    if (subs.ContainsKey(addr) && e.Connected) { ReadLevel(e, subs[addr].Ch, true); continue; }   // already listening: read through the held one
                    keep = Gatt(dev, e, addr);
                }
                catch (Exception x) { L("LE " + di.Name + ": " + x.Message); }
                finally { if (dev != null && !keep) dev.Dispose(); }
            }
        }
        // the Battery Service of a paired LE device: Windows' cache first; uncached only while it is connected. true = the device is
        // kept (to keep hearing its notifications)
        bool Gatt(BluetoothLEDevice dev, BtEntry e, string addr)
        {
            bool live = e.Connected;
            var sr = Wait(dev.GetGattServicesForUuidAsync(BtProto.BatteryService, BluetoothCacheMode.Cached));
            if (live && (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0)) sr = Wait(dev.GetGattServicesForUuidAsync(BtProto.BatteryService, BluetoothCacheMode.Uncached));
            if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0) return false;
            foreach (var svc in sr.Services)
            {
                var cr = Wait(svc.GetCharacteristicsForUuidAsync(BtProto.BatteryLevel, BluetoothCacheMode.Cached));
                if (live && (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0)) cr = Wait(svc.GetCharacteristicsForUuidAsync(BtProto.BatteryLevel, BluetoothCacheMode.Uncached));
                if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0) continue;
                var ch = cr.Characteristics[0];
                ReadLevel(e, ch, live);
                e.CanNotify = (ch.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0;
                if (live && e.CanNotify && Listen && !subs.ContainsKey(addr))
                {
                    var key = BtProto.Key(addr);
                    TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> h = (s, a) =>
                    {
                        try { var p = BtProto.Level(Bytes(a.CharacteristicValue)); var cb = Heard; if (p.HasValue && cb != null) cb(key, new BatteryReading { Percent = p, Source = BtProto.GattSource }); } catch { }
                    };
                    ch.ValueChanged += h;   // listening only - no descriptor (CCCD) is written
                    subs[addr] = new Sub { Dev = dev, Ch = ch, H = h };
                    return true;
                }
                return false;
            }
            return false;
        }
        void ReadLevel(BtEntry e, GattCharacteristic ch, bool live)
        {
            var rr = Wait(ch.ReadValueAsync(live ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached));
            if (rr.Status != GattCommunicationStatus.Success) { L((e.Name ?? e.Address) + ": battery read " + rr.Status); return; }
            var p = BtProto.Level(Bytes(rr.Value)); if (p.HasValue) { e.Battery = p; e.Source = BtProto.GattSource + (live ? "" : ", cached by Windows"); }
        }
        void Drop(string addr)
        {
            Sub s; if (!subs.TryGetValue(addr, out s)) return; subs.Remove(addr);
            try { s.Ch.ValueChanged -= s.H; } catch { }
            try { s.Dev.Dispose(); } catch { }
        }
        public void Stop() { foreach (var k in subs.Keys.ToList()) Drop(k); }
        public void Dispose() { Stop(); }
    }

    // made-up Bluetooth devices for --snap and the tests (nothing is opened)
    public sealed class FakeBtSource : IBtSource
    {
        public event Action<string, BatteryReading> Heard;
        public List<string> Log { get { return new List<string>(); } }
        public List<BtEntry> Entries = new List<BtEntry>(); public int Reads;
        public FakeBtSource() { Reset(); }
        public void Reset()
        {
            Entries = new List<BtEntry> {
                new BtEntry { Address = "0a1b2c3d4e01", Name = "Galaxy Buds2 Pro", Classic = true, Cod = 0x240404, Connected = true, Battery = 80, Source = BtProto.WindowsSource },
                new BtEntry { Address = "0a1b2c3d4e02", Name = "Xbox Wireless Controller", Le = true, Appearance = 0x03C4, Connected = true, Battery = 45, Source = BtProto.GattSource, CanNotify = true },
                new BtEntry { Address = "0a1b2c3d4e03", Name = "WH-1000XM5", Classic = true, Cod = 0x240418, Connected = false, Battery = 60, Source = BtProto.WindowsSource, LastSeen = DateTime.Now.AddHours(-2) },
                new BtEntry { Address = "0a1b2c3d4e04", Name = "Living Room TV", Classic = true, Cod = 0x24043C, Connected = true } };   // no battery: no card
            foreach (var e in Entries) e.Kind = e.Appearance.HasValue ? BtProto.KindFromAppearance(e.Appearance.Value, e.Name) : BtProto.KindFromCod(e.Cod.Value, e.Name);
        }
        public List<BtEntry> Read() { Reads++; return Entries.Select(e => (BtEntry)e.MemberwiseCloneEntry()).ToList(); }
        public void Hear(string key, int pct) { var h = Heard; if (h != null) h(key, new BatteryReading { Percent = pct, Source = BtProto.GattSource }); }
        public void Stop() { }
        public void Dispose() { }
    }
    static class BtEntryExt { public static BtEntry MemberwiseCloneEntry(this BtEntry e) { return new BtEntry { Address = e.Address, Name = e.Name, Kind = e.Kind, Source = e.Source, Le = e.Le, Classic = e.Classic, Connected = e.Connected, Cod = e.Cod, Appearance = e.Appearance, Battery = e.Battery, LastSeen = e.LastSeen, CanNotify = e.CanNotify }; } }
}

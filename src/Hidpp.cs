// Hallow Gear - Logitech HID++ 2.0, READ-ONLY. From the public documentation: Logitech's HID++ 2.0 feature specs (as published with
// Solaar), Solaar's docs and Linux's hid-logitech-hidpp driver.
//   short report 0x10 (7 bytes) / long report 0x11 (20 bytes): [id, device index, feature index, function << 4 | software id, params]
//   device index: 1..6 = a receiver slot, 0xFF = a device on its own USB cable
//   errors: HID++ 2.0 [id, dev, 0xFF, feature index, function|sw, code] - HID++ 1.0 [0x10, dev, 0x8F, sub id, address, code]
// The allow-list below is the complete list of functions Hallow Gear can send. Everything else - DPI, lighting, onboard profiles,
// pairing, receiver registers, host switching, any "set" - is refused by SafeReport.Hidpp before a single byte is built.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HallowGear
{
    public class BatteryReading
    {
        public int? Percent; public bool Charging, Full, External, Estimated; public string Level; public string Source; public int? Millivolts;
        public override string ToString() { return (Percent.HasValue ? Percent + "%" : "?%") + (Estimated ? " (est.)" : "") + (Charging ? " charging" : "") + (Full ? " full" : "") + (External ? " ext-power" : "") + " [" + Source + "]"; }
    }

    public static class Hidpp
    {
        public const byte ShortId = 0x10, LongId = 0x11, SwId = 0x0D;
        public const ushort IRoot = 0x0000, DeviceName = 0x0005, BatteryStatus = 0x1000, BatteryVoltage = 0x1001, UnifiedBattery = 0x1004;

        // ---- THE ALLOW-LIST: feature -> function -> name. Read-type calls only ("get..."), nothing that changes anything.
        public static readonly Dictionary<ushort, Dictionary<byte, string>> Allowed = new Dictionary<ushort, Dictionary<byte, string>> {
            { IRoot, new Dictionary<byte, string> { { 0, "getFeature" }, { 1, "getProtocolVersion (ping)" } } },
            { DeviceName, new Dictionary<byte, string> { { 0, "getDeviceNameCount" }, { 1, "getDeviceName" }, { 2, "getDeviceType" } } },
            { BatteryStatus, new Dictionary<byte, string> { { 0, "getBatteryLevelStatus" }, { 1, "getBatteryCapability" } } },
            { BatteryVoltage, new Dictionary<byte, string> { { 0, "getBatteryInfo" } } },
            { UnifiedBattery, new Dictionary<byte, string> { { 0, "getCapabilities" }, { 1, "getStatus" } } },
        };
        // the function's name when (feature, function, index, arguments) is an allowed read; null = refused
        public static string AllowedName(ushort featureId, byte function, byte featureIndex, byte[] args)
        {
            Dictionary<byte, string> fns; string name;
            if (!Allowed.TryGetValue(featureId, out fns) || !fns.TryGetValue(function, out name)) return null;
            // IRoot is always index 0; every other feature sits at 1..0x7F (0x80 and up are HID++ 1.0 sub ids - register writes among them)
            if (featureId == IRoot ? featureIndex != 0 : (featureIndex == 0 || featureIndex >= 0x80)) return null;
            args = args ?? new byte[0];
            if (featureId == IRoot && function == 0)
            {   // getFeature may only look up the features on this list
                if (args.Length < 2 || !Allowed.ContainsKey((ushort)(args[0] << 8 | args[1])) || args.Skip(2).Any(b => b != 0)) return null;
                return name;
            }
            if (featureId == IRoot && function == 1) return args.Length <= 3 && args.Take(2).All(b => b == 0) ? name : null;   // (0, 0, ping byte)
            if (featureId == DeviceName && function == 1) return args.Length <= 1 ? name : null;                             // (char index)
            return args.All(b => b == 0) ? name : null;                                                                          // no arguments
        }

        // ---- parsing replies
        public static bool IsReply(byte[] r, byte dev, byte index, byte function)
        {
            return r != null && r.Length >= 4 && (r[0] == ShortId || r[0] == LongId) && r[1] == dev && r[2] == index && r[3] == (byte)(function << 4 | SwId);
        }
        // an error for that request: the code, or 0 when it isn't an error for it
        public static int ErrorFor(byte[] r, byte dev, byte index, byte function)
        {
            if (r == null || r.Length < 6 || r[1] != dev) return 0;
            byte fs = (byte)(function << 4 | SwId);
            if ((r[0] == LongId || r[0] == ShortId) && r[2] == 0xFF && r[3] == index && r[4] == fs) return r[5] == 0 ? 0xFF : r[5];   // HID++ 2.0
            if (r[0] == ShortId && r[2] == 0x8F && r[3] == index && r[4] == fs) return r[5] == 0 ? 0xFF : r[5];                    // HID++ 1.0 (receiver)
            return 0;
        }
        public static byte[] Params(byte[] r) { return r == null || r.Length <= 4 ? new byte[0] : r.Skip(4).ToArray(); }
        public static string ErrorName(int code)
        {
            switch (code)
            {
                case 0x01: return "invalid sub id / unknown"; case 0x02: return "invalid address / argument"; case 0x03: return "invalid value / out of range";
                case 0x05: return "not allowed / too many"; case 0x07: return "busy"; case 0x08: return "unknown device (empty slot)";
                case 0x09: return "resource error (device asleep or off)"; case 0x0A: return "request unavailable";
            }
            return "error 0x" + code.ToString("X2");
        }

        // 0x1004 getStatus (and its event): [state of charge %, level flags, charging status, external power]
        public static BatteryReading Unified(byte[] p, bool socSupported)
        {
            if (p == null || p.Length < 4) return null;
            var b = new BatteryReading { Source = "HID++ Unified Battery (0x1004)" };
            int lvl = p[1];
            b.Level = (lvl & 8) != 0 ? "full" : (lvl & 4) != 0 ? "good" : (lvl & 2) != 0 ? "low" : (lvl & 1) != 0 ? "critical" : null;
            if (socSupported && p[0] <= 100) b.Percent = p[0];
            else if (b.Level != null) { b.Percent = b.Level == "full" ? 100 : b.Level == "good" ? 60 : b.Level == "low" ? 20 : 5; b.Estimated = true; }
            switch (p[2]) { case 1: case 2: b.Charging = true; break; case 3: b.Full = true; break; }
            b.External = p[3] != 0 || b.Charging || b.Full;
            return b;
        }
        // 0x1000 getBatteryLevelStatus (and its event): [level %, next level %, status]
        public static BatteryReading Status(byte[] p)
        {
            if (p == null || p.Length < 3) return null;
            var b = new BatteryReading { Source = "HID++ Battery Status (0x1000)" };
            if (p[0] > 0 && p[0] <= 100) b.Percent = p[0];
            switch (p[2]) { case 1: case 2: case 4: b.Charging = true; break; case 3: b.Full = true; if (!b.Percent.HasValue) b.Percent = 100; break; }
            b.External = b.Charging || b.Full;
            return b;
        }
        // 0x1001 getBatteryInfo (and its event): [voltage mV big-endian (2), flags]: bit 7 = external power, bits 0-2 = 0 charging, 1 full
        public static BatteryReading Voltage(byte[] p)
        {
            if (p == null || p.Length < 3) return null;
            int mv = p[0] << 8 | p[1]; int f = p[2];
            var b = new BatteryReading { Source = "HID++ Battery Voltage (0x1001)", Millivolts = mv, Percent = PercentFromMillivolts(mv), Estimated = true };
            if ((f & 0x80) != 0) { b.External = true; if ((f & 7) == 0) b.Charging = true; else if ((f & 7) == 1) b.Full = true; }
            if (b.Full) b.Percent = 100;
            return b;
        }
        // a lithium cell's voltage -> percent (the table Linux's hid-logitech-hidpp uses for 0x1001)
        static readonly int[] Volts = {
            4186, 4156, 4143, 4133, 4122, 4113, 4103, 4094, 4086, 4075, 4067, 4059, 4051, 4043, 4035, 4027, 4019, 4011, 4003, 3997,
            3989, 3983, 3976, 3969, 3961, 3955, 3949, 3942, 3935, 3929, 3922, 3916, 3909, 3902, 3896, 3890, 3883, 3877, 3870, 3865,
            3859, 3853, 3848, 3842, 3837, 3833, 3828, 3824, 3819, 3815, 3811, 3808, 3804, 3800, 3797, 3793, 3790, 3787, 3784, 3781,
            3778, 3775, 3772, 3770, 3767, 3764, 3762, 3759, 3757, 3754, 3751, 3748, 3744, 3741, 3737, 3734, 3730, 3726, 3724, 3720,
            3717, 3714, 3710, 3706, 3702, 3697, 3693, 3688, 3683, 3677, 3671, 3666, 3662, 3658, 3654, 3646, 3633, 3612, 3579, 3537 };
        public static int PercentFromMillivolts(int mv)
        {
            for (int i = 0; i < Volts.Length; i++) if (mv >= Volts[i]) return Volts.Length - i;
            return 0;
        }
        public static string TypeName(int t)
        {
            switch (t) { case 0: return "keyboard"; case 1: return "remote"; case 2: return "numpad"; case 3: return "mouse"; case 4: return "touchpad"; case 5: return "trackball"; case 6: return "presenter"; case 7: return "receiver"; case 8: return "headset"; }
            return "device";
        }
        // HID++ 1.0 receiver notification 0x41 (device connection): [0x10, dev, 0x41, protocol, flags, wpid lo, wpid hi]; flags bit 6 = link lost
        public static bool? LinkUp(byte[] r, byte dev)
        {
            if (r == null || r.Length < 5 || r[0] != ShortId || r[1] != dev || r[2] != 0x41) return null;
            return (r[4] & 0x40) == 0;
        }
        // a battery report heard without asking (the device's own event, or the answer to someone else's question - e.g. G HUB's)
        public static BatteryReading Passive(byte[] r, byte dev, ushort batteryFeature, byte batteryIndex, bool socSupported)
        {
            if (r == null || r.Length < 7 || (r[0] != LongId && r[0] != ShortId) || r[1] != dev || batteryIndex == 0 || r[2] != batteryIndex) return null;
            int fn = r[3] >> 4; var p = Params(r);
            if (batteryFeature == UnifiedBattery && (fn == 0 || fn == 1)) return Unified(p, socSupported);
            if (batteryFeature == BatteryStatus && fn == 0) return Status(p);
            if (batteryFeature == BatteryVoltage && fn == 0) return Voltage(p);
            return null;
        }
    }

    // one HID++ 2.0 device (a receiver slot or a corded device) behind its port(s); every call is one allow-listed read
    public class HidppDevice
    {
        readonly IReportPort longPort, shortPort; public readonly byte Index;
        readonly Dictionary<ushort, int> features = new Dictionary<ushort, int>();   // feature id -> index (0 = not there)
        public readonly List<string> Sent = new List<string>();                         // what was asked (for the log and tests)
        public int LastError; public int ReplyMs = 1200;
        public ushort BatteryFeature; public byte BatteryIndex; public bool SocSupported = true;
        public HidppDevice(IReportPort longPort, IReportPort shortPort, byte index) { this.longPort = longPort; this.shortPort = shortPort; Index = index; }

        // one read: the reply's parameters, or null (LastError says why: an error code, or -1 = no answer / couldn't send / refused)
        public byte[] Call(ushort featureId, byte function, params byte[] args)
        {
            LastError = 0;
            int fi = featureId == Hidpp.IRoot ? 0 : FeatureIndex(featureId);
            if (fi <= 0 && featureId != Hidpp.IRoot) { LastError = -2; return null; }
            var port = longPort ?? shortPort;
            var req = SafeReport.ForHidpp(port == longPort, Index, featureId, (byte)fi, function, Hidpp.SwId, args);
            if (req == null) { LastError = -3; return null; }
            lock (Sent) Sent.Add(req.What); SentLog.Add(req.What);
            byte idx = (byte)fi; byte[] reply;
            using (var w = new ReplyWaiter(r => Hidpp.IsReply(r, Index, idx, function) || Hidpp.ErrorFor(r, Index, idx, function) != 0, longPort, shortPort))
            {
                if (!port.Write(req)) { LastError = -1; return null; }
                reply = w.Wait(ReplyMs);
            }
            if (reply == null) { LastError = -1; return null; }
            int e = Hidpp.ErrorFor(reply, Index, idx, function); if (e != 0) { LastError = e; return null; }
            return Hidpp.Params(reply);
        }
        // HID++ protocol version (2 or 4.x for HID++ 2.0 devices); 0 = no answer / not reachable (LastError: 9 = asleep, 8 = empty slot)
        public int Ping()
        {
            var p = Call(Hidpp.IRoot, 1, 0, 0, 0x5A);
            return p == null || p.Length < 3 ? 0 : p[0];
        }
        public int FeatureIndex(ushort id)
        {
            int i; if (features.TryGetValue(id, out i)) return i;
            var p = Call(Hidpp.IRoot, 0, (byte)(id >> 8), (byte)id);
            if (p == null) { if (LastError > 0) features[id] = 0; return 0; }   // no answer: ask again next time
            features[id] = p.Length > 0 && p[0] < 0x80 ? p[0] : 0; return features[id];
        }
        public string Name()
        {
            var c = Call(Hidpp.DeviceName, 0); if (c == null || c.Length < 1 || c[0] == 0) return null;
            int count = Math.Min((int)c[0], 64); var sb = new List<byte>();
            while (sb.Count < count)
            {
                var p = Call(Hidpp.DeviceName, 1, (byte)sb.Count); if (p == null || p.Length == 0) break;
                int before = sb.Count;
                foreach (var b in p) { if (sb.Count >= count || b == 0) break; sb.Add(b); }
                if (sb.Count == before) break;
            }
            var s = Encoding.UTF8.GetString(sb.ToArray()).Trim();
            return s.Length == 0 ? null : s;
        }
        public int? DeviceType() { var p = Call(Hidpp.DeviceName, 2); return p == null || p.Length < 1 ? (int?)null : p[0]; }
        // finds the battery feature once (Unified Battery first, then Battery Status, then Battery Voltage); 0 = none
        public ushort FindBattery()
        {
            if (BatteryFeature != 0) return BatteryFeature;
            foreach (var f in new[] { Hidpp.UnifiedBattery, Hidpp.BatteryStatus, Hidpp.BatteryVoltage })
            {
                int i = FeatureIndex(f);
                if (i > 0)
                {
                    BatteryFeature = f; BatteryIndex = (byte)i;
                    if (f == Hidpp.UnifiedBattery) { var cap = Call(Hidpp.UnifiedBattery, 0); SocSupported = cap == null || cap.Length < 2 || (cap[1] & 0x02) != 0; }
                    return f;
                }
                if (LastError < 0) return 0;   // no answer: don't conclude "no battery"
            }
            return 0;
        }
        public BatteryReading Battery()
        {
            var f = FindBattery(); if (f == 0) return null;
            if (f == Hidpp.UnifiedBattery) return Hidpp.Unified(Call(f, 1), SocSupported);
            if (f == Hidpp.BatteryStatus) return Hidpp.Status(Call(f, 0));
            return Hidpp.Voltage(Call(f, 0));
        }
    }
}

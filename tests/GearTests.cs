// Hallow Gear unit tests. No real device is ever opened: HID++ and A50 messages are built and parsed from fixture bytes, a fake
// HID++ mouse answers on a fake port, and the family pipe test runs a test copy with made-up gear (HALLOW_GEAR_FAKE=1).
//   tests\GearTests.exe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

namespace HallowGear.Tests
{
    public static class Run
    {
        static int pass, fail;
        static void Ok(bool c, string name) { if (c) { pass++; Console.WriteLine("ok   " + name); } else { fail++; Console.WriteLine("FAIL " + name); } }
        static string Hex(byte[] b, int n = -1) { return b == null ? "null" : string.Join(" ", b.Take(n < 0 ? b.Length : n).Select(x => x.ToString("X2"))); }
        static byte[] B(string hex) { return hex.Split(' ').Where(x => x.Length > 0).Select(x => Convert.ToByte(x, 16)).ToArray(); }
        static string Dir;

        public static int Main(string[] args)
        {
            Dir = Path.Combine(Path.GetTempPath(), "HallowGearTest" + Process.GetCurrentProcess().Id);
            Environment.SetEnvironmentVariable("HALLOW_GEAR_TESTDIR", Dir);
            try { Building(); Parsing(); AllowList(); NoOtherWrites(); FakeMouse(); A50(); Merge(); States(); Alerts(); GameHold(); HistoryTests(); LookTests(); SettingsTests(); Pipe(); }
            catch (Exception e) { fail++; Console.WriteLine("FAIL exception: " + e); }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            Console.WriteLine(pass + " passed, " + fail + " failed");
            return fail == 0 ? 0 : 1;
        }

        // ---------------- HID++ messages
        static void Building()
        {
            var r = SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 0, Hidpp.SwId, 0x10, 0x04);
            Ok(r != null && Hex(r.Bytes) == "11 01 00 0D 10 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00", "IRoot getFeature(0x1004) on slot 1, long: " + Hex(r == null ? null : r.Bytes));
            r = SafeReport.ForHidpp(false, 2, Hidpp.IRoot, 0, 1, Hidpp.SwId, 0, 0, 0x5A);
            Ok(r != null && Hex(r.Bytes) == "10 02 00 1D 00 00 5A", "ping, short report: " + Hex(r == null ? null : r.Bytes));
            r = SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 5, 1, Hidpp.SwId);
            Ok(r != null && Hex(r.Bytes, 4) == "11 01 05 1D" && r.Length == 20, "0x1004 getStatus at index 5");
            r = SafeReport.ForHidpp(true, 0xFF, Hidpp.DeviceName, 2, 1, Hidpp.SwId, 16);
            Ok(r != null && Hex(r.Bytes, 5) == "11 FF 02 1D 10", "getDeviceName(16) on a corded device (index 0xFF)");
            Ok(r.What.Contains("0x0005") && r.What.Contains("getDeviceName"), "the request says what it is: " + r.What);
            var bytes = r.Bytes; bytes[2] = 0x80; Ok(r.Bytes[2] == 0x02, "Bytes is a copy - changing it can't change what is sent");
        }
        static void Parsing()
        {
            var reply = B("11 01 00 0D 06 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00");
            Ok(Hidpp.IsReply(reply, 1, 0, 0) && Hidpp.Params(reply)[0] == 6, "getFeature reply -> index 6");
            Ok(!Hidpp.IsReply(reply, 2, 0, 0) && !Hidpp.IsReply(reply, 1, 0, 1), "another device or function isn't our reply");
            Ok(!Hidpp.IsReply(B("11 01 00 0A 06 00 01"), 1, 0, 0), "someone else's software id (G HUB's) isn't our reply");
            Ok(Hidpp.ErrorFor(B("11 01 FF 05 1D 05 00 00"), 1, 5, 1) == 5, "HID++ 2.0 error code");
            Ok(Hidpp.ErrorFor(B("10 03 8F 00 1D 08 00"), 3, 0, 1) == 8 && Hidpp.ErrorName(8).Contains("empty"), "receiver HID++ 1.0 error 0x08 = empty slot");
            Ok(Hidpp.ErrorFor(B("10 02 8F 00 1D 09 00"), 2, 0, 1) == 9 && Hidpp.ErrorName(9).Contains("asleep"), "receiver error 0x09 = asleep / off");
            Ok(Hidpp.ErrorFor(reply, 1, 0, 0) == 0, "a good reply isn't an error");
            var u = Hidpp.Unified(B("42 04 00 00"), true);
            Ok(u.Percent == 66 && !u.Charging && !u.Full && !u.External && u.Level == "good", "0x1004: 66 %, good, discharging");
            u = Hidpp.Unified(B("4E 04 01 01"), true); Ok(u.Percent == 78 && u.Charging && u.External, "0x1004: charging on a cable");
            u = Hidpp.Unified(B("64 08 03 01"), true); Ok(u.Percent == 100 && u.Full && !u.Charging, "0x1004: full");
            u = Hidpp.Unified(B("00 02 00 00"), false); Ok(u.Percent == 20 && u.Estimated && u.Level == "low", "0x1004 without percent: level 'low' -> about 20 % (estimated)");
            u = Hidpp.Unified(B("2A 04 02 01"), true); Ok(u.Charging, "0x1004: slow charging counts as charging");
            var s = Hidpp.Status(B("50 32 01")); Ok(s.Percent == 80 && s.Charging, "0x1000: 80 % recharging");
            s = Hidpp.Status(B("64 00 03")); Ok(s.Percent == 100 && s.Full, "0x1000: charged");
            s = Hidpp.Status(B("0F 05 00")); Ok(s.Percent == 15 && !s.Charging && !s.External, "0x1000: 15 % discharging");
            var v = Hidpp.Voltage(B("0F 3C 00")); Ok(v.Millivolts == 3900 && v.Estimated && v.Percent > 55 && v.Percent < 75 && !v.Charging, "0x1001: 3900 mV -> about " + v.Percent + " % (estimated)");
            v = Hidpp.Voltage(B("10 2E 80")); Ok(v.Charging && v.External, "0x1001: external power + charging flag");
            v = Hidpp.Voltage(B("10 5A 81")); Ok(v.Full && v.Percent == 100, "0x1001: end of charge = full");
            Ok(Hidpp.PercentFromMillivolts(4200) == 100 && Hidpp.PercentFromMillivolts(3400) == 0 && Hidpp.PercentFromMillivolts(3800) > Hidpp.PercentFromMillivolts(3700), "voltage table runs the right way");
            Ok(Hidpp.LinkUp(B("10 01 41 10 40 9D 40"), 1) == false && Hidpp.LinkUp(B("10 01 41 10 00 9D 40"), 1) == true && Hidpp.LinkUp(B("10 02 41 10 00 9D 40"), 1) == null, "0x41 connection notification: link down / up, other slot ignored");
            var ev = Hidpp.Passive(B("11 01 05 00 3C 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 1, Hidpp.UnifiedBattery, 5, true);
            Ok(ev != null && ev.Percent == 60, "a battery event the mouse sends by itself is heard (no request)");
            Ok(Hidpp.Passive(B("11 01 06 00 3C 04 00 00"), 1, Hidpp.UnifiedBattery, 5, true) == null && Hidpp.Passive(B("11 01 05 20 3C 04 00 00"), 1, Hidpp.UnifiedBattery, 5, true) == null, "other features / functions aren't mistaken for battery reports");
            Ok(Hidpp.TypeName(3) == "mouse" && Hidpp.TypeName(0) == "keyboard" && Hidpp.TypeName(8) == "headset", "device types");
        }

        // ---------------- the read-only allow-lists
        static void AllowList()
        {
            int allowed = 0; var bad = new List<string>();
            for (int f = 0; f <= 0xFFFF; f++)
                for (byte fn = 0; fn < 16; fn++)
                {
                    var args = f == 0 && fn == 0 ? new byte[] { 0x10, 0x04 } : f == 0 && fn == 1 ? new byte[] { 0, 0, 0x5A } : new byte[0];
                    var name = Hidpp.AllowedName((ushort)f, fn, (byte)(f == 0 ? 0 : 5), args);
                    if (name == null) continue; allowed++;
                    Dictionary<byte, string> fns; if (!Hidpp.Allowed.TryGetValue((ushort)f, out fns) || !fns.ContainsKey(fn)) bad.Add(f.ToString("X4") + "/" + fn);
                    if (!name.StartsWith("get")) bad.Add(f.ToString("X4") + "/" + fn + " " + name);
                }
            Ok(bad.Count == 0 && allowed == Hidpp.Allowed.Values.Sum(x => x.Count), "of all 65536 features x 16 functions only the " + allowed + " listed reads can be built (all 'get...')" + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""));
            var writes = new[] {
                new object[] { 0x2201, 3, "Adjustable DPI setSensorDpi" }, new object[] { 0x2202, 4, "Extended DPI setSensorDpiParameters" }, new object[] { 0x8060, 2, "Report rate setReportRate" },
                new object[] { 0x8061, 3, "Extended report rate set" }, new object[] { 0x8070, 1, "Color LED effects setZoneEffect" }, new object[] { 0x8071, 1, "RGB effects" },
                new object[] { 0x8100, 2, "Onboard profiles setMode" }, new object[] { 0x8100, 6, "Onboard profiles memoryWrite" }, new object[] { 0x1814, 1, "Change host setCurrentHost" },
                new object[] { 0x1815, 4, "Hosts info" }, new object[] { 0x1B04, 3, "Special keys setCidReporting" }, new object[] { 0x2205, 1, "Pointer speed set" },
                new object[] { 0x00C2, 1, "DFU control" }, new object[] { 0x00D0, 1, "DFU" }, new object[] { 0x1802, 0, "Device reset" }, new object[] { 0x1830, 1, "Power modes set" },
                new object[] { 0x0007, 2, "Friendly name setFriendlyName" }, new object[] { 0x1004, 2, "Unified battery showBatteryStatus" }, new object[] { 0x1001, 1, "Battery voltage fn1" },
                new object[] { 0x1000, 2, "Battery status fn2" }, new object[] { 0x0005, 3, "Device name fn3" }, new object[] { 0x0000, 2, "IRoot fn2" }, new object[] { 0x0001, 1, "Feature set" } };
            foreach (var w in writes) Ok(SafeReport.ForHidpp(true, 1, (ushort)(int)w[0], 5, (byte)(int)w[1], Hidpp.SwId) == null, "refused: 0x" + ((int)w[0]).ToString("X4") + " fn" + w[1] + " (" + w[2] + ")");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 0x80, 1, Hidpp.SwId) == null && SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 0x82, 1, Hidpp.SwId) == null, "refused: feature index 0x80 / 0x82 (HID++ 1.0 SET_REGISTER / SET_LONG_REGISTER - receiver writes, pairing)");
            Ok(SafeReport.ForHidpp(true, 0xFF, Hidpp.UnifiedBattery, 0, 1, Hidpp.SwId) == null && SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 3, 0, Hidpp.SwId, 0x10, 0x04) == null, "refused: a feature at the wrong index (IRoot only at 0, others never at 0)");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 0, Hidpp.SwId, 0x22, 0x01) == null && SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 0, Hidpp.SwId, 0x81, 0x00) == null, "refused: looking up features that aren't on the list (DPI 0x2201, onboard profiles 0x8100)");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 5, 1, Hidpp.SwId, 1) == null && SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 1, Hidpp.SwId, 1, 0, 0) == null, "refused: arguments on calls that take none");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 5, 1, 0) == null, "refused: software id 0 (that's for device events)");
            // the A50: only the battery GET, never a payload
            var a50 = Enumerable.Range(0, 256).Where(c => SafeReport.ForA50((byte)c, 0x0C, 64) != null).ToList();
            Ok(a50.Count == 1 && a50[0] == 0x06, "A50: of all 256 commands only 0x06 (battery GET) can be built");
            Ok(SafeReport.ForA50(0x06, 0x0C, 64, 0x01) == null, "A50: the battery command with a payload (a SET) is refused");
            foreach (var w in new[] { new object[] { 0x08, new byte[] { 0xFF, 0x10 }, "volume SET" }, new object[] { 0x09, new byte[] { 0x01, 0xFF, 0x03 }, "sidetone SET" }, new object[] { 0x0F, new byte[] { 0 }, "lights SET" },
                                     new object[] { 0x14, new byte[] { 1 }, "noise gate SET" }, new object[] { 0x0D, new byte[] { 1, 3, 0 }, "EQ SET" } })
                Ok(SafeReport.ForA50((byte)(int)w[0], 0x1B, 64, (byte[])w[1]) == null, "A50 refused: " + w[2]);
            // nothing else can make a SafeReport
            var t = typeof(SafeReport);
            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Ok(ctors.Length == 1 && ctors[0].IsPrivate, "SafeReport has one constructor, and it's private");
            var makers = typeof(Program).Assembly.GetTypes().SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.ReturnType == t && m.DeclaringType == t).Select(m => m.Name).OrderBy(x => x).ToList();
            Ok(string.Join(",", makers) == "ForA50,ForHidpp", "only ForHidpp / ForA50 return a SafeReport: " + string.Join(",", makers));
            Ok(typeof(IReportPort).GetMethod("Write").GetParameters().Single().ParameterType == t, "a port's Write takes a SafeReport and nothing else");
        }
        // the only WriteFile call is HidPort.Write(SafeReport); HidD_SetFeature / SetOutputReport / SetConfiguration aren't even declared
        static void NoOtherWrites()
        {
            var asm = typeof(Program).Assembly; var wf = typeof(HidNative).GetMethod("WriteFile", BindingFlags.NonPublic | BindingFlags.Static);
            Ok(wf != null, "found the WriteFile import");
            var callers = new HashSet<string>(); var setters = new List<string>();
            foreach (var type in asm.GetTypes())
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                 .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
                {
                    if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0 && (m.Name.StartsWith("HidD_Set") || m.Name == "DeviceIoControl")) setters.Add(m.Name);
                    MethodBody body; try { body = m.GetMethodBody(); } catch { continue; } if (body == null) continue;
                    var il = body.GetILAsByteArray();
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x28 && il[i] != 0x6F) continue;
                        try { var target = type.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), type.IsGenericType ? type.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null); if (target == wf) callers.Add(type.Name + "." + m.Name); } catch { }
                    }
                }
            Ok(callers.Count == 1 && callers.Contains("HidPort.Write"), "WriteFile is called from HidPort.Write only: " + string.Join(", ", callers));
            Ok(setters.Count == 0, "no HidD_Set* / DeviceIoControl imports" + (setters.Count > 0 ? ": " + string.Join(", ", setters) : ""));
        }

        // ---------------- a fake HID++ mouse on slot 1 (asleep mouse on slot 2, empty 3..6), through the real HidppDevice / LogiSource code paths
        class FakePort : IReportPort
        {
            public event Action<byte[]> Report; public int OutLength { get { return 20; } }
            public readonly List<byte[]> Written = new List<byte[]>(); public Func<byte[], byte[]> Answer;
            public bool Write(SafeReport r) { var b = r.Bytes; Written.Add(b); var a = Answer(b); var h = Report; if (a != null && h != null) h(a); return true; }
            public void Push(byte[] r) { var h = Report; if (h != null) h(r); }
            public void Dispose() { }
        }
        static byte[] Mouse(byte[] q)
        {
            byte dev = q[1], idx = q[2], fs = q[3]; int fn = fs >> 4;
            Func<byte[], byte[]> L = p => { var r = new byte[20]; r[0] = 0x11; r[1] = dev; r[2] = idx; r[3] = fs; Array.Copy(p, 0, r, 4, Math.Min(16, p.Length)); return r; };
            if (dev == 2) return new byte[] { 0x10, dev, 0x8F, idx, fs, 0x09, 0 };
            if (dev != 1) return new byte[] { 0x10, dev, 0x8F, idx, fs, 0x08, 0 };
            if (idx == 0 && fn == 1) return L(new byte[] { 4, 5, q[6] });
            if (idx == 0 && fn == 0) { int f = q[4] << 8 | q[5]; return L(new byte[] { (byte)(f == 0x0005 ? 2 : f == 0x1004 ? 5 : 0) }); }
            var name = System.Text.Encoding.ASCII.GetBytes("PRO X 2");
            if (idx == 2 && fn == 0) return L(new byte[] { (byte)name.Length });
            if (idx == 2 && fn == 1) return L(name.Skip(q[4]).ToArray());
            if (idx == 2 && fn == 2) return L(new byte[] { 3 });
            if (idx == 5 && fn == 0) return L(new byte[] { 0x0F, 0x02 });
            if (idx == 5 && fn == 1) return L(new byte[] { 66, 4, 0, 0 });
            return new byte[] { 0x11, dev, 0xFF, idx, fs, 0x02 };
        }
        static void FakeMouse()
        {
            var port = new FakePort { Answer = Mouse };
            var d = new HidppDevice(port, null, 1) { ReplyMs = 300 };
            Ok(d.Ping() == 4, "ping: HID++ 4");
            Ok(d.Name() == "PRO X 2", "name read over getDeviceNameCount / getDeviceName");
            Ok(d.DeviceType() == 3, "type: mouse");
            var b = d.Battery(); Ok(b != null && b.Percent == 66 && !b.Charging && d.BatteryFeature == Hidpp.UnifiedBattery && d.BatteryIndex == 5, "battery 66 % via Unified Battery at index 5");
            var asleep = new HidppDevice(port, null, 2) { ReplyMs = 300 }; Ok(asleep.Ping() == 0 && asleep.LastError == 9, "an asleep mouse: error 0x09");
            var empty = new HidppDevice(port, null, 4) { ReplyMs = 300 }; Ok(empty.Ping() == 0 && empty.LastError == 8, "an empty slot: error 0x08");
            var silent = new HidppDevice(new FakePort { Answer = q => null }, null, 1) { ReplyMs = 100 }; Ok(silent.Ping() == 0 && silent.LastError == -1, "no answer: -1, and no feature is cached as missing");
            // every byte that went out was an allow-listed read
            var bad = port.Written.Where(w => { int fn = w[3] >> 4; return !(w[2] == 0 && (fn == 0 || fn == 1)) && !(w[2] == 2 && fn <= 2) && !(w[2] == 5 && fn <= 1); }).ToList();
            Ok(port.Written.Count > 5 && bad.Count == 0 && port.Written.All(w => (w[3] & 0x0F) == Hidpp.SwId), port.Written.Count + " requests sent, all of them reads with Hallow Gear's software id");
            Ok(d.Sent.All(x => x.Contains(" get")), "the log names each request: " + string.Join("; ", d.Sent.Distinct()));
        }
        static void A50()
        {
            var r = SafeReport.ForA50(A50Proto.CmdBattery, A50Proto.HandleBattery, 64);
            Ok(r != null && Hex(r.Bytes, 6) == "02 0C 03 00 06 0C" && r.Length == 64 && r.Bytes.Skip(6).All(x => x == 0), "A50 battery GET = 02 0C 03 00 06 0C, padded to 64");
            var b = A50Proto.Battery(B("02 0C 06 00 06 00 55 00 01"));
            Ok(b != null && b.Percent == 85 && b.Charging && b.External, "A50 reply: 85 %, docked = charging");
            b = A50Proto.Battery(B("02 0C 06 00 06 00 1A 00 00")); Ok(b != null && b.Percent == 26 && !b.Charging && !b.External, "A50 reply: 26 %, off the dock");
            b = A50Proto.Battery(B("02 0C 06 00 06 00 64 00 01")); Ok(b != null && b.Full && !b.Charging, "A50 reply: 100 % on the dock = full");
            Ok(A50Proto.Battery(B("02 0C 03 00 06 0C 00 00 00")) == null, "a bare ack (no payload) isn't a battery reading");
            Ok(A50Proto.Battery(B("02 0C 06 00 08 00 10 00 05")) == null, "a volume push isn't a battery reading");
            Ok(A50Proto.Battery(B("02 0C 06 00 06 00 C8 00 00")) == null, "nonsense percent (200) is ignored");
            var port = new FakePort { Answer = q => q[4] == 6 ? B("02 0C 06 00 06 00 47 00 00") : null };
            var rd = new A50Reader(port) { ReplyMs = 300 }; var got = rd.Battery();
            Ok(got != null && got.Percent == 71 && port.Written.Count == 1 && port.Written[0][4] == 6, "A50 reader: one GET, 71 %");
            Ok(A50Proto.IsControl(new HidInfo { Vid = 0x046D, Pid = 0x0B1C, UsagePage = 0xFF32, Usage = 0x74, Interface = 8, OutLen = 64 }) && !A50Proto.IsControl(new HidInfo { Vid = 0x046D, Pid = 0x0B1C, UsagePage = 0x000C, Interface = 3, OutLen = 0 }), "the A50's control interface is page 0xFF32 / interface 8");
        }

        // ---------------- devices
        static void Merge()
        {
            var t = DateTime.Now;
            var rx = new GearDevice { Key = "mouse:pro x 2", Kind = "mouse", Name = "PRO X 2", Via = "LIGHTSPEED receiver", Battery = 66, Updated = t.AddMinutes(-3) };
            var cable = new GearDevice { Key = "mouse:pro x 2", Kind = "mouse", Name = "PRO X 2", Via = "USB cable", Wired = true, Battery = 67, Charging = true, Updated = t };
            var kb = new GearDevice { Key = "keyboard:g713 keyboard", Kind = "keyboard", Name = "G713 Keyboard", Wired = true, HasBattery = false };
            var m = GearMerge.Merge(new[] { rx, kb, cable });
            Ok(m.Count == 2 && m[0].Kind == "mouse" && m[0].Wired && m[0].Charging && m[0].Battery == 67 && m[0].State == "charging" && m[0].StateLabel.Contains("wired"), "receiver + cable = one mouse card, wired (charging): " + m[0].StateLabel);
            var asleepCable = cable.Clone(); asleepCable.Battery = null; asleepCable.Charging = false; asleepCable.Updated = null;
            m = GearMerge.Merge(new[] { rx, asleepCable }); Ok(m.Count == 1 && m[0].Wired && m[0].Battery == 66, "cable without a battery answer keeps the receiver's reading");
            Ok(GearMerge.KeyFor("mouse", " PRO X 2 ") == "mouse:pro x 2", "keys ignore case and spaces");
        }
        static void States()
        {
            Func<Action<GearDevice>, GearDevice> D = f => { var d = new GearDevice { Key = "k", Kind = "mouse", Name = "M", Battery = 50 }; f(d); return d; };
            Ok(D(d => { }).State == "on-battery" && D(d => { }).StateLabel.Contains("wireless"), "on battery, wireless");
            Ok(D(d => d.Charging = true).State == "charging" && D(d => d.Full = true).State == "full", "charging / full");
            Ok(D(d => d.Asleep = true).State == "asleep" && D(d => d.Connected = false).State == "not-connected", "asleep / not connected");
            Ok(D(d => { d.HasBattery = false; d.Wired = true; }).State == "wired" && D(d => d.Wired = true).State == "wired", "wired");
            Ok(D(d => d.Battery = null).State == "wireless", "connected, battery not known yet: wireless");
            var h = new GearDevice { Kind = "headset", Battery = 90, Charging = true, External = true }; Ok(h.StateLabel == "Charging \u00B7 docked", "headset on its base: charging, docked");
        }

        // ---------------- alerts
        static List<string> Feed(BatteryAlerts a, AlertRules r, GearDevice d, params int[] levels)
        {
            var o = new List<string>(); foreach (var p in levels) { d.Battery = p; o.AddRange(a.Update(d, r, DateTime.Now).Select(x => x.Kind)); } return o;
        }
        static void Alerts()
        {
            var r = new AlertRules(); var a = new BatteryAlerts();
            var d = new GearDevice { Key = "mouse:m", Kind = "mouse", Name = "M", Battery = 30 };
            Ok(string.Join(",", Feed(a, r, d, 30, 25, 21)) == "", "no alert above 20 %");
            Ok(string.Join(",", Feed(a, r, d, 20)) == "low", "20 %: low, once");
            Ok(string.Join(",", Feed(a, r, d, 19, 18, 20, 15, 11)) == "", "...and not again while it drains (or wobbles)");
            Ok(string.Join(",", Feed(a, r, d, 10)) == "critical", "10 %: very low, once");
            Ok(string.Join(",", Feed(a, r, d, 9, 5, 2)) == "", "...and not again");
            d.Charging = true; Ok(string.Join(",", Feed(a, r, d, 12)) == "charging", "charging started");
            Ok(string.Join(",", Feed(a, r, d, 40, 80)) == "", "no repeats while charging");
            d.Charging = false; d.Full = true; Ok(string.Join(",", Feed(a, r, d, 100)) == "full", "fully charged");
            d.Full = false; Ok(string.Join(",", Feed(a, r, d, 99, 50, 21, 20)) == "low", "a new discharge: low again (once per discharge)");
            var a2 = new BatteryAlerts(); var d2 = new GearDevice { Key = "h", Kind = "headset", Name = "ASTRO A50", Battery = 8 };
            Ok(string.Join(",", Feed(a2, r, d2, 8)) == "critical" && string.Join(",", Feed(a2, r, d2, 7, 15)) == "", "first seen at 8 %: only 'very low', not both");
            Ok(string.Join(",", Feed(a2, r, d2, 40, 20)) == "low", "jumping 10+ points above a level (a fresh battery) re-arms it");
            var a3 = new BatteryAlerts(); var d3 = new GearDevice { Key = "w", Kind = "mouse", Name = "M", Battery = 70 };
            Feed(a3, r, d3, 70); d3.Wired = true; Ok(Feed(a3, r, d3, 70).Count == 0, "wired / wireless: off by default");
            var rw = new AlertRules { Wired = true }; d3.Wired = false; Ok(string.Join(",", Feed(a3, rw, d3, 70)) == "wireless", "...and on when asked");
            var off = new AlertRules { Low = false }; var a4 = new BatteryAlerts(); Ok(Feed(a4, off, new GearDevice { Key = "x", Kind = "mouse", Battery = 5 }, 5).Count == 0, "low alerts can be switched off");
            var a5 = new BatteryAlerts(); Ok(Feed(a5, r, new GearDevice { Key = "k", Kind = "keyboard", HasBattery = false }, 0).Count == 0 && Feed(a5, r, new GearDevice { Key = "s", Kind = "mouse", Asleep = true }, 3).Count == 0, "no alerts for a keyboard without battery or an asleep mouse");
            var custom = new AlertRules { Low1 = 30, Low2 = 15 }; var a6 = new BatteryAlerts(); var d6 = new GearDevice { Key = "c", Kind = "mouse", Battery = 50 };
            Ok(string.Join(",", Feed(a6, custom, d6, 50, 30, 16, 15)) == "low,critical", "custom levels 30 / 15");
            var al = new BatteryAlerts().Update(new GearDevice { Key = "e", Kind = "mouse", Name = "M", Battery = 19, Estimated = true }, r, DateTime.Now).Single();
            Ok(al.Title.Contains("19 %") && al.Text.Contains("estimated"), "the alert says the level and if it's an estimate: " + al.Text);
        }
        static void GameHold()
        {
            var router = new AlertRouter(); var t = DateTime.Now;
            Func<string, string, GearAlert> A = (k, kind) => new GearAlert { Key = k, Kind = kind, Title = k + " " + kind, Text = "", At = t = t.AddSeconds(1) };
            Ok(router.Route(new[] { A("mouse", "low") }, false).Count == 1, "no game: shown at once");
            Ok(router.Route(new[] { A("mouse", "low") }, true).Count == 0 && router.Held.Count == 1 && router.OverlayLine != null && router.OverlayLine.Kind == "low", "during a game: nothing shown, held, low battery goes to Overlay");
            router.Route(new[] { A("mouse", "critical") }, true);
            Ok(router.Held.Count == 1 && router.Held[0].Kind == "critical" && router.OverlayLine.Kind == "critical", "a newer low alert for the same device replaces the older one");
            router.Route(new[] { A("headset", "charging") }, true); router.Route(new[] { A("headset", "full") }, true);
            Ok(router.Held.Count == 2 && router.Held.Any(x => x.Kind == "full") && router.OverlayLine.Kind == "critical", "charging -> full keeps only 'full'; not an Overlay line");
            var after = router.GameEnded(DateTime.Now);
            Ok(after.Count == 2 && router.Held.Count == 0 && router.OverlayLine == null, "after the game: the held alerts are shown, the Overlay line goes");
            foreach (var k in new[] { "a", "b", "c", "d", "e" }) router.Route(new[] { A(k, k == "a" ? "low" : "charging") }, true);
            after = router.GameEnded(DateTime.Now);
            Ok(after.Count == 2 && after[0].Kind == "low" && after[1].Kind == "summary" && after[1].Text.Contains("b charging"), "more than 3 held: the low ones plus one summary");
        }
        static void HistoryTests()
        {
            var h = new History(); var t = new DateTime(2026, 11, 22, 12, 0, 0);
            h.Add("m", 80, t); h.Add("m", 80, t.AddMinutes(3)); h.Add("m", 79, t.AddMinutes(4)); h.Add("m", 79, t.AddMinutes(15));
            Ok(h.For("m").Count == 3, "a point when it changes, and at least every 10 min");
            h.Add("m", 70, t.AddHours(25)); Ok(h.For("m").Count == 1, "older than 24 h is dropped");
            var back = History.FromJson(h.ToJson(), t.AddHours(25)); Ok(back.For("m").Count == 1 && back.For("m")[0].P == 70, "saved and read back");
            Ok(History.FromJson("{not json", t).Points.Count == 0 && History.FromJson("{\"m\":[[\"x\",5],[\"2026-11-22T12:00:00\",500]]}", t).For("m").Single().P == 100, "a broken file doesn't crash; values are clamped");
        }
        static void LookTests()
        {
            Ok(!Look.Resolve(null, "hallow", () => true) && Look.Resolve("light", "hallow", () => false) && Look.Resolve("system", "hallow", () => true) && !Look.Resolve("system", "hallow", () => null), "mode: missing = dark, light, system follows Windows");
            Ok(Look.Resolve("dark", "light", () => false) && !Look.Resolve("light", "dark", () => true), "the app's own Look wins");
            Ok(Look.ModeOf("{\"accent\":\"#8B5CF6\",\"mode\":\"Light\"}") == "light" && Look.ModeOf("garbage") == "dark", "theme.json mode parsing");
            foreach (var accent in new[] { "#F07820", "#8B5CF6", "#22C55E", "#FACC15" })
            {
                var bad = Look.LightPairs(accent).Where(p => Look.Contrast((string)p[1], (string)p[2]) < (double)p[3]).Select(p => (string)p[0]).ToList();
                Ok(bad.Count == 0, "light palette contrast with accent " + accent + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""));
            }
        }
        static void SettingsTests()
        {
            var s = new GearSettings { Low1 = 90, Low2 = 95, PollSeconds = 5 }; s.Fix();
            Ok(s.Low1 == 50 && s.Low2 < s.Low1 && s.PollSeconds == 60, "settings are clamped (low <= 50 %, very low below low, at least 60 s)");
            var d = new GearSettings(); d.Fix(); Ok(d.AlertLow && d.Low1 == 20 && d.Low2 == 10 && d.AlertCharging && d.AlertFull && !d.AlertWired && !d.ReadMouseInGames && d.PollSeconds == 60, "defaults: 20 / 10 %, charging + full on, wired switch off, mouse not asked in games, 60 s");
        }

        // ---------------- the family pipe of a test copy (made-up gear, no windows)
        static string Cmd(string exe, string line)
        {
            var psi = new ProcessStartInfo(exe, "--cmd \"" + line + "\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_GEAR_TESTDIR"] = Dir;
            using (var p = Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd().Trim(); if (!p.WaitForExit(15000)) p.Kill(); return o; }
        }
        static Dictionary<string, object> J(string reply) { return reply.StartsWith("ok {") ? new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply.Substring(3)) : null; }
        static void Pipe()
        {
            var exe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "..", "HallowGear.exe"));
            Ok(File.Exists(exe), "the app is built: " + exe);
            Directory.CreateDirectory(Dir);
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_GEAR_TESTDIR"] = Dir; psi.EnvironmentVariables["HALLOW_GEAR_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_GEAR_HEADLESS"] = "1";
            var app = Process.Start(psi);
            try
            {
                string ping = null; for (int i = 0; i < 50 && (ping == null || !ping.StartsWith("ok")); i++) { Thread.Sleep(200); ping = Cmd(exe, "ping"); }
                Ok(ping == "ok {\"app\":\"Gear\",\"version\":\"" + Program.Version + "\"}", "ping: " + ping);
                Cmd(exe, "test-read");
                var st = J(Cmd(exe, "status"));
                Ok(st != null && (string)st["app"] == "Gear" && st.ContainsKey("updated") && (bool)st["running"] && st.ContainsKey("alert") && st.ContainsKey("game") && st.ContainsKey("lastCrash") && st.ContainsKey("lowAt"), "status: app, version, updated, running, alert, game, lowAt, lastCrash");
                var devs = st == null ? null : ((System.Collections.ArrayList)st["devices"]).Cast<Dictionary<string, object>>().ToList();
                Ok(devs != null && devs.Count == 3 && devs.All(x => new[] { "kind", "name", "battery", "state", "wired", "charging", "updated" }.All(x.ContainsKey)), "status.devices: kind, name, battery, state, wired, charging, updated");
                Ok(devs != null && devs.Any(x => (string)x["kind"] == "keyboard" && x["battery"] == null && (string)x["state"] == "wired") && devs.Any(x => (string)x["kind"] == "mouse" && x["battery"] is int), "keyboard: wired, no battery; mouse: a number");
                var file = Path.Combine(Dir, "status.json"); Ok(File.Exists(file) && J("ok " + File.ReadAllText(file)) != null, "status.json written (and valid JSON)");
                var menu = J(Cmd(exe, "menu")); Ok(menu != null && (string)menu["app"] == "Gear" && ((System.Collections.ArrayList)menu["items"]).Count >= 3, "menu");
                Ok(Cmd(exe, "invoke refresh") == "ok" && Cmd(exe, "invoke nope").StartsWith("error") && Cmd(exe, "open settings") == "ok" && Cmd(exe, "open nowhere").StartsWith("error"), "invoke / open");
                Ok(Cmd(exe, "frobnicate") == "error unknown command", "unknown command");
                Ok(Cmd(exe, "test-autostart on") == "ok True" && Cmd(exe, "test-autostart off") == "ok False", "start with Windows on / off (test file, not the real Run key)");
                // alerts: low -> a notification; during a game nothing pops up, the alert goes to status for Overlay; after the game it's shown
                Cmd(exe, "test-battery 0 19");
                var notes = Cmd(exe, "test-notes"); Ok(notes.Contains("Mouse battery low: 19 %"), "19 %: a low battery notification: " + notes);
                Cmd(exe, "test-game Delta Force"); Cmd(exe, "test-battery 0 9");
                var during = Cmd(exe, "test-notes"); st = J(Cmd(exe, "status"));
                var alert = st == null ? null : st["alert"] as Dictionary<string, object>;
                Ok(!during.Contains("very low") && alert != null && ((string)alert["text"]).Contains("9 %") && (string)alert["level"] == "critical" && (string)st["game"] == "Delta Force" && (int)st["held"] == 1, "in a game: no pop-up; status.alert for Overlay: " + (alert == null ? "none" : (string)alert["text"]));
                var asks = J(Cmd(exe, "test-asks")); Cmd(exe, "invoke refresh"); Thread.Sleep(600);
                Cmd(exe, "test-game off"); var after = Cmd(exe, "test-notes"); st = J(Cmd(exe, "status"));
                Ok(after.Contains("Mouse battery very low: 9 %") && st["alert"] == null && (int)st["held"] == 0, "after the game: the held alert is shown, Overlay's line is gone");
                Ok(Cmd(exe, "quit") == "ok", "quit");
                Ok(app.WaitForExit(10000), "it exits");
                var last = J("ok " + File.ReadAllText(file)); Ok(last != null && !(bool)last["running"], "status.json says not running after quit");
                Ok(Cmd(exe, "ping").StartsWith("error") , "--cmd when it isn't running: error (exit 2)");
            }
            finally { if (!app.HasExited) app.Kill(); }
        }
    }
}

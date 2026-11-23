// Hallow Gear 1.3.1 tests: a mouse that was never read before still gets a card when Gear starts during a game. The receiver's pairing
// info (HID++ 1.0 register 0xB5 - read-only, answered by the receiver itself) parsed from fixture bytes and its allow-list; the one gentle
// battery read at start (1-2 short messages) - once in a game, never with "never", every 5 min with "5min"; listening with a remembered
// battery feature; remembered devices (devices.json) shown at once as "last known"; and over the pipe, a test copy started during a game.
// No real device is opened, no window is shown, nothing plays a sound.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace HallowGear.Tests
{
    public static partial class Run
    {
        static void GameReadTests() { ReceiverAllowList(); PairingParsing(); FirstSightInGame(); ScheduleTests(); GameAskSettings(); RememberedTests(); WhoBuildsReports(); }

        // ---- the receiver allow-list: of every HID++ 1.0 sub id x register (x pages / arguments), only the 12 reads of register 0xB5
        static void ReceiverAllowList()
        {
            int allowed = 0; var bad = new List<string>();
            for (int sub = 0; sub < 256; sub++)
                for (int reg = 0; reg < 256; reg++)
                    foreach (var page in new byte[] { 0x00, 0x02, 0x20, 0x25, 0x26, 0x30, 0x40, 0x45, 0x46, 0x50, 0x60, 0xB5, 0xFF })
                    {
                        var r = SafeReport.ForReceiver((byte)sub, (byte)reg, page);
                        if (r == null) continue; allowed++;
                        if (sub != 0x83 || reg != 0xB5 || !r.What.Contains(" get")) bad.Add(sub.ToString("X2") + "/" + reg.ToString("X2") + "/" + page.ToString("X2"));
                    }
            var pages = Enumerable.Range(0, 256).Where(pg => SafeReport.ForReceiver(0x83, 0xB5, (byte)pg) != null).ToList();
            Ok(bad.Count == 0 && pages.Count == 12 && pages.All(pg => (pg >= 0x20 && pg <= 0x25) || (pg >= 0x40 && pg <= 0x45)) && allowed == 4,
                "1.3.1 receiver reads: of 256 sub ids x 256 registers only GET_LONG_REGISTER 0xB5 can be built, and of its 256 pages only 0x20-0x25 (pairing info) and 0x40-0x45 (name) - 12 reads, all 'get...'" + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""));
            foreach (var w in new[] {
                new object[] { 0x80, 0xB2, new byte[] { 0x01, 0x01, 0x1E }, "SET_REGISTER 0xB2 - open the pairing lock" }, new object[] { 0x80, 0xB2, new byte[] { 0x03, 0x01 }, "SET_REGISTER 0xB2 - unpair slot 1" },
                new object[] { 0x80, 0x02, new byte[] { 0x02 }, "SET_REGISTER 0x02 - make the receiver announce its devices" }, new object[] { 0x80, 0x00, new byte[] { 0x00, 0x09, 0x00 }, "SET_REGISTER 0x00 - notification flags" },
                new object[] { 0x82, 0xB5, new byte[] { 0x20 }, "SET_LONG_REGISTER 0xB5" }, new object[] { 0x81, 0xB5, new byte[] { 0x20 }, "GET_REGISTER (short) 0xB5" },
                new object[] { 0x83, 0xB5, new byte[] { 0x30 }, "0xB5 page 0x30 (extended pairing info - not needed)" }, new object[] { 0x83, 0xB5, new byte[] { 0x03 }, "0xB5 page 0x03 (receiver serial)" },
                new object[] { 0x83, 0xB5, new byte[] { 0x20, 0x01 }, "0xB5 page 0x20 with an extra argument" }, new object[] { 0x83, 0xB5, new byte[0], "0xB5 without a page" },
                new object[] { 0x83, 0xF1, new byte[] { 0x01 }, "firmware info register 0xF1" }, new object[] { 0x80, 0xF0, new byte[] { 0x49, 0x43, 0x50 }, "SET_REGISTER 0xF0 - DFU" } })
                Ok(SafeReport.ForReceiver((byte)(int)w[0], (byte)(int)w[1], (byte[])w[3 - 1]) == null, "refused: " + w[3]);
            var rq = SafeReport.ForReceiver(0x83, 0xB5, 0x20);
            Ok(rq != null && Hex(rq.Bytes) == "10 FF 83 B5 20 00 00" && rq.What.Contains("getPairingInfo (slot 1)"), "pairing info of slot 1 = 10 FF 83 B5 20 00 00 (a short report to the receiver itself, index 0xFF): " + (rq == null ? "null" : rq.What));
            rq = SafeReport.ForReceiver(0x83, 0xB5, 0x45); Ok(rq != null && Hex(rq.Bytes) == "10 FF 83 B5 45 00 00" && rq.What.Contains("getDeviceName (slot 6)"), "name of slot 6 = 10 FF 83 B5 45 00 00");
            Ok(SafeReport.ForHidpp(false, 0xFF, Hidpp.IRoot, 0x83, 0, Hidpp.SwId, 0xB5, 0x20) == null, "the HID++ 2.0 builder still refuses sub id 0x83 (receiver registers only through ForReceiver)");
        }

        // ---- pairing info, names and the 0x41 notification from fixture bytes (Solaar's documented layouts)
        static void PairingParsing()
        {
            var pair = Hidpp.Params(B("11 FF 83 B5 20 08 08 40 9D 00 00 02 00 00 00 00 00 00 00 00"));
            var pi = Hidpp.Pairing(pair, 1);
            Ok(pi != null && pi.Slot == 1 && pi.Wpid == 0x409D && pi.Kind == "mouse", "0xB5 page 0x20: slot 1, wpid 409D, kind 2 = mouse");
            Ok(Hidpp.Pairing(Hidpp.Params(B("11 FF 83 B5 22 07 08 40 8E 00 00 01 00 00 00 00 00 00 00 00")), 3).Kind == "keyboard", "page 0x22: slot 3, kind 1 = keyboard");
            Ok(Hidpp.Pairing(pair, 2) == null, "a reply for another slot's page isn't taken for this slot");
            Ok(Hidpp.Pairing(Hidpp.Params(B("11 FF 83 B5 21 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")), 2) == null, "wpid 0000 = nothing paired");
            Ok(Hidpp.RegisterError(B("10 FF 8F 83 B5 03 00")) == 3 && Hidpp.RegisterError(B("10 01 8F 00 1D 08 00")) == 0, "an error for the register read (an empty slot) is recognised; a slot's own error isn't mistaken for it");
            Ok(Hidpp.IsRegisterReply(B("11 FF 83 B5 20 08"), 0x20) && !Hidpp.IsRegisterReply(B("11 FF 83 B5 21 08"), 0x20) && !Hidpp.IsRegisterReply(B("11 01 83 B5 20 08"), 0x20), "the reply is matched by receiver index, sub id, register and page");
            var nm = Hidpp.Params(B("11 FF 83 B5 40 07 50 52 4F 20 58 20 32 00 00 00 00 00 00 00"));
            Ok(Hidpp.PairName(nm, 1) == "PRO X 2", "page 0x40: the name the receiver keeps for slot 1 = 'PRO X 2'");
            Ok(Hidpp.PairName(Hidpp.Params(B("11 FF 83 B5 40 00 00 00")), 1) == null && Hidpp.PairName(Hidpp.Params(B("11 FF 83 B5 40 03 01 02 03")), 1) == null, "an empty or garbled name is ignored");
            var con = Hidpp.FromConnection(B("10 01 41 10 02 9D 40"));
            Ok(con != null && con.Slot == 1 && con.Wpid == 0x409D && con.Kind == "mouse" && con.LinkUp, "0x41 device connection: slot 1, mouse, wpid 409D, link up");
            con = Hidpp.FromConnection(B("10 02 41 10 41 8E 40")); Ok(con != null && con.Kind == "keyboard" && !con.LinkUp, "0x41 with flag 0x40: link lost, kind keyboard");
            Ok(Hidpp.FromConnection(B("11 01 41 10 02 9D 40")) == null && Hidpp.FromConnection(B("10 FF 41 10 02 9D 40")) == null, "only short 0x41 reports for slots 1-6");
            var u = Hidpp.Unified(B("00 04 00 00"), true);
            Ok(u.Percent == 60 && u.Estimated && Hidpp.Unified(B("00 01 00 00"), true).Percent == 0, "0x1004 getStatus 0% with level 'good' = no percentage reported (about 60%), not an empty battery; 0% 'critical' stays 0");
        }

        // a fake LIGHTSPEED receiver: answers 0xB5 for its paired slots (or never, when tells = false), and HID++ 2.0 for each device
        static Func<byte[], byte[]> RxAnswers(Dictionary<int, FakeDev> devs, Dictionary<int, int> kinds, bool tells)
        {
            var dev = Answers(devs);
            return q =>
            {
                if (q[1] == 0xFF && q[2] == 0x83)
                {
                    if (!tells) return null;
                    int page = q[4], slot = page >= 0x40 ? page - 0x3F : page - 0x1F; FakeDev d;
                    if (!devs.TryGetValue(slot, out d)) return new byte[] { 0x10, 0xFF, 0x8F, 0x83, 0xB5, 0x03, 0 };
                    var r = new byte[20]; r[0] = 0x11; r[1] = 0xFF; r[2] = 0x83; r[3] = 0xB5; r[4] = (byte)page;
                    if (page < 0x40) { r[5] = 0x08; r[6] = 0x08; r[7] = 0x40; r[8] = (byte)(0x90 + slot); r[11] = (byte)kinds[slot]; }
                    else { var n = System.Text.Encoding.ASCII.GetBytes(d.Name); r[5] = (byte)n.Length; Array.Copy(n, 0, r, 6, Math.Min(14, n.Length)); }
                    return r;
                }
                return dev(q);
            };
        }
        static string What(byte[] w) { return w[1] == 0xFF && w[2] == 0x83 ? "rx" : w[2] == 0 ? "ifeat" + (w[3] >> 4) : "f" + w[2] + "." + (w[3] >> 4); }

        // ---- the bug: Gear starts while THE FINALS runs, nothing remembered - the PRO X 2 on slot 1 must get a card
        static void FirstSightInGame()
        {
            var devs = new Dictionary<int, FakeDev> { { 1, new FakeDev { Name = "PRO X 2", Type = 3, Features = new Dictionary<int, int> { { 0x0005, 2 }, { 0x1004, 5 } }, Battery = new Dictionary<int, byte[]> { { 0x1004 * 16 + 1, new byte[] { 66, 4, 0, 0 } } } } } };
            var kinds = new Dictionary<int, int> { { 1, 2 } };
            var longPort = new FakePort { Answer = RxAnswers(devs, kinds, true) }; var shortPort = new FakePort { Answer = q => null };
            // short requests are written to the short port, the receiver answers on the long one
            shortPort.Answer = q => { var a = longPort.Answer(q); if (a != null) longPort.Push(a); return null; };
            var infos = new List<HidInfo> {
                new HidInfo { Path = @"\\?\hid#vid_046d&pid_c54d&mi_02&col02#x", Vid = 0x046D, Pid = 0xC54D, UsagePage = 0xFF00, Usage = 2, InLen = 20, OutLen = 20, Interface = 2, Product = "USB Receiver" },
                new HidInfo { Path = @"\\?\hid#vid_046d&pid_c54d&mi_02&col01#x", Vid = 0x046D, Pid = 0xC54D, UsagePage = 0xFF00, Usage = 1, InLen = 7, OutLen = 7, Interface = 2, Product = "USB Receiver" } };
            var st = new GearSettings(); st.Fix();
            var src = new LogiSource(st) { Enumerator = () => infos, Opener = i => i.InLen == 20 ? longPort : shortPort, ReceiverReplyMs = 200, DeviceReplyMs = 200 };
            var heard = new List<string>(); src.Heard += (k, b) => heard.Add(k + " " + b.Percent); int found = 0; src.Found += () => found++;
            var all = new List<byte[]>(); Func<int> sent = () => longPort.Written.Count + shortPort.Written.Count;
            // 1) "never": listen only - the receiver is asked what is paired, the mouse is asked nothing
            var list = src.Read(false, false, true, false);
            var m = list.FirstOrDefault(x => x.Kind == "mouse");
            Ok(m != null && m.Name == "PRO X 2" && m.Waiting && m.Paused && m.Provisional && m.SlotKey == "C54D:1" && !m.Battery.HasValue, "a game at start, nothing remembered: the PRO X 2 (slot 1) gets a card - 'waiting for its battery report (game running)' - named by the receiver");
            var w1 = longPort.Written.Concat(shortPort.Written).ToList();
            Ok(w1.Count > 0 && w1.All(w => What(w) == "rx") && w1.Count <= 12, "...from " + w1.Count + " register reads answered by the receiver itself - nothing at all was sent to the mouse");
            int before = sent(); src.Read(false, false, false, false); src.Read(false, false, false, false);
            Ok(sent() == before, "the receiver is asked once per session: later reads during the game send nothing");
            // 2) the gentle read: exactly getFeature(0x1004) + getStatus to slot 1
            int lb = longPort.Written.Count, sb = shortPort.Written.Count;
            list = src.Read(false, false, false, true); m = list.FirstOrDefault(x => x.Kind == "mouse");
            var g = longPort.Written.Skip(lb).Concat(shortPort.Written.Skip(sb)).ToList();
            Ok(m != null && m.Battery == 66 && !m.Waiting && !m.Paused && m.Updated.HasValue && g.Count == 2 && g[0][1] == 1 && What(g[0]) == "ifeat0" && g[0][4] == 0x10 && g[0][5] == 0x04 && What(g[1]) == "f5.1",
                "the gentle read at start: 2 short messages to the mouse (getFeature 0x1004, getStatus) -> 66%: " + string.Join(" | ", g.Select(x => Hex(x, 6))));
            before = sent(); src.Read(false, false, false, true); src.Read(false, false, false, true); var again = src.Read(false, false, false, false);
            Ok(sent() == before && again.Single(x => x.Kind == "mouse").Battery == 66 && again.Single(x => x.Kind == "mouse").Paused, "...and only once: asking 'gentle' again this session sends nothing; the card keeps 66% (paused)");
            Ok(st.KnownSlotBattery.ContainsKey("C54D:1") && st.KnownSlotBattery["C54D:1"].StartsWith("1004 5 1"), "its battery feature is remembered for listening (C54D:1 -> " + (st.KnownSlotBattery.ContainsKey("C54D:1") ? st.KnownSlotBattery["C54D:1"] : "-") + ")");
            heard.Clear();   // (the answer to its own getStatus was heard too - the same 66%)
            longPort.Push(B("11 01 05 00 3B 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00"));
            Ok(heard.Count == 1 && heard[0] == "mouse:pro x 2 59", "the mouse's own battery report is heard: " + string.Join(", ", heard));
            // 3) a device the receiver announces by itself (0x41): Found, and a card on the next listing - nothing asked
            longPort.Push(B("10 02 41 10 01 8E 40")); before = sent();
            list = src.Read(false, false, false, false);
            Ok(found == 1 && list.Any(x => x.Kind == "keyboard" && x.Waiting && x.SlotKey == "C54D:2") && sent() == before, "the receiver announces a keyboard on slot 2 (0x41): a 'waiting' keyboard card, nothing sent");
            var everything = longPort.Written.Concat(shortPort.Written).ToList();
            Ok(everything.All(w => What(w) == "rx" ? SafeReport.ForReceiver(w[2], w[3], w[4]) != null : w[1] == 1 && (What(w) == "ifeat0" || What(w) == "f5.1")), everything.Count + " messages in all - receiver reads and the 2 battery messages, nothing else");
            src.Dispose();
            // 4) next session, "never": the remembered battery feature lets it listen without asking anything
            var src2 = new LogiSource(st) { Enumerator = () => infos, Opener = i => i.InLen == 20 ? longPort : shortPort, ReceiverReplyMs = 200, DeviceReplyMs = 200 };
            var heard2 = new List<string>(); src2.Heard += (k, b) => heard2.Add(k + " " + b.Percent);
            int devBefore = longPort.Written.Concat(shortPort.Written).Count(w => What(w) != "rx"); var cold = src2.Read(false, false, true, false);
            var deviceMsgs = longPort.Written.Concat(shortPort.Written).Count(w => What(w) != "rx") - devBefore;
            longPort.Push(B("11 01 05 00 3A 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00"));
            Ok(cold.Any(x => x.Kind == "mouse" && x.Waiting) && deviceMsgs == 0 && heard2.Count == 1 && heard2[0].EndsWith(" 58"), "a later session with 'never': the card is there, nothing is sent to the mouse, and its report is heard with the remembered battery feature (58%)");
            src2.Dispose();
            // 5) a receiver that doesn't answer register reads: the gentle read finds the mouse itself (empty slots are answered by the receiver)
            var lp = new FakePort { Answer = RxAnswers(devs, kinds, false) }; var sp = new FakePort(); sp.Answer = q => { var a = lp.Answer(q); if (a != null) lp.Push(a); return null; };
            var st3 = new GearSettings(); st3.Fix();
            var src3 = new LogiSource(st3) { Enumerator = () => infos, Opener = i => i.InLen == 20 ? lp : sp, ReceiverReplyMs = 100, DeviceReplyMs = 200 };
            var none = src3.Read(false, false, true, false);
            var l3 = src3.Read(false, false, false, true); var m3 = l3.FirstOrDefault(x => x.Kind == "mouse");
            var dm = lp.Written.Concat(sp.Written).Where(w => What(w) != "rx").ToList();
            Ok(none.Count == 0 && m3 != null && m3.Battery == 66 && m3.Name == "Logitech mouse" && m3.Provisional && dm.Count(w => w[1] == 1) == 2 && dm.All(w => What(w) == "ifeat0" || (w[1] == 1 && What(w) == "f5.1")),
                "a receiver that doesn't tell its pairings: the gentle read looks in slots 1-6 - 2 messages to the mouse, 1 'are you there' per empty slot (answered by the receiver) -> 'Logitech mouse' 66%");
            src3.Dispose();
            // 6) out of a game nothing changed: the full read names the mouse itself and the receiver's provisional name goes
            var src4 = new LogiSource(st3) { Enumerator = () => infos, Opener = i => i.InLen == 20 ? longPort : shortPort, ReceiverReplyMs = 200, DeviceReplyMs = 200 };
            var full = src4.Read(true, true, true);
            Ok(full.Count == 1 && full[0].Name == "PRO X 2" && !full[0].Provisional && full[0].Battery == 66 && st3.KnownSlots["C54D:1"] == "PRO X 2", "no game: the 1.0 full read as before (ping, name, type, battery) - the device names itself");
            src4.Dispose();
        }

        // ---- the reading schedule on a made-up clock
        static int CountGentle(ReadSchedule s, DateTime t0, int minutes, string ask, bool game, Func<int, bool> gameAt = null)
        {
            int n = 0;
            for (int sec = 0; sec <= minutes * 60; sec += 10)
            {
                var now = t0.AddSeconds(sec); bool gm = gameAt != null ? gameAt(sec) : game;
                var p = s.Plan(now, 60, gm, false, sec == 0, ask, true, 2);
                if (p.Gentle) n++;
                if (gm && p.Mice) return -1000;   // never a full mouse read in a game
                if (gm && p.Bt) return -2000;
                if (p.Any) s.Done(p, now);
            }
            return n;
        }
        static void ScheduleTests()
        {
            var t0 = new DateTime(2026, 11, 22, 21, 0, 0);
            Ok(CountGentle(new ReadSchedule(), t0, 60, "start", true) == 1, "a game at start, 'once when Gear starts': the gentle read happens once in an hour of play - then only listening (no full mouse read, no Bluetooth read)");
            Ok(CountGentle(new ReadSchedule(), t0, 60, "never", true) == 0, "'never': not once");
            Ok(CountGentle(new ReadSchedule(), t0, 30, "5min", true) == 7, "'every 5 min': at start and every 5 min (7 in 30 min)");
            Ok(CountGentle(new ReadSchedule(), t0, 60, "start", false, sec => sec >= 600) == 0, "no game at start (a full read), the game starts 10 min later: 'once when Gear starts' asks nothing during it");
            var s = new ReadSchedule(); var p0 = s.Plan(t0, 60, true, false, true, "start", true, 2);
            Ok(p0.Gentle && !p0.Mice && p0.Head && p0.Rescan && !p0.Bt, "the first read in a game: gentle + headset + rescan (" + p0 + ")");
            s.Done(p0, t0);
            var refresh = s.Plan(t0.AddSeconds(30), 60, true, true, true, "start", true, 2);
            Ok(refresh.Mice && !refresh.Gentle && !refresh.Bt, "Refresh now (clicked by you) during a game: a full read, as in 1.0 (no Bluetooth)");
            // out of a game: 1.0's schedule unchanged
            var o = new ReadSchedule(); var times = new List<string>();
            for (int sec = 0; sec <= 600; sec += 10) { var now = t0.AddSeconds(sec); var p = o.Plan(now, 60, false, false, false, "start", true, 2); if (p.Any) { times.Add(sec + ":" + p); o.Done(p, now); } }
            Ok(times.Count(x => x.Contains("mice")) == 11 && times.Count(x => x.Contains("bluetooth")) == 3 && !times.Any(x => x.Contains("gentle")), "no game: the mouse and headset every 60 s, Bluetooth every 5 min, never 'gentle' (" + times.Count + " reads in 10 min)");
            var gh = new ReadSchedule(); int heads = 0;
            for (int sec = 0; sec <= 600; sec += 10) { var now = t0.AddSeconds(sec); var p = gh.Plan(now, 60, true, false, false, "never", true, 2); if (p.Head) heads++; if (p.Any) gh.Done(p, now); }
            Ok(heads == 6, "in a game the headset is read at most every 2 min (" + heads + " in 10 min)");
        }
        static void GameAskSettings()
        {
            var d = new GearSettings(); d.Fix(); Ok(d.GameAsk == "start", "default: ask the mouse once when Gear starts");
            Directory.CreateDirectory(Dir); var f = Path.Combine(Dir, "settings.json");
            File.WriteAllText(f, "{\"ReadMouseInGames\":true}"); var a = GearSettings.Load();
            File.WriteAllText(f, "{\"ReadMouseInGames\":false,\"GameCritical\":\"overlay\"}"); var b = GearSettings.Load();
            File.WriteAllText(f, "{\"GameAsk\":\"always\"}"); var c = GearSettings.Load();
            File.WriteAllText(f, "{\"GameAsk\":\"never\"}"); var n = GearSettings.Load(); File.Delete(f);
            Ok(a.GameAsk == "5min" && b.GameAsk == "start" && b.GameCritical == "overlay" && c.GameAsk == "start" && n.GameAsk == "never", "a 1.3 file: 'ask during games' on -> every 5 min, off -> once at start; a bad value -> once at start; 'never' kept");
            Ok(MainWindow.GameAskLabel("start") == "Once when Gear starts" && MainWindow.GameAskLabel("5min") == "Every 5 min" && MainWindow.GameAskLabel("never") == "Never", "Settings > While I'm gaming > Ask the mouse for its battery: once when Gear starts / every 5 min / never");
        }

        // ---- remembered devices (devices.json)
        static void RememberedTests()
        {
            var now = new DateTime(2026, 11, 22, 21, 30, 0);
            var mem = new List<RememberedDevice>();
            var mouse = new GearDevice { Key = "mouse:pro x 2", Kind = "mouse", Name = "PRO X 2", Via = "LIGHTSPEED receiver", Battery = 66, Updated = now.AddMinutes(-12), SlotKey = "C54D:1", Source = "HID++ Unified Battery (0x1004)" };
            var kb = new GearDevice { Key = "keyboard:g713 keyboard", Kind = "keyboard", Name = "G713 Keyboard", Wired = true, HasBattery = false, Via = "USB cable" };
            var bt = new GearDevice { Key = "bt:0a1b2c3d4e01", Kind = "earbuds", Name = "Buds", Bluetooth = true, Battery = 80 };
            var wait = new GearDevice { Key = "mouse:g502", Kind = "mouse", Name = "G502", Waiting = true, Paused = true };
            Ok(RememberedGear.Update(mem, new[] { mouse, kb, bt, wait }, now) && mem.Count == 2 && mem.Any(r => r.Key == "mouse:pro x 2" && r.Battery == 66 && r.State == "on-battery" && r.SlotKey == "C54D:1"), "remembered: the mouse (66%, on battery, slot C54D:1) and the keyboard - not Bluetooth devices (they have their own memory) nor a card still waiting");
            Ok(!RememberedGear.Update(mem, new[] { mouse, kb }, now.AddMinutes(1)), "nothing changed -> nothing to save");
            var json = RememberedGear.ToJson(mem); var back = RememberedGear.FromJson(json);
            Ok(back.Count == 2 && back.Any(r => r.Name == "PRO X 2" && r.Battery == 66 && r.Updated != null) && !json.Contains("0a1b2c3d4e01"), "devices.json: written and read back (" + json.Length + " chars), no Bluetooth address in it");
            var card = RememberedGear.ToCard(back.First(r => r.Kind == "mouse"));
            Ok(card.LastKnown && card.Paused && card.Battery == 66 && card.Connected && card.State == "on-battery" && card.SlotKey == "C54D:1", "on start a remembered device is a card at once: last known, 66%, on battery");
            Ok(RememberedGear.LastKnownText(card, now) == "last known: 66% \u00B7 12 min ago", "the card says: " + RememberedGear.LastKnownText(card, now));
            var kcard = RememberedGear.ToCard(back.First(r => r.Kind == "keyboard")); Ok(!kcard.HasBattery && RememberedGear.LastKnownText(kcard, now).StartsWith("last known: wired"), "a keyboard without battery: 'last known: wired...'");
            Ok(RememberedGear.FromJson("{broken").Count == 0 && RememberedGear.FromJson("[{\"Key\":\"bt:aa\",\"Name\":\"x\"},{\"Key\":\"mouse:m\",\"Name\":\"M\",\"Battery\":250}]").Single().Battery == 100, "a broken file reads as empty; Bluetooth keys are dropped; values are clamped");
            // the receiver named it ("provisional"); then the mouse names itself on the same slot: the provisional memory goes
            var m2 = new List<RememberedDevice>();
            RememberedGear.Update(m2, new[] { new GearDevice { Key = "mouse:logitech mouse", Kind = "mouse", Name = "Logitech mouse", Battery = 50, Updated = now, SlotKey = "C54D:1", Provisional = true } }, now);
            RememberedGear.Update(m2, new[] { mouse }, now);
            Ok(m2.Count == 1 && m2[0].Key == "mouse:pro x 2", "a provisional name (from the receiver) is replaced when the device names itself on that slot");
            var asleep = mouse.Clone(); asleep.Asleep = true; asleep.Battery = null; RememberedGear.Update(mem, new[] { asleep }, now.AddMinutes(5));
            Ok(mem.First(r => r.Key == "mouse:pro x 2").State == "asleep" && mem.First(r => r.Key == "mouse:pro x 2").Battery == 66, "asleep: remembered as asleep, keeping its last battery");
            Ok(RememberedGear.Forget(mem, "mouse:pro x 2") && mem.Count == 1, "Forget removes it from the memory too");
            // a fresh battery report clears 'last known' / 'waiting'
            var w2 = card.Clone(); w2.Waiting = true; w2.Apply(new BatteryReading { Percent = 64, Source = "t" }, now);
            Ok(!w2.LastKnown && !w2.Waiting && w2.Battery == 64, "a battery report heard: no longer 'last known' / 'waiting'");
            var j = Controller.DeviceJson(card);
            Ok((bool)j["lastKnown"] && !(bool)j["waiting"] && j.ContainsKey("battery") && j.ContainsKey("updated"), "status.json per device gains lastKnown / waiting (nothing renamed or removed)");
        }

        // ---- the IL scan, extended: who may build each kind of SafeReport
        static void WhoBuildsReports()
        {
            var forR = typeof(SafeReport).GetMethod("ForReceiver"); var forH = typeof(SafeReport).GetMethod("ForHidpp"); var forA = typeof(SafeReport).GetMethod("ForA50");
            var callers = new Dictionary<MethodInfo, SortedSet<string>> { { forR, new SortedSet<string>() }, { forH, new SortedSet<string>() }, { forA, new SortedSet<string>() } };
            foreach (var type in typeof(Program).Assembly.GetTypes().Where(t => t.Namespace != "HallowGear.Tests"))
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                  .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
                {
                    MethodBody body; try { body = m.GetMethodBody(); } catch { continue; } if (body == null) continue;
                    var il = body.GetILAsByteArray();
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x28 && il[i] != 0x6F) continue;
                        MethodBase target; try { target = type.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { continue; }
                        var mi = target as MethodInfo; if (mi != null && callers.ContainsKey(mi)) callers[mi].Add(type.Name + "." + m.Name);
                    }
                }
            Ok(string.Join(",", callers[forR]) == "HidppReceiver.Read", "1.3.1 IL scan: receiver reads are built only in HidppReceiver.Read: " + string.Join(", ", callers[forR]));
            Ok(string.Join(",", callers[forH]) == "HidppDevice.Call", "HID++ 2.0 requests only in HidppDevice.Call: " + string.Join(", ", callers[forH]));
            Ok(callers[forA].Count == 1 && callers[forA].First().StartsWith("A50Reader."), "A50 requests only in the A50 reader: " + string.Join(", ", callers[forA]));
        }

        // ---- over the pipe: a test copy started while a game runs, with a devices.json from an earlier session
        static Process StartCopy(string exe)
        {
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_GEAR_TESTDIR"] = Dir; psi.EnvironmentVariables["HALLOW_GEAR_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_GEAR_HEADLESS"] = "1";
            var app = Process.Start(psi);
            string ping = null; for (int i = 0; i < 50 && (ping == null || !ping.StartsWith("ok")); i++) { Thread.Sleep(200); ping = Cmd(exe, "ping"); }
            return app;
        }
        static Dictionary<string, object> WaitRead(string exe)
        {
            Dictionary<string, object> st = null;
            for (int i = 0; i < 40; i++) { st = J(Cmd(exe, "status")); if (st != null && st["lastRead"] != null) break; Thread.Sleep(250); }
            return st;
        }
        static void PipeStartInGame()
        {
            var exe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "..", "HallowGear.exe"));
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "test-game.txt"), "THE FINALS");
            var seen = DateTime.Now.AddMinutes(-12).ToString("s");
            File.WriteAllText(Path.Combine(Dir, "devices.json"), "[{\"Key\":\"mouse:g502 x plus\",\"Kind\":\"mouse\",\"Name\":\"G502 X PLUS\",\"Via\":\"LIGHTSPEED receiver\",\"State\":\"on-battery\",\"Battery\":66,\"HasBattery\":true,\"Seen\":\"" + seen + "\",\"Updated\":\"" + seen + "\",\"SlotKey\":\"C547:2\"}]");
            var app = StartCopy(exe);
            try
            {
                var st = WaitRead(exe); var asks = J(Cmd(exe, "test-asks"));
                var devs = st == null ? new List<Dictionary<string, object>>() : ((System.Collections.ArrayList)st["devices"]).Cast<Dictionary<string, object>>().ToList();
                var g502 = devs.FirstOrDefault(x => (string)x["name"] == "G502 X PLUS");
                Ok(st != null && (string)st["game"] == "THE FINALS" && (string)st["gameAsk"] == "start", "a test copy started during a game (THE FINALS): status says game + gameAsk 'start'");
                Ok(g502 != null && (bool)g502["lastKnown"] && (int)g502["battery"] == 66 && (bool)g502["paused"], "a device remembered from the last session is shown at once: last known 66%");
                Ok(asks != null && (int)asks["gentle"] == 1 && (int)asks["mouse"] == 0, "the start-up read happened once (gentle 1, full mouse reads 0)");
                Thread.Sleep(1500); Cmd(exe, "test-game THE FINALS"); Thread.Sleep(500); asks = J(Cmd(exe, "test-asks"));
                Ok(asks != null && (int)asks["gentle"] == 1 && (int)asks["mouse"] == 0, "...and not again while the game runs (gentle still 1)");
                var notes = Cmd(exe, "test-notes"); Ok(notes == "ok []", "no notification from remembered values: " + notes);
                Ok(Cmd(exe, "quit") == "ok" && app.WaitForExit(10000), "quit");
                var mem = RememberedGear.FromJson(File.ReadAllText(Path.Combine(Dir, "devices.json")));
                Ok(mem.Any(r => r.Name == "PRO X SUPERLIGHT 2" && r.Battery == 64) && mem.Any(r => r.Name == "G502 X PLUS" && r.Battery == 66) && mem.Any(r => r.Name == "ASTRO A50"), "devices.json after quit: the mouse read now (64%), the remembered G502 (66%), the headset - " + mem.Count + " devices");
                // again, with "never"
                File.WriteAllText(Path.Combine(Dir, "settings.json"), "{\"GameAsk\":\"never\"}");
                app = StartCopy(exe);
                st = WaitRead(exe); Thread.Sleep(1000); asks = J(Cmd(exe, "test-asks"));
                devs = st == null ? new List<Dictionary<string, object>>() : ((System.Collections.ArrayList)st["devices"]).Cast<Dictionary<string, object>>().ToList();
                var m = devs.FirstOrDefault(x => (string)x["name"] == "PRO X SUPERLIGHT 2");
                Ok(asks != null && (int)asks["gentle"] == 0 && (int)asks["mouse"] == 0 && (string)st["gameAsk"] == "never", "'never': started during a game, nothing is asked of the mouse at all (gentle 0)");
                Ok(m != null && (int)m["battery"] == 64, "...and the mouse is still on screen with what was remembered (64%)");
                Ok(Cmd(exe, "quit") == "ok" && app.WaitForExit(10000), "quit");
            }
            finally { if (!app.HasExited) app.Kill(); }
        }
    }
}

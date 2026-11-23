// Hallow Gear 1.3 tests: notifications while gaming - the routes per alert type x game state x monitor count, the per-device
// overrides, the monitor without the game (made-up layouts), the pop-up's no-activate window styles (on a hidden window that is never
// shown), sound choice -> toast XML, Gear's own sound (built, never played) and that no volume / focus API is even imported.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HallowGear.Tests
{
    public static partial class Run
    {
        static List<Mon> Lay(params string[] lines) { return Monitors.Parse(string.Join("\n", lines)); }

        static void GamingDefaults()
        {
            var d = new GearSettings(); d.Fix();
            Ok(d.GameLow == "hold" && d.GameCritical == "popup" && d.GameCharging == "hold" && d.GameFull == "hold" && d.GameWired == "hold", "1.3 defaults: very low = pop-up on another monitor, everything else held until after the game (as 1.2)");
            Ok(d.PopupMonitor == "auto" && d.PopupCorner == "top-right" && d.PopupSeconds == 8 && d.PopupFallback == "hold", "1.3 defaults: the monitor without the game, top right, 8 s, one monitor -> the Overlay line then after the game");
            Ok(d.SoundNormal == "windows" && d.SoundGame == "silent" && d.GameVolume == 30 && d.SoundVolume == 70 && d.DeviceGame.Count == 0, "1.3 defaults: Windows' sound outside games, silent during games, no per-device overrides");
            // a 1.2.2 settings.json (none of the new fields) reads with the 1.3 defaults
            Directory.CreateDirectory(Dir); var f = Path.Combine(Dir, "settings.json");
            File.WriteAllText(f, "{\"AlertLow\":true,\"Low1\":25,\"Low2\":10,\"Look\":\"dark\",\"BtNoAlerts\":[\"bt:0a1b2c3d4e01\"]}");
            var old = GearSettings.Load(); File.Delete(f);
            Ok(old.Low1 == 25 && old.GameCritical == "popup" && old.GameLow == "hold" && old.PopupSeconds == 8 && old.SoundGame == "silent" && old.SoundNormal == "windows" && old.DeviceGame != null, "a 1.2.2 settings file gets the 1.3 defaults (and keeps its own values)");
            var bad = new GearSettings { GameLow = "loud", PopupCorner = "middle", PopupSeconds = 999, PopupFallback = "x", SoundGame = "boom", GameVolume = 500, DeviceGame = new Dictionary<string, string> { { "m", "nope" }, { "h", "overlay" } } }; bad.Fix();
            Ok(bad.GameLow == "hold" && bad.PopupCorner == "top-right" && bad.PopupSeconds == 60 && bad.PopupFallback == "hold" && bad.SoundGame == "silent" && bad.GameVolume == 100 && !bad.DeviceGame.ContainsKey("m") && bad.DeviceGame["h"] == "overlay", "bad values are fixed (unknown modes dropped, volume / seconds clamped)");
        }

        static void GamingRoutes()
        {
            var one = Lay("\\\\.\\DISPLAY1 0 0 1920 1080 primary");
            var two = Lay("\\\\.\\DISPLAY1 0 0 2560 1440 primary", "\\\\.\\DISPLAY2 2560 0 1920 1080");
            var kinds = new[] { "low", "critical", "charging", "full", "wired", "wireless" };
            // defaults: per type x game state x monitor count
            var s = new GearSettings(); s.Fix(); var bad = new List<string>(); int n = 0;
            foreach (var layout in new[] { one, two })
                foreach (var game in new[] { false, true })
                    foreach (var k in kinds)
                    {
                        n++; bool screen = Monitors.Choose(layout, layout[0], s.PopupMonitor) != null;
                        var w = GameRoute.Decide(s, "mouse:x", k, game, screen);
                        var want = !game ? Way.Toast : k == "critical" ? (layout.Count > 1 ? Way.Popup : Way.Hold) : Way.Hold;
                        if (w != want) bad.Add(layout.Count + " monitor(s), game " + game + ", " + k + ": " + w + " (want " + want + ")");
                    }
            Ok(bad.Count == 0 && n == 24, "defaults, 6 alert kinds x game / no game x 1 / 2 monitors (" + n + "): outside games a notification; in a game very low -> pop-up (2 monitors) or held + Overlay (1); the rest held" + (bad.Count > 0 ? ": " + string.Join("; ", bad) : ""));
            // every mode x monitor count x single-monitor fallback
            bad.Clear(); n = 0;
            foreach (var mode in GameModes.All)
                foreach (var fb in GameModes.Fallbacks)
                    foreach (var layout in new[] { one, two })
                    {
                        var t = new GearSettings { GameLow = mode, GameCritical = mode, GameCharging = mode, GameFull = mode, GameWired = mode, PopupFallback = fb }; t.Fix();
                        bool screen = Monitors.Choose(layout, layout[0], t.PopupMonitor) != null;
                        var fbWay = fb == "overlay" ? Way.Overlay : fb == "toast" ? Way.Toast : fb == "same" ? Way.PopupSame : Way.Hold;
                        var want = mode == "hold" ? Way.Hold : mode == "overlay" ? Way.Overlay : mode == "toast" ? Way.Toast : layout.Count > 1 ? Way.Popup : fbWay;
                        foreach (var k in kinds) { n++; var w = GameRoute.Decide(t, "k", k, true, screen); if (w != want) bad.Add(mode + "/" + fb + "/" + layout.Count + "/" + k + ": " + w); }
                    }
            Ok(bad.Count == 0 && n == 4 * 4 * 2 * 6, "every mode x single-monitor fallback x 1 / 2 monitors x kind (" + n + "): hold, Overlay only, Windows anyway, pop-up (or its fallback: held / Overlay / Windows / that screen)" + (bad.Count > 0 ? ": " + string.Join("; ", bad.Take(5)) : ""));
            // the router: only held alerts come back after the game (a pop-up / Windows / Overlay-only alert isn't shown twice)
            var router = new AlertRouter(); var t0 = DateTime.Now;
            Func<string, string, GearAlert> A = (k, kind) => new GearAlert { Key = k, Kind = kind, Title = k + " " + kind, Text = "", At = t0 = t0.AddSeconds(1) };
            var map = new Dictionary<string, Way> { { "low", Way.Hold }, { "critical", Way.Popup }, { "charging", Way.Overlay }, { "full", Way.Toast } };
            var r = router.RouteWays(new[] { A("mouse", "low"), A("headset", "critical"), A("kbd", "charging"), A("pad", "full") }, true, a => map[a.Kind]);
            Ok(r.Count == 4 && r[1].Way == Way.Popup && r[3].Way == Way.Toast && router.Held.Count == 1 && router.Held[0].Kind == "low", "in a game: the held one is kept; pop-up / Windows ones are handed back to be shown now");
            Ok(router.OverlayLine != null && router.OverlayLine.Kind == "charging", "Overlay line only: any alert kind becomes Overlay's line (here 'charging', newer than the held low one)");
            var after = router.GameEnded(DateTime.Now);
            Ok(after.Count == 1 && after[0].Kind == "low" && router.OverlayLine == null, "after the game only the held alert is shown - no duplicates of what popped up during it");
            var none = router.RouteWays(new[] { A("mouse", "critical") }, false, a => Way.Popup);
            Ok(none.Single().Way == Way.Toast && router.Held.Count == 0, "outside a game the game route isn't asked: a normal notification");
            Ok(router.Route(new[] { A("m", "low") }, true).Count == 0 && router.Held.Count == 1, "the 1.2 Route() still holds everything during a game");
        }

        static void GamingPerDevice()
        {
            var s = new GearSettings(); s.Fix();
            s.DeviceGame["mouse:pro x superlight 2|critical"] = "popup"; s.DeviceGame["headset:astro a50"] = "overlay"; s.DeviceGame["bt:0a1b2c3d4e01|wired"] = "toast"; s.GameCritical = "hold";
            Ok(GameRoute.ModeFor(s, "mouse:pro x superlight 2", "critical") == "popup" && GameRoute.ModeFor(s, "mouse:pro x superlight 2", "low") == "hold", "per device + type: the mouse's very low = pop-up, its low battery follows the type setting");
            Ok(GameRoute.ModeFor(s, "headset:astro a50", "low") == "overlay" && GameRoute.ModeFor(s, "headset:astro a50", "full") == "overlay" && GameRoute.ModeFor(s, "headset:astro a50", "critical") == "overlay", "per device (all types): the headset -> Overlay line only");
            Ok(GameRoute.ModeFor(s, "bt:0a1b2c3d4e01", "wireless") == "toast" && GameRoute.ModeFor(s, "other", "critical") == "hold", "wired / wireless share one setting; other devices follow the type settings");
            s.DeviceGame["headset:astro a50|critical"] = "toast";
            Ok(GameRoute.ModeFor(s, "headset:astro a50", "critical") == "toast" && GameRoute.ModeFor(s, "headset:astro a50", "low") == "overlay", "device + type beats the device's all-types choice");
            Ok(GameRoute.Decide(s, "mouse:pro x superlight 2", "critical", true, true) == Way.Popup && GameRoute.Decide(s, "headset:astro a50", "low", true, true) == Way.Overlay && GameRoute.Decide(s, "mouse:pro x superlight 2", "critical", false, true) == Way.Toast, "Decide uses the per-device choice in a game, a normal notification outside");
        }

        static void GamingMonitors()
        {
            var one = Lay("\\\\.\\DISPLAY1 0 0 1920 1080 primary");
            var two = Lay("\\\\.\\DISPLAY1 0 0 2560 1440 primary", "\\\\.\\DISPLAY2 2560 0 1920 1080");
            var three = Lay("\\\\.\\DISPLAY1 0 0 2560 1440 primary", "\\\\.\\DISPLAY2 -1920 0 1920 1080", "\\\\.\\DISPLAY3 2560 -200 1080 1920");
            Func<Mon, string> D = m => m == null ? "none" : m.Device.Substring(4);
            Ok(two.Count == 2 && two[0].Primary && two[1].Number == 2 && two[1].X == 2560 && two[0].WH == 1440 - 48 && three[1].X == -1920, "made-up layouts parse (number, primary, position, work area)");
            Ok(Monitors.Choose(one, one[0], "auto") == null && Monitors.Choose(one, null, "auto") == null, "one monitor: no pop-up screen (-> the fallback)");
            Ok(D(Monitors.Choose(one, one[0], "same")) == "DISPLAY1", "one monitor + \"same as the game\": the game's screen (only when picked)");
            Ok(D(Monitors.Choose(two, two[0], "auto")) == "DISPLAY2" && D(Monitors.Choose(two, two[1], "auto")) == "DISPLAY1", "two monitors: the one without the game (game on 1 -> 2, game on 2 -> 1)");
            Ok(D(Monitors.Choose(two, null, "auto")) == "DISPLAY2", "the game's monitor unknown: the main monitor counts as the game's");
            Ok(D(Monitors.Choose(three, three[1], "auto")) == "DISPLAY1" && D(Monitors.Choose(three, three[0], "auto")) == "DISPLAY2", "three monitors: the main one if it's free, else the lowest number without the game (left-of-main monitor at x -1920 too)");
            Ok(D(Monitors.Choose(three, three[0], "\\\\.\\DISPLAY3")) == "DISPLAY3", "a chosen monitor without the game: that one");
            Ok(D(Monitors.Choose(three, three[2], "\\\\.\\DISPLAY3")) == "DISPLAY1", "the chosen monitor shows the game: never there - another one");
            Ok(Monitors.Choose(two, two[0], "\\\\.\\DISPLAY3") == null, "the chosen monitor isn't connected: none (-> the fallback)");
            Ok(D(Monitors.Choose(two, two[1], "same")) == "DISPLAY2", "\"same as the game\": the game's monitor");
            bool never = true;
            foreach (var lay in new[] { one, two, three })
                foreach (var g in lay)
                    foreach (var pref in new[] { "auto", "\\\\.\\DISPLAY1", "\\\\.\\DISPLAY2", "\\\\.\\DISPLAY3", "\\\\.\\DISPLAY9" })
                    { var m = Monitors.Choose(lay, g, pref); if (m != null && m.Device == g.Device) never = false; }
            Ok(never, "never the game's monitor unless \"same\" is picked (every layout x game monitor x choice)");
            Ok(Monitors.Choose(new List<Mon>(), null, "same") == null && Monitor0(), "no monitors at all: none");
            // corners and stacking (physical pixels)
            var m2 = two[1]; var p0 = Monitors.Place(m2, "top-right", 360, 120, null, 16, 10); var p1 = Monitors.Place(m2, "top-right", 360, 100, new[] { 120 }, 16, 10);
            Ok(p0.X == 2560 + 1920 - 16 - 360 && p0.Y == 16 && p1.X == p0.X && p1.Y == 16 + 120 + 10, "top right of the second monitor; the next one stacks below it");
            var pb = Monitors.Place(two[0], "bottom-left", 360, 120, new[] { 120, 90 }, 16, 10);
            Ok(pb.X == 16 && pb.Y == (1440 - 48) - 16 - 120 - (120 + 10) - (90 + 10), "bottom left above the taskbar; older ones stack upwards");
            var pl = Monitors.Place(three[1], "top-left", 360, 120, null, 16, 10);
            Ok(pl.X == -1920 + 16 && pl.Y == 16 && three[1].Contains((int)pl.X, (int)pl.Y), "a monitor left of the main one (negative x)");
            Ok(Monitors.All().Count >= 0, "the real monitors enumerate without error (" + Monitors.All().Count + ")");
        }
        static bool Monitor0() { return Mon.NumberOf("\\\\.\\DISPLAY12") == 12 && Mon.NumberOf("x") == 0; }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        static void GamingWindow()
        {
            string err = null; var res = new Dictionary<string, object>();
            var th = new Thread(() =>
            {
                try
                {
                    // a hidden pop-up window: created without WS_VISIBLE, checked, closed - ShowNoActivate is never called here
                    var p = new GearPopup(new Border { Width = 120, Height = 40 }, -30000, -30000);
                    res["ex"] = p.ExStyleNow; res["style"] = p.StyleNow; res["visible"] = p.Visible;
                    res["mouseact"] = SendMessage(p.Handle, 0x21, p.Handle, new IntPtr(0x02010001)).ToInt64();
                    res["visibleAfter"] = p.Visible; bool dismissed = false; p.Dismissed += () => dismissed = true; p.Close(); res["dismissed"] = dismissed && p.Closed;
                    // the card: nothing in it can take keyboard focus (focus would activate the window)
                    if (Application.Current == null)
                    {   // the theme as the app loads it, its converters taken from this test assembly (Theme.xaml names assembly=HallowGear)
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        string x; using (var rs = typeof(Program).Assembly.GetManifestResourceStream("HallowGear.Theme.xaml")) using (var rd = new StreamReader(rs)) x = rd.ReadToEnd();
                        x = x.Replace("assembly=HallowGear\"", "assembly=" + typeof(Program).Assembly.GetName().Name + "\"");
                        Program.AccentHex = Accent.Default;
                        app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(Accent.ApplyTo(x, Ui.Readable(Program.AccentHex))));
                    }
                    var card = GearPopup.Card("mouse", "critical", "Mouse battery very low: 9%", "x", () => { }, () => { });
                    var all = new List<DependencyObject>(); Action<DependencyObject> walk = null;
                    walk = o => { all.Add(o); foreach (var ch in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) walk(ch); }; walk(card);
                    res["focusable"] = all.OfType<UIElement>().Count(u => u.Focusable); res["buttons"] = all.Count(o => o is System.Windows.Controls.Primitives.ButtonBase); res["elements"] = all.Count;
                }
                catch (Exception e) { err = e.ToString(); }
            });
            th.SetApartmentState(ApartmentState.STA); th.Start();
            if (!th.Join(60000)) { Ok(false, "pop-up window test: timed out"); return; }
            if (err != null) { Ok(false, "pop-up window test: " + err); return; }
            int ex = (int)res["ex"], style = (int)res["style"];
            Ok((ex & 0x08000000) != 0 && (ex & 0x80) != 0 && (ex & 0x8) != 0, "pop-up: WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST (ex style 0x" + ex.ToString("X8") + ")");
            Ok((ex & 0x40000) == 0 && GearPopup.ExStyle == (0x08000000 | 0x80 | 0x8), "pop-up: no WS_EX_APPWINDOW (so no taskbar button)");
            Ok((style & unchecked((int)0x80000000)) != 0 && (style & 0x10000000) == 0 && (style & 0x00C00000) == 0, "pop-up: WS_POPUP, no caption, created hidden (no WS_VISIBLE)");
            Ok(!(bool)res["visible"] && !(bool)res["visibleAfter"], "the test window was never shown");
            Ok((long)res["mouseact"] == 3, "a click on it answers WM_MOUSEACTIVATE with MA_NOACTIVATE (the game keeps focus)");
            Ok((bool)res["dismissed"], "closing it raises Dismissed (the stack reflows)");
            Ok((int)res["focusable"] == 0 && (int)res["buttons"] == 0 && (int)res["elements"] > 5, "the pop-up's content has no focusable element and no button (" + res["elements"] + " elements)");
            // what the code can call: no focus / foreground / volume API anywhere
            var imports = new SortedSet<string>(); var focusCalls = new SortedSet<string>();
            foreach (var type in typeof(Program).Assembly.GetTypes())
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    {
                        var di = m.GetCustomAttributes(typeof(DllImportAttribute), false).Cast<DllImportAttribute>().FirstOrDefault();
                        imports.Add((di != null && !string.IsNullOrEmpty(di.EntryPoint) ? di.EntryPoint : m.Name));
                    }
                    var owner = type; while (owner.DeclaringType != null) owner = owner.DeclaringType;
                    if (owner != typeof(GearPopup) && owner != typeof(PopupStack)) continue;
                    MethodBody body; try { body = m.GetMethodBody(); } catch { continue; } if (body == null) continue;
                    var il = body.GetILAsByteArray();
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x28 && il[i] != 0x6F) continue; MethodBase tg;
                        try { tg = type.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { continue; }
                        if (tg.Name == "Activate" || tg.Name == "Focus" || tg.Name == "set_ShowActivated" || (tg.Name == "Show" && tg.DeclaringType == typeof(Window))) focusCalls.Add(tg.DeclaringType.Name + "." + tg.Name);
                    }
                }
            var focusApis = imports.Where(x => x == "SetForegroundWindow" || x == "SetActiveWindow" || x == "SetFocus" || x == "BringWindowToTop" || x == "AllowSetForegroundWindow" || x == "SwitchToThisWindow").ToList();
            Ok(focusApis.Count == 0 && focusCalls.Count == 0, "the pop-up code never activates or focuses anything: no SetForegroundWindow / SetActiveWindow / SetFocus / BringWindowToTop import, no Activate / Focus / Window.Show call" + (focusApis.Count + focusCalls.Count > 0 ? ": " + string.Join(", ", focusApis.Concat(focusCalls)) : ""));
            var vol = imports.Where(x => x.IndexOf("Volume", StringComparison.OrdinalIgnoreCase) >= 0 || x.StartsWith("mixer", StringComparison.OrdinalIgnoreCase) || x.StartsWith("waveOut", StringComparison.OrdinalIgnoreCase) || x == "keybd_event" || x == "SendInput").ToList();
            var volTypes = typeof(Program).Assembly.GetTypes().Where(t => t.Name.Contains("AudioEndpointVolume") || t.Name.Contains("SimpleAudioVolume") || t.Name.Contains("AudioSessionManager")).Select(t => t.Name).ToList();
            Ok(vol.Count == 0 && volTypes.Count == 0, "no volume API at all (no waveOutSetVolume / mixer / endpoint or session volume / volume keys): the game's and Windows' volume can't be changed (" + imports.Count + " imports)" + (vol.Count + volTypes.Count > 0 ? ": " + string.Join(", ", vol.Concat(volTypes)) : ""));
        }

        static void GamingSound()
        {
            Func<string, System.Xml.XmlDocument> X = snd => { var d = new System.Xml.XmlDocument(); d.LoadXml(Toasts.Xml(new Note { Title = "Mouse battery low: 20%", Text = "x", Key = "m", Sound = snd })); return d; };
            var win = X("windows").SelectSingleNode("/toast/audio"); var sil = X("silent").SelectSingleNode("/toast/audio"); var hal = X("hallow").SelectSingleNode("/toast/audio");
            Ok(win != null && win.Attributes["src"].Value == "ms-winsoundevent:Notification.Default" && win.Attributes["silent"] == null, "sound 'Windows sound' -> <audio src=\"ms-winsoundevent:Notification.Default\"/>");
            Ok(sil != null && sil.Attributes["silent"].Value == "true" && sil.Attributes["src"] == null, "sound 'Silent' -> <audio silent=\"true\"/>");
            Ok(hal != null && hal.Attributes["silent"].Value == "true", "sound 'Hallow sound' -> the toast is silent (Gear plays its own sound)");
            Ok(X(null).SelectSingleNode("/toast/audio") == null && X("silent").DocumentElement.LastChild.Name == "audio", "no sound chosen -> no <audio> (1.2's XML); <audio> comes after <visual>");
            // which choice a toast gets: outside games SoundNormal, in a game SoundGame
            var s = new GearSettings(); s.Fix();
            Func<bool, string> pick = game => game ? s.SoundGame : s.SoundNormal;
            Ok(pick(false) == "windows" && pick(true) == "silent", "defaults: Windows' sound outside games, silent toasts in games");
            try
            {
                foreach (var snd in new[] { "windows", "silent", "hallow" }) WinToast.Create(Toasts.Xml(new Note { Title = "t", Text = "t", Key = "m", Sound = snd }), "g1", null);
                Ok(true, "Windows accepts the toast XML with each sound choice (built, not shown)");
            }
            catch (Exception e) { Ok(false, "Windows accepts the toast XML with each sound choice: " + e.Message); }
            Ok(Toasts.BalloonFlags(true, true) == 0x34 && Toasts.BalloonFlags(true) == 0x24, "the tray balloon: NIIF_NOSOUND when the sound is silent / Hallow's");
            // Gear's own sound: a short WAV, loudness in its own samples (never played by tests)
            var c30 = GearSound.Chime(30); var c100 = GearSound.Chime(100);
            Ok(System.Text.Encoding.ASCII.GetString(c30, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(c30, 8, 4) == "WAVE" && BitConverter.ToInt16(c30, 22) == 1 && BitConverter.ToInt32(c30, 24) == 44100 && c30.Length > 44 + 44100 / 5 * 2, "the Hallow sound: a 16-bit mono WAV, about a quarter of a second");
            int p30 = GearSound.Peak(c30), p100 = GearSound.Peak(c100);
            Ok(p30 > 0 && p30 < p100 && Math.Abs(p30 - p100 * 0.3) < 400 && p100 <= 0.55 * 32767 + 1, "its volume is in the samples: 30% peaks at " + p30 + ", 100% at " + p100);
            var half = GearSound.Scale(c100, 50);
            Ok(half != null && Math.Abs(GearSound.Peak(half) - p100 / 2) <= 1 && half.Length == c100.Length && GearSound.Scale(new byte[] { 1, 2, 3 }, 50) == null, "a WAV is made quieter by scaling its samples (50%: " + GearSound.Peak(half) + "); not a WAV -> nothing");
            Ok(GearSound.For("silent", 50) == null && GearSound.For("hallow", 50) != null && GearSound.For("windows", 50) != null, "the pop-up's sound: none when silent, the Hallow sound, or Windows' sound made quieter");
        }

        static void GamingTests() { GamingDefaults(); GamingRoutes(); GamingPerDevice(); GamingMonitors(); GamingWindow(); GamingSound(); }
    }
}

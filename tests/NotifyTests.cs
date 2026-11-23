// Hallow Gear 1.2 tests: device icons, the toast XML, the shortcut's AppUserModelID (on a temp shortcut, never the real one) and
// which way a notification goes. Nothing is ever shown: toasts are built but not Show()n, balloons and Hub are fakes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HallowGear.Tests
{
    public static partial class Run
    {
        static void IconTests()
        {
            int bad = 0, total = 0; var notes = new List<string>();
            foreach (var light in new[] { false, true })
            {
                var pal = DeviceIcons.For(light, Accent.Default);
                foreach (var k in DeviceIcons.Kinds)
                    foreach (var s in DeviceIcons.States)
                        foreach (var size in DeviceIcons.Sizes)
                        {
                            total++;
                            var b = DeviceIcons.Bitmap(k, s, size, light, Accent.Default);
                            var px = new byte[size * size * 4]; b.CopyPixels(px, size * 4, 0);
                            Func<int, int, int> A = (x, y) => px[(y * size + x) * 4 + 3];
                            bool corners = A(0, 0) == 0 && A(size - 1, 0) == 0 && A(0, size - 1) == 0;
                            int opaque = 0, match = 0; var want = DeviceIcons.Colour(s, pal);
                            for (int i = 0; i < px.Length; i += 4)
                            {
                                if (px[i + 3] < 250) continue; opaque++;
                                if (Math.Abs(px[i] - want.B) < 6 && Math.Abs(px[i + 1] - want.G) < 6 && Math.Abs(px[i + 2] - want.R) < 6) match++;
                            }
                            bool ok = b.PixelWidth == size && b.PixelHeight == size && corners && opaque > size * size / 12 && match > size * size / 20;
                            if (!ok) { bad++; if (notes.Count < 5) notes.Add(k + "/" + s + "/" + size + (light ? "/light" : "/dark") + " corners " + corners + " opaque " + opaque + " colour " + match); }
                        }
            }
            Ok(bad == 0 && total == 6 * 7 * 4 * 2, "icons: every kind x state at 32 / 48 / 64 / 96 px, dark and light (" + total + "): right size, transparent corners, drawn in the state's colour" + (bad > 0 ? ": " + string.Join("; ", notes) : ""));
            var dk = DeviceIcons.For(false, Accent.Default); var lt = DeviceIcons.For(true, Accent.Default);
            Ok(dk.Neutral != lt.Neutral && dk.Red != lt.Red && dk.Green != lt.Green && dk.Accent != lt.Accent, "icons: dark and light mode have their own colours (readable on each)");
            Ok(DeviceIcons.Colour("low", dk) == dk.Red && DeviceIcons.Colour("critical", dk) == dk.Red && DeviceIcons.Colour("charging", dk) == dk.Green && DeviceIcons.Colour("full", dk) == dk.Green
               && DeviceIcons.Colour("wired", dk) == dk.Neutral && DeviceIcons.Colour("wireless", dk) == dk.Neutral && DeviceIcons.Colour("normal", dk) == dk.Accent, "icons: red low / very low, green charging / full, neutral wired / wireless, accent otherwise");
            var normal = DeviceIcons.Png("mouse", "normal", 64, false, Accent.Default); var low = DeviceIcons.Png("mouse", "low", 64, false, Accent.Default); var crit = DeviceIcons.Png("mouse", "critical", 64, false, Accent.Default);
            Ok(!normal.SequenceEqual(low) && !low.SequenceEqual(crit), "icons: low and very low carry different marks; the state variants differ from the plain icon");
            Ok(DeviceIcons.KindOf("receiver") == "other" && DeviceIcons.KindOf(null) == "other" && DeviceIcons.KindOf("earbuds") == "earbuds" && DeviceIcons.Glyph("earbuds") == "earbuds" && Icons.Get("earbuds") != null && DeviceIcons.Glyph("controller") == "gamepad",
               "icons: earbuds have their own glyph, controller the gamepad, unknown kinds 'other'");
            Ok(DeviceIcons.StateOf("summary") == "normal" && DeviceIcons.StateOf("critical") == "critical" && DeviceIcons.StateOf("wireless") == "wireless", "icons: alert kind -> icon state");
            var d = new GearDevice { Key = "m", Kind = "mouse", Battery = 15 };
            Ok(DeviceIcons.StateOf(d, 20, 10) == "low" && DeviceIcons.StateOf(new GearDevice { Kind = "mouse", Battery = 5 }, 20, 10) == "critical" && DeviceIcons.StateOf(new GearDevice { Kind = "mouse", Battery = 5, Charging = true }, 20, 10) == "charging"
               && DeviceIcons.StateOf(new GearDevice { Kind = "mouse", Battery = 50 }, 20, 10) == "normal" && DeviceIcons.StateOf(new GearDevice { Kind = "mouse", Battery = 5, Connected = false }, 20, 10) == "normal", "cards: the icon state follows the battery");
            // files: in the test dir (a real copy: %LOCALAPPDATA%\HallowGear\icons), the accent in the name
            var png = DeviceIcons.PngFile("headset", "full", 96, false, "#F07820"); var ico = DeviceIcons.IcoFile("headset", "full", false, "#F07820");
            Ok(png.StartsWith(Path.Combine(Dir, "icons")) && File.Exists(png) && Path.GetFileName(png) == "headset-full-dark-f07820-96.png", "icon files go to <dir>\\icons with kind, state, mode and accent in the name: " + Path.GetFileName(png));
            var dec = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Ok(dec.Frames[0].PixelWidth == 96, "the PNG reads back at 96 px");
            var ib = File.ReadAllBytes(ico);
            Ok(BitConverter.ToInt16(ib, 2) == 1 && BitConverter.ToInt16(ib, 4) == 4 && ib[6] == 32 && ib[6 + 16] == 48 && ib[6 + 32] == 64 && ib[6 + 48] == 96 && ib[BitConverter.ToInt32(ib, 6 + 12) + 1] == (byte)'P', "the .ico has 32 / 48 / 64 / 96 px (PNG entries)");
            Ok(Toasts.WindowsLight() || !Toasts.WindowsLight(), "Windows' colour mode reads without error");
        }

        static void ToastXmlTests()
        {
            var logo = DeviceIcons.PngFile("mouse", "low", 96, false, Accent.Default);
            var n = new Note { Title = "Mouse <battery> & \"low\": 20%", Text = "PRO X 'SUPERLIGHT' 2 is at 20%. Plug in <its> cable & go.", Kind = "mouse", State = "low", Key = "mouse:pro x & 'co'", LogoFile = logo };
            var xml = Toasts.Xml(n);
            var doc = new System.Xml.XmlDocument(); doc.LoadXml(xml);
            var texts = doc.SelectNodes("/toast/visual/binding/text").Cast<System.Xml.XmlNode>().ToList();
            Ok(doc.SelectSingleNode("/toast/visual/binding/@template").Value == "ToastGeneric" && texts.Count == 3, "toast XML: ToastGeneric, title + text + attribution");
            Ok(texts[0].InnerText == n.Title && texts[1].InnerText == n.Text, "toast XML: < > & quotes in the texts are escaped and read back exactly");
            Ok(texts[2].Attributes["placement"].Value == "attribution" && texts[2].InnerText == "Hallow Gear", "toast XML: attribution 'Hallow Gear'");
            var img = doc.SelectSingleNode("/toast/visual/binding/image");
            Ok(img != null && img.Attributes["placement"].Value == "appLogoOverride" && img.Attributes["src"].Value == new Uri(logo).AbsoluteUri && img.Attributes["src"].Value.StartsWith("file:///") && File.Exists(new Uri(img.Attributes["src"].Value).LocalPath),
               "toast XML: the device icon as appLogoOverride, a file URI that exists: " + (img == null ? "none" : img.Attributes["src"].Value));
            var launch = doc.SelectSingleNode("/toast/@launch").Value;
            Ok(Toasts.KeyFromLaunch(launch) == n.Key && Toasts.KeyFromLaunch("gear") == null, "toast click: the launch argument carries the device key: " + launch);
            Ok(Toasts.Tag(n).Length <= 16 && Toasts.Tag(n) == Toasts.Tag(new Note { Key = n.Key, Title = "other" }), "toast tag: short, per device (a newer one replaces the older one)");
            var plain = Toasts.Xml(new Note { Title = "While you played", Text = "a \u00B7 b" }); var pd = new System.Xml.XmlDocument(); pd.LoadXml(plain);
            Ok(pd.SelectSingleNode("//image") == null, "toast XML: the summary / tray hint has no device icon (Windows shows Gear's)");
            var sp = new Note { Title = "t", Text = "t", LogoFile = Path.Combine(Dir, "a b", "x.png") };
            Ok(Toasts.Xml(sp).Contains("a%20b/x.png"), "toast XML: spaces in the icon path are URI-escaped");
            // the real WinRT toast object, built from that XML - never shown
            try
            {
                var toast = WinToast.Create(xml, Toasts.Tag(n), null); var back = WinToast.XmlOf(toast);
                Ok(back.Contains("appLogoOverride") && back.Contains("Hallow Gear"), "Windows accepts the XML: a ToastNotification is created (not shown)");
            }
            catch (Exception e) { Ok(false, "Windows accepts the XML: " + e.Message); }
        }

        static void ShortcutTests()
        {
            var real = Toasts.StartMenuShortcut; var realTime = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : DateTime.MinValue;
            var dir = Path.Combine(Dir, "lnk"); Directory.CreateDirectory(dir);
            var lnk = Path.Combine(dir, "Hallow Gear.lnk"); var exe = typeof(Run).Assembly.Location; var other = Path.Combine(dir, "other.exe"); File.WriteAllText(other, "");
            Action<string, string> make = (path, target) =>
            {
                var sh = Type.GetTypeFromProgID("WScript.Shell"); var w = Activator.CreateInstance(sh);
                var s = sh.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, w, new object[] { path });
                s.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { target });
                s.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, s, null);
            };
            make(lnk, exe);
            Ok(ShellLink.GetAumid(lnk) == null && string.Equals(ShellLink.GetTarget(lnk), exe, StringComparison.OrdinalIgnoreCase), "temp shortcut: points at the exe, no ID yet");
            var r1 = Toasts.EnsureShortcutId(exe, lnk); var r2 = Toasts.EnsureShortcutId(exe, lnk);
            Ok(r1 == "set" && r2 == "ok" && ShellLink.GetAumid(lnk) == Toasts.Aumid, "temp shortcut: System.AppUserModel.ID set to " + Toasts.Aumid + " (" + r1 + "), and left alone the second time (" + r2 + ")");
            Ok(string.Equals(ShellLink.GetTarget(lnk), exe, StringComparison.OrdinalIgnoreCase), "temp shortcut: its target is unchanged");
            var lnk2 = Path.Combine(dir, "elsewhere.lnk"); make(lnk2, other);
            Ok(Toasts.EnsureShortcutId(exe, lnk2) == "shortcut points elsewhere" && ShellLink.GetAumid(lnk2) == null, "a shortcut to another exe isn't touched");
            Ok(Toasts.EnsureShortcutId(exe, Path.Combine(dir, "none.lnk")) == "no shortcut", "no shortcut: nothing created");
            Ok(Toasts.NotYetKnown(new System.Runtime.InteropServices.COMException("Element not found.", unchecked((int)0x80070490))) && Toasts.NotYetKnown(new System.Reflection.TargetInvocationException(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80070490)))) && !Toasts.NotYetKnown(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80004005))),
               "1.2.1: Windows not knowing the ID yet (0x80070490, before the first toast) counts as on; other errors don't");
            Ok(Toasts.State(Path.Combine(dir, "none.lnk")) == ToastState.Unavailable && Toasts.State(lnk2) == ToastState.Unavailable, "no shortcut / no ID -> toasts unavailable (fallback)");
            Ok((File.Exists(real) ? File.GetLastWriteTimeUtc(real) : DateTime.MinValue) == realTime, "the real Start menu shortcut wasn't touched by the tests");
        }

        static void RouteTests()
        {
            Ok(NoteSender.Choose(ToastState.On, false) == NoteRoute.Toast && NoteSender.Choose(ToastState.On, true) == NoteRoute.Toast, "route: toasts on -> a toast (also when Hub hosts the icon)");
            Ok(NoteSender.Choose(ToastState.Unavailable, false) == NoteRoute.Balloon && NoteSender.Choose(ToastState.Unavailable, true) == NoteRoute.Hub, "route: no toasts -> Gear's balloon, or Hub when Hub hosts the icon");
            Ok(NoteSender.Choose(ToastState.Off, false) == NoteRoute.None && NoteSender.Choose(ToastState.Off, true) == NoteRoute.None, "route: notifications switched off in Windows -> nothing (not sneaked past as a balloon)");
            Ok(Toasts.BalloonFlags(true) == 0x24, "balloon: NIIF_USER | NIIF_LARGE_ICON");
            Func<ToastState, bool, bool, bool, int[]> run = (ts, hub, toastOk, hubOk) =>
            {
                int t = 0, h = 0, b = 0;
                var s = new NoteSender { Toasts = () => ts, HubHosts = () => hub, ShowToast = n => { t++; return toastOk; }, SendHub = n => { h++; return hubOk; }, ShowBalloon = n => { b++; return true; } };
                s.Send(new Note { Title = "Mouse battery low: 20%", Text = "x", Key = "m" });
                return new[] { t, h, b };
            };
            Func<int[], string> S = a => string.Join(",", a);
            Ok(S(run(ToastState.On, true, true, true)) == "1,0,0", "Hub hosts + toasts: one toast, nothing sent to Hub, no balloon (no duplicate)");
            Ok(S(run(ToastState.On, false, true, true)) == "1,0,0", "own icon + toasts: one toast, no balloon");
            Ok(S(run(ToastState.On, true, false, true)) == "1,1,0", "toast failed, Hub hosts: Hub's notify once, no balloon");
            Ok(S(run(ToastState.Unavailable, true, true, false)) == "0,1,1", "no toasts, Hub didn't answer: Gear's own balloon");
            Ok(S(run(ToastState.Unavailable, false, true, true)) == "0,0,1", "no toasts, own icon: the balloon with the icon");
            Ok(S(run(ToastState.Off, true, true, true)) == "0,0,0", "switched off in Windows: nothing");
            // the same alert twice within 30 s shows once
            int shown = 0; var now = DateTime.Now;
            var snd = new NoteSender { Toasts = () => ToastState.On, ShowToast = n => { shown++; return true; }, Now = () => now };
            var note = new Note { Title = "Headset is fully charged", Text = "ASTRO A50 is fully charged - ready to go.", Key = "h" };
            snd.Send(note); snd.Send(note); now = now.AddSeconds(31); snd.Send(note);
            Ok(shown == 2, "the same alert twice within 30 s is shown once (" + shown + " of 3)");
            // the game hold is unchanged: nothing reaches the sender during a game; afterwards at most 3
            var router = new AlertRouter(); int sent = 0; var t0 = DateTime.Now;
            var gs = new NoteSender { Toasts = () => ToastState.On, ShowToast = n => { sent++; return true; } };
            foreach (var k in new[] { "a", "b", "c", "d", "e" })
                foreach (var a in router.Route(new[] { new GearAlert { Key = k, Kind = k == "a" ? "low" : "charging", Title = k + " t", Text = k, At = t0 = t0.AddSeconds(1) } }, true)) gs.Send(new Note { Title = a.Title, Text = a.Text });
            Ok(sent == 0 && router.Held.Count == 5, "during a game no toast is sent (held)");
            foreach (var a in router.GameEnded(DateTime.Now)) gs.Send(new Note { Title = a.Title, Text = a.Text });
            Ok(sent >= 1 && sent <= 3, "after the game: at most 3 (" + sent + ", the rest as one summary)");
            // the preview's alerts come from the real rules
            var pv = Toasts.PreviewNotes((k, s) => null);
            Ok(pv.Count == 7 && pv[0].Title == "Mouse battery low: 20%" && pv[1].State == "critical" && pv[2].State == "charging" && pv[4].State == "full" && pv[5].Kind == "earbuds" && pv[6].Kind == "controller", "preview: 7 alerts from the real rules (" + string.Join(" | ", pv.Select(x => x.Title)) + ")");
        }
    }
}

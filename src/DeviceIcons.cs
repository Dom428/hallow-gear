// Hallow Gear 1.2 - the device icons: mouse, headset, keyboard, earbuds, controller and other, each in the accent colour, plus state
// variants - red with a low-battery mark (low / very low), green with a bolt (charging / fully charged), neutral with a plug or a
// signal (wired / wireless). Drawn in code from the Font Awesome Free glyphs Gear already uses (the earbuds are Hallow's own path),
// for the device cards and as PNG / ICO files (32, 48, 64, 96 px, transparent) for notifications in %LOCALAPPDATA%\HallowGear\icons.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HallowGear
{
    public static class DeviceIcons
    {
        public static readonly string[] Kinds = { "mouse", "headset", "keyboard", "earbuds", "controller", "other" };
        public static readonly string[] States = { "normal", "low", "critical", "charging", "full", "wired", "wireless" };
        public static readonly int[] Sizes = { 32, 48, 64, 96 };

        public static string KindOf(string deviceKind) { return Kinds.Contains(deviceKind ?? "") ? deviceKind : "other"; }
        public static string Glyph(string kind)
        {
            switch (KindOf(kind)) { case "mouse": return "computer-mouse"; case "headset": return "headphones"; case "keyboard": return "keyboard"; case "earbuds": return "earbuds"; case "controller": return "gamepad"; }
            return "battery-half";
        }
        static string Mark(string state)
        {
            switch (state) { case "low": return "battery-quarter"; case "critical": return "battery-empty"; case "charging": case "full": return "bolt"; case "wired": return "plug"; case "wireless": return "tower-broadcast"; }
            return null;
        }
        // an alert's kind -> the icon state ("summary" and others: normal)
        public static string StateOf(string alertKind) { return States.Contains(alertKind ?? "") && alertKind != "normal" ? alertKind : "normal"; }
        // a device card: red when low, green when charging / full, otherwise the accent (wired / wireless are said by the pill)
        public static string StateOf(GearDevice d, int low1, int low2)
        {
            if (d == null || !d.Connected || d.Asleep) return "normal";
            if (d.Full) return "full"; if (d.Charging) return "charging";
            if (d.HasBattery && d.Battery.HasValue) { if (low2 > 0 && d.Battery <= low2) return "critical"; if (d.Battery <= low1) return "low"; }
            return "normal";
        }

        // colours: the same as the app's theme (Theme.xaml dark, Look.Light) so the cards, toasts and Overlay agree
        public class Palette { public Color Accent, Red, Green, Neutral, OnBadge; }
        public static Palette For(bool light, string accentHex)
        {
            Func<string, Color> C = h => (Color)ColorConverter.ConvertFromString(h);
            string acc; try { acc = light ? Look.DarkAccent(accentHex) : Ui.Readable(accentHex); } catch { acc = light ? "#B85410" : Accent.Default; }
            return light ? new Palette { Accent = C(acc), Red = C("#C2333F"), Green = C("#1B7A44"), Neutral = C("#6B6478"), OnBadge = C("#FFFFFF") }
                         : new Palette { Accent = C(acc), Red = C("#E05A64"), Green = C("#6ED28C"), Neutral = C("#9790A8"), OnBadge = C("#14101B") };
        }
        public static Color Colour(string state, Palette p)
        {
            switch (state) { case "low": case "critical": return p.Red; case "charging": case "full": return p.Green; case "wired": case "wireless": return p.Neutral; }
            return p.Accent;
        }

        // the icon in a 100 x 100 box: the glyph (with a ring cut out where the badge sits) and the round badge with its mark
        public static Drawing Make(string kind, string state, bool light, string accentHex)
        {
            var p = For(light, accentHex); var col = Colour(state, p); var mark = Mark(state);
            var dg = new DrawingGroup();
            using (var ctx = dg.Open())
            {
                ctx.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 100, 100));   // keeps the box 100 x 100 whatever the glyph's shape
                Geometry g = Fit(Icons.Get(Glyph(kind)), new Rect(6, 6, 88, 88));
                if (mark != null) g = Geometry.Combine(g, new EllipseGeometry(new Point(76, 76), 27, 27), GeometryCombineMode.Exclude, null);
                ctx.DrawGeometry(Frozen(col), null, g);
                if (mark != null)
                {
                    ctx.DrawEllipse(Frozen(col), null, new Point(76, 76), 22.5, 22.5);
                    ctx.DrawGeometry(Frozen(p.OnBadge), null, Fit(Icons.Get(mark), new Rect(76 - 15, 76 - 15, 30, 30)));
                }
            }
            dg.Freeze(); return dg;
        }
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static Geometry Fit(Geometry src, Rect box)
        {
            var g = src.Clone(); var b = src.Bounds; double s = Math.Min(box.Width / b.Width, box.Height / b.Height);
            var m = Matrix.Identity; m.Translate(-b.X - b.Width / 2, -b.Y - b.Height / 2); m.Scale(s, s); m.Translate(box.X + box.Width / 2, box.Y + box.Height / 2);
            g.Transform = new MatrixTransform(m); return g;
        }

        // for the device cards
        public static FrameworkElement Element(string kind, string state, double size, bool light, string accentHex)
        {
            var img = new Image { Source = new DrawingImage(Make(kind, state, light, accentHex)), Width = size, Height = size, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality); return img;
        }

        // pixels (any thread: WPF drawing runs on an STA thread of its own when the caller isn't one)
        public static BitmapSource Bitmap(string kind, string state, int size, bool light, string accentHex)
        {
            return Sta(() =>
            {
                var v = new DrawingVisual();
                using (var ctx = v.RenderOpen()) { ctx.PushTransform(new ScaleTransform(size / 100.0, size / 100.0)); ctx.DrawDrawing(Make(kind, state, light, accentHex)); ctx.Pop(); }
                var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bmp.Render(v); bmp.Freeze(); return (BitmapSource)bmp;
            });
        }
        public static byte[] Png(string kind, string state, int size, bool light, string accentHex) { return Encode(Bitmap(kind, state, size, light, accentHex)); }
        public static byte[] Encode(BitmapSource b)
        {
            return Sta(() => { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b)); using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); } });
        }
        // an .ico with all four sizes (PNG-compressed entries, Vista+)
        public static byte[] Ico(string kind, string state, bool light, string accentHex)
        {
            var pngs = Sizes.Select(s => Png(kind, state, s, light, accentHex)).ToList();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
                int offset = 6 + 16 * pngs.Count;
                for (int i = 0; i < pngs.Count; i++)
                {
                    int s = Sizes[i]; w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
                w.Flush(); return ms.ToArray();
            }
        }

        static T Sta<T>(Func<T> f)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return f();
            T r = default(T); Exception err = null;
            var t = new Thread(() => { try { r = f(); } catch (Exception e) { err = e; } }); t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
            if (!t.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("icon rendering took too long");
            if (err != null) throw err; return r;
        }

        // the files notifications use: %LOCALAPPDATA%\HallowGear\icons (a test copy: <test dir>\icons). The accent is in the name, so a
        // new accent gets new files (Windows caches toast images by path).
        public static string Dir { get { return GearSettings.TestDir != null ? Path.Combine(GearSettings.TestDir, "icons") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HallowGear", "icons"); } }
        public static string FileName(string kind, string state, bool light, string accentHex, string ext, int size = 0)
        {
            var acc = (accentHex ?? "").TrimStart('#').ToLowerInvariant(); if (acc.Length == 0) acc = "default";
            return KindOf(kind) + "-" + StateOf(state) + "-" + (light ? "light" : "dark") + "-" + acc + (size > 0 ? "-" + size : "") + "." + ext;
        }
        static readonly object fileLock = new object();
        public static string PngFile(string kind, string state, int size, bool light, string accentHex, string dir = null)
        {
            var f = Path.Combine(dir ?? Dir, FileName(kind, state, light, accentHex, "png", size));
            lock (fileLock) if (!File.Exists(f)) Write(f, Png(KindOf(kind), StateOf(state), size, light, accentHex));
            return f;
        }
        public static string IcoFile(string kind, string state, bool light, string accentHex, string dir = null)
        {
            var f = Path.Combine(dir ?? Dir, FileName(kind, state, light, accentHex, "ico"));
            lock (fileLock) if (!File.Exists(f)) Write(f, Ico(KindOf(kind), StateOf(state), light, accentHex));
            return f;
        }
        static void Write(string f, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)); var tmp = f + ".tmp";
            File.WriteAllBytes(tmp, data); if (File.Exists(f)) File.Delete(tmp); else File.Move(tmp, f);
        }
    }
}

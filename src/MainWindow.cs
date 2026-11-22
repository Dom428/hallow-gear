// Hallow Gear main window: Your gear (a card per device: battery bar and %, state, last read, a 24-hour sparkline), Settings and
// "What it can read" (the honest list). Built in code; every colour is a Theme.xaml key, so light / dark just swaps values.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HallowGear
{
    public class MainWindow : Window
    {
        readonly Controller c;
        string page = "devices";
        readonly Dictionary<string, Button> nav = new Dictionary<string, Button>();
        Border content; TextBlock sayText; Button updateBtn; DispatcherTimer sayTimer, clock;
        public bool Exiting;
        public static readonly string[][] Pages = { new[] { "devices", "Your gear", "battery-three-quarters" }, new[] { "settings", "Settings", "gear" }, new[] { "about", "What it can read", "circle-info" } };
        public static string PageId(string s)
        {
            var v = (s ?? "").Trim().ToLowerInvariant();
            if (v == "gear" || v == "battery" || v == "home") v = "devices"; if (v == "alerts") v = "settings"; if (v == "help" || v == "read") v = "about";
            return Pages.Any(p => p[0] == v) ? v : null;
        }

        public MainWindow(Controller controller)
        {
            c = controller;
            Ui.Dress(this, "Hallow Gear", 940);
            SizeToContent = SizeToContent.Manual; Height = 700; MinWidth = 760; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Rebuild();
            Action changed = () => Dispatcher.BeginInvoke(new Action(() => { if (page == "devices") BuildPage(); }));
            c.Changed += changed;
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; clock.Tick += (s, e) => { if (page == "devices") BuildPage(); }; if (!Snap.On) clock.Start();
            Closed += (s, e) => { c.Changed -= changed; clock.Stop(); c.MainClosed(); };
        }

        public void Rebuild()
        {
            Ui.Restyle(this);
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var side = new DockPanel { Background = Ui.R("Side"), LastChildFill = false };
            var head = new StackPanel { Margin = new Thickness(16, 18, 16, 18) };
            head.Children.Add(Ui.Header("GEAR", 18));
            head.Children.Add(Ui.Muted("Your gear's battery", 11.5, 0));
            DockPanel.SetDock(head, Dock.Top); side.Children.Add(head);
            nav.Clear();
            foreach (var p in Pages)
            {
                var id = p[0];
                var b = new Button { Style = Ui.S("Nav"), Content = Icons.WithText(p[2], p[1], 14, Ui.R("Text")), Margin = new Thickness(8, 0, 8, 0) };
                b.Click += (s, e) => Show(id);
                DockPanel.SetDock(b, Dock.Top); side.Children.Add(b); nav[id] = b;
            }
            var foot = new StackPanel { Margin = new Thickness(16, 0, 16, 14) };
            updateBtn = Ui.Btn("Update", () => c.InstallUpdate(), true); updateBtn.Visibility = c.PendingUpdate == null ? Visibility.Collapsed : Visibility.Visible; updateBtn.HorizontalAlignment = HorizontalAlignment.Left; updateBtn.Margin = new Thickness(0, 0, 0, 10);
            if (c.PendingUpdate != null) updateBtn.Content = "Update to v" + c.PendingUpdate.Version.ToString(2);
            foot.Children.Add(updateBtn);
            foot.Children.Add(Ui.Muted("v" + Program.Version + " \u00B7 reads only", 11, 0));
            DockPanel.SetDock(foot, Dock.Bottom); side.Children.Add(foot);
            root.Children.Add(side);
            root.Children.Add(new Border { Width = 1, Background = Ui.R("Line"), HorizontalAlignment = HorizontalAlignment.Right });

            var right = new DockPanel(); Grid.SetColumn(right, 1);
            var bar = new DockPanel { Background = Ui.R("Side"), Height = 28 };
            sayText = new TextBlock { FontSize = 12, Foreground = Ui.R("TextSoft"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            bar.Children.Add(sayText);
            var barWrap = new Border { BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
            DockPanel.SetDock(barWrap, Dock.Bottom); right.Children.Add(barWrap);
            content = new Border(); right.Children.Add(content);
            root.Children.Add(right);
            Content = root;
            Show(page);
        }
        public void ShowUpdate(string label) { if (updateBtn == null) return; updateBtn.Content = label; updateBtn.Visibility = Visibility.Visible; }
        public void Say(string text)
        {
            if (sayText == null) return; sayText.Text = text ?? "";
            if (sayTimer != null) sayTimer.Stop();
            sayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) }; sayTimer.Tick += (s, e) => { sayTimer.Stop(); sayText.Text = ""; }; sayTimer.Start();
        }
        public void Show(string id)
        {
            page = PageId(id) ?? "devices";
            foreach (var kv in nav) kv.Value.Tag = kv.Key == page ? "on" : null;
            BuildPage();
        }
        void BuildPage()
        {
            FrameworkElement el = page == "settings" ? SettingsPage() : page == "about" ? AboutPage() : DevicesPage();
            content.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = el };
        }

        public static string Ago(DateTime? t, DateTime now)
        {
            if (!t.HasValue) return "not read yet";
            var s = (now - t.Value).TotalSeconds;
            if (s < 60) return "just now"; if (s < 3600) return (int)(s / 60) + " min ago"; if (s < 86400) return (int)(s / 3600) + " h ago";
            return t.Value.ToString("d MMM HH:mm");
        }

        // ---------------- Your gear
        FrameworkElement DevicesPage()
        {
            var sp = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var refresh = Ui.Btn(c.Busy ? "Reading..." : "Refresh now", () => { c.RefreshNow(); Say("Reading your gear..."); }); refresh.Margin = new Thickness(0);
            DockPanel.SetDock(refresh, Dock.Right); top.Children.Add(refresh);
            top.Children.Add(new TextBlock { Text = "Your gear", FontSize = 22, FontFamily = Ui.Head, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(top);
            var now = DateTime.Now;
            string how = c.Game != null ? "A game is running (" + c.Game + "): the mouse is only listened to, never asked; the headset is read at most every 2 min; Bluetooth devices aren't read (only what they send by themselves is heard). Alerts wait until you're done."
                : "Read " + Ago(c.LastRead, now) + " \u00B7 every " + (c.IntervalSeconds / 60) + " min" + (Controller.OnBattery() ? " (on battery)" : "") + " and when gear is plugged in or out" + (c.S.Bluetooth ? " \u00B7 Bluetooth devices every 5 min." : ".");
            sp.Children.Add(Ui.Muted(how, 12, 14));
            if (c.Game != null && c.Router.Held.Count > 0)
            {
                var held = string.Join(" \u00B7 ", c.Router.Held.Select(a => a.Title));
                var banner = new Border { Background = Ui.R("AmberBg"), BorderBrush = Ui.R("Amber"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 14),
                    Child = Icons.WithText("bell-slash", "Held for after the game: " + held + (c.Router.OverlayLine != null ? "  (shown as a quiet line in Hallow Overlay)" : ""), 13, Ui.R("Amber")) };
                ((TextBlock)((StackPanel)banner.Child).Children[1]).Foreground = Ui.R("Text"); ((TextBlock)((StackPanel)banner.Child).Children[1]).TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(banner);
            }
            var shown = c.VisibleDevices.ToList();
            if (shown.Count == 0)
                sp.Children.Add(Ui.Card(Ui.Muted(c.LastRead == null ? "Looking for your gear..." : "No gear found. Plug in the LIGHTSPEED, Unifying or Bolt receiver, the A50 base station or a Logitech keyboard - or connect a Bluetooth device that reports its battery to Windows.", 13)));
            foreach (var d in shown) { var card = Card(d, now); card.Margin = new Thickness(0, 0, 0, 12); card.Tag = d.Key; sp.Children.Add(card); }
            int hidden = c.Devices.Count - shown.Count;
            if (hidden > 0) sp.Children.Add(Ui.Muted(hidden + " hidden Bluetooth device" + (hidden == 1 ? "" : "s") + " - Settings > Bluetooth devices shows " + (hidden == 1 ? "it" : "them") + " again.", 11.5, 0));
            return sp;
        }
        public static string KindIcon(GearDevice d)
        {
            switch (d.Kind) { case "headset": return "headphones"; case "earbuds": return "earbuds"; case "keyboard": return "keyboard"; case "mouse": return "computer-mouse"; case "controller": return "gamepad"; }
            return d.Bluetooth ? "tower-broadcast" : "plug";
        }
        // 1.2: the same icons as the notifications (red when low, green when charging / full); faint when it isn't connected
        FrameworkElement CardIcon(GearDevice d)
        {
            if (!d.Connected || d.Asleep) return Icons.Make(KindIcon(d), 22, Ui.R("Faint"));
            var kind = DeviceIcons.Kinds.Contains(d.Kind) ? d.Kind : null;
            if (kind == null) return Icons.Make(KindIcon(d), 22, Ui.R("AccentBrush"));
            return DeviceIcons.Element(kind, DeviceIcons.StateOf(d, c.S.Low1, c.S.Low2), 30, Program.Light, Program.AccentHex);
        }
        // a toast was clicked: show that device's card
        public void FocusDevice(string key)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var sv = content.Child as ScrollViewer; var sp = sv == null ? null : sv.Content as StackPanel; if (sp == null) return;
                var card = sp.Children.OfType<FrameworkElement>().FirstOrDefault(x => (x.Tag as string) == key);
                if (card != null) card.BringIntoView();
            }), DispatcherPriority.Loaded);
        }
        string[] PillKeys(GearDevice d)
        {
            switch (d.State)
            {
                case "charging": case "full": return new[] { "Green", "GreenBg" };
                case "not-connected": return new[] { "Red", "RedBg" };
                case "asleep": return new[] { "Muted", "PanelHi" };
            }
            if (d.HasBattery && d.Battery.HasValue && d.Battery <= c.S.Low2) return new[] { "Red", "RedBg" };
            if (d.HasBattery && d.Battery.HasValue && d.Battery <= c.S.Low1) return new[] { "Amber", "AmberBg" };
            return new[] { "Lilac", "LilacBg" };
        }
        Border Card(GearDevice d, DateTime now)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            var ic = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(4), Background = Ui.R("PanelHi"), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Child = CardIcon(d) };
            ((FrameworkElement)ic.Child).HorizontalAlignment = HorizontalAlignment.Center; ((FrameworkElement)ic.Child).VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(ic);

            var mid = new StackPanel { Margin = new Thickness(0, 0, 18, 0) }; Grid.SetColumn(mid, 1);
            var title = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            title.Children.Add(new TextBlock { Text = d.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            var pk = PillKeys(d); var pill = Ui.Pill((d.Paused ? "Paused \u00B7 " : "") + d.StateLabel, pk[0], pk[1]); title.Children.Add(pill);
            mid.Children.Add(title);
            if (d.HasBattery)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var pct = new TextBlock { Text = d.Battery.HasValue ? d.Battery + " %" : "\u2013 %", FontFamily = Ui.Mono, FontSize = 22, FontWeight = FontWeights.SemiBold, Width = 86, VerticalAlignment = VerticalAlignment.Center };
                if (d.Estimated) pct.ToolTip = "Estimated from the battery's voltage or level";
                DockPanel.SetDock(pct, Dock.Right); row.Children.Add(pct);
                var track = new Border { Height = 12, CornerRadius = new CornerRadius(3), Background = Ui.R("LineDim"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
                var fillGrid = new Grid(); int v = d.Battery ?? 0;
                fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, v), GridUnitType.Star) });
                fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - v), GridUnitType.Star) });
                var fillKey = d.Charging || d.Full ? "Green" : v <= c.S.Low2 ? "Red" : v <= c.S.Low1 ? "Amber" : "Green";
                if (!d.Connected || d.Asleep || !d.Battery.HasValue) fillKey = "Faint";
                fillGrid.Children.Add(new Border { Background = Ui.R(fillKey), CornerRadius = new CornerRadius(2) });
                track.Child = fillGrid; row.Children.Add(track);
                mid.Children.Add(row);
            }
            var bits = new List<string> { d.Via };
            if (d.Bluetooth) bits.Add(d.Connected ? "connected" : d.LastSeen.HasValue ? "last seen " + Ago(d.LastSeen, now) : "not seen yet");
            if (d.Bluetooth && !c.AlertsOn(d)) bits.Add("alerts off");
            if (d.HasBattery && !(d.Bluetooth && !d.Connected)) bits.Add(d.Paused ? "paused while you play \u00B7 last read " + Ago(d.Updated, now) : "updated " + Ago(d.Updated, now));
            mid.Children.Add(Ui.Muted(string.Join(" \u00B7 ", bits.Where(x => !string.IsNullOrEmpty(x))), 11.5, 2));
            if (d.Source != null) mid.Children.Add(Ui.Muted("From: " + d.Source + (d.Estimated ? " (estimated)" : ""), 11, 2));
            if (d.Note != null) { var n = Ui.Muted(d.Note, 11.5, 0); n.FontStyle = FontStyles.Italic; mid.Children.Add(n); }
            if (!d.Connected && c.S.KnownGear.ContainsKey(d.Key)) { var key = d.Key; var fb = Ui.Btn("Forget", () => c.Forget(key)); fb.HorizontalAlignment = HorizontalAlignment.Left; fb.Margin = new Thickness(0, 8, 0, 0); fb.ToolTip = "Stop showing it here (it comes back when it is plugged in again)"; mid.Children.Add(fb); }
            g.Children.Add(mid);

            var spark = new StackPanel(); Grid.SetColumn(spark, 2);
            if (d.HasBattery)
            {
                spark.Children.Add(Ui.Caps("Last 24 h", 10));
                spark.Children.Add(Sparkline(c.Hist.For(d.Key), now, 250, 62));
            }
            g.Children.Add(spark);
            return Ui.Card(g, 16);
        }
        FrameworkElement Sparkline(List<History.Pt> pts, DateTime now, double w, double h)
        {
            var cv = new Canvas { Width = w, Height = h, Background = Ui.R("Well"), Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true };
            foreach (var y in new[] { 0.5 }) cv.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * y, Y2 = h * y, Stroke = Ui.R("LineDim"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 } });
            if (pts.Count < 2) { var t = Ui.Muted(pts.Count == 0 ? "No history yet" : "History starts now", 11); Canvas.SetLeft(t, 8); Canvas.SetTop(t, h / 2 - 8); cv.Children.Add(t); return cv; }
            var t0 = now.AddHours(-24);
            Func<History.Pt, Point> P = p => new Point(Math.Max(0, Math.Min(w, (p.T - t0).TotalMinutes / (24 * 60) * w)), 3 + (h - 6) * (1 - p.P / 100.0));
            var line = new Polyline { Stroke = Ui.R("Lilac"), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
            var fill = new Polygon { Fill = Ui.R("LilacBg") };
            var first = P(pts[0]); fill.Points.Add(new Point(first.X, h));
            foreach (var p in pts) { var q = P(p); line.Points.Add(q); fill.Points.Add(q); }
            fill.Points.Add(new Point(P(pts[pts.Count - 1]).X, h));
            cv.Children.Add(fill); cv.Children.Add(line);
            var lastPt = P(pts[pts.Count - 1]); var dot = new Ellipse { Width = 5, Height = 5, Fill = Ui.R("AccentBrush") }; Canvas.SetLeft(dot, lastPt.X - 2.5); Canvas.SetTop(dot, lastPt.Y - 2.5); cv.Children.Add(dot);
            return cv;
        }

        // ---------------- Settings
        FrameworkElement Section(string caps, params UIElement[] items)
        {
            var sp = new StackPanel(); sp.Children.Add(Ui.Caps(caps, 11)); var inner = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var i in items) inner.Children.Add(i); sp.Children.Add(inner);
            var card = Ui.Card(sp, 16); card.Margin = new Thickness(0, 0, 0, 14); return card;
        }
        FrameworkElement Chips(string label, int[] values, int current, Func<int, string> text, Action<int> pick)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            row.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center });
            foreach (var v in values)
            {
                var vv = v; var b = new ToggleButton { Content = text(v), Style = Ui.S("Chip"), IsChecked = v == current, Margin = new Thickness(0, 0, 6, 0) };
                b.Click += (s, e) => { pick(vv); BuildPage(); };
                row.Children.Add(b);
            }
            return row;
        }
        FrameworkElement SettingsPage()
        {
            var S = c.S; var sp = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            sp.Children.Add(new TextBlock { Text = "Settings", FontSize = 22, FontFamily = Ui.Head, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
            sp.Children.Add(Section("Alerts",
                Ui.Switch("Low battery alerts (once per discharge at each level)", S.AlertLow, v => { S.AlertLow = v; c.SaveSettings(); }),
                Chips("Low at", new[] { 10, 15, 20, 25, 30 }, S.Low1, v => v + " %", v => { S.Low1 = v; if (S.Low2 >= v) S.Low2 = Math.Max(1, v - 5); c.SaveSettings(); }),
                Chips("Very low at", new[] { 5, 10, 15 }.Where(x => x < S.Low1).ToArray(), S.Low2, v => v + " %", v => { S.Low2 = v; c.SaveSettings(); }),
                Ui.Switch("Charging started", S.AlertCharging, v => { S.AlertCharging = v; c.SaveSettings(); }),
                Ui.Switch("Fully charged", S.AlertFull, v => { S.AlertFull = v; c.SaveSettings(); }),
                Ui.Switch("Switched to wired / wireless", S.AlertWired, v => { S.AlertWired = v; c.SaveSettings(); }),
                Ui.Muted("Alerts are Windows notifications (from Hallow Hub's icon when Hub shows the Hallow icon). What they do while a game runs is up to you - see While I'm gaming.", 11.5, 0)));
            sp.Children.Add(GamingSection());
            sp.Children.Add(SoundSection());
            sp.Children.Add(PerDeviceSection());
            sp.Children.Add(Section("Reading",
                Chips("Check every", new[] { 60, 120, 300 }, S.PollSeconds, v => v / 60 + " min", v => { S.PollSeconds = v; c.SaveSettings(); }),
                Ui.Muted("On battery power (a laptop) it checks every 5 minutes at most. It also checks when gear is plugged in or out.", 11.5, 6),
                Ui.Switch("Ask the mouse during games too (every 2 min)", S.ReadMouseInGames, v => { S.ReadMouseInGames = v; c.SaveSettings(); }),
                Ui.Muted("Off (recommended): during a game Hallow Gear sends nothing to the mouse or receiver - it only listens to the battery reports the mouse sends by itself, so it can't add input latency. The headset's base station is a separate USB device and is read at most every 2 minutes.", 11.5, 0)));
            sp.Children.Add(BluetoothSection());
            var looks = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            looks.Children.Add(new TextBlock { Text = "Look", Width = 150, VerticalAlignment = VerticalAlignment.Center });
            foreach (var l in new[] { new[] { "hallow", "Follow Hallow" }, new[] { "light", "Light" }, new[] { "dark", "Dark" } })
            {
                var id = l[0]; var b = new ToggleButton { Content = l[1], Style = Ui.S("Chip"), IsChecked = S.Look == id, Margin = new Thickness(0, 0, 6, 0) };
                b.Click += (s, e) => { c.SetLook(id); if (!Snap.On) Dispatcher.BeginInvoke(new Action(() => { Show("settings"); })); };
                looks.Children.Add(b);
            }
            sp.Children.Add(Section("Hallow",
                Ui.Switch("Start Hallow Gear when I sign in to Windows (in the tray)", c.StartsWithWindows(), v => { c.SetStartWithWindows(v); c.WriteStatus(); }),
                Ui.Switch("Use the Hallow icon (Hallow Hub's tray menu lists Hallow Gear)", S.UseHallowIcon, v => { S.UseHallowIcon = v; c.SaveSettings(); c.CheckHost(); }),
                Ui.Switch("Use the Hallow accent colour", S.UseHallowAccent, v => { S.UseHallowAccent = v; c.SaveSettings(); c.ApplyAccent(); }),
                looks));
            var btns = new WrapPanel();
            btns.Children.Add(Ui.Btn("Check for updates", () => c.CheckForUpdate(true)));
            btns.Children.Add(Ui.Btn("Open data folder", () => { try { System.IO.Directory.CreateDirectory(GearSettings.Dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + GearSettings.Dir + "\""); } catch { } }));
            sp.Children.Add(btns);
            return sp;
        }

        // 1.3: a row of chips for a string setting
        FrameworkElement Pick(string label, string[] values, string current, Func<string, string> text, Action<string> pick, double labelWidth = 150, string tip = null)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
            if (label != null) row.Children.Add(new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            foreach (var v in values)
            {
                var vv = v; var b = new ToggleButton { Content = text(v), Style = Ui.S("Chip"), IsChecked = v == current, Margin = new Thickness(0, 2, 6, 2) };
                if (tip != null) b.ToolTip = tip;
                b.Click += (s, e) => { pick(vv); BuildPage(); };
                row.Children.Add(b);
            }
            return row;
        }
        static readonly string[] Corners = { "top-right", "top-left", "bottom-right", "bottom-left" };
        static string CornerLabel(string c) { switch (c) { case "top-left": return "Top left"; case "bottom-right": return "Bottom right"; case "bottom-left": return "Bottom left"; } return "Top right"; }
        FrameworkElement Sub(string text) { var t = Ui.Caps(text, 10, Ui.R("Muted")); t.Margin = new Thickness(0, 14, 0, 4); return t; }

        // 1.3: Settings > While I'm gaming - per alert type, the pop-up's monitor / corner / time, the single-monitor fallback
        FrameworkElement GamingSection()
        {
            var S = c.S; var items = new List<UIElement>();
            items.Add(Ui.Muted("What each alert does while a game runs. Hold until after: nothing during the game, then at most 3 when it closes (a low battery also shows as a quiet line in Hallow Overlay). Overlay line only: just that line. Pop-up, other screen: Gear's own small pop-up on a monitor without the game - it never takes focus from the game. Windows notification: shown anyway.", 11.5, 6));
            Func<string, Func<string>, Action<string>, FrameworkElement> typeRow = (t, get, set) => Pick(GameModes.TypeLabel(t), GameModes.All, get(), GameModes.Label, v => { set(v); c.SaveSettings(); });
            items.Add(typeRow("low", () => S.GameLow, v => S.GameLow = v));
            items.Add(typeRow("critical", () => S.GameCritical, v => S.GameCritical = v));
            items.Add(typeRow("charging", () => S.GameCharging, v => S.GameCharging = v));
            items.Add(typeRow("full", () => S.GameFull, v => S.GameFull = v));
            items.Add(typeRow("wired", () => S.GameWired, v => S.GameWired = v));
            items.Add(Sub("The pop-up"));
            var mons = c.Layout();
            var screens = new List<string> { "auto" }; screens.AddRange(mons.Select(m => m.Device)); screens.Add("same");
            if (S.PopupMonitor != "auto" && S.PopupMonitor != "same" && !screens.Contains(S.PopupMonitor)) screens.Insert(screens.Count - 1, S.PopupMonitor);   // chosen, not connected now
            Func<string, string> screenLabel = v =>
            {
                if (v == "auto") return "The one without the game"; if (v == "same") return "Same as the game";
                var m = mons.FirstOrDefault(x => x.Device == v); return m != null ? "Screen " + m.Number + (m.Primary ? " (main)" : "") : "Screen " + Mon.NumberOf(v) + " (not connected)";
            };
            items.Add(Pick("Screen", screens.ToArray(), S.PopupMonitor, screenLabel, v => { S.PopupMonitor = v; c.SaveSettings(); }));
            var idRow = new WrapPanel { Margin = new Thickness(150, 2, 0, 4) };
            var ident = Ui.Btn("Identify", () => c.Identify(), false, "Shows each screen's number on it for a moment"); idRow.Children.Add(ident);
            var tryIt = Ui.Btn("Try a pop-up", () => { var where = c.TryPopup(); Say(where == null ? "No screen for the pop-up right now." : "A sample pop-up is on " + where + "."); }, false, "Shows a sample pop-up where it would go now");
            idRow.Children.Add(tryIt);
            idRow.Children.Add(new TextBlock { Text = mons.Count + " screen" + (mons.Count == 1 ? "" : "s") + ": " + string.Join(",  ", mons.Select(m => m.Label)), Foreground = Ui.R("Muted"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
            items.Add(idRow);
            items.Add(Pick("Corner", Corners, S.PopupCorner, CornerLabel, v => { S.PopupCorner = v; c.SaveSettings(); }));
            items.Add(Pick("Hide after", new[] { "5", "8", "15", "30" }, S.PopupSeconds.ToString(), v => v + " s", v => { S.PopupSeconds = int.Parse(v); c.SaveSettings(); }));
            items.Add(Pick("With one screen", GameModes.Fallbacks, S.PopupFallback, GameModes.FallbackLabel, v => { S.PopupFallback = v; c.SaveSettings(); }));
            items.Add(Ui.Muted("Used when there's only one screen, the chosen screen isn't connected, or every other screen shows the game. The pop-up never goes on the game's screen unless you pick \"Same as the game\" or \"Pop-up on that screen\". Up to 3 stack in the corner.", 11.5, 0));
            return Section("While I'm gaming", items.ToArray());
        }
        FrameworkElement SoundSection()
        {
            var S = c.S;
            Func<string, int, Action> play = (snd, vol) => () => GearSound.Play(GearSound.For(snd, vol));
            var outside = Pick("Outside games", GearSound.Normal, S.SoundNormal, v => GearSound.Label(v, false), v => { S.SoundNormal = v; c.SaveSettings(); });
            var during = Pick("During games", GearSound.Game, S.SoundGame, v => GearSound.Label(v, true), v => { S.SoundGame = v; c.SaveSettings(); });
            var vol1 = Pick("Hallow sound volume", new[] { "40", "70", "100" }, S.SoundVolume.ToString(), v => v + " %", v => { S.SoundVolume = int.Parse(v); c.SaveSettings(); });
            ((Panel)vol1).Children.Add(Ui.Btn("Play", play(GearSound.Hallow, S.SoundVolume), false, "Plays the Hallow sound at this volume"));
            var vol2 = Pick("In-game volume", new[] { "10", "20", "30", "50" }, S.GameVolume.ToString(), v => v + " %", v => { S.GameVolume = int.Parse(v); c.SaveSettings(); });
            ((Panel)vol2).Children.Add(Ui.Btn("Play", play(S.SoundGame == GearSound.Silent ? GearSound.Hallow : S.SoundGame, S.GameVolume), false, "Plays the in-game sound (the pop-up's) at this volume"));
            return Section("Sound", outside, vol1, during, vol2,
                Ui.Muted("Windows notifications use Windows' own sound or none. The Hallow sound and the pop-up's sound are Gear's own, played on your default audio device at the volume above. Hallow Gear never changes the game's or Windows' volume - its sound is made quieter before it plays.", 11.5, 0));
        }
        // 1.3: per device - alerts on / off, and what its alerts do during a game (all of them, or per alert type)
        readonly HashSet<string> openDevices = new HashSet<string>();
        public void OpenDeviceRow(string key) { openDevices.Add(key); }
        FrameworkElement PerDeviceSection()
        {
            var S = c.S; var items = new List<UIElement>();
            items.Add(Ui.Muted("Turn a device's alerts off, or give it its own game behaviour (for example the mouse's very low battery as a pop-up, the headset's alerts on the Overlay line only).", 11.5, 6));
            var devs = c.Devices.Where(d => d.HasBattery && !S.BtHidden.Contains(d.Key)).ToList();
            if (devs.Count == 0) items.Add(Ui.Muted("No gear with a battery yet.", 11.5, 0));
            var modes = new[] { "default" }.Concat(GameModes.All).ToArray();
            foreach (var d in devs)
            {
                var key = d.Key; bool on = c.AlertsOn(d), open = openDevices.Contains(key); string all; S.DeviceGame.TryGetValue(key, out all);
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 2) };
                var btns = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(btns, Dock.Right);
                var al = new ToggleButton { Content = on ? "Alerts on" : "Alerts off", Style = Ui.S("Chip"), IsChecked = on, Margin = new Thickness(0, 0, 6, 0), ToolTip = "This device's battery alerts" };
                al.Click += (s, e) => { c.SetBtAlerts(key, !on); BuildPage(); };
                var more = new ToggleButton { Content = open ? "Per alert \u25B4" : "Per alert \u25BE", Style = Ui.S("Chip"), IsChecked = open || S.DeviceGame.Keys.Any(k => k.StartsWith(key + "|")), IsEnabled = on, ToolTip = "A game behaviour for each of its alerts" };
                more.Click += (s, e) => { if (open) openDevices.Remove(key); else openDevices.Add(key); BuildPage(); };
                btns.Children.Add(al); btns.Children.Add(more); row.Children.Add(btns);
                var label = Icons.WithText(KindIcon(d), d.Name, 14, Ui.R(on ? "AccentBrush" : "Faint")); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
                items.Add(row);
                if (!on) continue;
                var allRow = Pick("During games", modes, GameModes.Valid(all) ? all : "default", GameModes.Label, v => c.SetDeviceGame(key, null, v), 150);
                allRow.Margin = new Thickness(22, 2, 0, 2); items.Add(allRow);
                if (!open) continue;
                foreach (var t in GameModes.Types)
                {
                    var tt = t; string m; S.DeviceGame.TryGetValue(key + "|" + t, out m);
                    var r = Pick(GameModes.TypeLabel(t), modes, GameModes.Valid(m) ? m : "default", GameModes.Label, v => c.SetDeviceGame(key, tt, v), 150);
                    r.Margin = new Thickness(44, 0, 0, 0); items.Add(r);
                }
            }
            return Section("Per device", items.ToArray());
        }

        // 1.1: the Bluetooth switch, and per device: shown / hidden, alerts on / off
        FrameworkElement BluetoothSection()
        {
            var S = c.S; var items = new List<UIElement>();
            items.Add(Ui.Switch("Bluetooth devices (the battery Windows knows for earbuds, headsets, controllers...)", S.Bluetooth, v => { c.SetBluetooth(v); BuildPage(); }));
            items.Add(Ui.Muted("Read from what Windows already knows, at most every 5 minutes and never during a game. Nothing is paired, connected or changed. Charging isn't shown: Bluetooth batteries don't report it.", 11.5, 6));
            var bt = c.Devices.Where(d => d.Bluetooth).ToList();
            if (S.Bluetooth && bt.Count == 0) items.Add(Ui.Muted("No paired Bluetooth device has reported a battery to Windows yet.", 11.5, 0));
            foreach (var d in bt)
            {
                var key = d.Key; bool hidden = S.BtHidden.Contains(key), alertsOn = !S.BtNoAlerts.Contains(key);
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var btns = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(btns, Dock.Right);
                var show = new ToggleButton { Content = hidden ? "Hidden" : "Shown", Style = Ui.S("Chip"), IsChecked = !hidden, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Show this device's card (and its battery in Hallow Overlay)" };
                show.Click += (s, e) => { c.SetBtHidden(key, !hidden); BuildPage(); };
                var al = new ToggleButton { Content = alertsOn ? "Alerts on" : "Alerts off", Style = Ui.S("Chip"), IsChecked = alertsOn && !hidden, IsEnabled = !hidden, ToolTip = "Low battery alerts for this device" };
                al.Click += (s, e) => { c.SetBtAlerts(key, !alertsOn); BuildPage(); };
                btns.Children.Add(show); btns.Children.Add(al); row.Children.Add(btns);
                var label = Icons.WithText(KindIcon(d), d.Name + "  \u00B7  " + (d.Battery.HasValue ? d.Battery + " %" : "\u2013 %") + (d.Connected ? "" : " (not connected)"), 14, Ui.R(hidden ? "Faint" : "AccentBrush"));
                label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
                items.Add(row);
            }
            return Section("Bluetooth devices", items.ToArray());
        }

        // ---------------- What it can read (the honest list)
        FrameworkElement AboutPage()
        {
            var sp = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(new TextBlock { Text = "What Hallow Gear can read", FontSize = 22, FontFamily = Ui.Head, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            sp.Children.Add(Ui.Muted("It only reads. Every message it can send to a device is on a fixed read-only list in its code - nothing that changes DPI, lighting, profiles, pairing, sound or any other setting can even be built. It runs next to Logitech G HUB: devices are opened shared and asked a few small questions a minute.", 12.5, 14));
            Func<string, string, string[], FrameworkElement> Item = (icon, head, lines) =>
            {
                var s = new StackPanel(); s.Children.Add(Icons.WithText(icon, head, 16, Ui.R("AccentBrush")));
                ((TextBlock)((StackPanel)s.Children[0]).Children[1]).FontWeight = FontWeights.SemiBold; ((TextBlock)((StackPanel)s.Children[0]).Children[1]).FontSize = 14;
                foreach (var l in lines) s.Children.Add(new TextBlock { Text = "\u2022 " + l, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 5, 0, 0), Foreground = Ui.R("TextSoft") });
                var card = Ui.Card(s, 16); card.Margin = new Thickness(0, 0, 0, 12); return card;
            };
            sp.Children.Add(Item("computer-mouse", "LIGHTSPEED mouse (Logitech HID++ 2.0)", new[] {
                "Its name, and its battery from the mouse itself: Unified Battery (0x1004) when it has it - percent, charging / full / discharging, external power - or Battery Status (0x1000), or Battery Voltage (0x1001, then the percent is an estimate).",
                "Asleep or switched off: the receiver says the mouse isn't reachable (or that its link went down).",
                "Wired vs wireless: when it's on its USB cable it shows up as its own Logitech USB device too - then it's \"wired (charging)\".",
                "During a game it's never asked anything (unless you switch that on) - Hallow Gear only listens to the battery reports the mouse sends by itself." }));
            sp.Children.Add(Item("headphones", "ASTRO A50 (Gen 5) headset", new[] {
                "Battery percent and docked / charging, from the base station (USB 046d:0b1c), using the one battery question the open-source HeadsetControl project documents for the A50 Gen 5. That protocol is reverse-engineered by that project, not published by Logitech.",
                "Not known: whether the headset itself is switched on (the base station answers either way), and the A50 X (a different protocol) isn't supported.",
                "Not read: chat-mix, volume, EQ, noise gate, sidetone - and nothing is ever changed." }));
            sp.Children.Add(Item("keyboard", "Wired keyboard (G713)", new[] { "Present and wired. It has no battery, so there's nothing more to read." }));
            sp.Children.Add(Item("computer-mouse", "Other Logitech HID++ 2.0 devices", new[] {
                "Any device on any LIGHTSPEED, Unifying or Bolt receiver slot (1-6) - a second mouse, a wireless keyboard, a headset - gets its own card, with the same reads (name, type, battery).",
                "A Logitech device that talks HID++ on its own USB cable or straight over Bluetooth is read the same way.",
                "Expected to work, but only the PRO X 2, A50 and G713 have been tried on real hardware." }));
            sp.Children.Add(Item("tower-broadcast", "Bluetooth devices", new[] {
                "Earbuds, headsets, controllers, keyboards and mice paired with Windows: the battery Windows already knows (the one Settings > Bluetooth shows), or the standard Battery Service of a Bluetooth LE device.",
                "Only devices that are already paired. A live read only while a device is connected; otherwise Windows' last value - so nothing is ever connected or paired, and no prompt can appear.",
                "Battery notifications a device sends by itself are heard; Hallow Gear never writes anything to the device (not even the notification switch).",
                "Charging isn't shown: Bluetooth batteries don't report it. Each device can be hidden or have its alerts switched off in Settings." }));
            sp.Children.Add(Item("circle-question", "Not supported", new[] {
                "Other brands' 2.4 GHz dongles (Razer, SteelSeries, Corsair...) - each has its own protocol.",
                "ASTRO A50 Gen 4 and A50 X - different USB ids and protocols.",
                "Firmware updates are left to G HUB on purpose: Hallow Gear never writes to a device." }));
            sp.Children.Add(Item("shield-halved", "Never", new[] {
                "No DPI, lighting, onboard profiles, pairing, host switching, receiver settings, sound settings or firmware - the code refuses them before a byte is built.",
                "Nothing is sent anywhere: history stays in %APPDATA%\\HallowGear (24 hours). Only the update check talks to GitHub.",
                "Requests sent so far this session: " + SentLog.Total + "." }));
            return sp;
        }
    }
}

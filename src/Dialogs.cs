// Hallow Gear - the small themed dialogs: a confirm (updates) and the crash notice. --snap renders them without showing them.
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HallowGear
{
    public static class Dialogs
    {
        public static Window ConfirmWindow(string title, string text, string okLabel, Action<bool> done)
        {
            var w = new Window(); Ui.Dress(w, "Hallow Gear", 460); w.ResizeMode = ResizeMode.NoResize; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) }; w.Content = sp;
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); sp.Children.Add(Ui.Muted(text, 12.5));
            var row = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }; sp.Children.Add(row);
            bool answer = false;
            row.Children.Add(Ui.Ghost("Cancel", () => w.Close()));
            var ok = Ui.Btn(okLabel, () => { answer = true; w.Close(); }, true); ok.Margin = new Thickness(0); ok.MinWidth = 90; row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(answer); };
            return w;
        }
        public static bool Confirm(Window owner, string title, string text, string okLabel)
        {
            bool r = false; var w = ConfirmWindow(title, text, okLabel, a => r = a);
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            w.ShowDialog(); return r;
        }
    }

    public static class CrashNotice
    {
        public static Window Make(string file)
        {
            var w = new Window(); Ui.Dress(w, "Hallow Gear", 440);
            w.ResizeMode = ResizeMode.NoResize; w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Topmost = true;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(Ui.T("Hallow Gear closed unexpectedly last time", 15, true, null, 6));
            DateTime when = DateTime.Now; try { if (File.Exists(file)) when = File.GetLastWriteTime(file); } catch { }
            sp.Children.Add(Ui.Muted("On " + when.ToString("d MMM yyyy, HH:mm") + ". A report was saved on this PC - nothing was sent anywhere.", 12.5, 14));
            var row = new WrapPanel();
            row.Children.Add(Ui.Ghost("Show details", () => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\""); } catch { } }));
            row.Children.Add(Ui.Ghost("Open folder", () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } }));
            var ok = Ui.Btn("OK", () => w.Close(), true); ok.MinWidth = 70; row.Children.Add(ok);
            sp.Children.Add(row); w.Content = sp;
            return w;
        }
    }
}

// Linux: keep Mono's built-in system colors for dialogs and standard controls.
//
// Mono's WinForms imports the desktop's color scheme once (X11DesktopColors, through GTK 2 or
// KDE's settings). With a dark desktop theme this left message box and dialog buttons dark
// with unreadable labels, while the main window sets its own colors. Windows' classic system
// colors are what the toolkit was designed with, so put Mono's built-in colors back after the
// import. Set JCTOOL_DESKTOP_COLORS=1 to keep the desktop's colors.

using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace CppWinFormJoy
{
    static class SystemColorsFix
    {
        static readonly KnownColor[] system_colors = {
            KnownColor.ActiveBorder, KnownColor.ActiveCaption, KnownColor.ActiveCaptionText, KnownColor.AppWorkspace,
            KnownColor.Control, KnownColor.ControlDark, KnownColor.ControlDarkDark, KnownColor.ControlLight,
            KnownColor.ControlLightLight, KnownColor.ControlText, KnownColor.Desktop, KnownColor.GrayText,
            KnownColor.Highlight, KnownColor.HighlightText, KnownColor.HotTrack, KnownColor.InactiveBorder,
            KnownColor.InactiveCaption, KnownColor.InactiveCaptionText, KnownColor.Info, KnownColor.InfoText,
            KnownColor.Menu, KnownColor.MenuText, KnownColor.ScrollBar, KnownColor.Window, KnownColor.WindowFrame,
            KnownColor.WindowText, KnownColor.ButtonFace, KnownColor.ButtonHighlight, KnownColor.ButtonShadow,
            KnownColor.GradientActiveCaption, KnownColor.GradientInactiveCaption, KnownColor.MenuBar,
            KnownColor.MenuHighlight };

        static bool diag;

        static void Diag(string step)
        {
            if (!diag)
                return;
            Console.Write("  {0,-22}", step + ":");
            foreach (KnownColor k in new[] { KnownColor.Control, KnownColor.ControlText, KnownColor.ButtonFace, KnownColor.Window, KnownColor.WindowText })
                Console.Write(" {0}={1:X6}", k, Color.FromKnownColor(k).ToArgb() & 0xFFFFFF);
            Console.WriteLine();
        }

        // --diag-colors: what happens to the system colors on this system
        public static void Diagnose()
        {
            diag = true;
            Type mono = Type.GetType("Mono.Runtime");
            string version = mono == null ? "?" : (string)mono.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
            Console.WriteLine("Mono: " + version);
            Console.WriteLine("System.Drawing: " + typeof(Color).Assembly.Location);
            foreach (string v in new[] { "DESKTOP_SESSION", "KDE_FULL_SESSION", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "GTK2_RC_FILES",
                                         "GTK_THEME", "MONO_THEME", "MONO_VISUAL_STYLES", "JCTOOL_DESKTOP_COLORS", "DISPLAY", "WAYLAND_DISPLAY" })
                Console.WriteLine("  {0}={1}", v, Environment.GetEnvironmentVariable(v));
            UseBuiltInColors();
            Application.EnableVisualStyles();
            Diag("after visual styles");
            object theme = typeof(Form).Assembly.GetType("System.Windows.Forms.ThemeEngine")
                ?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            Console.WriteLine("Theme: " + (theme == null ? "?" : theme.GetType().FullName));
            using (Button b = new Button())
                Console.WriteLine("Button: BackColor={0:X6} ForeColor={1:X6} FlatStyle={2} UseVisualStyleBackColor={3}",
                    b.BackColor.ToArgb() & 0xFFFFFF, b.ForeColor.ToArgb() & 0xFFFFFF, b.FlatStyle, b.UseVisualStyleBackColor);
        }

        public static void UseBuiltInColors()
        {
            Diag("at start");
            if (Environment.GetEnvironmentVariable("JCTOOL_DESKTOP_COLORS") == "1")
                return;
            // Without a display, WinForms can't start (e.g. --selftest's non-window checks)
            if (Environment.GetEnvironmentVariable("DISPLAY") == null)
                return;
            try {
                // Mono's own colors, before anything is imported
                int[] builtin = new int[system_colors.Length];
                for (int i = 0; i < system_colors.Length; i++)
                    builtin[i] = Color.FromKnownColor(system_colors[i]).ToArgb();

                // Start WinForms' X11 driver and theme, which import the desktop colors, now
                Assembly swf = typeof(Form).Assembly;
                foreach (string name in new[] { "System.Windows.Forms.XplatUI", "System.Windows.Forms.ThemeEngine", "System.Windows.Forms.X11DesktopColors" }) {
                    Type t = swf.GetType(name);
                    if (t != null)
                        RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                }
                Diag("after desktop import");

                // Put Mono's colors back, the same way the import set them
                MethodInfo update = typeof(Color).Assembly.GetType("System.Drawing.KnownColors")
                    ?.GetMethod("Update", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (diag)
                    Console.WriteLine("  KnownColors.Update: " + (update == null ? "not found" : "found"));
                if (update == null)
                    return;
                for (int i = 0; i < system_colors.Length; i++)
                    update.Invoke(null, new object[] { (int)system_colors[i], builtin[i] });
                Diag("after reset");
            }
            catch (Exception e) {
                if (diag)
                    Console.WriteLine("  Failed: " + e);
                // Keep whatever colors Mono chose
            }
        }
    }
}

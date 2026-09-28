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

        public static void UseBuiltInColors()
        {
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

                // Put Mono's colors back, the same way the import set them
                MethodInfo update = typeof(Color).Assembly.GetType("System.Drawing.KnownColors")
                    ?.GetMethod("Update", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (update == null)
                    return;
                for (int i = 0; i < system_colors.Length; i++)
                    update.Invoke(null, new object[] { (int)system_colors[i], builtin[i] });
            }
            catch (Exception) {
                // Keep whatever colors Mono chose
            }
        }
    }
}

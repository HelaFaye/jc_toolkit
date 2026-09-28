// Linux: readable dialogs (message boxes, file dialogs and other standard controls).
//
// Mono draws dialog buttons at a fixed 23px height, and draws a label only if its whole line
// fits. Its default font, "Microsoft Sans Serif", falls back to the desktop's sans font; with
// Noto Sans (the default on Arch/KDE) the line is too tall, so every dialog button was blank.
// Use a font with compact line spacing (Liberation Sans, metric-compatible with Arial) as
// Mono's default font instead.
//
// Mono also imports the desktop's color scheme (X11DesktopColors, through GTK 2 or KDE's
// settings), so dialogs follow a dark theme. That is kept; JCTOOL_CLASSIC_COLORS=1 puts
// Mono's built-in light colors back instead.

using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace CppWinFormJoy
{
    static class DialogFix
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
                                         "GTK_THEME", "MONO_THEME", "MONO_VISUAL_STYLES", "JCTOOL_CLASSIC_COLORS", "DISPLAY", "WAYLAND_DISPLAY" })
                Console.WriteLine("  {0}={1}", v, Environment.GetEnvironmentVariable(v));
            Diag("at start");
            Apply();
            Application.EnableVisualStyles();
            Diag("after visual styles");
            object theme = typeof(Form).Assembly.GetType("System.Windows.Forms.ThemeEngine")
                ?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            Console.WriteLine("Theme: " + (theme == null ? "?" : theme.GetType().FullName));
            using (Button b = new Button()) {
                Console.WriteLine("Button: BackColor={0:X6} ForeColor={1:X6} Font={2} {3}pt",
                    b.BackColor.ToArgb() & 0xFFFFFF, b.ForeColor.ToArgb() & 0xFFFFFF, b.Font.Name, b.Font.SizeInPoints);
                using (Graphics g = b.CreateGraphics())
                    Console.WriteLine("Button label height: {0:F1}px (Mono's dialog buttons fit about 15px)", b.Font.GetHeight(g));
            }
        }

        static readonly string[] CompactSans = { "Liberation Sans", "Arimo", "DejaVu Sans" };

        public static void Apply()
        {
            // Without a display, WinForms can't start (e.g. --selftest's non-window checks)
            if (Environment.GetEnvironmentVariable("DISPLAY") == null)
                return;
            UseCompactDefaultFont();
            if (Environment.GetEnvironmentVariable("JCTOOL_CLASSIC_COLORS") == "1")
                UseBuiltInColors();
        }

        static object CurrentTheme()
        {
            return typeof(Form).Assembly.GetType("System.Windows.Forms.ThemeEngine")
                ?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        }

        static void UseCompactDefaultFont()
        {
            try {
                object theme = CurrentTheme();
                FieldInfo field = typeof(Form).Assembly.GetType("System.Windows.Forms.Theme")
                    ?.GetField("default_font", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                // Mono creates the default font on first use
                theme?.GetType().GetProperty("DefaultFont", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(theme);
                Font current = field?.GetValue(theme) as Font;
                if (current == null) {
                    if (diag)
                        Console.WriteLine("  Default font: not found");
                    return;
                }
                string requested = current.OriginalFontName ?? current.FontFamily.Name;
                string family = null;
                if (!Fonts.IsInstalledFamily(requested))
                    foreach (string c in CompactSans)
                        if (Fonts.IsInstalledFamily(c)) {
                            family = c;
                            break;
                        }
                if (family != null && family != current.FontFamily.Name)
                    field.SetValue(theme, new Font(family, current.Size, current.Style, current.Unit, current.GdiCharSet));
                if (diag)
                    Console.WriteLine("  Default font: {0} ({1} requested) -> {2}", current.FontFamily.Name, requested,
                        family == null ? "unchanged" : family);
            }
            catch (Exception e) {
                if (diag)
                    Console.WriteLine("  Default font failed: " + e.Message);
            }
        }

        static void UseBuiltInColors()
        {
            Diag("at start");
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

                // Put Mono's colors back through the theme's color properties (ColorControl, ...),
                // which the import uses, and System.Drawing's table (older Mono versions)
                object theme = CurrentTheme();
                MethodInfo update = typeof(Color).Assembly.GetType("System.Drawing.KnownColors")
                    ?.GetMethod("Update", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                int theme_set = 0;
                for (int i = 0; i < system_colors.Length; i++) {
                    Color c = Color.FromArgb(builtin[i]);
                    PropertyInfo p = theme?.GetType().GetProperty("Color" + system_colors[i],
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(Color)) {
                        p.SetValue(theme, c);
                        theme_set++;
                    }
                    update?.Invoke(null, new object[] { (int)system_colors[i], builtin[i] });
                }
                if (diag)
                    Console.WriteLine("  Theme colors set: {0}, KnownColors.Update: {1}", theme_set, update == null ? "not found" : "found");
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

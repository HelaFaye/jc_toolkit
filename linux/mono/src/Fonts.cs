// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// The form was laid out with Windows fonts (Segoe UI, Lucida Console). When they are not
// installed, Mono falls back to DejaVu Sans, which is much wider and clips text in the
// fixed-size controls. Swap in the closest installed font at the same size instead.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CppWinFormJoy
{
    public static class Fonts
    {
        static HashSet<string> installed;
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();

        static readonly string[] SansSubstitutes = { "Liberation Sans", "Arimo", "Noto Sans", "DejaVu Sans Condensed", "DejaVu Sans" };
        static readonly string[] MonoSubstitutes = { "DejaVu Sans Mono", "Liberation Mono", "Noto Mono" };

        static bool IsInstalled(string family)
        {
            if (installed == null) {
                installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try {
                    using (var fonts = new InstalledFontCollection())
                        foreach (FontFamily f in fonts.Families)
                            installed.Add(f.Name);
                }
                catch (Exception) {
                }
            }
            return installed.Contains(family);
        }

        public static string MapFamily(string family)
        {
            string mapped;
            if (cache.TryGetValue(family, out mapped))
                return mapped;
            mapped = family;
            if (!IsInstalled(family)) {
                string[] candidates = family.StartsWith("Lucida Console", StringComparison.OrdinalIgnoreCase)
                    ? MonoSubstitutes : family.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase)
                    ? SansSubstitutes : new string[0];
                foreach (string c in candidates) {
                    if (IsInstalled(c)) {
                        mapped = c;
                        break;
                    }
                }
            }
            cache[family] = mapped;
            return mapped;
        }

        public static Font Map(Font font)
        {
            if (font == null)
                return null;
            // When a family is missing, Mono substitutes it and keeps the requested name here.
            string family = MapFamily(font.OriginalFontName ?? font.FontFamily.Name);
            if (family == font.FontFamily.Name)
                return font;
            // Mono draws monospace text a little wider than GDI+ does with Lucida Console,
            // which pushes the calibration readouts past their boxes. Shrink it slightly.
            float size = Array.IndexOf(MonoSubstitutes, family) >= 0 ? font.Size * 0.92f : font.Size;
            return new Font(family, size, font.Style, font.Unit, font.GdiCharSet);
        }

        public static Font Create(string family, float size, FontStyle style, GraphicsUnit unit, byte charset)
        {
            return new Font(MapFamily(family), size, style, unit, charset);
        }

        // Applies Map() to a control tree, including menu and tool strip items.
        public static void Apply(Control root)
        {
            root.Font = Map(root.Font);
            foreach (Control c in root.Controls)
                Apply(c);
            var strip = root as ToolStrip;
            if (strip != null)
                foreach (ToolStripItem item in strip.Items)
                    ApplyItem(item);
        }

        // Mono's multiline TextBox keeps the line wrapping it computed while its panel was
        // detached from the form, which breaks lines at about half the box width. The app
        // removes and re-adds its panels all the time, so re-flow them whenever that happens.
        public static void RewrapTextBoxes(Control root)
        {
            var tb = root as TextBoxBase;
            if (tb != null && tb.Multiline && tb.WordWrap) {
                tb.WordWrap = false;
                tb.WordWrap = true;
            }
            foreach (Control c in root.Controls)
                RewrapTextBoxes(c);
        }

        static void ApplyItem(ToolStripItem item)
        {
            item.Font = Map(item.Font);
            var dropdown = item as ToolStripDropDownItem;
            if (dropdown != null)
                foreach (ToolStripItem child in dropdown.DropDownItems)
                    ApplyItem(child);
        }
    }
}

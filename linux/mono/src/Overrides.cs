// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Port of jctool/Overrides.h: dark theme colors for the menu and tool strips.

using System.Drawing;
using System.Windows.Forms;

namespace Overrides
{
    public class TestColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(55, 55, 55); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(85, 85, 85); } }
        public override Color MenuBorder { get { return Color.FromArgb(55, 55, 55); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(70, 70, 70); } }
        public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(85, 85, 85); } }
        public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(85, 85, 85); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(55, 55, 55); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(55, 55, 55); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(85, 85, 85); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(85, 85, 85); } }
    }

    public class OverrideTSSR : ToolStripSystemRenderer
    {
        public OverrideTSSR() {}

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Do nothing
        }
    }
}

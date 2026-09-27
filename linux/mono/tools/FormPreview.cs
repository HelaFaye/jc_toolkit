// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Shows the converted FormJoy designer layout on Mono, without any controller
// logic. With a file argument it saves a screenshot after 3 seconds and exits.
//   make preview                         (window)
//   xvfb-run -a -s "-screen 0 2000x1200x24" mono build/formpreview.exe out.png

using System;
using System.Drawing;
using System.Windows.Forms;
namespace CppWinFormJoy {
    partial class FormJoy : Form {
        public FormJoy() { InitializeComponent(); }
    }
    static class Program {
        [STAThread] static void Main(string[] a) {
            var f = new FormJoy();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            if (a.Length == 0) {
                Application.Run(f);
                return;
            }
            var t = new Timer { Interval = 3000 };
            t.Tick += (s, e) => {
                t.Stop();
                var r = f.Bounds;
                using (var bmp = new Bitmap(r.Width, r.Height)) {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(a[0]);
                }
                Console.WriteLine("captured {0}", r);
                Application.Exit();
            };
            t.Start();
            Application.Run(f);
        }
    }
}

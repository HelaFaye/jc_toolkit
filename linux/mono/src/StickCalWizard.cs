// Linux addition: guided stick calibration, like the Switch's "Calibrate Control Sticks".
// Reads the raw stick position from full input reports (mode 0x30) and measures the center
// (stick at rest) and the range (stick rotated along its edge). The result only fills the
// calibration editor's fields; nothing is written until the user clicks Write Cal there.
using System;
using System.Drawing;
using System.Windows.Forms;
using static CppWinFormJoy.Jc;

using u8 = System.Byte;

namespace CppWinFormJoy {

public unsafe class StickCalWizard : Form
{
    const int Sectors = 24;          // Directions that must be reached while rotating
    const int CenterSamples = 30;    // ~0.5s of reports at rest
    const int CenterMaxSpread = 0x60;
    const int MinHalfRange = 0x300;  // Less than this from the center to an edge: not a real rotation

    readonly bool left;              // Left stick (Joy-Con (L) / Pro left) or right stick
    readonly Label lbl_step, lbl_values;
    readonly PictureBox view;
    readonly Button btn_next, btn_cancel;
    readonly Timer poll;

    int step;                        // 0: center, 1: rotate
    int x = -1, y = -1;              // Latest raw position
    readonly int[] recent_x = new int[CenterSamples], recent_y = new int[CenterSamples];
    int recent_count, recent_pos;
    int center_x, center_y, min_x, max_x, min_y, max_y;
    readonly bool[] sector_done = new bool[Sectors];
    readonly System.Collections.Generic.List<Point> trace = new System.Collections.Generic.List<Point>();
    int reports;

    // min, center, max for X then Y (raw 12-bit values), set when the wizard finishes
    public int[] Result { get; private set; }

    public StickCalWizard(bool left_stick, Form owner)
    {
        left = left_stick;
        Owner = owner;
        Text = (left ? "Left" : "Right") + " stick calibration";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(70, 70, 70);
        ForeColor = Color.FromArgb(251, 251, 251);
        Font = new Font("Segoe UI", 9.75F);
        ClientSize = new Size(380, 300);

        lbl_step = new Label { Location = new Point(12, 10), Size = new Size(356, 60) };
        view = new PictureBox { Location = new Point(12, 76), Size = new Size(170, 170), BackColor = Color.FromArgb(55, 55, 55) };
        view.Paint += draw_view;
        lbl_values = new Label { Location = new Point(194, 76), Size = new Size(174, 170), Font = new Font("Segoe UI", 9F) };
        btn_next = new_button("Next", new Point(174, 258));
        btn_cancel = new_button("Cancel", new Point(278, 258));
        btn_next.Click += (s, e) => Next();
        btn_cancel.Click += (s, e) => Close();
        Controls.AddRange(new Control[] { lbl_step, view, lbl_values, btn_next, btn_cancel });
        AcceptButton = btn_next;
        CancelButton = btn_cancel;

        poll = new Timer { Interval = 15 };
        poll.Tick += (s, e) => read_reports();
        Load += (s, e) => { set_report_mode(0x30); show_step(); poll.Start(); };
        FormClosed += (s, e) => { poll.Stop(); set_report_mode(0x3F); };
    }

    Button new_button(string text, Point at)
    {
        var b = new Button { Text = text, Location = at, Size = new Size(90, 30), FlatStyle = FlatStyle.Flat,
                             BackColor = Color.FromArgb(85, 85, 85), ForeColor = Color.FromArgb(9, 255, 206),
                             Font = new Font("Segoe UI Semibold", 10), UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderColor = b.BackColor;
        return b;
    }

    static void set_report_mode(u8 mode)
    {
        u8* buf = stackalloc u8[49];
        memset(buf, 0, 49);
        var hdr = (brcm_hdr *)buf;
        var pkt = (brcm_cmd_01 *)(hdr + 1);
        hdr->cmd = 0x01;
        hdr->timer = (u8)(timming_byte & 0xF);
        timming_byte++;
        pkt->subcmd = 0x03;
        pkt->subcmd_arg.arg1 = mode;
        hid_write(handle, buf, 49);
        hid_read_timeout(handle, buf, 0, 64);
    }

    // Everything queued since the last tick (window stalls on XWayland must not lose the trace).
    void read_reports()
    {
        u8* buf = stackalloc u8[49];
        for (int n = 0; n < 64; n++) {
            if (hid_read_timeout(handle, buf, 49, 0) <= 12)
                break;
            if (buf[0] != 0x30 && buf[0] != 0x21)
                continue;
            u8* s = buf + (left ? 6 : 9);
            add_sample(s[0] | ((s[1] & 0xF) << 8), (s[1] >> 4) | (s[2] << 4));
        }
        update_values();
        view.Invalidate();
    }

    // Also used by the self-test.
    internal void add_sample(int sx, int sy)
    {
        x = sx; y = sy;
        reports++;
        if (step == 0) {
            recent_x[recent_pos] = sx;
            recent_y[recent_pos] = sy;
            recent_pos = (recent_pos + 1) % CenterSamples;
            recent_count = Math.Min(recent_count + 1, CenterSamples);
        }
        else {
            min_x = Math.Min(min_x, sx); max_x = Math.Max(max_x, sx);
            min_y = Math.Min(min_y, sy); max_y = Math.Max(max_y, sy);
            int dx = sx - center_x, dy = sy - center_y;
            // Count a direction when the stick is well away from the center
            if (dx * dx + dy * dy > MinHalfRange * MinHalfRange) {
                double angle = Math.Atan2(dy, dx) + Math.PI;
                sector_done[Math.Min(Sectors - 1, (int)(angle / (2 * Math.PI) * Sectors))] = true;
            }
            if (trace.Count < 4000)
                trace.Add(new Point(sx, sy));
        }
        btn_next.Enabled = step == 0 ? center_stable() : sectors_done() == Sectors;
    }

    bool center_stable()
    {
        if (recent_count < CenterSamples)
            return false;
        int lx = 4095, hx = 0, ly = 4095, hy = 0;
        for (int i = 0; i < CenterSamples; i++) {
            lx = Math.Min(lx, recent_x[i]); hx = Math.Max(hx, recent_x[i]);
            ly = Math.Min(ly, recent_y[i]); hy = Math.Max(hy, recent_y[i]);
        }
        return hx - lx <= CenterMaxSpread && hy - ly <= CenterMaxSpread;
    }

    int sectors_done()
    {
        int n = 0;
        foreach (bool d in sector_done)
            if (d) n++;
        return n;
    }

    internal bool NextEnabled { get { return btn_next.Enabled; } }

    internal void Next()
    {
        if (!btn_next.Enabled)
            return;
        if (step == 0) {
            int sx = 0, sy = 0;
            for (int i = 0; i < CenterSamples; i++) { sx += recent_x[i]; sy += recent_y[i]; }
            center_x = (sx + CenterSamples / 2) / CenterSamples;
            center_y = (sy + CenterSamples / 2) / CenterSamples;
            min_x = max_x = center_x;
            min_y = max_y = center_y;
            step = 1;
            show_step();
            return;
        }
        if (center_x - min_x < MinHalfRange || max_x - center_x < MinHalfRange ||
            center_y - min_y < MinHalfRange || max_y - center_y < MinHalfRange) {
            lbl_step.Text = "The range is too small. Push the stick all the way to its edge while rotating.";
            return;
        }
        Result = new[] { min_x, center_x, max_x, min_y, center_y, max_y };
        DialogResult = DialogResult.OK;
        Close();
    }

    void show_step()
    {
        if (step == 0) {
            lbl_step.Text = "1/2  Let go of the stick so it rests at its center, then click Next.";
            btn_next.Text = "Next";
        }
        else {
            lbl_step.Text = "2/2  Slowly rotate the stick along its outer edge, pushed all the way, " +
                            "until every direction is covered (2-3 turns). Then click Done.";
            btn_next.Text = "Done";
        }
        btn_next.Enabled = false;
    }

    void update_values()
    {
        if (x < 0) {
            lbl_values.Text = "Waiting for the controller..";
            return;
        }
        string text = String.Format("Now:  X {0:X3}  Y {1:X3}\n", x, y);
        if (step == 0)
            text += center_stable() ? "\nStick at rest." : "\nHold still..";
        else
            text += String.Format("\nCenter: X {0:X3}  Y {1:X3}\nX: {2:X3} - {3:X3}\nY: {4:X3} - {5:X3}\n\nDirections: {6}/{7}",
                                  center_x, center_y, min_x, max_x, min_y, max_y, sectors_done(), Sectors);
        lbl_values.Text = text;
    }

    void draw_view(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = view.Width, h = view.Height;
        // Raw 0-4095 mapped onto the box; Y up
        Func<int, int, PointF> map = (px, py) => new PointF(px * (w - 1) / 4095f, (h - 1) - py * (h - 1) / 4095f);
        using (var grid = new Pen(Color.FromArgb(90, 90, 90)))
            g.DrawLine(grid, w / 2, 0, w / 2, h);
        using (var grid = new Pen(Color.FromArgb(90, 90, 90)))
            g.DrawLine(grid, 0, h / 2, w, h / 2);
        if (step == 1) {
            using (var pen = new Pen(Color.FromArgb(9, 255, 206)))
                for (int i = 1; i < trace.Count; i++)
                    g.DrawLine(pen, map(trace[i - 1].X, trace[i - 1].Y), map(trace[i].X, trace[i].Y));
            PointF a = map(min_x, max_y), b = map(max_x, min_y);
            using (var pen = new Pen(Color.FromArgb(255, 188, 0)))
                g.DrawRectangle(pen, a.X, a.Y, b.X - a.X, b.Y - a.Y);
        }
        if (x >= 0) {
            PointF p = map(x, y);
            g.FillEllipse(Brushes.White, p.X - 4, p.Y - 4, 8, 8);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            poll.Dispose();
        base.Dispose(disposing);
    }
}

}

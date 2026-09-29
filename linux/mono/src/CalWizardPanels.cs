// Linux addition: guided calibration tabs of the Edit Calibration screen.
//   StickCalPanel:  like the Switch's "Calibrate Control Sticks": measures the center (stick at
//                   rest) and the range (stick rotated along its edge).
//   MotionCalPanel: like "Calibrate Motion Controls": measures the gyro and accelerometer
//                   offsets with the controller lying still on a flat surface.
// Both read full input reports (mode 0x30) only while measuring, and write only their own
// part of the user calibration (after a confirmation), keeping everything else as it is.
using System;
using System.Drawing;
using System.Windows.Forms;
using static CppWinFormJoy.Jc;

using u8 = System.Byte;
using u16 = System.UInt16;

namespace CppWinFormJoy {

static class CalUi
{
    public static readonly Color Back   = Color.FromArgb(70, 70, 70);
    public static readonly Color Dark   = Color.FromArgb(55, 55, 55);
    public static readonly Color Grid   = Color.FromArgb(95, 95, 95);
    public static readonly Color Text   = Color.FromArgb(251, 251, 251);
    public static readonly Color Dim    = Color.FromArgb(160, 160, 160);
    public static readonly Color Accent = Color.FromArgb(9, 255, 206);
    public static readonly Color Warn   = Color.FromArgb(255, 188, 0);

    public static Button NewButton(string text, int x, int y, int w = 96)
    {
        // Mono draws a button's text only if a whole line fits: keep them 30px tall
        var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 30), FlatStyle = FlatStyle.Flat,
                             BackColor = Color.FromArgb(85, 85, 85), ForeColor = Accent,
                             Font = new Font("Segoe UI Semibold", 9.75F), UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderColor = b.BackColor;
        return b;
    }

    public static Label NewLabel(int x, int y, int w, int h, float size = 9F)
    {
        return new Label { Location = new Point(x, y), Size = new Size(w, h), ForeColor = Text, BackColor = Back,
                           Font = new Font("Segoe UI", size) };
    }

    public static unsafe void Subcommand(u8 subcmd, u8 arg)
    {
        u8* buf = stackalloc u8[49];
        memset(buf, 0, 49);
        var hdr = (brcm_hdr *)buf;
        var pkt = (brcm_cmd_01 *)(hdr + 1);
        hdr->cmd = 0x01;
        hdr->timer = (u8)(timming_byte & 0xF);
        timming_byte++;
        pkt->subcmd = subcmd;
        pkt->subcmd_arg.arg1 = arg;
        hid_write(handle, buf, 49);
        hid_read_timeout(handle, buf, 0, 64);
    }

    // Reads every queued input report (a stalled window must not lose samples); calls
    // on_report for each full report (0x30).
    public static unsafe void ReadReports(Action<IntPtr> on_report)
    {
        u8* buf = stackalloc u8[49];
        for (int n = 0; n < 64; n++) {
            if (hid_read_timeout(handle, buf, 49, 0) <= 12)
                break;
            if (buf[0] == 0x30)
                on_report((IntPtr)buf);
        }
    }
}

public unsafe class StickCalPanel : Panel
{
    const int Sectors = 24;          // Directions that must be reached while rotating
    const int CenterSamples = 45;    // ~0.7s of reports at rest
    const int CenterMaxSpread = 0x60;
    const int MinHalfRange = 0x300;  // Less than this from the center to an edge: not a real rotation

    enum Step { Idle, Center, Rotate, Done }

    readonly FormJoy form;
    readonly Button btn_left, btn_right, btn_start, btn_finish, btn_save;
    readonly Label lbl_title, lbl_step1, lbl_step2, lbl_step3, lbl_info, lbl_values;
    readonly PictureBox view;
    readonly Timer poll;

    bool left = true;            // Pro Controller: the stick chosen (Joy-Con: its only stick)
    Step step;
    int x = -1, y = -1;
    readonly int[] recent_x = new int[CenterSamples], recent_y = new int[CenterSamples];
    int recent_count, recent_pos;
    int center_x, center_y, min_x, max_x, min_y, max_y;
    readonly bool[] sector_done = new bool[Sectors];
    readonly System.Collections.Generic.List<Point> trace = new System.Collections.Generic.List<Point>();

    // min, center, max for X then Y (raw 12-bit values) once measured
    public int[] Result { get; private set; }

    public StickCalPanel(FormJoy owner)
    {
        form = owner;
        BackColor = CalUi.Back;
        ForeColor = CalUi.Text;
        Size = new Size(450, 362);

        view = new PictureBox { Location = new Point(4, 34), Size = new Size(240, 240), BackColor = CalUi.Dark };
        view.Paint += draw_view;
        btn_left  = CalUi.NewButton("Left stick", 4, 0, 118);
        btn_right = CalUi.NewButton("Right stick", 126, 0, 118);
        btn_left.Click  += (s, e) => select_stick(true);
        btn_right.Click += (s, e) => select_stick(false);
        lbl_title = CalUi.NewLabel(4, 6, 240, 22, 10F);
        lbl_title.ForeColor = CalUi.Accent;

        lbl_step1 = CalUi.NewLabel(254, 0, 196, 40);
        lbl_step2 = CalUi.NewLabel(254, 44, 196, 56);
        lbl_step3 = CalUi.NewLabel(254, 104, 196, 40);
        lbl_values = CalUi.NewLabel(254, 150, 196, 124, 8.25F);
        lbl_info = CalUi.NewLabel(4, 280, 442, 38);
        btn_start  = CalUi.NewButton("Start", 4, 324);
        btn_finish = CalUi.NewButton("Finish", 104, 324);
        btn_save   = CalUi.NewButton("Save", 346, 324);
        btn_start.Click  += (s, e) => Start();
        btn_finish.Click += (s, e) => Finish();
        btn_save.Click   += (s, e) => Save(true);
        Controls.AddRange(new Control[] { view, btn_left, btn_right, lbl_title, lbl_step1, lbl_step2, lbl_step3,
                                          lbl_values, lbl_info, btn_start, btn_finish, btn_save });

        poll = new Timer { Interval = 15 };
        poll.Tick += (s, e) => { CalUi.ReadReports(p => add_report((u8*)p)); refresh(); };
        Reset();
    }

    // Called when the tab is shown (the controller may have changed)
    public void Reset()
    {
        Stop();
        bool pro = handle_type == PROCON;
        btn_left.Visible = btn_right.Visible = pro;
        lbl_title.Visible = !pro;
        if (!pro)
            left = handle_type == JOYCON_L;
        lbl_title.Text = handle_type == JOYCON_L ? "Joy-Con (L) stick" : handle_type == JOYCON_R ? "Joy-Con (R) stick" : "";
        step = Step.Idle;
        Result = null;
        refresh();
    }

    void select_stick(bool l)
    {
        left = l;
        Stop();
        step = Step.Idle;
        Result = null;
        refresh();
    }

    public void Start()
    {
        if (form.check_connection_lost())
            return;
        Stop();
        CalUi.Subcommand(0x03, 0x30);
        recent_count = recent_pos = 0;
        Array.Clear(sector_done, 0, Sectors);
        trace.Clear();
        x = y = -1;
        Result = null;
        step = Step.Center;
        poll.Start();
        refresh();
    }

    public void Stop()
    {
        if (!poll.Enabled)
            return;
        poll.Stop();
        CalUi.Subcommand(0x03, 0x3F);
    }

    internal void add_report(u8* buf)
    {
        u8* s = buf + (left ? 6 : 9);
        add_sample(s[0] | ((s[1] & 0xF) << 8), (s[1] >> 4) | (s[2] << 4));
    }

    internal void add_sample(int sx, int sy)
    {
        x = sx; y = sy;
        if (step == Step.Center) {
            recent_x[recent_pos] = sx;
            recent_y[recent_pos] = sy;
            recent_pos = (recent_pos + 1) % CenterSamples;
            recent_count = Math.Min(recent_count + 1, CenterSamples);
            if (!center_still())
                recent_count = Math.Min(recent_count, 1); // Moved: start over
            if (recent_count == CenterSamples) {
                int ax = 0, ay = 0;
                for (int i = 0; i < CenterSamples; i++) { ax += recent_x[i]; ay += recent_y[i]; }
                center_x = (ax + CenterSamples / 2) / CenterSamples;
                center_y = (ay + CenterSamples / 2) / CenterSamples;
                min_x = max_x = center_x;
                min_y = max_y = center_y;
                step = Step.Rotate;
            }
        }
        else if (step == Step.Rotate) {
            min_x = Math.Min(min_x, sx); max_x = Math.Max(max_x, sx);
            min_y = Math.Min(min_y, sy); max_y = Math.Max(max_y, sy);
            int dx = sx - center_x, dy = sy - center_y;
            if (dx * dx + dy * dy > MinHalfRange * MinHalfRange) {
                double angle = Math.Atan2(dy, dx) + Math.PI;
                sector_done[Math.Min(Sectors - 1, (int)(angle / (2 * Math.PI) * Sectors))] = true;
            }
            if (trace.Count < 4000)
                trace.Add(new Point(sx, sy));
        }
    }

    // The samples so far (up to the newest) stay within a small box
    bool center_still()
    {
        int lx = 4095, hx = 0, ly = 4095, hy = 0;
        for (int i = 0; i < recent_count; i++) {
            int k = (recent_pos - 1 - i + CenterSamples) % CenterSamples;
            lx = Math.Min(lx, recent_x[k]); hx = Math.Max(hx, recent_x[k]);
            ly = Math.Min(ly, recent_y[k]); hy = Math.Max(hy, recent_y[k]);
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

    internal bool CanFinish { get { return step == Step.Rotate && sectors_done() == Sectors; } }
    internal bool Rotating { get { return step == Step.Rotate; } }
    internal bool Measured { get { return Result != null; } }

    public void Finish()
    {
        if (!CanFinish)
            return;
        if (center_x - min_x < MinHalfRange || max_x - center_x < MinHalfRange ||
            center_y - min_y < MinHalfRange || max_y - center_y < MinHalfRange) {
            lbl_info.Text = "The range is too small. Push the stick all the way to its edge while rotating.";
            lbl_info.ForeColor = CalUi.Warn;
            return;
        }
        Stop();
        Result = new[] { min_x, center_x, max_x, min_y, center_y, max_y };
        step = Step.Done;
        refresh();
    }

    // Writes this stick's user calibration (11 bytes), keeping the other stick's.
    public int Save(bool confirm)
    {
        if (Result == null || form.check_connection_lost())
            return -1;
        if (confirm && MessageBox.Show("Write the measured " + stick_name().ToLower() + " calibration to the controller?",
                "Stick calibration", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return -1;
        u8* cal = stackalloc u8[11];
        u16* pair = stackalloc u16[2];
        int[] r = Result;
        cal[0] = 0xB2; cal[1] = 0xA1;
        // Left: +axis, center, -axis. Right: center, -axis, +axis (like the calibration editor).
        pair[0] = (u16)(r[2] - r[1]); pair[1] = (u16)(r[5] - r[4]);
        encode_stick_params(cal + (left ? 2 : 8), pair);
        pair[0] = (u16)r[1]; pair[1] = (u16)r[4];
        encode_stick_params(cal + (left ? 5 : 2), pair);
        pair[0] = (u16)(r[1] - r[0]); pair[1] = (u16)(r[4] - r[3]);
        encode_stick_params(cal + (left ? 8 : 5), pair);
        int res = write_spi_data(left ? 0x8010u : 0x801Bu, 11, cal);
        form.calibration_written();
        lbl_info.Text = res == 0 ? "Saved. The controller now uses this calibration." : "Failed to write the calibration. Please try again.";
        lbl_info.ForeColor = res == 0 ? CalUi.Accent : CalUi.Warn;
        return res;
    }

    string stick_name()
    {
        return handle_type == PROCON ? (left ? "Left stick" : "Right stick") : "Stick";
    }

    static string mark(bool done, bool current)
    {
        return done ? "✔ " : current ? "▶ " : "   ";
    }

    void refresh()
    {
        btn_left.BackColor  = left ? Color.FromArgb(110, 110, 110) : Color.FromArgb(85, 85, 85);
        btn_right.BackColor = left ? Color.FromArgb(85, 85, 85) : Color.FromArgb(110, 110, 110);
        btn_left.FlatAppearance.BorderColor = btn_left.BackColor;
        btn_right.FlatAppearance.BorderColor = btn_right.BackColor;

        lbl_step1.Text = mark(step > Step.Center, step == Step.Center) + "1. Center\n     Let go of the stick.";
        lbl_step2.Text = mark(step > Step.Rotate, step == Step.Rotate) + "2. Range\n     Rotate it slowly along the edge,\n     pushed all the way (2-3 turns).";
        lbl_step3.Text = mark(false, step == Step.Done) + "3. Save\n     Write it to the controller.";
        lbl_step1.ForeColor = step == Step.Center ? CalUi.Accent : step > Step.Center ? CalUi.Text : CalUi.Dim;
        lbl_step2.ForeColor = step == Step.Rotate ? CalUi.Accent : step > Step.Rotate ? CalUi.Text : CalUi.Dim;
        lbl_step3.ForeColor = step == Step.Done ? CalUi.Accent : CalUi.Dim;

        string v = x >= 0 ? String.Format("Stick:   X {0:X3}  Y {1:X3}\n", x, y) : "";
        if (step >= Step.Rotate)
            v += String.Format("Center: X {0:X3}  Y {1:X3}\nX range: {2:X3} - {3:X3}\nY range: {4:X3} - {5:X3}\n",
                               center_x, center_y, min_x, max_x, min_y, max_y);
        if (step == Step.Rotate)
            v += "Directions: " + sectors_done() + " / " + Sectors;
        lbl_values.Text = v;

        switch (step) {
            case Step.Idle:
                lbl_info.Text = "Measures the " + stick_name().ToLower() + "'s center and range and writes them as its user calibration. Click Start.";
                break;
            case Step.Center:
                lbl_info.Text = x < 0 ? "Waiting for the controller.." : recent_count > 1 ? "Hold still.." : "Let go of the stick so it rests at its center.";
                break;
            case Step.Rotate:
                lbl_info.Text = sectors_done() < Sectors ? "Rotate the stick along its edge until the whole ring lights up."
                                                        : "Every direction covered. Rotate once or twice more, then click Finish.";
                break;
            case Step.Done:
                lbl_info.Text = "Measured. Click Save to write it to the controller (Start: measure again).";
                break;
        }
        lbl_info.ForeColor = CalUi.Text;
        btn_start.Text = step == Step.Idle ? "Start" : "Restart";
        btn_finish.Enabled = CanFinish;
        btn_save.Enabled = step == Step.Done;
        view.Invalidate();
    }

    void draw_view(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int w = view.Width, h = view.Height;
        float cx = w / 2f, cy = h / 2f, ring = w / 2f - 14;
        using (var pen = new Pen(CalUi.Grid)) {
            g.DrawLine(pen, cx, 0, cx, h);
            g.DrawLine(pen, 0, cy, w, cy);
            g.DrawEllipse(pen, cx - ring, cy - ring, 2 * ring, 2 * ring);
        }
        // Map raw values: during rotation around the measured center, else around the middle
        float ox = step >= Step.Rotate ? center_x : 2048, oy = step >= Step.Rotate ? center_y : 2048;
        float scale = ring / 1500f;
        Func<float, float, PointF> map = (px, py) => new PointF(cx + (px - ox) * scale, cy - (py - oy) * scale);

        // Direction ring: lit segments were reached
        if (step >= Step.Rotate) {
            float a = 6, box = ring + 8;
            for (int i = 0; i < Sectors; i++) {
                // Sector i covers atan2 angles (i / Sectors) * 360 - 180 ..; screen Y is flipped
                float start = -((i + 1) * 360f / Sectors - 180f);
                bool lit = sector_done[i] || step == Step.Done;
                using (var pen = new Pen(lit ? CalUi.Accent : CalUi.Grid, a))
                    g.DrawArc(pen, cx - box, cy - box, 2 * box, 2 * box, start + 1.5f, 360f / Sectors - 3f);
            }
            using (var pen = new Pen(Color.FromArgb(160, CalUi.Accent)))
                for (int i = 1; i < trace.Count; i++)
                    g.DrawLine(pen, map(trace[i - 1].X, trace[i - 1].Y), map(trace[i].X, trace[i].Y));
            PointF lo = map(min_x, max_y), hi = map(max_x, min_y);
            using (var pen = new Pen(CalUi.Warn) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                g.DrawRectangle(pen, lo.X, lo.Y, hi.X - lo.X, hi.Y - lo.Y);
        }
        // Center step: a shrinking circle shows the hold-still progress
        if (step == Step.Center && recent_count > 1) {
            float r = 30 * (1 - (float)recent_count / CenterSamples) + 6;
            using (var pen = new Pen(CalUi.Accent, 2))
                g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        }
        if (x >= 0) {
            PointF p = map(x, y);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, p.X - 6, p.Y - 6, 12, 12);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            poll.Dispose();
        base.Dispose(disposing);
    }
}

public unsafe class MotionCalPanel : Panel
{
    const int NeedSamples = 300;     // 3 per report: ~1.7s still
    const int GyroMaxSpread = 40;    // Raw counts; a still Joy-Con varies by ~10
    const int AccMaxSpread = 150;
    const int OneG = 4096;           // Accelerometer at +-8G: 4096 counts per G

    enum Step { Idle, Measure, Done }

    readonly FormJoy form;
    readonly Button btn_start, btn_save;
    readonly Label lbl_steps, lbl_info, lbl_values;
    readonly PictureBox view;
    readonly Timer poll;

    Step step;
    readonly short[] last = new short[6];
    bool have_sample;
    readonly long[] sum = new long[6];
    readonly short[] lo = new short[6], hi = new short[6];
    int count;
    string problem = "";

    // Acc origin X, Y, Z, gyro origin X, Y, Z (raw) once measured
    public int[] Result { get; private set; }

    public MotionCalPanel(FormJoy owner)
    {
        form = owner;
        BackColor = CalUi.Back;
        ForeColor = CalUi.Text;
        Size = new Size(450, 362);

        view = new PictureBox { Location = new Point(4, 0), Size = new Size(240, 274), BackColor = CalUi.Dark };
        view.Paint += draw_view;
        lbl_steps = CalUi.NewLabel(254, 0, 196, 150);
        lbl_values = CalUi.NewLabel(254, 150, 196, 124, 8.25F);
        lbl_info = CalUi.NewLabel(4, 280, 442, 38);
        btn_start = CalUi.NewButton("Start", 4, 324);
        btn_save  = CalUi.NewButton("Save", 346, 324);
        btn_start.Click += (s, e) => Start();
        btn_save.Click  += (s, e) => Save(true);
        Controls.AddRange(new Control[] { view, lbl_steps, lbl_values, lbl_info, btn_start, btn_save });

        poll = new Timer { Interval = 15 };
        poll.Tick += (s, e) => { CalUi.ReadReports(p => add_report((u8*)p)); refresh(); };
        Reset();
    }

    public void Reset()
    {
        Stop();
        step = Step.Idle;
        Result = null;
        have_sample = false;
        refresh();
    }

    public void Start()
    {
        if (form.check_connection_lost())
            return;
        Stop();
        CalUi.Subcommand(0x40, 0x01); // IMU on
        CalUi.Subcommand(0x03, 0x30);
        restart_collection();
        Result = null;
        have_sample = false;
        step = Step.Measure;
        poll.Start();
        refresh();
    }

    public void Stop()
    {
        if (!poll.Enabled)
            return;
        poll.Stop();
        CalUi.Subcommand(0x03, 0x3F);
        CalUi.Subcommand(0x40, 0x00);
    }

    void restart_collection()
    {
        Array.Clear(sum, 0, 6);
        count = 0;
    }

    internal void add_report(u8* buf)
    {
        for (int s = 0; s < 3; s++) {
            short* v = (short*)(buf + 13 + s * 12);
            for (int i = 0; i < 6; i++)
                last[i] = v[i];
            have_sample = true;
            if (step == Step.Measure)
                add_sample();
        }
    }

    void add_sample()
    {
        if (count == 0) {
            for (int i = 0; i < 6; i++)
                lo[i] = hi[i] = last[i];
        }
        for (int i = 0; i < 6; i++) {
            lo[i] = Math.Min(lo[i], last[i]);
            hi[i] = Math.Max(hi[i], last[i]);
            sum[i] += last[i];
        }
        count++;
        problem = "";
        for (int i = 0; i < 3; i++)
            if (hi[i] - lo[i] > AccMaxSpread || hi[i + 3] - lo[i + 3] > GyroMaxSpread)
                problem = "The controller moved. Keep it still..";
        int z = Math.Abs(last[2]);
        if (problem == "" && (z < OneG * 3 / 4 || z > OneG * 5 / 4))
            problem = "Lay the controller flat (face up or down) on a level surface.";
        if (problem != "") {
            restart_collection();
            return;
        }
        if (count >= NeedSamples)
            finish();
    }

    void finish()
    {
        var r = new int[6];
        for (int i = 0; i < 6; i++)
            r[i] = (int)Math.Round((double)sum[i] / count);
        // At rest the accelerometer reads 1G on Z; the offset is what's left
        r[2] -= r[2] > 0 ? OneG : -OneG;
        Result = r;
        Stop();
        step = Step.Done;
    }

    internal bool Measured { get { return Result != null; } }

    // Writes the 6-axis user calibration: the measured origins, with the factory sensitivities.
    public int Save(bool confirm)
    {
        if (Result == null || form.check_connection_lost())
            return -1;
        if (confirm && MessageBox.Show("Write the measured motion calibration to the controller?",
                "Motion calibration", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return -1;
        u8* factory = stackalloc u8[24];
        u8* cal = stackalloc u8[26];
        memset(factory, 0, 24);
        get_spi_data(0x6020, 24, factory);
        cal[0] = 0xB2; cal[1] = 0xA1;
        for (int i = 0; i < 3; i++) {
            *(short*)(cal + 2 + i * 2)  = (short)Result[i];
            *(short*)(cal + 14 + i * 2) = (short)Result[i + 3];
        }
        for (int i = 0; i < 6; i++) {
            cal[8 + i]  = factory[6 + i];   // Acc sensitivity
            cal[20 + i] = factory[18 + i];  // Gyro sensitivity
        }
        int res = write_spi_data(0x8026, 26, cal);
        form.calibration_written();
        lbl_info.Text = res == 0 ? "Saved. The controller now uses this calibration." : "Failed to write the calibration. Please try again.";
        lbl_info.ForeColor = res == 0 ? CalUi.Accent : CalUi.Warn;
        return res;
    }

    void refresh()
    {
        string m1 = step == Step.Idle ? "▶ " : "✔ ";
        string m2 = step == Step.Measure ? "▶ " : step == Step.Done ? "✔ " : "   ";
        string m3 = step == Step.Done ? "▶ " : "   ";
        lbl_steps.Text = m1 + "1. Place it\n     On a flat, level, still surface,\n     buttons facing up.\n\n" +
                         m2 + "2. Measure\n     Don't touch it (~2 seconds).\n\n" +
                         m3 + "3. Save\n     Write it to the controller.";
        lbl_steps.ForeColor = step == Step.Idle ? CalUi.Text : CalUi.Accent;

        string v = "";
        if (have_sample)
            v = String.Format("Acc:   {0,6} {1,6} {2,6}\nGyro: {3,6} {4,6} {5,6}\n", last[0], last[1], last[2], last[3], last[4], last[5]);
        if (Result != null)
            v += String.Format("\nOffsets found:\nAcc:   {0,6} {1,6} {2,6}\nGyro: {3,6} {4,6} {5,6}",
                               Result[0], Result[1], Result[2], Result[3], Result[4], Result[5]);
        lbl_values.Text = v;

        switch (step) {
            case Step.Idle:
                lbl_info.Text = "Measures the motion sensors' offsets (fixes drift) and writes them as the 6-axis user calibration. Place the controller, then click Start.";
                break;
            case Step.Measure:
                lbl_info.Text = !have_sample ? "Waiting for the controller.." : problem != "" ? problem : "Measuring, don't touch it..";
                break;
            case Step.Done:
                lbl_info.Text = "Measured. Click Save to write it to the controller (Start: measure again).";
                break;
        }
        lbl_info.ForeColor = step == Step.Measure && problem != "" ? CalUi.Warn : CalUi.Text;
        btn_start.Text = step == Step.Idle ? "Start" : "Restart";
        btn_save.Enabled = step == Step.Done;
        view.Invalidate();
    }

    void draw_view(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int w = view.Width;
        float cx = w / 2f, cy = 110, r = 90;
        // Bubble level from the accelerometer's X/Y (tilt)
        using (var pen = new Pen(CalUi.Grid)) {
            g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
            g.DrawEllipse(pen, cx - 18, cy - 18, 36, 36);
            g.DrawLine(pen, cx - r, cy, cx + r, cy);
            g.DrawLine(pen, cx, cy - r, cx, cy + r);
        }
        if (have_sample) {
            float bx = Math.Max(-1, Math.Min(1, last[1] / (float)(OneG / 2)));
            float by = Math.Max(-1, Math.Min(1, last[0] / (float)(OneG / 2)));
            bool level = Math.Abs(last[0]) < OneG / 20 && Math.Abs(last[1]) < OneG / 20;
            using (var b = new SolidBrush(level ? CalUi.Accent : CalUi.Warn))
                g.FillEllipse(b, cx + bx * (r - 12) - 12, cy + by * (r - 12) - 12, 24, 24);
        }
        // Progress of the still measurement
        float progress = step == Step.Done ? 1 : step == Step.Measure ? Math.Min(1, count / (float)NeedSamples) : 0;
        using (var b = new SolidBrush(CalUi.Grid))
            g.FillRectangle(b, 16, 236, w - 32, 12);
        using (var b = new SolidBrush(CalUi.Accent))
            g.FillRectangle(b, 16, 236, (w - 32) * progress, 12);
        using (var b = new SolidBrush(CalUi.Dim))
        using (var f = new Font("Segoe UI", 8.25F))
            g.DrawString("Still: " + (int)(progress * 100) + "%", f, b, 16, 252);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            poll.Dispose();
        base.Dispose(disposing);
    }
}

}

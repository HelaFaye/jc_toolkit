// Linux addition: the HD Rumble Player's MIDI tab. Plays a MIDI file, or a MIDI device live,
// on the controller's HD Rumble (see Midi.cs).
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using static CppWinFormJoy.Jc;

namespace CppWinFormJoy {

public class MidiPanel : Panel
{
    readonly FormJoy form;
    readonly MidiRumble rumble = new MidiRumble();
    readonly MidiFilePlayer player;
    readonly MidiInput input;
    MidiSong song;
    string song_name = "";

    readonly Button btn_file_mode, btn_device_mode, btn_load, btn_play, btn_refresh, btn_listen;
    readonly Panel file_page, device_page;
    readonly Label lbl_file, lbl_status, lbl_notes, lbl_volume;
    readonly ComboBox cmb_part, cmb_device, cmb_channel;
    readonly CheckBox chk_bass;
    readonly TrackBar trk_volume;
    readonly PictureBox view;
    readonly Timer ui;
    bool device_mode;

    public MidiPanel(FormJoy owner)
    {
        form = owner;
        player = new MidiFilePlayer(rumble);
        input = new MidiInput(rumble);
        BackColor = CalUi.Back;
        ForeColor = CalUi.Text;
        Size = new Size(214, 376);

        btn_file_mode   = CalUi.NewButton("File", 2, 0, 103);
        btn_device_mode = CalUi.NewButton("Device", 109, 0, 103);
        btn_file_mode.Click   += (s, e) => set_mode(false);
        btn_device_mode.Click += (s, e) => set_mode(true);

        // File source
        file_page = new Panel { Location = new Point(0, 36), Size = new Size(214, 128), BackColor = CalUi.Back };
        btn_load = CalUi.NewButton("Load MIDI..", 2, 0, 103);
        btn_play = CalUi.NewButton("Play", 109, 0, 103);
        btn_load.Click += (s, e) => load_file();
        btn_play.Click += (s, e) => play_or_stop();
        lbl_file = CalUi.NewLabel(2, 34, 210, 34, 8.25F);
        lbl_file.Text = "No MIDI file loaded (.mid).";
        cmb_part = new_combo(2, 72, 210);
        cmb_part.SelectedIndexChanged += (s, e) => { if (player.Playing) play_or_stop(); };
        file_page.Controls.AddRange(new Control[] { btn_load, btn_play, lbl_file, cmb_part });

        // Device source
        device_page = new Panel { Location = new Point(0, 36), Size = new Size(214, 128), BackColor = CalUi.Back };
        cmb_device = new_combo(2, 2, 150);
        btn_refresh = CalUi.NewButton("Scan", 156, 0, 56);
        btn_refresh.Click += (s, e) => refresh_devices();
        cmb_channel = new_combo(2, 36, 150);
        cmb_channel.Items.Add("All channels");
        for (int c = 1; c <= 16; c++)
            cmb_channel.Items.Add("Channel " + c);
        cmb_channel.SelectedIndex = 0;
        cmb_channel.SelectedIndexChanged += (s, e) => input.channel = cmb_channel.SelectedIndex - 1;
        btn_listen = CalUi.NewButton("Listen", 2, 72, 210);
        btn_listen.Click += (s, e) => listen_or_stop();
        device_page.Controls.AddRange(new Control[] { cmb_device, btn_refresh, cmb_channel, btn_listen });

        // Shared: what's playing, options
        view = new PictureBox { Location = new Point(2, 168), Size = new Size(210, 96), BackColor = CalUi.Dark };
        view.Paint += draw_view;
        lbl_notes = CalUi.NewLabel(2, 268, 210, 18, 8.25F);
        lbl_status = CalUi.NewLabel(2, 286, 210, 32, 8.25F);
        chk_bass = new CheckBox { Location = new Point(2, 318), Size = new Size(210, 20), ForeColor = CalUi.Warn, BackColor = CalUi.Back,
                                  Font = new Font("Segoe UI", 8.25F), Text = "Lowest note on the low band", Checked = true };
        chk_bass.CheckedChanged += (s, e) => rumble.bass_on_low = chk_bass.Checked;
        lbl_volume = CalUi.NewLabel(2, 342, 70, 20, 8.25F);
        trk_volume = new TrackBar { Location = new Point(70, 338), Size = new Size(142, 30), Minimum = 0, Maximum = 100, Value = 100,
                                    TickStyle = TickStyle.None, BackColor = CalUi.Back, AutoSize = false };
        trk_volume.ValueChanged += (s, e) => { rumble.volume = trk_volume.Value / 100f; lbl_volume.Text = "Volume " + trk_volume.Value + "%"; };
        lbl_volume.Text = "Volume 100%";

        Controls.AddRange(new Control[] { btn_file_mode, btn_device_mode, file_page, device_page, view, lbl_notes, lbl_status,
                                          chk_bass, lbl_volume, trk_volume });
        ui = new Timer { Interval = 50 };
        ui.Tick += (s, e) => refresh();
        set_mode(false);
        refresh_devices();
    }

    static ComboBox new_combo(int x, int y, int w)
    {
        return new ComboBox { Location = new Point(x, y), Size = new Size(w, 26), DropDownStyle = ComboBoxStyle.DropDownList,
                              FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(85, 85, 85), ForeColor = CalUi.Text,
                              Font = new Font("Segoe UI", 8.25F) };
    }

    void set_mode(bool device)
    {
        Stop();
        device_mode = device;
        file_page.Visible = !device;
        device_page.Visible = device;
        foreach (var b in new[] { btn_file_mode, btn_device_mode }) {
            bool on = (b == btn_device_mode) == device;
            b.BackColor = on ? Color.FromArgb(110, 110, 110) : Color.FromArgb(85, 85, 85);
            b.FlatAppearance.BorderColor = b.BackColor;
            b.ForeColor = on ? CalUi.Accent : Color.FromArgb(200, 200, 200);
        }
        refresh();
    }

    // Stops playing/listening (leaving the tab or the panel)
    public void Stop()
    {
        player.Stop();
        input.Stop();
        ui.Stop();
        refresh();
    }

    void load_file()
    {
        using (var dlg = new OpenFileDialog { Filter = "MIDI files (*.mid, *.midi)|*.mid;*.midi;*.MID;*.MIDI|All files (*.*)|*.*",
                                              Title = "Open a MIDI file" }) {
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;
            Load(dlg.FileName);
        }
    }

    // Also used by the self-test
    public bool Load(string path)
    {
        Stop();
        try {
            song = MidiSong.Load(File.ReadAllBytes(path));
        }
        catch (Exception ex) {
            song = null;
            lbl_file.Text = "Can't load " + Path.GetFileName(path) + ": " + ex.Message;
            lbl_file.ForeColor = CalUi.Warn;
            fill_parts();
            return false;
        }
        song_name = Path.GetFileName(path);
        lbl_file.ForeColor = CalUi.Text;
        lbl_file.Text = song_name + "\n" + format_time(song.length) + ", " + song.notes.Count + " notes, " + song.parts.Count + " parts";
        fill_parts();
        refresh();
        return true;
    }

    void fill_parts()
    {
        cmb_part.Items.Clear();
        if (song == null)
            return;
        cmb_part.Items.Add("All parts (no drums)");
        foreach (var p in song.parts)
            cmb_part.Items.Add(p);
        cmb_part.SelectedIndex = 0;
    }

    public void Play()
    {
        if (song == null || form.check_connection_lost())
            return;
        input.Stop();
        player.Play(song, cmb_part.SelectedIndex - 1);
        ui.Start();
        refresh();
    }

    void play_or_stop()
    {
        if (player.Playing)
            Stop();
        else
            Play();
    }

    void refresh_devices()
    {
        cmb_device.Items.Clear();
        foreach (var d in MidiInput.Devices())
            cmb_device.Items.Add(new DeviceItem { path = d.Key, name = d.Value });
        if (cmb_device.Items.Count > 0)
            cmb_device.SelectedIndex = 0;
        refresh();
    }

    class DeviceItem
    {
        public string path, name;
        public override string ToString() { return name; }
    }

    // Listen to a MIDI device (or, for the self-test, any path giving MIDI bytes)
    public bool Listen(string path)
    {
        if (form.check_connection_lost())
            return false;
        player.Stop();
        bool ok = input.Listen(path);
        if (ok)
            ui.Start();
        refresh();
        return ok;
    }

    void listen_or_stop()
    {
        if (input.Listening) {
            Stop();
            return;
        }
        var item = cmb_device.SelectedItem as DeviceItem;
        if (item == null) {
            lbl_status.Text = "No MIDI device found. Connect one and click Scan.";
            lbl_status.ForeColor = CalUi.Warn;
            return;
        }
        Listen(item.path);
    }

    internal bool Playing { get { return player.Playing; } }
    internal bool Listening { get { return input.Listening; } }
    internal MidiSong Song { get { return song; } }
    internal int Part { get { return cmb_part.SelectedIndex - 1; } set { cmb_part.SelectedIndex = value + 1; } }
    internal float Volume { set { trk_volume.Value = (int)(value * 100); } }

    static string format_time(double s)
    {
        int t = (int)Math.Round(s);
        return (t / 60) + ":" + (t % 60).ToString("00");
    }

    void refresh()
    {
        bool playing = player.Playing, listening = input.Listening;
        if (!playing && !listening && ui.Enabled)
            ui.Stop();
        btn_play.Text = playing ? "Stop" : "Play";
        btn_play.Enabled = song != null;
        cmb_part.Visible = song != null; // Mono draws a disabled combo box light
        btn_listen.Text = listening ? "Stop" : "Listen";
        btn_listen.Enabled = cmb_device.Items.Count > 0 || listening;
        cmb_device.Enabled = btn_refresh.Enabled = !listening;

        string status;
        Color color = CalUi.Text;
        if (!device_mode)
            status = playing ? "Playing " + format_time(player.position) + " / " + format_time(song.length)
                   : song != null ? "Ready. Play sends the notes to the rumble." : "Load a MIDI file to play it on the rumble.";
        else if (input.error != "") {
            status = input.error;
            color = CalUi.Warn;
        }
        else if (listening)
            status = "Listening. Play something (" + input.messages + " messages).";
        else
            status = cmb_device.Items.Count > 0 ? "Pick a MIDI device and click Listen." : "No MIDI device found (/dev/snd/midi*). Connect one and click Scan.";
        lbl_status.Text = status;
        lbl_status.ForeColor = color;
        lbl_notes.Text = rumble.now_playing != "" ? "Notes: " + rumble.now_playing : "";
        view.Invalidate();
    }

    // The high band's note (teal) and the low band's (orange), on their frequency ranges
    void draw_view(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = view.Width;
        // The two bands' ranges (log scale), with the note each one plays
        using (var f = new Font("Segoe UI", 8F))
        using (var dim = new SolidBrush(CalUi.Dim)) {
            g.DrawString("High band", f, dim, 4, 4);
            g.DrawString("Low band", f, dim, 4, 50);
            draw_band(g, f, 22, w, MidiRumble.HighLow, MidiRumble.HighHigh, rumble.high_note, CalUi.Accent);
            draw_band(g, f, 68, w, MidiRumble.LowLow, MidiRumble.LowHigh, rumble.low_note, CalUi.Warn);
        }
    }

    void draw_band(Graphics g, Font f, int y, int w, double lo, double hi, int pitch, Color color)
    {
        using (var b = new SolidBrush(CalUi.Grid))
            g.FillRectangle(b, 8, y, w - 16, 8);
        if (pitch < 0)
            return;
        double hz = MidiNotes.Fold(MidiNotes.Frequency(pitch), lo, hi);
        float x = 8 + (float)((Math.Log(hz) - Math.Log(lo)) / (Math.Log(hi) - Math.Log(lo))) * (w - 16);
        using (var b = new SolidBrush(color)) {
            g.FillRectangle(b, x - 3, y - 3, 6, 14);
            string label = MidiNotes.Name(pitch) + "  " + (int)Math.Round(hz) + " Hz";
            float tx = Math.Min(w - 90, Math.Max(4, x - 30));
            g.DrawString(label, f, b, tx, y + 12);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            Stop();
            ui.Dispose();
        }
        base.Dispose(disposing);
    }
}

}

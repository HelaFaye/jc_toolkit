// Linux addition: MIDI to HD Rumble (the HD Rumble Player's MIDI tab).
//
// The idea comes from Musical-Joycons by sarossilli (github.com/sarossilli/MusicalJoycons):
// play MIDI notes as the frequency of the Joy-Con's rumble, octave-shifted into what the
// actuator can do. This is a separate implementation that uses both HD Rumble bands:
//   high band (81.75-1252 Hz): the highest note held, folded into 400-1252 Hz
//   low band (40.875-626 Hz):  the lowest note held (when two or more are), folded into 100-626 Hz
// Amplitudes come from the note velocities and the volume, encoded with the same tables as
// the HD Rumble Player's .bnvib conversion, and kept to a sum of 1.0 like it does.
//
// Sources: Standard MIDI Files (format 0/1, tempo changes), and MIDI devices through ALSA's
// raw MIDI devices (/dev/snd/midiC*D*: USB keyboards and other hardware).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using u8 = System.Byte;

namespace CppWinFormJoy {

public static class MidiNotes
{
    static readonly string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static string Name(int pitch)
    {
        return names[pitch % 12] + (pitch / 12 - 1);
    }

    public static double Frequency(int pitch)
    {
        return 440.0 * Math.Pow(2, (pitch - 69) / 12.0);
    }

    // Octave-shift a frequency into [low, high]
    public static double Fold(double hz, double low, double high)
    {
        while (hz < low)
            hz *= 2;
        while (hz > high)
            hz /= 2;
        return hz;
    }
}

// One note of a MIDI file, in seconds
public struct MidiNote
{
    public double start, end;
    public byte pitch, velocity;
    public int part;
}

public class MidiPart
{
    public int track, channel, notes;
    public string name;
    public bool drums { get { return channel == 9; } }
    public override string ToString()
    {
        return (name != "" ? name : "Track " + track) + " (ch " + (channel + 1) + (drums ? ", drums" : "") + ", " + notes + " notes)";
    }
}

public class MidiSong
{
    public readonly List<MidiNote> notes = new List<MidiNote>();
    public readonly List<MidiPart> parts = new List<MidiPart>();
    public double length;

    // Standard MIDI File: header chunk, then track chunks of delta-timed events.
    public static MidiSong Load(byte[] d)
    {
        int pos = 0;
        Func<int> u32 = () => { int v = (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3]; pos += 4; return v; };
        Func<int> u16 = () => { int v = (d[pos] << 8) | d[pos + 1]; pos += 2; return v; };
        if (d.Length < 14 || Encoding.ASCII.GetString(d, 0, 4) != "MThd")
            throw new InvalidDataException("Not a MIDI file");
        pos = 4;
        int header_len = u32();
        int format = u16(), ntracks = u16(), division = u16();
        pos = 8 + header_len;
        if (format > 1)
            throw new InvalidDataException("MIDI format " + format + " isn't supported");

        // Raw events: (tick, track, data), tempo changes (tick, us per quarter note)
        var tempos = new List<KeyValuePair<long, int>>();
        var raw = new List<Tuple<long, int, int, int, int, int>>(); // tick, track, status, data1, data2, order
        var track_names = new Dictionary<int, string>();
        for (int t = 0; t < ntracks && pos + 8 <= d.Length; t++) {
            string id = Encoding.ASCII.GetString(d, pos, 4);
            pos += 4;
            int len = u32();
            int end = Math.Min(d.Length, pos + len);
            if (id != "MTrk") {
                pos = end;
                continue;
            }
            long tick = 0;
            int status = 0;
            while (pos < end) {
                tick += read_varlen(d, ref pos);
                if (pos >= end)
                    break;
                int b = d[pos];
                if (b == 0xFF) {                 // Meta event
                    int type = d[pos + 1];
                    pos += 2;
                    int mlen = (int)read_varlen(d, ref pos);
                    if (type == 0x51 && mlen == 3)
                        tempos.Add(new KeyValuePair<long, int>(tick, (d[pos] << 16) | (d[pos + 1] << 8) | d[pos + 2]));
                    else if (type == 0x03 && !track_names.ContainsKey(t))
                        track_names[t] = Encoding.UTF8.GetString(d, pos, Math.Min(mlen, end - pos)).Trim('\0', ' ');
                    else if (type == 0x2F)
                        pos = end - mlen;        // End of track
                    pos += mlen;
                    continue;
                }
                if (b == 0xF0 || b == 0xF7) {    // SysEx
                    pos++;
                    pos += (int)read_varlen(d, ref pos);
                    continue;
                }
                if ((b & 0x80) != 0) {
                    status = b;
                    pos++;
                }
                else if (status == 0)
                    throw new InvalidDataException("Bad MIDI data in track " + t);
                int kind = status & 0xF0;
                int d1 = d[pos++];
                int d2 = (kind == 0xC0 || kind == 0xD0) ? 0 : d[pos++];
                raw.Add(Tuple.Create(tick, t, status, d1, d2, raw.Count));
            }
            pos = end;
        }

        // Tick to seconds, with the tempo map (or SMPTE time)
        tempos.Sort((a, b) => a.Key.CompareTo(b.Key));
        Func<long, double> seconds;
        if ((division & 0x8000) != 0) {
            int fps = 256 - (division >> 8);
            int per_frame = division & 0xFF;
            double tick_s = 1.0 / (fps == 29 ? 29.97 : fps) / per_frame;
            seconds = tick => tick * tick_s;
        }
        else {
            int tpq = Math.Max(1, division);
            seconds = tick => {
                double s = 0;
                long at = 0;
                int us = 500000;              // 120 BPM until the first tempo event
                foreach (var tc in tempos) {
                    if (tc.Key >= tick)
                        break;
                    s += (tc.Key - at) * (double)us / tpq / 1e6;
                    at = tc.Key;
                    us = tc.Value;
                }
                return s + (tick - at) * (double)us / tpq / 1e6;
            };
        }

        // Notes: pair note-ons with note-offs, per track and channel
        var song = new MidiSong();
        var part_index = new Dictionary<int, int>();          // track * 16 + channel
        var open = new Dictionary<int, Stack<Tuple<long, int>>>(); // (track, channel, pitch) -> starts
        raw.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item6.CompareTo(b.Item6)); // Stable: file order at the same tick
        foreach (var ev in raw) {
            int kind = ev.Item3 & 0xF0, ch = ev.Item3 & 0x0F;
            if (kind != 0x80 && kind != 0x90)
                continue;
            int key = (ev.Item2 * 16 + ch) * 128 + ev.Item4;
            if (kind == 0x90 && ev.Item5 > 0) {
                Stack<Tuple<long, int>> st;
                if (!open.TryGetValue(key, out st))
                    open[key] = st = new Stack<Tuple<long, int>>();
                st.Push(Tuple.Create(ev.Item1, ev.Item5));
                continue;
            }
            Stack<Tuple<long, int>> starts;
            if (!open.TryGetValue(key, out starts) || starts.Count == 0)
                continue;
            var on = starts.Pop();
            int pk = ev.Item2 * 16 + ch, pi;
            if (!part_index.TryGetValue(pk, out pi)) {
                pi = song.parts.Count;
                part_index[pk] = pi;
                string name;
                song.parts.Add(new MidiPart { track = ev.Item2, channel = ch, name = track_names.TryGetValue(ev.Item2, out name) ? name : "" });
            }
            song.parts[pi].notes++;
            var n = new MidiNote { start = seconds(on.Item1), end = seconds(ev.Item1), pitch = (byte)ev.Item4, velocity = (byte)on.Item2, part = pi };
            song.notes.Add(n);
            song.length = Math.Max(song.length, n.end);
        }
        // Parts in file order (track, then channel)
        var order = new List<int>();
        for (int i = 0; i < song.parts.Count; i++)
            order.Add(i);
        order.Sort((a, b) => (song.parts[a].track * 16 + song.parts[a].channel).CompareTo(song.parts[b].track * 16 + song.parts[b].channel));
        var remap = new int[order.Count];
        var sorted = new List<MidiPart>();
        for (int i = 0; i < order.Count; i++) {
            remap[order[i]] = i;
            sorted.Add(song.parts[order[i]]);
        }
        song.parts.Clear();
        song.parts.AddRange(sorted);
        for (int i = 0; i < song.notes.Count; i++) {
            var n = song.notes[i];
            n.part = remap[n.part];
            song.notes[i] = n;
        }
        song.notes.Sort((a, b) => a.start.CompareTo(b.start));
        return song;
    }

    static long read_varlen(byte[] d, ref int pos)
    {
        long v = 0;
        for (int i = 0; i < 4 && pos < d.Length; i++) {
            int b = d[pos++];
            v = (v << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                break;
        }
        return v;
    }
}

// Held notes -> HD Rumble, sent from its own thread: on every change, and again every 25ms
// while notes sound (a lost Bluetooth packet must not leave a wrong note playing).
public unsafe class MidiRumble : IDisposable
{
    public const double HighLow = 400, HighHigh = 1252; // High band range used for notes
    public const double LowLow = 100, LowHigh = 626;    // Low band range used for notes

    readonly SortedDictionary<int, int> held = new SortedDictionary<int, int>(); // pitch -> velocity
    readonly HashSet<int> sustained = new HashSet<int>();
    readonly object sync = new object();
    Thread thread;
    volatile bool running;
    bool dirty, sustain;

    public volatile float volume = 1.0f;   // 0-1
    public volatile bool bass_on_low = true;

    // What's playing now, for the display
    public volatile string now_playing = "";
    public volatile int high_note = -1, low_note = -1;

    public void Start()
    {
        if (running)
            return;
        lock (sync) { held.Clear(); sustained.Clear(); sustain = false; dirty = true; }
        // The controller ignores rumble reports until vibration is enabled (like the
        // HD Rumble Player does before playing a file)
        CalUi.Subcommand(0x48, 0x01);
        running = true;
        thread = new Thread(run) { IsBackground = true, Name = "MIDI rumble" };
        thread.Start();
    }

    public void Stop()
    {
        if (!running)
            return;
        running = false;
        thread.Join(500);
        send(0, 0, 0, 0);
        CalUi.Subcommand(0x48, 0x00);
        now_playing = "";
        high_note = low_note = -1;
    }

    public void Dispose() { Stop(); }

    public void NoteOn(int pitch, int velocity)
    {
        if (velocity == 0) {
            NoteOff(pitch);
            return;
        }
        lock (sync) { held[pitch] = velocity; sustained.Remove(pitch); dirty = true; }
    }

    public void NoteOff(int pitch)
    {
        lock (sync) {
            if (sustain && held.ContainsKey(pitch))
                sustained.Add(pitch);
            else
                held.Remove(pitch);
            dirty = true;
        }
    }

    public void Sustain(bool on)
    {
        lock (sync) {
            sustain = on;
            if (!on) {
                foreach (int p in sustained)
                    held.Remove(p);
                sustained.Clear();
            }
            dirty = true;
        }
    }

    public void AllOff()
    {
        lock (sync) { held.Clear(); sustained.Clear(); dirty = true; }
    }

    // The rumble for a set of held notes: high band frequency/amplitude, low band frequency/amplitude
    public static void Choose(IList<KeyValuePair<int, int>> notes, float volume, bool bass_on_low,
                              out double hf, out float ha, out double lf, out float la)
    {
        hf = lf = 0;
        ha = la = 0;
        if (notes.Count == 0)
            return;
        var top = notes[notes.Count - 1];
        hf = MidiNotes.Fold(MidiNotes.Frequency(top.Key), HighLow, HighHigh);
        ha = top.Value / 127f * volume;
        if (bass_on_low && notes.Count > 1) {
            var bottom = notes[0];
            lf = MidiNotes.Fold(MidiNotes.Frequency(bottom.Key), LowLow, LowHigh);
            la = bottom.Value / 127f * volume;
        }
        // Keep the sum at 1.0 or less, like the .bnvib conversion
        float sum = ha + la;
        if (sum > 1) {
            ha /= sum;
            la /= sum;
        }
    }

    void run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long last_send = -1000;
        bool sounding = false;
        var notes = new List<KeyValuePair<int, int>>();
        while (running) {
            bool changed;
            lock (sync) {
                changed = dirty;
                dirty = false;
                if (changed) {
                    notes.Clear();
                    notes.AddRange(held);
                }
            }
            long now = clock.ElapsedMilliseconds;
            if (changed || (sounding && now - last_send >= 25)) {
                double hf, lf;
                float ha, la;
                Choose(notes, volume, bass_on_low, out hf, out ha, out lf, out la);
                send(hf, ha, lf, la);
                last_send = now;
                sounding = notes.Count > 0;
                if (changed) {
                    var sb = new StringBuilder();
                    for (int i = notes.Count - 1; i >= 0 && sb.Length < 40; i--)
                        sb.Append(MidiNotes.Name(notes[i].Key)).Append(' ');
                    now_playing = sb.ToString().Trim();
                    high_note = notes.Count > 0 ? notes[notes.Count - 1].Key : -1;
                    low_note = bass_on_low && notes.Count > 1 ? notes[0].Key : -1;
                }
            }
            Thread.Sleep(1);
        }
    }

    // Encodes and sends one rumble report (0x10) to both sides
    public static void send(double hf, float ha, double lf, float la)
    {
        u8* buf = stackalloc u8[49];
        Jc.memset(buf, 0, 49);
        buf[0] = 0x10;
        buf[1] = (u8)(Jc.timming_byte & 0xF);
        Jc.timming_byte++;
        Encode(hf, ha, lf, la, buf + 2);
        for (int i = 0; i < 4; i++)
            buf[6 + i] = buf[2 + i];
        Jc.hid_write(Jc.handle, buf, 10);
    }

    // HD Rumble encoding of one side (4 bytes). Frequencies: encoded as round(log2(f / 10) * 32),
    // then high band (x - 0x60) * 4, low band x - 0x40. Amplitudes: the HD Rumble Player's tables.
    public static void Encode(double hf, float ha, double lf, float la, u8* out4)
    {
        int hj = amp_index(ha), lj = amp_index(la);
        if (hj == 0 && lj == 0) {
            out4[0] = 0x00; out4[1] = 0x01; out4[2] = 0x40; out4[3] = 0x40; // Silence (like jctool's)
            return;
        }
        int hf_code = hf > 0 ? (int)Math.Round(Math.Log(hf / 10, 2) * 32) : 0x60 + 0x60; // 320 Hz when off
        int lf_code = lf > 0 ? (int)Math.Round(Math.Log(lf / 10, 2) * 32) : 0x40 + 0x40; // 160 Hz when off
        hf_code = Math.Max(0x60, Math.Min(0xDF, hf_code));
        lf_code = Math.Max(0x40, Math.Min(0xBF, lf_code));
        int hf_raw = (hf_code - 0x60) * 4;
        int lf_raw = lf_code - 0x40;
        out4[0] = (u8)(hf_raw & 0xFF);
        out4[1] = (u8)(((hf_raw >> 8) & 0xFF) + Tables.lut_amp_ha[hj]);
        out4[2] = (u8)(((Tables.lut_amp_la[lj] >> 8) & 0xFF) + lf_raw);
        out4[3] = (u8)(Tables.lut_amp_la[lj] & 0xFF);
    }

    static int amp_index(float amp)
    {
        if (amp <= 0)
            return 0;
        int j;
        for (j = 1; j < 101; j++)
            if (amp < Tables.lut_amp_float[j])
                return j - 1;
        return 100;
    }

    // Decodes the high band frequency of 4 encoded bytes (for the self-test)
    public static double DecodeHighHz(u8* b)
    {
        int hf_raw = b[0] | ((b[1] & 0x01) << 8);
        return 10 * Math.Pow(2, (hf_raw / 4 + 0x60) / 32.0);
    }
}

// Plays a MIDI file's notes into a MidiRumble at their times
public class MidiFilePlayer
{
    readonly MidiRumble rumble;
    Thread thread;
    volatile bool running;
    public volatile float position;     // Seconds

    public MidiFilePlayer(MidiRumble r) { rumble = r; }

    public bool Playing { get { return running; } }

    // part < 0: all parts except drums
    public void Play(MidiSong song, int part)
    {
        Stop();
        var events = new List<Tuple<double, bool, int, int>>(); // time, on, pitch, velocity
        foreach (var n in song.notes) {
            if (part >= 0 ? n.part != part : song.parts[n.part].drums)
                continue;
            events.Add(Tuple.Create(n.start, true, (int)n.pitch, (int)n.velocity));
            events.Add(Tuple.Create(n.end, false, (int)n.pitch, 0));
        }
        // Offs before ons at the same time, so repeated notes restart
        events.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        running = true;
        position = 0;
        rumble.Start();
        thread = new Thread(() => run(events, song.length)) { IsBackground = true, Name = "MIDI file" };
        thread.Start();
    }

    public void Stop()
    {
        if (thread == null)
            return;
        running = false;
        thread.Join(1000);
        thread = null;
        rumble.Stop();
    }

    void run(List<Tuple<double, bool, int, int>> events, double length)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var held = new Dictionary<int, int>(); // pitch -> count (parts can overlap)
        int i = 0;
        while (running && (i < events.Count)) {
            double now = clock.Elapsed.TotalSeconds;
            position = (float)now;
            while (i < events.Count && events[i].Item1 <= now) {
                var ev = events[i++];
                int count;
                held.TryGetValue(ev.Item3, out count);
                if (ev.Item2) {
                    held[ev.Item3] = count + 1;
                    rumble.NoteOn(ev.Item3, ev.Item4);
                }
                else if (count > 0) {
                    held[ev.Item3] = count - 1;
                    if (count == 1)
                        rumble.NoteOff(ev.Item3);
                }
            }
            if (i < events.Count) {
                double wait = events[i].Item1 - clock.Elapsed.TotalSeconds;
                if (wait > 0.002)
                    Thread.Sleep(Math.Min(20, (int)(wait * 1000) - 1));
            }
        }
        position = (float)length;
        rumble.AllOff();
        Thread.Sleep(30);
        running = false;
    }
}

// MIDI input from an ALSA raw MIDI device (or any file/FIFO that gives MIDI bytes)
public class MidiInput
{
    [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
    [DllImport("libc")] static extern int close(int fd);
    [DllImport("libc")] static extern IntPtr read(int fd, byte[] buf, IntPtr count);
    [StructLayout(LayoutKind.Sequential)] struct pollfd { public int fd; public short events, revents; }
    [DllImport("libc")] static extern int poll([In, Out] pollfd[] fds, UIntPtr nfds, int timeout);
    const int O_RDONLY = 0, O_NONBLOCK = 0x800;

    readonly MidiRumble rumble;
    Thread thread;
    volatile bool running;
    int fd = -1;
    public volatile int channel = -1;    // -1: all
    public volatile string error = "";
    public volatile int messages;

    public MidiInput(MidiRumble r) { rumble = r; }

    public bool Listening { get { return running; } }

    // Raw MIDI devices: path and a readable name
    public static List<KeyValuePair<string, string>> Devices()
    {
        var list = new List<KeyValuePair<string, string>>();
        var card_names = new Dictionary<string, string>();
        try {
            // " 1 [Keystation     ]: USB-Audio - Keystation 49 MK3"
            foreach (string line in File.ReadAllLines("/proc/asound/cards")) {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)\s+\[[^\]]*\]:\s*[^-]*-\s*(.+)$");
                if (m.Success)
                    card_names[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
        }
        catch (Exception) { }
        try {
            foreach (string path in Directory.GetFiles("/dev/snd", "midiC*D*")) {
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"midiC(\d+)D(\d+)");
                string card = m.Groups[1].Value, dev = m.Groups[2].Value;
                string name;
                if (!card_names.TryGetValue(card, out name))
                    name = "Card " + card;
                list.Add(new KeyValuePair<string, string>(path, name + (dev != "0" ? " (port " + dev + ")" : "")));
            }
        }
        catch (Exception) { }
        list.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.Ordinal));
        return list;
    }

    public bool Listen(string path)
    {
        Stop();
        error = "";
        fd = open(path, O_RDONLY | O_NONBLOCK);
        if (fd < 0) {
            int errno = Marshal.GetLastWin32Error();
            error = errno == 13 ? "No permission to open " + path + " (add yourself to the audio group)" : "Can't open " + path + " (error " + errno + ")";
            return false;
        }
        messages = 0;
        running = true;
        rumble.Start();
        thread = new Thread(run) { IsBackground = true, Name = "MIDI input" };
        thread.Start();
        return true;
    }

    public void Stop()
    {
        if (thread == null)
            return;
        running = false;
        thread.Join(500);
        thread = null;
        close(fd);
        fd = -1;
        rumble.Stop();
    }

    void run()
    {
        var fds = new[] { new pollfd { fd = fd, events = 1 } };
        var buf = new byte[256];
        var parser = new MidiParser(this);
        while (running) {
            fds[0].revents = 0;
            if (poll(fds, (UIntPtr)1, 50) <= 0)
                continue;
            long n = (long)read(fd, buf, (IntPtr)buf.Length);
            if (n <= 0) {
                if ((fds[0].revents & 0x18) != 0) {   // POLLHUP / POLLERR: unplugged
                    error = "The MIDI device was disconnected.";
                    rumble.AllOff();
                    break;
                }
                Thread.Sleep(5);                     // FIFO without a writer
                continue;
            }
            for (int i = 0; i < n; i++)
                parser.Byte(buf[i]);
        }
    }

    // MIDI byte stream (running status, real-time bytes in between)
    internal class MidiParser
    {
        readonly MidiInput input;
        int status, need, have, d1;

        public MidiParser(MidiInput i) { input = i; }

        public void Byte(byte b)
        {
            if (b >= 0xF8)
                return;                  // Real-time (clock, active sensing): ignore
            if ((b & 0x80) != 0) {
                status = b >= 0xF0 ? 0 : b;   // SysEx/common: ignore until the next status
                int kind = b & 0xF0;
                need = (kind == 0xC0 || kind == 0xD0) ? 1 : 2;
                have = 0;
                return;
            }
            if (status == 0)
                return;
            if (have == 0 && need == 2) {
                d1 = b;
                have = 1;
                return;
            }
            handle(status, have == 1 ? d1 : b, have == 1 ? b : 0);
            have = 0;
        }

        void handle(int st, int a, int b)
        {
            int ch = st & 0x0F, kind = st & 0xF0;
            if (input.channel >= 0 && ch != input.channel)
                return;
            input.messages++;
            var r = input.rumble;
            if (kind == 0x90)
                r.NoteOn(a, b);
            else if (kind == 0x80)
                r.NoteOff(a);
            else if (kind == 0xB0 && a == 64)
                r.Sustain(b >= 64);
            else if (kind == 0xB0 && (a == 120 || a == 123))
                r.AllOff();
        }
    }
}

}

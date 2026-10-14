// HD Rumble: a tester for both bands, and .jcvib / .bnvib playback (ops.VibFile)
#include <math.h>
#include "common.h"
#include "tables.h"

typedef struct {
    HidVibrationDeviceHandle handles[2];
    int count;          // Handles in use
    int first;          // The first one for this controller
} Vib;

static bool vib_open(const Ctrl *c, Vib *v) {
    u32 style = ctrl_style(c);
    if (!style)
        return false;
    bool pair = style == HidNpadStyleTag_NpadJoyDual || style == HidNpadStyleTag_NpadHandheld;
    int total = (style == HidNpadStyleTag_NpadJoyLeft || style == HidNpadStyleTag_NpadJoyRight) ? 1 : 2;
    if (R_FAILED(hidInitializeVibrationDevices(v->handles, total, c->npad, style)))
        return false;
    if (pair) {              // [0] left Joy-Con, [1] right: only this controller's
        v->first = c->type == TYPE_RIGHT ? 1 : 0;
        v->count = 1;
    } else {                 // Pro Controller: both motors; a single Joy-Con: its one
        v->first = 0;
        v->count = total;
    }
    return true;
}

static void vib_send(const Vib *v, const HidVibrationValue *value) {
    HidVibrationValue values[2] = {*value, *value};
    hidSendVibrationValues(v->handles + v->first, values, v->count);
}

static void vib_stop(const Vib *v) {
    HidVibrationValue off = {0.0f, 160.0f, 0.0f, 320.0f};
    vib_send(v, &off);
}

static void tester(const Ctrl *c, const Vib *v) {
    static const char *NAMES[] = {"Low band frequency", "Low band amplitude", "High band frequency", "High band amplitude"};
    float val[4] = {160.0f, 0.5f, 320.0f, 0.5f};
    const float lo[4] = {40.0f, 0.0f, 80.0f, 0.0f}, hi[4] = {626.0f, 1.0f, 1252.0f, 1.0f};
    int sel = 0;
    bool redraw = true, on = false;
    while (!g_quit) {
        if (redraw) {
            ui_clear("HD Rumble tester");
            printf("Hold A to feel it. Both bands together: keep their amplitudes' sum at 1.0 or less.\n\n");
            for (int i = 0; i < 4; i++)
                printf(i == sel ? CONSOLE_GREEN "> %-22s %8.2f%s\n" CONSOLE_RESET : "  %-22s %8.2f%s\n", NAMES[i], val[i],
                       i % 2 ? "" : " Hz");
            ui_footer("Up/Down: choose   Left/Right: change (L/R: faster)   A (hold): vibrate   B: back");
            redraw = false;
        }
        u64 k = ui_poll(), held = ui_held();
        float step = (sel % 2) ? 0.05f : 10.0f;
        if (held & (HidNpadButton_L | HidNpadButton_R))
            step *= 5;
        if (k & HidNpadButton_AnyUp) sel = (sel + 3) % 4, redraw = true;
        if (k & HidNpadButton_AnyDown) sel = (sel + 1) % 4, redraw = true;
        if (k & HidNpadButton_AnyLeft) val[sel] -= step, redraw = true;
        if (k & HidNpadButton_AnyRight) val[sel] += step, redraw = true;
        if (val[sel] < lo[sel]) val[sel] = lo[sel];
        if (val[sel] > hi[sel]) val[sel] = hi[sel];
        if (k & HidNpadButton_B) break;
        bool want = held & HidNpadButton_A;
        if (want) {
            HidVibrationValue value = {val[1], val[0], val[3], val[2]};
            vib_send(v, &value);
        } else if (on) {
            vib_stop(v);
        }
        on = want;
    }
    vib_stop(v);
    (void)c;
}

// ----------------------------------------------------------------------------------------
// Files
typedef struct {
    u8 *data;
    size_t size;
    int type;            // 1 raw (.jcvib), 2 binary, 3 loop, 4 loop and wait (.bnvib)
    u32 rate_ms;         // Per sample
    u32 samples, loop_start, loop_end, loop_wait, offset;
} VibFile;

static const char *vib_parse(VibFile *v) {
    const u8 *f = v->data;
    if (v->size < 0x18)
        return "Unknown format";
    if (f[0] == 0x52 && f[1] == 0x52 && f[2] == 0x41 && f[3] == 0x57) {
        v->type = 1;
        v->rate_ms = (f[4] << 8) + f[5];
        v->samples = (u32)f[6] << 24 | f[7] << 16 | f[8] << 8 | f[9];
    } else if (f[4] == 0x03) {
        u32 rate = f[6] | (f[7] << 8);
        v->rate_ms = rate ? 1000 / rate : 0;
        u32 size = 0;
#define LE32(o) ((u32)f[o] | (u32)f[o + 1] << 8 | (u32)f[o + 2] << 16 | (u32)f[o + 3] << 24)
        if (f[0] == 0x04) {
            v->type = 2;
            size = LE32(0x8);
        } else if (f[0] == 0x0C) {
            v->type = 3;
            size = LE32(0x10);
            v->loop_start = LE32(0x8);
            v->loop_end = LE32(0xC);
        } else if (f[0] == 0x10) {
            v->type = 4;
            size = LE32(0x14);
            v->loop_start = LE32(0x8);
            v->loop_end = LE32(0xC);
            v->loop_wait = LE32(0x10);
        }
        v->samples = size / 4;
    }
    if (!v->type || !v->rate_ms)
        return "Unknown format";
    static const u32 OFFSETS[] = {0, 0xA, 0xC, 0x14, 0x18};
    v->offset = OFFSETS[v->type];
    if (v->offset + (u64)v->samples * 4 > v->size || v->loop_start > v->loop_end || v->loop_end > v->samples)
        return "The file is shorter than its header says, or its loop points are invalid";
    return NULL;
}

static float amp_ha(u8 code) {
    for (int j = 0; j < 101; j++)
        if (AMP_HA[j] == code)
            return AMP_FLOAT[j];
    return 0.0f;
}

static float amp_la(u16 code) {
    for (int j = 0; j < 101; j++)
        if (AMP_LA[j] == code)
            return AMP_FLOAT[j];
    return 0.0f;
}

// Encoded frequency (round(log2(f / 10) * 32)) to Hz
static float enc_freq(int e) {
    return 10.0f * powf(2.0f, e / 32.0f);
}

static HidVibrationValue vib_sample(const VibFile *v, u32 i) {
    const u8 *s = v->data + v->offset + i * 4;
    HidVibrationValue out;
    if (v->type == 1) {      // Raw HD Rumble bytes (the desktop app's encoding)
        int hf = s[0] | ((s[1] & 0x01) << 8);
        out.freq_high = enc_freq(hf / 4 + 0x60);
        out.amp_high = amp_ha(s[1] & 0xFE);
        out.freq_low = enc_freq((s[2] & 0x7F) + 0x40);
        out.amp_low = amp_la(((s[2] & 0x80) << 8) | s[3]);
    } else {                 // Binary: low amp, low freq, high amp, high freq
        out.amp_low = s[0] / 255.0f;
        out.freq_low = enc_freq(s[1]);
        out.amp_high = s[2] / 255.0f;
        out.freq_high = enc_freq(s[3]);
        float sum = out.amp_low + out.amp_high;      // The desktop app's safe limit
        if (sum > 1.0f) {
            out.amp_low /= sum;
            out.amp_high /= sum;
        }
    }
    return out;
}

static bool play_range(const Vib *v, const VibFile *f, u32 from, u32 to) {
    for (u32 i = from; i < to; i++) {
        HidVibrationValue value = vib_sample(f, i);
        vib_send(v, &value);
        svcSleepThread((u64)f->rate_ms * 1000000ULL);
        padUpdate(&g_pad);
        if (padGetButtonsDown(&g_pad) & HidNpadButton_B || !appletMainLoop())
            return false;
    }
    return true;
}

static void play_file(const Ctrl *c, const Vib *v) {
    static const char *const EXTS[] = {".jcvib", ".bnvib"};
    static char names[64][256];
    const char *items[64];
    int n = list_files(EXTS, 2, names, 64);
    if (!n) {
        ui_clear("HD Rumble");
        ui_message("No HD Rumble files (.jcvib, .bnvib) in " JC_DIR ".");
        return;
    }
    for (int i = 0; i < n; i++)
        items[i] = names[i];
    int sel = 0;
    while (!g_quit && (sel = ui_menu("Play an HD Rumble file", NULL, items, n, sel)) >= 0) {
        char path[300];
        snprintf(path, sizeof(path), JC_DIR "/%s", names[sel]);
        VibFile f = {0};
        FILE *fp = fopen(path, "rb");
        if (fp) {
            fseek(fp, 0, SEEK_END);
            long size = ftell(fp);
            fseek(fp, 0, SEEK_SET);
            if (size > 0 && size < 16 * 1024 * 1024 && (f.data = malloc(size)))
                f.size = fread(f.data, 1, size, fp);
            fclose(fp);
        }
        ui_clear("HD Rumble");
        const char *err = f.data ? vib_parse(&f) : "Couldn't read the file";
        if (err) {
            ui_message(CONSOLE_RED "%s" CONSOLE_RESET, err);
            free(f.data);
            continue;
        }
        static const char *TYPES[] = {"", "Raw HD Rumble", "Binary HD Rumble", "Loop Binary HD Rumble", "Loop and Wait Binary"};
        printf("%s\n%s, %u samples, %.2fs\n\nPlaying...", names[sel], TYPES[f.type], f.samples,
               f.rate_ms * f.samples / 1000.0f);
        ui_footer("B: stop");
        consoleUpdate(NULL);
        bool go = true;
        if (f.type <= 2) {
            go = play_range(v, &f, 0, f.samples);
        } else {
            go = play_range(v, &f, 0, f.loop_start) && play_range(v, &f, f.loop_start, f.loop_end);
            if (go) {
                vib_stop(v);
                svcSleepThread((u64)f.loop_wait * f.rate_ms * 1000000ULL);
                go = play_range(v, &f, f.loop_end, f.samples);
            }
        }
        vib_stop(v);
        free(f.data);
    }
    (void)c;
}

void feat_rumble(Ctrl *c) {
    Vib v;
    if (!vib_open(c, &v)) {
        ui_clear("HD Rumble");
        ui_message(CONSOLE_RED "This controller isn't in use by the Switch right now." CONSOLE_RESET
                   "\n\nPress a button on it, then try again.");
        return;
    }
    static const char *const ITEMS[] = {"Tester (frequencies and amplitudes)", "Play a file (.jcvib, .bnvib)"};
    int sel = 0;
    while (!g_quit && (sel = ui_menu("HD Rumble", NULL, ITEMS, 2, sel)) >= 0) {
        if (sel == 0)
            tester(c, &v);
        else
            play_file(c, &v);
    }
}

// The features that read and write the SPI flash (layout: jctool/ops.py, calibration.py)
#include "common.h"

static const u8 CAL_MAGIC[2] = {0xB2, 0xA1};

static bool failed(Result rc, const char *what) {
    if (R_SUCCEEDED(rc))
        return false;
    ui_message(CONSOLE_RED "%s failed: %s" CONSOLE_RESET "\n\nIs the controller still connected?", what, rc_text(rc));
    return true;
}

// ----------------------------------------------------------------------------------------
// Device info
static const char *BATTERY_LEVELS[] = {"Empty", "Low", "Medium", "Good", "Full"};

void feat_info(Ctrl *c) {
    char mac[24];
    ui_clear("Device info");
    ctrl_mac_text(c, mac);
    printf("Type:        %s\n", ctrl_type_name(c->type));
    printf("Connection:  %s\n", ctrl_iface_name(c->iface));
    printf("MAC:         %s\n", mac);
    printf("S/N:         %s\n", c->sn[0] ? c->sn : "(none)");
    if (c->npad == HidNpadIdType_Handheld && c->has_npad)
        printf("Player:      Handheld\n");
    else if (c->has_npad)
        printf("Player:      %d\n", c->npad + 1);
    HidPowerInfo p;
    if (ctrl_battery(c, &p))
        printf("Battery:     %s%s\n", BATTERY_LEVELS[p.battery_level > 4 ? 4 : p.battery_level],
               p.is_charging ? ", charging" : "");
    u8 colors[12];
    Result rc = spi_read(c, 0x6050, colors, sizeof(colors));
    if (R_SUCCEEDED(rc)) {
        printf("\nBody:        #%02X%02X%02X ", colors[0], colors[1], colors[2]);
        ui_swatch(colors);
        printf("\nButtons:     #%02X%02X%02X ", colors[3], colors[4], colors[5]);
        ui_swatch(colors + 3);
        if (c->type == TYPE_PRO) {
            printf("\nLeft grip:   #%02X%02X%02X ", colors[6], colors[7], colors[8]);
            ui_swatch(colors + 6);
            printf("\nRight grip:  #%02X%02X%02X ", colors[9], colors[10], colors[11]);
            ui_swatch(colors + 9);
        }
        printf("\n");
    } else {
        printf("\nSPI flash: %s\n", rc_text(rc));
    }
    u8 user[22], sensor[2];
    if (R_SUCCEEDED(spi_read(c, 0x8010, user, sizeof(user))) && R_SUCCEEDED(spi_read(c, 0x8026, sensor, 2))) {
        bool l = c->type != TYPE_RIGHT && !memcmp(user, CAL_MAGIC, 2);
        bool r = c->type != TYPE_LEFT && !memcmp(user + 11, CAL_MAGIC, 2);
        bool m = !memcmp(sensor, CAL_MAGIC, 2);
        printf("\nCalibration: %s%s%s%s\n", !(l || r || m) ? "Factory" : "User:",
               l ? (c->type == TYPE_PRO ? " L stick" : " stick") : "", r ? (c->type == TYPE_PRO ? " R stick" : " stick") : "",
               m ? " motion" : "");
    }
    ui_message("");
}

// ----------------------------------------------------------------------------------------
// Colors: presets (jctool/presets.py), the user's own (colors.xml) and an RGB editor
typedef struct {
    const char *name;
    u8 a[3], b[3];
} Preset;

static const Preset RETAIL[] = {
    {"Grey", {0x82, 0x82, 0x82}, {0x0F, 0x0F, 0x0F}},
    {"Neon Blue", {0x0A, 0xB9, 0xE6}, {0x00, 0x1E, 0x1E}},
    {"Neon Red", {0xFF, 0x3C, 0x28}, {0x1E, 0x0A, 0x0A}},
    {"Neon Yellow", {0xE6, 0xFF, 0x00}, {0x14, 0x28, 0x00}},
    {"Neon Pink", {0xFF, 0x32, 0x78}, {0x28, 0x00, 0x1E}},
    {"Neon Green", {0x1E, 0xDC, 0x00}, {0x00, 0x28, 0x00}},
    {"Red", {0xE1, 0x0F, 0x00}, {0x28, 0x0A, 0x0A}},
    {"Pro Black", {0x32, 0x32, 0x32}, {0xFF, 0xFF, 0xFF}},
    {"Pastel Pink", {0xFF, 0xAF, 0xAF}, {0x37, 0x2D, 0x2D}},
    {"Pastel Yellow", {0xF5, 0xFF, 0x82}, {0x32, 0x33, 0x2D}},
    {"Pastel Purple", {0xF0, 0xCB, 0xEB}, {0x37, 0x30, 0x37}},
    {"Pastel Green", {0xBC, 0xFF, 0xC8}, {0x2D, 0x32, 0x2D}},
};
static const Preset GRIPS[] = {
    {"Normal Grips", {0x46, 0x46, 0x46}, {0x46, 0x46, 0x46}},
    {"Xenoblade Grips", {0xDD, 0x3B, 0x64}, {0xDD, 0x3B, 0x64}},
    {"Splatoon Grips", {0x1E, 0xDC, 0x00}, {0xFF, 0x32, 0x78}},
};
#define N_RETAIL (int)(sizeof(RETAIL) / sizeof(RETAIL[0]))
#define N_GRIPS (int)(sizeof(GRIPS) / sizeof(GRIPS[0]))

#define MAX_USER 16
#define COLORS_XML JC_DIR "/colors.xml"
typedef struct {
    char name[64];
    u8 rgb[3];
} UserColor;

// The RC elements of the original's colors.xml: rgb_color="RRGGBB" tooltip="name"
static int load_user_colors(UserColor *out) {
    FILE *f = fopen(COLORS_XML, "r");
    if (!f)
        return 0;
    char line[512];
    int n = 0;
    while (fgets(line, sizeof(line), f) && n < MAX_USER) {
        char *rgb = strstr(line, "rgb_color=\"");
        if (!strstr(line, "<RC") || !rgb)
            continue;
        unsigned v;
        if (sscanf(rgb + 11, "%6x", &v) != 1)
            continue;
        out[n].rgb[0] = v >> 16;
        out[n].rgb[1] = v >> 8;
        out[n].rgb[2] = v;
        strcpy(out[n].name, "Custom");
        char *tip = strstr(line, "tooltip=\"");
        if (tip) {
            tip += 9;
            char *end = strchr(tip, '"');
            if (end && end - tip < 63) {
                memcpy(out[n].name, tip, end - tip);
                out[n].name[end - tip] = 0;
            }
        }
        n++;
    }
    fclose(f);
    return n;
}

static bool save_user_colors(const UserColor *list, int n) {
    if (!ensure_dir())
        return false;
    FILE *f = fopen(COLORS_XML, "w");
    if (!f)
        return false;
    fprintf(f, "<?xml version='1.0' encoding='utf-8'?>\n<configuration>\n\t<!--Values are in RGB-->\n\t<RetailColors>\n");
    int first = n > MAX_USER ? n - MAX_USER : 0;
    for (int i = first; i < n; i++)
        fprintf(f, "\t\t<RC key=\"retailpreset_%02d\" rgb_color=\"%02X%02X%02X\" tooltip=\"%s\" />\n", i - first + 1,
                list[i].rgb[0], list[i].rgb[1], list[i].rgb[2], list[i].name);
    fprintf(f, "\t</RetailColors>\n</configuration>\n");
    fclose(f);
    return true;
}

static void save_user_color(const u8 rgb[3]) {
    UserColor list[MAX_USER + 1];
    char name[64] = "";
    int n = load_user_colors(list);
    snprintf(name, sizeof(name), "Custom %d", n + 1);
    char typed[64] = "";
    if (ui_text("Name for this color", name, typed, sizeof(typed), 40))
        strcpy(name, typed);
    for (char *p = name; *p; p++)        // Keeps the XML simple
        if (*p == '"' || *p == '<' || *p == '>' || *p == '&')
            *p = '_';
    int out = 0;
    for (int i = 0; i < n; i++)
        if (strcmp(list[i].name, name))
            list[out++] = list[i];
    strcpy(list[out].name, name);
    memcpy(list[out].rgb, rgb, 3);
    out++;
    if (out > MAX_USER) {
        memmove(list, list + 1, sizeof(UserColor) * (out - 1));
        out--;
    }
    if (!save_user_colors(list, out))
        ui_message(CONSOLE_RED "Couldn't write " COLORS_XML CONSOLE_RESET);
}

// Preset picker for one slot pair: body+buttons (retail and the user's), or the grips
static bool pick_preset(Ctrl *c, bool grips, u8 colors[12], int slot) {
    const char *items[N_RETAIL + MAX_USER + 1];
    char labels[MAX_USER][80];
    UserColor user[MAX_USER];
    int n = 0, n_user = 0;
    if (grips) {
        for (int i = 0; i < N_GRIPS; i++)
            items[n++] = GRIPS[i].name;
    } else {
        for (int i = 0; i < N_RETAIL; i++)
            items[n++] = RETAIL[i].name;
        n_user = load_user_colors(user);
        for (int i = 0; i < n_user; i++) {
            snprintf(labels[i], sizeof(labels[i]), "%.40s (#%02X%02X%02X, yours: sets the %s)", user[i].name, user[i].rgb[0],
                     user[i].rgb[1], user[i].rgb[2], slot == 1 ? "buttons" : "body");
            items[n++] = labels[i];
        }
    }
    int sel = ui_menu(grips ? "Grip colors" : "Retail colors", NULL, items, n, 0);
    if (sel < 0)
        return false;
    if (grips) {
        memcpy(colors + 6, GRIPS[sel].a, 3);
        memcpy(colors + 9, GRIPS[sel].b, 3);
    } else if (sel < N_RETAIL) {
        memcpy(colors, RETAIL[sel].a, 3);
        memcpy(colors + 3, RETAIL[sel].b, 3);
    } else {
        memcpy(colors + (slot == 1 ? 3 : 0), user[sel - N_RETAIL].rgb, 3);
    }
    (void)c;
    return true;
}

static void remove_user_color(void) {
    UserColor list[MAX_USER];
    const char *items[MAX_USER];
    int n = load_user_colors(list);
    if (!n) {
        ui_clear("Colors");
        ui_message("You have no saved colors.");
        return;
    }
    for (int i = 0; i < n; i++)
        items[i] = list[i].name;
    int sel = ui_menu("Remove a saved color", NULL, items, n, 0);
    if (sel < 0)
        return;
    memmove(list + sel, list + sel + 1, sizeof(UserColor) * (n - sel - 1));
    save_user_colors(list, n - 1);
}

void feat_colors(Ctrl *c) {
    static const char *SLOTS[] = {"Body", "Buttons", "Left grip", "Right grip"};
    u8 colors[12], orig[12];
    ui_clear("Colors");
    printf("Reading...\n");
    if (failed(spi_read(c, 0x6050, colors, 12), "Reading the colors"))
        return;
    memcpy(orig, colors, 12);
    int slots = c->type == TYPE_PRO ? 4 : 2, slot = 0, ch = 0;
    bool redraw = true;
    while (!g_quit) {
        if (redraw) {
            ui_clear("Colors");
            printf("Edit the colors, then write them with A. The Switch shows new colors after the\n"
                   "controller reconnects.\n\n");
            for (int i = 0; i < slots; i++) {
                u8 *rgb = colors + i * 3;
                printf("%s %-11s ", i == slot ? CONSOLE_GREEN ">" CONSOLE_RESET : " ", SLOTS[i]);
                ui_swatch(rgb);
                printf("  ");
                for (int j = 0; j < 3; j++)
                    printf(i == slot && j == ch ? CONSOLE_GREEN "[%c %3d]" CONSOLE_RESET : " %c %3d ", "RGB"[j], rgb[j]);
                printf("   #%02X%02X%02X%s\n\n", rgb[0], rgb[1], rgb[2], memcmp(rgb, orig + i * 3, 3) ? "  (changed)" : "");
            }
            printf("\nUp/Down: color   Left/Right: R, G or B   L/R: -1/+1   ZL/ZR: -16/+16\n"
                   "Y: presets   X: save this color as yours   -: remove one of yours\n");
            ui_footer("A: write to the controller   B: back (without writing)");
            redraw = false;
        }
        u64 k = ui_poll();
        if (!k)
            continue;
        redraw = true;
        u8 *v = &colors[slot * 3 + ch];
        if (k & HidNpadButton_AnyUp) slot = (slot + slots - 1) % slots;
        else if (k & HidNpadButton_AnyDown) slot = (slot + 1) % slots;
        else if (k & HidNpadButton_AnyLeft) ch = (ch + 2) % 3;
        else if (k & HidNpadButton_AnyRight) ch = (ch + 1) % 3;
        else if (k & HidNpadButton_L) *v = *v > 0 ? *v - 1 : 0;
        else if (k & HidNpadButton_R) *v = *v < 255 ? *v + 1 : 255;
        else if (k & HidNpadButton_ZL) *v = *v > 16 ? *v - 16 : 0;
        else if (k & HidNpadButton_ZR) *v = *v < 239 ? *v + 16 : 255;
        else if (k & HidNpadButton_Y) pick_preset(c, slot >= 2, colors, slot);
        else if (k & HidNpadButton_X) save_user_color(colors + slot * 3);
        else if (k & HidNpadButton_Minus) remove_user_color();
        else if (k & HidNpadButton_B) return;
        else if (k & HidNpadButton_A) {
            ui_clear("Colors");
            if (!ui_confirm("Write these colors to the %s?", ctrl_type_name(c->type)))
                continue;
            printf("\nWriting...\n");
            consoleUpdate(NULL);
            if (failed(spi_write(c, 0x6050, colors, slots * 3), "Writing the colors"))
                continue;
            memcpy(orig, colors, 12);
            ui_message(CONSOLE_GREEN "Done." CONSOLE_RESET " Reconnect the controller to see them in the Switch's menus.");
        } else {
            redraw = false;
        }
    }
}

// ----------------------------------------------------------------------------------------
// SPI backup and restore (the desktop app's spi_<kind>_<MAC>.bin)
#define SPI_SIZE 0x80000

static void backup_name(const Ctrl *c, char *out, size_t size) {
    const char *kind = c->type == TYPE_LEFT ? "left_" : c->type == TYPE_RIGHT ? "right_" : "pro_";
    snprintf(out, size, JC_DIR "/spi_%s%02X%02X%02X%02X%02X%02X.bin", kind, c->mac[0], c->mac[1], c->mac[2], c->mac[3],
             c->mac[4], c->mac[5]);
}

void feat_backup(Ctrl *c) {
    char path[256];
    ui_clear("Back up the SPI flash");
    backup_name(c, path, sizeof(path));
    if (!ui_confirm("Back up all 512KB of the %s's SPI flash to\n%s?\n\nIt takes a few minutes. "
                    "Keep the controller close.", ctrl_type_name(c->type), path))
        return;
    if (!ensure_dir()) {
        ui_message(CONSOLE_RED "Couldn't create " JC_DIR CONSOLE_RESET);
        return;
    }
    u8 *data = malloc(SPI_SIZE);
    if (!data)
        return;
    const u32 step = 0x1000;
    for (u32 off = 0; off < SPI_SIZE && !g_quit; off += step) {
        ui_at(10, 1);
        printf("\x1b[2KReading %3uKB of 512KB  [", off / 1024);
        for (u32 i = 0; i < 40; i++)
            printf(i < off * 40 / SPI_SIZE ? "#" : ".");
        printf("]");
        ui_footer("B: cancel");
        if (ui_poll() & HidNpadButton_B) {
            free(data);
            ui_message("Cancelled.");
            return;
        }
        if (failed(spi_read(c, off, data + off, step), "Reading the SPI flash")) {
            free(data);
            return;
        }
    }
    FILE *f = fopen(path, "wb");
    bool ok = f && fwrite(data, 1, SPI_SIZE, f) == SPI_SIZE;
    if (f)
        fclose(f);
    free(data);
    if (ok)
        ui_message(CONSOLE_GREEN "\nSaved %s" CONSOLE_RESET "\n\nKeep a copy on your computer: the desktop app restores it too.", path);
    else
        ui_message(CONSOLE_RED "\nCouldn't write %s" CONSOLE_RESET, path);
}

static const u8 VALIDATION[20] = {0x01, 0x08, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x08, 0xC0, 0x5D,
                                  0x89, 0xFD, 0x04, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x40, 0x06};
static const u8 FW_MAGIC[6] = {0x0A, 0xFB, 0x00, 0x00, 0x02, 0x0D};
static const u8 OTA_MAGIC[8] = {0xAA, 0x55, 0xF0, 0x0F, 0x68, 0xE5, 0x97, 0xD2};

// The desktop app's Backup.check: NULL when it fits this controller
static const char *check_backup(const Ctrl *c, const u8 *d, size_t size, bool *same_mac) {
    *same_mac = false;
    if (size != SPI_SIZE)
        return "Partial backup! The file size must be 512KB (524288 bytes).";
    if (d[0x6012] != c->type || d[0x6013] != 0xA0)
        return "Wrong backup! It's from another type of controller.";
    bool valid = !memcmp(d, VALIDATION, 20);
    bool ota = !memcmp(d + 0x1FF4 + 6, OTA_MAGIC, 8);
    for (int i = 0; i < 6; i++) {
        if (i == 1)
            continue;
        if (d[0x10000 + i] != FW_MAGIC[i] || (ota && d[0x28000 + i] != FW_MAGIC[i]))
            valid = false;
    }
    if (!valid)
        return "Corrupt backup! Please try another backup.";
    *same_mac = true;
    for (int i = 0; i < 6; i++)                // Stored reversed
        if (d[0x1A - i] != c->mac[i])
            *same_mac = false;
    return NULL;
}

static Result write_progress(const Ctrl *c, u32 offset, const u8 *data, size_t size, const char *what) {
    for (size_t done = 0; done < size; done += 0x100) {
        size_t n = size - done < 0x100 ? size - done : 0x100;
        ui_at(12, 1);
        printf("\x1b[2K%s: %zu of %zu bytes", what, done, size);
        consoleUpdate(NULL);
        Result rc = spi_write(c, offset + done, data + done, n);
        if (R_FAILED(rc))
            return rc;
    }
    return 0;
}

void feat_restore(Ctrl *c) {
    static const char *const EXTS[] = {".bin"};
    static char names[64][256];
    const char *items[64];
    int n = list_files(EXTS, 1, names, 64);
    if (!n) {
        ui_clear("Restore");
        ui_message("No backups in " JC_DIR ".\n\nBack up a controller first, or copy a backup (spi_*.bin) there.");
        return;
    }
    for (int i = 0; i < n; i++)
        items[i] = names[i];
    int sel = ui_menu("Restore: choose a backup", NULL, items, n, 0);
    if (sel < 0)
        return;
    char path[300];
    snprintf(path, sizeof(path), JC_DIR "/%s", names[sel]);
    u8 *d = malloc(SPI_SIZE + 1);
    if (!d)
        return;
    FILE *f = fopen(path, "rb");
    size_t size = f ? fread(d, 1, SPI_SIZE + 1, f) : 0;
    if (f)
        fclose(f);
    bool same_mac;
    const char *err = check_backup(c, d, size, &same_mac);
    ui_clear("Restore");
    if (err) {
        ui_message(CONSOLE_RED "%s" CONSOLE_RESET, err);
        free(d);
        return;
    }
    char sub[400];
    snprintf(sub, sizeof(sub), "Backup: %s\nIt's %s.\n", names[sel],
             same_mac ? "from this controller" : CONSOLE_YELLOW "from another controller of this type" CONSOLE_RESET);
    static const char *const OPTIONS[] = {
        "Colors", "Serial number", "User calibration (sticks and motion)",
        "Full restore: factory configuration and calibration, user calibration",
    };
    int opt = ui_menu("Restore", sub, OPTIONS, 4, 0);
    Result rc = 0;
    ui_clear("Restore");
    if (opt == 0 && ui_confirm("Restore the colors?")) {
        rc = spi_write(c, 0x6050, d + 0x6050, 12);
    } else if (opt == 1 && ui_confirm("Restore the serial number (%.16s)?", d + 0x6000)) {
        rc = spi_write(c, 0x6000, d + 0x6000, 0x10);
    } else if (opt == 2 && ui_confirm("Restore the user calibration of the sticks and motion sensors?")) {
        rc = spi_write(c, 0x8010, d + 0x8010, 0x30);
    } else if (opt == 3 && ui_confirm("Write the factory configuration and calibration (0x6000, 4KB) and the user\n"
                                      "calibration (0x8000, 4KB) from the backup?%s\n\nDon't disconnect the controller "
                                      "while it writes.",
                                      same_mac ? "" : CONSOLE_YELLOW "\n\nThe backup is from another controller: this "
                                                      "gives this one its S/N and calibration." CONSOLE_RESET)) {
        rc = write_progress(c, 0x6000, d + 0x6000, 0x1000, "Factory configuration");
        if (R_SUCCEEDED(rc))
            rc = write_progress(c, 0x8000, d + 0x8000, 0x1000, "User calibration");
        if (R_SUCCEEDED(rc)) {
            u8 erase[16];
            memset(erase, 0xFF, sizeof(erase));
            rc = spi_write(c, 0xF000, erase, 16);       // The S/N backup
        }
        if (R_SUCCEEDED(rc)) {
            free(d);
            hidsysDisconnectUniquePad(c->id);
            ui_message(CONSOLE_GREEN "\nRestored." CONSOLE_RESET " The controller was disconnected: reconnect it.");
            return;
        }
    } else {
        free(d);
        return;
    }
    free(d);
    if (!failed(rc, "Restoring"))
        ui_message(CONSOLE_GREEN "Restored." CONSOLE_RESET " Reconnect the controller for the Switch to use it.");
}

// ----------------------------------------------------------------------------------------
// Calibration (readout and back to factory)
typedef struct {
    int x_minus, x_center, x_plus, y_minus, y_center, y_plus;
} StickCal;

static void stick_params(const u8 *d, int *a, int *b) {
    *a = ((d[1] << 8) & 0xF00) | d[0];
    *b = (d[2] << 4) | (d[1] >> 4);
}

static StickCal decode_stick(const u8 *d, bool left) {
    int p[6];
    for (int i = 0; i < 3; i++)
        stick_params(d + i * 3, &p[i * 2], &p[i * 2 + 1]);
    int center = left ? 2 : 0, minus = left ? 4 : 2, plus = left ? 0 : 4;
    StickCal c = {p[center] - p[minus], p[center], p[center] + p[plus],
                  p[center + 1] - p[minus + 1], p[center + 1], p[center + 1] + p[plus + 1]};
    return c;
}

static void print_stick(const char *name, const StickCal *c) {
    if (!c)
        printf("  %s: No calibration\n", name);
    else
        printf("  %s: center (%d, %d)  X [%d - %d]  Y [%d - %d]\n", name, c->x_center, c->y_center, c->x_minus,
               c->x_plus, c->y_minus, c->y_plus);
}

static void print_sensor(const char *name, const u8 *d) {
    s16 v[12];
    for (int i = 0; i < 12; i++)
        v[i] = (s16)u16le(d + i * 2);
    printf("  %s Acc:  origin %6d %6d %6d  sensitivity %6d %6d %6d\n", name, v[0], v[1], v[2], v[3], v[4], v[5]);
    printf("  %s Gyro: origin %6d %6d %6d  sensitivity %6d %6d %6d\n", name, v[6], v[7], v[8], v[9], v[10], v[11]);
}

void feat_calibration(Ctrl *c) {
    while (!g_quit) {
        u8 fstick[18], ustick[22], fsensor[24], usensor[26], model[6], sparams[18], sparams2[18];
        ui_clear("Calibration");
        printf("Reading...\n");
        consoleUpdate(NULL);
        Result rc = spi_read(c, 0x603D, fstick, 18);
        if (R_SUCCEEDED(rc)) rc = spi_read(c, 0x8010, ustick, 22);
        if (R_SUCCEEDED(rc)) rc = spi_read(c, 0x6020, fsensor, 24);
        if (R_SUCCEEDED(rc)) rc = spi_read(c, 0x8026, usensor, 26);
        if (R_SUCCEEDED(rc)) rc = spi_read(c, 0x6080, model, 6);
        if (R_SUCCEEDED(rc)) rc = spi_read(c, 0x6086, sparams, 18);
        if (R_SUCCEEDED(rc)) rc = spi_read(c, 0x6098, sparams2, 18);
        if (failed(rc, "Reading the calibration"))
            return;
        bool has_l = c->type != TYPE_RIGHT, has_r = c->type != TYPE_LEFT;
        bool user_l = has_l && !memcmp(ustick, CAL_MAGIC, 2), user_r = has_r && !memcmp(ustick + 11, CAL_MAGIC, 2);
        bool user_m = !memcmp(usensor, CAL_MAGIC, 2);
        StickCal fl = decode_stick(fstick, true), fr = decode_stick(fstick + 9, false);
        StickCal ul = decode_stick(ustick + 2, true), ur = decode_stick(ustick + 13, false);
        ui_clear("Calibration");
        printf("Stick calibration (factory):\n");
        if (has_l) print_stick("Left stick ", &fl);
        if (has_r) print_stick("Right stick", &fr);
        printf("Stick calibration (user):\n");
        if (has_l) print_stick("Left stick ", user_l ? &ul : NULL);
        if (has_r) print_stick("Right stick", user_r ? &ur : NULL);
        printf("6-axis calibration:\n");
        print_sensor("Factory", fsensor);
        if (user_m) print_sensor("User   ", usensor + 2);
        else printf("  User: No calibration\n");
        int dz, rr;
        printf("Device parameters (factory):\n  Flat surface ACC offset: %04X %04X %04X\n", u16le(model), u16le(model + 2),
               u16le(model + 4));
        stick_params(sparams + 3, &dz, &rr);
        printf("  %s: deadzone %d, range ratio %d\n", c->type == TYPE_PRO ? "Left stick " : "Stick", dz, rr);
        if (c->type == TYPE_PRO) {
            stick_params(sparams2 + 3, &dz, &rr);
            printf("  Right stick: deadzone %d, range ratio %d\n", dz, rr);
        }
        printf("\nA user calibration overrides the factory one. To calibrate the sticks and motion\n"
               "sensors, use System Settings > Controllers and Sensors on the Switch.\n");

        const char *items[3];
        int kinds[3], n = 0;
        if (user_l) { items[n] = c->type == TYPE_PRO ? "Left stick: back to factory" : "Stick: back to factory"; kinds[n++] = 0; }
        if (user_r) { items[n] = c->type == TYPE_PRO ? "Right stick: back to factory" : "Stick: back to factory"; kinds[n++] = 1; }
        if (user_m) { items[n] = "Motion sensors: back to factory"; kinds[n++] = 2; }
        if (!n) {
            ui_message("");
            return;
        }
        int row = 24;
        for (int i = 0; i < n; i++) {
            ui_at(row + i, 1);
            printf("%c: %s", "XYR"[i], items[i]);
        }
        ui_footer("B: back");
        u64 k = ui_wait(HidNpadButton_B | HidNpadButton_X | (n > 1 ? HidNpadButton_Y : 0) | (n > 2 ? HidNpadButton_R : 0));
        int choice = k & HidNpadButton_X ? 0 : k & HidNpadButton_Y ? 1 : k & HidNpadButton_R ? 2 : -1;
        if (choice < 0)
            return;
        ui_clear("Calibration");
        if (!ui_confirm("%s?\nThis erases the user calibration.", items[choice]))
            continue;
        u8 ff[26];
        memset(ff, 0xFF, sizeof(ff));
        if (kinds[choice] == 0) rc = spi_write(c, 0x8010, ff, 11);
        else if (kinds[choice] == 1) rc = spi_write(c, 0x801B, ff, 11);
        else rc = spi_write(c, 0x8026, ff, 26);
        if (!failed(rc, "Erasing"))
            ui_message(CONSOLE_GREEN "Done." CONSOLE_RESET " Reconnect the controller for the Switch to use it.");
    }
}

// ----------------------------------------------------------------------------------------
// Serial number (ops.change_sn / restore_sn_from_controller)
void feat_serial(Ctrl *c) {
    while (!g_quit) {
        u8 sn[16], backup[16];
        ui_clear("Serial number");
        Result rc = spi_read(c, 0x6000, sn, 16);
        if (R_SUCCEEDED(rc))
            rc = spi_read(c, 0xF000, backup, 16);
        if (failed(rc, "Reading the S/N"))
            return;
        char cur[17] = {0}, saved[17] = {0};
        for (int i = 0, j = 0; i < 16; i++)
            if (sn[i] >= 32 && sn[i] <= 126) cur[j++] = sn[i];
        for (int i = 0, j = 0; i < 16; i++)
            if (backup[i] >= 32 && backup[i] <= 126) saved[j++] = backup[i];
        bool has_backup = backup[0] == 0x00;
        printf("S/N:            %s\n", cur[0] ? cur : "(none)");
        printf("Original (kept inside the controller): %s\n\n", has_backup ? saved : "(not changed yet)");
        printf("X: change the S/N\n%s", has_backup ? "Y: restore the original S/N\n" : "");
        ui_footer("B: back");
        u64 k = ui_wait(HidNpadButton_B | HidNpadButton_X | (has_backup ? HidNpadButton_Y : 0));
        if (k & HidNpadButton_B || !k)
            return;
        if (k & HidNpadButton_Y) {
            if (ui_confirm("Restore the original S/N (%s)?", saved) && !failed(spi_write(c, 0x6000, backup, 16), "Writing"))
                ui_message(CONSOLE_GREEN "Done." CONSOLE_RESET);
            continue;
        }
        char text[32] = "";
        if (!ui_text("New S/N (1-15 characters)", cur, text, sizeof(text), 15))
            continue;
        size_t len = strlen(text);
        bool ok = len >= 1 && len <= 15;
        for (size_t i = 0; i < len; i++)
            if (text[i] < 32 || text[i] > 126) ok = false;
        if (!ok) {
            ui_message(CONSOLE_RED "Use 1-15 plain characters." CONSOLE_RESET);
            continue;
        }
        if (!ui_confirm("Change the S/N to %s?", text))
            continue;
        // The first change keeps the original at 0xF000 (when it's a real one)
        if (sn[0] == 0 && sn[1] == 0 && sn[2] == 0x58 && backup[0] == 0xFF)
            rc = spi_write(c, 0xF000, sn, 16);
        u8 out[16] = {0};
        memcpy(out + 16 - len, text, len);
        if (R_SUCCEEDED(rc))
            rc = spi_write(c, 0x6000, out, 16);
        if (!failed(rc, "Writing the S/N"))
            ui_message(CONSOLE_GREEN "Done." CONSOLE_RESET);
    }
}

// ----------------------------------------------------------------------------------------
// SPI viewer (the CLI's debug SPI reads)
void feat_spi_viewer(Ctrl *c) {
    u32 addr = 0;
    u8 data[0x100];
    bool reload = true;
    Result rc = 0;
    while (!g_quit) {
        if (reload) {
            rc = spi_read(c, addr, data, sizeof(data));
            ui_clear("SPI flash viewer");
            if (R_FAILED(rc)) {
                printf("0x%05X: %s\n", addr, rc_text(rc));
            } else {
                printf("Offset   00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F\n");
                for (int r = 0; r < 16; r++) {
                    printf("%05X    ", addr + r * 16);
                    for (int i = 0; i < 16; i++)
                        printf("%02X ", data[r * 16 + i]);
                    printf("  ");
                    for (int i = 0; i < 16; i++) {
                        u8 b = data[r * 16 + i];
                        printf("%c", b >= 32 && b < 127 ? b : '.');
                    }
                    printf("\n");
                }
            }
            printf("\nUp/Down: 0x100   Left/Right: 0x1000   L/R: 0x10000\n\n"
                   "0x6000 S/N, 0x6020 factory motion, 0x603D factory sticks, 0x6050 colors,\n"
                   "0x8010 user sticks, 0x8026 user motion");
            ui_footer("B: back");
            reload = false;
        }
        u64 k = ui_poll();
        s32 delta = 0;
        if (k & HidNpadButton_AnyUp) delta = -0x100;
        else if (k & HidNpadButton_AnyDown) delta = 0x100;
        else if (k & HidNpadButton_AnyLeft) delta = -0x1000;
        else if (k & HidNpadButton_AnyRight) delta = 0x1000;
        else if (k & HidNpadButton_L) delta = -0x10000;
        else if (k & HidNpadButton_R) delta = 0x10000;
        else if (k & HidNpadButton_B) return;
        if (delta) {
            addr = (u32)((s32)addr + delta + SPI_SIZE) % SPI_SIZE;
            reload = true;
        }
    }
}

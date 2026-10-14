#include "common.h"

PadState g_pad;
bool g_quit = false;

u64 ui_poll(void) {
    consoleUpdate(NULL);
    if (!appletMainLoop()) {
        g_quit = true;
        return 0;
    }
    padUpdate(&g_pad);
    return padGetButtonsDown(&g_pad);
}

u64 ui_held(void) {
    return padGetButtons(&g_pad);
}

u64 ui_wait(u64 mask) {
    while (!g_quit) {
        u64 k = ui_poll() & mask;
        if (k)
            return k;
    }
    return 0;
}

void ui_at(int row, int col) {
    printf("\x1b[%d;%dH", row, col);
}

void ui_clear(const char *title) {
    printf("\x1b[2J\x1b[1;1H" CONSOLE_CYAN "Joy-Con Toolkit " APP_VERSION CONSOLE_RESET);
    if (title)
        printf(" - %s", title);
    printf("\n\n");
}

void ui_footer(const char *text) {
    ui_at(ROWS, 1);
    printf("\x1b[2K" CONSOLE_YELLOW "%s" CONSOLE_RESET, text);
}

static void vmessage(const char *fmt, va_list ap) {
    vprintf(fmt, ap);
}

void ui_message(const char *fmt, ...) {
    va_list ap;
    printf("\n");
    va_start(ap, fmt);
    vmessage(fmt, ap);
    va_end(ap);
    ui_footer("A / B: back");
    ui_wait(HidNpadButton_A | HidNpadButton_B);
}

bool ui_confirm(const char *fmt, ...) {
    va_list ap;
    printf("\n");
    va_start(ap, fmt);
    vmessage(fmt, ap);
    va_end(ap);
    ui_footer("A: yes   B: no");
    return ui_wait(HidNpadButton_A | HidNpadButton_B) == HidNpadButton_A;
}

int ui_menu(const char *title, const char *subtitle, const char *const *items, int count, int sel) {
    const int top = 4, height = ROWS - top - 2;
    int first = 0;
    bool redraw = true;
    if (sel < 0 || sel >= count)
        sel = 0;
    while (!g_quit) {
        if (redraw) {
            ui_clear(title);
            if (subtitle)
                printf("%s", subtitle);
            if (sel < first)
                first = sel;
            if (sel >= first + height)
                first = sel - height + 1;
            for (int i = first; i < count && i < first + height; i++) {
                ui_at(top + i - first, 1);
                printf(i == sel ? CONSOLE_GREEN "> %s" CONSOLE_RESET : "  %s", items[i]);
            }
            ui_footer("Up/Down: choose   A: select   B: back");
            redraw = false;
        }
        u64 k = ui_poll();
        if (k & (HidNpadButton_AnyUp)) {
            sel = (sel + count - 1) % count;
            redraw = true;
        } else if (k & (HidNpadButton_AnyDown)) {
            sel = (sel + 1) % count;
            redraw = true;
        } else if (k & HidNpadButton_A) {
            return sel;
        } else if (k & HidNpadButton_B) {
            return -1;
        }
    }
    return -1;
}

bool ui_text(const char *guide, const char *initial, char *out, size_t size, int maxlen) {
    SwkbdConfig kbd;
    if (R_FAILED(swkbdCreate(&kbd, 0)))
        return false;
    swkbdConfigMakePresetDefault(&kbd);
    swkbdConfigSetGuideText(&kbd, guide);
    if (initial)
        swkbdConfigSetInitialText(&kbd, initial);
    swkbdConfigSetStringLenMax(&kbd, maxlen);
    Result rc = swkbdShow(&kbd, out, size);
    swkbdClose(&kbd);
    return R_SUCCEEDED(rc) && out[0];
}

void ui_swatch(const u8 rgb[3]) {
    printf("\x1b[48;2;%d;%d;%dm      " CONSOLE_RESET, rgb[0], rgb[1], rgb[2]);
}

const char *rc_text(Result rc) {
    static char buf[48];
    snprintf(buf, sizeof(buf), "error 2%03d-%04d (0x%X)", R_MODULE(rc), R_DESCRIPTION(rc), rc);
    return buf;
}

bool ensure_dir(void) {
    mkdir("sdmc:/switch", 0777);
    mkdir(JC_DIR, 0777);
    struct stat st;
    return stat(JC_DIR, &st) == 0;
}

static int cmp_names(const void *a, const void *b) {
    return strcasecmp((const char *)a, (const char *)b);
}

int list_files(const char *const *exts, int n_exts, char names[][256], int max) {
    DIR *d = opendir(JC_DIR);
    int n = 0;
    if (!d)
        return 0;
    struct dirent *e;
    while ((e = readdir(d)) && n < max) {
        const char *dot = strrchr(e->d_name, '.');
        if (!dot || strlen(e->d_name) >= 256)
            continue;
        for (int i = 0; i < n_exts; i++) {
            if (!strcasecmp(dot, exts[i])) {
                strcpy(names[n++], e->d_name);
                break;
            }
        }
    }
    closedir(d);
    qsort(names, n, 256, cmp_names);
    return n;
}

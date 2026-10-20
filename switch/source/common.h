// Joy-Con Toolkit for the Nintendo Switch (libnx homebrew).
// The controllers are reached through the system's HID services: hidsys for the list and
// their details, hiddbg for the SPI flash, hid for input, vibration and battery, irs for
// the IR camera and nfc for tags. The SPI layout and the file formats are the desktop
// app's (jctool/ops.py, jctool/calibration.py), so backups and presets work across both.
#pragma once
#include <switch.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <dirent.h>
#include <sys/stat.h>
#include <time.h>

#ifndef APP_VERSION
#define APP_VERSION "dev"
#endif

// Backups, HD Rumble files, IR images and colors.xml
#define JC_DIR "sdmc:/switch/jctool"

// Controller types, as the SPI stores them (0x6012) and the desktop app names them
enum { TYPE_NONE = 0, TYPE_LEFT = 1, TYPE_RIGHT = 2, TYPE_PRO = 3 };

typedef struct {
    HidsysUniquePadId id;
    int type;
    HidNpadInterfaceType iface;
    u8 mac[6];
    char sn[17];
    bool has_npad;
    HidNpadIdType npad;
} Ctrl;

// ui.c: the console, input and the screens every feature uses
#define COLS 80
#define ROWS 45
extern PadState g_pad;
extern bool g_quit;                      // The system asked the app to close
u64 ui_poll(void);                       // Presents the frame, reads input: buttons pressed
u64 ui_held(void);
u64 ui_wait(u64 mask);                   // Until one of these is pressed (0 when quitting)
void ui_clear(const char *title);
void ui_at(int row, int col);
void ui_footer(const char *text);
void ui_message(const char *fmt, ...);   // A screen of text, then waits for A or B
bool ui_confirm(const char *fmt, ...);   // A: yes, B: no
int ui_menu(const char *title, const char *subtitle, const char *const *items, int count, int sel);
bool ui_text(const char *guide, const char *initial, char *out, size_t size, int maxlen);
void ui_swatch(const u8 rgb[3]);         // A block in that color
const char *rc_text(Result rc);

// ctrl.c: the controllers and their SPI flash. SPI operations that don't finish:
#define JC_ERR_TIMEOUT MAKERESULT(Module_Libnx, LibnxError_Timeout)
#define JC_ERR_DISCONNECTED MAKERESULT(Module_Libnx, LibnxError_NotFound)
int ctrl_list(Ctrl *out, int max);
bool ctrl_refresh(Ctrl *c);              // Still connected? (and its current Npad)
const char *ctrl_type_name(int type);
const char *ctrl_iface_name(HidNpadInterfaceType iface);
void ctrl_mac_text(const Ctrl *c, char *out);   // 1C:45:86:...
Result spi_read(const Ctrl *c, u32 offset, void *buf, size_t size);
Result spi_write(const Ctrl *c, u32 offset, const void *data, size_t size);
u32 ctrl_style(const Ctrl *c);           // The Npad's style (one HidNpadStyleTag), 0 if none
bool ctrl_battery(const Ctrl *c, HidPowerInfo *info);

// The features (one screen each)
void feat_info(Ctrl *c);
void feat_colors(Ctrl *c);
void feat_backup(Ctrl *c);
void feat_restore(Ctrl *c);
void feat_calibration(Ctrl *c);
void feat_serial(Ctrl *c);
void feat_buttons(Ctrl *c);
void feat_rumble(Ctrl *c);
void feat_ir(Ctrl *c);
void feat_nfc(Ctrl *c);
void feat_led(Ctrl *c);
void feat_spi_viewer(Ctrl *c);

// Helpers shared by the features
bool ensure_dir(void);
int list_files(const char *const *exts, int n_exts, char names[][256], int max);
static inline u16 u16le(const u8 *d) { return d[0] | (d[1] << 8); }

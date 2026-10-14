// The Joy-Con (R)'s IR camera (a terminal preview like jctool-cli's, and 320x240 captures)
// and its NFC reader
#include "common.h"

#define IR_BUF 0x12C00          // 320x240, 8-bit
static u8 g_ir[IR_BUF];

static const u32 IR_SIZES[][2] = {{320, 240}, {160, 120}, {80, 60}, {40, 30}, {20, 15}};

static void save_bmp(const char *path, const u8 *img, int w, int h) {
    FILE *f = fopen(path, "wb");
    if (!f)
        return;
    u32 row = (w * 3 + 3) & ~3, data = row * h;
    u8 hdr[54] = {'B', 'M'};
    u32 fields[][2] = {{2, 54 + data}, {10, 54}, {14, 40}, {18, w}, {22, h}, {34, data}};
    for (size_t i = 0; i < sizeof(fields) / sizeof(fields[0]); i++)
        memcpy(hdr + fields[i][0], &fields[i][1], 4);
    hdr[26] = 1;
    hdr[28] = 24;
    fwrite(hdr, 1, 54, f);
    u8 line[320 * 3 + 4] = {0};
    for (int y = h - 1; y >= 0; y--) {
        for (int x = 0; x < w; x++)
            memset(line + x * 3, img[y * w + x], 3);
        fwrite(line, 1, row, f);
    }
    fclose(f);
}

static Result ir_start(IrsIrCameraHandle h, IrsImageTransferProcessorConfig *cfg, u32 format) {
    irsStopImageProcessor(h);
    cfg->format = format;
    return irsRunImageTransferProcessor(h, cfg, 0x100000);
}

// The next frame (newer than *sample): true when there is one
static bool ir_frame(IrsIrCameraHandle h, u64 *sample, u32 size, u64 timeout_ms) {
    IrsImageTransferProcessorState st;
    u64 start = armGetSystemTick();
    while (armTicksToNs(armGetSystemTick() - start) < timeout_ms * 1000000ULL) {
        if (R_SUCCEEDED(irsGetImageTransferProcessorState(h, g_ir, size, &st)) && st.sampling_number != *sample) {
            *sample = st.sampling_number;
            return true;
        }
        svcSleepThread(10000000ULL);
    }
    return false;
}

void feat_ir(Ctrl *c) {
    ui_clear("IR camera");
    if (c->type != TYPE_RIGHT) {
        ui_message("Only the Joy-Con (R) has an IR camera.");
        return;
    }
    if (!c->has_npad) {
        ui_message(CONSOLE_RED "This Joy-Con isn't in use by the Switch right now." CONSOLE_RESET);
        return;
    }
    Result rc = irsInitialize();
    IrsIrCameraHandle h;
    if (R_SUCCEEDED(rc))
        rc = irsGetIrCameraHandle(&h, c->npad);
    if (R_FAILED(rc)) {
        ui_message(CONSOLE_RED "The IR camera isn't available: %s" CONSOLE_RESET, rc_text(rc));
        irsExit();
        return;
    }
    IrsImageTransferProcessorConfig cfg;
    irsGetDefaultImageTransferProcessorConfig(&cfg);
    const u32 preview = IrsImageTransferProcessorFormat_80x60;
    rc = ir_start(h, &cfg, preview);
    u64 sample = 0;
    char status[160] = "";
    static const char RAMP[] = " .:-=+*#%@";
    while (!g_quit && R_SUCCEEDED(rc)) {
        if (ir_frame(h, &sample, 80 * 60, 1000)) {
            ui_clear("IR camera");
            for (int y = 0; y < 30; y++) {        // Two rows per line: the characters are tall
                char line[81];
                for (int x = 0; x < 80; x++) {
                    int v = (g_ir[(y * 2) * 80 + x] + g_ir[(y * 2 + 1) * 80 + x]) / 2;
                    line[x] = RAMP[v * 10 / 256];
                }
                line[80] = 0;
                printf("%s", line);
            }
            ui_at(34, 1);
            printf("Exposure %3u us   Gain %2u   LEDs %s   %s\n%s", (u32)(cfg.exposure_time / 1000), cfg.gain,
                   cfg.light_target == 3 ? "off" : "on", cfg.is_negative_image_used ? "Negative" : "",
                   status);
            ui_footer("A: save a 320x240 picture   L/R: exposure   ZL/ZR: gain   X: LEDs   Y: negative   B: back");
        }
        u64 k = ui_poll();
        bool restart = true;
        if (k & HidNpadButton_B)
            break;
        else if (k & HidNpadButton_L) cfg.exposure_time = cfg.exposure_time > 50000 ? cfg.exposure_time - 50000 : 7000;
        else if (k & HidNpadButton_R) cfg.exposure_time = cfg.exposure_time + 50000 < 600000 ? cfg.exposure_time + 50000 : 600000;
        else if (k & HidNpadButton_ZL) cfg.gain = cfg.gain > 1 ? cfg.gain - 1 : 1;
        else if (k & HidNpadButton_ZR) cfg.gain = cfg.gain < 16 ? cfg.gain + 1 : 16;
        else if (k & HidNpadButton_X) cfg.light_target = cfg.light_target == 3 ? 0 : 3;
        else if (k & HidNpadButton_Y) cfg.is_negative_image_used ^= 1;
        else if (k & HidNpadButton_A) {
            ui_at(35, 1);
            printf("\x1b[2KTaking a 320x240 picture...");
            consoleUpdate(NULL);
            u64 s = 0;
            rc = ir_start(h, &cfg, IrsImageTransferProcessorFormat_320x240);
            bool got = R_SUCCEEDED(rc) && ir_frame(h, &s, IR_BUF, 1500) && ir_frame(h, &s, IR_BUF, 3000);
            if (got && ensure_dir()) {
                char path[96];
                time_t t = time(NULL);
                strftime(path, sizeof(path), JC_DIR "/IRcamera_%Y%m%d_%H%M%S.bmp", localtime(&t));
                save_bmp(path, g_ir, IR_SIZES[0][0], IR_SIZES[0][1]);
                snprintf(status, sizeof(status), "Saved %s", path);
            } else {
                snprintf(status, sizeof(status), "No picture from the camera.");
            }
        } else {
            restart = false;
        }
        if (restart) {
            rc = ir_start(h, &cfg, preview);
            sample = 0;
        }
    }
    if (R_FAILED(rc))
        ui_message(CONSOLE_RED "The IR camera stopped: %s" CONSOLE_RESET, rc_text(rc));
    irsStopImageProcessor(h);
    irsExit();
}

// ----------------------------------------------------------------------------------------
static const char *tag_type(u32 t) {
    switch (t) {
        case NfcTagType_Type1: return "Type 1 (Topaz)";
        case NfcTagType_Type2: return "Type 2 (NTAG, Ultralight, amiibo)";
        case NfcTagType_Type3: return "Type 3 (FeliCa)";
        case NfcTagType_Type4A: return "Type 4A (DESFire)";
        case NfcTagType_Type4B: return "Type 4B";
        case NfcTagType_Type5: return "Type 5 (ISO15693)";
        case NfcTagType_Mifare: return "MIFARE Classic";
    }
    return "Unknown";
}

void feat_nfc(Ctrl *c) {
    ui_clear("NFC");
    if (c->type == TYPE_LEFT) {
        ui_message("The Joy-Con (L) has no NFC reader: use the Joy-Con (R) or a Pro Controller.");
        return;
    }
    Result rc = nfcInitialize(NfcServiceType_User);
    NfcDeviceHandle handles[8], h;
    s32 total = 0;
    if (R_SUCCEEDED(rc))
        rc = nfcListDevices(&total, handles, 8);
    if (R_FAILED(rc) || total < 1) {
        ui_message(CONSOLE_RED "No NFC reader is available%s%s" CONSOLE_RESET, R_FAILED(rc) ? ": " : ".",
                   R_FAILED(rc) ? rc_text(rc) : "");
        nfcExit();
        return;
    }
    h = handles[0];
    for (s32 i = 0; i < total; i++) {           // This controller's reader
        u32 npad;
        if (c->has_npad && R_SUCCEEDED(nfcGetNpadId(&handles[i], &npad)) && npad == (u32)c->npad)
            h = handles[i];
    }
    rc = nfcStartDetection(&h, NfcProtocol_All);
    NfcDeviceState last = (NfcDeviceState)-1;
    while (!g_quit && R_SUCCEEDED(rc)) {
        NfcDeviceState st;
        if (R_SUCCEEDED(nfcGetDeviceState(&h, &st)) && st != last) {
            last = st;
            ui_clear("NFC");
            printf("Hold a tag on the %s.\n\n", c->type == TYPE_PRO ? "Pro Controller's NFC spot (the Switch logo)"
                                                                     : "Joy-Con (R)'s stick");
            NfcTagInfo info;
            if (st == NfcDeviceState_TagFound && R_SUCCEEDED(nfcGetTagInfo(&h, &info))) {
                printf(CONSOLE_GREEN "Tag found\n\n" CONSOLE_RESET "UID:   ");
                for (int i = 0; i < info.uid.uid_length && i < 10; i++)
                    printf("%02X%s", info.uid.uid[i], i + 1 < info.uid.uid_length ? ":" : "");
                printf("\nType:  %s\nProtocol: %s\n", tag_type(info.tag_type),
                       info.protocol == NfcProtocol_TypeA ? "ISO14443A" : info.protocol == NfcProtocol_TypeB ? "ISO14443B"
                       : info.protocol == NfcProtocol_TypeF ? "FeliCa" : "?");
                printf("\nThe Switch reads tags' IDs; their contents can be read with the desktop app.\n");
            } else if (st == NfcDeviceState_TagRemoved) {
                printf("The tag was removed. Waiting for a tag...\n");
                nfcStopDetection(&h);
                rc = nfcStartDetection(&h, NfcProtocol_All);
            } else {
                printf("Waiting for a tag...\n");
            }
            ui_footer("B: back");
        }
        if (ui_poll() & HidNpadButton_B)
            break;
    }
    if (R_FAILED(rc))
        ui_message(CONSOLE_RED "NFC failed: %s" CONSOLE_RESET, rc_text(rc));
    nfcStopDetection(&h);
    nfcExit();
}

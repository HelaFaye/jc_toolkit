#include "common.h"

// ----------------------------------------------------------------------------------------
// Button test: the controller's own input, until + and - are pressed together
static const struct {
    u64 mask;
    const char *name;
} BUTTONS[] = {
    {HidNpadButton_A, "A"}, {HidNpadButton_B, "B"}, {HidNpadButton_X, "X"}, {HidNpadButton_Y, "Y"},
    {HidNpadButton_StickL, "L stick"}, {HidNpadButton_StickR, "R stick"}, {HidNpadButton_L, "L"},
    {HidNpadButton_R, "R"}, {HidNpadButton_ZL, "ZL"}, {HidNpadButton_ZR, "ZR"}, {HidNpadButton_Plus, "+"},
    {HidNpadButton_Minus, "-"}, {HidNpadButton_Left, "Left"}, {HidNpadButton_Up, "Up"},
    {HidNpadButton_Right, "Right"}, {HidNpadButton_Down, "Down"}, {HidNpadButton_LeftSL, "SL (L)"},
    {HidNpadButton_LeftSR, "SR (L)"}, {HidNpadButton_RightSL, "SL (R)"}, {HidNpadButton_RightSR, "SR (R)"},
};

static void feat_buttons_impl(Ctrl *c) {
    ui_clear("Button test");
    if (!c->has_npad) {
        ui_message(CONSOLE_RED "This controller isn't in use by the Switch right now." CONSOLE_RESET);
        return;
    }
    PadState p;
    padInitializeWithMask(&p, 1ULL << c->npad);
    HidSixAxisSensorHandle six[2];
    u32 style = ctrl_style(c);
    int n_six = (style == HidNpadStyleTag_NpadJoyDual) ? 2 : 1;
    bool motion = style && R_SUCCEEDED(hidGetSixAxisSensorHandles(six, n_six, c->npad, style));
    int six_i = (style == HidNpadStyleTag_NpadJoyDual && c->type == TYPE_RIGHT) ? 1 : 0;
    if (motion)
        hidStartSixAxisSensor(six[six_i]);
    while (!g_quit) {
        consoleUpdate(NULL);
        if (!appletMainLoop()) {
            g_quit = true;
            break;
        }
        padUpdate(&p);
        padUpdate(&g_pad);
        u64 held = padGetButtons(&p);
        ui_clear("Button test");
        printf("Press the buttons and move the sticks. The Switch reports the controller's input\n"
               "with its calibration applied.\n\nPressed: ");
        for (size_t i = 0; i < sizeof(BUTTONS) / sizeof(BUTTONS[0]); i++)
            if (held & BUTTONS[i].mask)
                printf(CONSOLE_GREEN "%s " CONSOLE_RESET, BUTTONS[i].name);
        printf("\n\n");
        for (int s = 0; s < 2; s++) {
            HidAnalogStickState st = padGetStickPos(&p, s);
            printf("%s stick: X %6d  Y %6d\n", s ? "Right" : "Left ", st.x, st.y);
        }
        if (motion) {
            HidSixAxisSensorState ss;
            if (hidGetSixAxisSensorStates(six[six_i], &ss, 1)) {
                printf("\nAccelerometer (G):     X %6.2f  Y %6.2f  Z %6.2f\n", ss.acceleration.x, ss.acceleration.y,
                       ss.acceleration.z);
                printf("Gyroscope (turns/s):   X %6.2f  Y %6.2f  Z %6.2f\n", ss.angular_velocity.x, ss.angular_velocity.y,
                       ss.angular_velocity.z);
            }
        }
        ui_footer("+ and - together: back");
        u64 all = held | padGetButtons(&g_pad);
        if ((all & HidNpadButton_Plus) && (all & HidNpadButton_Minus))
            break;
        svcSleepThread(16000000ULL);
    }
    if (motion)
        hidStopSixAxisSensor(six[six_i]);
    // Don't let the + / - release reach the next screen
    while (!g_quit && (ui_held() & (HidNpadButton_Plus | HidNpadButton_Minus)))
        ui_poll();
}

void feat_buttons(Ctrl *c) {
    feat_buttons_impl(c);
}

// ----------------------------------------------------------------------------------------
// The HOME button's LED (Joy-Con (R), Pro Controller)
void feat_led(Ctrl *c) {
    ui_clear("HOME LED");
    if (c->type == TYPE_LEFT) {
        ui_message("The Joy-Con (L) has no HOME button.");
        return;
    }
    static const char *const ITEMS[] = {"Off", "On", "Breathing", "Heartbeat"};
    int sel = 0;
    while (!g_quit && (sel = ui_menu("HOME LED", NULL, ITEMS, 4, sel)) >= 0) {
        HidsysNotificationLedPattern p;
        memset(&p, 0, sizeof(p));
        if (sel == 1) {
            p.baseMiniCycleDuration = 0x1;
            p.totalMiniCycles = 0x1;
            p.startIntensity = 0xF;
            p.miniCycles[0].ledIntensity = 0xF;
            p.miniCycles[0].transitionSteps = 0xF;
            p.miniCycles[1] = p.miniCycles[0];
        } else if (sel == 2) {
            p.baseMiniCycleDuration = 0x8;
            p.totalMiniCycles = 0x2;
            p.startIntensity = 0x2;
            p.miniCycles[0].ledIntensity = 0xF;
            p.miniCycles[0].transitionSteps = 0xF;
            p.miniCycles[1].ledIntensity = 0x2;
            p.miniCycles[1].transitionSteps = 0xF;
        } else if (sel == 3) {
            p.baseMiniCycleDuration = 0x1;
            p.totalMiniCycles = 0xF;
            p.startIntensity = 0x0;
            const u8 beat[][3] = {{0xF, 0x8, 0x0}, {0x0, 0x8, 0x0}, {0xF, 0x8, 0x0}, {0x0, 0x8, 0xF}};
            for (int i = 0; i < 4; i++) {
                p.miniCycles[i].ledIntensity = beat[i][0];
                p.miniCycles[i].transitionSteps = beat[i][1];
                p.miniCycles[i].finalStepDuration = beat[i][2];
            }
        }
        Result rc = hidsysSetNotificationLedPattern(&p, c->id);
        if (R_FAILED(rc)) {
            ui_clear("HOME LED");
            ui_message(CONSOLE_RED "Failed: %s" CONSOLE_RESET, rc_text(rc));
        }
    }
}

// ----------------------------------------------------------------------------------------
static void about(void) {
    ui_clear("About");
    ui_message("Joy-Con Toolkit " APP_VERSION " for the Nintendo Switch\n\n"
               "The Joy-Con Toolkit by CTCaer, ported to Python by HelaFaye, and to the Switch\n"
               "as homebrew. The desktop and Android app: https://github.com/HelaFaye/jc_toolkit\n\n"
               "Files: " JC_DIR "\n"
               "  spi_<type>_<MAC>.bin  SPI backups (the desktop app's format)\n"
               "  *.jcvib, *.bnvib      HD Rumble files to play\n"
               "  colors.xml            Your colors (the desktop app's format)\n"
               "  IRcamera_*.bmp        IR camera pictures\n\n"
               "SPI writes change the controller's own memory. Back it up first.");
}

static void controller_menu(Ctrl *c) {
    static const char *const ITEMS[] = {
        "Device info", "Colors", "Back up the SPI flash", "Restore from a backup", "Calibration",
        "Serial number", "Button test", "HD Rumble", "IR camera", "NFC", "HOME LED", "SPI flash viewer",
        "Disconnect",
    };
    const int n = sizeof(ITEMS) / sizeof(ITEMS[0]);
    int sel = 0;
    while (!g_quit) {
        char sub[160], mac[24];
        ctrl_mac_text(c, mac);
        snprintf(sub, sizeof(sub), "%s  %s  %s\n", ctrl_type_name(c->type), mac, ctrl_iface_name(c->iface));
        sel = ui_menu(ctrl_type_name(c->type), sub, ITEMS, n, sel);
        if (sel < 0)
            return;
        if (!ctrl_refresh(c)) {
            ui_clear(NULL);
            ui_message(CONSOLE_RED "The controller disconnected." CONSOLE_RESET);
            return;
        }
        switch (sel) {
            case 0: feat_info(c); break;
            case 1: feat_colors(c); break;
            case 2: feat_backup(c); break;
            case 3: feat_restore(c); break;
            case 4: feat_calibration(c); break;
            case 5: feat_serial(c); break;
            case 6: feat_buttons(c); break;
            case 7: feat_rumble(c); break;
            case 8: feat_ir(c); break;
            case 9: feat_nfc(c); break;
            case 10: feat_led(c); break;
            case 11: feat_spi_viewer(c); break;
            case 12:
                ui_clear("Disconnect");
                if (ui_confirm("Disconnect the %s?", ctrl_type_name(c->type))) {
                    hidsysDisconnectUniquePad(c->id);
                    return;
                }
                break;
        }
    }
}

int main(int argc, char *argv[]) {
    consoleInit(NULL);
    padConfigureInput(8, HidNpadStyleSet_NpadStandard);
    padInitializeAny(&g_pad);
    Result rc_sys = hidsysInitialize();
    Result rc_dbg = hiddbgInitialize();

    Ctrl list[16];
    int sel = 0;
    while (!g_quit) {
        if (R_FAILED(rc_sys) || R_FAILED(rc_dbg)) {
            ui_clear(NULL);
            printf(CONSOLE_RED "The Switch's HID services aren't available (%s)." CONSOLE_RESET "\n\n"
                   "Run this from the Homebrew Menu with Atmosphere.", rc_text(R_FAILED(rc_sys) ? rc_sys : rc_dbg));
            ui_footer("+: exit");
            ui_wait(HidNpadButton_Plus);
            break;
        }
        int n = ctrl_list(list, 16);
        const char *items[18];
        char labels[16][96];
        for (int i = 0; i < n; i++) {
            char mac[24];
            ctrl_mac_text(&list[i], mac);
            snprintf(labels[i], sizeof(labels[i]), "%-15s %s  %-16s %s", ctrl_type_name(list[i].type), mac,
                     ctrl_iface_name(list[i].iface), list[i].sn);
            items[i] = labels[i];
        }
        items[n] = "Refresh the list";
        items[n + 1] = "About";
        const char *sub = hosversionBefore(6, 0, 0)
            ? CONSOLE_RED "The SPI features need Switch system version 6.0.0 or newer.\n" CONSOLE_RESET
            : (n ? "Choose a controller. Connect more in Controllers > Change Grip/Order.\n"
                 : "No controllers. Connect one in Controllers > Change Grip/Order.\n");
        int choice = ui_menu("Controllers", sub, items, n + 2, sel);
        if (choice < 0) {
            ui_clear(NULL);
            if (ui_confirm("Exit the Joy-Con Toolkit?"))
                break;
            continue;
        }
        sel = choice;
        if (choice < n)
            controller_menu(&list[choice]);
        else if (choice == n + 1)
            about();
    }
    hiddbgExit();
    hidsysExit();
    consoleExit(NULL);
    (void)argc;
    (void)argv;
    return 0;
}

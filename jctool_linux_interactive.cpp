// Joy-Con Toolkit (Linux CLI) - interactive menu with the features of Joy-Con Toolkit's
// window. The controller protocol code is in cli/jc_core.cpp; the menus below follow the
// window's handlers (linux/mono/src/FormJoy.cs / jctool/FormJoy.h), with the same checks,
// warnings and SPI offsets.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <unistd.h>
#include <sys/select.h>

#include <deque>
#include <string>
#include <vector>

#include "cli/jc_core.h"
#include "cli/png.h"

#define VENDOR_ID_NINTENDO  0x057E
#define PRODUCT_JOY_CON_L   0x2006
#define PRODUCT_JOY_CON_R   0x2007
#define PRODUCT_PRO         0x2009

static bool temp_celsius = true;

// ---------------------------------------------------------------------------------------------
// Input

static void quit_program(int code);

static std::string fmt_color_prompt(const char *question, const u8 *rgb) {
    char buf[128];
    snprintf(buf, sizeof(buf), "%s [%02X%02X%02X]: ", question, rgb[0], rgb[1], rgb[2]);
    return buf;
}

// Lines typed ahead during a long operation (see ui_poll), used by the next prompts
static std::deque<std::string> pending_lines;

// Reads one line from stdin (without the newline). False at end of input.
static bool get_line(std::string &out) {
    if (!pending_lines.empty()) {
        out = pending_lines.front();
        pending_lines.pop_front();
        return true;
    }
    char line[256];
    if (!fgets(line, sizeof(line), stdin))
        return false;
    size_t n = strlen(line);
    while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r'))
        line[--n] = 0;
    out = line;
    return true;
}

// Reads a line from stdin. Ends the program at end of input.
static std::string read_line(const char *prompt) {
    std::string line;
    if (prompt)
        printf("%s", prompt);
    fflush(stdout);
    if (!get_line(line)) {
        printf("\n");
        quit_program(0);
    }
    return line;
}

// Reads a number from stdin. Returns 0 on success, -1 on bad input, -2 at end of input.
int read_number(const char *format, int *value) {
    std::string line;
    fflush(stdout);
    if (!get_line(line))
        return -2;
    return sscanf(line.c_str(), format, value) == 1 ? 0 : -1;
}

static bool ask_yes_no(const char *question) {
    std::string answer = read_line((std::string(question) + " [y/N]: ").c_str());
    return answer == "y" || answer == "Y" || answer == "yes" || answer == "Yes";
}

// Asks for a number (decimal, or hex with 0x). Empty input keeps the current value.
static int ask_int(const char *question, int current, int min, int max) {
    while (true) {
        std::string answer = read_line((std::string(question) + " [" + std::to_string(current) + "]: ").c_str());
        if (answer.empty())
            return current;
        char *end = NULL;
        long v = strtol(answer.c_str(), &end, 0);
        if (end && *end == 0 && v >= min && v <= max)
            return (int)v;
        printf("  Enter a number from %d to %d.\n", min, max);
    }
}

// Asks for an RGB color as RRGGBB hex. Empty input keeps the current value.
static bool ask_color(const char *question, u8 *rgb) {
    while (true) {
        std::string answer = read_line(fmt_color_prompt(question, rgb).c_str());
        if (answer.empty())
            return false;
        if (answer[0] == '#')
            answer = answer.substr(1);
        unsigned v;
        char tail;
        if (answer.size() == 6 && sscanf(answer.c_str(), "%6x%c", &v, &tail) == 1) {
            rgb[0] = (v >> 16) & 0xFF;
            rgb[1] = (v >> 8) & 0xFF;
            rgb[2] = v & 0xFF;
            return true;
        }
        printf("  Enter a color as RRGGBB (hex), e.g. FF3C28.\n");
    }
}

// True when a line was typed (Enter) since the last check. Used to stop long operations.
static bool stdin_has_line() {
    fd_set fds;
    FD_ZERO(&fds);
    FD_SET(0, &fds);
    struct timeval tv = { 0, 0 };
    return select(1, &fds, NULL, NULL, &tv) > 0;
}

// ---------------------------------------------------------------------------------------------
// Connection

static std::string accepted_third_party_path;

struct found_device {
    std::string path;
    u16 product_id;
    int type;
    std::string name;
    std::string serial;
};

static const char *type_name(int type) {
    switch (type) {
        case JOYCON_L: return "Joy-Con (L)";
        case JOYCON_R: return "Joy-Con (R)";
        case PROCON:   return "Pro Controller";
        default:       return "None";
    }
}

static std::string wstr(const wchar_t *w) {
    std::string s;
    if (w)
        for (; *w; w++)
            s += (*w < 128) ? (char)*w : '?';
    return s;
}

// Nintendo controllers, and third-party ones that report as a "Wireless Gamepad" (like the
// window's pseudo-third-party support). hidraw gives Bluetooth devices an empty manufacturer.
static std::vector<found_device> find_controllers() {
    std::vector<found_device> list;
    struct hid_device_info *devs = hid_enumerate(0, 0);
    for (struct hid_device_info *cur = devs; cur; cur = cur->next) {
        found_device d;
        d.path = cur->path;
        d.product_id = cur->product_id;
        d.serial = wstr(cur->serial_number);
        std::string product = wstr(cur->product_string);
        std::string maker = wstr(cur->manufacturer_string);
        if (cur->vendor_id == VENDOR_ID_NINTENDO && cur->product_id == PRODUCT_JOY_CON_L)
            d.type = JOYCON_L;
        else if (cur->vendor_id == VENDOR_ID_NINTENDO && cur->product_id == PRODUCT_JOY_CON_R)
            d.type = JOYCON_R;
        else if (cur->vendor_id == VENDOR_ID_NINTENDO && cur->product_id == PRODUCT_PRO)
            d.type = PROCON;
        else if (product == "Wireless Gamepad" && (maker == "Nintendo" || maker.empty()) && cur->usage == 0x0005)
            d.type = -PROCON; // Third-party, used as a Pro Controller
        else
            continue;
        d.name = d.type > 0 ? type_name(d.type) : "Third-party \"Wireless Gamepad\" (as Pro Controller)";
        list.push_back(d);
    }
    hid_free_enumeration(devs);
    return list;
}

static int connect_device(const found_device &d) {
    if (d.type < 0 && d.path != accepted_third_party_path) {
        printf("\nA potential third-party device has been detected:\n\n\t%s\n\n", d.name.c_str());
        if (!ask_yes_no("Editing could be potentially unstable. Would you like to use this device anyways?"))
            return -1;
    }
    if (handle)
        hid_close(handle);
    handle = hid_open_path(d.path.c_str());
    if (!handle) {
        fprintf(stderr, "[-] Failed to open %s (permissions? see linux/udev)\n", d.path.c_str());
        handle_type = NOTHING;
        return -1;
    }
    if (d.type < 0)
        accepted_third_party_path = d.path;
    handle_type = d.type < 0 ? PROCON : d.type;
    timming_byte = 0;
    // Like the window at start: quiet input reports, busy LEDs
    silence_input_report();
    set_led_busy();
    printf("[+] Connected to %s\n", type_name(handle_type));
    return 0;
}

int select_device(void) {
    std::vector<found_device> list = find_controllers();
    if (list.empty()) {
        fprintf(stderr, "[-] No Joy-Con or Pro Controller found\n"
                        "    Pair it (see linux/README.md), check /dev/hidraw* permissions\n"
                        "    and that the hid_nintendo driver is not holding it.\n");
        return -1;
    }

    printf("\n=== Available Devices ===\n");
    for (size_t i = 0; i < list.size(); i++) {
        printf("[%zu] %s", i, list[i].name.c_str());
        if (!list[i].serial.empty())
            printf(" (%s)", list[i].serial.c_str());
        printf("  %s\n", list[i].path.c_str());
    }

    int choice = 0;
    if (list.size() > 1) {
        printf("\nSelect device [0-%zu]: ", list.size() - 1);
        if (read_number("%d", &choice) != 0 || choice < 0 || choice >= (int)list.size()) {
            fprintf(stderr, "[-] Invalid selection\n");
            return -1;
        }
    }
    return connect_device(list[choice]);
}

// The window checked the connection before every action.
static bool need_device() {
    if (handle)
        return true;
    printf("[-] No device connected. Select one first.\n");
    return select_device() == 0;
}

static void quit_program(int code) {
    if (handle)
        hid_close(handle);
    hid_exit();
    exit(code);
}

// ---------------------------------------------------------------------------------------------
// What the window showed (called by cli/jc_core.cpp)

static std::string live_command;   // A line typed during an IR stream (live config)

// Enter (an empty line) stops a long operation, like the window's Stop/Cancel buttons.
// During an IR stream, "e", "g" and "r" lines change the camera settings. Anything else
// was typed ahead and is kept for the next prompts.
void ui_poll() {
    if (!stdin_has_line())
        return;
    char buf[256];
    if (!fgets(buf, sizeof(buf), stdin))
        return;
    std::string line = buf;
    while (!line.empty() && (line.back() == '\n' || line.back() == '\r'))
        line.pop_back();
    if (!line.empty()) {
        if (enable_IRVideoPhoto && (line[0] == 'e' || line[0] == 'g' || line[0] == 'r'))
            live_command = line;   // Handled by the IR stream (ui_ir_frame)
        else
            pending_lines.push_back(line);
        return;
    }
    cancel_spi_dump = true;
    enable_button_test = false;
    enable_IRVideoPhoto = false;
    enable_NFCScanning = false;
    printf("\nStopping...\n");
}

void ui_spi_progress(u32 offset) {
    printf("\r  %.2fKB of 512KB   (Enter to cancel)", offset / 1024.0f);
    fflush(stdout);
}

void ui_custom_command(const std::string &sent, const std::string &reply_cmd, const std::string &reply) {
    printf("\nSent:\n%s\n", sent.c_str());
    if (!reply_cmd.empty())
        printf("%s\n", reply_cmd.c_str());
    printf("\n%s\n", reply.c_str());
}

void ui_button_test_info(const std::string &text) {
    printf("%s\n\n", text.c_str() + (text.compare(0, 2, "\n\n") == 0 ? 2 : 0));
    printf("Live input (Enter to stop):\n\0337");   // Save the cursor position
}

void ui_button_test(const std::string &report, const std::string &sensors) {
    printf("\0338\033[J%s\n\n%s", report.c_str(), sensors.c_str()); // Redraw from the saved position
    fflush(stdout);
}

void ui_nfc_uid(const std::string &text) {
    printf("\n%s\n", text.c_str());
}

void ui_nfc_tag(const std::string &text) {
    printf("%s\n", text.c_str());
}

// show_ntag_contents
void ui_ntag_contents(u8 *ntag_buf, u8 ntag_pages) {
    const char *model = ntag_pages == 45 ? "213" : ntag_pages == 135 ? "215" : ntag_pages == 231 ? "216" : "???";
    const char *kind = ntag_buf[16] == 0xA5 ? " (Amiibo)" : ntag_buf[16] == 0x01 ? " (NDEF)" : "";
    printf("NTAG %s%s\n\n", model, kind);
    for (int i = 0; i < ntag_pages; i++) {
        printf("%02X: ", i);
        for (int j = 0; j < 4; j++)
            printf("%02X ", ntag_buf[i * 4 + j]);
        printf("|");
        for (int j = 0; j < 4; j++) {
            u8 c = ntag_buf[i * 4 + j];
            printf("%c", (c < 0x20 || c > 0x7e) ? '.' : c);
        }
        printf("|\n");
    }
}

// ---------------------------------------------------------------------------------------------
// IR camera settings (the window's IR Camera panel) and images

enum { IR_GREY = 0, IR_NIGHT = 1, IR_IRONBOW = 2, IR_INFRARED = 3 };

static struct {
    int  resolution = 0;        // 0: 240x320, 1: 120x160, 2: 60x80, 3: 30x40
    int  mode = 0;              // 0: Capture, 1: Pointing, 2: Clustering
    int  colorize = IR_IRONBOW;
    bool leds_far = true;       // Far/Narrow (75°) Leds 1/2
    bool leds_near = true;      // Near/Wide (130°) Leds 3/4
    int  intensity_far = 15;    // 0-15
    int  intensity_near = 16;   // 0-16
    bool flashlight = false;
    bool strobe = false;
    bool ex_filter = true;      // External IR filter
    bool selfie = false;
    int  exposure = 300;        // us, 0-600
    bool auto_exposure = false; // For streaming (Capture always uses it)
    int  gain = 2;              // Digital gain 1-20 (streaming without auto exposure)
    bool denoise = true;
    int  edge_smoothing = 35;
    int  color_interpolation = 68;
    int  custom_reg = 0;        // Live config: register address (page << 8 | reg)
    int  custom_val = 0;
} ir;

static int ir_image_width = 320;
static int ir_image_height = 240;
static bool ir_show_preview = true;
static int ir_frames_shown = 0;
static std::string ir_status_text;
static std::vector<u8> ir_last_capture;
static int ir_last_capture_w = 0, ir_last_capture_h = 0;

static const char *ir_res_names[] = { "240x320", "120x160", "60x80", "30x40" };
static const char *ir_mode_names[] = { "Capture", "Pointing", "Clustering" };
static const char *ir_color_names[] = { "Greyscale", "Night vision", "Ironbow", "Infrared" };

// prepareSendIRConfig: the camera configuration from the settings
static bool build_ir_config(ir_image_config &cfg, bool startNewConfig) {
    memset(&cfg, 0, sizeof(cfg));
    if (startNewConfig) {
        static const int widths[] = { 320, 160, 80, 40 }, heights[] = { 240, 120, 60, 30 };
        static const u8 res_regs[] = { 0x00, 0x50, 0x64, 0x69 }, frags[] = { 0xff, 0x3f, 0x0f, 0x03 };
        int r = ir.resolution;
        if (ir.auto_exposure && r == 3)
            r = 2;              // The window disables 30x40 with auto exposure
        ir_image_width  = widths[r];
        ir_image_height = heights[r];
        cfg.ir_res_reg  = res_regs[r];
        ir_max_frag_no  = frags[r];

        if (ir.mode == 0)
            cfg.ir_mode = 0x07;
        else {
            cfg.ir_mode = ir.mode == 1 ? 0x04 : 0x06;
            ir_image_width = 320;
            ir_image_height = 240;
        }
    }
    else
        cfg.ir_res_reg = ir.resolution == 3 ? 0x69 : 0x00;

    // Enable IR Leds. Only the following configurations are supported.
    if (ir.leds_far && ir.leds_near)
        cfg.ir_leds = 0x00;      // Both Far/Narrow 75° and Near/Wide 130° Led groups are enabled.
    else if (ir.leds_far && !ir.leds_near)
        cfg.ir_leds = 0x20;      // Only Far/Narrow 75° Led group is enabled.
    else if (!ir.leds_far && ir.leds_near)
        cfg.ir_leds = 0x10;      // Only Near/Wide 130° Led group is enabled.
    else
        cfg.ir_leds = 0x30;      // Both groups disabled

    // IR Leds Intensity
    cfg.ir_leds_intensity = (u16)((ir.intensity_far << 8) | ir.intensity_near);

    // IR Leds Effects
    if (ir.flashlight)
        cfg.ir_leds |= 0x01;
    if (ir.strobe && !ir.flashlight)
        cfg.ir_leds |= 0x80;

    // External Light filter (Dark-frame subtraction). Disabled with the leds in flashlight mode.
    cfg.ir_ex_light_filter = ((ir.ex_filter || ir.strobe) && !ir.flashlight) ? 0x03 : 0x00;

    // Flip image
    cfg.ir_flip = ir.selfie ? 0x02 : 0x00;

    // Exposure time (Shutter speed) is in us. Valid values are 0 to 600us or 0 - 1/1666.66s
    cfg.ir_exposure = (u16)(ir.exposure * 31200 / 1000);
    if (!ir.auto_exposure && enable_IRVideoPhoto) {
        enable_IRAutoExposure = false;
        cfg.ir_digital_gain = (u8)ir.gain;
    }
    else {
        enable_IRAutoExposure = true;
        cfg.ir_digital_gain = 1; // Disable digital gain for auto exposure
    }

    //De-noise algorithms
    cfg.ir_denoise = (ir.denoise ? 0x01 : 0x00) << 16;
    cfg.ir_denoise |= (u32)((ir.edge_smoothing & 0xFF) << 8);
    cfg.ir_denoise |= (u32)(ir.color_interpolation & 0xFF);
    return true;
}

static void ir_pixel_rgb(u8 v, u8 *rgb) {
    switch (ir.colorize) {
        case IR_IRONBOW: {
            u32 c = ir_iron_palette(v);
            rgb[0] = (c >> 16) & 0xFF; rgb[1] = (c >> 8) & 0xFF; rgb[2] = c & 0xFF;
            break;
        }
        case IR_GREY:  rgb[0] = rgb[1] = rgb[2] = v; break;
        case IR_NIGHT: rgb[0] = 0; rgb[1] = v; rgb[2] = 0; break;
        default:       rgb[0] = v; rgb[1] = 0; rgb[2] = 0; break;
    }
}

// setIRPictureWindow: colorize and rotate 90° clockwise, like the window shows and saves it.
static std::vector<u8> ir_render(const u8 *image, int &out_w, int &out_h) {
    int w = ir_image_width, h = ir_image_height;
    out_w = h;
    out_h = w;
    std::vector<u8> rgb((size_t)out_w * out_h * 3);
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++) {
            // Rotate90FlipNone: source (x, y) goes to (h - 1 - y, x)
            u8 *px = &rgb[((size_t)x * out_w + (h - 1 - y)) * 3];
            ir_pixel_rgb(image[y * w + x], px);
        }
    return rgb;
}

// Terminal preview with 24-bit color half blocks (each character is 2 pixels tall)
static void ir_print_preview(const std::vector<u8> &rgb, int w, int h, bool redraw) {
    if (!ir_show_preview || !isatty(1))
        return;
    int cols = 48;
    int rows = (int)((long)h * cols / w / 2);
    if (redraw)
        printf("\0338");
    else
        printf("\n\0337");
    for (int r = 0; r < rows; r++) {
        for (int c = 0; c < cols; c++) {
            int x = c * w / cols;
            const u8 *top = &rgb[((size_t)((2 * r) * h / (2 * rows)) * w + x) * 3];
            const u8 *bot = &rgb[((size_t)((2 * r + 1) * h / (2 * rows)) * w + x) * 3];
            printf("\033[38;2;%d;%d;%dm\033[48;2;%d;%d;%dm▀", top[0], top[1], top[2], bot[0], bot[1], bot[2]);
        }
        printf("\033[0m\n");
    }
}

void ui_ir_status(const std::string &text) {
    ir_status_text = text;
    printf("\r\033[K%s", text.c_str());
    fflush(stdout);
}

void ui_ir_help(const std::string &text) {
    std::string one_line = text;
    for (char &c : one_line)
        if (c == '\n')
            c = ' ';
    printf("\r\033[K%s", one_line.c_str());
    fflush(stdout);
}

void ui_ir_exposure(int exposure) {
    ir.exposure = exposure;
}

static void ir_apply_live_command();

void ui_ir_frame(const u8 *image) {
    int w, h;
    std::vector<u8> rgb = ir_render(image, w, h);
    if (!enable_IRVideoPhoto) {
        // Capture: saved like the window does (the frame that ends the capture is kept last)
        write_png_rgb("IRcamera.png", rgb.data(), w, h);
        ir_last_capture = rgb;
        ir_last_capture_w = w;
        ir_last_capture_h = h;
    }
    else {
        // Stream: the newest frame, for an image viewer that reloads it
        write_png_rgb("IRstream.png", rgb.data(), w, h);
        printf("\r\033[K");
        ir_print_preview(rgb, w, h, ir_frames_shown++ > 0);
        printf("Streaming: IRstream.png updated. Enter: stop; e <us>: exposure; g <1-20>: gain; r <reg> <val>: register\n");
        if (!live_command.empty())
            ir_apply_live_command();
    }
}

// btn_IRConfigLive_Click during a stream
static void ir_apply_live_command() {
    std::string cmd = live_command;
    live_command.clear();
    int a = 0, b = 0;
    if (sscanf(cmd.c_str(), "e %d", &a) == 1)
        ir.exposure = CLAMP(a, 0, 600);
    else if (sscanf(cmd.c_str(), "g %d", &a) == 1)
        ir.gain = CLAMP(a, 1, 20);
    else if (sscanf(cmd.c_str(), "r %i %i", &a, &b) == 2) {
        ir.custom_reg = a & 0xFFFF;
        ir.custom_val = b & 0xFF;
    }
    else
        return;
    ir_image_config cfg;
    build_ir_config(cfg, false);
    cfg.ir_custom_register = (u32)(ir.custom_reg | (ir.custom_val << 16));
    ir_sensor_config_live(cfg);
    printf("Applied: exposure %dus, gain %d, register %04X = %02X\n", ir.exposure, ir.gain, ir.custom_reg, ir.custom_val);
}

// ---------------------------------------------------------------------------------------------
// Menu

void print_menu(void) {
    printf("\n");
    printf("=== Joy-Con Toolkit (Linux CLI) ===");
    if (handle)
        printf("   [%s]", type_name(handle_type));
    printf("\n");
    printf(" 1. Detect and select device\n");
    printf(" 2. Get device info\n");
    printf(" 3. Get battery status\n");
    printf(" 4. Set player LED\n");
    printf(" 5. Test vibration\n");
    printf(" 6. Read calibration data\n");
    printf(" 7. Colors (view / change)\n");
    printf(" 8. Backup SPI flash\n");
    printf(" 9. Restore from an SPI backup\n");
    printf("10. Serial number (change / restore)\n");
    printf("11. Edit calibration and stick parameters\n");
    printf("12. Button, stick and 6-axis test\n");
    printf("13. HD Rumble player\n");
    printf("14. IR camera (Joy-Con R)\n");
    printf("15. NFC / amiibo scan (Joy-Con R, Pro)\n");
    printf("16. Debug: send a custom command\n");
    printf("17. List HID devices\n");
    printf("18. Disconnect the controller\n");
    printf(" 0. Exit\n");
    printf("Select: ");
}
// ---------------------------------------------------------------------------------------------
// Device info, battery, temperature (full_refresh, update_battery, update_temperature)

static void show_battery() {
    u8 batt_info[3] = { 0 };
    get_battery(batt_info);

    int batt_percent = 0;
    int batt = (batt_info[0] & 0xF0) >> 4;

    // Calculate aproximate battery percent from regulated voltage
    u16 batt_volt = (u16)(batt_info[1] + (batt_info[2] << 8));
    if (batt_volt < 0x560)
        batt_percent = 1;
    else if (batt_volt > 0x55F && batt_volt < 0x5A0)
        batt_percent = (int)(((batt_volt - 0x60) & 0xFF) / 7.0f + 1);
    else if (batt_volt > 0x59F && batt_volt < 0x5E0)
        batt_percent = (int)(((batt_volt - 0xA0) & 0xFF) / 2.625f + 11);
    else if (batt_volt > 0x5DF && batt_volt < 0x618)
        batt_percent = (int)((batt_volt - 0x5E0) / 1.8965f + 36);
    else if (batt_volt > 0x617 && batt_volt < 0x658)
        batt_percent = (int)(((batt_volt - 0x18) & 0xFF) / 1.8529f + 66);
    else if (batt_volt > 0x657)
        batt_percent = 100;

    static const char *states[] = { "Empty (disconnected?)", "Empty, charging", "Low, please charge your device!",
        "Low, charging", "Medium", "Medium, charging", "Good", "Good, charging", "Full", "Almost full, charging" };
    printf("Battery:     %.2fV - %d%%  (%s)\n", (batt_volt * 2.5) / 1000, batt_percent, batt <= 9 ? states[batt] : "?");
}

static void show_temperature() {
    u8 temp_info[2] = { 0 };
    get_temperature(temp_info);
    // Convert reading to Celsius according to datasheet
    float temperature_c = 25.0f + uint16_to_int16(temp_info[1] << 8 | temp_info[0]) * 0.0625f;
    if (temp_celsius)
        printf("Temperature: %.1f\u00B0C\n", temperature_c);
    else
        printf("Temperature: %.1f\u00B0F\n", temperature_c * 1.8f + 32);
}

static void show_colors() {
    u8 c[12] = { 0 };
    if (get_spi_data(0x6050, 12, c) != 0) {
        printf("Colors:      Error!\n");
        return;
    }
    printf("Body:        #%02X%02X%02X\n", c[0], c[1], c[2]);
    printf("Buttons:     #%02X%02X%02X\n", c[3], c[4], c[5]);
    if (handle_type == PROCON)
        printf("Grips:       left #%02X%02X%02X, right #%02X%02X%02X\n", c[6], c[7], c[8], c[9], c[10], c[11]);
}

void cmd_device_info(void) {
    if (!need_device())
        return;
    u8 device_info[10] = { 0 };
    get_device_info(device_info);
    printf("\nController:  %s\n", type_name(handle_type));
    printf("FW Version:  %X.%02X\n", device_info[0], device_info[1]);
    printf("MAC:         %02X:%02X:%02X:%02X:%02X:%02X\n",
        device_info[4], device_info[5], device_info[6], device_info[7], device_info[8], device_info[9]);
    if (handle_type != PROCON)
        printf("S/N:         %s\n", get_sn(0x6001, 0xF).c_str());
    else
        printf("S/N:         Not supported\n");
    show_battery();
    show_temperature();
    show_colors();
}

void cmd_battery_status(void) {
    if (!need_device())
        return;
    printf("\n");
    show_battery();
    show_temperature();
    if (ask_yes_no("Switch the temperature between Celsius and Fahrenheit?")) {
        temp_celsius = !temp_celsius;
        show_temperature();
    }
}

void cmd_set_player_led(void) {
    if (!need_device())
        return;
    printf("\nLED patterns (combine by adding):\n");
    printf("  0x01: Player 1\n");
    printf("  0x02: Player 2\n");
    printf("  0x04: Player 3\n");
    printf("  0x08: Player 4\n");
    printf("  Add 0x10-0x80 to flash the same LEDs instead\n");
    int pattern = ask_int("Enter pattern (e.g. 0x01)", 0x01, 0, 0xFF);
    u8 arg = (u8)pattern;
    u8 reply[49];
    if (send_subcommand(0x30, &arg, 1, reply) == 0)
        printf("[+] LED command acknowledged\n");
    else
        printf("[-] No response\n");
}

void cmd_test_vibration(void) {
    if (!need_device())
        return;
    printf("\n[*] Sending the rumble confirmation pattern...\n");
    send_rumble();
    printf("[+] Done\n");
}

// ---------------------------------------------------------------------------------------------
// Calibration (btn_refreshUserCal_Click, btn_writeUserCal_Click, btn_writeStickParams_Click)

struct stick_cal { bool valid; int x_minus, x_center, x_plus, y_minus, y_center, y_plus; };

// Left stick data is ordered max-above-center, center, min-below-center; right stick is
// center, min-below-center, max-above-center.
static stick_cal decode_stick(u8 *d, bool left) {
    u16 p[6];
    decode_stick_params(p,     d);
    decode_stick_params(p + 2, d + 3);
    decode_stick_params(p + 4, d + 6);
    stick_cal c;
    c.valid = true;
    int center = left ? 2 : 0, minus = left ? 4 : 2, plus = left ? 0 : 4;
    c.x_center = p[center];     c.y_center = p[center + 1];
    c.x_minus  = p[center] - p[minus];     c.y_minus = p[center + 1] - p[minus + 1];
    c.x_plus   = p[center] + p[plus];      c.y_plus  = p[center + 1] + p[plus + 1];
    return c;
}

static void print_stick(const char *name, const stick_cal &c) {
    if (!c.valid)
        printf("  %s: No calibration\n", name);
    else
        printf("  %s: center (%d, %d)  X [%d - %d]  Y [%d - %d]\n", name,
            c.x_center, c.y_center, c.x_minus, c.x_plus, c.y_minus, c.y_plus);
}

static void print_sensor(const char *name, const u8 *d) {
    s16 v[12];
    for (int i = 0; i < 12; i++)
        v[i] = uint16_to_int16(d[i * 2] | (d[i * 2 + 1] << 8));
    printf("  %s Acc:  origin %6d %6d %6d   sensitivity %6d %6d %6d\n", name, v[0], v[1], v[2], v[3], v[4], v[5]);
    printf("  %s Gyro: origin %6d %6d %6d   sensitivity %6d %6d %6d\n", name, v[6], v[7], v[8], v[9], v[10], v[11]);
}

void cmd_read_calibration(void) {
    if (!need_device())
        return;
    u8 factory_stick[18] = { 0 }, user_stick[22] = { 0 }, factory_sensor[24] = { 0 }, user_sensor[26] = { 0 };
    u8 sensor_model[6] = { 0 }, stick_model[0x24] = { 0 };
    get_spi_data(0x603D, 18, factory_stick);
    get_spi_data(0x8010, 22, user_stick);
    get_spi_data(0x6020, 24, factory_sensor);
    get_spi_data(0x8026, 26, user_sensor);
    get_spi_data(0x6080, 6, sensor_model);
    get_spi_data(0x6086, 0x12, stick_model);
    get_spi_data(0x6098, 0x12, stick_model + 0x12);

    bool left = handle_type != JOYCON_R, right = handle_type != JOYCON_L;
    printf("\nStick calibration (factory):\n");
    if (left)  print_stick("Left stick ", decode_stick(factory_stick, true));
    if (right) print_stick("Right stick", decode_stick(factory_stick + 9, false));
    printf("Stick calibration (user):\n");
    if (left) {
        stick_cal c = { false, 0, 0, 0, 0, 0, 0 };
        if ((user_stick[0] | user_stick[1] << 8) == 0xA1B2) c = decode_stick(user_stick + 2, true);
        print_stick("Left stick ", c);
    }
    if (right) {
        stick_cal c = { false, 0, 0, 0, 0, 0, 0 };
        if ((user_stick[11] | user_stick[12] << 8) == 0xA1B2) c = decode_stick(user_stick + 13, false);
        print_stick("Right stick", c);
    }
    printf("6-axis calibration:\n");
    print_sensor("Factory", factory_sensor);
    if ((user_sensor[0] | user_sensor[1] << 8) == 0xA1B2)
        print_sensor("User   ", user_sensor + 2);
    else
        printf("  User: No calibration\n");

    u16 p[2];
    printf("Device parameters (factory):\n");
    printf("  Flat surface ACC offset: %04X %04X %04X\n",
        sensor_model[0] | sensor_model[1] << 8, sensor_model[2] | sensor_model[3] << 8, sensor_model[4] | sensor_model[5] << 8);
    decode_stick_params(p, stick_model + 3);
    printf("  %s: deadzone %d, range ratio %d\n", handle_type == PROCON ? "Left stick " : "Stick", p[0], p[1]);
    if (handle_type == PROCON) {
        decode_stick_params(p, stick_model + 0x15);
        printf("  Right stick: deadzone %d, range ratio %d\n", p[0], p[1]);
    }
}

static void edit_stick(const char *name, stick_cal &c) {
    printf("%s:\n", name);
    c.x_minus  = ask_int("  X minimum", c.x_minus, 0, 0xFFF);
    c.x_center = ask_int("  X center ", c.x_center, 0, 0xFFF);
    c.x_plus   = ask_int("  X maximum", c.x_plus, 0, 0xFFF);
    c.y_minus  = ask_int("  Y minimum", c.y_minus, 0, 0xFFF);
    c.y_center = ask_int("  Y center ", c.y_center, 0, 0xFFF);
    c.y_plus   = ask_int("  Y maximum", c.y_plus, 0, 0xFFF);
}

static void encode_stick(u8 *out, const stick_cal &c, bool left) {
    u16 pair[2];
    // Center X,Y
    pair[0] = (u16)c.x_center; pair[1] = (u16)c.y_center;
    encode_stick_params(out + (left ? 3 : 0), pair);
    // +Axis X,Y
    pair[0] = (u16)(c.x_plus - c.x_center); pair[1] = (u16)(c.y_plus - c.y_center);
    encode_stick_params(out + (left ? 0 : 6), pair);
    // -Axis X,Y
    pair[0] = (u16)(c.x_center - c.x_minus); pair[1] = (u16)(c.y_center - c.y_minus);
    encode_stick_params(out + (left ? 6 : 3), pair);
}

void cmd_edit_calibration(void) {
    if (!need_device())
        return;
    printf("\n 1. User stick and 6-axis calibration\n 2. Stick device parameters (factory)\n 0. Back\n");
    int choice = ask_int("Select", 0, 0, 2);
    if (choice == 1) {
        u8 user_stick_cal[22] = { 0 }, user_sensor_cal[26] = { 0 }, factory_stick[18] = { 0 };
        get_spi_data(0x8010, 22, user_stick_cal);
        msleep(100);
        get_spi_data(0x8026, 26, user_sensor_cal);
        msleep(100);
        get_spi_data(0x603D, 18, factory_stick);

        bool left = handle_type != JOYCON_R, right = handle_type != JOYCON_L;
        stick_cal lc = decode_stick(factory_stick, true), rc = decode_stick(factory_stick + 9, false);
        bool l_on = false, r_on = false, s_on = false;
        if (left && (user_stick_cal[0] | user_stick_cal[1] << 8) == 0xA1B2) { lc = decode_stick(user_stick_cal + 2, true); l_on = true; }
        if (right && (user_stick_cal[11] | user_stick_cal[12] << 8) == 0xA1B2) { rc = decode_stick(user_stick_cal + 13, false); r_on = true; }
        s_on = (user_sensor_cal[0] | user_sensor_cal[1] << 8) == 0xA1B2;
        int acc[3], gyro[3];
        for (int i = 0; i < 3; i++) {
            acc[i]  = user_sensor_cal[2 + i * 2] | user_sensor_cal[3 + i * 2] << 8;
            gyro[i] = user_sensor_cal[14 + i * 2] | user_sensor_cal[15 + i * 2] << 8;
        }

        printf("\nA user calibration that is disabled is erased (the factory one is used).\n");
        if (left) {
            l_on = ask_yes_no(l_on ? "Keep a left stick user calibration? (currently set)" : "Set a left stick user calibration? (currently none)");
            if (l_on) edit_stick("Left stick", lc);
        }
        if (right) {
            r_on = ask_yes_no(r_on ? "Keep a right stick user calibration? (currently set)" : "Set a right stick user calibration? (currently none)");
            if (r_on) edit_stick("Right stick", rc);
        }
        s_on = ask_yes_no(s_on ? "Keep a 6-axis user calibration? (currently set)" : "Set a 6-axis user calibration? (currently none)");
        if (s_on) {
            const char *axis[] = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
                acc[i] = ask_int((std::string("  Acc ") + axis[i] + " origin (0-65535)").c_str(), acc[i], 0, 0xFFFF);
            for (int i = 0; i < 3; i++)
                gyro[i] = ask_int((std::string("  Gyro ") + axis[i] + " origin (0-65535)").c_str(), gyro[i], 0, 0xFFFF);
        }
        if (!ask_yes_no("Are you sure you want to continue?"))
            return;

        memset(user_stick_cal, 0, 22);
        memset(user_sensor_cal, 0, 26);
        if (left && l_on) {
            user_stick_cal[0] = 0xB2; user_stick_cal[1] = 0xA1;
            encode_stick(user_stick_cal + 2, lc, true);
        }
        else
            memset(user_stick_cal, 0xFF, 11);   // Erase left stick user cal
        if (right && r_on) {
            user_stick_cal[11] = 0xB2; user_stick_cal[12] = 0xA1;
            encode_stick(user_stick_cal + 13, rc, false);
        }
        else
            memset(&user_stick_cal[11], 0xFF, 11); // Erase right stick user cal
        if (s_on) {
            user_sensor_cal[0] = 0xB2; user_sensor_cal[1] = 0xA1;
            for (int i = 0; i < 3; i++) {
                user_sensor_cal[2 + i * 2] = acc[i] & 0xFF;   user_sensor_cal[3 + i * 2] = acc[i] >> 8;
                user_sensor_cal[14 + i * 2] = gyro[i] & 0xFF; user_sensor_cal[15 + i * 2] = gyro[i] >> 8;
            }
        }
        else
            memset(user_sensor_cal, 0xFF, 26);  // Erase user sensor cal

        int res = write_spi_data(0x8010, 22, user_stick_cal);
        if (res == 0) {
            msleep(100);
            res = write_spi_data(0x8026, 26, user_sensor_cal);
        }
        printf(res == 0 ? "[+] The user calibration was written to SPI!\n" : "[-] Failed to write user calibration to SPI! Please try again..\n");
    }
    else if (choice == 2) {
        u8 main_left[3] = { 0 }, pro_right[3] = { 0 };
        u16 p[4] = { 0 };
        get_spi_data(0x6089, 3, main_left);
        decode_stick_params(p, main_left);
        if (handle_type == PROCON) {
            msleep(100);
            get_spi_data(0x609B, 3, pro_right);
            decode_stick_params(p + 2, pro_right);
        }
        printf("\nWarning! These are stick device parameters coming from factory.\n");
        const char *first = handle_type == PROCON ? "Left stick" : "Stick";
        p[0] = (u16)ask_int((std::string(first) + " deadzone").c_str(), p[0], 0, 0xFFF);
        p[1] = (u16)ask_int((std::string(first) + " range ratio").c_str(), p[1], 0, 0xFFF);
        if (handle_type == PROCON) {
            p[2] = (u16)ask_int("Right stick deadzone", p[2], 0, 0xFFF);
            p[3] = (u16)ask_int("Right stick range ratio", p[3], 0, 0xFFF);
        }
        if (!ask_yes_no("Are you sure you want to continue?"))
            return;
        encode_stick_params(main_left, p);
        int res = write_spi_data(0x6089, 3, main_left);
        if (res == 0 && handle_type == PROCON) {
            msleep(100);
            encode_stick_params(pro_right, p + 2);
            res = write_spi_data(0x609B, 3, pro_right);
        }
        printf(res == 0 ? "[+] The Stick Device Parameters were written to SPI!\n" : "[-] Failed to write the Stick Device Parameters to SPI! Please try again..\n");
    }
}

// ---------------------------------------------------------------------------------------------
// Colors (btn_writeColorsToSpi_Click)

void cmd_colors(void) {
    if (!need_device())
        return;
    printf("\n");
    show_colors();
    u8 c[12] = { 0 };
    if (get_spi_data(0x6050, 12, c) != 0)
        return;
    printf("\nEnter new colors as RRGGBB (Enter keeps a color).\n");
    bool changed = ask_color("Body color", c);
    changed |= ask_color("Buttons color", c + 3);
    if (handle_type == PROCON) {
        changed |= ask_color("Left grip color", c + 6);
        changed |= ask_color("Right grip color", c + 9);
    }
    if (!changed)
        return;

    set_led_busy();
    printf("\nDon't forget to make a backup first!\n");
    if (!ask_yes_no("Are you sure you want to continue?"))
        return;
    int error = write_spi_data(0x6050, handle_type != PROCON ? 6 : 12, c);
    send_rumble();
    if (error == 0) {
        printf("[+] The colors were written to the device!\n");
        show_colors(); // Check that the colors were written
    }
    else
        printf("[-] Failed to write the colors to the device!\n");
}

// ---------------------------------------------------------------------------------------------
// SPI backup (btn_makeSPIBackup_Click)

void cmd_backup(void) {
    if (!need_device())
        return;
    u8 device_info[10] = { 0 };
    get_device_info(device_info);
    char filename[64];
    snprintf(filename, sizeof(filename), "spi_%s%02X%02X%02X%02X%02X%02X.bin",
        handle_type == JOYCON_L ? "left_" : handle_type == JOYCON_R ? "right_" : "pro_",
        device_info[4], device_info[5], device_info[6], device_info[7], device_info[8], device_info[9]);

    if (access(filename, F_OK) == 0) {
        printf("The file %s already exists!\n", filename);
        if (!ask_yes_no("Do you want to overwrite it?"))
            return;
    }
    printf("\nDumping the SPI flash to %s (takes a few minutes)...\n", filename);
    send_rumble();
    set_led_busy();
    cancel_spi_dump = false;

    int error = dump_spi(filename);
    printf("\n");
    if (error == 0 && !cancel_spi_dump) {
        send_rumble();
        printf("[+] Done dumping SPI! Saved to %s\n", filename);
    }
    else if (cancel_spi_dump)
        printf("[-] Cancelled. %s is incomplete.\n", filename);
    else
        printf("[-] Failed to dump the SPI chip!\n");
    cancel_spi_dump = false;
}

// ---------------------------------------------------------------------------------------------
// Restore (btn_loadSPIBackup_Click, btn_restore_Click)

static bool load_file(const std::string &path, std::vector<u8> &data) {
    FILE *f = fopen(path.c_str(), "rb");
    if (!f) {
        printf("[-] Cannot open %s\n", path.c_str());
        return false;
    }
    data.clear();
    u8 chunk[65536];
    size_t n;
    while ((n = fread(chunk, 1, sizeof(chunk), f)) > 0)
        data.insert(data.end(), chunk, chunk + n);
    fclose(f);
    return true;
}

void cmd_restore(void) {
    if (!need_device())
        return;
    std::string path = read_line("\nSPI backup file (.bin): ");
    std::vector<u8> backup_spi;
    if (path.empty() || !load_file(path, backup_spi))
        return;

    bool validation_check = true;
    bool mac_check = true;
    bool allow_full_restore = true;
    bool ota_exists = true;
    //Bootloader, device type, FW DS1, FW DS2
    const u8 validation_magic[] = {
        0x01, 0x08, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x08, 0xC0, 0x5D, 0x89, 0xFD, 0x04, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x40, 0x06,
        (u8)handle_type, 0xA0,
        0x0A, 0xFB, 0x00, 0x00, 0x02, 0x0D,
        0xAA, 0x55, 0xF0, 0x0F, 0x68, 0xE5, 0x97, 0xD2 };

    if (backup_spi.size() != 524288) {
        printf("[-] Partial backup! The file size must be 512KB (524288 Bytes)\n");
        return;
    }
    const char *str_dev_type = type_name(handle_type);
    const char *str_backup_dev_type = backup_spi[0x6012] >= 1 && backup_spi[0x6012] <= 3 ? type_name(backup_spi[0x6012]) : "unknown";

    //Backup Validation
    for (int i = 0; i < 20; i++)
        if (validation_magic[i] != backup_spi[i]) {
            validation_check = false;
            break;
        }
    for (int i = 20; i < 22; i++)
        if (validation_magic[i] != backup_spi[0x6012 + i - 20]) {
            printf("[-] Wrong backup! The file is a \"%s\" backup but your device is a \"%s\"!\n"
                   "    Please try with a \"%s\" SPI backup.\n", str_backup_dev_type, str_dev_type, str_dev_type);
            return;
        }
    for (int i = 28; i < 36; i++)
        if (validation_magic[i] != backup_spi[0x1FF4 + i - 22]) {
            ota_exists = false;
            break;
        }
    for (int i = 22; i < 28; i++) {
        if (validation_magic[i] != backup_spi[0x10000 + i - 22] && i != 23) {
            validation_check = false;
            break;
        }
        if (ota_exists && validation_magic[i] != backup_spi[0x28000 + i - 22] && i != 23) {
            validation_check = false;
            break;
        }
    }
    if (!validation_check) {
        printf("[-] Corrupt backup! Please try another backup.\n");
        return;
    }

    u8 mac_addr[10] = { 0 };
    get_device_info(mac_addr);
    for (int i = 4; i < 10; i++)
        if (mac_addr[i] != backup_spi[0x1A - i + 4])
            mac_check = false;
    printf("Backup of %02x:%02x:%02x:%02x:%02x:%02x loaded.\n",
        backup_spi[0x1A], backup_spi[0x19], backup_spi[0x18], backup_spi[0x17], backup_spi[0x16], backup_spi[0x15]);
    if (!mac_check) {
        printf("Different BT MAC address! The SPI backup is from another \"%s\".\n", str_dev_type);
        if (!ask_yes_no("Do you want to continue?"))
            return;
        allow_full_restore = false;
    }

    printf("\n 1. Restore Color\n 2. Restore S/N\n 3. Restore user calibration\n 4. Factory reset user calibration\n");
    printf(allow_full_restore ? " 5. Full restore (factory configuration and user calibration)\n"
                              : " 5. Full restore: disabled, the backup does not match your device\n");
    printf(" 0. Back\n");
    int choice = ask_int("Select", 0, 0, 5);
    if (choice == 0 || (choice == 5 && !allow_full_restore))
        return;

    bool rst_l = false, rst_r = false, rst_sensor = false;
    if (choice == 3 || choice == 4) {
        if (handle_type != JOYCON_R) rst_l = ask_yes_no("  Left stick calibration?");
        if (handle_type != JOYCON_L) rst_r = ask_yes_no("  Right stick calibration?");
        rst_sensor = ask_yes_no("  6-axis (acc/gyro) calibration?");
    }

    set_led_busy();
    int error = 0;
    if (choice == 1) {
        printf("The device color will be restored with the backup values!\n");
        if (!ask_yes_no("Are you sure you want to continue?"))
            return;
        error = write_spi_data(0x6050, 12, &backup_spi[0x6050]);
        show_colors(); //Check that the colors were written
        send_rumble();
        if (error == 0)
            printf("[+] The colors were restored!\n");
    }
    else if (choice == 2) {
        printf("The serial number will be restored with the backup values!\n"
               "*Make sure that this backup was your original one!\n");
        if (!ask_yes_no("Are you sure you want to continue?"))
            return;
        error = write_spi_data(0x6000, 0x10, &backup_spi[0x6000]);
        send_rumble();
        if (error == 0) {
            if (handle_type != PROCON)
                printf("[+] The serial number was restored and changed to \"%s\"!\n", get_sn(0x6001, 0xF).c_str());
            else
                printf("[+] The serial number was restored!\n");
        }
    }
    else if (choice == 3 || choice == 4) {
        printf(choice == 3 ? "The selected user calibration will be restored from the backup!\n"
                           : "The selected user calibration will be factory resetted!\n");
        if (!ask_yes_no("Are you sure you want to continue?"))
            return;
        u8 l_stick[0xB], r_stick[0xB], sensor[0x1A];
        if (choice == 3) {
            memcpy(l_stick, &backup_spi[0x8010], 0xB);
            memcpy(r_stick, &backup_spi[0x801B], 0xB);
            memcpy(sensor, &backup_spi[0x8026], 0x1A);
        }
        else {
            memset(l_stick, 0xFF, 0xB);
            memset(r_stick, 0xFF, 0xB);
            memset(sensor, 0xFF, 0x1A);
        }
        if (handle_type != JOYCON_R && rst_l)
            error = write_spi_data(0x8010, 0xB, l_stick);
        msleep(100);
        if (handle_type != JOYCON_L && rst_r && error == 0)
            error = write_spi_data(0x801B, 0xB, r_stick);
        msleep(100);
        if (rst_sensor && error == 0)
            error = write_spi_data(0x8026, 0x1A, sensor);
        send_rumble();
        if (error == 0)
            printf(choice == 3 ? "[+] The user calibration was restored!\n" : "[+] The user calibration was factory resetted!\n");
    }
    else if (choice == 5) {
        printf("This will do a full restore of the Factory configuration and User calibration!\n");
        if (!ask_yes_no("Are you sure you want to continue?"))
            return;
        printf("Restoring Factory Configuration and User Calibration... Don't disconnect your device!\n");
        u8 sn_backup_erase[0x10];
        memset(sn_backup_erase, 0xFF, 16);

        // Factory Configuration Sector 0x6000
        for (int i = 0x00; i < 0x1000 && error == 0; i = i + 0x10) {
            error = write_spi_data(0x6000 + i, 0x10, &backup_spi[0x6000 + i]);
            printf("\r  %.2fKB of 8KB", i / 1024.0f);
            fflush(stdout);
            msleep(60);
        }
        // User Calibration Sector 0x8000
        for (int i = 0x00; i < 0x1000 && error == 0; i = i + 0x10) {
            error = write_spi_data(0x8000 + i, 0x10, &backup_spi[0x8000 + i]);
            printf("\r  %.2fKB of 8KB", 4 + (i / 1024.0f));
            fflush(stdout);
            msleep(60);
        }
        // Erase S/N backup storage
        if (error == 0)
            error = write_spi_data(0xF000, 0x10, sn_backup_erase);
        printf("\r  %.2fKB of 8KB\n", 0x2000 / 1024.0f);

        if (error == 0) {
            u8 custom_cmd[44];
            // Set shipment
            memset(custom_cmd, 0, sizeof(custom_cmd));
            custom_cmd[0] = 0x01; custom_cmd[5] = 0x08; custom_cmd[6] = 0x01;
            send_custom_command(custom_cmd);
            // Clear pairing info
            memset(custom_cmd, 0, sizeof(custom_cmd));
            custom_cmd[0] = 0x01; custom_cmd[5] = 0x07;
            send_custom_command(custom_cmd);
            // Reboot controller and go into pairing mode
            memset(custom_cmd, 0, sizeof(custom_cmd));
            custom_cmd[0] = 0x01; custom_cmd[5] = 0x06; custom_cmd[6] = 0x02;
            send_custom_command(custom_cmd);

            send_rumble();
            printf("[+] The full restore was completed!\n"
                   "    The controller was rebooted and it is now in pairing mode!\n"
                   "    Pair it with the Switch or PC again.\n");
            hid_close(handle);
            handle = NULL;
            handle_type = NOTHING;
            return;
        }
    }
    if (error != 0)
        printf("[-] Failed to restore or restore incomplete! Please try again..\n");
}

// ---------------------------------------------------------------------------------------------
// Serial number (btn_changeSN_Click, btn_restoreSN_Click)

void cmd_serial(void) {
    if (!need_device())
        return;
    if (handle_type == PROCON) {
        printf("[-] Changing or restoring the S/N is not supported for Pro Controllers!\n");
        return;
    }
    printf("\nCurrent S/N: %s\n\n 1. Change S/N\n 2. Restore S/N from the backup inside the controller\n 0. Back\n", get_sn(0x6001, 0xF).c_str());
    int choice = ask_int("Select", 0, 0, 2);
    if (choice == 1) {
        std::string new_sn;
        while (true) {
            new_sn = read_line("New S/N (up to 15 ASCII characters): ");
            bool ok = !new_sn.empty() && new_sn.size() <= 15;
            for (unsigned char ch : new_sn)
                if (ch < 32 || ch > 126)
                    ok = false;
            if (ok)
                break;
            printf("  Use 1-15 non-extended ASCII characters.\n");
        }
        printf("This will change your Serial Number! Make a backup first!\n");
        if (!ask_yes_no("Are you sure you want to continue?") || !ask_yes_no("Did you make a backup?"))
            return;

        int error = 0;
        int sn_ok = 1;
        const u8 sn_magic[] = { 0x00, 0x00, 0x58 };
        u8 spi_sn[0x10], sn_backup[1] = { 0 };
        memset(spi_sn, 0x11, 16);
        //Check if sn is original
        get_spi_data(0x6000, 0x10, spi_sn);
        for (int i = 0; i < 3; i++)
            if (spi_sn[i] != sn_magic[i]) {
                sn_ok = 0;
                break;
            }
        //Check if already made
        get_spi_data(0xF000, 0x1, sn_backup);
        if (sn_ok != 0 && sn_backup[0] == 0xFF)
            error = write_spi_data(0xF000, 0x10, spi_sn);
        msleep(100);
        u8 sn[16] = { 0 };
        memcpy(sn + 16 - new_sn.size(), new_sn.data(), new_sn.size());
        if (error == 0)
            error = write_spi_data(0x6000, 0x10, sn);
        send_rumble();
        if (error == 0)
            printf("[+] The S/N was written to the device! The new S/N is now \"%s\"!\n"
                   "    A backup of your S/N was created inside the SPI.\n", get_sn(0x6001, 0xF).c_str());
        else
            printf("[-] Failed to write the S/N to the device!\n");
    }
    else if (choice == 2) {
        if (!ask_yes_no("Do you really want to restore it from the S/N backup inside your controller's SPI?"))
            return;
        u8 spi_sn[0x10];
        memset(spi_sn, 0x11, 16);
        //Check if there is an SN backup
        get_spi_data(0xF000, 0x10, spi_sn);
        msleep(100);
        if (spi_sn[0] != 0x00) {
            printf("[-] No S/N backup found inside your controller's SPI.\n"
                   "    This can happen if the first time you changed your S/N was with an older version\n"
                   "    of Joy-Con Toolkit. Otherwise, you never changed your S/N.\n");
            return;
        }
        int error = write_spi_data(0x6000, 0x10, spi_sn);
        send_rumble();
        if (error == 0)
            printf("[+] The S/N was restored to the device! The new S/N is now \"%s\"!\n", get_sn(0x6001, 0xF).c_str());
        else
            printf("[-] Failed to restore the S/N!\n");
    }
}

// ---------------------------------------------------------------------------------------------
// Button test (btn_runBtnTest_Click)

void cmd_button_test(void) {
    if (!need_device())
        return;
    printf("\n");
    enable_button_test = true;
    button_test();
    enable_button_test = false;
    printf("\n");
}

// ---------------------------------------------------------------------------------------------
// HD Rumble (btn_loadVib_Click, btn_vibPlay_Click, the easter egg tunes)

static std::vector<u8> vib_loaded, vib_converted;

void cmd_hd_rumble(void) {
    if (!need_device())
        return;
    printf("\n 1. Play an HD Rumble file (.bnvib, .jcvib)\n 2. Tune: Super Mario Bros.\n 3. Tune: Super Mario Odyssey \"OK\"\n 0. Back\n");
    int choice = ask_int("Select", 0, 0, 3);
    if (choice == 2 || choice == 3) {
        printf("Get the controller near your ear. If the tune is slow or choppy, get closer to the\n"
               "Bluetooth adapter and keep line of sight.\n");
        set_led_busy();
        play_tune(choice == 2 ? 0 : 1);
        send_rumble();
        printf("[+] The HD Rumble music has ended.\n");
        return;
    }
    if (choice != 1)
        return;

    std::string path = read_line("File: ");
    if (path.empty() || !load_file(path, vib_loaded))
        return;
    if (vib_loaded.size() < 0x18) {
        printf("[-] Type: Unknown format\n");
        return;
    }
    vib_converted = vib_loaded;
    vib_loaded_file = vib_loaded.data();
    vib_file_converted = vib_converted.data();
    const u8 file_magic[] = { 0x52, 0x52, 0x41, 0x57, 0x4, 0xC, 0x3, 0x10 };
    int vib_file_type = 0;
    u16 vib_sample_rate = 0;
    u32 vib_samples = 0, vib_loop_start = 0, vib_loop_end = 0, vib_loop_wait = 0, vib_size = 0;
    const u8 *f = vib_loaded_file;

    //check for vib_file_type
    if (f[0] == file_magic[0]) {
        vib_file_type = (f[1] == file_magic[1] && f[2] == file_magic[2] && f[3] == file_magic[3]) ? 1 : 0;
        if (vib_file_type == 1) {
            vib_sample_rate = (u16)((f[0x4] << 8) + f[0x5]);
            vib_samples = (u32)((f[0x6] << 24) + (f[0x7] << 16) + (f[0x8] << 8) + f[0x9]);
            printf("Type: Raw HD Rumble\n");
        }
    }
    else if (f[4] == file_magic[6]) {
        u16 rate = (u16)(f[0x6] + (f[0x7] << 8));
        vib_sample_rate = rate ? (u16)(1000 / rate) : 0;
        if (f[0] == file_magic[4]) {
            vib_file_type = 2;
            vib_size = (u32)(f[0x8] + (f[0x9] << 8) + (f[0xA] << 16) + (f[0xB] << 24));
            printf("Type: Binary HD Rumble\n");
        }
        else if (f[0] == file_magic[5]) {
            vib_file_type = 3;
            vib_size = (u32)(f[0x10] + (f[0x11] << 8) + (f[0x12] << 16) + (f[0x13] << 24));
            vib_loop_start = (u32)(f[0x8] + (f[0x9] << 8) + (f[0xA] << 16) + (f[0xB] << 24));
            vib_loop_end = (u32)(f[0xC] + (f[0xD] << 8) + (f[0xE] << 16) + (f[0xF] << 24));
            printf("Type: Loop Binary HD Rumble\n");
        }
        else if (f[0] == file_magic[7]) {
            vib_file_type = 4;
            vib_size = (u32)(f[0x14] + (f[0x15] << 8) + (f[0x16] << 16) + (f[0x17] << 24));
            vib_loop_start = (u32)(f[0x8] + (f[0x9] << 8) + (f[0xA] << 16) + (f[0xB] << 24));
            vib_loop_end = (u32)(f[0xC] + (f[0xD] << 8) + (f[0xE] << 16) + (f[0xF] << 24));
            vib_loop_wait = (u32)(f[0x10] + (f[0x11] << 8) + (f[0x12] << 16) + (f[0x13] << 24));
            printf("Type: Loop and Wait Binary\n");
        }
        vib_samples = vib_size / 4;
    }
    if (vib_file_type == 0 || vib_sample_rate == 0) {
        printf("[-] Type: Unknown format\n");
        return;
    }
    // The samples must be inside the file (the window trusted the header)
    u32 data_off = vib_file_type == 1 ? 0xA : vib_file_type == 2 ? 0xC : vib_file_type == 3 ? 0x14 : 0x18;
    if ((u64)data_off + (u64)vib_samples * 4 > vib_loaded.size() || vib_loop_start > vib_loop_end || vib_loop_end > vib_samples) {
        printf("[-] The file is shorter than its header says, or its loop points are invalid.\n");
        return;
    }
    printf("Sample rate: %dms\nSamples: %u (%.2fs)\n", vib_sample_rate, vib_samples, (vib_sample_rate * vib_samples) / 1000.0f);

    int vib_loop_times = 0;
    if (vib_file_type == 3 || vib_file_type == 4)
        vib_loop_times = ask_int("Loop times", 0, 0, 999);
    if (vib_file_type >= 2) {
        printf("Equalizer (percent, 100 = unchanged):\n");
        int lf_amp  = ask_int("  Low frequency amplitude  (0-200)", 100, 0, 200) / 10;
        int lf_freq = ask_int("  Low frequency pitch      (0-200)", 100, 0, 200) / 10;
        int hf_amp  = ask_int("  High frequency amplitude (0-200)", 100, 0, 200) / 10;
        int hf_freq = ask_int("  High frequency pitch     (0-200)", 100, 0, 200) / 10;
        //Convert to RAW vibration, apply EQ and clamp inside safe values
        hd_rumble_convert(vib_file_type, vib_samples, lf_amp, lf_freq, hf_amp, hf_freq);
    }
    printf("Playing... (Enter to skip to the end)\n");
    play_hd_rumble_file(vib_file_type, vib_sample_rate, (int)vib_samples, (int)vib_loop_start, (int)vib_loop_end, (int)vib_loop_wait, vib_loop_times);
    printf("[+] Done\n");
}

// ---------------------------------------------------------------------------------------------
// IR camera

static void ir_print_settings() {
    printf("\n=== IR camera ===\n");
    printf(" 1. Capture (saves IRcamera.png)\n");
    printf(" 2. Stream (updates IRstream.png, Enter to stop)\n");
    printf(" 3. Resolution:        %s%s\n", ir_res_names[ir.resolution], ir.auto_exposure && ir.resolution == 3 ? " (60x80 with auto exposure)" : "");
    printf(" 4. Mode:              %s\n", ir_mode_names[ir.mode]);
    printf(" 5. Colorize:          %s\n", ir_color_names[ir.colorize]);
    printf(" 6. Far/Narrow leds:   %s, intensity %d/15\n", ir.leds_far ? "on" : "off", ir.intensity_far);
    printf(" 7. Near/Wide leds:    %s, intensity %d/16\n", ir.leds_near ? "on" : "off", ir.intensity_near);
    printf(" 8. Flashlight mode:   %s\n", ir.flashlight ? "on" : "off");
    printf(" 9. Strobe flash mode: %s\n", ir.strobe ? "on" : "off");
    printf("10. External IR filter:%s\n", ir.ex_filter ? " on" : " off");
    printf("11. Selfie mode:       %s\n", ir.selfie ? "on" : "off");
    printf("12. Exposure:          %dus\n", ir.exposure);
    printf("13. Auto exposure:     %s (streaming; Capture always uses it unless Quick capture)\n", ir.auto_exposure ? "on" : "off");
    printf("14. Digital gain:      %d (streaming without auto exposure)\n", ir.gain);
    printf("15. De-noise:          %s, edge smoothing %d, color interpolation %d\n", ir.denoise ? "on" : "off", ir.edge_smoothing, ir.color_interpolation);
    printf("16. Quick capture:     %s\n", ir_quick_capture ? "on" : "off");
    printf("17. Terminal preview:  %s\n", ir_show_preview ? "on" : "off");
    printf(" 0. Back\n");
}

// prepareSendIRConfig(true) and the Capture / Stream buttons
static void ir_run(bool stream) {
    static const char *errors[] = { "", "1ID31", "2MCUON", "3MCUONBUSY", "4MCUMODESET", "5MCUSETBUSY",
                                    "6IRMODESET", "7IRSETBUSY", "8IRCFG", "9IRFCFG", "10IRNOCFG" };
    ir_image_config cfg;
    enable_IRVideoPhoto = stream;
    build_ir_config(cfg, true);
    ir_exposure_value = ir.exposure;
    ir_frames_shown = 0;
    ir_last_capture.clear();
    live_command.clear();
    trace_note(stream ? "IR: configure (stream)" : "IR: configure (capture)");
    printf("Status: Configuring%s\n", stream ? "" : " (a capture takes up to ~30s at 240x320)");
    int res = ir_sensor(cfg);

    // The camera occasionally keeps its previous settings (e.g. resolution). Set it
    // up again and capture (or stream) once more; if it still didn't apply them, say so.
    // A stream stopped with Enter in the meantime isn't restarted.
    if (res == 0 && ir_last_capture_stale && (!stream || enable_IRVideoPhoto)) {
        trace_note("IR: camera didn't apply the settings, setting it up again");
        printf("\nStatus: Camera didn't apply the settings, retrying..\n");
        res = ir_sensor(cfg);
        if (res == 0 && ir_last_capture_stale)
            res = 10;
    }
    enable_IRVideoPhoto = false;
    printf("\n");
    if (res == 10)
        printf("Status: Camera didn't apply the settings. Try again\n");
    else if (res > 0)
        printf("Status: Error %s!\n", res <= 10 ? errors[res] : "?");
    else if (!stream) {
        if (!ir_last_capture.empty())
            ir_print_preview(ir_last_capture, ir_last_capture_w, ir_last_capture_h, false);
        printf("Status: Done! Saved to IRcamera.png\n");
    }
    else
        printf("Status: Standby\n");
}

void cmd_ir_camera(void) {
    if (!need_device())
        return;
    if (handle_type != JOYCON_R) {
        printf("[-] The IR camera is only on the Joy-Con (R).\n");
        return;
    }
    while (true) {
        ir_print_settings();
        int c = ask_int("Select", 0, 0, 17);
        switch (c) {
            case 0: return;
            case 1: ir_run(false); break;
            case 2: ir_run(true); break;
            case 3: ir.resolution = ask_int("Resolution (0: 240x320, 1: 120x160, 2: 60x80, 3: 30x40)", ir.resolution, 0, 3); break;
            case 4: ir.mode = ask_int("Mode (0: Capture, 1: Pointing, 2: Clustering)", ir.mode, 0, 2); break;
            case 5: ir.colorize = ask_int("Colorize (0: Greyscale, 1: Night vision, 2: Ironbow, 3: Infrared)", ir.colorize, 0, 3); break;
            case 6: ir.leds_far = ask_yes_no("Far/Narrow (75\u00B0) leds on?");
                    if (ir.leds_far)
                        ir.intensity_far = ask_int("Intensity (0-15)", ir.intensity_far, 0, 15);
                    break;
            case 7: ir.leds_near = ask_yes_no("Near/Wide (130\u00B0) leds on?");
                    if (ir.leds_near)
                        ir.intensity_near = ask_int("Intensity (0-16)", ir.intensity_near, 0, 16);
                    break;
            case 8: ir.flashlight = !ir.flashlight; break;
            case 9: ir.strobe = !ir.strobe; break;
            case 10: ir.ex_filter = !ir.ex_filter; break;
            case 11: ir.selfie = !ir.selfie; break;
            case 12: ir.exposure = ask_int("Exposure in us (0-600)", ir.exposure, 0, 600); break;
            case 13: ir.auto_exposure = !ir.auto_exposure; break;
            case 14: ir.gain = ask_int("Digital gain (1-20, lossy)", ir.gain, 1, 20); break;
            case 15: ir.denoise = ask_yes_no("De-noise on?");
                     if (ir.denoise) {
                         ir.edge_smoothing = ask_int("Edge smoothing (0-255)", ir.edge_smoothing, 0, 255);
                         ir.color_interpolation = ask_int("Color interpolation (0-255)", ir.color_interpolation, 0, 255);
                     }
                     break;
            case 16: ir_quick_capture = !ir_quick_capture;
                     if (ir_quick_capture)
                         printf("Quick capture: no auto exposure adjustment, uses the exposure as set, saves the 2nd frame.\n");
                     break;
            case 17: ir_show_preview = !ir_show_preview; break;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// NFC (btn_NFC_Click)

void cmd_nfc(void) {
    if (!need_device())
        return;
    if (handle_type == JOYCON_L) {
        printf("[-] NFC is only on the Joy-Con (R) and the Pro Controller.\n");
        return;
    }
    static const char *errors[] = { "", "1ID31", "2MCUON", "3MCUONBUSY", "4MCUMODESET", "5MCUSETBUSY", "6NFCPOLL" };
    printf("\nTouch the NFC area with an amiibo or NFC tag... (Enter to stop)\n");
    enable_NFCScanning = true;
    int res = nfc_tag_info();
    if (res > 0 && res <= 6)
        printf("Error %s!\n", errors[res]);
    enable_NFCScanning = false;
}

// ---------------------------------------------------------------------------------------------
// Debug custom command (btn_dbgSendCmd_Click)

static int parse_hex(const std::string &text) {
    char *end = NULL;
    long v = strtol(text.c_str(), &end, 16);
    return (end && end != text.c_str()) ? (int)v : 0;
}

void cmd_debug(void) {
    if (!need_device())
        return;
    printf("\nAll values in hex. Empty input keeps the default.\n"
           "Command: 01 subcommand (with rumble data), 10 rumble only, 11 MCU/IR/NFC.\n");
    u8 test[44];
    memset(test, 0, sizeof(test));
    std::string v;
    v = read_line("Command [01]: ");            test[0] = (u8)(v.empty() ? 0x01 : parse_hex(v));
    v = read_line("Rumble HF frequency [00]: "); test[1] = (u8)parse_hex(v);
    v = read_line("Rumble HF amplitude [01]: "); test[2] = (u8)(v.empty() ? 0x01 : parse_hex(v));
    v = read_line("Rumble LF frequency [40]: "); test[3] = (u8)(v.empty() ? 0x40 : parse_hex(v));
    v = read_line("Rumble LF amplitude [40]: "); test[4] = (u8)(v.empty() ? 0x40 : parse_hex(v));
    v = read_line("Subcommand [00]: ");         test[5] = (u8)parse_hex(v);
    std::string ss_arguments = read_line("Arguments (hex bytes, e.g. 0102A0): ");

    // The window's argument parser: pairs of hex digits, a leading low nibble if odd
    u8 get_i = 0;
    size_t pos = 0;
    char i_getarg;
    for (char &ch : ss_arguments)
        if (ch >= 'a' && ch <= 'f')
            ch -= 32;
    std::string clean;
    for (char ch : ss_arguments)
        if ((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F'))
            clean += ch;
    ss_arguments = clean;
    //Get Low nibble if odd number of characters
    if (ss_arguments.length() % 2 != 0) {
        i_getarg = ss_arguments[pos++];
        if (i_getarg >= 'A' && i_getarg <= 'F')
            test[6] = (u8)((i_getarg - '7') & 0xF);
        else if (i_getarg <= '9' && i_getarg >= '0')
            test[6] = (u8)((i_getarg - '0') & 0xF);
        get_i++;
    }
    // Only 38 argument bytes fit in the 44 byte command buffer.
    while (pos < ss_arguments.length() && 6 + get_i < 44) {
        i_getarg = ss_arguments[pos++];
        //Get High nibble
        if (i_getarg >= 'A' && i_getarg <= 'F')
            test[6 + get_i] = (u8)(((i_getarg - '7') << 4) & 0xF0);
        else if (i_getarg <= '9' && i_getarg >= '0')
            test[6 + get_i] = (u8)(((i_getarg - '0') << 4) & 0xF0);
        //Get Low nibble
        if (pos < ss_arguments.length()) {
            i_getarg = ss_arguments[pos++];
            if (i_getarg >= 'A' && i_getarg <= 'F')
                test[6 + get_i] += (u8)((i_getarg - '7') & 0xF);
            else if (i_getarg <= '9' && i_getarg >= '0')
                test[6 + get_i] += (u8)((i_getarg - '0') & 0xF);
        }
        get_i++;
    }
    send_custom_command(test);
}

// ---------------------------------------------------------------------------------------------
// HID listing (-l) and disconnect

static void cmd_list_hid(void) {
    struct hid_device_info *devs = hid_enumerate(0x0, 0x0);
    for (struct hid_device_info *cur_dev = devs; cur_dev; cur_dev = cur_dev->next) {
        std::string product = wstr(cur_dev->product_string), maker = wstr(cur_dev->manufacturer_string), serial = wstr(cur_dev->serial_number);
        printf("HID Device: 0x%04x \"%s\"\n", cur_dev->product_id, product.empty() ? "Unknown Product" : product.c_str());
        printf("\tvendor = 0x%04x \"%s\"\n", cur_dev->vendor_id, maker.empty() ? "Unknown Manufacturer" : maker.c_str());
        printf("\trelease = %d\n", cur_dev->release_number);
        printf("\tserial = %s\n", serial.empty() ? "Unknown Serial Number" : serial.c_str());
        printf("\tusage = 0x%04x page: 0x%x\n", cur_dev->usage, cur_dev->usage_page);
        printf("%s\n\n", cur_dev->path);
    }
    hid_free_enumeration(devs);
}

static void cmd_disconnect(void) {
    if (!handle)
        return;
    if (!ask_yes_no("Disconnect the controller (it turns off its Bluetooth connection)?"))
        return;
    u8 custom_cmd[44];
    memset(custom_cmd, 0, sizeof(custom_cmd));
    custom_cmd[0] = 0x01;
    custom_cmd[5] = 0x06;
    custom_cmd[6] = 0x00;
    send_custom_command(custom_cmd);
    hid_close(handle);
    handle = NULL;
    handle_type = NOTHING;
}

// ---------------------------------------------------------------------------------------------

int main(int argc, char **argv) {
    int choice;
    bool list_hid = false;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "-l") == 0)
            list_hid = true;                 // List all HID devices first
        else if (strcmp(argv[i], "-d") == 0)
            enable_traffic_dump = true;      // Log all controller traffic to traffic_log.txt
        else {
            printf("Usage: %s [-l] [-d]\n  -l  list all HID devices first\n  -d  log controller traffic to traffic_log.txt\n", argv[0]);
            return 1;
        }
    }

    printf("Joy-Con Toolkit (Linux CLI - Interactive)\n");
    printf("==========================================\n\n");

    if (hid_init()) {
        fprintf(stderr, "[-] hid_init() failed\n");
        return 1;
    }
    printf("[+] HID API initialized\n");
    if (list_hid)
        cmd_list_hid();

    // Connect right away when a controller is there, like the window does
    if (!find_controllers().empty())
        select_device();

    while (1) {
        print_menu();
        int res = read_number("%d", &choice);
        if (res == -2)
            choice = 0; // End of input
        else if (res != 0)
            choice = -1;

        switch (choice) {
            case 1:  select_device(); break;
            case 2:  cmd_device_info(); break;
            case 3:  cmd_battery_status(); break;
            case 4:  cmd_set_player_led(); break;
            case 5:  cmd_test_vibration(); break;
            case 6:  cmd_read_calibration(); break;
            case 7:  cmd_colors(); break;
            case 8:  cmd_backup(); break;
            case 9:  cmd_restore(); break;
            case 10: cmd_serial(); break;
            case 11: cmd_edit_calibration(); break;
            case 12: cmd_button_test(); break;
            case 13: cmd_hd_rumble(); break;
            case 14: cmd_ir_camera(); break;
            case 15: cmd_nfc(); break;
            case 16: cmd_debug(); break;
            case 17: cmd_list_hid(); break;
            case 18: cmd_disconnect(); break;
            case 0:
                printf("\n[*] Exiting...\n");
                quit_program(0);
                break;
            default:
                printf("[-] Invalid choice\n");
        }
    }

    return 0;
}

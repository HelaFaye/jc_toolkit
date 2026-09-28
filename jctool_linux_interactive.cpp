#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <hidapi/hidapi.h>
#include <unistd.h>
#include <stdint.h>

#define VENDOR_ID_NINTENDO  0x057E
#define PRODUCT_JOY_CON_L   0x2006
#define PRODUCT_JOY_CON_R   0x2007
#define PRODUCT_PRO         0x2009

typedef uint8_t u8;
typedef uint16_t u16;
typedef uint32_t u32;

hid_device *handle = NULL;
u16 handle_product_id = 0;
u8 timing_byte = 0;

// Neutral rumble data, sent with every subcommand (like Joy-Con Toolkit).
static const u8 rumble_neutral[8] = { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };

void print_menu(void) {
    printf("\n");
    printf("=== Joy-Con Toolkit (Linux CLI) ===\n");
    printf("1. Detect and select device\n");
    printf("2. Get device info\n");
    printf("3. Get battery status\n");
    printf("4. Set player LED\n");
    printf("5. Test vibration\n");
    printf("6. Read calibration data\n");
    printf("7. Exit\n");
    printf("Select: ");
}

// Reads a number from stdin. Returns 0 on success, -1 on bad input, -2 at end of input.
int read_number(const char *format, int *value) {
    char line[64];
    if (!fgets(line, sizeof(line), stdin))
        return -2;
    return sscanf(line, format, value) == 1 ? 0 : -1;
}

static int is_supported(struct hid_device_info *dev) {
    return dev->product_id == PRODUCT_JOY_CON_L ||
           dev->product_id == PRODUCT_JOY_CON_R ||
           dev->product_id == PRODUCT_PRO;
}

static const char *product_name(u16 product_id) {
    switch (product_id) {
        case PRODUCT_JOY_CON_L: return "Joy-Con (L)";
        case PRODUCT_JOY_CON_R: return "Joy-Con (R)";
        case PRODUCT_PRO:       return "Pro Controller";
        default:                return "Unknown";
    }
}

int select_device(void) {
    struct hid_device_info *devs, *cur_dev;
    int device_count = 0;
    int choice;

    devs = hid_enumerate(VENDOR_ID_NINTENDO, 0);
    if (!devs) {
        fprintf(stderr, "[-] No Nintendo devices found\n");
        return -1;
    }

    printf("\n=== Available Devices ===\n");
    for (cur_dev = devs; cur_dev; cur_dev = cur_dev->next) {
        if (!is_supported(cur_dev))
            continue;
        // hidraw gives Bluetooth devices an empty product string, so use the product id.
        printf("[%d] %s", device_count, product_name(cur_dev->product_id));
        if (cur_dev->serial_number && cur_dev->serial_number[0])
            printf(" (%ls)", cur_dev->serial_number);
        printf("  %s\n", cur_dev->path);
        device_count++;
    }

    if (device_count == 0) {
        fprintf(stderr, "[-] No Joy-Con or Pro Controller found\n");
        hid_free_enumeration(devs);
        return -1;
    }

    printf("\nSelect device [0-%d]: ", device_count - 1);
    if (read_number("%d", &choice) != 0 || choice < 0 || choice >= device_count) {
        fprintf(stderr, "[-] Invalid selection\n");
        hid_free_enumeration(devs);
        return -1;
    }

    int idx = 0;
    for (cur_dev = devs; cur_dev; cur_dev = cur_dev->next) {
        if (!is_supported(cur_dev))
            continue;
        if (idx++ != choice)
            continue;
        if (handle)
            hid_close(handle);
        handle = hid_open_path(cur_dev->path);
        if (handle) {
            handle_product_id = cur_dev->product_id;
            printf("[+] Connected to %s\n", product_name(handle_product_id));
            hid_free_enumeration(devs);
            return 0;
        }
        fprintf(stderr, "[-] Failed to open device (permissions? see linux/udev)\n");
        hid_free_enumeration(devs);
        return -1;
    }

    hid_free_enumeration(devs);
    return -1;
}

int send_command(u8 cmd, u8 subcmd, const u8 *args, int arg_len) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return -1;
    }

    u8 buf[49];
    memset(buf, 0, 49);
    buf[0] = cmd;
    buf[1] = timing_byte & 0xF;
    timing_byte++;
    memcpy(buf + 2, rumble_neutral, 8);
    buf[10] = subcmd;

    if (args && arg_len > 0)
        memcpy(buf + 11, args, arg_len > 38 ? 38 : arg_len);

    return hid_write(handle, buf, 49);
}

// Sends a subcommand and waits for its reply (input report 0x21 echoing the subcommand).
// Other input reports that arrive meanwhile are skipped. Returns the reply length, or -1.
int subcommand(u8 subcmd, const u8 *args, int arg_len, u8 *reply, int reply_len) {
    for (int attempt = 0; attempt < 3; attempt++) {
        if (send_command(0x01, subcmd, args, arg_len) < 0)
            return -1;
        for (int reads = 0; reads < 20; reads++) {
            memset(reply, 0, reply_len);
            int res = hid_read_timeout(handle, reply, reply_len, 100);
            if (res < 0)
                return -1;
            if (res == 0)
                break;
            if (reply[0] == 0x21 && reply[14] == subcmd)
                return res;
        }
    }
    return -1;
}

void cmd_device_info(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    printf("\n[*] Getting device info...\n");
    u8 buf[64];
    if (subcommand(0x02, NULL, 0, buf, sizeof(buf)) < 0) {
        printf("[-] No response\n");
        return;
    }

    const char *type = buf[17] == 1 ? "Joy-Con (L)" : buf[17] == 2 ? "Joy-Con (R)" : buf[17] == 3 ? "Pro Controller" : "Unknown";
    printf("[+] Controller type: %s (%d)\n", type, buf[17]);
    printf("    Firmware: %X.%02X\n", buf[15], buf[16]);
    printf("    MAC: %02X:%02X:%02X:%02X:%02X:%02X\n",
           buf[19], buf[20], buf[21], buf[22], buf[23], buf[24]);
}

void cmd_battery_status(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    printf("\n[*] Getting battery status...\n");
    u8 buf[64];
    if (subcommand(0x50, NULL, 0, buf, sizeof(buf)) < 0) {
        printf("[-] No response\n");
        return;
    }

    // Input report byte 2, high nibble: level (0, 2, 4, 6, 8) + charging bit, as in Joy-Con Toolkit.
    u8 batt = buf[2] >> 4;
    static const char *levels[] = { "Empty", "Low", "Medium", "Good", "Full" };
    u16 volt = buf[15] | (buf[16] << 8);
    printf("[+] Battery: %s (%d/4)\n", levels[(batt >> 1) > 4 ? 4 : (batt >> 1)], batt >> 1);
    printf("[+] Charging: %s\n", (batt & 1) ? "Yes" : "No");
    printf("[+] Voltage: %.2fV\n", volt * 2.5 / 1000);
}

void cmd_set_player_led(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    int pattern;
    printf("\nLED patterns (combine by adding):\n");
    printf("  0x01: Player 1\n");
    printf("  0x02: Player 2\n");
    printf("  0x04: Player 3\n");
    printf("  0x08: Player 4\n");
    printf("  Add 0x10-0x80 to flash the same LEDs instead\n");
    printf("Enter pattern (hex): ");
    if (read_number("%x", &pattern) != 0 || pattern < 0 || pattern > 0xFF) {
        fprintf(stderr, "[-] Invalid pattern\n");
        return;
    }

    printf("[*] Setting player LED to 0x%02x...\n", pattern);
    u8 arg = (u8)pattern;
    u8 buf[64];
    if (subcommand(0x30, &arg, 1, buf, sizeof(buf)) >= 0)
        printf("[+] LED command acknowledged\n");
    else
        printf("[-] No response\n");
}

void cmd_test_vibration(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    printf("\n[*] Enabling vibration...\n");
    u8 arg = 0x01;
    u8 buf[64];
    if (subcommand(0x48, &arg, 1, buf, sizeof(buf)) < 0) {
        printf("[-] No response\n");
        return;
    }
    printf("[+] Vibration enabled\n");

    printf("[*] Sending test rumble pattern...\n");
    // Rumble-only output report 0x10; the same pattern for both sides (Joy-Con Toolkit's test tone).
    // The Joy-Con stops a rumble after a short while, so repeat it for about a second.
    for (int i = 0; i < 20; i++) {
        memset(buf, 0, 49);
        buf[0] = 0x10;
        buf[1] = timing_byte & 0xF;
        timing_byte++;
        buf[2] = 0xc2;
        buf[3] = 0xc8;
        buf[4] = 0x03;
        buf[5] = 0x72;
        memcpy(buf + 6, buf + 2, 4);
        hid_write(handle, buf, 49);
        usleep(50000);
    }

    // Back to neutral before disabling.
    memset(buf, 0, 49);
    buf[0] = 0x10;
    buf[1] = timing_byte & 0xF;
    timing_byte++;
    memcpy(buf + 2, rumble_neutral, 8);
    hid_write(handle, buf, 49);

    printf("[*] Disabling vibration...\n");
    arg = 0x00;
    subcommand(0x48, &arg, 1, buf, sizeof(buf));
    printf("[+] Vibration disabled\n");
}

// Reads len (max 0x1D) bytes of SPI flash at offset. Returns 0 on success.
int spi_read(u32 offset, u8 len, u8 *out) {
    u8 args[5] = { (u8)(offset & 0xFF), (u8)((offset >> 8) & 0xFF), (u8)((offset >> 16) & 0xFF), (u8)(offset >> 24), len };
    u8 buf[64];
    for (int attempt = 0; attempt < 3; attempt++) {
        if (subcommand(0x10, args, 5, buf, sizeof(buf)) < 0)
            continue;
        // Reply echoes the offset at 15..18 and the size at 19; the data follows from 20.
        if (memcmp(buf + 15, args, 4) == 0) {
            memcpy(out, buf + 20, len);
            return 0;
        }
    }
    return -1;
}

// Stick calibration: 3 pairs of 12-bit values packed in 9 bytes.
static void unpack_stick(const u8 *d, u16 out[6]) {
    for (int i = 0; i < 3; i++) {
        out[i * 2]     = (u16)(((d[i * 3 + 1] << 8) & 0xF00) | d[i * 3]);
        out[i * 2 + 1] = (u16)((d[i * 3 + 2] << 4) | (d[i * 3 + 1] >> 4));
    }
}

// Left stick data is ordered max-above-center, center, min-below-center; right stick is
// center, min-below-center, max-above-center.
static void print_stick(const char *name, const u8 *d, int left) {
    u16 v[6];
    unpack_stick(d, v);
    int c = left ? 2 : 0, lo = left ? 4 : 2, hi = left ? 0 : 4;
    printf("    %s: center X %4d Y %4d | range X -%d +%d | range Y -%d +%d\n",
           name, v[c], v[c + 1], v[lo], v[hi], v[lo + 1], v[hi + 1]);
}

void cmd_read_calibration(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    printf("\n[*] Reading calibration data (read-only)...\n");
    u8 d[0x1D];

    int has_left = handle_product_id != PRODUCT_JOY_CON_R;
    int has_right = handle_product_id != PRODUCT_JOY_CON_L;

    printf("[+] Factory stick calibration:\n");
    if (has_left) {
        if (spi_read(0x603D, 9, d) == 0) print_stick("Left stick ", d, 1);
        else printf("    Left stick: read failed\n");
    }
    if (has_right) {
        if (spi_read(0x6046, 9, d) == 0) print_stick("Right stick", d, 0);
        else printf("    Right stick: read failed\n");
    }

    printf("[+] User stick calibration:\n");
    if (has_left) {
        if (spi_read(0x8010, 11, d) != 0) printf("    Left stick: read failed\n");
        else if (d[0] == 0xB2 && d[1] == 0xA1) print_stick("Left stick ", d + 2, 1);
        else printf("    Left stick: none (factory is used)\n");
    }
    if (has_right) {
        if (spi_read(0x801B, 11, d) != 0) printf("    Right stick: read failed\n");
        else if (d[0] == 0xB2 && d[1] == 0xA1) print_stick("Right stick", d + 2, 0);
        else printf("    Right stick: none (factory is used)\n");
    }

    printf("[+] Factory 6-axis calibration:\n");
    if (spi_read(0x6020, 24, d) == 0) {
        int16_t v[12];
        for (int i = 0; i < 12; i++)
            v[i] = (int16_t)(d[i * 2] | (d[i * 2 + 1] << 8));
        printf("    Accel origin X %6d Y %6d Z %6d | sensitivity X %6d Y %6d Z %6d\n", v[0], v[1], v[2], v[3], v[4], v[5]);
        printf("    Gyro  origin X %6d Y %6d Z %6d | sensitivity X %6d Y %6d Z %6d\n", v[6], v[7], v[8], v[9], v[10], v[11]);
    }
    else {
        printf("    read failed\n");
    }
}

int main(void) {
    int choice;

    printf("Joy-Con Toolkit (Linux CLI - Interactive)\n");
    printf("==========================================\n\n");

    if (hid_init()) {
        fprintf(stderr, "[-] hid_init() failed\n");
        return 1;
    }
    printf("[+] HID API initialized\n");

    while (1) {
        print_menu();
        int res = read_number("%d", &choice);
        if (res == -2)
            choice = 7; // End of input
        else if (res != 0)
            choice = 0;

        switch (choice) {
            case 1:
                select_device();
                break;
            case 2:
                cmd_device_info();
                break;
            case 3:
                cmd_battery_status();
                break;
            case 4:
                cmd_set_player_led();
                break;
            case 5:
                cmd_test_vibration();
                break;
            case 6:
                cmd_read_calibration();
                break;
            case 7:
                printf("\n[*] Exiting...\n");
                if (handle)
                    hid_close(handle);
                hid_exit();
                return 0;
            default:
                printf("[-] Invalid choice\n");
        }
    }

    return 0;
}

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
u8 timing_byte = 0;

struct device_info {
    char product[256];
    char serial[256];
    u16 vendor_id;
    u16 product_id;
    hid_device_info *hid_info;
};

void print_menu(void) {
    printf("\n");
    printf("=== Joy-Con Toolkit (Linux CLI) ===\n");
    printf("1. Detect and select device\n");
    printf("2. Get device info\n");
    printf("3. Get battery status\n");
    printf("4. Set player LED\n");
    printf("5. Test vibration\n");
    printf("6. Read calibration data (stub)\n");
    printf("7. Exit\n");
    printf("Select: ");
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
    cur_dev = devs;
    while (cur_dev) {
        if ((cur_dev->product_id == PRODUCT_JOY_CON_L ||
             cur_dev->product_id == PRODUCT_JOY_CON_R ||
             cur_dev->product_id == PRODUCT_PRO) &&
            cur_dev->product_string) {

            printf("[%d] %ls ", device_count, cur_dev->product_string);
            if (cur_dev->serial_number)
                printf("(SN: %ls)", cur_dev->serial_number);
            printf("\n");
            device_count++;
        }
        cur_dev = cur_dev->next;
    }

    if (device_count == 0) {
        fprintf(stderr, "[-] No Joy-Con or Pro Controller found\n");
        hid_free_enumeration(devs);
        return -1;
    }

    printf("\nSelect device [0-%d]: ", device_count - 1);
    scanf("%d", &choice);
    getchar();

    if (choice < 0 || choice >= device_count) {
        fprintf(stderr, "[-] Invalid selection\n");
        hid_free_enumeration(devs);
        return -1;
    }

    cur_dev = devs;
    int idx = 0;
    while (cur_dev) {
        if ((cur_dev->product_id == PRODUCT_JOY_CON_L ||
             cur_dev->product_id == PRODUCT_JOY_CON_R ||
             cur_dev->product_id == PRODUCT_PRO) &&
            cur_dev->product_string) {

            if (idx == choice) {
                if (handle)
                    hid_close(handle);
                handle = hid_open_path(cur_dev->path);
                if (handle) {
                    printf("[+] Connected to device\n");
                    hid_free_enumeration(devs);
                    return 0;
                } else {
                    fprintf(stderr, "[-] Failed to open device\n");
                    hid_free_enumeration(devs);
                    return -1;
                }
            }
            idx++;
        }
        cur_dev = cur_dev->next;
    }

    hid_free_enumeration(devs);
    return -1;
}

int send_command(u8 cmd, u8 subcmd, u8 *args, int arg_len) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return -1;
    }

    u8 buf[49];
    memset(buf, 0, 49);
    buf[0] = cmd;
    buf[1] = timing_byte & 0xF;
    timing_byte++;
    buf[10] = subcmd;

    if (args && arg_len > 0)
        memcpy(buf + 11, args, arg_len > 38 ? 38 : arg_len);

    return hid_write(handle, buf, 49);
}

int read_response(u8 *buf, int len, int timeout_ms) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return -1;
    }
    memset(buf, 0, len);
    return hid_read_timeout(handle, buf, len, timeout_ms);
}

void cmd_device_info(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    printf("\n[*] Getting device info...\n");
    send_command(0x01, 0x02, NULL, 0);

    u8 buf[65];
    int res = read_response(buf, 65, 500);

    if (res > 0) {
        printf("[+] Device info received (%d bytes)\n", res);
        printf("    Controller type: 0x%02x\n", buf[9]);
        printf("    MAC: %02x:%02x:%02x:%02x:%02x:%02x\n",
               buf[20], buf[21], buf[22], buf[23], buf[24], buf[25]);
        printf("    Firmware: %d.%d\n", buf[27], buf[28]);
    } else {
        printf("[-] No response\n");
    }
}

void cmd_battery_status(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    printf("\n[*] Getting battery status...\n");
    send_command(0x01, 0x50, NULL, 0);

    u8 buf[65];
    int res = read_response(buf, 65, 500);

    if (res > 0) {
        u8 batt_level = (buf[2] >> 4) & 0xF;
        u8 charging = (buf[2] >> 4) & 0x1;
        printf("[+] Battery: %d/4\n", batt_level);
        printf("[+] Charging: %s\n", charging ? "Yes" : "No");
    } else {
        printf("[-] No response\n");
    }
}

void cmd_set_player_led(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    int pattern;
    printf("\nLED patterns:\n");
    printf("  0x01: Player 1\n");
    printf("  0x02: Player 2\n");
    printf("  0x04: Player 3\n");
    printf("  0x08: Player 4\n");
    printf("Enter pattern (hex): ");
    scanf("%x", &pattern);
    getchar();

    printf("[*] Setting player LED to 0x%02x...\n", pattern);
    u8 arg = (u8)pattern;
    send_command(0x01, 0x30, &arg, 1);

    u8 buf[65];
    int res = read_response(buf, 65, 500);

    if (res > 0) {
        printf("[+] LED command acknowledged\n");
    } else {
        printf("[-] No response\n");
    }
}

void cmd_test_vibration(void) {
    if (!handle) {
        fprintf(stderr, "[-] No device connected\n");
        return;
    }

    printf("\n[*] Enabling vibration...\n");
    u8 arg = 0x01;
    send_command(0x01, 0x48, &arg, 1);

    u8 buf[65];
    int res = read_response(buf, 65, 500);

    if (res <= 0) {
        printf("[-] No response\n");
        return;
    }

    printf("[+] Vibration enabled\n");
    sleep(1);

    printf("[*] Sending test rumble pattern...\n");
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
    sleep(1);

    printf("[*] Disabling vibration...\n");
    arg = 0x00;
    send_command(0x01, 0x48, &arg, 1);
    read_response(buf, 65, 500);
    printf("[+] Vibration disabled\n");
}

void cmd_read_calibration(void) {
    printf("\n[*] Calibration data reading - STUB\n");
    printf("    This feature requires SPI read implementation\n");
    printf("    See LINUX_BUILD.md for development roadmap\n");
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
        scanf("%d", &choice);
        getchar();

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

// A software Joy-Con / Pro Controller behind the hidapi functions, for testing jctool-cli
// without hardware (cli/test/run_tests.sh). C++ port of linux/mono/src/FakeJoyCon.cs: it
// answers the subcommands the toolkit uses the way a real controller does, backed by a
// 512KB SPI flash image in memory. The IR camera is emulated in image transfer mode; NFC is
// not emulated.
//
// Environment:
//   JCFAKE_TYPE=l|r|pro      controller type (default r)
//   JCFAKE_SPI_IN=file       start from this flash image
//   JCFAKE_SPI_OUT=file      save the flash image on hid_exit()
//   JCFAKE_WHITE=n           IR white pixel count reported after the first frame

#include <hidapi/hidapi.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <deque>
#include <string>
#include <thread>
#include <vector>

typedef uint8_t u8;

namespace {

struct FakeJoyCon {
    int type = 2;
    int product_id = 0x2007;
    std::vector<u8> spi = std::vector<u8>(0x80000, 0xFF);
    u8 mac[6] = { 0x98, 0xB6, 0xE9, 0x12, 0x34, 0x56 };
    std::deque<std::vector<u8>> replies;
    u8 input_mode = 0x3F;
    bool imu_on = false;
    u8 timer = 0;
    int tick = 0;
    u8 mcu_state = 0;       // 0: off, 1: standby, 5: IR
    u8 ir_mode = 0;
    u8 ir_max_frag = 0;
    int ir_frames_sent = 0;
    int ir_frame_index = 0;
    int ir_white_pixels = 0;
    int ir_stuck_runs = 0;              // Runs that keep sending 240x320 rows (JCFAKE_IR_STUCK)
    bool ir_stuck_run = false;
    int ir_mode_sets = 0;
    int ir_last_frag = 0;
    bool ir_skip_after_register_write = false;
    bool ir_skipped_for_register_write = false;

    void put(int offset, std::initializer_list<int> data) {
        for (int b : data)
            spi[offset++] = (u8)b;
    }

    void init(int t) {
        type = t;
        product_id = t == 1 ? 0x2006 : t == 2 ? 0x2007 : 0x2009;
        // Values the SPI backup validation expects at fixed places
        put(0x0000, { 0x01, 0x08, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x08, 0xC0, 0x5D, 0x89, 0xFD, 0x04, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x40, 0x06 });
        for (int i = 0; i < 6; i++)
            spi[0x1A - i] = mac[i];   // stored reversed
        put(0x10000, { 0x0A, 0xFB, 0x00, 0x00, 0x02, 0x0D });
        // Factory configuration
        put(0x6000, { 0x00, 0x00 });
        const char *sn = t == 3 ? "" : "XAW70012345678";
        for (size_t i = 0; i < 14; i++)
            spi[0x6002 + i] = i < strlen(sn) ? (u8)sn[i] : 0;
        put(0x6012, { t, 0xA0 });
        put(0x6020, { 0xD3, 0xFF, 0xD5, 0xFF, 0x55, 0x01, 0x00, 0x40, 0x00, 0x40, 0x00, 0x40,
                      0x19, 0x00, 0xDD, 0xFF, 0xDC, 0xFF, 0x3B, 0x34, 0x3B, 0x34, 0x3B, 0x34 });
        put(0x603D, { 0xBA, 0xF5, 0x62, 0x6F, 0xC8, 0x77, 0xED, 0x95, 0x5B,
                      0x16, 0xD8, 0x7D, 0xF2, 0xB5, 0x5F, 0x86, 0x65, 0x5E });
        if (t == 3)
            put(0x6050, { 0x32, 0x32, 0x32, 0xFF, 0xFF, 0xFF, 0x32, 0x32, 0x32, 0x32, 0x32, 0x32 });
        else if (t == 2)
            put(0x6050, { 0xFF, 0x3C, 0x28, 0x1E, 0x0A, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        else
            put(0x6050, { 0x0A, 0xB9, 0xE6, 0x00, 0x1E, 0x1E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        put(0x6080, { 0x50, 0xFD, 0x00, 0x00, 0xC6, 0x0F });
        put(0x6086, { 0x0F, 0x30, 0x61, 0x96, 0x30, 0xF3, 0xD4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xC7, 0x79, 0x9C, 0x33, 0x36, 0x63 });
        put(0x6098, { 0x0F, 0x30, 0x61, 0x96, 0x30, 0xF3, 0xD4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xC7, 0x79, 0x9C, 0x33, 0x36, 0x63 });
    }

    std::vector<u8> new_report(u8 id, int length) {
        std::vector<u8> r(length, 0);
        r[0] = id;
        r[1] = timer++;
        r[2] = 0x8E;            // battery full, Bluetooth
        // Sticks at their calibrated centers
        r[6] = 0x6F; r[7] = 0x8C; r[8] = 0x77;
        r[9] = 0xF2; r[10] = 0xD5; r[11] = 0x7D;
        return r;
    }

    void ack(u8 subcmd, u8 ack_byte, const std::vector<u8> &data = {}) {
        std::vector<u8> r = new_report(0x21, 49);
        r[13] = ack_byte;
        r[14] = subcmd;
        for (size_t i = 0; i < data.size() && 15 + i < 49; i++)
            r[15 + i] = data[i];
        replies.push_back(r);
    }

    std::vector<u8> mcu_report(u8 report_type) {
        std::vector<u8> r = new_report(0x31, 362);
        r[49] = report_type;
        return r;
    }

    void mcu_config(const u8 *data) {
        std::vector<u8> r = new_report(0x21, 49);
        r[13] = 0xA0;
        r[14] = 0x21;
        if (data[11] == 0x21) {             // Set MCU mode
            if (mcu_state != 0)
                mcu_state = data[13];
            r[15] = 0x01;
            r[22] = 0x01;
        }
        else if (data[11] == 0x23 && data[12] == 0x01) {   // Set IR mode
            ir_mode = data[13];
            ir_max_frag = data[14];
            ir_frames_sent = 0;
            ir_frame_index = 0;
            ir_stuck_run = ir_stuck_runs > 0;
            if (ir_stuck_runs > 0)
                ir_stuck_runs--;
            if (ir_mode == 0x07)
                fprintf(stderr, "[fake] IR mode set %d%s\n", ++ir_mode_sets, ir_stuck_run ? " (stuck at 240x320)" : "");
            r[15] = 0x0b;
        }
        else if (data[11] == 0x23 && data[12] == 0x04) {   // Write IR registers
            if (mcu_state == 5 && ir_frames_sent > 0)
                ir_skip_after_register_write = true;
            r[15] = 0x13;
            r[16] = 0x00;
            r[17] = ir_mode == 0x04 ? 0x02 : ir_mode;
        }
        replies.push_back(r);
    }

    void mcu_write(const u8 *data) {
        if (data[10] == 0x01) {                 // MCU status
            std::vector<u8> r = mcu_report(0x01);
            r[56] = mcu_state;
            replies.push_back(r);
        }
        else if (data[10] == 0x03 && data[11] == 0x00 && mcu_state == 5 && ir_mode != 0) {
            // IR fragment ACK: send the next fragment of a test image
            int frag = data[12] == 0x01 ? data[13] : (ir_frames_sent == 0 ? 0 : (data[14] + 1) % (ir_max_frag + 1));
            if (data[12] == 0x01 && ir_skipped_for_register_write)
                frag = (ir_last_frag + 1) % (ir_max_frag + 1);
            ir_skipped_for_register_write = false;
            if (ir_skip_after_register_write && data[12] != 0x01 && ir_frames_sent > 0) {
                frag = (frag + 1) % (ir_max_frag + 1);
                ir_skip_after_register_write = false;
                ir_skipped_for_register_write = true;
            }
            if (frag == 0 && ir_frames_sent > 0)
                ir_frame_index++;           // A new frame starts
            ir_last_frag = frag;
            std::vector<u8> r = mcu_report(0x03);
            r[50] = 0x00;
            r[51] = ir_mode;
            r[52] = (u8)frag;
            // The first frame of a run is a leftover frame from before, with its own stats
            bool leftover = ir_frame_index == 0;
            r[53] = leftover ? 31 : 0x40;   // average intensity
            int white = leftover ? 5600 : ir_white_pixels;
            r[55] = white & 0xFF;
            r[56] = white >> 8;
            if (ir_mode == 0x07 && ir_stuck_run) {
                for (int i = 0; i < 300; i++)   // 320 pixel rows: left half dark, right half bright
                    r[59 + i] = ((frag * 300 + i) % 320) < 160 ? 30 : 200;
            }
            else if (ir_mode == 0x07) {
                int width = ir_max_frag == 0x3f ? 160 : ir_max_frag == 0x0f ? 80 : ir_max_frag == 0x03 ? 40 : 320;
                int height = (ir_max_frag + 1) * 300 / width;
                for (int i = 0; i < 300; i++) {
                    int x = (frag * 300 + i) % width, y = (frag * 300 + i) / width;
                    r[59 + i] = (x % 20) < 2 ? 0xFF : (u8)(y * 200 / height + x * 50 / width + (leftover ? 5 : 0));
                }
            }
            replies.push_back(r);
            ir_frames_sent++;
        }
    }

    int write(const u8 *data, size_t length) {
        u8 cmd = data[0];
        if (cmd == 0x11) {
            mcu_write(data);
            return (int)length;
        }
        if (cmd != 0x01)
            return (int)length;   // 0x10 rumble only: no reply
        u8 subcmd = data[10];
        switch (subcmd) {
            case 0x02: // Device info
                ack(0x02, 0x82, { 0x03, 0x89, (u8)type, 0x02, mac[0], mac[1], mac[2], mac[3], mac[4], mac[5], 0x01, 0x01 });
                break;
            case 0x03: // Set input report mode
                input_mode = data[11];
                ack(0x03, 0x80);
                break;
            case 0x10: { // SPI read: echo offset/size, then the data
                uint32_t offset;
                memcpy(&offset, data + 11, 4);
                u8 size = data[15];
                std::vector<u8> reply(5 + size, 0);
                memcpy(reply.data(), &offset, 4);
                reply[4] = size;
                if (offset + size <= spi.size())
                    memcpy(reply.data() + 5, spi.data() + offset, size);
                ack(0x10, 0x90, reply);
                break;
            }
            case 0x11: { // SPI write
                uint32_t offset;
                memcpy(&offset, data + 11, 4);
                u8 size = data[15];
                if (offset + size <= spi.size())
                    memcpy(spi.data() + offset, data + 16, size);
                ack(0x11, 0x80, { 0x00 });
                break;
            }
            case 0x40: // Enable/disable IMU
                imu_on = data[11] != 0;
                ack(0x40, 0x80);
                break;
            case 0x43: // Read IMU register(s)
                if (data[11] == 0x10)
                    ack(0x43, 0xC0, { data[11], data[12], (u8)(imu_on ? 0x30 : 0x00) });
                else
                    ack(0x43, 0xC0, { data[11], data[12], 0x60, 0x00 });   // 31.0 C
                break;
            case 0x21: // MCU config
                mcu_config(data);
                break;
            case 0x22: // MCU on/off
                mcu_state = data[11] != 0 ? 1 : 0;
                ack(0x22, 0x80);
                break;
            case 0x50: // Regulated voltage
                ack(0x50, 0xD0, { 0x10, 0x06 });   // 0x610: 3.88V
                break;
            default:
                ack(subcmd, 0x80);
                break;
        }
        return (int)length;
    }

    int read(u8 *data, size_t length, int milliseconds) {
        std::vector<u8> r;
        if (!replies.empty()) {
            r = replies.front();
            replies.pop_front();
        }
        else if (input_mode == 0x30) {
            std::this_thread::sleep_for(std::chrono::milliseconds(15));
            r = new_report(0x30, 49);
            r[3] = (tick++ / 20) % 2 == 0 ? 0x08 : 0x00;   // blink the A button
            for (int i = 13; i < 49; i++)
                r[i] = (u8)(i * 7 + tick);
        }
        else {
            if (milliseconds > 0)
                std::this_thread::sleep_for(std::chrono::milliseconds(std::min(milliseconds, 5)));
            return 0;
        }
        size_t n = std::min(length, r.size());
        memcpy(data, r.data(), n);
        return (int)n;
    }
};

FakeJoyCon fake;
int fake_handle_token;
wchar_t product_name[64];

} // namespace

extern "C" {

int HID_API_EXPORT hid_init(void) {
    const char *t = getenv("JCFAKE_TYPE");
    fake.init(t && strcmp(t, "l") == 0 ? 1 : t && strcmp(t, "pro") == 0 ? 3 : 2);
    const char *in = getenv("JCFAKE_SPI_IN");
    if (in) {
        FILE *f = fopen(in, "rb");
        if (f) {
            size_t n = fread(fake.spi.data(), 1, fake.spi.size(), f);
            (void)n;
            fclose(f);
        }
    }
    const char *stuck = getenv("JCFAKE_IR_STUCK");
    if (stuck)
        fake.ir_stuck_runs = atoi(stuck);
    const char *white = getenv("JCFAKE_WHITE");
    if (white)
        fake.ir_white_pixels = atoi(white);
    return 0;
}

int HID_API_EXPORT hid_exit(void) {
    const char *out = getenv("JCFAKE_SPI_OUT");
    if (out) {
        FILE *f = fopen(out, "wb");
        if (f) {
            fwrite(fake.spi.data(), 1, fake.spi.size(), f);
            fclose(f);
        }
    }
    return 0;
}

struct hid_device_info HID_API_EXPORT *hid_enumerate(unsigned short, unsigned short) {
    auto *d = (hid_device_info *)calloc(1, sizeof(hid_device_info));
    d->path = strdup("/dev/fake-joycon");
    d->vendor_id = 0x057E;
    d->product_id = (unsigned short)fake.product_id;
    swprintf(product_name, 64, L"%ls", fake.type == 1 ? L"Joy-Con (L)" : fake.type == 2 ? L"Joy-Con (R)" : L"Pro Controller");
    d->product_string = product_name;
    d->serial_number = (wchar_t *)L"98:b6:e9:12:34:56";
    d->manufacturer_string = (wchar_t *)L"";
    d->usage = 0x0005;
    d->usage_page = 0x0001;
    return d;
}

void HID_API_EXPORT hid_free_enumeration(struct hid_device_info *devs) {
    if (devs) {
        free(devs->path);
        free(devs);
    }
}

hid_device HID_API_EXPORT *hid_open_path(const char *) {
    return (hid_device *)&fake_handle_token;
}

void HID_API_EXPORT hid_close(hid_device *) {
}

int HID_API_EXPORT hid_write(hid_device *, const unsigned char *data, size_t length) {
    return fake.write(data, length);
}

int HID_API_EXPORT hid_read_timeout(hid_device *, unsigned char *data, size_t length, int milliseconds) {
    return fake.read(data, length, milliseconds);
}

}

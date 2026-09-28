// Joy-Con Toolkit protocol code for the Linux CLI (jctool-cli).
// Ported from linux/mono/src/JcTool.cs, the Linux port of CTCaer's jctool/jctool.cpp,
// with the same packet layouts, offsets, retry counts and Linux fixes.
// Copyright (c) 2018 CTCaer. Licensed under the MIT license (see LICENSE).
#pragma once

#include <cstdint>
#include <string>
#include <hidapi/hidapi.h>

typedef uint8_t  u8;
typedef uint16_t u16;
typedef uint32_t u32;
typedef uint64_t u64;
typedef int8_t   s8;
typedef int16_t  s16;
typedef int32_t  s32;

template <typename T> T CLAMP(const T& value, const T& low, const T& high)
{
    return value < low ? low : (value > high ? high : value);
}

#pragma pack(push, 1)

struct brcm_hdr {
    u8 cmd;
    u8 timer;
    u8 rumble_l[4];
    u8 rumble_r[4];
};

struct brcm_cmd_01 {
    u8 subcmd;
    union {
        struct {
            u32 offset;
            u8 size;
        } spi_data;

        struct {
            u8 arg1;
            u8 arg2;
        } subcmd_arg;

        struct {
            u8  mcu_cmd;
            u8  mcu_subcmd;
            u8  mcu_mode;
        } subcmd_21_21;

        struct {
            u8  mcu_cmd;
            u8  mcu_subcmd;
            u8  no_of_reg;
            u16 reg1_addr;
            u8  reg1_val;
            u16 reg2_addr;
            u8  reg2_val;
            u16 reg3_addr;
            u8  reg3_val;
            u16 reg4_addr;
            u8  reg4_val;
            u16 reg5_addr;
            u8  reg5_val;
            u16 reg6_addr;
            u8  reg6_val;
            u16 reg7_addr;
            u8  reg7_val;
            u16 reg8_addr;
            u8  reg8_val;
            u16 reg9_addr;
            u8  reg9_val;
        } subcmd_21_23_04;

        struct {
            u8  mcu_cmd;
            u8  mcu_subcmd;
            u8  mcu_ir_mode;
            u8  no_of_frags;
            u16 mcu_major_v;
            u16 mcu_minor_v;
        } subcmd_21_23_01;
    };
};

struct ir_image_config {
    u8  ir_res_reg;
    u16 ir_exposure;
    u8  ir_leds; // Leds to enable, Strobe/Flashlight modes
    u16 ir_leds_intensity; // MSByte: Leds 1/2, LSB: Leds 3/4
    u8  ir_digital_gain;
    u8  ir_ex_light_filter;
    u32 ir_custom_register; // MSByte: Enable/Disable, Middle Byte: Edge smoothing, LSB: Color interpolation
    u16 ir_buffer_update_time;
    u8  ir_hand_analysis_mode;
    u8  ir_hand_analysis_threshold;
    u32 ir_denoise; // MSByte: Enable/Disable, Middle Byte: Edge smoothing, LSB: Color interpolation
    u8  ir_flip;
    u8  ir_mode;
};

#pragma pack(pop)

enum { NOTHING = 0, JOYCON_L = 1, JOYCON_R = 2, PROCON = 3 };

// State shared with the menus (the same globals as jctool.cpp)
extern hid_device *handle;
extern int  handle_type;
extern u8   timming_byte;
extern u8   ir_max_frag_no;
extern volatile bool enable_button_test;
extern volatile bool enable_IRVideoPhoto;
extern bool enable_IRAutoExposure;
extern volatile bool enable_NFCScanning;
extern volatile bool cancel_spi_dump;
extern bool enable_traffic_dump;

// IR state and Linux options (see linux/README.md)
extern int  ir_exposure_value;      // Exposure in us, updated by auto exposure
extern bool ir_quick_capture;       // Capture: no auto exposure adjustment, save the 2nd frame
extern bool ir_skip_leftover;       // JCTOOL_IR_SKIP_LEFTOVER=1
extern bool ir_patient_setup;       // JCTOOL_IR_PATIENT_SETUP=1
extern int  ir_last_frame_missing;
extern bool ir_last_capture_stale;

// Loaded HD rumble file (file_type 1: raw; 2-4: binary, converted into vib_file_converted)
extern u8  *vib_loaded_file;
extern u8  *vib_file_converted;

// hidapi wrappers that behave like the Windows hidapi jctool.cpp was written against
int  jc_hid_write(hid_device *dev, const u8 *data, size_t length);
int  jc_hid_read_timeout(hid_device *dev, u8 *data, size_t length, int milliseconds);
void msleep(int ms);

// Protocol functions (same names and behavior as jctool.cpp)
s16  uint16_to_int16(u16 a);
u16  int16_to_uint16(s16 a);
u8   mcu_crc8_calc(u8 *buf, u8 size);
void decode_stick_params(u16 *decoded_stick_params, u8 *encoded_stick_params);
void encode_stick_params(u8 *encoded_stick_params, u16 *decoded_stick_params);
void AnalogStickCalc(float *pOutX, float *pOutY, u16 x, u16 y, u16 *x_calc, u16 *y_calc);
int  set_led_busy();
std::string get_sn(u32 offset, u16 read_len);
int  get_spi_data(u32 offset, u16 read_len, u8 *test_buf);
int  write_spi_data(u32 offset, u16 write_len, u8 *test_buf);
int  get_device_info(u8 *test_buf);
int  get_battery(u8 *test_buf);
int  get_temperature(u8 *test_buf);
int  dump_spi(const char *dev_name);
int  send_rumble();
int  send_custom_command(u8 *arg);
int  button_test();
int  play_tune(int tune_no);
int  play_hd_rumble_file(int file_type, u16 sample_rate, int samples, int loop_start, int loop_end, int loop_wait, int loop_times);
int  get_raw_ir_image(u8 mode, u8 show_status);
int  ir_sensor(ir_image_config &ir_cfg);
int  get_ir_registers(int start_reg, int reg_group);
int  ir_sensor_config_live(ir_image_config &ir_cfg);
int  nfc_tag_info();
int  silence_input_report();
bool ir_frame_has_other_width(u8 *image, int max_frag_no);
void trace_note(const std::string &what);
u32  ir_iron_palette(u8 value);
void hd_rumble_convert(int vib_file_type, u32 vib_samples, int lf_amp, int lf_freq, int hf_amp, int hf_freq);
int  send_subcommand(u8 subcmd, const u8 *args, int arg_len, u8 *reply);

// Implemented by the CLI front end: what the Windows code showed in the window.
void ui_poll();                                           // Check for "stop" (Enter) during long operations
void ui_spi_progress(u32 offset);                         // SPI dump progress
void ui_custom_command(const std::string &sent, const std::string &reply_cmd, const std::string &reply);
void ui_button_test_info(const std::string &text);        // Calibration/device parameters shown before the test
void ui_button_test(const std::string &report, const std::string &sensors);
void ui_ir_status(const std::string &text);
void ui_ir_help(const std::string &text);
void ui_ir_frame(const u8 *image);                        // Finished IR frame (8bpp, ir_image_width x height)
void ui_ir_exposure(int exposure);
void ui_nfc_uid(const std::string &text);
void ui_nfc_tag(const std::string &text);
void ui_ntag_contents(u8 *ntag_buf, u8 ntag_pages);

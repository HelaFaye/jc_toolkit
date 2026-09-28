// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Port of jctool/jctool.cpp (protocol code). Kept as close to the C++ as C# allows:
// same packet layouts, offsets, retry counts and goto structure.

using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using s16 = System.Int16;

namespace CppWinFormJoy
{
    public static unsafe partial class Jc
    {
        // Takes int so callers can pass the byte arithmetic (and s16 values) jctool.cpp passes.
        public static s16 uint16_to_int16(int a) {
            return unchecked((s16)(u16)a);
        }


        public static u16 int16_to_uint16(int a) {
            return unchecked((u16)(s16)a);
        }


        public static u8 mcu_crc8_calc(u8 *buf, u8 size) {
            u8 crc8 = 0x0;

            for (int i = 0; i < size; ++i) {
                crc8 = Tables.mcu_crc8_table[(u8)(crc8 ^ buf[i])];
            }
            return crc8;
        }


        public static void decode_stick_params(u16 *decoded_stick_params, u8 *encoded_stick_params) {
            decoded_stick_params[0] = (u16)((encoded_stick_params[1] << 8) & 0xF00 | encoded_stick_params[0]);
            decoded_stick_params[1] = (u16)((encoded_stick_params[2] << 4) | (encoded_stick_params[1] >> 4));
        }


        public static void encode_stick_params(u8 *encoded_stick_params, u16 *decoded_stick_params) {
            encoded_stick_params[0] =  (u8)(decoded_stick_params[0] & 0xFF);
            encoded_stick_params[1] = (u8)((decoded_stick_params[0] & 0xF00) >> 8 | (decoded_stick_params[1] & 0xF) << 4);
            encoded_stick_params[2] = (u8)((decoded_stick_params[1] & 0xFF0) >> 4);
        }


        // Credit to Hypersect (Ryan Juckett)
        // http://blog.hypersect.com/interpreting-analog-sticks/
        public static void AnalogStickCalc(
            float *pOutX,       // out: resulting stick X value
            float *pOutY,       // out: resulting stick Y value
            u16 x,              // in: initial stick X value
            u16 y,              // in: initial stick Y value
            u16* x_calc,        // calc -X, CenterX, +X
            u16* y_calc         // calc -Y, CenterY, +Y
        )
        {
            float x_f, y_f;
            // Apply Joy-Con center deadzone. 0xAE translates approx to 15%. Pro controller has a 10% deadzone.
            float deadZoneCenter = 0.15f;
            // Add a small ammount of outer deadzone to avoid edge cases or machine variety.
            float deadZoneOuter = 0.10f;

            // convert to float based on calibration and valid ranges per +/-axis
            x = CLAMP(x, x_calc[0], x_calc[2]);
            y = CLAMP(y, y_calc[0], y_calc[2]);
            if (x >= x_calc[1])
                x_f = (float)(x - x_calc[1]) / (float)(x_calc[2] - x_calc[1]);
            else
                x_f = -((float)(x - x_calc[1]) / (float)(x_calc[0] - x_calc[1]));
            if (y >= y_calc[1])
                y_f = (float)(y - y_calc[1]) / (float)(y_calc[2] - y_calc[1]);
            else
                y_f = -((float)(y - y_calc[1]) / (float)(y_calc[0] - y_calc[1]));

            // Interpolate zone between deadzones
            float mag = (float)Math.Sqrt(x_f*x_f + y_f*y_f);
            if (mag > deadZoneCenter) {
                // scale such that output magnitude is in the range [0.0f, 1.0f]
                float legalRange = 1.0f - deadZoneOuter - deadZoneCenter;
                float normalizedMag = Math.Min(1.0f, (mag - deadZoneCenter) / legalRange);
                float scale = normalizedMag / mag;
                pOutX[0] = x_f * scale;
                pOutY[0] = y_f * scale;
            }
            else
            {
                // stick is in the inner dead zone
                pOutX[0] = 0.0f;
                pOutY[0] = 0.0f;
            }
        }


        public static int set_led_busy() {
            int res;
            u8* buf = stackalloc u8[49];
            memset(buf, 0, 49);
            var hdr = (brcm_hdr *)buf;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x30;
            pkt->subcmd_arg.arg1 = 0x81;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf, 0, 64);

            //Set breathing HOME Led
            if (handle_type != 1) {
                memset(buf, 0, 49);
                hdr = (brcm_hdr *)buf;
                pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x38;
                pkt->subcmd_arg.arg1 = 0x28;
                pkt->subcmd_arg.arg2 = 0x20;
                buf[13] = 0xF2;
                buf[14] = buf[15] = 0xF0;
                res = hid_write(handle, buf, 49);
                res = hid_read_timeout(handle, buf, 0, 64);
            }

            return 0;
        }


        public static string get_sn(u32 offset, u16 read_len) {
            int res;
            int error_reading = 0;
            u8* buf = stackalloc u8[49];
            string test = "";
            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x10;
                pkt->spi_data.offset = offset;
                pkt->spi_data.size = (u8)(read_len);
                res = hid_write(handle, buf, 49);

                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);
                    if ((*(u16*)&buf[0xD] == 0x1090) && (*(u32*)&buf[0xF] == offset))
                        goto check_result;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 20)
                    return "Error!";
            }
            check_result:
            if (res >= 0x14 + read_len) {
                for (int i = 0; i < read_len; i++) {
                    if (buf[0x14 + i] != 0x000) {
                        test += (char)buf[0x14 + i];
                    }else
                        test += "";
                    }
            }
            else {
                return "Error!";
            }
            return test;
        }


        public static int get_spi_data(u32 offset, u16 read_len, u8 *test_buf) {
            int res;
            u8* buf = stackalloc u8[49];
            int error_reading = 0;
            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x10;
                pkt->spi_data.offset = offset;
                pkt->spi_data.size = (u8)(read_len);
                res = hid_write(handle, buf, 49);

                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);
                    if ((*(u16*)&buf[0xD] == 0x1090) && (*(u32*)&buf[0xF] == offset))
                        goto check_result;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 20)
                    return 1;
            }
            check_result:
            if (res >= 0x14 + read_len) {
                    for (int i = 0; i < read_len; i++) {
                        test_buf[i] = buf[0x14 + i];
                    }
            }
        
            return 0;
        }


        public static int write_spi_data(u32 offset, u16 write_len, u8* test_buf) {
            int res;
            u8* buf = stackalloc u8[49];
            int error_writing = 0;
            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x11;
                pkt->spi_data.offset = offset;
                pkt->spi_data.size = (u8)(write_len);
                for (int i = 0; i < write_len; i++)
                    buf[0x10 + i] = test_buf[i];

                res = hid_write(handle, buf, 49);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);
                    if (*(u16*)&buf[0xD] == 0x1180)
                        goto check_result;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_writing++;
                if (error_writing == 20)
                    return 1;
            }
            check_result:
            return 0;
        }


        public static int get_device_info(u8* test_buf) {
            int res;
            u8* buf = stackalloc u8[49];
            int error_reading = 0;
            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x02;
                res = hid_write(handle, buf, 49);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);        
                    if (*(u16*)&buf[0xD] == 0x0282)
                        goto check_result;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 20)
                    break;
            }
            check_result:
            for (int i = 0; i < 0xA; i++) {
                test_buf[i] = buf[0xF + i];
            }

            return 0;
        }


        public static int get_battery(u8* test_buf) {
            int res;
            u8* buf = stackalloc u8[49];
            int error_reading = 0;
            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x50;
                res = hid_write(handle, buf, 49);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);
                    if (*(u16*)&buf[0xD] == 0x50D0)
                        goto check_result;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 20)
                    break;
            }
            check_result:
            test_buf[0] = buf[0x2];
            test_buf[1] = buf[0xF];
            test_buf[2] = buf[0x10];

            return 0;
        }


        public static int get_temperature(u8* test_buf) {
            int res;
            u8* buf = stackalloc u8[49];
            int error_reading = 0;
            bool imu_changed = false;

            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x43;
                pkt->subcmd_arg.arg1 = 0x10;
                pkt->subcmd_arg.arg2 = 0x01;
                res = hid_write(handle, buf, 49);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);
                    if (*(u16*)&buf[0xD] == 0x43C0)
                        goto check_result;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 20)
                    break;
            }
            check_result:
            if ((buf[0x11] >> 4) == 0x0) {

                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x40;
                pkt->subcmd_arg.arg1 = 0x01;
                res = hid_write(handle, buf, 49);
                res = hid_read_timeout(handle, buf, 0, 64);

                imu_changed = true;

                // Let temperature sensor stabilize for a little bit.
                Sleep(64);
            }
            error_reading = 0;
            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x43;
                pkt->subcmd_arg.arg1 = 0x20;
                pkt->subcmd_arg.arg2 = 0x02;
                res = hid_write(handle, buf, 49);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);
                    if (*(u16*)&buf[0xD] == 0x43C0)
                        goto check_result2;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 20)
                    break;
            }
            check_result2:
            test_buf[0] = buf[0x11];
            test_buf[1] = buf[0x12];

            if (imu_changed) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x40;
                pkt->subcmd_arg.arg1 = 0x00;
                res = hid_write(handle, buf, 49);
                res = hid_read_timeout(handle, buf, 0, 64);
            }

            return 0;
        }


        public static int dump_spi(string dev_name) {
            string file_dev_name = dev_name;
            int error_reading = 0;

            string filename_sys = file_dev_name;
            file_dev_name = "./" + file_dev_name;

            FileStream f;
            try {
                f = new FileStream(file_dev_name, FileMode.Create, FileAccess.Write);
            }
            catch (Exception err) {
                MessageBox.Show("Cannot open file " + filename_sys + " for writing!\n\nError: " + err.Message, "Error opening file!", MessageBoxButtons.OK ,MessageBoxIcon.Exclamation);

                return 1;
            }
            byte[] chunk = new byte[0x1d];

            int res;
            u8* buf = stackalloc u8[49];
        
            u16 read_len = 0x1d;
            u32 offset = 0x0;
            while (offset < 0x80000 && !cancel_spi_dump) {
                error_reading = 0;
                FormJoy.myform1.label_progress.Text = String.Format("{0:F2}KB of 512KB", offset/1024.0f);
                Application.DoEvents();

                while (true) {
                    memset(buf, 0, 49);
                    var hdr = (brcm_hdr *)buf;
                    var pkt = (brcm_cmd_01 *)(hdr + 1);
                    hdr->cmd = 1;
                    hdr->timer = (u8)(timming_byte & 0xF);
                    timming_byte++;
                    pkt->subcmd = 0x10;
                    pkt->spi_data.offset = offset;
                    pkt->spi_data.size = (u8)(read_len);
                    res = hid_write(handle, buf, 49);
                    int retries = 0;
                    while (true) {
                        res = hid_read_timeout(handle, buf, 49, 64);
                        if ((*(u16*)&buf[0xD] == 0x1090) && (*(u32*)&buf[0xF] == offset))
                            goto check_result;

                        retries++;
                        if (retries > 8 || res == 0)
                            break;
                    }
                    if (retries > 8)
                        error_reading++;
                    if (error_reading > 10) {
                        f.Close();
                        return 1;
                    }     
                }
                check_result:
                for (int i = 0; i < read_len; i++)
                    chunk[i] = buf[0x14 + i];
                f.Write(chunk, 0, read_len);
                offset += read_len;
                if (offset == 0x7FFE6)
                    read_len = 0x1A;
            }
            f.Close();

            return 0;
        }


        public static int send_rumble() {
            int res;
            u8* buf = stackalloc u8[49];
            u8* buf2 = stackalloc u8[49];
        
            //Enable Vibration
            memset(buf, 0, 49);
            var hdr = (brcm_hdr *)buf;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x48;
            pkt->subcmd_arg.arg1 = 0x01;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf2, 0, 64);

            //New vibration like switch
            Sleep(16);
            //Send confirmation 
            memset(buf, 0, 49);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            hdr->rumble_l[0] = 0xc2;
            hdr->rumble_l[1] = 0xc8;
            hdr->rumble_l[2] = 0x03;
            hdr->rumble_l[3] = 0x72;
            memcpy(hdr->rumble_r, hdr->rumble_l, 4);
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf2, 0, 64);

            Sleep(81);

            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            hdr->rumble_l[0] = 0x00;
            hdr->rumble_l[1] = 0x01;
            hdr->rumble_l[2] = 0x40;
            hdr->rumble_l[3] = 0x40;
            memcpy(hdr->rumble_r, hdr->rumble_l, 4);
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf2, 0, 64);

            Sleep(5);

            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            hdr->rumble_l[0] = 0xc3;
            hdr->rumble_l[1] = 0xc8;
            hdr->rumble_l[2] = 0x60;
            hdr->rumble_l[3] = 0x64;
            memcpy(hdr->rumble_r, hdr->rumble_l, 4);
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf2, 0, 64);

            Sleep(5);

            //Disable vibration
            memset(buf, 0, 49);
            hdr = (brcm_hdr *)buf;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            hdr->rumble_l[0] = 0x00;
            hdr->rumble_l[1] = 0x01;
            hdr->rumble_l[2] = 0x40;
            hdr->rumble_l[3] = 0x40;
            memcpy(hdr->rumble_r, hdr->rumble_l, 4);
            pkt->subcmd = 0x48;
            pkt->subcmd_arg.arg1 = 0x00;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf, 0, 64);

            memset(buf, 0, 49);
            hdr = (brcm_hdr *)buf;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x30;
            pkt->subcmd_arg.arg1 = 0x01;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf, 0, 64);

            // Set HOME Led
            if (handle_type != 1) {
                memset(buf, 0, 49);
                hdr = (brcm_hdr *)buf;
                pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x38;
                // Heartbeat style configuration
                buf[11] = 0xF1;
                buf[12] = 0x00;
                buf[13] = buf[14] = buf[15] = buf[16] = buf[17] = buf[18] = 0xF0;
                buf[19] = buf[22] = buf[25] = buf[28] = buf[31] = 0x00;
                buf[20] = buf[21] = buf[23] = buf[24] = buf[26] = buf[27] = buf[29] = buf[30] = buf[32] = buf[33] = 0xFF;
                res = hid_write(handle, buf, 49);
                res = hid_read_timeout(handle, buf, 0, 64);
            }

            return 0;
        }


        public static int send_custom_command(u8* arg) {
            int res_write;
            int res;
            int byte_seperator = 1;
            string input_report_cmd = null;
            string input_report_sys = null;
            string output_report_sys = null;
            u8* buf_cmd = stackalloc u8[49];
            u8* buf_reply = stackalloc u8[0x170];
            memset(buf_cmd, 0, 49);
            memset(buf_reply, 0, 368);

            buf_cmd[0] = arg[0]; // cmd
            buf_cmd[1] = (u8)(timming_byte & 0xF);
            timming_byte++;
            // Vibration pattern
            buf_cmd[2] = buf_cmd[6] = arg[1];
            buf_cmd[3] = buf_cmd[7] = arg[2];
            buf_cmd[4] = buf_cmd[8] = arg[3];
            buf_cmd[5] = buf_cmd[9] = arg[4];

            buf_cmd[10] = arg[5]; // subcmd

            // subcmd x21 crc byte
            if (arg[5] == 0x21)
                arg[43] = mcu_crc8_calc(arg + 7, 36);

            output_report_sys = String.Format("Cmd:  {0:X2}   Subcmd: {1:X2}\r\n", buf_cmd[0], buf_cmd[10]);
            if (buf_cmd[0] == 0x01 || buf_cmd[0] == 0x10 || buf_cmd[0] == 0x11) {
                for (int i = 6; i < 44; i++) {
                    buf_cmd[5 + i] = arg[i];
                    output_report_sys += String.Format("{0:X2} ", buf_cmd[5 + i]);
                    if (byte_seperator == 4)
                        output_report_sys += " ";
                    if (byte_seperator == 8) {
                        byte_seperator = 0;
                        output_report_sys += "\r\n";
                    }
                    byte_seperator++;
                }
            }
            //Use subcmd after command
            else {
                for (int i = 6; i < 44; i++) {
                    buf_cmd[i - 5] = arg[i];
                    output_report_sys += String.Format("{0:X2} ", buf_cmd[i - 5]);
                    if (byte_seperator == 4)
                        output_report_sys += " ";
                    if (byte_seperator == 8) {
                        byte_seperator = 0;
                        output_report_sys += "\r\n";
                    }
                    byte_seperator++;
                }
            }
            FormJoy.myform1.textBoxDbg_sent.Text = output_report_sys;

            //Packet size header + subcommand and uint8 argument
            res_write = hid_write(handle, buf_cmd, 49);

            if (res_write < 0)
                input_report_sys += "hid_write failed!\r\n\r\n";
            int retries = 0;
            while (true) {
                res = hid_read_timeout(handle, buf_reply, 368, 64);

                if (res > 0) {
                    if (arg[0] == 0x01 && buf_reply[0] == 0x21)
                        break;
                    else if (arg[0] != 0x01)
                        break;
                }

                retries++;
                if (retries == 20)
                    break;
            }
            byte_seperator = 1;
            if (res > 12) {
                if (buf_reply[0] == 0x21 || buf_reply[0] == 0x30 || buf_reply[0] == 0x33 || buf_reply[0] == 0x31 || buf_reply[0] == 0x3F) {
                    input_report_cmd += String.Format("\r\nInput report: 0x{0:X2}\r\n", buf_reply[0]);
                    input_report_sys += String.Format("Subcmd Reply:\r\n", buf_reply[0]);
                    int len = 49;
                    if (buf_reply[0] == 0x33 || buf_reply[0] == 0x31)
                        len = 362;
                    for (int i = 1; i < 13; i++) {
                        input_report_cmd += String.Format("{0:X2} ", buf_reply[i]);
                        if (byte_seperator == 4)
                            input_report_cmd += " ";
                        if (byte_seperator == 8) {
                            byte_seperator = 0;
                            input_report_cmd += "\r\n";
                        }
                        byte_seperator++;
                    }
                    byte_seperator = 1;
                    for (int i = 13; i < len; i++) {
                        input_report_sys += String.Format("{0:X2} ", buf_reply[i]);
                        if (byte_seperator == 4)
                            input_report_sys += " ";
                        if (byte_seperator == 8) {
                            byte_seperator = 0;
                            input_report_sys += "\r\n";
                        }
                        byte_seperator++;
                    }
                    int crc_check_ok = 0;
                    if (arg[5] == 0x21) {
                        crc_check_ok = (buf_reply[48] == mcu_crc8_calc(buf_reply + 0xF, 33)) ? 1 : 0;
                        if (crc_check_ok != 0)
                            input_report_sys += "(CRC OK)";
                        else
                            input_report_sys += "(Wrong CRC)";
                    }
                }
                else {
                    input_report_sys += String.Format("ID: {0:X2} Subcmd reply:\r\n", buf_reply[0]);
                    for (int i = 13; i < res; i++) {
                        input_report_sys += String.Format("{0:X2} ", buf_reply[i]);
                        if (byte_seperator == 4)
                            input_report_sys += " ";
                        if (byte_seperator == 8) {
                            byte_seperator = 0;
                            input_report_sys += "\r\n";
                        }
                        byte_seperator++;
                    }
                }
            }
            else if (res > 0 && res <= 12) {
                for (int i = 0; i < res; i++)
                    input_report_sys += String.Format("{0:X2} ", buf_reply[i]);
            }
            else {
                input_report_sys += "No reply";
            }
            FormJoy.myform1.textBoxDbg_reply.Text = input_report_sys;
            FormJoy.myform1.textBoxDbg_reply_cmd.Text = input_report_cmd;

            return 0;
        }


        public static int button_test() {
            int res;
            int limit_output = 0;
            string input_report_cmd = null;
            string input_report_sys = null;
            u8* buf_cmd = stackalloc u8[49];
            u8* buf_reply = stackalloc u8[0x170];
            float* acc_cal_coeff = stackalloc float[3];
            float* gyro_cal_coeff = stackalloc float[3];
            float* cal_x = stackalloc float[1];
            float* cal_y = stackalloc float[1];

            bool has_user_cal_stick_l = false;
            bool has_user_cal_stick_r = false;
            bool has_user_cal_sensor = false;

            u8* factory_stick_cal = stackalloc u8[0x12];
            u8* user_stick_cal = stackalloc u8[0x16];
            u8* sensor_model = stackalloc u8[0x6];
            u8* stick_model = stackalloc u8[0x24];
            u8* factory_sensor_cal = stackalloc u8[0x18];
            u8* user_sensor_cal = stackalloc u8[0x1A];
            s16* sensor_cal = stackalloc s16[0x2 * 0x3];
            u16* stick_cal_x_l = stackalloc u16[0x3];
            u16* stick_cal_y_l = stackalloc u16[0x3];
            u16* stick_cal_x_r = stackalloc u16[0x3];
            u16* stick_cal_y_r = stackalloc u16[0x3];
            memset(factory_stick_cal, 0, 0x12);
            memset(user_stick_cal, 0, 0x16);
            memset(sensor_model, 0, 0x6);
            memset(stick_model, 0, 0x12);
            memset(factory_sensor_cal, 0, 0x18);
            memset(user_sensor_cal, 0, 0x1A);
            memset(sensor_cal, 0, 12);
            memset(stick_cal_x_l, 0, 6);
            memset(stick_cal_y_l, 0, 6);
            memset(stick_cal_x_r, 0, 6);
            memset(stick_cal_y_r, 0, 6);
            get_spi_data(0x6020, 0x18, factory_sensor_cal);
            get_spi_data(0x603D, 0x12, factory_stick_cal);
            get_spi_data(0x6080, 0x6, sensor_model);
            get_spi_data(0x6086, 0x12, stick_model);
            get_spi_data(0x6098, 0x12, &stick_model[0x12]);
            get_spi_data(0x8010, 0x16, user_stick_cal);
            get_spi_data(0x8026, 0x1A, user_sensor_cal);

            // Analog Stick device parameters
            FormJoy.myform1.txtBox_devParameters.Text = String.Format("Flat surface ACC Offset:\r\n{0:X4} {1:X4} {2:X4}\r\n\r\n\r\nStick Parameters:\r\n{3:X3} {4:X3}\r\n{5:X2} (Deadzone)\r\n{6:X3} (Range ratio)",
                sensor_model[0] | sensor_model[1] << 8,
                sensor_model[2] | sensor_model[3] << 8,
                sensor_model[4] | sensor_model[5] << 8,
                (stick_model[1] << 8) & 0xF00 | stick_model[0], (stick_model[2] << 4) | (stick_model[1] >> 4),
                (stick_model[4] << 8) & 0xF00 | stick_model[3],
                ((stick_model[5] << 4) | (stick_model[4] >> 4)));

            for (int i = 0; i < 10; i = i + 3) {
                FormJoy.myform1.txtBox_devParameters.Text += String.Format("\r\n{0:X3} {1:X3}",
                    (stick_model[7 + i] << 8) & 0xF00 | stick_model[6 + i],
                    (stick_model[8 + i] << 4) | (stick_model[7 + i] >> 4));
            }

            FormJoy.myform1.txtBox_devParameters2.Text = String.Format("Stick Parameters 2:\r\n{0:X3} {1:X3}\r\n{2:X2} (Deadzone)\r\n{3:X3} (Range ratio)",
                (stick_model[19] << 8) & 0xF00 | stick_model[18], (stick_model[20] << 4) | (stick_model[19] >> 4),
                (stick_model[22] << 8) & 0xF00 | stick_model[21],
                ((stick_model[23] << 4) | (stick_model[22] >> 4)));

            for (int i = 0; i < 10; i = i + 3) {
                FormJoy.myform1.txtBox_devParameters2.Text += String.Format("\r\n{0:X3} {1:X3}",
                    (stick_model[25 + i] << 8) & 0xF00 | stick_model[24 + i],
                    (stick_model[26 + i] << 4) | (stick_model[25 + i] >> 4));
            }

            // Stick calibration
            if (handle_type != 2) {
                stick_cal_x_l[1] = (u16)((factory_stick_cal[4] << 8) & 0xF00 | factory_stick_cal[3]);
                stick_cal_y_l[1] = (u16)((factory_stick_cal[5] << 4) | (factory_stick_cal[4] >> 4));
                stick_cal_x_l[0] = (u16)(stick_cal_x_l[1] - ((factory_stick_cal[7] << 8) & 0xF00 | factory_stick_cal[6]));
                stick_cal_y_l[0] = (u16)(stick_cal_y_l[1] - ((factory_stick_cal[8] << 4) | (factory_stick_cal[7] >> 4)));
                stick_cal_x_l[2] = (u16)(stick_cal_x_l[1] + ((factory_stick_cal[1] << 8) & 0xF00 | factory_stick_cal[0]));
                stick_cal_y_l[2] = (u16)(stick_cal_y_l[1] + ((factory_stick_cal[2] << 4) | (factory_stick_cal[2] >> 4)));
                FormJoy.myform1.textBox_lstick_fcal.Text = String.Format("L Stick Factory:\r\nCenter X,Y: ({0:X3}, {1:X3})\r\nX: [{2:X3} - {4:X3}] Y: [{3:X3} - {5:X3}]",
                    stick_cal_x_l[1], stick_cal_y_l[1], stick_cal_x_l[0], stick_cal_y_l[0], stick_cal_x_l[2], stick_cal_y_l[2]);
            }
            else {
                FormJoy.myform1.textBox_lstick_fcal.Text = "L Stick Factory:\r\nNo calibration";
            }
            if (handle_type != 1) {
                stick_cal_x_r[1] = (u16)((factory_stick_cal[10] << 8) & 0xF00 | factory_stick_cal[9]);
                stick_cal_y_r[1] = (u16)((factory_stick_cal[11] << 4) | (factory_stick_cal[10] >> 4));
                stick_cal_x_r[0] = (u16)(stick_cal_x_r[1] - ((factory_stick_cal[13] << 8) & 0xF00 | factory_stick_cal[12]));
                stick_cal_y_r[0] = (u16)(stick_cal_y_r[1] - ((factory_stick_cal[14] << 4) | (factory_stick_cal[13] >> 4)));
                stick_cal_x_r[2] = (u16)(stick_cal_x_r[1] + ((factory_stick_cal[16] << 8) & 0xF00 | factory_stick_cal[15]));
                stick_cal_y_r[2] = (u16)(stick_cal_y_r[1] + ((factory_stick_cal[17] << 4) | (factory_stick_cal[16] >> 4)));
                FormJoy.myform1.textBox_rstick_fcal.Text = String.Format("R Stick Factory:\r\nCenter X,Y: ({0:X3}, {1:X3})\r\nX: [{2:X3} - {4:X3}] Y: [{3:X3} - {5:X3}]",
                    stick_cal_x_r[1], stick_cal_y_r[1],    stick_cal_x_r[0], stick_cal_y_r[0],    stick_cal_x_r[2], stick_cal_y_r[2]);
            }
            else {
                FormJoy.myform1.textBox_rstick_fcal.Text = "R Stick Factory:\r\nNo calibration";
            }

            if ((user_stick_cal[0] | user_stick_cal[1] << 8) == 0xA1B2) {
                stick_cal_x_l[1] = (u16)((user_stick_cal[6] << 8) & 0xF00 | user_stick_cal[5]);
                stick_cal_y_l[1] = (u16)((user_stick_cal[7] << 4) | (user_stick_cal[6] >> 4));
                stick_cal_x_l[0] = (u16)(stick_cal_x_l[1] - ((user_stick_cal[9] << 8) & 0xF00 | user_stick_cal[8]));
                stick_cal_y_l[0] = (u16)(stick_cal_y_l[1] - ((user_stick_cal[10] << 4) | (user_stick_cal[9] >> 4)));
                stick_cal_x_l[2] = (u16)(stick_cal_x_l[1] + ((user_stick_cal[3] << 8) & 0xF00 | user_stick_cal[2]));
                stick_cal_y_l[2] = (u16)(stick_cal_y_l[1] + ((user_stick_cal[4] << 4) | (user_stick_cal[3] >> 4)));
                FormJoy.myform1.textBox_lstick_ucal.Text = String.Format("L Stick User:\r\nCenter X,Y: ({0:X3}, {1:X3})\r\nX: [{2:X3} - {4:X3}] Y: [{3:X3} - {5:X3}]",
                    stick_cal_x_l[1], stick_cal_y_l[1], stick_cal_x_l[0], stick_cal_y_l[0], stick_cal_x_l[2], stick_cal_y_l[2]);
            }
            else {
                FormJoy.myform1.textBox_lstick_ucal.Text = "L Stick User:\r\nNo calibration";
            }
            if ((user_stick_cal[0xB] | user_stick_cal[0xC] << 8) == 0xA1B2) {
                stick_cal_x_r[1] = (u16)((user_stick_cal[14] << 8) & 0xF00 | user_stick_cal[13]);
                stick_cal_y_r[1] = (u16)((user_stick_cal[15] << 4) | (user_stick_cal[14] >> 4));
                stick_cal_x_r[0] = (u16)(stick_cal_x_r[1] - ((user_stick_cal[17] << 8) & 0xF00 | user_stick_cal[16]));
                stick_cal_y_r[0] = (u16)(stick_cal_y_r[1] - ((user_stick_cal[18] << 4) | (user_stick_cal[17] >> 4)));
                stick_cal_x_r[2] = (u16)(stick_cal_x_r[1] + ((user_stick_cal[20] << 8) & 0xF00 | user_stick_cal[19]));
                stick_cal_y_r[2] = (u16)(stick_cal_y_r[1] + ((user_stick_cal[21] << 4) | (user_stick_cal[20] >> 4)));
                FormJoy.myform1.textBox_rstick_ucal.Text = String.Format("R Stick User:\r\nCenter X,Y: ({0:X3}, {1:X3})\r\nX: [{2:X3} - {4:X3}] Y: [{3:X3} - {5:X3}]",
                    stick_cal_x_r[1], stick_cal_y_r[1], stick_cal_x_r[0], stick_cal_y_r[0], stick_cal_x_r[2], stick_cal_y_r[2]);
            }
            else {
                FormJoy.myform1.textBox_rstick_ucal.Text = "R Stick User:\r\nNo calibration";
            }

            // Sensor calibration
            FormJoy.myform1.textBox_6axis_cal.Text = "6-Axis Factory (XYZ):\r\nAcc:  ";
            for (int i = 0; i < 0xC; i = i + 6) {
                FormJoy.myform1.textBox_6axis_cal.Text += String.Format("{0:X4} {1:X4} {2:X4}\r\n      ",
                    factory_sensor_cal[i + 0] | factory_sensor_cal[i + 1] << 8,
                    factory_sensor_cal[i + 2] | factory_sensor_cal[i + 3] << 8,
                    factory_sensor_cal[i + 4] | factory_sensor_cal[i + 5] << 8);
            }
            // Acc cal origin position
            sensor_cal[0] = uint16_to_int16(factory_sensor_cal[0] | factory_sensor_cal[1] << 8);
            sensor_cal[1] = uint16_to_int16(factory_sensor_cal[2] | factory_sensor_cal[3] << 8);
            sensor_cal[2] = uint16_to_int16(factory_sensor_cal[4] | factory_sensor_cal[5] << 8);

            FormJoy.myform1.textBox_6axis_cal.Text += "\r\nGyro: ";
            for (int i = 0xC; i < 0x18; i = i + 6) {
                FormJoy.myform1.textBox_6axis_cal.Text += String.Format("{0:X4} {1:X4} {2:X4}\r\n      ",
                    factory_sensor_cal[i + 0] | factory_sensor_cal[i + 1] << 8,
                    factory_sensor_cal[i + 2] | factory_sensor_cal[i + 3] << 8,
                    factory_sensor_cal[i + 4] | factory_sensor_cal[i + 5] << 8);
            }
            // Gyro cal origin position
            sensor_cal[3] = uint16_to_int16(factory_sensor_cal[0xC] | factory_sensor_cal[0xD] << 8);
            sensor_cal[4] = uint16_to_int16(factory_sensor_cal[0xE] | factory_sensor_cal[0xF] << 8);
            sensor_cal[5] = uint16_to_int16(factory_sensor_cal[0x10] | factory_sensor_cal[0x11] << 8);

            if ((user_sensor_cal[0x0] | user_sensor_cal[0x1] << 8) == 0xA1B2) {
                FormJoy.myform1.textBox_6axis_ucal.Text = "6-Axis User (XYZ):\r\nAcc:  ";
                for (int i = 0; i < 0xC; i = i + 6) {
                    FormJoy.myform1.textBox_6axis_ucal.Text += String.Format("{0:X4} {1:X4} {2:X4}\r\n      ",
                        user_sensor_cal[i + 2] | user_sensor_cal[i + 3] << 8,
                        user_sensor_cal[i + 4] | user_sensor_cal[i + 5] << 8,
                        user_sensor_cal[i + 6] | user_sensor_cal[i + 7] << 8);
                }
                // Acc cal origin position
                sensor_cal[0] = uint16_to_int16(user_sensor_cal[2] | user_sensor_cal[3] << 8);
                sensor_cal[1] = uint16_to_int16(user_sensor_cal[4] | user_sensor_cal[5] << 8);
                sensor_cal[2] = uint16_to_int16(user_sensor_cal[6] | user_sensor_cal[7] << 8);
                FormJoy.myform1.textBox_6axis_ucal.Text += "\r\nGyro: ";
                for (int i = 0xC; i < 0x18; i = i + 6) {
                    FormJoy.myform1.textBox_6axis_ucal.Text += String.Format("{0:X4} {1:X4} {2:X4}\r\n      ",
                        user_sensor_cal[i + 2] | user_sensor_cal[i + 3] << 8,
                        user_sensor_cal[i + 4] | user_sensor_cal[i + 5] << 8,
                        user_sensor_cal[i + 6] | user_sensor_cal[i + 7] << 8);
                }
                // Gyro cal origin position
                sensor_cal[3] = uint16_to_int16(user_sensor_cal[0xE] | user_sensor_cal[0xF] << 8);
                sensor_cal[4] = uint16_to_int16(user_sensor_cal[0x10] | user_sensor_cal[0x11] << 8);
                sensor_cal[5] = uint16_to_int16(user_sensor_cal[0x12] | user_sensor_cal[0x13] << 8);
            }
            else {
                FormJoy.myform1.textBox_6axis_ucal.Text = "\r\n\r\nUser:\r\nNo calibration";
            }


            // Enable nxpad standard input report
            memset(buf_cmd, 0, 49);
            var hdr = (brcm_hdr *)buf_cmd;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x03;
            pkt->subcmd_arg.arg1 = 0x30;
            res = hid_write(handle, buf_cmd, 49);
            res = hid_read_timeout(handle, buf_cmd, 0, 120);

            // Enable IMU
            memset(buf_cmd, 0, 49);
            hdr = (brcm_hdr *)buf_cmd;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x40;
            pkt->subcmd_arg.arg1 = 0x01;
            res = hid_write(handle, buf_cmd, 49);
            res = hid_read_timeout(handle, buf_cmd, 0, 120);

            // Use SPI calibration and convert them to SI acc unit (m/s^2)
            acc_cal_coeff[0] = (float)(1.0 / (float)(16384 - uint16_to_int16(sensor_cal[0]))) * 4.0f  * 9.8f;
            acc_cal_coeff[1] = (float)(1.0 / (float)(16384 - uint16_to_int16(sensor_cal[1]))) * 4.0f  * 9.8f;
            acc_cal_coeff[2] = (float)(1.0 / (float)(16384 - uint16_to_int16(sensor_cal[2]))) * 4.0f  * 9.8f;

            // Use SPI calibration and convert them to SI gyro unit (rad/s)
            gyro_cal_coeff[0] = (float)(936.0 / (float)(13371 - uint16_to_int16(sensor_cal[3])) * 0.01745329251994);
            gyro_cal_coeff[1] = (float)(936.0 / (float)(13371 - uint16_to_int16(sensor_cal[4])) * 0.01745329251994);
            gyro_cal_coeff[2] = (float)(936.0 / (float)(13371 - uint16_to_int16(sensor_cal[5])) * 0.01745329251994);

            // Input report loop
            while (enable_button_test) {
                res = hid_read_timeout(handle, buf_reply, 368, 200);

                if (res > 12) {
                    if (buf_reply[0] == 0x21 || buf_reply[0] == 0x30 || buf_reply[0] == 0x31 || buf_reply[0] == 0x32 || buf_reply[0] == 0x33) {
                        if (((buf_reply[2] >> 1) & 0x3) == 3)
                            input_report_cmd = String.Format("Conn: BT");
                        else if (((buf_reply[2] >> 1) & 0x3) == 0)
                            input_report_cmd = String.Format("Conn: USB");
                        else
                            input_report_cmd = String.Format("Conn: {0:X}?", (buf_reply[2] >> 1) & 0x3);
                        input_report_cmd += String.Format("\r\nBatt: {0:X}/4   ", buf_reply[2] >> 5);
                        if (((buf_reply[2] >> 4) & 0x1) != 0)
                            input_report_cmd += "Charging: Yes\r\n";
                        else
                            input_report_cmd += "Charging: No\r\n";

                        input_report_cmd += String.Format("Vibration decision: ");
                        input_report_cmd += String.Format("{0:X}, {1:X}\r\n", (buf_reply[12] >> 7) & 1, (buf_reply[12] >> 4) & 7);

                        input_report_cmd += String.Format("\r\nButtons: ");
                        for (int i = 3; i < 6; i++)
                            input_report_cmd += String.Format("{0:X2} ", buf_reply[i]);
                
                        if (handle_type != 2) {
                            input_report_cmd += String.Format("\r\n\r\nL Stick (Raw/Cal):\r\nX:   {0:X3}   Y:   {1:X3}\r\n",
                                buf_reply[6] | (u16)((buf_reply[7] & 0xF) << 8),
                                (buf_reply[7] >> 4) | (buf_reply[8] << 4));

                            AnalogStickCalc(
                                cal_x, cal_y,
                                (u16)(buf_reply[6] | (u16)((buf_reply[7] & 0xF) << 8)),
                                (u16)((buf_reply[7] >> 4) | (buf_reply[8] << 4)),
                                stick_cal_x_l,
                                stick_cal_y_l);

                            input_report_cmd += String.Format("X: {0,5:f2}   Y: {1,5:f2}\r\n",
                                cal_x[0], cal_y[0]);
                        }
                        if (handle_type != 1) {
                            input_report_cmd += String.Format("\r\n\r\nR Stick (Raw/Cal):\r\nX:   {0:X3}   Y:   {1:X3}\r\n",
                                buf_reply[9] | (u16)((buf_reply[10] & 0xF) << 8),
                                (buf_reply[10] >> 4) | (buf_reply[11] << 4));

                            AnalogStickCalc(
                                cal_x, cal_y,
                                (u16)(buf_reply[9] | (u16)((buf_reply[10] & 0xF) << 8)),
                                (u16)((buf_reply[10] >> 4) | (buf_reply[11] << 4)),
                                stick_cal_x_r,
                                stick_cal_y_r);

                            input_report_cmd += String.Format("X: {0,5:f2}   Y: {1,5:f2}\r\n",
                                cal_x[0], cal_y[0]);
                        }

                        input_report_sys = String.Format("Acc/meter (Raw/Cal):\r\n");
                        //The controller sends the sensor data 3 times with a little bit different values. Skip them
                        input_report_sys += String.Format("X: {0:X4}  {1,7:F2} m/s\u00B2\r\n", buf_reply[13] | (buf_reply[14] << 8) & 0xFF00,
                            (float)(uint16_to_int16(buf_reply[13] | (buf_reply[14] << 8) & 0xFF00)) * acc_cal_coeff[0]);
                        input_report_sys += String.Format("Y: {0:X4}  {1,7:F2} m/s\u00B2\r\n", buf_reply[15] | (buf_reply[16] << 8) & 0xFF00,
                            (float)(uint16_to_int16(buf_reply[15] | (buf_reply[16] << 8) & 0xFF00)) * acc_cal_coeff[1]);
                        input_report_sys += String.Format("Z: {0:X4}  {1,7:F2} m/s\u00B2\r\n", buf_reply[17] | (buf_reply[18] << 8) & 0xFF00,
                            (float)(uint16_to_int16(buf_reply[17] | (buf_reply[18] << 8) & 0xFF00))  * acc_cal_coeff[2]);

                        input_report_sys += String.Format("\r\nGyroscope (Raw/Cal):\r\n");
                        input_report_sys += String.Format("X: {0:X4}  {1,7:F2} rad/s\r\n", buf_reply[19] | (buf_reply[20] << 8) & 0xFF00,
                            (float)(uint16_to_int16(buf_reply[19] | (buf_reply[20] << 8) & 0xFF00) - uint16_to_int16(sensor_cal[3])) * gyro_cal_coeff[0]);
                        input_report_sys += String.Format("Y: {0:X4}  {1,7:F2} rad/s\r\n", buf_reply[21] | (buf_reply[22] << 8) & 0xFF00,
                            (float)(uint16_to_int16(buf_reply[21] | (buf_reply[22] << 8) & 0xFF00) - uint16_to_int16(sensor_cal[4])) * gyro_cal_coeff[1]);
                        input_report_sys += String.Format("Z: {0:X4}  {1,7:F2} rad/s\r\n", buf_reply[23] | (buf_reply[24] << 8) & 0xFF00,
                            (float)(uint16_to_int16(buf_reply[23] | (buf_reply[24] << 8) & 0xFF00) - uint16_to_int16(sensor_cal[5])) * gyro_cal_coeff[2]);
                    }
                    else if (buf_reply[0] == 0x3F) {
                        input_report_cmd = "";
                        for (int i = 0; i < 17; i++)
                            input_report_cmd += String.Format("{0:X2} ", buf_reply[i]);
                    }

                    if (limit_output == 1) {
                        FormJoy.myform1.textBox_btn_test_reply.Text    = input_report_cmd;
                        FormJoy.myform1.textBox_btn_test_subreply.Text = input_report_sys;
                    }
                    //Only update every 75ms for better readability. No need for real time parsing.
                    else if (limit_output > 4) {
                        limit_output = 0;
                    }
                    limit_output++;
                }
                Application.DoEvents();
            }

            memset(buf_cmd, 0, 49);
            hdr = (brcm_hdr *)buf_cmd;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x03;
            pkt->subcmd_arg.arg1 = 0x3F;
            res = hid_write(handle, buf_cmd, 49);
            res = hid_read_timeout(handle, buf_cmd, 0, 64);

            memset(buf_cmd, 0, 49);
            hdr = (brcm_hdr *)buf_cmd;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x40;
            pkt->subcmd_arg.arg1 = 0x00;
            res = hid_write(handle, buf_cmd, 49);
            res = hid_read_timeout(handle, buf_cmd, 0, 64);

            return 0;
        }


        public static int play_tune(int tune_no) {
            int res;
            u8* buf = stackalloc u8[49];
            u8* buf2 = stackalloc u8[49];

            //Enable Vibration
            memset(buf, 0, 49);
            var hdr = (brcm_hdr *)buf;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x48;
            pkt->subcmd_arg.arg1 = 0x01;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf2, 0, 120);
            // This needs to be changed for new bigger tunes.
            u32[] tune = new u32[6000];
            int tune_size = 0;
            switch (tune_no) {
                case 0:
                    Array.Copy(Tables.tune_SMB, tune, Tables.tune_SMB.Length);
                    tune_size = Tables.tune_SMB.Length;
                    break;
                case 1:
                    Array.Copy(Tables.tune_SMO_OK, tune, Tables.tune_SMO_OK.Length);
                    tune_size = Tables.tune_SMO_OK.Length;
                    break;
            }

            for (int i = 0; i < tune_size; i++) {
                Sleep(15);
                memset(buf, 0, 49);
                hdr = (brcm_hdr *)buf;
                pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x10;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                hdr->rumble_l[0] = (u8)((tune[i] >> 24) & 0xFF);
                hdr->rumble_l[1] = (u8)((tune[i] >> 16) & 0xFF);
                hdr->rumble_l[2] = (u8)((tune[i] >> 8) & 0xFF);
                hdr->rumble_l[3] = (u8)(tune[i] & 0xFF);
                memcpy(hdr->rumble_r, hdr->rumble_l, 4);
                res = hid_write(handle, buf, 49);
                // Joy-con does not reply when Output Report is 0x10

                Application.DoEvents();
            }

            // Disable vibration
            Sleep(15);
            memset(buf, 0, 49);
            hdr = (brcm_hdr *)buf;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            hdr->rumble_l[0] = 0x00;
            hdr->rumble_l[1] = 0x01;
            hdr->rumble_l[2] = 0x40;
            hdr->rumble_l[3] = 0x40;
            memcpy(hdr->rumble_r, hdr->rumble_l, 4);
            pkt->subcmd = 0x48;
            pkt->subcmd_arg.arg1 = 0x00;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf, 0, 64);

            memset(buf, 0, 49);
            hdr = (brcm_hdr *)buf;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x30;
            pkt->subcmd_arg.arg1 = 0x01;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf, 0, 64);


            return 0;
        }


        public static int play_hd_rumble_file(int file_type, u16 sample_rate, int samples, int loop_start, int loop_end, int loop_wait, int loop_times) {
            int res;
            u8* buf = stackalloc u8[49];
            u8* buf2 = stackalloc u8[49];

            //Enable Vibration
            memset(buf, 0, 49);
            var hdr = (brcm_hdr *)buf;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x48;
            pkt->subcmd_arg.arg1 = 0x01;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf2, 0, 120);

            if (file_type == 1 || file_type == 2) {
                for (int i = 0; i < samples * 4; i = i + 4) {
                    Sleep(sample_rate);
                    memset(buf, 0, 49);
                    hdr = (brcm_hdr *)buf;
                    pkt = (brcm_cmd_01 *)(hdr + 1);
                    hdr->cmd = 0x10;
                    hdr->timer = (u8)(timming_byte & 0xF);
                    timming_byte++;
                    if (file_type == 1) {
                        hdr->rumble_l[0] = FormJoy.myform1.vib_loaded_file[0x0A + i];
                        hdr->rumble_l[1] = FormJoy.myform1.vib_loaded_file[0x0B + i];
                        hdr->rumble_l[2] = FormJoy.myform1.vib_loaded_file[0x0C + i];
                        hdr->rumble_l[3] = FormJoy.myform1.vib_loaded_file[0x0D + i];
                    }
                    //file_type is simple bnvib
                    else {
                        hdr->rumble_l[0] = FormJoy.myform1.vib_file_converted[0x0C + i];
                        hdr->rumble_l[1] = FormJoy.myform1.vib_file_converted[0x0D + i];
                        hdr->rumble_l[2] = FormJoy.myform1.vib_file_converted[0x0E + i];
                        hdr->rumble_l[3] = FormJoy.myform1.vib_file_converted[0x0F + i];
                    }
                    memcpy(hdr->rumble_r, hdr->rumble_l, 4);

                    res = hid_write(handle, buf, sizeof(brcm_hdr));
                    Application.DoEvents();
                }
            }
            else if (file_type == 3 || file_type == 4) {
                u8 vib_off = 0;
                if (file_type == 3)
                    vib_off = 8;
                else if (file_type == 4)
                    vib_off = 12;

                for (int i = 0; i < loop_start * 4; i = i + 4) {
                    Sleep(sample_rate);
                    memset(buf, 0, 49);
                    hdr = (brcm_hdr *)buf;
                    pkt = (brcm_cmd_01 *)(hdr + 1);
                    hdr->cmd = 0x10;
                    hdr->timer = (u8)(timming_byte & 0xF);
                    timming_byte++;
                
                    hdr->rumble_l[0] = FormJoy.myform1.vib_loaded_file[0x0C + vib_off + i];
                    hdr->rumble_l[1] = FormJoy.myform1.vib_loaded_file[0x0D + vib_off + i];
                    hdr->rumble_l[2] = FormJoy.myform1.vib_loaded_file[0x0E + vib_off + i];
                    hdr->rumble_l[3] = FormJoy.myform1.vib_loaded_file[0x0F + vib_off + i];
                    memcpy(hdr->rumble_r, hdr->rumble_l, 4);
                
                    res = hid_write(handle, buf, sizeof(brcm_hdr));
                    Application.DoEvents();
                }
                for (int j = 0; j < 1 + loop_times; j++) {
                    for (int i = loop_start * 4; i < loop_end * 4; i = i + 4) {
                        Sleep(sample_rate);
                        memset(buf, 0, 49);
                        hdr = (brcm_hdr *)buf;
                        pkt = (brcm_cmd_01 *)(hdr + 1);
                        hdr->cmd = 0x10;
                        hdr->timer = (u8)(timming_byte & 0xF);
                        timming_byte++;
                    
                        hdr->rumble_l[0] = FormJoy.myform1.vib_loaded_file[0x0C + vib_off + i];
                        hdr->rumble_l[1] = FormJoy.myform1.vib_loaded_file[0x0D + vib_off + i];
                        hdr->rumble_l[2] = FormJoy.myform1.vib_loaded_file[0x0E + vib_off + i];
                        hdr->rumble_l[3] = FormJoy.myform1.vib_loaded_file[0x0F + vib_off + i];
                        memcpy(hdr->rumble_r, hdr->rumble_l, 4);

                        res = hid_write(handle, buf, sizeof(brcm_hdr));
                        Application.DoEvents();
                    }
                    Sleep(sample_rate);
                    // Disable vibration
                    memset(buf, 0, 49);
                    hdr = (brcm_hdr *)buf;
                    pkt = (brcm_cmd_01 *)(hdr + 1);
                    hdr->cmd = 0x10;
                    hdr->timer = (u8)(timming_byte & 0xF);
                    timming_byte++;
                    hdr->rumble_l[0] = 0x00;
                    hdr->rumble_l[1] = 0x01;
                    hdr->rumble_l[2] = 0x40;
                    hdr->rumble_l[3] = 0x40;
                    memcpy(hdr->rumble_r, hdr->rumble_l, 4);
                    res = hid_write(handle, buf, sizeof(brcm_hdr));
                    Sleep(loop_wait * sample_rate);
                }
                for (int i = loop_end * 4; i < samples * 4; i = i + 4) {
                    Sleep(sample_rate);
                    memset(buf, 0, 49);
                    hdr = (brcm_hdr *)buf;
                    pkt = (brcm_cmd_01 *)(hdr + 1);
                    hdr->cmd = 0x10;
                    hdr->timer = (u8)(timming_byte & 0xF);
                    timming_byte++;
                
                    hdr->rumble_l[0] = FormJoy.myform1.vib_loaded_file[0x0C + vib_off + i];
                    hdr->rumble_l[1] = FormJoy.myform1.vib_loaded_file[0x0D + vib_off + i];
                    hdr->rumble_l[2] = FormJoy.myform1.vib_loaded_file[0x0E + vib_off + i];
                    hdr->rumble_l[3] = FormJoy.myform1.vib_loaded_file[0x0F + vib_off + i];
                    memcpy(hdr->rumble_r, hdr->rumble_l, 4);

                    res = hid_write(handle, buf, sizeof(brcm_hdr));
                    Application.DoEvents();
                }
            }

            Sleep(sample_rate);
            // Disable vibration
            memset(buf, 0, 49);
            hdr = (brcm_hdr *)buf;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x10;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            hdr->rumble_l[0] = 0x00;
            hdr->rumble_l[1] = 0x01;
            hdr->rumble_l[2] = 0x40;
            hdr->rumble_l[3] = 0x40;
            memcpy(hdr->rumble_r, hdr->rumble_l, 4);
            res = hid_write(handle, buf, 49);

            Sleep(sample_rate + 120);
            memset(buf, 0, 49);
            hdr = (brcm_hdr *)buf;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            hdr->rumble_l[0] = 0x00;
            hdr->rumble_l[1] = 0x01;
            hdr->rumble_l[2] = 0x40;
            hdr->rumble_l[3] = 0x40;
            memcpy(hdr->rumble_r, hdr->rumble_l, 4);
            pkt->subcmd = 0x48;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf, 0, 64);

            memset(buf, 0, 49);
            hdr = (brcm_hdr *)buf;
            pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x30;
            pkt->subcmd_arg.arg1 = 0x01;
            res = hid_write(handle, buf, 49);
            res = hid_read_timeout(handle, buf, 0, 64);

            return 0;
        }


        public static int ir_sensor_auto_exposure(int white_pixels_percent) {
            int res;
            u8* buf = stackalloc u8[49];
            u16 new_exposure = 0;
            int old_exposure = ir_exposure_value; // numeric_IRExposure.Value, read before the capture started

            // Calculate new exposure;
            if (white_pixels_percent == 0)
                old_exposure += 10;
            else if (white_pixels_percent > 5)
                old_exposure -= (white_pixels_percent / 4) * 20;

            old_exposure = CLAMP(old_exposure, 0, 600);
            ir_exposure_value = old_exposure;
            ir_show_exposure(old_exposure);
            new_exposure = (u16)(old_exposure * 31200 / 1000);

            memset(buf, 0, 49);
            var hdr = (brcm_hdr *)buf;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x21;

            pkt->subcmd_21_23_04.mcu_cmd    = 0x23; // Write register cmd
            pkt->subcmd_21_23_04.mcu_subcmd = 0x04; // Write register to IR mode subcmd
            pkt->subcmd_21_23_04.no_of_reg  = 0x03; // Number of registers to write. Max 9.

            pkt->subcmd_21_23_04.reg1_addr = 0x3001; // R: 0x0130 - Set Exposure time LSByte
            pkt->subcmd_21_23_04.reg1_val  = (u8)(new_exposure & 0xFF);
            pkt->subcmd_21_23_04.reg2_addr = 0x3101; // R: 0x0131 - Set Exposure time MSByte
            pkt->subcmd_21_23_04.reg2_val  = (u8)((new_exposure & 0xFF00) >> 8);
            pkt->subcmd_21_23_04.reg3_addr = 0x0700; // R: 0x0007 - Finalize config - Without this, the register changes do not have any effect.
            pkt->subcmd_21_23_04.reg3_val  = 0x01;

            buf[48] = mcu_crc8_calc(buf + 12, 36);
            res = hid_write(handle, buf, 49);

            return res;
        }

        public static void hline(u8* buffer, u16 x0, u16 x1, u16 y, u8 brightness) {
            u8* line = buffer + y * 320;
            for (u16 x = x0; x <= x1; x++)
                line[x] = brightness;
        }

        public static void vline(u8* buffer, u16 x, u16 y0, u16 y1, u8 brightness) {
            u8* line = buffer + x;
            for (u16 y = y0; y <= y1; y++)
                line[y*320] = brightness;
        }

        public static void drawCluster(u8* buffer, u16* data) {
            u8 brightness = (u8)(data[1]);
            u16 x0 = data[4];
            u16 x1 = data[5];
            u16 y0 = data[6];
            u16 y1 = data[7];
            u16 cx = (u16)((data[2] + 32) / 64);
            u16 cy = (u16)((data[3] + 32) / 64);
            if (x1 < x0 || y1 < y0 || x1 >= 320 || y1 >= 240 || cx < x0 || cx > x1 || cy < y0 || cy > y1) {
                printf("err: ");
                for (int i = 0; i < 16; i++)
                    printf("%02x", ((u8*)data)[i]);
                printf("\n");
                return;
            }
            hline(buffer, x0, x1, y0, brightness);
            hline(buffer, x0, x1, y1, brightness);
            hline(buffer, x0, x1, cy, brightness);
            vline(buffer, x0, y0, y1, brightness);
            vline(buffer, x1, y0, y1, brightness);
            vline(buffer, cx, y0, y1, brightness);
        }

        // The Windows code ran the IR transfer on the UI thread and called DoEvents() after every
        // fragment (up to 512 per capture). Under Mono on XWayland DoEvents() often blocks for about
        // a second, while the Joy-Con waits for the fragment ACK, so frames never finished.
        // On Linux the transfer runs on its own thread (ir_worker) and the status text, stats and
        // frames are handed to the UI thread. Only the newest of each is kept, so a stalled UI
        // never slows the transfer down.
        public static volatile System.Threading.Thread ir_worker;
        public static int ir_last_frame_missing; // fragments missing from the last finished IR frame

        // Linux: the "Quick capture" option in the IR camera settings (off by default). Capture then
        // skips the auto exposure adjustment, uses the Exposure value as set and saves the second
        // frame, instead of adjusting at the start of the second frame and saving the next complete
        // one: ~11s faster at 240x320 on a Joy-Con (R).
        public static bool ir_quick_capture;
        public static bool ir_last_capture_stale; // The camera didn't apply the capture's settings

        // Linux: true when a frame's pixels line up as rows of another resolution's width, i.e. the
        // camera kept its previous resolution. A real image is smoothest (least change between
        // vertically neighboring pixels) at its own width. On a Joy-Con (R), good frames were
        // 1.25-1.75x rougher at any other width; stuck frames were 30-100x smoother at 320.
        public static bool ir_frame_has_other_width(u8* image, int max_frag_no) {
            int pixels = (max_frag_no + 1) * 300;
            int width = max_frag_no == 0x3f ? 160 : max_frag_no == 0x0f ? 80 : max_frag_no == 0x03 ? 40 : 320;
            if (width == 320)
                return false;
            double own = vertical_roughness(image, pixels, width);
            if (own < 1.0)
                return false; // (Almost) uniform, e.g. black: nothing to tell by
            foreach (int other in new[] { 320, 160, 80, 40 }) {
                if (other == width || pixels % other != 0 || pixels / other < 4)
                    continue;
                if (vertical_roughness(image, pixels, other) < own * 0.5)
                    return true;
            }
            return false;
        }

        static double vertical_roughness(u8* image, int pixels, int width) {
            long sum = 0;
            for (int i = 0; i + width < pixels; i++)
                sum += Math.Abs(image[i] - image[i + width]);
            return (double)sum / (pixels - width);
        }

        static readonly object ir_ui_lock = new object();
        static string ir_pending_status;
        static string ir_pending_help;
        static byte[] ir_pending_frame;
        static bool ir_pending_frame_save;
        static int ir_pending_exposure = -1;
        static bool ir_ui_posted;
        public static int ir_exposure_value;
        static Func<int> ir_pending_action;
        static int ir_pending_action_res;
        static volatile bool ir_pending_action_done;

        static bool on_ir_worker() {
            return ir_worker != null && System.Threading.Thread.CurrentThread == ir_worker;
        }

        // Caller holds ir_ui_lock.
        static void ir_ui_post() {
            if (ir_ui_posted)
                return;
            ir_ui_posted = true;
            try {
                FormJoy.myform1.BeginInvoke((Action)ir_ui_flush);
            }
            catch (Exception) {
                ir_ui_posted = false; // Window closed
            }
        }

        // Applies the newest pending UI updates. Runs on the UI thread.
        public static void ir_ui_flush() {
            string status, help;
            byte[] frame;
            bool save;
            int exposure;
            lock (ir_ui_lock) {
                status = ir_pending_status;     ir_pending_status = null;
                help = ir_pending_help;         ir_pending_help = null;
                frame = ir_pending_frame;       ir_pending_frame = null;
                save = ir_pending_frame_save;
                exposure = ir_pending_exposure; ir_pending_exposure = -1;
                ir_ui_posted = false;
            }
            if (FormJoy.myform1 == null || FormJoy.myform1.IsDisposed)
                return;
            if (exposure >= 0)
                FormJoy.myform1.numeric_IRExposure.Value = exposure;
            if (frame != null) {
                fixed (u8* p = frame)
                    FormJoy.myform1.setIRPictureWindow(p, true, save);
            }
            if (status != null)
                FormJoy.myform1.lbl_IRStatus.Text = status;
            if (help != null)
                FormJoy.myform1.lbl_IRHelp.Text = help;
        }

        static void ir_show_status(string text) {
            if (!on_ir_worker()) {
                FormJoy.myform1.lbl_IRStatus.Text = text;
                return;
            }
            lock (ir_ui_lock) { ir_pending_status = text; ir_ui_post(); }
        }

        static void ir_show_help(string text) {
            if (!on_ir_worker()) {
                FormJoy.myform1.lbl_IRHelp.Text = text;
                return;
            }
            lock (ir_ui_lock) { ir_pending_help = text; ir_ui_post(); }
        }

        static void ir_show_frame(u8* buf_image) {
            if (!on_ir_worker()) {
                FormJoy.myform1.setIRPictureWindow(buf_image, true);
                return;
            }
            int size = 320 * 240; // The largest frame; smaller resolutions use the start of it
            byte[] frame = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)buf_image, frame, 0, size);
            lock (ir_ui_lock) {
                ir_pending_frame = frame;
                ir_pending_frame_save = !enable_IRVideoPhoto;
                ir_ui_post();
            }
        }

        static void ir_show_exposure(int exposure) {
            if (!on_ir_worker()) {
                FormJoy.myform1.numeric_IRExposure.Value = exposure;
                return;
            }
            lock (ir_ui_lock) { ir_pending_exposure = exposure; ir_ui_post(); }
        }

        // Runs where the Windows code ran DoEvents(): on the worker, it runs a device command the
        // UI asked for (e.g. "Apply" live config while streaming), so it goes out between
        // fragments exactly like it did from inside DoEvents() on Windows.
        static void ir_pump() {
            if (!on_ir_worker()) {
                Application.DoEvents();
                return;
            }
            Func<int> action;
            lock (ir_ui_lock) { action = ir_pending_action; ir_pending_action = null; }
            if (action != null) {
                ir_pending_action_res = action();
                ir_pending_action_done = true;
            }
        }

        // Runs a device command on the IR worker if one is active (so only one thread talks to
        // the controller), otherwise directly. Called from the UI thread; keeps the UI alive.
        public static int ir_run_on_device(Func<int> action) {
            System.Threading.Thread worker = ir_worker;
            if (worker == null || on_ir_worker())
                return action();
            ir_pending_action_done = false;
            lock (ir_ui_lock) { ir_pending_action = action; }
            while (!ir_pending_action_done) {
                if (!worker.IsAlive) {
                    lock (ir_ui_lock) {
                        if (ir_pending_action == action) {
                            ir_pending_action = null;
                            return action();
                        }
                    }
                }
                Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            return ir_pending_action_res;
        }

        // Runs func (ir_sensor) on the IR worker thread and keeps the UI responsive until it ends.
        public static int ir_run_worker(Func<int> func) {
            int res = 0;
            Exception error = null;
            if (ir_worker != null) {
                // Shouldn't happen: the IR buttons are disabled while the camera runs.
                trace_note("IR: already running, request ignored");
                return 0;
            }
            var t = new System.Threading.Thread(() => {
                trace_note("IR: thread started");
                try { res = func(); }
                catch (Exception ex) { error = ex; }
                trace_note("IR: thread finished, result " + res + (error != null ? ", error " + error.GetType().Name + ": " + error.Message : ""));
            });
            t.IsBackground = true;
            ir_worker = t;
            t.Start();
            while (!t.Join(10))
                Application.DoEvents();
            ir_worker = null;
            trace_note("IR: window thread saw the end");
            ir_ui_flush();
            trace_note("IR: window updated");
            if (error != null) {
                Console.Error.WriteLine("IR camera thread failed: " + error);
                throw new Exception("IR camera thread failed", error);
            }
            return res;
        }

        public static int get_raw_ir_image(u8 mode, u8 show_status) {
            StringBuilder ir_status = new StringBuilder();

            int elapsed_time = 0;
            int elapsed_time2 = 0;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            u8* buf = stackalloc u8[49];
            u8* buf_reply = stackalloc u8[0x170];
            u8* buf_image = (u8*)System.Runtime.InteropServices.Marshal.AllocHGlobal(19 * 4096); // 8bpp greyscale image.
            u16 bad_signal = 0;
            int error_reading = 0;
            float noise_level = 0.0f;
            int avg_intensity_percent = 0;
            int previous_frag_no = 0;
            int got_frag_no = 0;
            int missed_packet_no = 0;
            bool missed_packet = false;
            int initialization = 2;
            // Linux: which fragments of the current frame arrived. The Windows code counted a
            // frame as done when its last fragment came in order, even if an earlier one was
            // skipped and not resent yet, so a saved capture could keep a strip of the previous
            // frame (seen as a bar at the image edge). The saved frame must now be complete.
            u8* frag_seen = stackalloc u8[256];
            int incomplete_retries = 0;
            bool ir_exposure_adjusted = false;
            int frames_done = 0;
            long first_frame_stats = -1;
            ir_last_capture_stale = false;
            memset(frag_seen, 0, 256);
            bool quick_capture = ir_quick_capture && !enable_IRVideoPhoto; // Captures only; read once per run
            trace_note("IR: quick capture " + (quick_capture ? "on" : "off")
                + ", skip leftover " + (ir_skip_leftover ? "on" : "off") + ", patient setup " + (ir_patient_setup ? "on" : "off"));
            bool skip_tried = false, skip_pending = false;
            u8* leftover_frag0 = stackalloc u8[300];
            long leftover_stats = 0;
            // Linux fix: the Joy-Con counts white pixels on the full sensor, whatever the resolution
            // (a 60x80 capture reported 8302, more than its 4800 pixels). The Windows code divided by
            // the current resolution's pixel count, so below 240x320 auto exposure overreacted and
            // dropped the exposure to 0 (black images). Use the full sensor, capped like the u16 count.
            int max_pixels = 218 * 300;
            int white_pixels_percent = 0;

            memset(buf_image, 0, 19 * 4096); // C++ cleared sizeof(pointer) bytes; clear the whole frame instead

            memset(buf, 0, 49);
            memset(buf_reply, 0, 368);
            var hdr = (brcm_hdr *)buf;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x11;
            pkt->subcmd = 0x03;
            buf[48] = 0xFF;

            // First ack
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            buf[14] = 0x0;
            buf[47] = mcu_crc8_calc(buf + 11, 36);
            hid_write(handle, buf, 49);

            // IR Read/ACK loop for fragmented data packets. 
            // It also avoids requesting missed data fragments, we just skip it to not complicate things.
            while (enable_IRVideoPhoto || initialization != 0) {
                memset(buf_reply, 0, 368);
                hid_read_timeout(handle, buf_reply, 368, 200);

                //Check if new packet
                if (buf_reply[0] == 0x31 && buf_reply[49] == 0x03 && buf_reply[51] == mode) {
                    got_frag_no = buf_reply[52];
                    bool frame_done = false;

                    // JCTOOL_IR_SKIP_LEFTOVER: see whether the Joy-Con started a new frame after we
                    // ACKed the whole leftover frame. A new frame 0 differs from the leftover's.
                    if (skip_pending && mode == 0x07) {
                        skip_pending = false;
                        bool same = got_frag_no == 0;
                        for (int i = 0; same && i < 300; i++)
                            if (buf_reply[59 + i] != leftover_frag0[i])
                                same = false;
                        if (got_frag_no == 0 && !same) {
                            trace_note("IR: skip leftover worked, a new frame started");
                            initialization = 1;
                            frames_done = 1;
                            first_frame_stats = leftover_stats;
                            memset(frag_seen, 0, 256);
                            previous_frag_no = ir_max_frag_no; // So this fragment 0 is next in sequence
                        }
                        else {
                            trace_note("IR: skip leftover ignored (got fragment " + got_frag_no + (same ? ", the same data" : "") + ")");
                        }
                    }
                    if (got_frag_no == (previous_frag_no + 1) % (ir_max_frag_no + 1) || mode != 0x07) {
                        previous_frag_no = got_frag_no;

                        // ACK for fragment
                        hdr->timer = (u8)(timming_byte & 0xF);
                        timming_byte++;
                        buf[14] = (u8)(previous_frag_no);
                        buf[47] = mcu_crc8_calc(buf + 11, 36);
                        hid_write(handle, buf, 49);

                        if (mode == 0x06) {
                            memset(buf_image, 0, 320 * 240);
                            for (int i = 61; i + 16 <= 59+300; i += 16) {
                                if (buf_reply[i] != 0 || buf_reply[i + 1] != 0) {
                                    drawCluster(buf_image, (u16*)(buf_reply + i));
                                }
                            }
                        }
                        else if (mode == 0x04)
                        {
                            // weird data arrangement!
                            memset(buf_image, 0, 320 * 240);
                            for (int i = 61; i + 16 <= 59+300; i += 16) {
                                if (i == 61 + 48 || i == 61 + 97 || i == 61 + 146 || i == 61 + 195 || i == 61 + 244)
                                    i++;
                                if (buf_reply[i] != 0 || buf_reply[i + 1] != 0) {
                                    drawCluster(buf_image, (u16*)(buf_reply + i));
                                }
                            }
                        }
                        else if (mode == 0x07) {
                            memcpy(buf_image + (300 * got_frag_no), buf_reply + 59, 300);
                            frag_seen[got_frag_no] = 1;

                            // Auto exposure.
                            // TODO: Fix placement, so it doesn't drop next fragment.
                            // Linux: the Joy-Con answers the exposure change in place of a fragment
                            // and doesn't resend it, so that frame is always incomplete. While a
                            // capture waits for a complete frame, adjust only once, so the retry
                            // frames can come through whole. Streaming adjusts every frame as before.
                            // "Quick capture" skips it and uses the Exposure value as set.
                            if (enable_IRAutoExposure && !quick_capture && initialization < 2 && got_frag_no == 0
                                && (initialization == 0 || !ir_exposure_adjusted)) {
                                white_pixels_percent = (int)((*(u16*)&buf_reply[55] * 100) / max_pixels);
                                ir_sensor_auto_exposure(white_pixels_percent);
                                ir_exposure_adjusted = true;
                            }

                            // Status percentage
                            ir_status.Clear();
                            if (initialization < 2) {
                                if (show_status == 2)
                                    ir_status.Append("Status: Streaming.. ");
                                else
                                    ir_status.Append("Status: Receiving.. ");
                            }
                            else
                                ir_status.Append("Status: Initializing.. ");
                            ir_status.Append(String.Format("{0,3:F0}", (float)got_frag_no / (float)(ir_max_frag_no + 1) * 100.0f));
                            ir_status.Append("% - ");

                            //debug
                           // printf("%02X Frag: Copy\n", got_frag_no);

                            ir_show_status(ir_status.ToString() + (sw.ElapsedMilliseconds - elapsed_time).ToString() + "ms");
                            elapsed_time = (int)sw.ElapsedMilliseconds;
                        }

                        // Check if final fragment. Draw the frame.
                        frame_done = got_frag_no == ir_max_frag_no || mode != 0x07;
                    }
                    // Repeat/Missed fragment
                    else if (got_frag_no != 0 || previous_frag_no != 0) {
                        // Check if repeat ACK should be send. Avoid writing to image buffer.
                        if (got_frag_no == previous_frag_no) {
                            //debug
                            //printf("%02X Frag: Repeat\n", got_frag_no);

                            // ACK for fragment
                            hdr->timer = (u8)(timming_byte & 0xF);
                            timming_byte++;
                            buf[14] = (u8)(got_frag_no);
                            buf[47] = mcu_crc8_calc(buf + 11, 36);
                            hid_write(handle, buf, 49);

                            missed_packet = false;
                        }
                        // Check if missed fragment and request it.
                        else if(missed_packet_no != got_frag_no && !missed_packet) {
                            if (ir_max_frag_no != 0x03) {
                                //debug
                                //printf("%02X Frag: Missed %02X, Prev: %02X, PrevM: %02X\n", got_frag_no, previous_frag_no + 1, previous_frag_no, missed_packet_no);

                                // Missed packet
                                hdr->timer = (u8)(timming_byte & 0xF);
                                timming_byte++;
                                //Request for missed packet. You send what the next fragment number will be, instead of the actual missed packet.
                                buf[12] = 0x1;
                                buf[13] = (u8)(previous_frag_no + 1);
                                buf[14] = 0;
                                buf[47] = mcu_crc8_calc(buf + 11, 36);
                                hid_write(handle, buf, 49);

                                buf[12] = 0x00;
                                buf[13] = 0x00;

                                memcpy(buf_image + (300 * got_frag_no), buf_reply + 59, 300);
                                frag_seen[got_frag_no] = 1;

                                previous_frag_no = got_frag_no;
                                missed_packet_no = got_frag_no - 1;
                                missed_packet = true;
                            }
                            // Check if missed fragment and res is 30x40. Don't request it.
                            else {
                                //debug
                                //printf("%02X Frag: Missed but res is 30x40\n", got_frag_no);

                                // ACK for fragment
                                hdr->timer = (u8)(timming_byte & 0xF);
                                timming_byte++;
                                buf[14] = (u8)(got_frag_no);
                                buf[47] = mcu_crc8_calc(buf + 11, 36);
                                hid_write(handle, buf, 49);

                                memcpy(buf_image + (300 * got_frag_no), buf_reply + 59, 300);
                                frag_seen[got_frag_no] = 1;

                                previous_frag_no = got_frag_no;
                            }
                        }
                        // Got the requested missed fragments.
                        else if (missed_packet_no == got_frag_no){
                            //debug
                            //printf("%02X Frag: Got missed %02X\n", got_frag_no, missed_packet_no);

                            // ACK for fragment
                            hdr->timer = (u8)(timming_byte & 0xF);
                            timming_byte++;
                            buf[14] = (u8)(got_frag_no);
                            buf[47] = mcu_crc8_calc(buf + 11, 36);
                            hid_write(handle, buf, 49);

                            memcpy(buf_image + (300 * got_frag_no), buf_reply + 59, 300);
                            frag_seen[got_frag_no] = 1;

                            previous_frag_no = got_frag_no;
                            missed_packet = false;
                        }
                        // Repeat of fragment that is not max fragment.
                        else {
                            //debug
                            //printf("%02X Frag: RepeatWoot M:%02X\n", got_frag_no, missed_packet_no);

                            // ACK for fragment
                            hdr->timer = (u8)(timming_byte & 0xF);
                            timming_byte++;
                            buf[14] = (u8)(got_frag_no);
                            buf[47] = mcu_crc8_calc(buf + 11, 36);
                            hid_write(handle, buf, 49);
                        }
                    
                        // Status percentage
                        ir_status.Clear();
                        if (initialization < 2) {
                            if (show_status == 2)
                                ir_status.Append("Status: Streaming.. ");
                            else
                                ir_status.Append("Status: Receiving.. ");
                        }
                        else
                            ir_status.Append("Status: Initializing.. ");
                        ir_status.Append(String.Format("{0,3:F0}", (float)got_frag_no / (float)(ir_max_frag_no + 1) * 100.0f));
                        ir_status.Append("% - ");

                        ir_show_status(ir_status.ToString() + (sw.ElapsedMilliseconds - elapsed_time).ToString() + "ms");
                        elapsed_time = (int)sw.ElapsedMilliseconds;
                    }
                
                    // Streaming start
                    else {
                        // ACK for fragment
                        hdr->timer = (u8)(timming_byte & 0xF);
                        timming_byte++;
                        buf[14] = (u8)(got_frag_no);
                        // JCTOOL_IR_SKIP_LEFTOVER: on the leftover frame's first fragment, ACK its last one.
                        if (ir_skip_leftover && !skip_tried && mode == 0x07 && initialization == 2 && got_frag_no == 0) {
                            skip_tried = true;
                            skip_pending = true;
                            memcpy(leftover_frag0, buf_reply + 59, 300);
                            leftover_stats = ((long)buf_reply[53] << 32) | ((long)buf_reply[54] << 16) | *(u16*)&buf_reply[55];
                            buf[14] = (u8)ir_max_frag_no;
                        }
                        buf[47] = mcu_crc8_calc(buf + 11, 36);
                        hid_write(handle, buf, 49);
                        buf[14] = (u8)(got_frag_no);

                        memcpy(buf_image + (300 * got_frag_no), buf_reply + 59, 300);
                        frag_seen[got_frag_no] = 1;

                        //debug
                        //printf("%02X Frag: 0 %02X\n", buf_reply[52], previous_frag_no);

                        ir_show_status((sw.ElapsedMilliseconds - elapsed_time).ToString() + "ms");
                        elapsed_time = (int)sw.ElapsedMilliseconds;

                        previous_frag_no = 0;
                    }

                    if (frame_done) {
                        // Update Viewport
                        elapsed_time2 = (int)sw.ElapsedMilliseconds - elapsed_time2;
                        ir_show_frame(buf_image);

                        //debug
                        //printf("%02X Frag: Draw -------\n", got_frag_no);

                        // Stats/IR header parsing
                        // buf_reply[53]: Average Intensity. 0-255 scale.
                        // buf_reply[54]: Unknown. Shows up only when EXFilter is enabled.
                        // *(u16*)&buf_reply[55]: White pixels (pixels with 255 value). Max 65535. Uint16 constraints, even though max is 76800.
                        // *(u16*)&buf_reply[57]: Pixels with ambient noise from external light sources (sun, lighter, IR remotes, etc). Cleaned by External Light Filter.
                        noise_level = (float)(*(u16*)&buf_reply[57]) / ((float)(*(u16*)&buf_reply[55]) + 1.0f);
                        white_pixels_percent = (int)((*(u16*)&buf_reply[55] * 100) / max_pixels);
                        avg_intensity_percent = (int)((buf_reply[53] * 100) / 255);
                        ir_show_help(String.Format("Amb Noise: {0:f2},  Int: {1:D}%,  FPS: {2:D} ({3:D}ms)\nEXFilter: {4:D},  White Px: {5:D}%,  EXF Int: {6:D}",
                            noise_level, avg_intensity_percent, elapsed_time2 > 0 ? (int)(1000 / elapsed_time2) : 0, elapsed_time2, *(u16*)&buf_reply[57], white_pixels_percent, buf_reply[54]));

                        elapsed_time2 = (int)sw.ElapsedMilliseconds;

                        int missing = 0;
                        if (mode == 0x07) {
                            for (int i = 0; i <= ir_max_frag_no; i++)
                                if (frag_seen[i] == 0)
                                    missing++;
                            if (missing > 0)
                                trace_note("IR: frame done with " + missing + " fragment(s) not received");
                        }
                        memset(frag_seen, 0, 256);
                        ir_last_frame_missing = missing;

                        if (initialization != 0) {
                            // Linux: don't finish a capture on an incomplete frame; take the next
                            // one instead (a few times at most, so a capture always ends).
                            if (initialization == 1 && missing > 0 && incomplete_retries < 3)
                                incomplete_retries++;
                            else
                                initialization--;

                            // Linux: check that the camera applied the capture's settings. The first
                            // frame of a run is a leftover frame from before. A 120x160 capture was
                            // seen to keep sending 240x320 rows (cut into 120x160's 64 fragments),
                            // either repeating the leftover frame or as new frames.
                            long frame_stats = ((long)buf_reply[53] << 32) | ((long)buf_reply[54] << 16) | *(u16*)&buf_reply[55];
                            if (frames_done++ == 0)
                                first_frame_stats = frame_stats;
                            else if (initialization == 0 && mode == 0x07 && !enable_IRVideoPhoto) {
                                if (frame_stats == first_frame_stats) {
                                    ir_last_capture_stale = true;
                                    trace_note("IR: the saved frame repeats the leftover first frame");
                                }
                                else if (ir_frame_has_other_width(buf_image, ir_max_frag_no)) {
                                    ir_last_capture_stale = true;
                                    trace_note("IR: the saved frame has rows of another resolution");
                                }
                            }
                        }
                    }
                }
                // Empty IR report. Send Ack again. Otherwise, it fallbacks to high latency mode (30ms per data fragment)
                else if (buf_reply[0] == 0x31) {
                    // ACK for fragment
                    hdr->timer = (u8)(timming_byte & 0xF);
                    timming_byte++;

                    // Send ACK again or request missed frag
                    if (buf_reply[49] == 0xFF) {
                        buf[14] = (u8)(previous_frag_no);
                    }
                    else if (buf_reply[49] == 0x00) {
                        buf[12] = 0x1;
                        buf[13] = (u8)(previous_frag_no + 1);
                        buf[14] = 0;
                       // printf("%02X Mode: Missed next packet %02X\n", buf_reply[49], previous_frag_no + 1);
                    }

                    buf[47] = mcu_crc8_calc(buf + 11, 36);
                    hid_write(handle, buf, 49);

                    buf[12] = 0x00;
                    buf[13] = 0x00;
                }
                ir_pump();
            }
        
            System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)buf_image);

            return 0;
        }


        // Linux experiments (environment variables, off by default):
        // JCTOOL_IR_PATIENT_SETUP=1  Wait ~20 reports (~300ms) for a setup reply before re-sending
        //                            instead of 9 (~135ms). A Joy-Con (R) answered after 120-150ms,
        //                            so the early re-sends caused duplicate replies and more re-sends.
        // JCTOOL_IR_SKIP_LEFTOVER=1  ACK the whole leftover first frame when its first fragment
        //                            arrives, hoping the Joy-Con moves on to a new frame.
        public static bool ir_patient_setup = Environment.GetEnvironmentVariable("JCTOOL_IR_PATIENT_SETUP") == "1";
        public static bool ir_skip_leftover = Environment.GetEnvironmentVariable("JCTOOL_IR_SKIP_LEFTOVER") == "1";
        static int ir_setup_reads { get { return ir_patient_setup ? 19 : 8; } }

        public static int ir_sensor(ref ir_image_config ir_cfg) {
            int res;
            u8* buf = stackalloc u8[0x170];
            const int output_buffer_length = 49;
            int error_reading = 0;
            int res_get = 0;
            // Set input report to x31
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x31;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (*(u16*)&buf[0xD] == 0x0380)
                        goto step1;

                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 1;
                    goto step10;
                }
            }

        step1:
            // Enable MCU
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x22;
                pkt->subcmd_arg.arg1 = 0x1;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (*(u16*)&buf[0xD] == 0x2280)
                        goto step2;

                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 2;
                    goto step10;
                }
            }

        step2:
            // Request MCU mode status
            error_reading = 0;
            while (true) { // Not necessary, but we keep to make sure the MCU is ready.
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x01;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x31) {
                        //if (buf[49] == 0x01 && buf[56] == 0x06) // MCU state is Initializing
                        // *(u16*)buf[52]LE x04 in lower than 3.89fw, x05 in 3.89
                        // *(u16*)buf[54]LE x12 in lower than 3.89fw, x18 in 3.89
                        // buf[56]: mcu mode state
                        if (buf[49] == 0x01 && buf[56] == 0x01) // MCU state is Standby
                            goto step3;
                    }
                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 3;
                    goto step10;
                }
            }

        step3:
            // Set MCU mode
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x21;
            
                pkt->subcmd_21_21.mcu_cmd    = 0x21; // Set MCU mode cmd
                pkt->subcmd_21_21.mcu_subcmd = 0x00; // Set MCU mode cmd
                pkt->subcmd_21_21.mcu_mode   = 0x05; // MCU mode - 1: Standby, 4: NFC, 5: IR, 6: Initializing/FW Update?

                buf[48] = mcu_crc8_calc(buf + 12, 36);
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x21) {
                        // *(u16*)buf[18]LE x04 in lower than 3.89fw, x05 in 3.89
                        // *(u16*)buf[20]LE x12 in lower than 3.89fw, x18 in 3.89
                        // buf[56]: mcu mode state
                        if (buf[15] == 0x01 && *(u32*)&buf[22] == 0x01) // Mcu mode is Standby
                            goto step4;
                    }
                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 4;
                    goto step10;
                }
            }

        step4:
            // Request MCU mode status
            error_reading = 0;
            while (true) { // Not necessary, but we keep to make sure the MCU mode changed.
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x01;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x31) {
                        // *(u16*)buf[52]LE x04 in lower than 3.89fw, x05 in 3.89
                        // *(u16*)buf[54]LE x12 in lower than 3.89fw, x18 in 3.89
                        if (buf[49] == 0x01 && buf[56] == 0x05) // Mcu mode is IR
                            goto step5;
                    }
                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 5;
                    goto step10;
                }
            }

        step5:
            // Set IR mode and number of packets for each data blob. Blob size is packets * 300 bytes.
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;

                pkt->subcmd = 0x21;
                pkt->subcmd_21_23_01.mcu_cmd     = 0x23;
                pkt->subcmd_21_23_01.mcu_subcmd  = 0x01; // Set IR mode cmd
                pkt->subcmd_21_23_01.mcu_ir_mode = ir_cfg.ir_mode; // IR mode - 2: No mode/Disable?, 3: Moment, 4: Dpd (Wii-style pointing), 6: Clustering,
                                                         // 7: Image transfer, 8-10: Hand analysis (Silhouette, Image, Silhouette/Image), 0,1/5/10+: Unknown
                pkt->subcmd_21_23_01.no_of_frags = ir_max_frag_no; // Set number of packets to output per buffer
                pkt->subcmd_21_23_01.mcu_major_v = 0x0500; // Set required IR MCU FW v5.18. Major 0x0005.
                pkt->subcmd_21_23_01.mcu_minor_v = 0x1800; // Set required IR MCU FW v5.18. Minor 0x0018.

                buf[48] = mcu_crc8_calc(buf + 12, 36);
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);

                    if (buf[0] == 0x21) {
                        // Mode set Ack
                        if (buf[15] == 0x0b)
                            goto step6;
                    }
                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 6;
                    goto step10;
                }
            }

        step6:
            // Request IR mode status
            error_reading = 0;
            while (false) { // Not necessary
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;

                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x02;

                buf[47] = mcu_crc8_calc(buf + 11, 36);
                buf[48] = 0xFF;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x31) {
                        // mode set to 7: Image transfer

                        if (buf[49] == 0x13 && buf[50] == 0 && buf[51] == ir_cfg.ir_mode)
                            goto step7;
                    }
                    retries++;
                    if (retries > 4 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 7;
                    goto step10;
                }
            }

        step7:
            // Write to registers for the selected IR mode
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x21;

                pkt->subcmd_21_23_04.mcu_cmd    = 0x23; // Write register cmd
                pkt->subcmd_21_23_04.mcu_subcmd = 0x04; // Write register to IR mode subcmd
                pkt->subcmd_21_23_04.no_of_reg  = 0x09; // Number of registers to write. Max 9.      

                pkt->subcmd_21_23_04.reg1_addr  = 0x2e00; // R: 0x002e - Set Resolution based on sensor binning and skipping
                pkt->subcmd_21_23_04.reg1_val   = ir_cfg.ir_res_reg;
                pkt->subcmd_21_23_04.reg2_addr  = 0x3001; // R: 0x0130 - Set Exposure time LSByte - (31200 * us /1000) & 0xFF - Max: 600us, Max encoded: 0x4920.
                pkt->subcmd_21_23_04.reg2_val   = (u8)(ir_cfg.ir_exposure & 0xFF);
                pkt->subcmd_21_23_04.reg3_addr  = 0x3101; // R: 0x0131 - Set Exposure time MSByte - ((31200 * us /1000) & 0xFF00) >> 8
                pkt->subcmd_21_23_04.reg3_val   = (u8)((ir_cfg.ir_exposure & 0xFF00) >> 8);
                pkt->subcmd_21_23_04.reg4_addr  = 0x3201; // R: 0x0132 - Enable Max exposure Time - 0: Manual exposure, 1: Max exposure
                pkt->subcmd_21_23_04.reg4_val   = 0x00;
                pkt->subcmd_21_23_04.reg5_addr  = 0x1000; // R: 0x0010 - Set IR Leds groups state - Only 3 LSB usable
                pkt->subcmd_21_23_04.reg5_val   = ir_cfg.ir_leds;
                pkt->subcmd_21_23_04.reg6_addr  = 0x2e01; // R: 0x012e - Set digital gain LSB 4 bits of the value - 0-0xff
                pkt->subcmd_21_23_04.reg6_val   = (u8)((ir_cfg.ir_digital_gain & 0xF) << 4);
                pkt->subcmd_21_23_04.reg7_addr  = 0x2f01; // R: 0x012f - Set digital gain MSB 4 bits of the value - 0-0x7
                pkt->subcmd_21_23_04.reg7_val   = (u8)((ir_cfg.ir_digital_gain & 0xF0) >> 4);
                pkt->subcmd_21_23_04.reg8_addr  = 0x0e00; // R: 0x00e0 - External light filter - LS o bit0: Off/On, bit1: 0x/1x, bit2: ??, bit4,5: ??.
                pkt->subcmd_21_23_04.reg8_val   = ir_cfg.ir_ex_light_filter;
                pkt->subcmd_21_23_04.reg9_addr  = 0x4301; // R: 0x0143 - ExLF/White pixel stats threshold - 200: Default
                pkt->subcmd_21_23_04.reg9_val   = 0xc8;

                buf[48] = mcu_crc8_calc(buf + 12, 36);
                res = hid_write(handle, buf, output_buffer_length);

                // Request IR mode status, before waiting for the x21 ack
                memset(buf, 0, 368);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x02;
                buf[47] = mcu_crc8_calc(buf + 11, 36);
                buf[48] = 0xFF;
                res = hid_write(handle, buf, output_buffer_length);

                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x21) {
                        // Registers for mode 7: Image transfer set
                        if (buf[15] == 0x13 && buf[16] == 0 && buf[17] == (ir_cfg.ir_mode == 0x04 ? 0x02 : ir_cfg.ir_mode))
                            goto step8;
                    }
                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 8;
                    goto step10;
                }
            }

        step8:
            // Write to registers for the selected IR mode
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x21;

                pkt->subcmd_21_23_04.mcu_cmd    = 0x23; // Write register cmd
                pkt->subcmd_21_23_04.mcu_subcmd = 0x04; // Write register to IR mode subcmd
                pkt->subcmd_21_23_04.no_of_reg  = 0x08; // Number of registers to write. Max 9.      

                pkt->subcmd_21_23_04.reg1_addr  = 0x1100; // R: 0x0011 - Leds 1/2 Intensity - Max 0x0F.
                pkt->subcmd_21_23_04.reg1_val   = (u8)((ir_cfg.ir_leds_intensity >> 8) & 0xFF);
                pkt->subcmd_21_23_04.reg2_addr  = 0x1200; // R: 0x0012 - Leds 3/4 Intensity - Max 0x10.
                pkt->subcmd_21_23_04.reg2_val   = (u8)(ir_cfg.ir_leds_intensity & 0xFF);
                pkt->subcmd_21_23_04.reg3_addr  = 0x2d00; // R: 0x002d - Flip image - 0: Normal, 1: Vertically, 2: Horizontally, 3: Both 
                pkt->subcmd_21_23_04.reg3_val   = ir_cfg.ir_flip;
                pkt->subcmd_21_23_04.reg4_addr  = 0x6701; // R: 0x0167 - Enable De-noise smoothing algorithms - 0: Disable, 1: Enable.
                pkt->subcmd_21_23_04.reg4_val   = (u8)((ir_cfg.ir_denoise >> 16) & 0xFF);
                pkt->subcmd_21_23_04.reg5_addr  = 0x6801; // R: 0x0168 - Edge smoothing threshold - Max 0xFF, Default 0x23
                pkt->subcmd_21_23_04.reg5_val   = (u8)((ir_cfg.ir_denoise >> 8) & 0xFF);
                pkt->subcmd_21_23_04.reg6_addr  = 0x6901; // R: 0x0169 - Color Interpolation threshold - Max 0xFF, Default 0x44
                pkt->subcmd_21_23_04.reg6_val   = (u8)(ir_cfg.ir_denoise & 0xFF);
                pkt->subcmd_21_23_04.reg7_addr  = 0x0400; // R: 0x0004 - LSB Buffer Update Time - Default 0x32
                if (ir_cfg.ir_res_reg == 0x69)
                    pkt->subcmd_21_23_04.reg7_val = 0x2d; // A value of <= 0x2d is fast enough for 30 x 40, so the first fragment has the updated frame.  
                else
                    pkt->subcmd_21_23_04.reg7_val = 0x32; // All the other resolutions the default is enough. Otherwise a lower value can break hand analysis.
                pkt->subcmd_21_23_04.reg8_addr  = 0x0700; // R: 0x0007 - Finalize config - Without this, the register changes do not have any effect.
                pkt->subcmd_21_23_04.reg8_val   = 0x01;

                buf[48] = mcu_crc8_calc(buf + 12, 36);
                res = hid_write(handle, buf, output_buffer_length);

                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x21) {
                        // Registers for mode 7: Image transfer set
                        // Keep the original x0700 check for image transfer and also accept the reply seen in pointing/clustering
                        if (buf[15] == 0x13 && ((buf[16] == 0 && buf[17] == (ir_cfg.ir_mode == 0x04 ? 0x02 : ir_cfg.ir_mode)) ||
                            (buf[50] == 0 && buf[51] == ir_cfg.ir_mode)))
                            goto step9;
                        // If the Joy-Con gets to reply to the previous x11 - x03 02 cmd before sending the above,
                        // it will reply with the following if we do not send x11 - x03 02 again:
                        else if (buf[15] == 0x23) // Got mcu mode config write.
                            goto step9;
                    }
                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 9;
                    goto step10;
                }
            }

        step9:
            // Stream or Capture images from NIR Camera
            if (enable_IRVideoPhoto)
                res_get = get_raw_ir_image(ir_cfg.ir_mode, 2);
            else
                res_get = get_raw_ir_image(ir_cfg.ir_mode, 1);

            //////
            // TODO: Should we send subcmd x21 with 'x230102' to disable IR mode before disabling MCU?
        step10:
            // Disable MCU
            memset(buf, 0, 368);
            var hdr0 = (brcm_hdr *)buf;
            var pkt0 = (brcm_cmd_01 *)(hdr0 + 1);
            hdr0->cmd = 1;
            hdr0->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt0->subcmd = 0x22;
            pkt0->subcmd_arg.arg1 = 0x00;
            res = hid_write(handle, buf, output_buffer_length);
            res = hid_read_timeout(handle, buf, 368, 64);  


            // Set input report back to x3f
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x3f;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (*(u16*)&buf[0xD] == 0x0380)
                        goto stepf;

                    retries++;
                    if (retries > ir_setup_reads || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    goto stepf;
                }
            }

        stepf:
            return res_get;
        }


        public static int get_ir_registers(int start_reg, int reg_group) {
            int res;
            u8* buf = stackalloc u8[0x170];
            const int output_buffer_length = 49;
            int error_reading = 0;
            int res_get = 0;

            // Get the IR registers
            error_reading = 0;
            int pos_ir_registers = start_reg;
            while (true) {
            repeat_send:
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                memset(buf, 0, 368);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x03;

                buf[12] = 0x1; // seems to be always 0x01

                buf[13] = (u8)(pos_ir_registers); // 0-4 registers page/group
                buf[14] = 0x00; // offset. this plus the number of registers, must be less than x7f
                buf[15] = 0x7f; // Number of registers to show + 1

                buf[47] = mcu_crc8_calc(buf + 11, 36);

                res = hid_write(handle, buf, output_buffer_length);

                int tries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[49] == 0x1b && buf[51] == pos_ir_registers && buf[52] == 0x00) {
                        error_reading = 0;
                        printf("--.%02X, %02X : %02X:\n", buf[51], buf[52], buf[53]);
                        for (int i = 0; i <= buf[52] + buf[53]; i++)
                            if ((i & 0xF) == 0xF)
                                printf("%02X | ", buf[54 + i]);
                            else
                                printf("%02X ", buf[54 + i]);
                        printf("\n");
                        break;
                    }
                    tries++;
                    if (tries > 8) {
                        error_reading++;
                        if (error_reading > 5) {
                            return 1;
                        }
                        goto repeat_send;
                    }

                }
                pos_ir_registers++;
                if (pos_ir_registers > reg_group) {
                    break;
                }
            
            }
            printf("\n");

            return 0;
        }


        public static int ir_sensor_config_live(ref ir_image_config ir_cfg) {
            int res;
            u8* buf = stackalloc u8[49];

            memset(buf, 0, 49);
            var hdr = (brcm_hdr *)buf;
            var pkt = (brcm_cmd_01 *)(hdr + 1);
            hdr->cmd = 0x01;
            hdr->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt->subcmd = 0x21;

            pkt->subcmd_21_23_04.mcu_cmd    = 0x23; // Write register cmd
            pkt->subcmd_21_23_04.mcu_subcmd = 0x04; // Write register to IR mode subcmd
            pkt->subcmd_21_23_04.no_of_reg  = 0x09; // Number of registers to write. Max 9.

            pkt->subcmd_21_23_04.reg1_addr = 0x3001; // R: 0x0130 - Set Exposure time LSByte
            pkt->subcmd_21_23_04.reg1_val  = (u8)(ir_cfg.ir_exposure & 0xFF);
            pkt->subcmd_21_23_04.reg2_addr = 0x3101; // R: 0x0131 - Set Exposure time MSByte
            pkt->subcmd_21_23_04.reg2_val  = (u8)((ir_cfg.ir_exposure & 0xFF00) >> 8);
            pkt->subcmd_21_23_04.reg3_addr = 0x1000; // R: 0x0010 - Set IR Leds groups state
            pkt->subcmd_21_23_04.reg3_val  = ir_cfg.ir_leds;
            pkt->subcmd_21_23_04.reg4_addr = 0x2e01; // R: 0x012e - Set digital gain LSB 4 bits
            pkt->subcmd_21_23_04.reg4_val  = (u8)((ir_cfg.ir_digital_gain & 0xF) << 4);
            pkt->subcmd_21_23_04.reg5_addr = 0x2f01; // R: 0x012f - Set digital gain MSB 4 bits
            pkt->subcmd_21_23_04.reg5_val  = (u8)((ir_cfg.ir_digital_gain & 0xF0) >> 4);
            pkt->subcmd_21_23_04.reg6_addr = 0x0e00; // R: 0x00e0 - External light filter
            pkt->subcmd_21_23_04.reg6_val  = ir_cfg.ir_ex_light_filter;
            pkt->subcmd_21_23_04.reg7_addr = (u16)((ir_cfg.ir_custom_register & 0xFF) << 8 | (ir_cfg.ir_custom_register >> 8) & 0xFF);
            pkt->subcmd_21_23_04.reg7_val  = (u8)((ir_cfg.ir_custom_register >> 16) & 0xFF);
            pkt->subcmd_21_23_04.reg8_addr = 0x1100; // R: 0x0011 - Leds 1/2 Intensity - Max 0x0F.
            pkt->subcmd_21_23_04.reg8_val  = (u8)((ir_cfg.ir_leds_intensity >> 8) & 0xFF);
            pkt->subcmd_21_23_04.reg9_addr = 0x1200; // R: 0x0012 - Leds 3/4 Intensity - Max 0x10.
            pkt->subcmd_21_23_04.reg9_val  = (u8)(ir_cfg.ir_leds_intensity & 0xFF);

            buf[48] = mcu_crc8_calc(buf + 12, 36);
            res = hid_write(handle, buf, 49);

            // Important. Otherwise we gonna have a dropped packet.
            Sleep(15);

            pkt->subcmd_21_23_04.no_of_reg = 0x06; // Number of registers to write. Max 9.

            pkt->subcmd_21_23_04.reg1_addr = 0x2d00; // R: 0x002d - Flip image - 0: Normal, 1: Vertically, 2: Horizontally, 3: Both 
            pkt->subcmd_21_23_04.reg1_val  = ir_cfg.ir_flip;
            pkt->subcmd_21_23_04.reg2_addr = 0x6701; // R: 0x0167 - Enable De-noise smoothing algorithms - 0: Disable, 1: Enable.
            pkt->subcmd_21_23_04.reg2_val  = (u8)((ir_cfg.ir_denoise >> 16) & 0xFF);
            pkt->subcmd_21_23_04.reg3_addr = 0x6801; // R: 0x0168 - Edge smoothing threshold - Max 0xFF, Default 0x23
            pkt->subcmd_21_23_04.reg3_val  = (u8)((ir_cfg.ir_denoise >> 8) & 0xFF);
            pkt->subcmd_21_23_04.reg4_addr = 0x6901; // R: 0x0169 - Color Interpolation threshold - Max 0xFF, Default 0x44
            pkt->subcmd_21_23_04.reg4_val  = (u8)(ir_cfg.ir_denoise & 0xFF);
            pkt->subcmd_21_23_04.reg5_addr = 0x0400; // R: 0x0004 - LSB Buffer Update Time - Default 0x32
            if (ir_cfg.ir_res_reg == 0x69)
                pkt->subcmd_21_23_04.reg5_val = 0x2d; // A value of <= 0x2d is fast enough for 30 x 40, so the first fragment has the updated frame.  
            else
                pkt->subcmd_21_23_04.reg5_val = 0x32; // All the other resolutions the default is enough. Otherwise a lower value can break hand analysis.
            pkt->subcmd_21_23_04.reg6_addr = 0x0700; // R: 0x0007 - Finalize config - Without this, the register changes do not have any effect.
            pkt->subcmd_21_23_04.reg6_val  = 0x01;

            buf[48] = mcu_crc8_calc(buf + 12, 36);
            res = hid_write(handle, buf, 49);

            // get_ir_registers(0,4); // Get all register pages
            // get_ir_registers((ir_cfg.ir_custom_register >> 8) & 0xFF, (ir_cfg.ir_custom_register >> 8) & 0xFF); // Get all registers based on changed register's page

            return res;
        }


        public static int nfc_tag_info() {
            /////////////////////////////////////////////////////
            // Kudos to Eric Betts (https://github.com/bettse) //
            // for nfc comm starters                           //
            /////////////////////////////////////////////////////
            int res;
            u8* buf = stackalloc u8[0x170];
            u8* buf2 = stackalloc u8[0x170];
            const int output_buffer_length = 49;
            int error_reading = 0;
            int res_get = 0;
            u8* tag_uid_buf = stackalloc u8[10];
            u8  tag_uid_size = 0;
            u8* ntag_buffer = stackalloc u8[924];
            u16 ntag_buffer_pos = 0;
            u8  ntag_pages = 0; // Max 231
            u8  tag_type = 0;
            u16 payload_size = 0;
            bool ntag_init_done = false;

            memset(tag_uid_buf, 0, 10);
            memset(ntag_buffer, 0, 924);

            // Set input report to x31
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x31;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (*(u16*)&buf[0xD] == 0x0380)
                        goto step1;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 1;
                    goto step9;
                }
            }

        step1:
            // Enable MCU
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x22;
                pkt->subcmd_arg.arg1 = 0x1;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (*(u16*)&buf[0xD] == 0x2280)
                        goto step2;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 2;
                    goto step9;
                }
            }

        step2:
            // Request MCU mode status
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x01;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x31) {
                        //if (buf[49] == 0x01 && buf[56] == 0x06) // MCU state is Initializing
                        // *(u16*)buf[52]LE x04 in lower than 3.89fw, x05 in 3.89
                        // *(u16*)buf[54]LE x12 in lower than 3.89fw, x18 in 3.89
                        // buf[56]: mcu mode state
                        if (buf[49] == 0x01 && buf[56] == 0x01) // Mcu mode is Standby
                            goto step3;
                    }
                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 3;
                    goto step9;
                }
            }

        step3:
            // Set MCU mode
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x01;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x21;

                pkt->subcmd_21_21.mcu_cmd = 0x21; // Set MCU mode cmd
                pkt->subcmd_21_21.mcu_subcmd = 0x00; // Set MCU mode cmd
                pkt->subcmd_21_21.mcu_mode = 0x04; // MCU mode - 1: Standby, 4: NFC, 5: IR, 6: Initializing/FW Update?

                buf[48] = mcu_crc8_calc(buf + 12, 36);
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x21) {
                        // *(u16*)buf[18]LE x04 in lower than 3.89fw, x05 in 3.89
                        // *(u16*)buf[20]LE x12 in lower than 3.89fw, x18 in 3.89
                        if (buf[15] == 0x01 && buf[22] == 0x01) // Mcu mode is standby
                            goto step4;
                    }
                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 4;
                    goto step9;
                }
            }

        step4:
            // Request MCU mode status
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x01;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x31) {
                        // *(u16*)buf[52]LE x04 in lower than 3.89fw, x05 in 3.89
                        // *(u16*)buf[54]LE x12 in lower than 3.89fw, x18 in 3.89
                        if (buf[49] == 0x01 && buf[56] == 0x04) // Mcu mode is NFC
                            goto step5;
                    }
                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    res_get = 5;
                    goto step9;
                }
            }

        step5:
            // Request NFC mode status
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;

                pkt->subcmd = 0x02;
                pkt->subcmd_arg.arg1 = 0x04; // 0: Cancel all, 4: StartWaitingReceive
                pkt->subcmd_arg.arg2 = 0x00; // Count of the currecnt packet if the cmd is a series of packets.
                buf[13] = 0x00;
                buf[14] = 0x08; // 8: Last cmd packet, 0: More cmd packet should be  expected
                buf[15] = 0x00; // Length of data after cmd header

                buf[47] = mcu_crc8_calc(buf + 11, 36); //Without the last byte
                res = hid_write(handle, buf, output_buffer_length - 1);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x31) {
                        if (buf[49] == 0x2a && *(u16*)&buf[50] == 0x0500 && buf[55] == 0x31 && buf[56] == 0x0b)// buf[56] == 0x0b: Initializing/Busy
                            break;
                        if (buf[49] == 0x2a && *(u16*)&buf[50] == 0x0500 && buf[55] == 0x31 && buf[56] == 0x00) // buf[56] == 0x00: Awaiting cmd
                            goto step6;
                    }
                    retries++;
                    if (retries > 4 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 9) {
                    res_get = 6;
                    goto step9;
                }
            }

        step6:
            // Request NFC mode status
            error_reading = 0;
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;

                pkt->subcmd = 0x02;
                pkt->subcmd_arg.arg1 = 0x01; // 1: Start polling, 2: Stop polling, 
                pkt->subcmd_arg.arg2 = 0x00; // Count of the currecnt packet if the cmd is a series of packets.
                buf[13] = 0x00;
                buf[14] = 0x08; // 8: Last cmd packet, 0: More cmd packet should be expected
                buf[15] = 0x05; // Length of data after cmd header
                buf[16] = 0x01; // 1: Enable Mifare support
                buf[17] = 0x00; // Unknown.
                buf[18] = 0x00; // Unknown.
                buf[19] = 0x2c; // Unknown. Some values work (0x07) other don't.
                buf[20] = 0x01; // Unknown. This is not needed but Switch sends it.

                buf[47] = mcu_crc8_calc(buf + 11, 36); //Without the last byte
                res = hid_write(handle, buf, output_buffer_length - 1);
                int retries = 0;
                while (true) {
                    if (!enable_NFCScanning)
                        goto step7;
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (buf[0] == 0x31) {
                        // buf[49] == 0x2a: NFC MCU input report
                        // buf[50] shows when there's error?
                        // buf[51] == 0x05: NFC
                        // buf[54] always 9?
                        // buf[55] always x31?
                        // buf[56]: MCU/NFC state
                        // buf[62]: nfc tag IC
                        // buf[63]: nfc tag Type
                        // buf[64]: size of following data and it's the last NFC header byte
                        if (buf[49] == 0x2a && *(u16*)&buf[50] == 0x0500 && buf[56] == 0x09) { // buf[56] == 0x09: Tag detected
                            tag_uid_size = buf[64];
                            FormJoy.myform1.txtBox_nfcUid.Text = "UID:  ";
                            for (int i = 0; i < tag_uid_size; i++) {
                                if (i < tag_uid_size - 1) {
                                    tag_type = buf[62]; // Save tag type
                                    tag_uid_buf[i] = buf[65 + i]; // Save UID
                                    FormJoy.myform1.txtBox_nfcUid.Text += String.Format("{0:X2}:", buf[65 + i]);
                                }
                                else {
                                    FormJoy.myform1.txtBox_nfcUid.Text += String.Format("{0:X2}", buf[65 + i]);
                                }
                            }
                            FormJoy.myform1.txtBox_nfcUid.Text += String.Format("\r\nType: {0:s}", buf[62] == 0x2 ? "NTAG" : "MIFARE");
                            Application.DoEvents();
                            goto step7;
                        }
                        else if (buf[49] == 0x2a)
                            break;
                    }
                    retries++;
                    if (retries > 4 || res == 0) {
                        Application.DoEvents();
                        break;
                    }
                }
                error_reading++;
                if (error_reading > 100) {
                    res_get = 7;
                    if (ntag_init_done)
                        FormJoy.myform1.txtBox_NFCTag.Text = String.Format("Tag lost!");
                    else
                        FormJoy.myform1.txtBox_NFCTag.Text = String.Format("No Tag detected!");
                    goto step9;
                }
            }

        step7:
            // Read NTAG contents
            error_reading = 0;
            while (true) {
                memset(buf2, 0, 368);
                var hdr = (brcm_hdr *)buf2;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 0x11;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;

                pkt->subcmd = 0x02;
                pkt->subcmd_arg.arg1 = 0x06; // 6: Read Ntag data, 0xf: Read mifare data
                buf2[12] = 0x00;
                buf2[13] = 0x00;
                buf2[14] = 0x08;
                buf2[15] = 0x13; // Length of data after cmd header

                buf2[16] = 0xd0; // Unknown
                buf2[17] = 0x07; // Unknown or UID lentgh?
                buf2[18] = 0x00; // Only for Mifare cmds or should have a UID?

                buf2[19] = 0x00; //should have a UID?
                buf2[20] = 0x00; //should have a UID?
                buf2[21] = 0x00; //should have a UID?
                buf2[22] = 0x00; //should have a UID?
                buf2[23] = 0x00; //should have a UID?
                buf2[24] = 0x00; //should have a UID?

                buf2[25] = 0x00; // 1: Ntag215 only. 0: All tags, otherwise error x48 (Invalid format error)

                // https://www.tagnfc.com/en/info/11-nfc-tags-specs

                // If the following is selected wrongly, error x3e (Read error)
                switch (ntag_pages) {
                    case 0:
                        buf2[26] = 0x01;
                        break;
                        // Ntag213
                    case 45:
                        // The following 7 bytes should be decided with max the current ntag pages and what we want to read.
                        buf2[26] = 0x01; // How many blocks to read. Each block should be <= 60 pages (240 bytes)? Min/Max values are 1/4, otherwise error x40 (Argument error)

                        buf2[27] = 0x00; // Block 1 starting page
                        buf2[28] = 0x2C; // Block 1 ending page
                        buf2[29] = 0x00; // Block 2 starting page
                        buf2[30] = 0x00; // Block 2 ending page
                        buf2[31] = 0x00; // Block 3 starting page
                        buf2[32] = 0x00; // Block 3 ending page
                        buf2[33] = 0x00; // Block 4 starting page
                        buf2[34] = 0x00; // Block 4 ending page
                        break;
                        // Ntag215
                    case 135:
                        // The following 7 bytes should be decided with max the current ntag pages and what we want to read.
                        buf2[26] = 0x03; // How many page ranges to read. Each range should be <= 60 pages (240 bytes)? Max value is 4.

                        buf2[27] = 0x00; // Block 1 starting page
                        buf2[28] = 0x3b; // Block 1 ending page
                        buf2[29] = 0x3c; // Block 2 starting page
                        buf2[30] = 0x77; // Block 2 ending page
                        buf2[31] = 0x78; // Block 3 starting page
                        buf2[32] = 0x86; // Block 3 ending page
                        buf2[33] = 0x00; // Block 4 starting page
                        buf2[34] = 0x00; // Block 4 ending page
                        break;
                    case 231:
                        // The following 7 bytes should be decided with max the current ntag pages and what we want to read.
                        buf2[26] = 0x04; // How many page ranges to read. Each range should be <= 60 pages (240 bytes)? Max value is 4.

                        buf2[27] = 0x00; // Block 1 starting page
                        buf2[28] = 0x3b; // Block 1 ending page
                        buf2[29] = 0x3c; // Block 2 starting page
                        buf2[30] = 0x77; // Block 2 ending page
                        buf2[31] = 0x78; // Block 3 starting page
                        buf2[32] = 0xB3; // Block 3 ending page
                        buf2[33] = 0xB4; // Block 4 starting page
                        buf2[34] = 0xE6; // Block 4 ending page
                        break;
                    default:
                        break;
                }

                buf2[47] = mcu_crc8_calc(buf2 + 11, 36);

                res = hid_write(handle, buf2, output_buffer_length - 1);

                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf2, 368, 64);
                    if (buf2[0] == 0x31) {
                        if ((buf2[49] == 0x3a || buf2[49] == 0x2a) && buf2[56] == 0x07) {
                            FormJoy.myform1.txtBox_NFCTag.Text = String.Format("Error {0:X2}!", buf2[50]);
                            goto step9;///////////
                        }
                        else if (buf2[49] == 0x3a && buf2[51] == 0x07) {
                            if (ntag_init_done) {
                                payload_size = (u16)((buf2[54] << 8 | buf2[55]) & 0x7FF);
                                // The lengths come from the controller. C++ trusted them; bound
                                // them so a malformed reply can't write past the buffers.
                                if (buf2[52] == 0x01) {
                                    int len = Math.Min(Math.Min(payload_size - 60, 368 - 116), 924 - ntag_buffer_pos);
                                    if (len > 0) {
                                        memcpy(ntag_buffer + ntag_buffer_pos, buf2 + 116, len);
                                        ntag_buffer_pos += (u16)len;
                                    }
                                }
                                else {
                                    int len = Math.Min(Math.Min((int)payload_size, 368 - 56), 924 - ntag_buffer_pos);
                                    if (len > 0)
                                        memcpy(ntag_buffer + ntag_buffer_pos, buf2 + 56, len);
                                }
                            }
                            else if (buf2[52] == 0x01) {
                                if (tag_type == 2) {
                                    switch (buf2[74]) {
                                        case 0:
                                            ntag_pages = 135;
                                            break;
                                        case 3:
                                            ntag_pages = 45;
                                            break;
                                        case 4:
                                            ntag_pages = 231;
                                            break;
                                        default:
                                            goto step9;///////////
                                            break;
                                    }
                                }
                            }
                            break;
                        }
                        else if (buf2[49] == 0x2a && buf2[56] == 0x04) { // finished
                            if (ntag_init_done) {
                                FormJoy.myform1.show_ntag_contents(ntag_buffer, ntag_pages);
                                Application.DoEvents();
                                goto step9;///////////
                            }
                            ntag_init_done = true;

                            memset(buf, 0, 368);
                            var hdr2 = (brcm_hdr *)buf;
                            var pkt2 = (brcm_cmd_01 *)(hdr2 + 1);
                            hdr2->cmd = 0x11;
                            hdr2->timer = (u8)(timming_byte & 0xF);
                            timming_byte++;

                            pkt2->subcmd = 0x02;
                            pkt2->subcmd_arg.arg1 = 0x02; // 0: Cancel all, 4: StartWaitingReceive
                            pkt2->subcmd_arg.arg2 = 0x00; // Count of the currecnt packet if the cmd is a series of packets.
                            buf[13] = 0x00;
                            buf[14] = 0x08; // 8: Last cmd packet, 0: More cmd packet should be  expected
                            buf[15] = 0x00; // Length of data after cmd header

                            buf[47] = mcu_crc8_calc(buf + 11, 36); //Without the last byte
                            res = hid_write(handle, buf, output_buffer_length - 1);
                            Sleep(200);
                            goto step5;
                        }
                        else if (buf2[49] == 0x2a)
                            break;
                    }
                    retries++;
                    if (retries > 4 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 9) {
                    res_get = 8;
                    if (buf[62] == 0x4)
                        FormJoy.myform1.txtBox_NFCTag.Text = String.Format("Mifare reading is not supported for now..");
                    goto step9;
                }
            }

        step9:
            // Disable MCU
            memset(buf, 0, 368);
            var hdr0 = (brcm_hdr *)buf;
            var pkt0 = (brcm_cmd_01 *)(hdr0 + 1);
            hdr0->cmd = 1;
            hdr0->timer = (u8)(timming_byte & 0xF);
            timming_byte++;
            pkt0->subcmd = 0x22;
            pkt0->subcmd_arg.arg1 = 0x00;
            res = hid_write(handle, buf, output_buffer_length);
            res = hid_read_timeout(handle, buf, 368, 64);


            // Set input report to x3f
            while (true) {
                memset(buf, 0, 368);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x3f;
                res = hid_write(handle, buf, output_buffer_length);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 368, 64);
                    if (*(u16*)&buf[0xD] == 0x0380)
                        goto stepf;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 7) {
                    goto stepf;
                }
            }
        stepf:
            if (res_get > 0)
                return res_get;

            return 0;
        }


        public static int silence_input_report() {
            int res;
            u8* buf = stackalloc u8[49];
            int error_reading = 0;

            while (true) {
                memset(buf, 0, 49);
                var hdr = (brcm_hdr *)buf;
                var pkt = (brcm_cmd_01 *)(hdr + 1);
                hdr->cmd = 1;
                hdr->timer = (u8)(timming_byte & 0xF);
                timming_byte++;
                pkt->subcmd = 0x03;
                pkt->subcmd_arg.arg1 = 0x3f;
                res = hid_write(handle, buf, 49);
                int retries = 0;
                while (true) {
                    res = hid_read_timeout(handle, buf, 49, 64);
                    if (*(u16*)&buf[0xD] == 0x0380)
                        goto stepf;

                    retries++;
                    if (retries > 8 || res == 0)
                        break;
                }
                error_reading++;
                if (error_reading > 4)
                    break;
            }

        stepf:
            return 0;
        }


    }
}

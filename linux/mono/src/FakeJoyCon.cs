// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// A software Joy-Con/Pro Controller for testing without hardware (--selftest, --demo).
// It answers the subcommands jctool uses the way a real controller does
// (see dekuNukem/Nintendo_Switch_Reverse_Engineering), backed by a 512KB SPI
// flash image in memory. The IR camera is emulated in image transfer mode
// (a test pattern); NFC is not emulated.

using System;
using System.Collections.Generic;
using System.Threading;

using u8 = System.Byte;

namespace CppWinFormJoy
{
    public unsafe class FakeJoyCon
    {
        public static readonly IntPtr Handle = new IntPtr(0x7FFF0001);

        public readonly int type;       // 1: Joy-Con (L), 2: Joy-Con (R), 3: Pro Controller
        public readonly int product_id;
        public readonly byte[] spi = new byte[0x80000];
        public readonly byte[] mac = { 0x98, 0xB6, 0xE9, 0x12, 0x34, 0x56 };
        public int writes;              // number of output reports received
        public int spi_writes;          // number of SPI write subcommands received
        public int last_write_length;

        readonly Queue<byte[]> replies = new Queue<byte[]>();
        byte input_mode = 0x3F;
        bool imu_on;
        byte timer;
        int tick;
        byte mcu_state;     // 0: off, 1: standby, 5: IR
        byte ir_mode;
        byte ir_max_frag;
        public int ir_frames_sent;
        public int closes;
        public int ir_register_writes;
        public int ir_register_write_thread;
        public int ir_fragment_delay_ms; // > 0: pace IR fragments like real hardware

        public FakeJoyCon(int type)
        {
            this.type = type;
            product_id = new[] { 0, 0x2006, 0x2007, 0x2009 }[type];
            for (int i = 0; i < spi.Length; i++)
                spi[i] = 0xFF;

            // Values jctool's SPI backup validation expects at fixed places
            Put(0x0000, 0x01, 0x08, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x08, 0xC0, 0x5D, 0x89, 0xFD, 0x04, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x40, 0x06);
            for (int i = 0; i < 6; i++)
                spi[0x1A - i] = mac[i];   // stored reversed
            Put(0x10000, 0x0A, 0xFB, 0x00, 0x00, 0x02, 0x0D);

            // Factory configuration
            Put(0x6000, 0x00, 0x00);
            Put(0x6002, System.Text.Encoding.ASCII.GetBytes(type == 3 ? "\0\0\0\0\0\0\0\0\0\0\0\0\0\0" : "XAW70012345678"));
            Put(0x6012, (byte)type, 0xA0);
            Put(0x6020, 0xD3, 0xFF, 0xD5, 0xFF, 0x55, 0x01, 0x00, 0x40, 0x00, 0x40, 0x00, 0x40,
                        0x19, 0x00, 0xDD, 0xFF, 0xDC, 0xFF, 0x3B, 0x34, 0x3B, 0x34, 0x3B, 0x34);
            Put(0x603D, 0xBA, 0xF5, 0x62, 0x6F, 0xC8, 0x77, 0xED, 0x95, 0x5B,
                        0x16, 0xD8, 0x7D, 0xF2, 0xB5, 0x5F, 0x86, 0x65, 0x5E);
            if (type == 3)
                Put(0x6050, 0x32, 0x32, 0x32, 0xFF, 0xFF, 0xFF, 0x32, 0x32, 0x32, 0x32, 0x32, 0x32);
            else if (type == 2)
                Put(0x6050, 0xFF, 0x3C, 0x28, 0x1E, 0x0A, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
            else
                Put(0x6050, 0x0A, 0xB9, 0xE6, 0x00, 0x1E, 0x1E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
            Put(0x6080, 0x50, 0xFD, 0x00, 0x00, 0xC6, 0x0F);
            Put(0x6086, 0x0F, 0x30, 0x61, 0x96, 0x30, 0xF3, 0xD4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xC7, 0x79, 0x9C, 0x33, 0x36, 0x63);
            Put(0x6098, 0x0F, 0x30, 0x61, 0x96, 0x30, 0xF3, 0xD4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xC7, 0x79, 0x9C, 0x33, 0x36, 0x63);
        }

        void Put(int offset, params byte[] data)
        {
            Array.Copy(data, 0, spi, offset, data.Length);
        }

        byte[] NewReport(byte id, int length)
        {
            var r = new byte[length];
            r[0] = id;
            r[1] = timer++;
            r[2] = 0x8E;            // battery full, Bluetooth
            // Sticks at their calibrated centers
            r[6] = 0x6F; r[7] = 0x8C; r[8] = 0x77;
            r[9] = 0xF2; r[10] = 0xD5; r[11] = 0x7D;
            return r;
        }

        void Ack(byte subcmd, byte ack, params byte[] data)
        {
            var r = NewReport(0x21, 49);
            r[13] = ack;
            r[14] = subcmd;
            Array.Copy(data, 0, r, 15, Math.Min(data.Length, 49 - 15));
            replies.Enqueue(r);
        }

        public int Write(u8* data, int length)
        {
            writes++;
            last_write_length = length;
            byte cmd = data[0];
            if (cmd == 0x11)
                return McuWrite(data, length);
            if (cmd != 0x01)
                return length;   // 0x10 rumble only: no reply

            byte subcmd = data[10];
            switch (subcmd) {
                case 0x02: // Device info
                    Ack(0x02, 0x82, 0x03, 0x89, (byte)type, 0x02, mac[0], mac[1], mac[2], mac[3], mac[4], mac[5], 0x01, 0x01);
                    break;
                case 0x03: // Set input report mode
                    input_mode = data[11];
                    Ack(0x03, 0x80);
                    break;
                case 0x10: { // SPI read: echo offset/size, then the data
                    uint offset = *(uint*)&data[11];
                    byte size = data[15];
                    var reply = new byte[5 + size];
                    Array.Copy(BitConverter.GetBytes(offset), reply, 4);
                    reply[4] = size;
                    if (offset + size <= spi.Length)
                        Array.Copy(spi, offset, reply, 5, size);
                    Ack(0x10, 0x90, reply);
                    break;
                }
                case 0x11: { // SPI write
                    uint offset = *(uint*)&data[11];
                    byte size = data[15];
                    spi_writes++;
                    if (offset + size <= spi.Length)
                        for (int i = 0; i < size; i++)
                            spi[offset + i] = data[16 + i];
                    Ack(0x11, 0x80, 0x00);
                    break;
                }
                case 0x40: // Enable/disable IMU
                    imu_on = data[11] != 0;
                    Ack(0x40, 0x80);
                    break;
                case 0x43: // Read IMU register(s)
                    if (data[11] == 0x10)
                        Ack(0x43, 0xC0, data[11], data[12], (byte)(imu_on ? 0x30 : 0x00));
                    else
                        Ack(0x43, 0xC0, data[11], data[12], 0x60, 0x00);   // 31.0 C
                    break;
                case 0x21: // MCU config
                    McuConfig(data);
                    break;
                case 0x22: // MCU on/off
                    mcu_state = (byte)(data[11] != 0 ? 1 : 0);
                    Ack(0x22, 0x80);
                    break;
                case 0x50: // Regulated voltage
                    Ack(0x50, 0xD0, 0x10, 0x06);   // 0x610: 3.88V
                    break;
                default:
                    Ack(subcmd, 0x80);
                    break;
            }
            return length;
        }

        byte[] McuReport(byte report_type)
        {
            var r = NewReport(0x31, 362);
            r[49] = report_type;
            return r;
        }

        void McuConfig(u8* data)
        {
            var r = NewReport(0x21, 49);
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
                r[15] = 0x0b;
            }
            else if (data[11] == 0x23 && data[12] == 0x04) {   // Write IR registers
                ir_register_writes++;
                ir_register_write_thread = Thread.CurrentThread.ManagedThreadId;
                r[15] = 0x13;
                r[16] = 0x00;
                r[17] = (byte)(ir_mode == 0x04 ? 0x02 : ir_mode);
            }
            replies.Enqueue(r);
        }

        int McuWrite(u8* data, int length)
        {
            if (data[10] == 0x01) {                 // MCU status
                var r = McuReport(0x01);
                r[56] = mcu_state;
                replies.Enqueue(r);
            }
            else if (data[10] == 0x03 && data[11] == 0x00 && mcu_state == 5 && ir_mode != 0) {
                // IR fragment ACK: send the next fragment of a test pattern (diagonal gradient)
                int frag = data[12] == 0x01 ? data[13] : (ir_frames_sent == 0 ? 0 : (data[14] + 1) % (ir_max_frag + 1));
                var r = McuReport(0x03);
                r[50] = 0x00;
                r[51] = ir_mode;
                r[52] = (byte)frag;
                r[53] = 0x40;                        // average intensity
                if (ir_mode == 0x07)
                    for (int i = 0; i < 300; i++)
                        r[59 + i] = (byte)(((frag * 300 + i) * 255 / ((ir_max_frag + 1) * 300)) ^ ((i % 20) < 2 ? 0xFF : 0));
                if (ir_fragment_delay_ms > 0)
                    Thread.Sleep(ir_fragment_delay_ms);
                replies.Enqueue(r);
                ir_frames_sent++;
            }
            return length;
        }

        public int Read(u8* data, int length, int milliseconds)
        {
            byte[] r = null;
            if (replies.Count > 0) {
                r = replies.Dequeue();
            }
            else if (input_mode == 0x30) {
                Thread.Sleep(15);
                r = NewReport(0x30, 49);
                r[3] = (byte)((tick++ / 20) % 2 == 0 ? 0x08 : 0x00);   // blink the A button
                for (int i = 13; i < 49; i++)
                    r[i] = (byte)(i * 7 + tick);
            }
            else {
                if (milliseconds > 0)
                    Thread.Sleep(Math.Min(milliseconds, 5));
                return 0;
            }
            int n = Math.Min(length, r.Length);
            for (int i = 0; i < n; i++)
                data[i] = r[i];
            return n;
        }
    }
}

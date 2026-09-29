// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Port of jctool/FormJoy.h: constructor, class variables and all event handlers.
// The control declarations and InitializeComponent() are in FormJoy.Designer.cs.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows.Forms;
using static CppWinFormJoy.Jc;

using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using s16 = System.Int16;

namespace CppWinFormJoy
{
public unsafe partial class FormJoy : System.Windows.Forms.Form
{
    public static FormJoy myform1;

    private int handler_close;
    private int option_is_on;
    private bool allow_full_restore;
    private bool disable_expert_mode;
    private bool temp_celsius;
    private byte[] backup_spi;
    public  byte[] vib_loaded_file;
    public  byte[] vib_file_converted;
    private u16 vib_sample_rate;
    private u32 vib_samples;
    private u32 vib_loop_start;
    private u32 vib_loop_end;
    private u32 vib_loop_wait;
    private int vib_converted;
    //file type: 1 = Raw, 2 = bnvib (0x4), 3 = bnvib loop (0xC), 4 = bnvib loop (0x10)
    private int vib_file_type;
    private float lf_gain;
    private float lf_pitch;
    private float hf_gain;
    private float hf_pitch;
    private Color jcBodyColor;
    private Color jcButtonsColor;
    private Color jcGripLeftColor;
    private Color jcGripRightColor;
    private int ir_image_width;
    private int ir_image_height;
    private jcColor.JoyConColorPicker JCColorPicker;

    private System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(images));

    // Read-only views used by --selftest
    internal string DevText  { get { return this.textBoxDev.Text; } }
    internal string MacText  { get { return this.textBoxMAC.Text; } }
    internal string FwText   { get { return this.textBoxFW.Text; } }
    internal string SnText   { get { return this.textBoxSN.Text; } }
    internal string BodyText { get { return this.lbl_Body_hex_txt.Text; } }
    internal string RefreshBattery() { update_battery(); return this.toolStripLabel_batt.Text; }
    internal Image PreviewImage { get { return this.pictureBoxPreview.Image; } }
    internal void RefreshPreview() { update_colors_from_spi(false); }
    internal int CaptureIR() { enable_IRVideoPhoto = false; return prepareSendIRConfig(true); }
    internal void SelectIRResolution60p(bool on) { this.radioBtn_IR60p.Checked = on; this.radioBtn_IR240p.Checked = !on; }
    internal decimal IRExposure { get { return this.numeric_IRExposure.Value; } set { this.numeric_IRExposure.Value = value; } }
    internal CheckBox IRQuickCaptureOption { get { return this.chkBox_IRQuickCapture; } }
    private CheckBox chkBox_IRQuickCapture;
    internal void ClickIRStream() { btn_getVideo_Click(null, EventArgs.Empty); }
    internal void ClickIRConfigLive() { btn_IRConfigLive_Click(null, EventArgs.Empty); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && timer_link != null)
            timer_link.Dispose();
        if (disposing && components != null)
            components.Dispose();
        base.Dispose(disposing);
    }

    public FormJoy()
    {
        handler_close   = 0;
        option_is_on    = 0;
        vib_file_type   = 0;
        vib_sample_rate = 0;
        vib_samples     = 0;
        vib_loop_start  = 0;
        vib_loop_end    = 0;
        vib_loop_wait   = 0;
        disable_expert_mode = true;
        temp_celsius        = true;

        silence_input_report();
        set_led_busy();

        InitializeComponent();
        Fonts.Apply(this);
        this.ControlAdded += (sender, e) => Fonts.RewrapTextBoxes(e.Control);

        // Set static form, to allow calling functions from unmanaged code.
        myform1 = this;

        //Initialise locations on start for easy designing
        this.grpBox_DebugCmd.Location         = new System.Drawing.Point(494, 36);
        this.grpBox_Restore.Location          = new System.Drawing.Point(494, 36);
        this.grpBox_ChangeSN.Location         = new System.Drawing.Point(494, 36);
        this.grpBox_VibPlayer.Location        = new System.Drawing.Point(494, 36);
        this.grpBox_ButtonTest.Location       = new System.Drawing.Point(494, 36);
        this.grpBox_nfc.Location              = new System.Drawing.Point(724, 36);
        this.grpBox_IR.Location               = new System.Drawing.Point(521, 36);
        this.grpBox_IRSettings.Location       = new System.Drawing.Point(14, 120);
        this.grpBox_editCalModel.Location     = new System.Drawing.Point(14, 120);

        // Get controller info
        full_refresh(false);
        
        // Set properties that otherwise get removed by Designer in InitializeComponent()
        this.comboBox_rstOption.Items.Add("Restore Color");
        this.comboBox_rstOption.Items.Add("Restore S/N");
        this.comboBox_rstOption.Items.Add("Restore User Calibration");
        this.comboBox_rstOption.Items.Add("Factory Reset User Calibration");
        this.comboBox_rstOption.Items.Add("Full Restore");
        this.comboBox_rstOption.DrawItem +=
            new System.Windows.Forms.DrawItemEventHandler(this.comboBox_darkTheme_DrawItem);
            
        this.menuStrip1.Renderer =
            new System.Windows.Forms.ToolStripProfessionalRenderer(new Overrides.TestColorTable());
        this.toolStrip1.Renderer = new Overrides.OverrideTSSR();

        this.textBoxDbg_cmd.Validating         += new CancelEventHandler(this.textBoxDbg_Validating);
        this.textBoxDbg_cmd.Validated          += new EventHandler(this.textBoxDbg_Validated);
        this.textBoxDbg_subcmd.Validating      += new CancelEventHandler(this.textBoxDbg_subcmd_Validating);
        this.textBoxDbg_subcmd.Validated       += new EventHandler(this.textBoxDbg_subcmd_Validated);
        this.textBoxDbg_SubcmdArg.Validating   += new CancelEventHandler(this.textBoxDbg_SubcmdArg_Validating);
        this.textBoxDbg_SubcmdArg.Validated    += new EventHandler(this.textBoxDbg_SubcmdArg_Validated);
        this.textBoxDbg_lfamp.Validating       += new CancelEventHandler(this.textBoxDbg_Validating);
        this.textBoxDbg_lfamp.Validated        += new EventHandler(this.textBoxDbg_Validated);
        this.textBoxDbg_lfreq.Validating       += new CancelEventHandler(this.textBoxDbg_Validating);
        this.textBoxDbg_lfreq.Validated        += new EventHandler(this.textBoxDbg_Validated);
        this.textBoxDbg_hamp.Validating        += new CancelEventHandler(this.textBoxDbg_Validating);
        this.textBoxDbg_hamp.Validated         += new EventHandler(this.textBoxDbg_Validated);
        this.textBoxDbg_hfreq.Validating       += new CancelEventHandler(this.textBoxDbg_Validating);
        this.textBoxDbg_hfreq.Validated        += new EventHandler(this.textBoxDbg_Validated);
        this.textBox_chg_sn.Validating         += new CancelEventHandler(this.textBox_chg_sn_Validating);
        this.textBox_chg_sn.Validated          += new EventHandler(this.textBox_chg_sn_Validated);
        this.textBox_vib_loop_times.Validating += new CancelEventHandler(this.textBox_loop_Validating);
        this.textBox_vib_loop_times.Validated  += new EventHandler(this.textBox_loop_Validated);

        this.chkBox_IRFlashlight.CheckedChanged   += new EventHandler(this.IRFlashlight_checkedChanged);
        this.chkBox_IRBrightLeds.CheckedChanged   += new EventHandler(this.IRLeds_checkedChanged);
        this.chkBox_IRDimLeds.CheckedChanged      += new EventHandler(this.IRLeds_checkedChanged);
        this.chkBox_IRDenoise.CheckedChanged      += new EventHandler(this.IRDenoise_checkedChanged);
        this.chkBox_IRAutoExposure.CheckedChanged += new EventHandler(this.IRAutoExposure_checkedChanged);

        // Linux: "Quick capture" option, under Auto Exposure (whose box was taller than its text).
        this.chkBox_IRAutoExposure.Height = 36;
        this.chkBox_IRQuickCapture = new CheckBox();
        this.chkBox_IRQuickCapture.CheckAlign = System.Drawing.ContentAlignment.MiddleRight;
        this.chkBox_IRQuickCapture.Font       = this.chkBox_IRAutoExposure.Font;
        this.chkBox_IRQuickCapture.ForeColor  = this.chkBox_IRAutoExposure.ForeColor;
        this.chkBox_IRQuickCapture.Location   = new System.Drawing.Point(109, 199);
        this.chkBox_IRQuickCapture.Margin     = new System.Windows.Forms.Padding(0);
        this.chkBox_IRQuickCapture.Name       = "chkBox_IRQuickCapture";
        this.chkBox_IRQuickCapture.Size       = new System.Drawing.Size(115, 21);
        this.chkBox_IRQuickCapture.Text       = "Quick capture";
        this.chkBox_IRQuickCapture.CheckedChanged += (sender, e) => { ir_quick_capture = this.chkBox_IRQuickCapture.Checked; };
        this.grpBox_IRSettings.Controls.Add(this.chkBox_IRQuickCapture);
        this.toolTip1.SetToolTip(this.chkBox_IRQuickCapture,
            "Capture: skip the auto exposure adjustment, use the Exposure value as set\n" +
            "and save the second frame. About a third faster. Off: like the original (Windows) version.");

        // Linux: link health in the status bar, left of the temperature. Updated every second
        // from what the app sent and received (it doesn't poll the controller for it).
        this.toolStripLabel_link = new ToolStripLabel();
        this.toolStripLabel_link.Alignment = ToolStripItemAlignment.Right;
        this.toolStripLabel_link.Font      = new System.Drawing.Font(this.toolStripLabel_temp.Font.FontFamily, 8.25F);
        this.toolStripLabel_link.ForeColor = System.Drawing.Color.FromArgb(251, 251, 251);
        this.toolStripLabel_link.Margin    = new System.Windows.Forms.Padding(0);
        this.toolStripLabel_link.Padding   = new System.Windows.Forms.Padding(2, 0, 6, 0);
        this.toolStripLabel_link.Overflow  = ToolStripItemOverflow.Never;
        this.toolStripLabel_link.Name      = "toolStripLabel_link";
        this.toolStripLabel_link.Text      = "Link idle";
        this.toolStripLabel_link.ToolTipText = "Link health, over the last second (Linux)";
        this.toolStrip1.Items.Add(this.toolStripLabel_link);
        // Linux: calibration in use, as a third row of the info section (the rows are moved
        // closer together to make room). Click: open the Calibration screen.
        this.label_sn.Top = this.textBoxSN.Top = this.label_fw.Top = this.textBoxFW.Top = 34;
        this.label_mac.Top = this.textBoxMAC.Top = this.label_dev.Top = this.textBoxDev.Top = 64;
        this.label_cal = new Label();
        this.label_cal.Font      = this.label_sn.Font;
        this.label_cal.ForeColor = this.label_sn.ForeColor;
        this.label_cal.Location  = new System.Drawing.Point(10, 94);
        this.label_cal.Size      = new System.Drawing.Size(100, 20);
        this.label_cal.Text      = "Calibration:";
        this.lbl_calStatus = new Label();
        this.lbl_calStatus.Font      = this.textBoxSN.Font;
        this.lbl_calStatus.ForeColor = this.textBoxSN.ForeColor;
        this.lbl_calStatus.Location  = new System.Drawing.Point(110, 94);
        this.lbl_calStatus.Size      = new System.Drawing.Size(250, 20);
        this.lbl_calStatus.Text      = "";
        this.lbl_calStatus.Cursor    = Cursors.Hand;
        this.lbl_calStatus.Click    += (sender, e) => open_calibration();
        this.link_calibrate = new LinkLabel();
        this.link_calibrate.Font      = new System.Drawing.Font("Segoe UI", 9.75F);
        this.link_calibrate.LinkColor = this.link_calibrate.ActiveLinkColor = System.Drawing.Color.FromArgb(255, 188, 0);
        this.link_calibrate.Location  = new System.Drawing.Point(375, 95);
        this.link_calibrate.Size      = new System.Drawing.Size(92, 20);
        this.link_calibrate.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
        this.link_calibrate.Text      = "Calibrate..";
        this.link_calibrate.LinkClicked += (sender, e) => open_calibration();
        this.Controls.AddRange(new Control[] { this.label_cal, this.lbl_calStatus, this.link_calibrate });

        // Linux: the Calibration screen gets tabs: guided Sticks and Motion calibration, and
        // the original editor (Manual), moved into its own tab unchanged.
        this.grpBox_editCalModel.Text = "Calibration";
        var manual = new Panel();
        manual.BackColor = this.grpBox_editCalModel.BackColor;
        manual.Location  = new System.Drawing.Point(3, 56);
        manual.Size      = new System.Drawing.Size(450, 366);
        foreach (Control c in new Control[] { this.lbl_editStickDevHelp, this.btn_writeUserCal, this.btn_refreshUserCal,
                                              this.grpBox_CalUserAcc, this.grpBox_StickDevParam,
                                              this.grpBox_rightStickUCal, this.grpBox_leftStickUCal }) {
            this.grpBox_editCalModel.Controls.Remove(c);
            c.Location = new System.Drawing.Point(c.Left - 3, c.Top - 20);
            manual.Controls.Add(c);
        }
        this.stickCalPanel  = new StickCalPanel(this)  { Location = new System.Drawing.Point(3, 56) };
        this.motionCalPanel = new MotionCalPanel(this) { Location = new System.Drawing.Point(3, 56) };
        this.cal_pages = new Control[] { this.stickCalPanel, this.motionCalPanel, manual };
        string[] tab_names = { "Sticks", "Motion", "Manual" };
        this.cal_tabs = new Button[3];
        for (int i = 0; i < 3; i++) {
            int tab = i;
            var b = CalUi.NewButton(tab_names[i], 6 + i * 104, 22, 100);
            b.Click += (sender, e) => select_cal_tab(tab);
            this.cal_tabs[i] = b;
            this.grpBox_editCalModel.Controls.Add(b);
            this.grpBox_editCalModel.Controls.Add(this.cal_pages[i]);
        }
        var tab_line = new Panel { BackColor = System.Drawing.Color.FromArgb(110, 110, 110),
                                   Location = new System.Drawing.Point(6, 52), Size = new System.Drawing.Size(444, 2) };
        this.grpBox_editCalModel.Controls.Add(tab_line);
        this.grpBox_editCalModel.Size = new System.Drawing.Size(456, 428);
        this.toolTip1.SetToolTip(this.cal_tabs[0], "Guided stick calibration: center and range");
        this.toolTip1.SetToolTip(this.cal_tabs[1], "Guided motion (6-axis) calibration: gyro and accelerometer offsets");
        this.toolTip1.SetToolTip(this.cal_tabs[2], "The original editor: user calibration values and stick device parameters");
        select_cal_tab(0);

        if (check_connection_ok && handle_type != NOTHING)
            update_cal_status();
        link_take_stats();
        this.timer_link = new Timer { Interval = 1000 };
        this.timer_link.Tick += (sender, e) => update_link_health();
        this.timer_link.Start();

        this.toolTip1.SetToolTip(this.label_sn, "Click here to change your S/N");
        this.toolTip1.SetToolTip(this.textBox_vib_loop_times,
            "Set how many additional times the loop will be played.\n\nChoose a number from 0 to 999");
        this.toolTip1.SetToolTip(this.label_loop_times,
            "Set how many additional times the loop will be played.\n\nChoose a number from 0 to 999");

        // Unicode escapes
        this.chkBox_IRBrightLeds.Text = "Far/Narrow   (75\u00B0)  Leds 1/2";
        this.chkBox_IRDimLeds.Text    = "Near/Wide  (130\u00B0)  Leds 3/4";
        
        // Final form window adjustments
        this.CenterToScreen();
        this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        reset_window_option(true);

        //Done drawing!
        send_rumble();
    }

    //////////////
    // Functions
    //////////////

    private void full_refresh(bool check_connection) {
        if (check_connection) {
            if (check_if_connected())
                return;

            this.toolStripBtn_Disconnect.Enabled = true;
            this.toolStripLabel_temp.Enabled     = true;
            this.toolStripLabel_batt.Enabled     = true;
            this.toolStripBtn_batt.Enabled       = true;
        }

        this.btn_runBtnTest.Text = "Turn on";
        enable_button_test = false;

        if (handle_type != PROCON) {
            this.textBoxSN.Text = get_sn(0x6001, 0xF);
            this.textBox_chg_sn.Text = this.textBoxSN.Text;

            this.btn_changeGripsColor.Enabled = false;
        }
        else {
            this.textBoxSN.Text = "Not supported";
            this.textBox_chg_sn.Text = this.textBoxSN.Text;

            this.btn_changeGripsColor.Enabled = true;
        }

        if (handle_type != JOYCON_L) {
            if (handle_type == JOYCON_R)
                this.iRCameraToolStripMenuItem.Enabled = true;  // JC (R)
            else
                this.iRCameraToolStripMenuItem.Enabled = false; // Pro con
            this.grpBox_nfc.Enabled = true;
        }
        else {
            this.iRCameraToolStripMenuItem.Enabled = false; // JC (L)
            this.grpBox_nfc.Enabled = false;
        }

        u8* device_info = stackalloc u8[10];
        memset(device_info, 0, 10);

        get_device_info(device_info);

        this.textBoxFW.Text = String.Format("{0:X}.{1:X2}", device_info[0], device_info[1]);
        this.textBoxMAC.Text = String.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
            device_info[4], device_info[5], device_info[6], device_info[7], device_info[8], device_info[9]);

        if (handle_type == JOYCON_L)
            this.textBoxDev.Text = "Joy-Con (L)";
        else if (handle_type == JOYCON_R)
            this.textBoxDev.Text = "Joy-Con (R)";
        else if (handle_type == PROCON)
            this.textBoxDev.Text = "Pro Controller";

        update_battery();
        update_temperature();
        update_colors_from_spi(!check_connection);
        update_cal_status();
    }

    // Linux: which calibration the controller uses, in the status bar. User calibration
    // (SPI 0x8010 sticks, 0x8026 6-axis, magic B2 A1) overrides the factory one when present.
    internal void update_cal_status() {
        if (this.lbl_calStatus == null)
            return; // Called by full_refresh() before the constructor adds the row
        if (handle == IntPtr.Zero && fake == null) {
            this.lbl_calStatus.Text = "";
            return;
        }
        u8* user_cal = stackalloc u8[22];
        u8* sensor_cal = stackalloc u8[2];
        memset(user_cal, 0xFF, 22);
        memset(sensor_cal, 0xFF, 2);
        get_spi_data(0x8010, 22, user_cal);
        get_spi_data(0x8026, 2, sensor_cal);
        bool left  = handle_type != JOYCON_R && user_cal[0] == 0xB2 && user_cal[1] == 0xA1;
        bool right = handle_type != JOYCON_L && user_cal[11] == 0xB2 && user_cal[12] == 0xA1;
        bool imu   = sensor_cal[0] == 0xB2 && sensor_cal[1] == 0xA1;
        var parts = new System.Collections.Generic.List<string>();
        if (left)
            parts.Add(handle_type == PROCON ? "L stick" : "stick");
        if (right)
            parts.Add(handle_type == PROCON ? "R stick" : "stick");
        if (imu)
            parts.Add("motion");
        this.lbl_calStatus.Text = parts.Count == 0 ? "Factory" : "User (" + string.Join(", ", parts) + ")";
        string tip = "Calibration in use (a user calibration overrides the factory one):\n";
        if (handle_type != JOYCON_R)
            tip += "  " + (handle_type == PROCON ? "Left stick" : "Stick") + ": " + (left ? "user" : "factory") + "\n";
        if (handle_type != JOYCON_L)
            tip += "  " + (handle_type == PROCON ? "Right stick" : "Stick") + ": " + (right ? "user" : "factory") + "\n";
        tip += "  Motion (6-axis): " + (imu ? "user" : "factory") + "\nClick to calibrate.";
        this.toolTip1.SetToolTip(this.lbl_calStatus, tip);
    }

    internal string CalText { get { return this.lbl_calStatus.Text; } }

    private Label label_cal, lbl_calStatus;
    private LinkLabel link_calibrate;
    private StickCalPanel stickCalPanel;
    private MotionCalPanel motionCalPanel;
    private Control[] cal_pages;
    private Button[] cal_tabs;
    private int cal_tab;

    internal StickCalPanel StickCal { get { return stickCalPanel; } }
    internal MotionCalPanel MotionCal { get { return motionCalPanel; } }

    private void open_calibration() {
        if (option_is_on != 7)
            editCalibrationToolStripMenuItem_Click(null, EventArgs.Empty);
    }

    internal void select_cal_tab(int tab) {
        stickCalPanel.Stop();
        motionCalPanel.Stop();
        cal_tab = tab;
        for (int i = 0; i < cal_pages.Length; i++) {
            cal_pages[i].Visible = i == tab;
            cal_tabs[i].BackColor = i == tab ? System.Drawing.Color.FromArgb(110, 110, 110) : System.Drawing.Color.FromArgb(85, 85, 85);
            cal_tabs[i].FlatAppearance.BorderColor = cal_tabs[i].BackColor;
            cal_tabs[i].ForeColor = i == tab ? System.Drawing.Color.FromArgb(9, 255, 206) : System.Drawing.Color.FromArgb(200, 200, 200);
        }
        if (tab == 0)
            stickCalPanel.Reset();
        else if (tab == 1)
            motionCalPanel.Reset();
    }

    // For the wizards
    internal bool check_connection_lost() { return check_if_connected(); }

    internal void calibration_written() {
        update_cal_status();
        if (this.btn_writeUserCal.Enabled)
            btn_refreshUserCal_Click(null, EventArgs.Empty); // Keep the Manual tab in sync
    }

    private void btn_writeColorsToSpi_Click(System.Object sender, System.EventArgs e) {
        if (check_if_connected())
            return;

        set_led_busy();
        if (MessageBox.Show("Don't forget to make a backup first!\n" +
            "You can also find retail colors at the bottom of the colors dialog for each type.\n\n" +
            "Are you sure you want to continue?",
            "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
        {
            int error = 0;
            this.btn_writeColorsToSpi.Enabled = false;

            u8* newColors = stackalloc u8[12];
            memset(newColors, 0, 12);

            newColors[0]  = (u8)jcBodyColor.R;
            newColors[1]  = (u8)jcBodyColor.G;
            newColors[2]  = (u8)jcBodyColor.B;
            newColors[3]  = (u8)jcButtonsColor.R;
            newColors[4]  = (u8)jcButtonsColor.G;
            newColors[5]  = (u8)jcButtonsColor.B;
            newColors[6]  = (u8)jcGripLeftColor.R;
            newColors[7]  = (u8)jcGripLeftColor.G;
            newColors[8]  = (u8)jcGripLeftColor.B;
            newColors[9]  = (u8)jcGripRightColor.R;
            newColors[10] = (u8)jcGripRightColor.G;
            newColors[11] = (u8)jcGripRightColor.B;

            if (handle_type != PROCON)
                error = write_spi_data(0x6050, 6, newColors);
            else
                error = write_spi_data(0x6050, 12, newColors);

            send_rumble();

            if (error == 0) {
                MessageBox.Show("The colors were written to the device!", "Done!",
                    MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                //Check that the colors were written
                update_colors_from_spi(false);
            }
            else {
                MessageBox.Show("Failed to write the colors to the device!", "Failed!",
                    MessageBoxButtons.OK, MessageBoxIcon.Stop);
            }

            update_battery();
            update_temperature();
        }
    }

    private void btn_makeSPIBackup_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        this.menuStrip1.Enabled = false;
        this.toolStrip1.Enabled = false;

        int do_backup = 0;
        u8* device_info = stackalloc u8[10];
        memset(device_info, 0, 10);
        get_device_info(device_info);

        String filename = "spi_";
        if (handle_type == JOYCON_L)
            filename += "left_";
        else if (handle_type == JOYCON_R)
            filename += "right_";
        else if (handle_type == PROCON)
            filename += "pro_";

        filename += String.Format("{0:X2}{1:X2}{2:X2}{3:X2}{4:X2}{5:X2}",
            device_info[4], device_info[5], device_info[6], device_info[7], device_info[8], device_info[9]);
        filename += ".bin";

        String path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), filename);
        if (File.Exists(path))
        {
            if (MessageBox.Show("The file " + filename + " already exists!\n\nDo you want to overwrite it?",
                "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, 
                MessageBoxDefaultButton.Button2) ==System.Windows.Forms.DialogResult.Yes)
            {
                do_backup = 1;
            }
        }
        else {
            do_backup = 1;
        }

        if (do_backup != 0) {
            handler_close = 1;
            this.grpBox_Color.Visible = false;
            reset_window_option(true);
            Application.DoEvents();
            send_rumble();
            set_led_busy();
            cancel_spi_dump = false;

            int error = dump_spi(filename);

            this.grpBox_Color.Visible = true;
            handler_close = 0;
            if (error == 0 && !cancel_spi_dump) {
                send_rumble();
                MessageBox.Show("Done dumping SPI!", "SPI Dumping", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
            }
            else if (error != 0 && !cancel_spi_dump)
                MessageBox.Show("Failed to dump the SPI chip!", "SPI Dumping Failed!", MessageBoxButtons.OK, MessageBoxIcon.Stop);

        }
        this.menuStrip1.Enabled = true;
        this.toolStrip1.Enabled = true;
        update_battery();
        update_temperature();
    }

    // Mono's resource manager can return the same cached Bitmap on every GetObject() call,
    // and the code below recolors and draws onto these bitmaps. Work on a 32bpp ARGB copy
    // so the cached originals stay untouched (same result as on Windows).
    private Bitmap load_layer(string name) {
        // Not disposed: it may be the resource manager's cached instance.
        return new Bitmap((Bitmap)resources.GetObject(name));
    }

    private void update_joycon_color(u8 r, u8 g, u8 b, u8 rb, u8 gb, u8 bb, u8 rgl, u8 ggl, u8 bgl, u8 rgr, u8 ggr, u8 bgr) {
        Bitmap MyImage = null;
        Bitmap MyImageLayer = null;
        Bitmap MyImageLayer2 = null;

        // Apply body color 
        switch (handle_type) {
            case JOYCON_L:
                MyImage = load_layer("base64_l_joy_body");
                break;
            case JOYCON_R:
                MyImage = load_layer("base64_r_joy_body");
                break;
            case PROCON:
                MyImage = load_layer("base64_pro_body");
                MyImageLayer = load_layer("base64_pro_grips_l");
                MyImageLayer2 = load_layer("base64_pro_grips_r");
                break;
            default:
                MyImage = load_layer("base64_pro_body");
                break;
        }
        // Skip slow SetPixel(). Reduce latency pixel set latency from 842us . 260ns.
        System.Drawing.Imaging.BitmapData bmd = MyImage.LockBits(new System.Drawing.Rectangle(0, 0, MyImage.Width, MyImage.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int PixelSize = 4;
        for (int y = 0; y < MyImage.Height; y++) {
            byte* row = (byte *)bmd.Scan0.ToPointer() + (y * bmd.Stride);
            for (int x = 0; x < MyImage.Width; x++) {
                // Values are in BGRA in memory. Here in ARGB order.
                //row[x * PixelSize + 3] = alpha;
                row[x * PixelSize + 2] = r;
                row[x * PixelSize + 1] = g;
                row[x * PixelSize]     = b;
            }
        }
        MyImage.UnlockBits(bmd);

        // Apply grips color
        if (handle_type == PROCON) {
            // Skip slow SetPixel(). Reduce latency pixel set latency from 842us . 260ns.
            bmd = MyImageLayer.LockBits(new System.Drawing.Rectangle(0, 0, MyImageLayer.Width, MyImageLayer.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData bmd2 = MyImageLayer2.LockBits(new System.Drawing.Rectangle(0, 0, MyImageLayer2.Width, MyImageLayer2.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < MyImageLayer.Height; y++) {
            byte* row = (byte *)bmd.Scan0.ToPointer() + (y * bmd.Stride);
            byte* row2 = (byte *)bmd2.Scan0.ToPointer() + (y * bmd2.Stride);
            for (int x = 0; x < MyImageLayer.Width; x++) {
                    // White Buttons
                    if (Color.FromArgb(255, 255, 255) == Color.FromArgb(rb, gb, bb)) {
                        // Normal Pro
                        if (Color.FromArgb(0x32, 0x32, 0x32) == Color.FromArgb(r, g, b)) {
                            row[x * PixelSize + 2]  = 0x46;
                            row[x * PixelSize + 1]  = 0x46;
                            row[x * PixelSize]      = 0x46;
                            row2[x * PixelSize + 2] = 0x46;
                            row2[x * PixelSize + 1] = 0x46;
                            row2[x * PixelSize]     = 0x46;
                        }
                        // Xenoblade Pro
                        else if (Color.FromArgb(0x32, 0x31, 0x32) == Color.FromArgb(r, g, b)) {
                            row[x * PixelSize + 2]  = 0xdd;
                            row[x * PixelSize + 1]  = 0x3b;
                            row[x * PixelSize]      = 0x64;
                            row2[x * PixelSize + 2] = 0xdd;
                            row2[x * PixelSize + 1] = 0x3b;
                            row2[x * PixelSize]     = 0x64;
                        }
                        // Splatoon Pro
                        else if (Color.FromArgb(0x31, 0x32, 0x32) == Color.FromArgb(r, g, b)) {
                            row[x * PixelSize + 2]  = 0x1e;
                            row[x * PixelSize + 1]  = 0xdc;
                            row[x * PixelSize]      = 0x00;
                            row2[x * PixelSize + 2] = 0xff;
                            row2[x * PixelSize + 1] = 0x32;
                            row2[x * PixelSize]     = 0x78;
                        }
                        // Custom Pro. Body is not one of the 3 retail colors. Apply the Left Grip/Right Grip Colors from SPI.
                        else {
                            row[x * PixelSize + 2]  = rgl;
                            row[x * PixelSize + 1]  = ggl;
                            row[x * PixelSize]      = bgl;
                            row2[x * PixelSize + 2] = rgr;
                            row2[x * PixelSize + 1] = ggr;
                            row2[x * PixelSize]     = bgr;
                        }
                    }
                    // Custom Grips. Buttons are not white. Apply the Left Grip/Right Grip Colors from SPI.
                    else {
                        row[x * PixelSize + 2]  = rgl;
                        row[x * PixelSize + 1]  = ggl;
                        row[x * PixelSize]      = bgl;
                        row2[x * PixelSize + 2] = rgr;
                        row2[x * PixelSize + 1] = ggr;
                        row2[x * PixelSize]     = bgr;
                    }
                }
            }
            MyImageLayer.UnlockBits(bmd);
            MyImageLayer2.UnlockBits(bmd2);
            MyImageLayer = drawLayeredImage(MyImageLayer, MyImageLayer2);
        }

        // Apply buttons color 
        switch (handle_type) {
            case JOYCON_L:
                MyImageLayer = load_layer("base64_l_joy_buttons");
                break;
            case JOYCON_R:
                MyImageLayer = load_layer("base64_r_joy_buttons");
                break;
            case PROCON:
                MyImage = drawLayeredImage(MyImage, MyImageLayer); // Apply grips layer
                MyImageLayer = load_layer("base64_pro_buttons");
                break;
            default:
                MyImageLayer = load_layer("base64_pro_buttons");
                break;
        }
        // Skip slow SetPixel(). Reduce latency pixel set latency from 842us . 260ns.
        bmd = MyImageLayer.LockBits(new System.Drawing.Rectangle(0, 0, MyImageLayer.Width, MyImageLayer.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < MyImageLayer.Height; y++) {
            byte* row = (byte *)bmd.Scan0.ToPointer() + (y * bmd.Stride);
            for (int x = 0; x < MyImageLayer.Width; x++) {
                row[x * PixelSize + 2] = rb;
                row[x * PixelSize + 1] = gb;
                row[x * PixelSize]     = bb;
            }
        }
        MyImageLayer.UnlockBits(bmd);
        MyImage = drawLayeredImage(MyImage, MyImageLayer);

        // Apply outlines
        switch (handle_type) {
            case JOYCON_L:
                MyImageLayer = load_layer("base64_l_joy_lines");
                break;
            case JOYCON_R:
                MyImageLayer = load_layer("base64_r_joy_lines");
                break;
            case PROCON:
                MyImageLayer = load_layer("base64_pro_lines");
                break;
            default:
                MyImageLayer = load_layer("base64_pro_lines");
                break;
        }
        MyImage = drawLayeredImage(MyImage, MyImageLayer);

        // Draw image
        this.pictureBoxPreview.Image = (Image)(MyImage);
        this.pictureBoxPreview.ClientSize = new System.Drawing.Size(312, 192);
        this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
    }

    private Bitmap drawLayeredImage(Bitmap baseImage, Bitmap layerImage) {
        Graphics g = System.Drawing.Graphics.FromImage(baseImage);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        g.DrawImage(layerImage, 0, 0);
        return baseImage;
    }

    private void update_colors_from_spi(bool update_color_dialog) {
        u8* spiColors = stackalloc u8[12];
        memset(spiColors, 0, 12);

        int res = get_spi_data(0x6050, 12, spiColors);

        update_joycon_color(
            (u8)spiColors[0], (u8)spiColors[1], (u8)spiColors[2],    // Body Colors
            (u8)spiColors[3], (u8)spiColors[4], (u8)spiColors[5],    // Button Colors
            (u8)spiColors[6], (u8)spiColors[7], (u8)spiColors[8],    // Left Grip Colors (Pro Controller, Switch Update 5.0.0+)
            (u8)spiColors[9], (u8)spiColors[10], (u8)spiColors[11]); // Right Grip Colors (Pro Controller, Switch Update 5.0.0+)

        if (update_color_dialog) {
            this.jcBodyColor = Color.FromArgb(0xFF, (u8)spiColors[0], (u8)spiColors[1], (u8)spiColors[2]);
            this.lbl_Body_hex_txt.Text = "Body: #" + String.Format("{0:X6}",
                ((u8)spiColors[0] << 16) + ((u8)spiColors[1] << 8) + ((u8)spiColors[2]));
            this.lbl_Body_hex_txt.Font = (Fonts.Create("Segoe UI", 9, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point,
                (System.Byte)(161)));
            this.lbl_Body_hex_txt.Size = new System.Drawing.Size(128, 24);

            this.jcButtonsColor = Color.FromArgb(0xFF, (u8)spiColors[3], (u8)spiColors[4], (u8)spiColors[5]);
            this.lbl_Buttons_hex_txt.Text = "Buttons: #" + String.Format("{0:X6}",
                ((u8)spiColors[3] << 16) + ((u8)spiColors[4] << 8) + ((u8)spiColors[5]));
            this.lbl_Buttons_hex_txt.Font = (Fonts.Create("Segoe UI", 9, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point,
                (System.Byte)(161)));
            this.lbl_Buttons_hex_txt.Size = new System.Drawing.Size(128, 24);

            this.jcGripLeftColor = Color.FromArgb(0xFF, (u8)spiColors[6], (u8)spiColors[7], (u8)spiColors[8]);
            this.jcGripRightColor = Color.FromArgb(0xFF, (u8)spiColors[9], (u8)spiColors[10], (u8)spiColors[11]);
        }

        if (res != 0) {
            this.lbl_Body_hex_txt.Text = "Error!";
            this.lbl_Buttons_hex_txt.Text = "Error!";
        }
    }

    private ToolStripLabel toolStripLabel_link;
    private Timer timer_link;
    private int link_errors_total, link_timeouts_total;
    private long link_longest_gap_total;

    static readonly System.Drawing.Color link_ok    = System.Drawing.Color.FromArgb(251, 251, 251);
    static readonly System.Drawing.Color link_slow  = System.Drawing.Color.FromArgb(255, 188, 0);
    static readonly System.Drawing.Color link_error = System.Drawing.Color.FromArgb(255, 60, 40);

    // Link: reports per second and the longest wait for one while the app was reading
    // ("66/s 17ms"; details in the tooltip).
    // Orange: a wait over 100ms. Red: failed reads/writes (e.g. the controller disconnected).
    internal void update_link_health() {
        LinkStats st = link_take_stats();
        int errors = st.errors + st.write_errors;
        link_errors_total   += errors;
        link_timeouts_total += st.timeouts;
        link_longest_gap_total = Math.Max(link_longest_gap_total, st.longest_gap_ms);
        string text;
        if (handle == IntPtr.Zero && fake == null)
            text = "No link";
        else if (st.reports == 0 && st.writes == 0 && errors == 0)
            text = "Link idle";
        else {
            int rate = st.interval_ms > 0 ? (int)Math.Round(st.reports * 1000.0 / st.interval_ms) : st.reports;
            text = errors > 0 ? "Link: " + errors + " err" : rate + "/s " + st.longest_gap_ms + "ms";
        }
        this.toolStripLabel_link.Text = text;
        this.toolStripLabel_link.ForeColor = errors > 0 ? link_error : st.longest_gap_ms > 100 ? link_slow : link_ok;
        this.toolStripLabel_link.ToolTipText =
            "Link health over the last second (Linux):\n" +
            "  reports received: " + st.reports + ", sent: " + st.writes + "\n" +
            "  reads that timed out: " + st.timeouts + " (normal while idle in simple HID mode)\n" +
            "  longest wait for a report: " + st.longest_gap_ms + "ms (~15ms is normal)\n" +
            "  failed reads/writes: " + errors + "\n" +
            "Since start: " + link_errors_total + " failed, " + link_timeouts_total + " timeouts, longest wait " + link_longest_gap_total + "ms";
    }

    internal string LinkText { get { return this.toolStripLabel_link.Text; } }
    internal System.Drawing.Color LinkColor { get { return this.toolStripLabel_link.ForeColor; } }

    private void update_battery() {
        u8* batt_info = stackalloc u8[3];
        memset(batt_info, 0, 3);

        get_battery(batt_info);

        int batt_percent = 0;
        int batt = ((u8)batt_info[0] & 0xF0) >> 4;
        
        // Calculate aproximate battery percent from regulated voltage
        u16 batt_volt = (u16)((u8)batt_info[1] + ((u8)batt_info[2] << 8));
        if (batt_volt < 0x560)
            batt_percent = 1;
        else if (batt_volt > 0x55F && batt_volt < 0x5A0) {
            batt_percent = (int)(((batt_volt - 0x60) & 0xFF) / 7.0f + 1);
        }
        else if (batt_volt > 0x59F && batt_volt < 0x5E0) {
            batt_percent = (int)(((batt_volt - 0xA0) & 0xFF) / 2.625f + 11);
        }
        else if (batt_volt > 0x5DF && batt_volt < 0x618) {
            batt_percent = (int)((batt_volt - 0x5E0) / 1.8965f + 36);
        }
        else if (batt_volt > 0x617 && batt_volt < 0x658) {
            batt_percent = (int)(((batt_volt - 0x18) & 0xFF) / 1.8529f + 66);
        }
        else if (batt_volt > 0x657)
            batt_percent = 100;

        this.toolStripLabel_batt.Text = String.Format(" {0:f2}V - {1:D}%", (batt_volt * 2.5) / 1000, batt_percent);

        // Update Battery icon from input report value.
        switch (batt) {
            case 0:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_0")));
                this.toolStripBtn_batt.ToolTipText = "Empty\n\nDisconnected?";
                break;
            case 1:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_0_chr")));
                this.toolStripBtn_batt.ToolTipText = "Empty, Charging.";
                break;
            case 2:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_25")));
                this.toolStripBtn_batt.ToolTipText = "Low\n\nPlease charge your device!";
                break;
            case 3:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_25_chr")));
                this.toolStripBtn_batt.ToolTipText = "Low\n\nCharging";
                break;
            case 4:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_50")));
                this.toolStripBtn_batt.ToolTipText = "Medium";
                break;
            case 5:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_50_chr")));
                this.toolStripBtn_batt.ToolTipText = "Medium\n\nCharging";
                break;
            case 6:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_75")));
                this.toolStripBtn_batt.ToolTipText = "Good";
                break;
            case 7:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_75_chr")));
                this.toolStripBtn_batt.ToolTipText = "Good\n\nCharging";
                break;
            case 8:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_100")));
                this.toolStripBtn_batt.ToolTipText = "Full";
                break;
            case 9:
                this.toolStripBtn_batt.Image =
                    ((System.Drawing.Bitmap)(resources.GetObject("batt_100_chr")));
                this.toolStripBtn_batt.ToolTipText = "Almost full\n\nCharging";
                break;
        }
    
    }

    private void update_temperature() {
        u8* temp_info = stackalloc u8[2];
        memset(temp_info, 0, 2);
        get_temperature(temp_info);
        // Convert reading to Celsius according to datasheet
        float temperature_c = 25.0f + uint16_to_int16(temp_info[1] << 8 | temp_info[0]) * 0.0625f;
        float temperature_f = temperature_c * 1.8f + 32;

        if (temp_celsius)
            this.toolStripLabel_temp.Text = String.Format("{0:f1}\u2103 ", temperature_c);
        else
            this.toolStripLabel_temp.Text = String.Format("{0:f1}\u2109 ", temperature_f);
    }

    private void Form1_FormClosing(System.Object  sender, FormClosingEventArgs e)
    {
        //check if spi dumping in progress
        if (handler_close == 1) {
            if (MessageBox.Show("SPI dumping in process!\n\nDo you really want to exit?", "Warning!",
                MessageBoxButtons.YesNo, MessageBoxIcon.Stop) == System.Windows.Forms.DialogResult.Yes) {
                Environment.Exit(0);
            }
            else {
                e.Cancel = true;
            }
        }
        else if (handler_close == 2) {
            if (MessageBox.Show("Full restore in process!\n\nDo you really want to exit?", "Warning!",
                MessageBoxButtons.YesNo, MessageBoxIcon.Stop) == System.Windows.Forms.DialogResult.Yes) {
                Environment.Exit(0);
            }
            else {
                e.Cancel = true;
            }
        }
        else if (handler_close == 0) {
            Environment.Exit(0);
        }
    }

    private void aboutToolStripMenuItem_Click(System.Object sender, System.EventArgs e) {
        if (MessageBox.Show("CTCaer's " + this.Text + "\n\nDo you want to go to the official forum and check for the latest version?\n\n",
            "About", MessageBoxButtons.YesNo, MessageBoxIcon.Asterisk) == System.Windows.Forms.DialogResult.Yes)
        {
            System.Diagnostics.Process.Start("http://gbatemp.net/threads/tool-joy-con-toolkit-v1-0.478560/");
        }
    }

    private void debugToolStripMenuItem_Click(System.Object  sender, System.EventArgs  e) {
        if (option_is_on != 1) {
            reset_window_option(false);
            this.Controls.Add(this.grpBox_DebugCmd);
            this.Controls.Add(this.btn_enableExpertMode);
            // Recalculate buggy textboxes for high DPI
            this.textBoxDbg_SubcmdArg.Size = new System.Drawing.Size(188, 44);
            this.textBoxDbg_sent.Size      = new System.Drawing.Size(170, 47);
            this.textBoxDbg_reply_cmd.Size = new System.Drawing.Size(170, 45);
            this.textBoxDbg_reply.Size     = new System.Drawing.Size(170, 69);
            option_is_on = 1;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        else
            reset_window_option(true);
    }

    private void btn_enableExpertMode_Click(System.Object  sender, System.EventArgs  e) {
        disable_expert_mode = false;
        this.grpBox_DebugCmd.Text = "Debug: Expert Mode";
    }

    private void btn_RestoreEnable_Click(System.Object  sender, System.EventArgs  e) {
        if (option_is_on != 2) {
            reset_window_option(false);
            this.Controls.Add(this.grpBox_Restore);
            option_is_on = 2;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        else
            reset_window_option(true);
    }

    private void label_sn_Click(System.Object  sender, System.EventArgs  e) {
        if (option_is_on != 3) {
            reset_window_option(false);
            this.Controls.Add(this.grpBox_ChangeSN);
            option_is_on = 3;
            this.textBox_chg_sn.Size = new System.Drawing.Size(186, 25);
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        else
            reset_window_option(true);
    }

    private void btnPlayVibEnable_Click(System.Object  sender, System.EventArgs  e) {
        if (option_is_on != 4) {
            reset_window_option(false);
            this.Controls.Add(this.grpBox_VibPlayer);
            option_is_on = 4;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        else
            reset_window_option(true);
    }

    private void buttonTestToolStripMenuItem_Click(System.Object  sender, System.EventArgs  e) {
        if (option_is_on != 5) {
            reset_window_option(false);
            this.Controls.Add(this.grpBox_ButtonTest);
            this.Controls.Add(this.grpBox_StickCal);
            this.Controls.Add(this.grpBox_dev_param);
            this.Controls.Add(this.grpBox_accGyroCal);
            this.Controls.Add(this.grpBox_nfc);
            // Recalculate buggy textboxes for high DPI
            this.lbl_nfcHelp.Size   = new System.Drawing.Size(203, 85);
            this.txtBox_nfcUid.Size = new System.Drawing.Size(208, 53);
            this.textBox_lstick_fcal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_lstick_ucal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_rstick_fcal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_rstick_ucal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_6axis_cal.Size     = new System.Drawing.Size(156, 88);
            this.textBox_6axis_ucal.Size    = new System.Drawing.Size(156, 88);
            this.txtBox_devParameters.Size  = new System.Drawing.Size(135, 185);
            this.txtBox_devParameters2.Size = new System.Drawing.Size(140, 130);
            this.textBox_btn_test_reply.Size    = new System.Drawing.Size(205, 172);
            this.textBox_btn_test_subreply.Size = new System.Drawing.Size(205, 140);

            this.btn_runBtnTest.Text = "Turn on";
            option_is_on = 5;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        else {
            reset_window_option(true);
            this.btn_runBtnTest.Text = "Turn on";
        }
    }

    private void iRCameraToolStripMenuItem_Click(System.Object  sender, System.EventArgs  e) {
        if (option_is_on != 6) {
            reset_window_option(false);
            this.Controls.Add(this.grpBox_IR);
            this.Controls.Add(this.grpBox_IRSettings);
            this.grpBox_IRSettings.BringToFront();
            this.btn_getIRStream.Text = "Stream";
            option_is_on = 6;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        else {
            reset_window_option(true);
            this.btn_getIRStream.Text = "Stream";
        }
    }

    private void editCalibrationToolStripMenuItem_Click(System.Object  sender, System.EventArgs  e) {
        if (option_is_on != 7) {
            reset_window_option(false);
            this.Controls.Add(this.grpBox_editCalModel);
            this.lbl_editStickDevHelp.Size = new System.Drawing.Size(211, 46);
            this.grpBox_editCalModel.BringToFront();
            select_cal_tab(0);
            option_is_on = 7;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        else
            reset_window_option(true);
    }

    private void reset_window_option(bool reset_all) {
        if (stickCalPanel != null) {
            stickCalPanel.Stop();
            motionCalPanel.Stop();
        }
        if (!check_connection_ok) {
            this.toolStripBtn_refresh.Enabled    = false;
            this.toolStripBtn_Disconnect.Enabled = false;
        }
        enable_button_test  = false;
        enable_IRVideoPhoto = false;
        enable_NFCScanning  = false;
        this.Controls.Remove(this.grpBox_DebugCmd);
        this.Controls.Remove(this.grpBox_Restore);
        this.Controls.Remove(this.grpBox_ChangeSN);
        this.Controls.Remove(this.grpBox_VibPlayer);
        this.Controls.Remove(this.grpBox_ButtonTest);
        this.Controls.Remove(this.grpBox_IR);
        this.Controls.Remove(this.grpBox_IRSettings);
        this.Controls.Remove(this.grpBox_editCalModel);

        this.Controls.Remove(this.grpBox_nfc);
        this.Controls.Remove(this.btn_enableExpertMode);
        this.Controls.Remove(this.grpBox_StickCal);
        this.Controls.Remove(this.grpBox_dev_param);
        this.Controls.Remove(this.grpBox_accGyroCal);

        this.textBoxDbg_sent.Visible      = false;
        this.textBoxDbg_reply.Visible     = false;
        this.textBoxDbg_reply_cmd.Visible = false;

        grpBox_leftStickUCal.Enabled  = false;
        grpBox_rightStickUCal.Enabled = false;
        grpBox_CalUserAcc.Enabled     = false;
        grpBox_StickDevParam.Enabled  = false;

        this.menuStrip1.Refresh();
        this.toolStrip1.Refresh();

        if (reset_all) {
            option_is_on = 0;
        }
    }

    private static int parse_hex(string text) {
        int value;
        if (Int32.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            return value;
        return 0;
    }

    private void btn_dbgSendCmd_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;


        u8* test = stackalloc u8[44];
        memset(test, 0, 44);

        // std::stringstream << std::hex >> int: parse as hex, 0 if it isn't a number.
        test[0] = (u8)parse_hex(this.textBoxDbg_cmd.Text);
        test[1] = (u8)parse_hex(this.textBoxDbg_hfreq.Text);
        test[2] = (u8)parse_hex(this.textBoxDbg_hamp.Text);
        test[3] = (u8)parse_hex(this.textBoxDbg_lfreq.Text);
        test[4] = (u8)parse_hex(this.textBoxDbg_lfamp.Text);
        test[5] = (u8)parse_hex(this.textBoxDbg_subcmd.Text);

        u8 get_i = 0;
        string ss_arguments = this.textBoxDbg_SubcmdArg.Text;
        int pos = 0;
        char i_getarg;

        //Get Low nibble if odd number of characters
        if (ss_arguments.Length % 2 != 0) {
            i_getarg = ss_arguments[pos++];
            if (i_getarg >= 'A' && i_getarg <= 'F')
                test[6] = (u8)((i_getarg - '7') & 0xF);
            else if (i_getarg <= '9' && i_getarg >= '0')
                test[6] = (u8)((i_getarg - '0') & 0xF);
            get_i++;
        }

        // Only 38 argument bytes fit in the 44 byte command buffer.
        while (pos < ss_arguments.Length && 6 + get_i < 44) {
            i_getarg = ss_arguments[pos++];
            //Get High nibble
            if (i_getarg >= 'A' && i_getarg <= 'F')
                test[6 + get_i] = (u8)(((i_getarg - '7') << 4) & 0xF0);
            else if (i_getarg <= '9' && i_getarg > '0')
                test[6 + get_i] = (u8)(((i_getarg - '0') << 4) & 0xF0);
            //Get Low nibble
            if (pos < ss_arguments.Length) {
                i_getarg = ss_arguments[pos++];
                if (i_getarg >= 'A' && i_getarg <= 'F')
                    test[6 + get_i] += (u8)((i_getarg - '7') & 0xF);
                else if (i_getarg <= '9' && i_getarg >= '0')
                    test[6 + get_i] += (u8)((i_getarg - '0') & 0xF);
            }
            get_i++;
        }

        send_custom_command(test);
        this.textBoxDbg_sent.Visible      = true;
        this.textBoxDbg_reply.Visible     = true;
        this.textBoxDbg_reply_cmd.Visible = true;

        if (test[5] != 0x06) {
            update_battery();
            update_temperature();
        }
    }

    private void btn_loadSPIBackup_Click(System.Object  sender, System.EventArgs  e) {
        bool validation_check = true;
        bool mac_check = true;
        allow_full_restore = true;
        bool ota_exists = true;
        //Bootloader, device type, FW DS1, FW DS2
        byte[] validation_magic = { 
            0x01, 0x08, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x08, 0xC0, 0x5D, 0x89, 0xFD, 0x04, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x40, 0x06,
            (u8)handle_type, 0xA0,
            0x0A, 0xFB, 0x00, 0x00, 0x02, 0x0D,
            0xAA, 0x55, 0xF0, 0x0F, 0x68, 0xE5, 0x97, 0xD2 };
        Stream fileStream;
        string str_dev_type = null;
        string str_backup_dev_type = null;

        OpenFileDialog openFileDialog1 = new OpenFileDialog();
        openFileDialog1.InitialDirectory =
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "BackupDirectory");
        openFileDialog1.Filter = "SPI Backup (*.bin)|*.bin";
        openFileDialog1.FilterIndex = 1;
        openFileDialog1.RestoreDirectory = true;
        if (openFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
            (fileStream = openFileDialog1.OpenFile()) != null)
        {
            System.IO.MemoryStream ms = new System.IO.MemoryStream(1048576);;
            fileStream.CopyTo(ms);
            this.backup_spi = ms.ToArray();
            fileStream.Close();

            if (this.backup_spi.Length != 524288) {
                MessageBox.Show("The file size must be 512KB (524288 Bytes)", "Partial backup!",
                    MessageBoxButtons.OK, MessageBoxIcon.Stop);
                this.txtBox_fileLoaded.Text = "No file loaded";
                this.comboBox_rstOption.Visible = false;
                this.lbl_rstDesc.Visible = false;
                this.btn_restore.Visible = false;
                this.grpBox_RstUser.Visible = false;
                this.lbl_rstDisclaimer.Visible = true;
                
                return;
            }

            if (handle_type == JOYCON_L)
                str_dev_type = "Joy-Con (L)";
            else if (handle_type == JOYCON_R)
                str_dev_type = "Joy-Con (R)";
            else if (handle_type == PROCON)
                str_dev_type = "Pro controller";

            if (this.backup_spi[0x6012] == 1)
                str_backup_dev_type = "Joy-Con (L)";
            else if (this.backup_spi[0x6012] == 2)
                str_backup_dev_type = "Joy-Con (R)";
            else if (this.backup_spi[0x6012] == 3)
                str_backup_dev_type = "Pro controller";

            //Backup Validation
            for (int i = 0; i < 20; i++) {
                if (validation_magic[i] != this.backup_spi[i]) {
                    validation_check = false;
                    break;
                }
            }
            for (int i = 20; i < 22; i++) {
                if (validation_magic[i] != this.backup_spi[0x6012 + i - 20]) {
                    MessageBox.Show("The file is a \"" + str_backup_dev_type
                        + "\" backup but your device is a \"" + str_dev_type + "\"!\n\nPlease try with a \""
                        + str_dev_type + "\" SPI backup.", "Wrong backup!", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                    this.txtBox_fileLoaded.Text = "No file loaded";
                    this.lbl_rstDesc.Visible        = false;
                    this.comboBox_rstOption.Visible = false;
                    this.btn_restore.Visible        = false;
                    this.grpBox_RstUser.Visible     = false;
                    this.lbl_rstDisclaimer.Visible  = true;

                    return;
                }
            }
            for (int i = 28; i < 36; i++) {
                if (validation_magic[i] != this.backup_spi[0x1FF4 + i - 22])
                {
                    ota_exists = false;
                    break;
                }
            }

            if (ota_exists) {
                for (int i = 22; i < 28; i++) {
                    if (validation_magic[i] != this.backup_spi[0x10000 + i - 22] && i != 23) {
                        validation_check = false;
                        break;
                    }
                    if (validation_magic[i] != this.backup_spi[0x28000 + i - 22] && i != 23) {
                        validation_check = false;
                        break;
                    }
                }
            }
            else {
                for (int i = 22; i < 28; i++) {
                    if (validation_magic[i] != this.backup_spi[0x10000 + i - 22] && i != 23) {
                        validation_check = false;
                        break;
                    }
                }
            }
            if (!validation_check) {
                MessageBox.Show("The SPI backup is corrupted!\n\nPlease try another backup.",
                    "Corrupt backup!", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                this.txtBox_fileLoaded.Text = "No file loaded";
                this.lbl_rstDesc.Visible        = false;
                this.comboBox_rstOption.Visible = false;
                this.btn_restore.Visible        = false;
                this.grpBox_RstUser.Visible     = false;
                this.lbl_rstDisclaimer.Visible  = true;
                
                return;
            }

            u8* mac_addr = stackalloc u8[10];
            memset(mac_addr, 0, 10);
            get_device_info(mac_addr);

            for (int i = 4; i < 10; i++) {
                if (mac_addr[i] != this.backup_spi[0x1A - i + 4]) {
                    mac_check = false;
                }
            }
            if (!mac_check) {
                if (MessageBox.Show("The SPI backup is from another \"" + str_dev_type + "\".\n\nDo you want to continue?",
                    "Different BT MAC address!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) ==
                    System.Windows.Forms.DialogResult.Yes)
                {
                    this.txtBox_fileLoaded.Text = String.Format("{0:x2}:{1:x2}:{2:x2}:{3:x2}:{4:x2}:{5:x2}",
                        this.backup_spi[0x1A], this.backup_spi[0x19], this.backup_spi[0x18],
                        this.backup_spi[0x17], this.backup_spi[0x16], this.backup_spi[0x15]);
                    this.comboBox_rstOption.SelectedItem = "Restore Color";
                    this.comboBox_rstOption.Visible = true;
                    this.btn_restore.Visible = true;
                    this.btn_restore.Enabled = true;
                    this.lbl_rstDisclaimer.Visible = false;
                    allow_full_restore = false;
                }
                else {
                    this.txtBox_fileLoaded.Text = "No file loaded";
                    this.lbl_rstDesc.Visible        = false;
                    this.comboBox_rstOption.Visible = false;
                    this.btn_restore.Visible        = false;
                    this.grpBox_RstUser.Visible     = false;
                    this.lbl_rstDisclaimer.Visible  = true;
                }
            }
            else {
                this.txtBox_fileLoaded.Text = String.Format("{0:x2}:{1:x2}:{2:x2}:{3:x2}:{4:x2}:{5:x2}",
                    this.backup_spi[0x1A], this.backup_spi[0x19], this.backup_spi[0x18],
                    this.backup_spi[0x17], this.backup_spi[0x16], this.backup_spi[0x15]);
                this.comboBox_rstOption.SelectedItem = "Restore Color";
                this.comboBox_rstOption.Visible = true;
                this.btn_restore.Visible = true;
                this.btn_restore.Enabled = true;
                this.lbl_rstDisclaimer.Visible = false;
            }
        }
    }

    protected void comboBox_darkTheme_DrawItem(System.Object sender, DrawItemEventArgs e)
    {
        Brush brush = new System.Drawing.SolidBrush(Color.FromArgb(251, 251, 251));;

        if (e.Index < 0)
            return;

        ComboBox combo = (ComboBox)sender;
        if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(105, 105, 105)), e.Bounds);
        else
            e.Graphics.FillRectangle(new SolidBrush(combo.BackColor), e.Bounds);

        e.Graphics.DrawString(combo.Items[e.Index].ToString(), e.Font, brush,
            e.Bounds, StringFormat.GenericDefault);

        e.DrawFocusRectangle();
    }

    private void comboBox_rstOption_SelectedIndexChanged(System.Object  sender, System.EventArgs  e) {
        this.grpBox_RstUser.Visible = false;
        this.lbl_rstDesc.Visible = false;
        this.btn_restore.Text = "Restore";
        this.lbl_rstDesc.Text = "This allows for the device colors to be restored from the loaded backup.\n\n"+
            "For more options click on the list above.";

        if (this.comboBox_rstOption.SelectedIndex == 0) {
            this.lbl_rstDesc.Visible = true;
        }
        if (this.comboBox_rstOption.SelectedIndex == 1) {
            this.lbl_rstDesc.Visible = true;
            this.lbl_rstDesc.Text = "This will restore your S/N from the selected backup.\n" +
                "*Make sure that this backup was your original one!\n\nIf you lost your S/N, " +
                "check the plastic sliver it was wrapped inside the box.";
        }
        else if (this.comboBox_rstOption.SelectedIndex == 2) {
            this.lbl_rstDesc.Visible = true;
            this.lbl_rstDesc.Text =
                "This lets you restore the chosen user calibrations from your SPI backup.";
            this.grpBox_RstUser.Visible = true;
            if (handle_type == JOYCON_L) {
                this.checkBox_rst_R_StickCal.Visible  = false;
                this.checkBox_rst_L_StickCal.Visible  = true;
                this.checkBox_rst_L_StickCal.Location = this.checkBox_rst_R_StickCal.Location;
            }
            else if (handle_type == JOYCON_R) {
                this.checkBox_rst_R_StickCal.Visible = true;
                this.checkBox_rst_L_StickCal.Visible = false;
            }
            else if (handle_type == PROCON) {
                this.checkBox_rst_R_StickCal.Visible = true;
                this.checkBox_rst_L_StickCal.Visible = true;
            }

        }
        else if (this.comboBox_rstOption.SelectedIndex == 3) {
            this.lbl_rstDesc.Visible = true;
            this.lbl_rstDesc.Text =
                "This option does the same factory reset with the option inside Switch's controller calibration menu.";
            this.btn_restore.Text = "Reset";
            this.grpBox_RstUser.Visible = true;
            if (handle_type == JOYCON_L) {
                this.checkBox_rst_R_StickCal.Visible  = false;
                this.checkBox_rst_L_StickCal.Visible  = true;
                this.checkBox_rst_L_StickCal.Location = this.checkBox_rst_R_StickCal.Location;
            }
            else if (handle_type == JOYCON_R) {
                this.checkBox_rst_R_StickCal.Visible = true;
                this.checkBox_rst_L_StickCal.Visible = false;
            }
            else if (handle_type == PROCON) {
                this.checkBox_rst_R_StickCal.Visible = true;
                this.checkBox_rst_L_StickCal.Visible = true;
            }
        }
        else if (this.comboBox_rstOption.SelectedIndex == 4) {
            this.lbl_rstDesc.Visible = true;
            this.lbl_rstDesc.Text = "This restores the Factory and User configuration.\n\n" +
                "To preserve factory configuration from accidental overwrite, " +
                "the full restore is disabled if the backup does not match your device.";
            if (!allow_full_restore)
                this.btn_restore.Enabled = false;
        }
    }

    private void btn_restore_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;
        this.menuStrip1.Enabled = false;
        this.toolStrip1.Enabled = false;
        set_led_busy();
        int error = 0;
        if (this.comboBox_rstOption.SelectedIndex == 0) {
            if (MessageBox.Show("The device color will be restored with the backup values!\n\nAre you sure you want to continue?",
                "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                u8* backupColor = stackalloc u8[12];
                memset(backupColor, 0, 12);

                for (int i = 0; i < 12; i++) {
                    backupColor[i] = this.backup_spi[0x6050 + i];
                }

                error = write_spi_data(0x6050, 12, backupColor);

                //Check that the colors were written
                update_colors_from_spi(true);
                send_rumble();
                if (error == 0)
                    MessageBox.Show("The colors were restored!", "Restore Finished!", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
            }

        }
        else if (this.comboBox_rstOption.SelectedIndex == 1) {
            if (MessageBox.Show("The serial number will be restored with the backup values!\n\nAre you sure you want to continue?",
                "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                u8* sn = stackalloc u8[0x10];
                memset(sn, 0, 0x10);


                for (int i = 0; i < 0x10; i++) {
                    sn[i] = this.backup_spi[0x6000 + i];
                }

                error = write_spi_data(0x6000, 0x10, sn);
                send_rumble();
                if (error == 0) {
                    string new_sn = null;
                    if (handle_type != 3) {
                        new_sn = get_sn(0x6001, 0xF);
                        MessageBox.Show("The serial number was restored and changed to \"" + new_sn + "\"!",
                            "Restore Finished!", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                    }
                    else {
                        MessageBox.Show("The serial number was restored!", "Restore Finished!",
                            MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                    }
                }
            }
        }
        else if (this.comboBox_rstOption.SelectedIndex == 2) {
            if (MessageBox.Show("The selected user calibration will be restored from the backup!\n\n" +
                "Are you sure you want to continue?", "Warning!",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                u8* l_stick = stackalloc u8[0xB];
                memset(l_stick, 0, 0xB);
                u8* r_stick = stackalloc u8[0xB];
                memset(r_stick, 0, 0xB);
                u8* sensor = stackalloc u8[0x1A];
                memset(sensor, 0, 0x1A);


                for (int i = 0; i < 0xB; i++) {
                    l_stick[i] = this.backup_spi[0x8010 + i];
                    r_stick[i] = this.backup_spi[0x801B + i];
                }
                for (int i = 0; i < 0x1A; i++) {
                    sensor[i] = this.backup_spi[0x8026 + i];
                }

                if (handle_type != 2 && this.checkBox_rst_L_StickCal.Checked == true)
                    error = write_spi_data(0x8010, 0xB, l_stick);
                Sleep(100);
                if (handle_type != 1 && this.checkBox_rst_R_StickCal.Checked == true && error == 0)
                    error = write_spi_data(0x801B, 0xB, r_stick);
                Sleep(100);
                if (this.checkBox_rst_accGyroCal.Checked == true && error == 0)
                    error = write_spi_data(0x8026, 0x1A, sensor);
                send_rumble();

                if (error == 0) {
                    update_cal_status();
                    MessageBox.Show("The user calibration was restored!", "Calibration Restore Finished!",
                        MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                }
            }

        }
        else if (this.comboBox_rstOption.SelectedIndex == 3) {
            if (MessageBox.Show("The selected user calibration will be factory resetted!\n\nAre you sure you want to continue?",
                "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                u8* l_stick = stackalloc u8[0xB];
                memset(l_stick, 0xFF, 0xB);
                u8* r_stick = stackalloc u8[0xB];
                memset(r_stick, 0xFF, 0xB);
                u8* sensor = stackalloc u8[0x1A];
                memset(sensor, 0xFF, 0x1A);

                if (handle_type != 2 && this.checkBox_rst_L_StickCal.Checked == true)
                    error = write_spi_data(0x8010, 0xB, l_stick);
                Sleep(100);
                if (handle_type != 1 && this.checkBox_rst_R_StickCal.Checked == true && error == 0)
                    error = write_spi_data(0x801B, 0xB, r_stick);
                Sleep(100);
                if (this.checkBox_rst_accGyroCal.Checked == true && error == 0)
                    error = write_spi_data(0x8026, 0x1A, sensor);
                send_rumble();

                if (error == 0) {
                    update_cal_status();
                    MessageBox.Show("The user calibration was factory resetted!", "Calibration Reset Finished!",
                        MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                }
            }

        }
        else if (this.comboBox_rstOption.SelectedIndex == 4) {
            if (MessageBox.Show("This will do a full restore of the Factory configuration and User calibration!\n\n" +
                "Are you sure you want to continue?",
                "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                handler_close = 2;
                this.btn_loadSPIBackup.Enabled = false;
                this.btn_restore.Enabled = false;
                this.grpBox_Color.Visible = false;
                this.lbl_spiProggressDesc.Text = "Restoring Factory Configuration and User Calibration...\n\nDon\'t disconnect your device!";
                u8* full_restore_data = stackalloc u8[0x10];
                u8* sn_backup_erase = stackalloc u8[0x10];
                memset(sn_backup_erase, 0xFF, 16);

                // Factory Configuration Sector 0x6000
                for (int i = 0x00; i < 0x1000; i = i + 0x10) {
                    memset(full_restore_data, 0, 0x10);
                    for (int j = 0; j < 0x10; j++)
                        full_restore_data[j] = this.backup_spi[0x6000 + i + j];
                    if (error == 0)
                        error = write_spi_data((u32)(0x6000 + i), 0x10, full_restore_data);

                    FormJoy.myform1.label_progress.Text = String.Format("{0:F2}KB of 8KB", i / 1024.0f);
                    Application.DoEvents();
                    Sleep(60);
                }
                // User Calibration Sector 0x8000
                for (int i = 0x00; i < 0x1000; i = i + 0x10) {
                    memset(full_restore_data, 0, 0x10);
                    for (int j = 0; j < 0x10; j++)
                        full_restore_data[j] = this.backup_spi[0x8000 + i + j];
                    if (error == 0)
                        error = write_spi_data((u32)(0x8000 + i), 0x10, full_restore_data);

                    FormJoy.myform1.label_progress.Text = String.Format("{0:F2}KB of 8KB", 4 + (i / 1024.0f));
                    Application.DoEvents();
                    Sleep(60);
                }
                // Erase S/N backup storage
                if (error == 0)
                    error = write_spi_data(0xF000, 0x10, sn_backup_erase);

                FormJoy.myform1.label_progress.Text = String.Format("{0:F2}KB of 8KB", 0x2000 / 1024.0f);
                Application.DoEvents();

                if (error == 0) {
                    // Set shipment
                    u8* custom_cmd = stackalloc u8[7];
                    memset(custom_cmd, 0, 7);
                    custom_cmd[0] = 0x01;
                    custom_cmd[5] = 0x08;
                    custom_cmd[6] = 0x01;
                    send_custom_command(custom_cmd);
                    // Clear pairing info
                    memset(custom_cmd, 0, 7);
                    custom_cmd[0] = 0x01;
                    custom_cmd[5] = 0x07;
                    send_custom_command(custom_cmd);
                    // Reboot controller and go into pairing mode
                    memset(custom_cmd, 0, 7);
                    custom_cmd[0] = 0x01;
                    custom_cmd[5] = 0x06;
                    custom_cmd[6] = 0x02;
                    send_custom_command(custom_cmd);

                    send_rumble();

                    MessageBox.Show("The full restore was completed!\nThe controller was rebooted and it is now in pairing mode!\n\n" +
                        "Exit Joy-Con Toolkit and pair with Switch or PC again.", "Full Restore Finished!",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    handler_close = 0;
                }
                this.grpBox_Color.Visible = true;
                this.btn_loadSPIBackup.Enabled = true;
                this.btn_restore.Enabled = true;
            }
        }
        if (error != 0) {
            MessageBox.Show("Failed to restore or restore incomplete!\n\nPlease try again..", "CTCaer's Joy-Con Toolkit - Restore Failed!",
                MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
        }
        else {
            //Avoid sending commands with disconnected controller
            if (this.comboBox_rstOption.SelectedIndex != 4) {
                update_battery();
                update_temperature();
            }
        }
        this.menuStrip1.Enabled = true;
        this.toolStrip1.Enabled = true;
    }

    private void textBoxDbg_subcmd_Validating(System.Object sender, CancelEventArgs e)
    {
        bool cancel = false;
        int number = -1;
        if (Int32.TryParse(this.textBoxDbg_subcmd.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number))
        {
            if ((number == 0x10 || number == 0x11 || number == 0x12) && disable_expert_mode) {
                cancel = true;
                this.errorProvider2.SetError(this.textBoxDbg_subcmd,
                    "The subcommands:\n0x10: SPI Read\n0x11: SPI Write\n0x12: SPI Sector Erase\nare disabled!");
            }else
                cancel = false;
        }
        else
        {
            //This control has failed validation: text box is not a number
            cancel = true;
            this.errorProvider2.SetError(this.textBoxDbg_subcmd, "The input must be a valid uint8 HEX!");
        }
        e.Cancel = cancel;
    }
    private void textBoxDbg_subcmd_Validated(System.Object sender, System.EventArgs e)
    {
        //Control has validated, clear any error message.
        this.errorProvider2.SetError(this.textBoxDbg_subcmd, String.Empty);
    }

    private void textBoxDbg_Validating(System.Object sender, CancelEventArgs e)
    {
        TextBox test = (TextBox)sender;
        bool cancel = false;
        int number = -1;
        if (Int32.TryParse(test.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number))
        {
            cancel = false;
        }
        else
        {
            cancel = true;
            this.errorProvider2.SetError(test, "The input must be a valid uint8 HEX!");
        }
        e.Cancel = cancel;
    }
    private void textBoxDbg_Validated(System.Object sender, System.EventArgs e)
    {
        //Control has validated, clear any error message.
        TextBox test = (TextBox)sender;
        this.errorProvider2.SetError(test, String.Empty);
    }

    private void textBox_loop_Validating(System.Object sender, CancelEventArgs e)
    {
        TextBox test = (TextBox)sender;
        bool cancel = false;
        int number = -1;
        if (Int32.TryParse(test.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            cancel = false;
        }
        else
        {
            //This control has failed validation: text box is not a number
            cancel = true;
            this.errorProvider2.SetError(test, "The input must be a number from 0-999!");
        }
        e.Cancel = cancel;
    }

    private void textBox_loop_Validated(System.Object sender, System.EventArgs e)
    {
        TextBox test = (TextBox)sender;
        this.errorProvider2.SetError(test, String.Empty);
    }

    private void textBoxDbg_SubcmdArg_Validating(System.Object sender, CancelEventArgs e)
    {
        TextBox test = (TextBox)sender;
        bool cancel = false;
        int number = -1;
        char[] mn_str_sn = test.Text.ToCharArray();

        for (int i = 0; i < mn_str_sn.Length; i++) {
            if ((u8)(mn_str_sn[i] >> 8) == 0x00)
            {
                if (('0' <= (u8)mn_str_sn[i] && (u8)mn_str_sn[i] <= '9') || ('A' <= (u8)mn_str_sn[i] && (u8)mn_str_sn[i] <= 'F')) {
                    cancel = false;
                }
                else {
                    cancel = true;
                    this.errorProvider1.SetError(test, "The input must be a valid HEX number!");
                }
            }
            else {
                cancel = true;
                this.errorProvider1.SetError(test, "The input must be a valid HEX number!");
            }
            if (cancel)
                break;
        }
        e.Cancel = cancel;
    }
    private void textBoxDbg_SubcmdArg_Validated(System.Object sender, System.EventArgs e)
    {
        //Control has validated, clear any error message.
        TextBox test = (TextBox)sender;
        this.errorProvider1.SetError(test, String.Empty);
    }

    private void textBox_chg_sn_Validating(System.Object sender, CancelEventArgs e)
    {
        TextBox test = (TextBox)sender;
        bool cancel = false;
        int number = -1;
        char[] mn_str_sn = test.Text.ToCharArray();

        for (int i = 0; i < mn_str_sn.Length; i++) {
            if ((u8)(mn_str_sn[i] >> 8)  == 0x00)
            {
                if (31 < (u8)mn_str_sn[i] && (u8)mn_str_sn[i] < 127) {
                    cancel = false;
                }
                else {
                    cancel = true;
                    this.errorProvider1.SetError(test, "Extended ASCII characters are not supported!");
                }
            }
            else
            {
                //This control has failed validation: text box is not a number
                cancel = true;
                this.errorProvider1.SetError(test, "Unicode characters are not supported!\n\n" +
                    "Use non-extended ASCII characters only!");
            }
            if (cancel)
                break;
        }
        e.Cancel = cancel;
    }
    private void textBox_chg_sn_Validated(System.Object sender, System.EventArgs e)
    {
        //Control has validated, clear any error message.
        TextBox test = (TextBox)sender;
        this.errorProvider1.SetError(test, String.Empty);
    }

    private void btn_changeSN_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (handle_type != 3) {
            if (MessageBox.Show("This will change your Serial Number!\n\nMake a backup first!\n\n" +
                "Are you sure you want to continue?", "Warning!",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                if (MessageBox.Show("Did you make a backup?", "Warning!",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
                {
                    int error = 0;
                    int sn_ok = 1;
                    u8[] sn_magic = { 0x00, 0x00, 0x58 };
                    u8* spi_sn = stackalloc u8[0x10];
                    u8* sn_backup = stackalloc u8[0x1];
                    memset(spi_sn, 0x11, 16);
                    memset(sn_backup, 0x00, 1);
                    
                    //Check if sn is original
                    get_spi_data(0x6000, 0x10, spi_sn);
                    for (int i = 0; i < 3; i++) {
                        if (spi_sn[i] != sn_magic[i]) {
                            sn_ok = 0;
                            break;
                        }
                    }
                    //Check if already made
                    get_spi_data(0xF000, 0x1, sn_backup);
                    if (sn_ok != 0 && sn_backup[0] == 0xFF)
                        error = write_spi_data(0xF000, 0x10, spi_sn);
                    Sleep(100);
                    char[] mn_str_sn = this.textBox_chg_sn.Text.ToCharArray();
                    u8* sn = stackalloc u8[32];

                    int length = 16 - mn_str_sn.Length;

                    for (int i = 0; i < length; i++) {
                        sn[i] = 0x00;
                    }
                    for (int i = 0; i < mn_str_sn.Length; i++) {
                        sn[length + i] = (u8)(mn_str_sn[i] & 0xFF);
                    }
                    if (error == 0)
                        error = write_spi_data(0x6000, 0x10, sn);
                    update_battery();
                    update_temperature();
                    send_rumble();
                    if (error == 0) {
                        String new_sn = get_sn(0x6001, 0xF);
                        MessageBox.Show("The S/N was written to the device!\n\nThe new S/N is now \"" + new_sn +
                            "\"!\n\nIf you still ignored the warnings about creating a backup, the S/N in the left of the main window will not change. " +
                            "Copy it somewhere safe!\n\nLastly, a backup of your S/N was created inside the SPI.");
                    }
                    else {
                        MessageBox.Show("Failed to write the S/N to the device!");
                    }
                }
            }
        }
        else
            MessageBox.Show("Changing S/N is not supported for Pro Controllers!", "Error!", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void btn_restoreSN_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (handle_type != 3) {
            if (MessageBox.Show("Do you really want to restore it from the S/N backup inside your controller\'s SPI?\n\nYou can also choose to restore it from a SPI backup you previously made, through the main Restore option.",
                "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes) {
                int sn_ok = 1;
                int error = 0;
                u8* spi_sn = stackalloc u8[0x10];
                memset(spi_sn, 0x11, 16);

                //Check if there is an SN backup
                get_spi_data(0xF000, 0x10, spi_sn);
                Sleep(100);
                if (spi_sn[0] != 0x00) {
                        sn_ok = 0;
                    }
                if (sn_ok != 0) {
                    error = write_spi_data(0x6000, 0x10, spi_sn);
                }
                else {
                    MessageBox.Show("No S/N backup found inside your controller\'s SPI.\n\nThis can happen if the first time you changed your S/N was with an older version of Joy-Con Toolkit.\nOtherwise, you never changed your S/N.",
                        "Error!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                update_battery();
                update_temperature();
                send_rumble();
                if (error == 0) {
                    String new_sn = get_sn(0x6001, 0xF);
                    MessageBox.Show("The S/N was restored to the device!\n\nThe new S/N is now \"" + new_sn + "\"!");
                }
                else {
                    MessageBox.Show("Failed to restore the S/N!");
                }
            }
        }
        else
            MessageBox.Show("Restoring S/N is not supported for Pro Controllers!", "Error!", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    }

    private void pictureBoxBattery_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (MessageBox.Show("HOORAY!!\n\nYou found the easter egg!\n\nMake sure you have a good signal and get the device near your ear.\n\nThen press OK to hear the tune!\n\nIf the tune is slow or choppy:\n1. Close the app\n2. Press the sync button once to turn off the device\n3. Get close to your BT adapter and maintain LoS\n4. Press any other button and run the app again.",
            "Easter egg!", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == System.Windows.Forms.DialogResult.OK)
        {
            set_led_busy();
            play_tune(0);
            update_battery();
            update_temperature();
            send_rumble();
            MessageBox.Show("The HD Rumble music has ended.", "Easter egg!");
        }
    }

    private void toolStripLabel_batt_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (MessageBox.Show("HOORAY!!\n\nYou found another easter egg!\n\nMake sure you have a good signal and get the device near your ear.\n\nThen press OK to hear the tune!\n\nIf the tune is slow or choppy:\n1. Close the app\n2. Press the sync button once to turn off the device\n3. Get close to your BT adapter and maintain LoS\n4. Press any other button and run the app again.",
            "Easter egg 2!", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == System.Windows.Forms.DialogResult.OK)
        {
            set_led_busy();
            play_tune(1);
            update_battery();
            update_temperature();
            send_rumble();
            MessageBox.Show("The HD Rumble music has ended.", "Easter egg!");
        }
    }

    private void btn_loadVib_Click(System.Object  sender, System.EventArgs  e) {
        Stream fileStream;
        byte[] file_magic = { 0x52, 0x52, 0x41, 0x57, 0x4, 0xC, 0x3, 0x10};

        OpenFileDialog openFileDialog1 = new OpenFileDialog();
        openFileDialog1.InitialDirectory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "RumbleDirectory");
        openFileDialog1.Filter = "Binary HD Rumble (*.bnvib)|*.bnvib|Raw HD Rumble (*.jcvib)|*.jcvib";
        openFileDialog1.FilterIndex = 1;
        openFileDialog1.RestoreDirectory = true;
        if (openFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK && (fileStream = openFileDialog1.OpenFile()) != null)
        {    
            vib_converted = 0;
            this.label_hdrumble_filename.Text = openFileDialog1.SafeFileName;
            this.label_vib_loaded.Text = "";
            this.label_samplerate.Text = "";
            this.label_samples.Text = "";
            this.textBox_vib_loop_times.Text = "0";
            this.textBox_vib_loop_times.Visible = false;
            this.label_loop_times.Visible = false;
            System.IO.MemoryStream ms = new System.IO.MemoryStream((int)fileStream.Length);;
            fileStream.CopyTo(ms);
            this.vib_loaded_file = ms.ToArray();
            this.vib_file_converted = ms.ToArray();
            fileStream.Close();

            //check for vib_file_type
            if (this.vib_loaded_file[0] == file_magic[0]) {
                for (int i = 1; i < 4; i++) {
                    if (this.vib_loaded_file[i] == file_magic[i]) {
                        vib_file_type = 1;
                        this.groupBox_vib_eq.Visible = true;
                        this.groupBox_vib_eq.Enabled = false;
                    }
                    else {
                        vib_file_type = 0;
                        this.groupBox_vib_eq.Visible = false;

                        break;
                    }
                }
                if (vib_file_type == 1) {
                    this.label_vib_loaded.Text = "Type: Raw HD Rumble";
                    this.btn_vibPlay.Enabled = true;
                    vib_sample_rate = (u16)((this.vib_loaded_file[0x4] << 8) + this.vib_loaded_file[0x5]);
                    vib_samples = (u32)((this.vib_loaded_file[0x6] << 24) + (this.vib_loaded_file[0x7] << 16) + (this.vib_loaded_file[0x8] << 8) + this.vib_loaded_file[0x9]);
                    this.label_samplerate.Text = "Sample rate: " + vib_sample_rate + "ms";
                    this.label_samples.Text = "Samples: " + vib_samples + " (" + (vib_sample_rate * vib_samples) / 1000.0f + "s)";
                }
                else {
                    this.label_vib_loaded.Text = "Type: Unknown format";
                    this.btn_vibPlay.Enabled = false;
                }
            }
            else if (this.vib_loaded_file[4] == file_magic[6]) {
                if (this.vib_loaded_file[0] == file_magic[4]) {
                    vib_file_type = 2;
                    this.groupBox_vib_eq.Visible = true;
                    this.groupBox_vib_eq.Enabled = true;
                    this.label_vib_loaded.Text = "Type: Binary HD Rumble";
                    u32 vib_size = (u32)(this.vib_loaded_file[0x8] + (this.vib_loaded_file[0x9] << 8) + (this.vib_loaded_file[0xA] << 16) + (this.vib_loaded_file[0xB] << 24));
                    vib_sample_rate = (u16)(1000 / (this.vib_loaded_file[0x6] + (this.vib_loaded_file[0x7] << 8)));
                    vib_samples = vib_size / 4;
                    this.label_samplerate.Text = "Sample rate: " + vib_sample_rate + "ms";
                    this.label_samples.Text = "Samples: " + vib_samples + " (" + (vib_sample_rate * vib_samples) / 1000.0f + "s)";
                }
                else if (this.vib_file_converted[0] == file_magic[5]) {
                    vib_file_type = 3;
                    this.label_vib_loaded.Text = "Type: Loop Binary HD Rumble";
                    this.groupBox_vib_eq.Visible = true;
                    this.groupBox_vib_eq.Enabled = true;
                    u32 vib_size = (u32)(this.vib_loaded_file[0x10] + (this.vib_loaded_file[0x11] << 8) + (this.vib_loaded_file[0x12] << 16) + (this.vib_loaded_file[0x13] << 24));
                    vib_sample_rate = (u16)(1000 / (this.vib_loaded_file[0x6] + (this.vib_loaded_file[0x7] << 8)));
                    vib_samples = vib_size / 4;
                    vib_loop_start = (u32)(this.vib_loaded_file[0x8] + (this.vib_loaded_file[0x9] << 8) + (this.vib_loaded_file[0xA] << 16) + (this.vib_loaded_file[0xB] << 24));
                    vib_loop_end = (u32)(this.vib_loaded_file[0xC] + (this.vib_loaded_file[0xD] << 8) + (this.vib_loaded_file[0xE] << 16) + (this.vib_loaded_file[0xF] << 24));
                    this.label_samplerate.Text = "Sample rate: " + vib_sample_rate + "ms";
                    this.label_samples.Text = "Samples: " + vib_samples + " (" + (vib_sample_rate * vib_samples) / 1000.0f + "s)";
                    this.label_loop_times.Visible = true;
                    this.textBox_vib_loop_times.Visible = true;
                }
                else if (this.vib_file_converted[0] == file_magic[7]) {
                    vib_file_type = 4;
                    this.label_vib_loaded.Text = "Type: Loop and Wait Binary";
                    this.groupBox_vib_eq.Visible = true;
                    this.groupBox_vib_eq.Enabled = true;
                    u32 vib_size = (u32)(this.vib_loaded_file[0x14] + (this.vib_loaded_file[0x15] << 8) + (this.vib_loaded_file[0x16] << 16) + (this.vib_loaded_file[0x17] << 24));
                    vib_sample_rate = (u16)(1000 / (this.vib_loaded_file[0x6] + (this.vib_loaded_file[0x7] << 8)));
                    vib_samples = vib_size / 4;
                    vib_loop_start = (u32)(this.vib_loaded_file[0x8] + (this.vib_loaded_file[0x9] << 8) + (this.vib_loaded_file[0xA] << 16) + (this.vib_loaded_file[0xB] << 24));
                    vib_loop_end = (u32)(this.vib_loaded_file[0xC] + (this.vib_loaded_file[0xD] << 8) + (this.vib_loaded_file[0xE] << 16) + (this.vib_loaded_file[0xF] << 24));
                    vib_loop_wait = (u32)(this.vib_loaded_file[0x10] + (this.vib_loaded_file[0x11] << 8) + (this.vib_loaded_file[0x12] << 16) + (this.vib_loaded_file[0x13] << 24));
                    this.label_samplerate.Text = "Sample rate: " + vib_sample_rate + "ms";
                    this.label_samples.Text = "Samples: " + vib_samples + " (" + (vib_sample_rate * vib_samples) / 1000.0f + "s)";
                    this.label_loop_times.Visible = true;
                    this.textBox_vib_loop_times.Visible = true;
                }
                this.btn_vibPlay.Enabled = true;
            }
            else {
                vib_file_type = 0;
                this.label_vib_loaded.Text = "Type: Unknown format";
                this.btn_vibPlay.Enabled = false;
                this.groupBox_vib_eq.Visible = false;
            }
            this.btn_vibResetEQ.PerformClick();
        }
    }

    private void btn_vibPlay_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        int vib_loop_times = 0;
        if ((vib_file_type == 2 || vib_file_type == 3 || vib_file_type == 4) && vib_converted == 0) {
            u8 vib_off = 0;
            if (vib_file_type == 3)
                vib_off = 8;
            if (vib_file_type == 4)
                vib_off = 12;
            //Convert to RAW vibration, apply EQ and clamp inside safe values
            this.btn_vibPlay.Text = "Loading...";
            //vib_size = this.vib_loaded_file[0x8] + (this.vib_loaded_file[0x9] << 8) + (this.vib_loaded_file[0xA] << 16) + (this.vib_loaded_file[0xB] << 24);

            //Convert to raw
            for (u32 i = 0; i < (vib_samples * 4); i = i + 4)
            {
                //Apply amp eq
                u8 tempLA = (this.trackBar_lf_amp.Value == 10 ? this.vib_loaded_file[0xC + vib_off + i] : (u8)CLAMP((float)this.vib_loaded_file[0xC + vib_off + i] * lf_gain, 0.0f, 255.0f));
                u8 tempHA = (this.trackBar_hf_amp.Value == 10 ? this.vib_loaded_file[0xE + vib_off + i] : (u8)CLAMP((float)this.vib_loaded_file[0xE + vib_off + i] * hf_gain, 0.0f, 255.0f));

                //Apply safe limit. The sum of LF and HF amplitudes should be lower than 1.0
                float apply_safe_limit = (float)tempLA / 255.0f + tempHA / 255.0f;
                //u8 tempLA = (apply_safe_limit > 1.0f ? (u8)((float)this.vib_file_converted[0xC + i] * (0.55559999f / apply_safe_limit)) : (u8)((float)this.vib_file_converted[0xC + i] * 0.55559999f));
                tempLA = (apply_safe_limit > 1.0f ? (u8)((float)tempLA    * (1.0f / apply_safe_limit)) : tempLA);
                tempHA = (apply_safe_limit > 1.0f ? (u8)((float)tempHA * (1.0f / apply_safe_limit)) : tempHA);

                //Apply eq and convert frequencies to raw range
                u8 tempLF = (u8)((this.trackBar_lf_freq.Value == 10 ? this.vib_loaded_file[0xD + vib_off + i] : (u8)CLAMP((float)this.vib_loaded_file[0xD + vib_off + i] * lf_pitch, 0.0f, 191.0f)) - 0x40);
                u16 tempHF = (u16)(((this.trackBar_hf_freq.Value == 10 ? this.vib_loaded_file[0xF + vib_off + i] : (u8)CLAMP((float)this.vib_loaded_file[0xF + vib_off + i] * hf_pitch, 0.0f, 223.0f)) - 0x60) * 4);

                //Encode amplitudes with the look up table and direct encode frequencies
                int j;
                float temp = tempLA / 255.0f;
                for (j = 1; j < 101; j++) {
                    if (temp < Tables.lut_amp_float[j]) {
                        j--;
                        break;
                    }
                }
                this.vib_file_converted[0xE + vib_off + i] = (u8)(((Tables.lut_amp_la[j] >> 8) & 0xFF) + tempLF);
                this.vib_file_converted[0xF + vib_off + i] = (u8)(Tables.lut_amp_la[j] & 0xFF);

                temp = tempHA / 255.0f;
                for (j = 1; j < 101; j++) {
                    if (temp < Tables.lut_amp_float[j]) {
                        j--;
                        break;
                    }
                }
                this.vib_file_converted[0xC + vib_off + i] = (u8)(tempHF & 0xFF);
                this.vib_file_converted[0xD + vib_off + i] = (u8)(((tempHF >> 8) & 0xFF) + Tables.lut_amp_ha[j]);

            }
            vib_converted = 1;
        }

        // std::stringstream >> int: decimal, 0 if it isn't a number.
        if (!Int32.TryParse(this.textBox_vib_loop_times.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out vib_loop_times))
            vib_loop_times = 0;

        this.btn_vibPlay.Enabled     = false;
        this.btn_loadVib.Enabled     = false;
        this.groupBox_vib_eq.Enabled = false;
        this.btn_vibPlay.Text = "Playing...";


        play_hd_rumble_file(vib_file_type, vib_sample_rate, (int)vib_samples, (int)vib_loop_start, (int)vib_loop_end, (int)vib_loop_wait, vib_loop_times);

        this.btn_vibPlay.Text = "Play";
        this.btn_vibPlay.Enabled = true;
        this.btn_loadVib.Enabled = true;
        if (vib_file_type == 2 || vib_file_type == 3 || vib_file_type == 4) {
            this.groupBox_vib_eq.Enabled = true;
        }
        update_battery();
        update_temperature();

    }
     
    private void btn_vibResetEQ_Click(System.Object  sender, System.EventArgs  e) {
        this.trackBar_lf_amp.Value = 10;
        this.trackBar_lf_freq.Value = 10;
        this.trackBar_hf_amp.Value = 10;
        this.trackBar_hf_freq.Value = 10;
    }

    private void TrackBar_ValueChanged(System.Object sender, System.EventArgs e)
    {
        lf_gain  = this.trackBar_lf_amp.Value / 10.0f;
        lf_pitch = this.trackBar_lf_freq.Value / 10.0f;
        hf_gain  = this.trackBar_hf_amp.Value / 10.0f;
        hf_pitch = this.trackBar_hf_freq.Value / 10.0f;
        this.toolTip1.SetToolTip(this.trackBar_lf_amp,  String.Format("{0:d}%", (int)(lf_gain * 100.01f)));
        this.toolTip1.SetToolTip(this.trackBar_lf_freq, String.Format("{0:d}%", (int)(lf_pitch * 100.01f)));
        this.toolTip1.SetToolTip(this.trackBar_hf_amp,  String.Format("{0:d}%", (int)(hf_gain * 100.01f)));
        this.toolTip1.SetToolTip(this.trackBar_hf_freq, String.Format("{0:d}%", (int)(hf_pitch * 100.01f)));
        vib_converted = 0;
    }

    private void btn_runBtnTest_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (!enable_button_test) {
            this.btn_runBtnTest.Text = "Turn off";
            enable_button_test = true;
            button_test();
        }
        else {
            this.btn_runBtnTest.Text = "Turn on";
            enable_button_test = false;
        }

    }

    private void btn_spiCancel_Click(System.Object  sender, System.EventArgs  e) {
        cancel_spi_dump = true;
    }

    private void toolStripLabel_temp_Click(System.Object  sender, System.EventArgs  e) {
        if (temp_celsius)
            temp_celsius = false;
        else
            temp_celsius = true;

        update_temperature();
    }

    private void toolStripBtn_Disconnect_Click(System.Object  sender, System.EventArgs  e) {
        u8* custom_cmd = stackalloc u8[7];
        memset(custom_cmd, 0, 7);
        custom_cmd[0] = 0x01;
        custom_cmd[5] = 0x06;
        custom_cmd[6] = 0x00;
        send_custom_command(custom_cmd);
        this.toolStripBtn_Disconnect.Enabled = false;
        this.toolStripLabel_temp.Enabled     = false;
        this.toolStripLabel_batt.Enabled     = false;
        this.toolStripBtn_batt.Enabled       = false;
    }


    private void toolStripBtn_refresh_Click(System.Object  sender, System.EventArgs  e) {
        full_refresh(true);
    }
 

    private void btn_changeNormalColor_Click(System.Object  sender, System.EventArgs  e) {
        changeColorDialog(false);
    }


    private void btn_changeGripsColor_Click(System.Object  sender, System.EventArgs  e) {
        changeColorDialog(true);
    }


    private void changeColorDialog(bool gripsDialog) {
        if (gripsDialog)
            JCColorPicker = new jcColor.JoyConColorPicker(this.jcGripLeftColor, this.jcGripRightColor, gripsDialog);
        else
            JCColorPicker = new jcColor.JoyConColorPicker(this.jcBodyColor, this.jcButtonsColor, gripsDialog);

        System.Drawing.Rectangle screenRectangle = RectangleToScreen(this.ClientRectangle);
        int titleHeight = Math.Max(screenRectangle.Top - this.Top, this.toolStrip1.Height);

        Fonts.Apply(JCColorPicker);
        JCColorPicker.TopLevel = false;
        JCColorPicker.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        JCColorPicker.m_cmd_Cancel.Click += new System.EventHandler(this.Color_Picker_Cancel);
        JCColorPicker.m_cmd_OK.Click += new System.EventHandler(this.Color_Picker_OK);

        if (option_is_on == 5) {
            enable_button_test = false;
            enable_NFCScanning = false;
            this.Controls.Remove(this.grpBox_dev_param);
            this.Controls.Remove(this.grpBox_StickCal);
            this.Controls.Remove(this.grpBox_accGyroCal);
            this.Controls.Remove(this.grpBox_nfc);
        }

        JCColorPicker.Show();
        
        JCColorPicker.Location = new System.Drawing.Point(0,25);
        JCColorPicker.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);

        this.panel_filler.Location = new System.Drawing.Point(JCColorPicker.ClientSize.Width, 25);
        this.panel_filler.Size = new System.Drawing.Size(3, JCColorPicker.ClientSize.Height);
        this.Controls.Add(JCColorPicker);

        this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);

        JCColorPicker.BringToFront();
        JCColorPicker.Padding = new System.Windows.Forms.Padding(0, 0, 0, titleHeight);
        this.menuStrip1.BringToFront();
        this.toolStrip1.BringToFront();
        this.menuStrip1.Enabled = false;
        this.toolStrip1.Enabled = false;
        this.menuStrip1.Refresh();
        this.toolStrip1.Refresh();

        // Load grips color panel outside, so High DPI scaling can work
        JCColorPicker.loadGripsPanel();
    }

    private void Color_Picker_Cancel(System.Object  sender, System.EventArgs  e) {
        this.panel_filler.Location = new System.Drawing.Point(0, 0);
        this.panel_filler.Size = new System.Drawing.Size(0, 0);
        if (option_is_on == 5) {
            this.Controls.Add(this.grpBox_StickCal);
            this.Controls.Add(this.grpBox_dev_param);
            this.Controls.Add(this.grpBox_accGyroCal);
            this.Controls.Add(this.grpBox_nfc);
            this.lbl_nfcHelp.Size = new System.Drawing.Size(203, 85);
            this.txtBox_nfcUid.Size = new System.Drawing.Size(208, 53);
            this.textBox_lstick_fcal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_lstick_ucal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_rstick_fcal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_rstick_ucal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_6axis_cal.Size     = new System.Drawing.Size(156, 88);
            this.textBox_6axis_ucal.Size    = new System.Drawing.Size(156, 88);
            this.txtBox_devParameters.Size  = new System.Drawing.Size(135, 185);
            this.txtBox_devParameters2.Size = new System.Drawing.Size(140, 130);

            this.textBox_btn_test_reply.Size = new System.Drawing.Size(205, 172);
            this.textBox_btn_test_subreply.Size = new System.Drawing.Size(205, 140);

            this.btn_runBtnTest.Text = "Turn on";
            option_is_on = 5;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        this.menuStrip1.Enabled = true;
        this.toolStrip1.Enabled = true;
        this.menuStrip1.Refresh();
        this.toolStrip1.Refresh();
    }
    
    private void Color_Picker_OK(System.Object  sender, System.EventArgs  e) {
        this.panel_filler.Location = new System.Drawing.Point(0, 0);
        this.panel_filler.Size = new System.Drawing.Size(0, 0);
        if (option_is_on == 5) {
            this.Controls.Add(this.grpBox_StickCal);
            this.Controls.Add(this.grpBox_dev_param);
            this.Controls.Add(this.grpBox_accGyroCal);
            this.Controls.Add(this.grpBox_nfc);
            this.lbl_nfcHelp.Size = new System.Drawing.Size(203, 85);
            this.txtBox_nfcUid.Size = new System.Drawing.Size(208, 53);
            this.textBox_lstick_fcal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_lstick_ucal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_rstick_fcal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_rstick_ucal.Size   = new System.Drawing.Size(207, 44);
            this.textBox_6axis_cal.Size     = new System.Drawing.Size(156, 88);
            this.textBox_6axis_ucal.Size    = new System.Drawing.Size(156, 88);
            this.txtBox_devParameters.Size  = new System.Drawing.Size(135, 185);
            this.txtBox_devParameters2.Size = new System.Drawing.Size(140, 130);

            this.textBox_btn_test_reply.Size = new System.Drawing.Size(205, 172);
            this.textBox_btn_test_subreply.Size = new System.Drawing.Size(205, 140);

            this.btn_runBtnTest.Text = "Turn on";
            option_is_on = 5;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        }
        if (!JCColorPicker.GripsColorValue) {
            this.jcBodyColor = JCColorPicker.PrimaryColor;
            this.jcButtonsColor = JCColorPicker.SecondaryColor;
            this.lbl_Body_hex_txt.Text = "Body: #" + String.Format("{0:X6}",
                (jcBodyColor.R << 16) + (jcBodyColor.G << 8) + (jcBodyColor.B));
            this.lbl_Body_hex_txt.Font = (Fonts.Create("Segoe UI", 9, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point,
                (System.Byte)(161)));
            this.lbl_Body_hex_txt.Size = new System.Drawing.Size(128, 24);

            this.lbl_Buttons_hex_txt.Text = "Buttons: #" + String.Format("{0:X6}",
                (jcButtonsColor.R << 16) + (jcButtonsColor.G << 8) + (jcButtonsColor.B));
            this.lbl_Buttons_hex_txt.Font = (Fonts.Create("Segoe UI", 9, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point,
                (System.Byte)(161)));
            this.lbl_Buttons_hex_txt.Size = new System.Drawing.Size(128, 24);
        }
        else {
            this.jcGripLeftColor  = JCColorPicker.PrimaryColor;
            this.jcGripRightColor = JCColorPicker.SecondaryColor;
        }
        update_joycon_color(
            jcBodyColor.R,      jcBodyColor.G,      jcBodyColor.B,
            jcButtonsColor.R,   jcButtonsColor.G,   jcButtonsColor.B,
            jcGripLeftColor.R,  jcGripLeftColor.G,  jcGripLeftColor.B,
            jcGripRightColor.R, jcGripRightColor.G, jcGripRightColor.B
        );

        this.btn_writeColorsToSpi.Enabled = true;

        this.menuStrip1.Enabled = true;
        this.toolStrip1.Enabled = true;

        this.menuStrip1.Refresh();
        this.toolStrip1.Refresh();
    }


    private void fixToolstripOverlap(System.Object  sender, System.EventArgs  e) {
        this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        System.Drawing.Rectangle screenRectangle = RectangleToScreen(this.ClientRectangle);
        int titleHeight = screenRectangle.Top - this.Top;
        // The title bar height stands in for the tool strip's height here. Without a
        // window manager (or with a thin title bar) that is too small, so use at least the strip.
        titleHeight = Math.Max(titleHeight, this.toolStrip1.Height);

        this.grpBox_Color.Margin      = new System.Windows.Forms.Padding(0, 0, 14, titleHeight);
        this.grpBox_StickCal.Margin   = new System.Windows.Forms.Padding(0, 0, 0, titleHeight);
        this.grpBox_IRSettings.Margin = new System.Windows.Forms.Padding(0, 0, 0, titleHeight);
    }


    private void TrackBarIR_ValueChanged(System.Object sender, System.EventArgs e) {
        this.toolTip1.SetToolTip(this.trackBar_IRGain, String.Format("{0:d}x", this.trackBar_IRGain.Value));
    }


    private void TrackBarIRLedsIntensity_ValueChanged(System.Object sender, System.EventArgs e) {
        TrackBar temp = (TrackBar)sender;
        if (temp.Maximum == 15)
            this.toolTip1.SetToolTip((TrackBar)sender, String.Format("{0:d}%", (temp.Value * 100) / 15));
        else
            this.toolTip1.SetToolTip((TrackBar)sender, String.Format("{0:d}%", (temp.Value * 100) / 16));
    }


    private void IRFlashlight_checkedChanged(System.Object sender, System.EventArgs e) {
        if (this.chkBox_IRFlashlight.Checked) {
            this.chkBox_IRExFilter.Enabled     = false;
            this.chkBox_IRStrobe.Enabled       = false;
            this.trackBar_IRBrightLeds.Enabled = false;
            this.trackBar_IRDimLeds.Enabled    = false;
            this.lbl_IRLed1Int.Enabled         = false;
            this.lbl_IRLed2Int.Enabled         = false;
        }
        else {
            this.chkBox_IRExFilter.Enabled     = true;
            this.chkBox_IRStrobe.Enabled       = true;
            this.trackBar_IRBrightLeds.Enabled = true;
            this.trackBar_IRDimLeds.Enabled    = true;
            this.lbl_IRLed1Int.Enabled         = true;
            this.lbl_IRLed2Int.Enabled         = true;
        }
    }


    private void IRLeds_checkedChanged(System.Object sender, System.EventArgs e) {
        if (this.chkBox_IRBrightLeds.Checked) {
            this.trackBar_IRBrightLeds.Enabled = true;
            this.lbl_IRLed1Int.Enabled         = true;
        }
        else {
            this.trackBar_IRBrightLeds.Enabled = false;
            this.lbl_IRLed1Int.Enabled         = false;
        }
        if (this.chkBox_IRDimLeds.Checked) {
            this.trackBar_IRDimLeds.Enabled = true;
            this.lbl_IRLed2Int.Enabled      = true;
        }
        else {
            this.trackBar_IRDimLeds.Enabled = false;
            this.lbl_IRLed2Int.Enabled      = false;
        }
    }


    private void IRDenoise_checkedChanged(System.Object sender, System.EventArgs e) {
        if (this.chkBox_IRDenoise.Checked) {
            this.numeric_IRDenoiseEdgeSmoothing.Enabled = true;
            this.numeric_IRDenoiseColorInterpolation.Enabled = true;
            this.lbl_IRDenoise1.Enabled = true;
            this.lbl_IRDenoise2.Enabled = true;
        }
        else {
            this.numeric_IRDenoiseEdgeSmoothing.Enabled = false;
            this.numeric_IRDenoiseColorInterpolation.Enabled = false;
            this.lbl_IRDenoise1.Enabled = false;
            this.lbl_IRDenoise2.Enabled = false;
        }
    }


    private void IRAutoExposure_checkedChanged(System.Object sender, System.EventArgs e) {
        if (this.chkBox_IRAutoExposure.Checked) {
            if (this.radioBtn_IR30p.Checked)
                this.radioBtn_IR60p.Checked = true;
            this.radioBtn_IR30p.Enabled     = false;
            this.trackBar_IRGain.Enabled    = false;
            this.numeric_IRExposure.Enabled = false;
            this.lbl_digitalGain.Enabled    = false;
            this.lbl_exposure.Enabled       = false;
        }
        else {
            this.radioBtn_IR30p.Enabled     = true;
            this.trackBar_IRGain.Enabled    = true;
            this.numeric_IRExposure.Enabled = true;
            this.lbl_digitalGain.Enabled    = true;
            this.lbl_exposure.Enabled       = true;
        }
    }

 
    private int prepareSendIRConfig(bool startNewConfig) {
        string error_msg = null;
        ir_image_config ir_new_config = new ir_image_config();
        int res = 0;

        trace_note("IR: configure (" + (startNewConfig ? "new run" : "live change") + ")");
        this.lbl_IRStatus.Text = "Status: Configuring";
        Application.DoEvents();
        trace_note("IR: window events processed");

        // The IR camera lens has a FoV of 123�. The IR filter is a NIR 850nm wavelength pass filter.

        // Resolution config register and no of packets expected
        // The sensor supports a max of Binning [4 x 2] and max Skipping [4 x 4]
        // The maximum reduction in resolution is a combined Binning/Skipping [16 x 8]
        // The bits control the matrices used. Skipping [Bits0,1 x Bits2,3], Binning [Bits4,5 x Bit6]. Bit7 is unused.
        if (startNewConfig) {
            if (this.radioBtn_IR240p.Checked) {
                ir_image_width  = 320;
                ir_image_height = 240;
                ir_new_config.ir_res_reg = 0b00000000; // Full pixel array
                ir_max_frag_no  = 0xff;
            }
            else if (this.radioBtn_IR120p.Checked) {
                ir_image_width  = 160;
                ir_image_height = 120;
                ir_new_config.ir_res_reg = 0b01010000; // Sensor Binning [2 X 2]
                ir_max_frag_no  = 0x3f;
            }
            else if (this.radioBtn_IR60p.Checked) {
                ir_image_width  = 80;
                ir_image_height = 60;
                ir_new_config.ir_res_reg = 0b01100100; // Sensor Binning [4 x 2] and Skipping [1 x 2]
                ir_max_frag_no  = 0x0f;
            }
            else if (this.radioBtn_IR30p.Checked) {
                ir_image_width  = 40;
                ir_image_height = 30;
                ir_new_config.ir_res_reg = 0b01101001; // Sensor Binning [4 x 2] and Skipping [2 x 4]
                ir_max_frag_no  = 0x03;
            }

            if (this.radioBtn_IRModeCapture.Checked) {
                ir_new_config.ir_mode = 0x07;
            }
            else if (this.radioBtn_IRModePointing.Checked) {
                ir_new_config.ir_mode = 0x04;
                ir_image_width = 320;
                ir_image_height = 240;
            }
            else if (this.radioBtn_IRModeClustering.Checked) {
                ir_new_config.ir_mode = 0x06;
                ir_image_width = 320;
                ir_image_height = 240;
            }
            else {
                return 8;
            }
        }

        // Enable IR Leds. Only the following configurations are supported.
        if (this.chkBox_IRBrightLeds.Checked == true && this.chkBox_IRDimLeds.Checked == true)
            ir_new_config.ir_leds = 0b000000; // Both Far/Narrow 75\u00B0 and Near/Wide 130\u00B0 Led groups are enabled.
        else if (this.chkBox_IRBrightLeds.Checked == true && this.chkBox_IRDimLeds.Checked == false)
            ir_new_config.ir_leds = 0b100000; // Only Far/Narrow 75\u00B0 Led group is enabled.
        else if (this.chkBox_IRBrightLeds.Checked == false && this.chkBox_IRDimLeds.Checked == true)
            ir_new_config.ir_leds = 0b010000; // Only Near/Wide 130� Led group is enabled.
        else if (this.chkBox_IRBrightLeds.Checked == false && this.chkBox_IRDimLeds.Checked == false)
            ir_new_config.ir_leds = 0b110000; // Both groups disabled

        // IR Leds Intensity
        ir_new_config.ir_leds_intensity = (u16)(((u8)this.trackBar_IRBrightLeds.Value << 8) | (u8)this.trackBar_IRDimLeds.Value);

        // IR Leds Effects
        if (this.chkBox_IRFlashlight.Checked)
            ir_new_config.ir_leds |= 0b01;
        if (this.chkBox_IRStrobe.Checked)
            ir_new_config.ir_leds |= 0b10000000;

        // External Light filter (Dark-frame subtraction). Additionally, disable if leds in flashlight mode.
        if ((this.chkBox_IRExFilter.Checked || this.chkBox_IRStrobe.Checked) && this.chkBox_IRExFilter.Enabled)
            ir_new_config.ir_ex_light_filter = 0x03;
        else
            ir_new_config.ir_ex_light_filter = 0x00;

        // Flip image. Food for tracking camera movement when taking selfies :P
        if (this.chkBox_IRSelfie.Checked)
            ir_new_config.ir_flip = 0x02;
        else
            ir_new_config.ir_flip = 0x00;

        // Exposure time (Shutter speed) is in us. Valid values are 0 to 600us or 0 - 1/1666.66s
        ir_new_config.ir_exposure = (u16)(this.numeric_IRExposure.Value * 31200 / 1000);
        if (!this.chkBox_IRAutoExposure.Checked && enable_IRVideoPhoto) {
            enable_IRAutoExposure = false;
            ir_new_config.ir_digital_gain = (u8)this.trackBar_IRGain.Value;
        }
        else {
            enable_IRAutoExposure = true;
            ir_new_config.ir_digital_gain = 1; // Disable digital gain for auto exposure
        }
        

        //De-noise algorithms
        if (this.chkBox_IRDenoise.Checked)
            ir_new_config.ir_denoise = 0x01 << 16;
        else
            ir_new_config.ir_denoise = 0x00 << 16;
        ir_new_config.ir_denoise |= (u32)(((u8)this.numeric_IRDenoiseEdgeSmoothing.Value & 0xFF) << 8);
        ir_new_config.ir_denoise |= (u32)((u8)this.numeric_IRDenoiseColorInterpolation.Value & 0xFF);

        // Initialize camera
        if (startNewConfig) {
            // Configure the IR camera and take a photo or stream.
            // Linux: on its own thread, so UI stalls (XWayland) can't hold up the transfer.
            ir_exposure_value = (int)this.numeric_IRExposure.Value;
            ir_image_config cfg = ir_new_config;
            res = ir_run_worker(() => ir_sensor(ref cfg));

            // Linux: the camera occasionally keeps its previous settings (e.g. resolution). Set it
            // up again and capture (or stream) once more; if it still didn't apply them, say so.
            // A stream stopped by the user in the meantime isn't restarted.
            bool streaming = enable_IRVideoPhoto;
            if (res == 0 && ir_last_capture_stale && (!streaming || enable_IRVideoPhoto)) {
                trace_note("IR: camera didn't apply the settings, setting it up again");
                this.lbl_IRStatus.Text = "Status: Camera didn't apply the settings, retrying..";
                res = ir_run_worker(() => ir_sensor(ref cfg));
                if (res == 0 && ir_last_capture_stale)
                    res = 10;
            }

            // Get error
            switch (res) {
                case 1:
                    error_msg = "1ID31";
                    break;
                case 2:
                    error_msg = "2MCUON";
                    break;
                case 3:
                    error_msg = "3MCUONBUSY";
                    break;
                case 4:
                    error_msg = "4MCUMODESET";
                    break;
                case 5:
                    error_msg = "5MCUSETBUSY";
                    break;
                case 6:
                    error_msg = "6IRMODESET";
                    break;
                case 7:
                    error_msg = "7IRSETBUSY";
                    break;
                case 8:
                    error_msg = "8IRCFG";
                    break;
                case 9:
                    error_msg = "9IRFCFG";
                    break;
                case 10:
                    error_msg = "10IRNOCFG";
                    break;
                default:
                    break;
            }
            if (res == 10)
                this.lbl_IRStatus.Text = "Status: Camera didn't apply the settings. Try again";
            else if (res > 0)
                this.lbl_IRStatus.Text = "Status: Error " + error_msg + "!";
        }
        // Change camera configuration
        else {
            ir_new_config.ir_custom_register = (u32)(((u16)this.numeric_IRCustomRegAddr.Value) | ((u8)this.numeric_IRCustomRegVal.Value << 16));
            ir_image_config cfg = ir_new_config;
            res = ir_run_on_device(() => ir_sensor_config_live(ref cfg));
        }

        return res;
    }
    
    public void setIRPictureWindow(u8* buf_image, bool ir_video_photo, bool? save = null) {
        Bitmap MyImage = new Bitmap(ir_image_width, ir_image_height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        int buf_pos = 0;

        // Skip slow SetPixel(). Reduce latency pixel set latency from 842us . 260ns.
        System.Drawing.Imaging.BitmapData bmd = MyImage.LockBits(new System.Drawing.Rectangle(0, 0, ir_image_width, ir_image_height), System.Drawing.Imaging.ImageLockMode.WriteOnly, MyImage.PixelFormat);
        int PixelSize = 3;

        for (int y = 0; y < ir_image_height; y++) {
            byte* row = (byte *)bmd.Scan0.ToPointer() + (y * bmd.Stride);
            for (int x = 0; x < ir_image_width; x++) {
                // Ironbow Palette
                if (this.radioBtn_IRColorHeat.Checked) {
                    // Values are in BGR in memory. Here in RGB order.
                    row[x * PixelSize + 2] = (u8)((Tables.iron_palette[buf_image[x + buf_pos]] >> 16)&0xFF);
                    row[x * PixelSize + 1] = (u8)((Tables.iron_palette[buf_image[x + buf_pos]] >> 8) & 0xFF);
                    row[x * PixelSize]     =  (u8)(Tables.iron_palette[buf_image[x + buf_pos]] & 0xFF);
                }
                // Greyscale
                else if (this.radioBtn_IRColorGrey.Checked) {
                    // Values are in BGR in memory. Here in RGB order.
                    row[x * PixelSize + 2] = buf_image[x + buf_pos];
                    row[x * PixelSize + 1] = buf_image[x + buf_pos];
                    row[x * PixelSize]     = buf_image[x + buf_pos];
                }
                // Night vision
                else if (this.radioBtn_IRColorGreen.Checked) {
                    // Values are in BGR in memory. Here in RGB order.
                    row[x * PixelSize + 2] = 0;
                    row[x * PixelSize + 1] = buf_image[x + buf_pos];
                    row[x * PixelSize]     = 0;
                }
                // Red vision
                else {
                    // Values are in BGR in memory. Here in RGB order.
                    row[x * PixelSize + 2] = buf_image[x + buf_pos];
                    row[x * PixelSize + 1] = 0;
                    row[x * PixelSize]     = 0;
                }
            }
            buf_pos += ir_image_width;
        }
        MyImage.UnlockBits(bmd);

        Image rotatedImage = (Image)(MyImage);
        rotatedImage.RotateFlip(RotateFlipType.Rotate90FlipNone);

        if (save ?? !enable_IRVideoPhoto)
            rotatedImage.Save("IRcamera.png", System.Drawing.Imaging.ImageFormat.Png);

        Image resizedImage = new Bitmap(240, 320);
        using (Graphics graphicsHandle = Graphics.FromImage(resizedImage)) {
            graphicsHandle.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphicsHandle.DrawImage(rotatedImage, 0, 0, 240, 320);
        }
        // Linux: free each frame's images now. libgdiplus keeps them in native memory until the
        // GC finalizes them, which piles up while streaming.
        rotatedImage.Dispose();

        this.pictureBoxIR.ClientSize = new System.Drawing.Size(240, 320);
        Image oldImage = this.pictureBoxIR.Image;
        this.pictureBoxIR.Image = resizedImage;
        if (oldImage != null)
            oldImage.Dispose();
        //this.AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
    }

    private void btn_getImage_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        int res;

        this.btn_getIRStream.Enabled = false;
        this.btn_getIRImage.Enabled  = false;
        enable_IRVideoPhoto = false;

        res = prepareSendIRConfig(true);
        
        if (res == 0)
            this.lbl_IRStatus.Text = "Status: Done! Saved to IRcamera.png";

        this.btn_getIRStream.Enabled = true;
        this.btn_getIRImage.Enabled  = true;
    }

    
    private void btn_getVideo_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        int res;

        this.btn_getIRImage.Enabled = false;
        this.grpBox_IRRes.Enabled   = false;
        if (enable_IRVideoPhoto) {
            enable_IRVideoPhoto = false;
            this.btn_getIRStream.Enabled = false;
        }
        else {
            enable_IRVideoPhoto = true;
            this.btn_getIRStream.Text = "Stop";
            this.btn_IRConfigLive.Enabled = true;
            res = prepareSendIRConfig(true);

            enable_IRVideoPhoto = false;
            if (res == 0)
                this.lbl_IRStatus.Text = "Status: Standby";
            this.btn_getIRImage.Enabled  = true;
            this.grpBox_IRRes.Enabled    = true;
            this.btn_getIRStream.Enabled = true;
        }

        
        this.btn_IRConfigLive.Enabled = false;
        this.btn_getIRStream.Text = "Stream";
    }


    private void btn_IRConfigLive_Click(System.Object  sender, System.EventArgs  e) {
        if (enable_IRVideoPhoto)
            prepareSendIRConfig(false);
    }


    private void btn_NFC_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (enable_NFCScanning) {
            this.btn_NFC.Text = "Scan";
            enable_NFCScanning  = false;
        }
        else {
            string error_msg = null;
            this.btn_NFC.Text = "Stop";
            enable_NFCScanning  = true;
            Application.DoEvents();
            int res = nfc_tag_info();

            // Get error
            switch (res) {
                case 1:
                    error_msg = "1ID31";
                    break;
                case 2:
                    error_msg = "2MCUON";
                    break;
                case 3:
                    error_msg = "3MCUONBUSY";
                    break;
                case 4:
                    error_msg = "4MCUMODESET";
                    break;
                case 5:
                    error_msg = "5MCUSETBUSY";
                    break;
                case 6:
                    error_msg = "6NFCPOLL";
                    break;
                default:
                    break;
            }
            if (res > 0)
                this.txtBox_nfcUid.Text = "Error " + error_msg + "!";
        }
        this.btn_NFC.Text = "Scan";
        enable_NFCScanning = false;
    }


    private void btn_refreshUserCal_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        u8* user_stick_cal = stackalloc u8[22];
        u8* user_sensor_cal = stackalloc u8[26];
        u8* stick_model_main_left = stackalloc u8[3];
        u8* stick_model_pro_right = stackalloc u8[3];
        u16* decoded_stick_pair = stackalloc u16[16];
        memset(user_stick_cal,        0, 22);
        memset(user_sensor_cal,       0, 26);
        memset(stick_model_main_left, 0, 3);
        memset(stick_model_pro_right, 0, 3);
        memset(decoded_stick_pair,    0, 32);

        get_spi_data(0x8010, 22, user_stick_cal);
        Sleep(100);
        get_spi_data(0x8026, 26, user_sensor_cal);
        Sleep(100);
        get_spi_data(0x6089, 3, stick_model_main_left);
        if (handle_type == PROCON) {
            Sleep(100);
            get_spi_data(0x609B, 3, stick_model_pro_right);
        }

        // Left stick user cal
        if (handle_type != 2) {
            if (*(u16*)&user_stick_cal[0] == 0xA1B2) {
                // Center X,Y
                decode_stick_params(decoded_stick_pair + 2, user_stick_cal + 5);
                // -Axis X,Y Offset
                decode_stick_params(decoded_stick_pair,     user_stick_cal + 8);
                // +Axis X,Y Offset
                decode_stick_params(decoded_stick_pair + 4, user_stick_cal + 2);

                this.numeric_leftUserCal_x_minus.Value  = decoded_stick_pair[2] - decoded_stick_pair[0];
                this.numeric_leftUserCal_x_center.Value = decoded_stick_pair[2];
                this.numeric_leftUserCal_x_plus.Value   = decoded_stick_pair[2] + decoded_stick_pair[4];

                this.numeric_leftUserCal_y_minus.Value  = decoded_stick_pair[3] - decoded_stick_pair[1];
                this.numeric_leftUserCal_y_center.Value = decoded_stick_pair[3];
                this.numeric_leftUserCal_y_plus.Value   = decoded_stick_pair[3] + decoded_stick_pair[5];

                this.checkBox_enableLeftUserCal.Checked = true;
            }
            else
                this.checkBox_enableLeftUserCal.Checked = false;

            grpBox_leftStickUCal.Enabled = true;
        }
        else
            grpBox_leftStickUCal.Enabled = false;
        // Right stick user cal
        if (handle_type != 1) {
            if (*(u16*)&user_stick_cal[0xB] == 0xA1B2) {
                // Center X,Y
                decode_stick_params(decoded_stick_pair + 8, user_stick_cal + 13);
                // -Axis X,Y Offset
                decode_stick_params(decoded_stick_pair + 6, user_stick_cal + 16);
                // +Axis X,Y Offset
                decode_stick_params(decoded_stick_pair + 10, user_stick_cal + 19);

                this.numeric_rightUserCal_x_minus.Value  = decoded_stick_pair[8] - decoded_stick_pair[6];
                this.numeric_rightUserCal_x_center.Value = decoded_stick_pair[8];
                this.numeric_rightUserCal_x_plus.Value   = decoded_stick_pair[8] + decoded_stick_pair[10];

                this.numeric_rightUserCal_y_minus.Value  = decoded_stick_pair[9] - decoded_stick_pair[7];
                this.numeric_rightUserCal_y_center.Value = decoded_stick_pair[9];
                this.numeric_rightUserCal_y_plus.Value   = decoded_stick_pair[9] + decoded_stick_pair[11];

                this.checkBox_enableRightUserCal.Checked = true;
            }
            else
                this.checkBox_enableRightUserCal.Checked = false;

            grpBox_rightStickUCal.Enabled = true;
        }
        else
            grpBox_rightStickUCal.Enabled = false;

        // Acc/Gyro user cal
        if (*(u16*)&user_sensor_cal[0] == 0xA1B2) {
            this.numeric_CalEditAccX.Value = *(u16*)&user_sensor_cal[2];
            this.numeric_CalEditAccY.Value = *(u16*)&user_sensor_cal[4];
            this.numeric_CalEditAccZ.Value = *(u16*)&user_sensor_cal[6];

            this.numeric_CalEditGyroX.Value = *(u16*)&user_sensor_cal[14];
            this.numeric_CalEditGyroY.Value = *(u16*)&user_sensor_cal[16];
            this.numeric_CalEditGyroZ.Value = *(u16*)&user_sensor_cal[18];

            this.checkBox_enableSensorUserCal.Checked = true;
        }
        else
            this.checkBox_enableSensorUserCal.Checked = false;
        grpBox_CalUserAcc.Enabled = true;

        // Stick device parameters. These come from factory.
        decode_stick_params(decoded_stick_pair + 12, stick_model_main_left);
        this.numeric_StickParamDeadzone.Value   = decoded_stick_pair[12];
        this.numeric_StickParamRangeRatio.Value = decoded_stick_pair[13];
        this.numeric_StickParamDeadzone2.Enabled   = false;
        this.numeric_StickParamRangeRatio2.Enabled = false;
        this.lbl_proStickHelp.Enabled              = false;
        if (handle_type == PROCON) {
            decode_stick_params(decoded_stick_pair + 14, stick_model_pro_right);
            this.numeric_StickParamDeadzone2.Value   = decoded_stick_pair[14];
            this.numeric_StickParamRangeRatio2.Value = decoded_stick_pair[15];
            this.numeric_StickParamDeadzone2.Enabled   = true;
            this.numeric_StickParamRangeRatio2.Enabled = true;
            this.lbl_proStickHelp.Enabled              = true;
        }
        grpBox_StickDevParam.Enabled = true;
        
        // Enable write buttons
        this.btn_writeStickParams.Enabled = true;
        this.btn_writeUserCal.Enabled = true;
    }


    internal void RefreshUserCal() { btn_refreshUserCal_Click(null, EventArgs.Empty); }
    internal int[] UserCalFields(bool left) {
        NumericUpDown[] f = left
            ? new[] { numeric_leftUserCal_x_minus, numeric_leftUserCal_x_center, numeric_leftUserCal_x_plus,
                      numeric_leftUserCal_y_minus, numeric_leftUserCal_y_center, numeric_leftUserCal_y_plus }
            : new[] { numeric_rightUserCal_x_minus, numeric_rightUserCal_x_center, numeric_rightUserCal_x_plus,
                      numeric_rightUserCal_y_minus, numeric_rightUserCal_y_center, numeric_rightUserCal_y_plus };
        return Array.ConvertAll(f, n => (int)n.Value);
    }

    // r: min, center, max for X then Y (raw); for the self-test of the Manual tab
    internal void apply_stick_cal(bool left, int[] r) {
        NumericUpDown[] f = left
            ? new[] { numeric_leftUserCal_x_minus, numeric_leftUserCal_x_center, numeric_leftUserCal_x_plus,
                      numeric_leftUserCal_y_minus, numeric_leftUserCal_y_center, numeric_leftUserCal_y_plus }
            : new[] { numeric_rightUserCal_x_minus, numeric_rightUserCal_x_center, numeric_rightUserCal_x_plus,
                      numeric_rightUserCal_y_minus, numeric_rightUserCal_y_center, numeric_rightUserCal_y_plus };
        for (int i = 0; i < 6; i++)
            f[i].Value = Math.Max(f[i].Minimum, Math.Min(f[i].Maximum, r[i]));
        (left ? checkBox_enableLeftUserCal : checkBox_enableRightUserCal).Checked = true;
        btn_writeUserCal.Enabled = true;
    }

    private void btn_writeUserCal_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (MessageBox.Show("Are you sure you want to continue?",
            "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes) {
            int res = write_user_cal_fields();
            if (res == 0)
                MessageBox.Show("The user calibration was written to SPI!", "CTCaer's Joy-Con Toolkit - Write Success!", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
            else
                MessageBox.Show("Failed to write user calibration to SPI!\n\nPlease try again..", "CTCaer's Joy-Con Toolkit - Write Failed!", MessageBoxButtons.OK, MessageBoxIcon.Stop);
        }
    }

    // Writes the editor's user calibration fields (Linux: split out of the button, for the self-test).
    internal int write_user_cal_fields() {
        {
            u8* user_stick_cal = stackalloc u8[22];
            u8* user_sensor_cal = stackalloc u8[26];
            u16* decoded_stick_pair = stackalloc u16[2];
            memset(user_stick_cal,     0, 22);
            memset(user_sensor_cal,    0, 26);
            memset(decoded_stick_pair, 0, 4);

            if (handle_type != 2 && this.checkBox_enableLeftUserCal.Checked) {
                *(u16*)&user_stick_cal[0] = 0xA1B2;
                // Center X,Y
                decoded_stick_pair[0] = (u16)this.numeric_leftUserCal_x_center.Value;
                decoded_stick_pair[1] = (u16)this.numeric_leftUserCal_y_center.Value;
                encode_stick_params(user_stick_cal + 5, decoded_stick_pair);
                // +Axis X,Y
                decoded_stick_pair[0] = (u16)((u16)this.numeric_leftUserCal_x_plus.Value - (u16)this.numeric_leftUserCal_x_center.Value);
                decoded_stick_pair[1] = (u16)((u16)this.numeric_leftUserCal_y_plus.Value - (u16)this.numeric_leftUserCal_y_center.Value);
                encode_stick_params(user_stick_cal + 2, decoded_stick_pair);
                // -Axis X,Y
                decoded_stick_pair[0] = (u16)((u16)this.numeric_leftUserCal_x_center.Value - (u16)this.numeric_leftUserCal_x_minus.Value);
                decoded_stick_pair[1] = (u16)((u16)this.numeric_leftUserCal_y_center.Value - (u16)this.numeric_leftUserCal_y_minus.Value);
                encode_stick_params(user_stick_cal + 8, decoded_stick_pair);
            }
            else {
                // Erase left stick user cal
                memset(user_stick_cal, 0xFF, 11);
            }
            if (handle_type != 1 && this.checkBox_enableRightUserCal.Checked) {
                *(u16*)&user_stick_cal[11] = 0xA1B2;
                // Center X,Y
                decoded_stick_pair[0] = (u16)this.numeric_rightUserCal_x_center.Value;
                decoded_stick_pair[1] = (u16)this.numeric_rightUserCal_y_center.Value;
                encode_stick_params(user_stick_cal + 13, decoded_stick_pair);
                // +Axis X,Y
                decoded_stick_pair[0] = (u16)((u16)this.numeric_rightUserCal_x_plus.Value - (u16)this.numeric_rightUserCal_x_center.Value);
                decoded_stick_pair[1] = (u16)((u16)this.numeric_rightUserCal_y_plus.Value - (u16)this.numeric_rightUserCal_y_center.Value);
                encode_stick_params(user_stick_cal + 19, decoded_stick_pair);
                // -Axis X,Y
                decoded_stick_pair[0] = (u16)((u16)this.numeric_rightUserCal_x_center.Value - (u16)this.numeric_rightUserCal_x_minus.Value);
                decoded_stick_pair[1] = (u16)((u16)this.numeric_rightUserCal_y_center.Value - (u16)this.numeric_rightUserCal_y_minus.Value);
                encode_stick_params(user_stick_cal + 16, decoded_stick_pair);
            }
            else {
                // Erase right stick user cal
                memset(&user_stick_cal[11], 0xFF, 11);
            }

            if (this.checkBox_enableSensorUserCal.Checked) {
                *(u16*)&user_sensor_cal[0] = 0xA1B2;
                *(u16*)&user_sensor_cal[2] = (u16)this.numeric_CalEditAccX.Value;
                *(u16*)&user_sensor_cal[4] = (u16)this.numeric_CalEditAccY.Value;
                *(u16*)&user_sensor_cal[6] = (u16)this.numeric_CalEditAccZ.Value;

                *(u16*)&user_sensor_cal[14] = (u16)this.numeric_CalEditGyroX.Value;
                *(u16*)&user_sensor_cal[16] = (u16)this.numeric_CalEditGyroY.Value;
                *(u16*)&user_sensor_cal[18] = (u16)this.numeric_CalEditGyroZ.Value;
            }
            else {
                // Erase user sensor cal
                memset(user_sensor_cal, 0xFF, 26);
            }

            int res = write_spi_data(0x8010, 22, user_stick_cal);
            if (res == 0) {
                Sleep(100);
                res = write_spi_data(0x8026, 26, user_sensor_cal);
            }
            update_cal_status();
            return res;
        }
    }


    private void btn_writeStickParams_Click(System.Object  sender, System.EventArgs  e) {
        if (check_if_connected())
            return;

        if (MessageBox.Show("Warning!\n\nThese are stick device parameters coming from factory.\n\nAre you sure you want to continue?",
            "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes) {
            u8* stick_model_main_left = stackalloc u8[3];
            u8* stick_model_pro_right = stackalloc u8[3];
            u16* decoded_stick_pair = stackalloc u16[4];

            memset(stick_model_main_left, 0, 3);
            memset(stick_model_pro_right, 0, 3);
            memset(decoded_stick_pair, 0, 8);

            // Joy-Con's Main stick or Pro's Left Stick
            decoded_stick_pair[0] = (u16)this.numeric_StickParamDeadzone.Value;
            decoded_stick_pair[1] = (u16)this.numeric_StickParamRangeRatio.Value;
            encode_stick_params(stick_model_main_left, decoded_stick_pair);

            // Pro's Right Stick
            if (handle_type == PROCON) {
                decoded_stick_pair[2] = (u16)this.numeric_StickParamDeadzone2.Value;
                decoded_stick_pair[3] = (u16)this.numeric_StickParamRangeRatio2.Value;
                encode_stick_params(stick_model_pro_right, decoded_stick_pair + 2);
            }

            int res = write_spi_data(0x6089, 3, stick_model_main_left);
            if (res == 0 && handle_type == PROCON) {
                Sleep(100);
                res = write_spi_data(0x609B, 3, stick_model_pro_right);
            }
            if (res == 0)
                MessageBox.Show("The Stick Device Parameters were written to SPI!", "CTCaer's Joy-Con Toolkit - Write Success!", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
            else
                MessageBox.Show("Failed to Stick Device Parameters to SPI!\n\nPlease try again..", "CTCaer's Joy-Con Toolkit - Write Failed!", MessageBoxButtons.OK, MessageBoxIcon.Stop);
        }
    }


    private bool check_if_connected() {
        if (device_connection() == 0) {
            this.iRCameraToolStripMenuItem.Enabled = false;
            this.grpBox_nfc.Enabled = false;

            this.textBoxSN.Text = "Disconnected";
            this.textBoxFW.Text = "0.00";
            this.textBoxMAC.Text = "00:00:00:00:00:00";
            this.textBoxDev.Text = "None";
            this.lbl_Buttons_hex_txt.Text = "";
            this.lbl_Body_hex_txt.Text = "";

            this.toolStripBtn_batt.ToolTipText = "";

            if (temp_celsius)
                this.toolStripLabel_temp.Text = String.Format("{0:f1}\u2103 ", 0);
            else
                this.toolStripLabel_temp.Text = String.Format("{0:f1}\u2109 ", 0);
            this.grpBox_Color.Enabled = false;
            this.pictureBoxPreview.Image = null;
            return true;
        }
        else {
            this.grpBox_Color.Enabled = true;
            return false;
        }
    }


    public void show_ntag_contents(u8* ntag_buf, u8 ntag_pages) {
        String ntag_temp_string = "";
        for (int i = 0; i < ntag_pages; i++) {
            ntag_temp_string += String.Format("{0:X2}: ", i);
            for (int j = 0; j < 4; j++)
                ntag_temp_string += String.Format("{0:X2} ", ntag_buf[i * 4 + j]);
            ntag_temp_string += "|";
            for (int j = 0; j < 4; j++) {
                if (ntag_buf[i * 4 + j] < 0x20 || ntag_buf[i * 4 + j] > 0x7e)
                    ntag_temp_string += ".";
                else
                    ntag_temp_string += String.Format("{0:S}", Convert.ToChar(ntag_buf[i * 4 + j]));
            }
            ntag_temp_string += "|";
            if (i != (ntag_pages - 1))
            ntag_temp_string += "\r\n";
            if (i == 4) {
                switch (ntag_pages) {
                    case 45:
                        this.txtBox_nfcUid.Text += "213 ";
                        break;
                    case 135:
                        this.txtBox_nfcUid.Text += "215 ";
                        break;
                    case 231:
                        this.txtBox_nfcUid.Text += "216 ";
                        break;
                    default:
                        this.txtBox_nfcUid.Text += "???";
                        break;
                }
                switch (ntag_buf[16]) {
                    case 0xA5:
                        this.txtBox_nfcUid.Text += "(Amiibo)";
                        break;
                    case 0x01:
                        this.txtBox_nfcUid.Text += "(NDEF)";
                        break;
                    default:
                        break;
                }
            }
        }
        this.txtBox_NFCTag.Text = ntag_temp_string;
    }
    private void proControllerToolStripMenuItem_Click(System.Object sender, System.EventArgs e) {
        this.dropDown_controllerPrioritySelection.Text = "Pro controller";
        handle_priority = PROCON;
        full_refresh(true);
    }
    private void joyConRToolStripMenuItem_Click(System.Object sender, System.EventArgs e) {
        this.dropDown_controllerPrioritySelection.Text = "Joy-Con (R)";
        handle_priority = JOYCON_R;
        full_refresh(true);
    }
    private void joyConLToolStripMenuItem_Click(System.Object sender, System.EventArgs e) {
        this.dropDown_controllerPrioritySelection.Text = "Joy-Con (L)";
        handle_priority = JOYCON_L;
        full_refresh(true);
    }
    private void anyToolStripMenuItem_Click(System.Object sender, System.EventArgs e) {
        this.dropDown_controllerPrioritySelection.Text = "Any";
        handle_priority = NOTHING;
        full_refresh(true);
    }
}
}

using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ZenStates.Core;
using ZenStatesDebugTool.Properties;
using Application = System.Windows.Forms.Application;
using static ZenStates.Core.Cpu;
using Microsoft.Win32.TaskScheduler;
using System.Security.Principal;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm : Form
    {
        //private static readonly int Threads = Convert.ToInt32(Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS"));
        private BackgroundWorker backgroundWorker1;
        private readonly NUMAUtil _numaUtil;
        private readonly Cpu cpu;
        List<SmuAddressSet> matches;
        private readonly Mailbox testMailbox = new Mailbox();
        private readonly string wmiAMDACPI = "AMD_ACPI";
        private readonly string wmiScope = "root\\wmi";
        private readonly string profilesPath;
        private readonly string defaultsPath;
        private ManagementObject classInstance;
        private string instanceName;
        private ManagementBaseObject pack;
        private const string profilesFolderName = "profiles";
        private const string filename = "co_profile.txt";
        private readonly string[] args;
        private readonly bool isApplyProfile;

        public SettingsForm()
        {
            InitializeComponent();
            _numaUtil = new NUMAUtil();
            textBoxResult.Text = $@"Detected NUMA nodes. ({_numaUtil.HighestNumaNode + 1})" + textBoxResult.Text;

            try
            {
                profilesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, profilesFolderName);
                defaultsPath =  Path.Combine(profilesPath, filename);
                
                args = Environment.GetCommandLineArgs();
                foreach (string arg in args)
                {
                    isApplyProfile |= (arg.ToLower() == "--applyprofile");
                }

                cpu = new Cpu();

                InitForm();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.Error);
                Dispose();
                ExitApplication();
            }
        }

        private void ExitApplication()
        {
            cpu?.Dispose();

            if (Application.MessageLoop)
                Application.Exit();
            else
                Environment.Exit(1);
        }

        private void InitTestMailbox(uint msgAddr, uint rspAddr, uint argAddr)
        {
            testMailbox.SMU_ADDR_MSG = msgAddr;
            testMailbox.SMU_ADDR_RSP = rspAddr;
            testMailbox.SMU_ADDR_ARG = argAddr;
            ResetSmuAddresses();
        }

        private void InitTestMailbox(Mailbox mailbox)
        {
            testMailbox.SMU_ADDR_MSG = mailbox.SMU_ADDR_MSG;
            testMailbox.SMU_ADDR_RSP = mailbox.SMU_ADDR_RSP;
            testMailbox.SMU_ADDR_ARG = mailbox.SMU_ADDR_ARG;
            ResetSmuAddresses();
        }

        private void ResetSmuAddresses()
        {
            textBoxCMDAddress.Text = $"0x{Convert.ToString(testMailbox.SMU_ADDR_MSG, 16).ToUpper()}";
            textBoxRSPAddress.Text = $"0x{Convert.ToString(testMailbox.SMU_ADDR_RSP, 16).ToUpper()}";
            textBoxARGAddress.Text = $"0x{Convert.ToString(testMailbox.SMU_ADDR_ARG, 16).ToUpper()}";
        }

        private void DisplaySystemInfo()
        {
            try
            {
                cpuInfoLabel.Text = cpu.systemInfo.CpuName;
                modelInfoLabel.Text = $"{cpu.systemInfo.Model:X2}";
                packageTypeInfoLabel.Text = cpu.info.packageType.ToString();
                mbVendorInfoLabel.Text = cpu.systemInfo.MbVendor;
                mbModelInfoLabel.Text = cpu.systemInfo.MbName;
                biosInfoLabel.Text = cpu.systemInfo.BiosVersion;
                smuInfoLabel.Text = cpu.systemInfo.GetSmuVersionString();
                firmwareInfoLabel.Text = $"{cpu.systemInfo.PatchLevel:X8}";
                cpuIdLabel.Text = $"{cpu.systemInfo.GetCpuIdString()} ({cpu.info.codeName})";
                configInfoLabel.Text = $"{cpu.info.topology.ccds} CCD / {cpu.info.topology.ccxs} CCX / {cpu.systemInfo.PhysicalCoreCount} physical cores";
            }
            catch { }
        }

        private void InitForm()
        {
            /*if (cpu.Status == Utils.LibStatus.PARTIALLY_OK)
            {
                if (cpu.LastError != null)
                    MessageBox.Show(cpu.LastError.Message, Resources.Error);
            }*/

            if (cpu.smu.Version == 0)
            {
                MessageBox.Show("Error getting SMU version!\n" +
                    "Default SMU addresses are not responding to commands.",
                    "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (!Directory.Exists(profilesPath))
            {
                MessageBox.Show("Profiles directory does not exist, created one for you.");
                Directory.CreateDirectory(profilesPath);
            }

            InitTestMailbox(cpu.smu.Rsmu);
            DisplaySystemInfo();

            pstateIdBox.SelectedIndex = 0;

            pstateDid.KeyDown += PstateFidDid_KeyDown;
            pstateDid.KeyPress += PstateFidDid_KeyPress;
            pstateDid.KeyUp += PstateFidDid_KeyUp;
            pstateFid.KeyDown += PstateFidDid_KeyDown;
            pstateFid.KeyPress += PstateFidDid_KeyPress;
            pstateFid.KeyUp += PstateFidDid_KeyUp;

            PopulateFrequencyList(comboBoxACF.Items);
            PopulateFrequencyList(comboBoxSCF.Items);
            PopulateCCDList(comboBoxCore.Items);
            PopulateMailboxesList(comboBoxMailboxSelect.Items);

            comboBoxCore.SelectedIndex = 0;
            double multi = GetCurrentMulti();
            if (multi >= 5.50)
            {
                int index = (int)((multi - 5.50) / 0.25);
                if (index > -1 && index < comboBoxACF.Items.Count && index < comboBoxSCF.Items.Count)
                {
                    comboBoxACF.SelectedIndex = index;
                    comboBoxSCF.SelectedIndex = index;
                }
            }

            InitCoreControl();
            InitPBO();
            PopulateWmiFunctions();

            double? currentBclk = cpu.GetBclk();
            labelBCLK.Text = currentBclk + " MHz";
            numericUpDownBclk.Text = $"{currentBclk}";

            var prochotEnabled = cpu.IsProchotEnabled();
            checkBoxPROCHOT.Checked = prochotEnabled;
            //checkBoxPROCHOT.Enabled = prochotEnabled;
            //buttonApplyPROCHOT.Enabled = prochotEnabled;

            comboBoxMailboxSelect.SelectedIndex = 0;

            ToolTip toolTip = new ToolTip();
            toolTip.SetToolTip(checkBoxPROCHOT, "Disables temperature throttling. Can be useful on extreme cooling.");

            if (isApplyProfile)
            {
                tabControl1.SelectedTab = tabPagePbo;
                BtnLoadCOProfile_Click(null, null);
                ButtonApplyCO_Click(null, null);
            }

            SetStatusText($"{cpu.info.codeName}. Ready.");
        }

        // TODO: Detect OC Mode and return PState freq if on auto
        private double GetCurrentMulti()
        {
            double multi = cpu.GetCoreMulti();
            if (multi == 0)
                SetStatusText($@"Error getting current frequency!");

            return multi;
        }

        private void PopulateFrequencyList(ComboBox.ObjectCollection l)
        {
            for (double multi = 5.5; multi <= 70; multi += 0.25)
            {
                l.Add((object)new FrequencyListItem(multi, string.Format("x{0:0.00}", multi)));
            }
        }

        private void PopulateCCDList(ComboBox.ObjectCollection l)
        {
            int ccxInCcd = cpu.info.family == Cpu.Family.FAMILY_19H ? 1 : 2;
            int coresInCcx = 8 / ccxInCcd;
            for (int core = 0; core < cpu.info.topology.cores; ++core)
                l.Add(new CoreListItem(core / 8, core / coresInCcx, core));
        }

        private void PopulateMailboxesList(ComboBox.ObjectCollection l)
        {
            l.Clear();
            l.Add(new MailboxListItem("RSMU", cpu.smu.Rsmu));
            l.Add(new MailboxListItem("MP1", cpu.smu.Mp1Smu));
            l.Add(new MailboxListItem("HSMP", cpu.smu.Hsmp));
        }

        private void AddMailboxToList(string label, SmuAddressSet addressSet)
        {
            comboBoxMailboxSelect.Items.Add(new MailboxListItem(label, addressSet));
        }

        private void InitCoreControl()
        {
            uint cores = cpu.info.topology.physicalCores;
            //var performanceOfCores = cpu.info.topology.performanceOfCore;
            uint coresPerGroup = 8;
            uint logicalIndexGroup1 = 0;
            uint logicalIndexGroup2 = 0;

            for (uint i = 0; i < cores; i++)
            {
                uint mapIndex = i / coresPerGroup;
                uint coreInGroup = i % coresPerGroup;
                bool isDisabled = ((~cpu.info.topology.coreDisableMap[mapIndex] >> (int)coreInGroup) & 1) == 0;

                if (!isDisabled)
                {
                    try
                    {
                        CheckBox control = (CheckBox)Controls.Find($"checkBox{i}", true)[0];
                        if (control != null)
                        {
                            control.Enabled = true;
                            control.Checked = true;

                            if (mapIndex == 0) // Group 1
                            {
                                control.Tag = $"{logicalIndexGroup1}";
                                //var performanceOfCore = performanceOfCores[logicalIndexGroup1];
                                logicalIndexGroup1++;
                            }
                            else // Group 2
                            {
                                control.Tag = $"{logicalIndexGroup2}";
                                logicalIndexGroup2++;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Error initializing core {i}: {e}");
                    }
                }
            }

            checkBoxSMT.Checked = cpu.systemInfo.SMT;
        }

        private void InitPBO()
        {
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                uint cores = cpu.info.topology.physicalCores;
                for (var i = 0; i < cores; i++)
                {
                    int mapIndex = i < 8 ? 0 : 1;
                    if ((~cpu.info.topology.coreDisableMap[mapIndex] >> i % 8 & 1) == 1)
                    {
                        try
                        {
                            NumericUpDown control = (NumericUpDown)Controls.Find($"numericUpDownCO_{i}", true)[0];
                            if (control != null)
                            {
                                control.Enabled = true;
                                uint coreMask = cpu.MakeCoreMask((uint)i);
                                uint? margin = cpu.GetPsmMarginSingleCore((uint)(((mapIndex << 8) | i % 8 & 0xF) << 20));
                                if (margin != null)
                                    control.Value = Convert.ToDecimal((int)margin);
                            }
                        } catch (Exception e) {
                            Console.WriteLine(e);
                        }
                    }
                }
            }

            /*using (RegistryKey key = Registry.CurrentUser.OpenSubKey
                ("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                if (key != null)
                {
                    checkBoxApplyCOStartup.Checked = key.GetValue("RyzenSDT") != null;
                }
            }*/

            checkBoxApplyCOStartup.Checked = TaskExists("RyzenSDT");
            numericUpDownFmax.Value = cpu.GetFMax();
        }

        private void ApplyFrequencyAllCoreSetting(int frequency)
        {
            if (cpu.SetFrequencyAllCore(Convert.ToUInt32(frequency)))
                SetStatusText(string.Format("Set frequency to {0} MHz!", frequency));
            else
                HandleError("Error setting frequency!");
        }

        private void ApplyFrequencySingleCoreSetting(CoreListItem i, int frequency)
        {
            uint coreMask = Convert.ToUInt32(((i.CCD << 4 | i.CCX % 2 & 15) << 4 | i.CORE % 4 & 15) << 20);
            if (cpu.SetFrequencySingleCore(coreMask, Convert.ToUInt32(frequency)))
                SetStatusText(string.Format("Set core {0} frequency to {1} MHz!", i, frequency));
            else
                HandleError("Error setting frequency!");
        }

        private void EnableOCMode(bool prochotEnabled = true)
        {
            if (cpu.smu.SendSmuCommand(cpu.smu.Rsmu, cpu.smu.Rsmu.SMU_MSG_EnableOcMode, prochotEnabled ? 0U : 0x1000000))
                SetStatusText(prochotEnabled ? "PROCHOT enabled." : "PROCHOT disabled.");
            else
                HandleError("Error setting OC Mode!");
        }

        private void DisableOCMode()
        {
            if (cpu.DisableOcMode() == SMU.Status.OK)
                SetStatusText(string.Format("Set OK!"));
            else
                HandleError("Error disabling OC Mode!");
        }

        private void SetStatusText(string status)
        {
            labelStatus.Text = status;
            Console.WriteLine($"CMD Status: {status}");
        }

        private void SetButtonsState(bool enabled = true)
        {
            buttonApply.Enabled = enabled;
            buttonDefaults.Enabled = enabled;
            buttonProbe.Enabled = enabled;
            buttonPciRead.Enabled = enabled;
            buttonPciScan.Enabled = enabled;
            buttonExport.Enabled = enabled;
            buttonMsrRead.Enabled = enabled;
            buttonMsrScan.Enabled = enabled;
            buttonMsrWrite.Enabled = enabled;
            buttonPMTable.Enabled = enabled;
            buttonSmuLog.Enabled = enabled;

            textBoxCMDAddress.Enabled = enabled;
            textBoxRSPAddress.Enabled = enabled;
            textBoxARGAddress.Enabled = enabled;
            textBoxCMD.Enabled = enabled;
            textBoxARG0.Enabled = enabled;
            textBoxPciAddress.Enabled = enabled;
            textBoxPciValue.Enabled = enabled;
            textBoxPciStartReg.Enabled = enabled;
            textBoxPciEndReg.Enabled = enabled;
            textBoxMsrAddress.Enabled = enabled;
            textBoxMsrEdx.Enabled = enabled;
            textBoxMsrEax.Enabled = enabled;
            textBoxMsrStart.Enabled = enabled;
            textBoxMsrEnd.Enabled = enabled;
            comboBoxMailboxSelect.Enabled = enabled;
            // textBoxResult.Enabled = enabled;
        }

        private void TryConvertToUint(string text, out uint address)
        {
            try
            {
                address = Convert.ToUInt32(text.Trim().ToLower(), 16);
            }
            catch
            {
                throw new ApplicationException("Invalid hexadecimal value.");
            }
        }

        private void HandleError(string message, string title = "Error")
        {
            SetStatusText(Resources.Error);
            MessageBox.Show(message, title);
        }

        private void ShowResultMessageBox(uint data)
        {
            uint[] d = { data };
            ShowResultMessageBox(d);
        }

        private void ShowResultMessageBox(uint[] data)
        {
            string responseString = "";
            string[] hexArray = new string[data.Length];
            string[] decArray = new string[data.Length];
            string[] binArray = new string[data.Length];

            for (var i = 0; i < data.Length; i++)
            {
                hexArray[i] = $"0x{Convert.ToString(data[i], 16).ToUpper()}";
                decArray[i] = $"{Convert.ToString(data[i], 10).ToUpper()}";
                binArray[i] = $"{Convert.ToString(data[i], 2).ToUpper()}";
            }

            responseString += "HEX: " + string.Join(", ", hexArray);
            responseString += Environment.NewLine;
            responseString += "DEC: " + string.Join(", ", decArray);
            responseString += Environment.NewLine;
            responseString += "BIN: " + string.Join(", ", binArray);
            responseString += Environment.NewLine;
            responseString += Environment.NewLine;

            Console.WriteLine($"Response: {responseString}");
            textBoxResult.Text = responseString + textBoxResult.Text;
        }

        private void ShowResult(uint data)
        {
            string responseString =
                $"REG: {textBoxPciAddress.Text.Trim()}" +
                Environment.NewLine +
                $"HEX: 0x{Convert.ToString(data, 16).ToUpper()}" +
                Environment.NewLine +
                $"INT: {Convert.ToString(data, 10).ToUpper()}" +
                Environment.NewLine +
                $"BIN: {Convert.ToString(data, 2).PadLeft(32, '0')}" +
                Environment.NewLine +
                Environment.NewLine;
            Console.WriteLine($"Response: {responseString}");
            textBoxResult.Text = responseString + textBoxResult.Text;
        }

        private void ShowResultForm(string title="Result", string result="No result")
        {
            Invoke(new MethodInvoker(delegate
            {
                var resultForm = new ResultForm();
                resultForm.textBoxFormResult.Text = result;
                resultForm.Text = title;
                resultForm.Show();
            }));
        }

        // TODO: Show all args
        private void ApplySettings()
        {
            try
            {
                uint[] args = ZenStates.Core.Utils.MakeCmdArgs();
                string[] userArgs = textBoxARG0.Text.Trim().Split(',');

                TryConvertToUint(textBoxCMDAddress.Text, out uint addrMsg);
                TryConvertToUint(textBoxRSPAddress.Text, out uint addrRsp);
                TryConvertToUint(textBoxARGAddress.Text, out uint addrArg);
                TryConvertToUint(textBoxCMD.Text, out uint command);

                testMailbox.SMU_ADDR_MSG = addrMsg;
                testMailbox.SMU_ADDR_RSP = addrRsp;
                testMailbox.SMU_ADDR_ARG = addrArg;

                for (var i = 0; i < userArgs.Length; i++)
                {
                    if (i == args.Length)
                        break;

                    TryConvertToUint(userArgs[i], out uint temp);
                    args[i] = temp;
                }
                

                Console.WriteLine("MSG Address:  0x" + Convert.ToString(testMailbox.SMU_ADDR_MSG, 16).ToUpper());
                Console.WriteLine("RSP Address:  0x" + Convert.ToString(testMailbox.SMU_ADDR_RSP, 16).ToUpper());
                Console.WriteLine("ARG0 Address: 0x" + Convert.ToString(testMailbox.SMU_ADDR_ARG, 16).ToUpper());
                Console.WriteLine("ARG0        : 0x" + Convert.ToString(args[0], 16).ToUpper());

                SMU.Status status = cpu.smu.SendSmuCommand(testMailbox, command, ref args);

                if (status == SMU.Status.OK)
                {
                    ShowResultMessageBox(args);
                }

                SetStatusText(GetSMUStatus.GetByType(status));
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message);
            }
        }

        private void ButtonDefaults_Click(object sender, EventArgs e)
        {
            InitTestMailbox(cpu.smu.Rsmu);
            comboBoxMailboxSelect.SelectedIndex = 0;
            textBoxCMD.Value = 1;
            textBoxARG0.Text = "0";
        }

        private void ButtonApply_Click(object sender, EventArgs e)
        {
            try
            {
                ApplySettings();
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message, "Error reading response");
            }
        }

        private void HandlePciReadBtnClick()
        {
            try
            {
                SetStatusText("Reading, please wait...");
                SetButtonsState(false);

                TryConvertToUint(textBoxPciAddress.Text, out uint address);
                uint data = cpu.ReadDword(address);

                textBoxPciValue.Text = $"0x{data:X8}";

                SetButtonsState();
                SetStatusText(GetSMUStatus.GetByType(SMU.Status.OK));
                ShowResult(data);
            }
            catch (ApplicationException ex)
            {
                SetButtonsState();
                HandleError(ex.Message);
            }
        }

        private void HandlePciWriteBtnClick()
        {
            try
            {
                SetStatusText("Writing, please wait...");
                SetButtonsState(false);

                TryConvertToUint(textBoxPciAddress.Text, out uint address);
                TryConvertToUint(textBoxPciValue.Text, out uint data);

                bool res = false;
                if (cpu.WriteDwordEx(cpu.smu.SMU_OFFSET_ADDR, address))
                    res = cpu.WriteDwordEx(cpu.smu.SMU_OFFSET_DATA, data);

                if (res)
                    SetStatusText("Write OK.");
                else
                    SetStatusText(Resources.Error);

                SetButtonsState();
            }
            catch (ApplicationException ex)
            {
                SetButtonsState();
                HandleError(ex.Message);
            }
        }

        private void ButtonPciRead_Click(object sender, EventArgs e)
        {
            HandlePciReadBtnClick();
        }

        private void TextBoxPciAddress_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                HandlePciReadBtnClick();
        }

        private void ButtonPciWrite_Click(object sender, EventArgs e)
        {
            HandlePciWriteBtnClick();
        }

        private void TextBoxPciValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                HandlePciWriteBtnClick();
        }

        private SMU.Status TrySettings(uint msgAddr, uint rspAddr, uint argAddr, uint cmd, uint value)
        {
            uint[] args = new uint[6];
            args[0] = value;

            testMailbox.SMU_ADDR_MSG = msgAddr;
            testMailbox.SMU_ADDR_RSP = rspAddr;
            testMailbox.SMU_ADDR_ARG = argAddr;

            return cpu.smu.SendSmuCommand(testMailbox, cmd, ref args);
        }

        private void ScanSmuRange(uint start, uint end, uint step, uint offset)
        {
            matches = new List<SmuAddressSet>();

            List<KeyValuePair<uint, uint>> temp = new List<KeyValuePair<uint, uint>>();

            while (start <= end)
            {
                uint smuRspAddress = start + offset;
 
                if (cpu.ReadDword(start) != 0xFFFFFFFF)
                {
                    // Send unknown command 0xFF to each pair of this start and possible response addresses
                    if (cpu.WriteDwordEx(start, 0xFF))
                    {
                        Thread.Sleep(10);

                        while (smuRspAddress <= end)
                        {
                            // Expect UNKNOWN_CMD status to be returned if the mailbox works
                            if (cpu.ReadDword(smuRspAddress) == 0xFE)
                            {
                                // Send Get_SMU_Version command
                                if (cpu.WriteDwordEx(start, 0x2))
                                {
                                    Thread.Sleep(10);
                                    if (cpu.ReadDword(smuRspAddress) == 0x1)
                                        temp.Add(new KeyValuePair<uint, uint>(start, smuRspAddress));
                                }
                            }
                            smuRspAddress += step;
                        }
                    }
                }

                start += step;
            }

            if (temp.Count > 0)
            {
                for (var i = 0; i < temp.Count; i++)
                {
                    Console.WriteLine($"{temp[i].Key:X8}: {temp[i].Value:X8}");
                }

                Console.WriteLine();
            }

            List<uint> possibleArgAddresses = new List<uint>();

            foreach (var pair in temp)
            {
                Console.WriteLine($"Testing {pair.Key:X8}: {pair.Value:X8}");

                if (TrySettings(pair.Key, pair.Value, 0xFFFFFFFF, 0x2, 0xFF) == SMU.Status.OK)
                {
                    var smuArgAddress = pair.Value + 4;
                    while (smuArgAddress <= end)
                    {
                        if (cpu.ReadDword(smuArgAddress) == cpu.smu.Version)
                        {
                            possibleArgAddresses.Add(smuArgAddress);
                        }
                        smuArgAddress += step;
                    }
                }

                // Verify the arg address returns correct value (should be test argument + 1)
                foreach (var address in possibleArgAddresses)
                {
                    uint testArg = 0xFAFAFAFA;
                    var retries = 3;

                    while (retries > 0)
                    {
                        testArg++;
                        retries--;

                        // Send test command
                        if (TrySettings(pair.Key, pair.Value, address, 0x1, testArg) == SMU.Status.OK)
                            if (cpu.ReadDword(address) != testArg + 1)
                                retries = -1;
                    }

                    if (retries == 0)
                    {
                        matches.Add(new SmuAddressSet(pair.Key, pair.Value, address));

                        string responseString =
                                $"CMD:  0x{pair.Key:X8}" +
                                Environment.NewLine +
                                $"RSP:  0x{pair.Value:X8}" +
                                Environment.NewLine +
                                $"ARG:  0x{address:X8}" +
                                Environment.NewLine +
                                Environment.NewLine;

                        Invoke(new MethodInvoker(delegate
                        {
                            textBoxResult.Text += responseString;
                        }));

                        break;
                    }
                }
            }
        }

        /*private void ScanSmuRange_old(uint start, uint end, int step, byte offset)
        {
            matches = new List<SmuAddressSet>();

            while (start <= end)
            {
                uint smuRspAddress = start + offset;
                uint smuArgAddress = 0xFFFFFFFF;

                if (cpu.ReadDword(start) != 0xFFFFFFFF)
                {
                    // Check if CMD-RSP pair returns correct status, while using a placeholder ARG address
                    if (TrySettings(start, smuRspAddress, smuArgAddress, testMailbox.SMU_MSG_TestMessage, 0x0) == SMU.Status.OK)
                    {
                        // Send smu version command, so the corresponding ARG0 address changes its value
                        TrySettings(start, smuRspAddress, smuArgAddress, testMailbox.SMU_MSG_GetSmuVersion, 0x0);
                        bool match = false;

                        smuArgAddress = smuRspAddress + 4;

                        // Scan for ARG address
                        while ((smuArgAddress <= end) && !match)
                        {
                            // Check if smu version major is in range
                            var currentRegValue = (cpu.ReadDword(smuArgAddress) & 0x00FF0000) >> 16;
                            Console.WriteLine($"REG: 0x{smuArgAddress:X8} Value: 0x{currentRegValue:X8}");
                            if (currentRegValue > 1 && currentRegValue <= 99)
                            {
                                // Send test message with an argument, using the potential ARG0 address
                                var argValue = (uint)matches.Count * 2 + 99;
                                TrySettings(start, smuRspAddress, smuArgAddress, testMailbox.SMU_MSG_TestMessage, argValue);
                                currentRegValue = cpu.ReadDword(smuArgAddress);
                                Console.WriteLine($"REG: 0x{smuArgAddress:X8} Value: 0x{currentRegValue:X8}");

                                // Check the address for expected value (argument + 1)
                                if (currentRegValue == argValue + 1)
                                {
                                    match = true;
                                    matches.Add(new SmuAddressSet(start, smuRspAddress, smuArgAddress));

                                    string responseString =
                                        $"CMD:  0x{start:X8}" +
                                        Environment.NewLine +
                                        $"RSP:  0x{smuRspAddress:X8}" +
                                        Environment.NewLine +
                                        $"ARG:  0x{smuArgAddress:X8}" +
                                        Environment.NewLine +
                                        Environment.NewLine;

                                    smuArgAddress += 20;

                                    Invoke(new MethodInvoker(delegate
                                    {
                                        textBoxResult.Text += responseString;
                                    }));
                                }
                            }

                            smuArgAddress += 0x4;
                        }
                    }
                }

                start += (uint)step;
            }
        }*/

        private void RunBackgroundTask(DoWorkEventHandler task, RunWorkerCompletedEventHandler completedHandler)
        {
            try
            {
                SetButtonsState(false);
                textBoxResult.Clear();

                backgroundWorker1 = new BackgroundWorker();
                backgroundWorker1.DoWork += task;
                backgroundWorker1.RunWorkerCompleted += completedHandler;
                backgroundWorker1.RunWorkerAsync();
            }
            catch (ApplicationException ex)
            {
                SetStatusText(Resources.Error);
                SetButtonsState();
                HandleError(ex.Message);
            }
        }

        private void BackgroundWorkerTrySettings_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning SMU addresses, please wait...");
                }));

                switch (cpu.info.codeName)
                {
                    case Cpu.CodeName.BristolRidge:
                        //ScanSmuRange(0x13000000, 0x13000F00, 4, 0x10);
                        break;
                    case Cpu.CodeName.RavenRidge:
                    case Cpu.CodeName.Picasso:
                    case Cpu.CodeName.FireFlight:
                    case Cpu.CodeName.Dali:
                    case Cpu.CodeName.Renoir:
                        ScanSmuRange(0x03B10500, 0x03B10998, 8, 0x3C);
                        ScanSmuRange(0x03B10A00, 0x03B10AFF, 4, 0x60);
                        break;
                    case Cpu.CodeName.PinnacleRidge:
                    case Cpu.CodeName.SummitRidge:
                    case Cpu.CodeName.Matisse:
                    case Cpu.CodeName.Whitehaven:
                    case Cpu.CodeName.Naples:
                    case Cpu.CodeName.Colfax:
                    case Cpu.CodeName.Vermeer:
                    //case Cpu.CodeName.Raphael:
                        ScanSmuRange(0x03B10500, 0x03B10998, 8, 0x3C);
                        ScanSmuRange(0x03B10500, 0x03B10AFF, 4, 0x4C);
                        break;
                    case Cpu.CodeName.Raphael:
                        ScanSmuRange(0x03B10500, 0x03B10998, 8, 0x3C);
                        // ScanSmuRange(0x03B10500, 0x03B10AFF, 4, 0x4C);
                        break;
                    case Cpu.CodeName.Rome:
                        ScanSmuRange(0x03B10500, 0x03B10AFF, 4, 0x4C);
                        break;
                    default:
                        break;
                }
            }
            catch (ApplicationException)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    SetStatusText(Resources.Error);
                }));
            }
        }

        private void ButtonScan_Click(object sender, EventArgs e)
        {
            var confirmResult = MessageBox.Show(
                "The scan process might crash your system or have other unexpected results. " +
                Environment.NewLine +
                "It could take up to 1 minute, depending on the system and current workload." +
                Environment.NewLine +
                "Do you want to continue?",
                "Confirm Scan",
                MessageBoxButtons.OKCancel
            );

            if (confirmResult == DialogResult.OK)
                RunBackgroundTask(BackgroundWorkerTrySettings_DoWork, SmuScan_WorkerCompleted);
        }

        private void TabControl1_Selected(object sender, TabControlEventArgs e)
        {
            if (e.TabPage == tabPageInfo)
                splitContainer1.Panel2Collapsed = true;
            else if (splitContainer1.Panel2Collapsed)
                splitContainer1.Panel2Collapsed = false;
        }

        public string GenerateReportJson()
        {
            StringWriter sw = new StringWriter();
            JsonTextWriter writer = new JsonTextWriter(sw)
            {
                Formatting = Formatting.Indented
            };

            // {
            writer.WriteStartObject();

            writer.WritePropertyName("AppVersion");
            writer.WriteValue(Application.ProductVersion);

            writer.WritePropertyName("OSVersion");
            writer.WriteValue(new ComputerInfo().OSFullName);

            Type type = cpu.systemInfo.GetType();
            PropertyInfo[] properties = type.GetProperties();

            foreach (PropertyInfo property in properties)
            {
                writer.WritePropertyName(property.Name);
                if (property.Name == "CpuId" || property.Name == "PatchLevel")
                    writer.WriteValue($"{property.GetValue(cpu.systemInfo, null):X8}");
                else if (property.Name == "SmuVersion")
                    writer.WriteValue(cpu.systemInfo.GetSmuVersionString());
                else
                    writer.WriteValue(property.GetValue(cpu.systemInfo, null));
            }

            // "SmuAddresses:"
            writer.WritePropertyName("Mailboxes");
            writer.WriteStartArray();
            foreach (SmuAddressSet set in matches)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("MsgAddress");
                writer.WriteValue($"0x{set.MsgAddress:X8}");
                writer.WritePropertyName("RspAddress");
                writer.WriteValue($"0x{set.RspAddress:X8}");
                writer.WritePropertyName("ArgAddress");
                writer.WriteValue($"0x{set.ArgAddress:X8}");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // }
            writer.WriteEndObject();

            sw.Close();

            return sw.ToString();
        }

        private void BackgroundWorkerReport_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            string unixTimestamp = Convert.ToString((DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalMinutes);
            string fileName = $@"SMUDebug_{unixTimestamp}.json";

            if (File.Exists(fileName))
                File.Delete(fileName);

            using (var sw = new StreamWriter(fileName, true))
            {
                sw.WriteLine(GenerateReportJson());
            }

            //ResetSmuAddresses();
            SetButtonsState();
            SetStatusText("Report Complete.");
            MessageBox.Show($"Report saved as {fileName}");
        }

        public static void CalculatePstateDetails(uint eax, ref uint IddDiv, ref uint IddVal, ref uint CpuVid, ref uint CpuDfsId, ref uint CpuFid)
        {
            IddDiv = eax >> 30;
            IddVal = eax >> 22 & 0xFF;
            CpuVid = eax >> 14 & 0xFF;
            CpuDfsId = eax >> 8 & 0x3F;
            CpuFid = eax & 0xFF;
        }

        private void ButtonExport_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(BackgroundWorkerTrySettings_DoWork, BackgroundWorkerReport_RunWorkerCompleted);
        }

        private bool nonNumberEntered;

        private void PstateFidDid_KeyDown(object sender, KeyEventArgs e)
        {
            nonNumberEntered = false;

            if (e.KeyCode < Keys.D0 || e.KeyCode > Keys.D9)
            {
                if (e.KeyCode < Keys.NumPad0 || e.KeyCode > Keys.NumPad9)
                {
                    if (e.KeyCode != Keys.Back)
                    {
                        nonNumberEntered = true;
                    }
                }
            }

            if (ModifierKeys == Keys.Shift)
            {
                nonNumberEntered = true;
            }
        }

        private void PstateFidDid_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (nonNumberEntered)
            {
                e.Handled = true;
            }
        }

        private void PstateFidDid_KeyUp(object sender, KeyEventArgs e)
        {
            var fid = string.IsNullOrEmpty(pstateFid.Text) ? 0 : int.Parse(pstateFid.Text);
            var did = string.IsNullOrEmpty(pstateDid.Text) ? 1 : int.Parse(pstateDid.Text);
            pstateFrequency.Text = (fid * 25 / (did * 12.5)) * 100 + "MHz";
        }

        private void BtnPstateRead_Click(object sender, EventArgs e)
        {
            uint eax = default, edx = default;
            var pstateId = pstateIdBox.SelectedIndex;
            if (!cpu.ReadMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), ref eax, ref edx))
            {
                SetStatusText($@"Error reading PState {pstateId}!");
                return;
            }

            uint IddDiv = 0x0;
            uint IddVal = 0x0;
            uint CpuVid = 0x0;
            uint CpuDfsId = 0x0;
            uint CpuFid = 0x0;

            CalculatePstateDetails(eax, ref IddDiv, ref IddVal, ref CpuVid, ref CpuDfsId, ref CpuFid);

            pstateDid.Text = Convert.ToString(CpuDfsId, 10);
            pstateFid.Text = Convert.ToString(CpuFid, 10);
            pstateFrequency.Text = (CpuFid * 25 / (CpuDfsId * 12.5)) * 100 + "MHz";

            SetStatusText($@"PState {pstateId} successfully read.");

            pstateDid.ReadOnly = false;
            pstateFid.ReadOnly = false;
            btnPstateWrite.Enabled = true;
        }

        private void BtnPstateWrite_Click(object sender, EventArgs e)
        {
            var confirmResult = MessageBox.Show(
                @"This will change the selected PState and your CPU frequency." +
                Environment.NewLine +
                @"Setting a high frequency could crash/damage your system." +
                Environment.NewLine +
                @"Do you want to continue?",
                @"Confirm PState change",
                MessageBoxButtons.OKCancel
            );

            if (confirmResult != DialogResult.OK) return;

            if (string.IsNullOrEmpty(pstateDid.Text) || string.IsNullOrEmpty(pstateFid.Text))
            {
                MessageBox.Show("Can't write because DID/FID is empty!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var pstateId = pstateIdBox.SelectedIndex;
            uint eax = default, edx = default;
            uint IddDiv = 0x0;
            uint IddVal = 0x0;
            uint CpuVid = 0x0;
            uint CpuDfsId = 0x0;
            uint CpuFid = 0x0;

            if (!cpu.ReadMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), ref eax, ref edx))
            {
                SetStatusText($@"Error reading PState {pstateId}!");
                return;
            }

            CalculatePstateDetails(eax, ref IddDiv, ref IddVal, ref CpuVid, ref CpuDfsId, ref CpuFid);

            eax = (IddDiv & 0xFF) << 30 | (IddVal & 0xFF) << 22 | (CpuVid & 0xFF) << 14 | (uint.Parse(pstateDid.Text) & 0xFF) << 8 | uint.Parse(pstateFid.Text) & 0xFF;

            if (_numaUtil.HighestNumaNode > 0)
            {
                for (var i = 0; i < (int)_numaUtil.HighestNumaNode; i++)
                {
                    if (!WritePstateClick(pstateId, eax, edx, i)) return;
                }
            }
            else
            {
                if (!WritePstateClick(pstateId, eax, edx)) return;
            }

            SetStatusText($@"Successfully written PState {pstateId}.");
        }

        // P0 fix C001_0015 HWCR[21]=1
        // Fixes timer issues when not using HPET
        public bool ApplyTscWorkaround()
        {
            uint eax = 0, edx = 0;

            if (cpu.ReadMsr(0xC0010015, ref eax, ref edx))
            {
                eax |= 0x200000;
                return cpu.WriteMsr(0xC0010015, eax, edx);
            }

            SetStatusText($@"Error applying TSC fix!");
            return false;
        }

        private bool WritePstateClick(int pstateId, uint eax, uint edx, int numanode = 0)
        {
            if (_numaUtil.HighestNumaNode > 0) _numaUtil.SetThreadProcessorAffinity((ushort)(numanode + 1), Enumerable.Range(0, Environment.ProcessorCount).ToArray());

            if (!ApplyTscWorkaround()) return false;

            if (!cpu.WriteMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), eax, edx))
            {
                SetStatusText($@"Error writing PState {pstateId}!");
                return false;
            }

            return true;
        }

        private void PciScan_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxPciStartReg.Text, out uint startReg);
                TryConvertToUint(textBoxPciEndReg.Text, out uint endReg);

                if (endReg <= startReg)
                {
                    HandleError("End register is not greater than start register");
                    return;
                }

                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning PCI addresses, please wait...");
                }));

                string result = "REG         Value(HEX) Value(BIN)" + Environment.NewLine;

                while (startReg <= endReg)
                {
                    var data = cpu.ReadDword(startReg);
                    result += $"0x{startReg:X8}: 0x{data:X8} {Convert.ToString(data, 2).PadLeft(32, '0')}" + Environment.NewLine;
                    startReg += 4;
                }
                    
                ShowResultForm("PCI Scan result", result);
            }
            catch (ApplicationException ex)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }));
            }
        }

        private void Scan_WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetButtonsState();
            SetStatusText("Scan Complete.");
        }

        private void SmuScan_WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            int index = comboBoxMailboxSelect.SelectedIndex;
            PopulateMailboxesList(comboBoxMailboxSelect.Items);

            for (var i = 0; i < matches.Count; i++)
            {
                AddMailboxToList($"Mailbox {i + 1}", matches[i]);
            }

            if (index > comboBoxMailboxSelect.Items.Count)
                index = 0;

            comboBoxMailboxSelect.SelectedIndex = index;
            SetButtonsState();
            //ResetSmuAddresses();
            SetStatusText("Scan Complete.");
        }

        private void ButtonPciScan_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(PciScan_DoWork, Scan_WorkerCompleted);
        }

        private void ButtonApplyAC_Click(object sender, EventArgs e)
        {
            int frequency = (int)(((FrequencyListItem)comboBoxACF.SelectedItem).multi * 100.00);
            ApplyFrequencyAllCoreSetting(frequency);
        }

        private void ButtonApplySC_Click(object sender, EventArgs e)
        {
            ApplyFrequencyAllCoreSetting(550);
            int frequency = (int)(((FrequencyListItem)comboBoxSCF.SelectedItem).multi * 100.00);
            ApplyFrequencySingleCoreSetting((CoreListItem)comboBoxCore.SelectedItem, frequency);
        }

        private void ButtonApplyPROCHOT_Click(object sender, EventArgs e)
        {
            if (checkBoxPROCHOT.Checked)
            {
                DisableOCMode();
            }
            EnableOCMode(checkBoxPROCHOT.Checked);
            if (!checkBoxPROCHOT.Checked && cpu.IsProchotEnabled())
            {
                checkBoxPROCHOT.Checked = true;
                HandleError($@"Error, PROCHOT could not be disabled!");
            }
            /*else
            {
                checkBoxPROCHOT.Enabled = false;
                buttonApplyPROCHOT.Enabled = false;
            }*/
        }

        private void ReadMsr_Task(object sender, DoWorkEventArgs e)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning MSR range, please wait...");
                }));

                string result = "MSR         EDX(63-32) EAX(31-0)" + Environment.NewLine;

                TryConvertToUint(textBoxMsrStart.Text, out uint startReg);
                TryConvertToUint(textBoxMsrEnd.Text, out uint endReg);

                while (startReg <= endReg)
                {
                    uint eax = default, edx = default;
                    if (cpu.ReadMsr(startReg, ref eax, ref edx))
                    {
                        result += $"0x{startReg:X8}: 0x{edx:X8} 0x{eax:X8}" + Environment.NewLine;
                    }

                    startReg += 1;
                }

                ShowResultForm("MSR Scan result", result);
            }
            catch (ApplicationException ex)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }));
            }
        }

        private void ButtonMsrRead_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxMsrAddress.Text, out uint msr);
            uint eax = default, edx = default;
            if (cpu.ReadMsr(msr, ref eax, ref edx))
            {
                textBoxMsrEdx.Text = $"0x{edx:X8}";
                textBoxMsrEax.Text = $"0x{eax:X8}";
            }
        }

        private void ButtonMsrWrite_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxMsrEdx.Text, out uint edx);
            TryConvertToUint(textBoxMsrEax.Text, out uint eax);
            TryConvertToUint(textBoxMsrAddress.Text, out uint msr);

            if (!cpu.WriteMsr(msr, eax, edx))
            {
                HandleError($@"Error writing MSR {textBoxMsrAddress.Text}!");
                return;
            }

            SetStatusText("Write OK.");
        }

        private void ButtonMsrScan_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(ReadMsr_Task, Scan_WorkerCompleted);
        }

        private void ReadCPUID_Task(object sender, DoWorkEventArgs e)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning CPUID range, please wait...");
                }));

                string result = "CPUID       EAX        EBX        ECX        EDX" + Environment.NewLine;
                uint LFuncStd = 0, LFuncExt = 0;
                uint eax = 0, ebx = 0, ecx = 0, edx = 0;

                if (cpu.Cpuid(0x00000000, ref eax, ref ebx, ref ecx, ref edx))
                    LFuncStd = eax;

                if (cpu.Cpuid(0x80000000, ref eax, ref ebx, ref ecx, ref edx))
                    LFuncExt = eax - 0x80000000;

                for (uint i = 0; i <= LFuncStd; ++i)
                {
                    var index = 0x00000000 + i;
                    cpu.Cpuid(index, ref eax, ref ebx, ref ecx, ref edx);
                    result += $"0x{index:X8}: 0x{eax:X8} 0x{ebx:X8} 0x{ecx:X8} 0x{edx:X8}" + Environment.NewLine;
                }

                for (uint i = 0; i <= LFuncExt; ++i)
                {
                    var index = 0x80000000 + i;
                    cpu.Cpuid(index, ref eax, ref ebx, ref ecx, ref edx);
                    result += $"0x{index:X8}: 0x{eax:X8} 0x{ebx:X8} 0x{ecx:X8} 0x{edx:X8}" + Environment.NewLine;
                }

                ShowResultForm("CPUID Scan result", result);
            }
            catch (ApplicationException ex)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }));
            }
        }

        private void ButtonCPUIDRead_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxCPUIDAddress.Text, out uint index);
            uint eax = 0, ebx = 0, ecx = 0, edx = 0;
            if (cpu.Cpuid(index, ref eax, ref ebx, ref ecx, ref edx))
            {
                textBoxCPUIDeax.Text = $"0x{eax:X8}";
                textBoxCPUIDebx.Text = $"0x{ebx:X8}";
                textBoxCPUIDecx.Text = $"0x{ecx:X8}";
                textBoxCPUIDedx.Text = $"0x{edx:X8}";
            }
        }

        private void ButtonCPUIDScan_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(ReadCPUID_Task, Scan_WorkerCompleted);
        }

        private void ButtonPMTable_Click(object sender, EventArgs e)
        {
            if (cpu.Status == IOModule.LibStatus.OK)
                new Thread(() => new PowerTableMonitor(cpu).ShowDialog()).Start();
            else
                HandleError("IO driver is not responding or not loaded.");
        }

        private void ButtonSMUMonitor_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxCMDAddress.Text, out uint addrMsg);
            TryConvertToUint(textBoxRSPAddress.Text, out uint addrRsp);
            TryConvertToUint(textBoxARGAddress.Text, out uint addrArg);

            new Thread(() => new SMUMonitor(cpu, addrMsg, addrArg, addrRsp).ShowDialog()).Start();
        }

        private void ComboBoxMailboxSelect_SelectedIndexChanged(object sender, EventArgs e)
        {
            MailboxListItem item = comboBoxMailboxSelect.SelectedItem as MailboxListItem;
            InitTestMailbox(item.msgAddr, item.rspAddr, item.argAddr);
        }

        private void SettingsForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            ExitApplication();
        }

        private void buttonGetCO_Click(object sender, EventArgs e)
        {
            InitPBO();
        }

        private void ApplyCO()
        {
            //if (cpu.info.family == Cpu.Family.FAMILY_19H)
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                for (var i = 0; i < cpu.info.topology.physicalCores; i++)
                {
                    int mapIndex = i < 8 ? 0 : 1;
                    if ((~cpu.info.topology.coreDisableMap[mapIndex] >> i % 8 & 1) == 1)
                    {
                        NumericUpDown control = (NumericUpDown)Controls.Find($"numericUpDownCO_{i}", true)[0];
                        if (control != null)
                        {
                            cpu.SetPsmMarginSingleCore((uint)(((mapIndex << 8) | i % 8 & 0xF) << 20), Convert.ToInt32(control.Value));
                        }
                    }
                }
            }
            else
            {
                HandleError("Not supported");
            }
        }

        private void ButtonApplyCO_Click(object sender, EventArgs e)
        {
            ApplyCO();
            InitPBO();
        }

        private string GetWmiInstanceName()
        {
            try
            {
                instanceName = WMI.GetInstanceName(wmiScope, wmiAMDACPI);
            }
            catch
            {
                // ignored
            }

            return instanceName;
        }

        private void PopulateWmiFunctions()
        {
            try
            {
                instanceName = GetWmiInstanceName();
                classInstance = new ManagementObject(wmiScope,
                    $"{wmiAMDACPI}.InstanceName='{instanceName}'",
                    null);

                // Get function names with their IDs
                string[] functionObjects = { "GetObjectID", "GetObjectID2" };
                var index = 1;

                foreach (var functionObject in functionObjects)
                {
                    try
                    {
                        pack = WMI.InvokeMethodAndGetValue(classInstance, functionObject, "pack", null, 0);

                        if (pack != null)
                        {
                            var ID = (uint[])pack.GetPropertyValue("ID");
                            var IDString = (string[])pack.GetPropertyValue("IDString");
                            var Length = (byte)pack.GetPropertyValue("Length");

                            for (var i = 0; i < Length; ++i)
                            {
                                if (IDString[i] == "")
                                    break;

                                WmiCmdListItem item = new WmiCmdListItem($"{IDString[i] + ": "}{ID[i]:X8}", ID[i], !IDString[i].StartsWith("Get"));
                                comboBoxAvailableCommands.Items.Add(item);
                            }
                        }
                        else
                        {
                            comboBoxAvailableCommands.Items.Add("<FAILED>");
                        }

                        comboBoxAvailableCommands.SelectedIndex = 0;
                    }
                    catch
                    {
                        // ignored
                    }

                    index++;
                }
            }
            catch
            {
                // ignored
            }
        }

        private void ComboBoxAvailableCommands_SelectedIndexChanged(object sender, EventArgs e)
        {
            WmiCmdListItem command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;

            comboBoxAvailableValues.Items.Clear();
            comboBoxAvailableValues.Enabled = false;
            textBoxWmiArgument.Text = "";
            textBoxWmiArgument.Enabled = false;

            if (command.isSet) {
                // Get possible values (index) of a memory option in BIOS
                var dvaluesPack = WMI.InvokeMethodAndGetValue(classInstance, "Getdvalues", "pack", "ID", command.value);
                if (dvaluesPack != null)
                {
                    uint[] DValuesBuffer = (uint[])dvaluesPack.GetPropertyValue("DValuesBuffer");
                    Console.WriteLine(command.text);
                    foreach (uint value in DValuesBuffer)
                    {
                        if (value != 0)
                        {
                            WmiCmdListItem item = new WmiCmdListItem(value.ToString(), value);
                            Console.WriteLine(value);
                            comboBoxAvailableValues.Items.Add(item);
                        }
                    }
                    Console.WriteLine("------------------------");

                    if (comboBoxAvailableValues.Items.Count > 0)
                        comboBoxAvailableValues.Enabled = true;
                    else
                        comboBoxAvailableValues.Items.Add("No values available for this command");
                }
                textBoxWmiArgument.Enabled = true;
            }
            else
            {
                comboBoxAvailableValues.Items.Add("Get commands don't support values");
            }

            comboBoxAvailableValues.SelectedIndex = 0;
        }

        private void ComboBoxAvailableValues_SelectedIndexChanged(object sender, EventArgs e)
        {
            WmiCmdListItem command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;
            if (command.isSet && comboBoxAvailableValues.Enabled)
                textBoxWmiArgument.Text = comboBoxAvailableValues.Text;
            else
                textBoxWmiArgument.Text = "";
        }

        private void ButtonWmiCmdSend_Click(object sender, EventArgs e)
        {
            WmiCmdListItem command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;
            uint value = 0;
            if (command.isSet)
            {
                string text = textBoxWmiArgument.Text;
                //if (text.StartsWith("0x"))
                {
                    //TryConvertToUint(text, out value);
                }
                //else
                {
                    value = uint.Parse(text);
                }
            }

            if (value >= 0 && value < 0x10000)
            {
                var response = WMI.RunCommand(classInstance, command.value, value);
                var text = command.text + Environment.NewLine + "------------------------" + Environment.NewLine;
                foreach (byte b in response)
                {
                    text += "0x" + b.ToString("X2") + Environment.NewLine;
                }
                text += "------------------------" + Environment.NewLine;
                textBoxResult.Text = text + Environment.NewLine + textBoxResult.Text;
            }
        }

        private void ButtonBCLKApply_Click(object sender, EventArgs e)
        {
            double targetBclk = double.Parse(numericUpDownBclk.Text);
            cpu.SetBclk(targetBclk);

            double? currentBclk = cpu.GetBclk();
            labelBCLK.Text = currentBclk + " MHz";
            numericUpDownBclk.Text = $"{currentBclk}";
        }

        private void BulkMarginChangeHandler(int ccd, int step = 1)
        {
            for (var i = ccd * 8; i < ccd * 8 + 8; ++i)
            {
                NumericUpDown control = (NumericUpDown)Controls.Find($"numericUpDownCO_{i}", true)[0];
                if (control != null && control.Enabled)
                {
                    control.Value += step;
                }
            }
        }

        private void Button_ccd0_inc_Click(object sender, EventArgs e)
        {
            BulkMarginChangeHandler(0, 1);
        }

        private void Button_ccd1_inc_Click(object sender, EventArgs e)
        {
            BulkMarginChangeHandler(1, 1);
        }

        private void Button_ccd0_dec_Click(object sender, EventArgs e)
        {
            BulkMarginChangeHandler(0, -1);
        }

        private void Button_ccd1_dec_Click(object sender, EventArgs e)
        {
            BulkMarginChangeHandler(1, -1);
        }

        private void ButtonCpuidDecode_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxCpuid.Text.Trim(), out uint eax);

            Cpu.CPUInfo info = new Cpu.CPUInfo
            {
                cpuid = eax
            };
            info.family = (Family)(((info.cpuid & 0xf00) >> 8) + ((info.cpuid & 0xff00000) >> 20));
            info.baseModel = (info.cpuid & 0xf0) >> 4;
            info.extModel = (info.cpuid & 0xf0000) >> 12;
            info.model = info.baseModel + info.extModel;
            info.stepping = eax & 0xf;

            string responseString =
                Environment.NewLine +
                $"cpuid: 0x{info.cpuid:X8}" +
                Environment.NewLine +
                $"family: {info.family} ({(uint)info.family:X2}h)" +
                Environment.NewLine +
                $"base model: 0x{info.baseModel:X2}" +
                Environment.NewLine +
                $"ext. model: 0x{info.extModel:X2}" +
                Environment.NewLine +
                $"model: 0x{info.model:X2}" +
                Environment.NewLine +
                $"stepping: {info.stepping}" +
                Environment.NewLine +
                Environment.NewLine;

            Invoke(new MethodInvoker(delegate
            {
                textBoxResult.Text += responseString;
            }));
        }

        private void BtnSaveCOProfile_Click(object sender, EventArgs e)
        {
            List<Tuple<int, int>> margins = new List<Tuple<int, int>>();

            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                for (var i = 0; i < 16; i++)
                {
                    NumericUpDown control = (NumericUpDown)Controls.Find($"numericUpDownCO_{i}", true)[0];
                    if (control != null && control.Enabled)
                    {
                        margins.Add(new Tuple<int, int>(i, Convert.ToInt32(control.Value)));
                    }
                }
            }

            if (margins.Count > 0)
            {
                try
                {
                    using (StreamWriter file = new StreamWriter(defaultsPath))
                    {
                        foreach (var entry in margins)
                            file.WriteLine("[{0},{1}]", entry.Item1, entry.Item2);

                        textBoxResult.Text = $"Profile saved in {defaultsPath}" + Environment.NewLine + textBoxResult.Text;
                    }
                }
                catch (Exception)
                {
                    HandleError("Could not save profile to file!");
                }
            }
        }

        private List<Tuple<int, int>> LoadCOProfile()
        {
            List<Tuple<int, int>> margins = new List<Tuple<int, int>>();
            try
            {
                if (!Directory.Exists(profilesPath))
                {
                    MessageBox.Show("Profiles directory does not exist, created one for you.");
                    Directory.CreateDirectory(profilesPath);
                }

                // load from file if it exists
                if (File.Exists(defaultsPath))
                {
                    var lines = File.ReadAllLines(defaultsPath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("["))
                        {
                            var values = line.Replace("[", "").Replace("]", "").Replace(" ", "").Split(',');
                            Int32.TryParse(values[0], NumberStyles.Integer, CultureInfo.CurrentCulture, out int index);
                            Int32.TryParse(values[1], NumberStyles.Integer, CultureInfo.CurrentCulture, out int margin);
                            margins.Add(new Tuple<int, int>(index, margin));
                        }
                    }
                }
                else
                {
                    HandleError("No CO profile saved.");
                }
            }
            catch (Exception ex)
            {
                HandleError("Could not load saved profile!");
            }
            
            return margins;
        }

        private void BtnLoadCOProfile_Click(object sender, EventArgs e)
        {
            List<Tuple<int, int>> margins = LoadCOProfile();

            if (margins.Count > 0 && cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                for (var i = 0; i < margins.Count; i++)
                {
                    NumericUpDown control = (NumericUpDown)Controls.Find($"numericUpDownCO_{margins[i].Item1}", true)[0];
                    if (control != null && control.Enabled)
                    {
                        control.Value = margins[i].Item2;
                    }
                }

                textBoxResult.Text = $"Saved CO profile loaded from {defaultsPath}" + Environment.NewLine + textBoxResult.Text;
            }
        }

        static bool TaskExists(string taskName)
        {
            // Open the task service
            using (TaskService taskService = new TaskService())
            {
                // Attempt to retrieve the task
                Task task = taskService.GetTask(taskName);
                return task != null;
            }
        }

        static void AddTaskToScheduler(string taskName, string executablePath, int delaySeconds = 0)
        {
            // Create a new task service
            using (TaskService taskService = new TaskService())
            {
                // Create a new task definition
                TaskDefinition taskDefinition = taskService.NewTask();

                // Set the task properties
                taskDefinition.RegistrationInfo.Description = "Run Ryzen SMU Debug Tool on user logon to apply CO profile. Automatically created by RyzenSDT. Remove manually or from the checkbox in PBO tab.";
                taskDefinition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;
                taskDefinition.Principal.LogonType = TaskLogonType.InteractiveToken;

                // Create a trigger that starts the task at logon with a specified delay
                LogonTrigger logonTrigger = new LogonTrigger();
                logonTrigger.Delay = TimeSpan.FromSeconds(delaySeconds); // Set the delay
                taskDefinition.Triggers.Add(logonTrigger);

                // Create an action that runs the specified executable
                ExecAction execAction = new ExecAction(executablePath, "--applyprofile");
                taskDefinition.Actions.Add(execAction);

                // Register the task in the root folder of the Task Scheduler
                taskService.RootFolder.RegisterTaskDefinition(taskName, taskDefinition);
            }
        }

        static void RemoveTaskFromScheduler(string taskName)
        {
            // Open the task service
            using (TaskService taskService = new TaskService())
            {
                // Delete the task from the Task Scheduler
                taskService.RootFolder.DeleteTask(taskName, false);
            }
        }

        private void SetStartup(bool isChecked = false)
        {
            /*using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                if (isChecked && key.GetValue("RyzenSDT") == null)
                {
                    key.SetValue("RyzenSDT", Application.ExecutablePath + " --applyprofile");
                }
                else if (!isChecked && key.GetValue("RyzenSDT") != null)
                {
                    key.DeleteValue("RyzenSDT", false);
                }
            }*/

            if (isChecked && !TaskExists("RyzenSDT"))
            {
                AddTaskToScheduler("RyzenSDT", Application.ExecutablePath, 0);
            }
            else
            {
                RemoveTaskFromScheduler("RyzenSDT");
            }
        }

        private void CheckBoxApplyCOStartup_CheckedChanged(object sender, EventArgs e)
        {
            SetStartup((sender as CheckBox).Checked);
            textBoxResult.Text = $"Startup settings saved." + Environment.NewLine + textBoxResult.Text;
        }

        private void tableLayoutPanel14_Paint(object sender, PaintEventArgs e)
        {

        }

        private void ButtonApplyCoreMap_Click(object sender, EventArgs e)
        {
            uint ccd0 = 0x8000;
            uint ccd1 = 0x8100;

            for (int i = 0; i < 8; i++)
            {
                CheckBox control = (CheckBox)Controls.Find($"checkBox{i}", true)[0];
                if (control != null && control.Enabled)
                {
                    if (!control.Checked)
                    {
                        int logicalIndex = Convert.ToInt32(control.Tag as string);
                        ccd0 = Utils.SetBits(ccd0, logicalIndex, 1, 1);
                    }
                }
            }

            for (int i = 0; i < 8; i++)
            {
                CheckBox control = (CheckBox)Controls.Find($"checkBox{i + 8}", true)[0];
                if (control != null && control.Enabled)
                {
                    if (!control.Checked)
                    {
                        int logicalIndex = Convert.ToInt32(control.Tag as string);
                        ccd1 = Utils.SetBits(ccd1, logicalIndex, 1, 1);
                    }
                }
            }

            var cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

            if (cmdItem != null) {
                WMI.RunCommand(classInstance, cmdItem.value, ccd0);
                WMI.RunCommand(classInstance, cmdItem.value, ccd1);
            }

            cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, checkBoxSMT.Checked ? 1u : 0);
            }

            ConfirmWindowsRestart();
        }

        private void Button5_Click(object sender, EventArgs e)
        {
            var cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 0x8000);
                WMI.RunCommand(classInstance, cmdItem.value, 0x81FF);
            }

            cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 0);
            }

            ConfirmWindowsRestart();
        }

        private void Button6_Click(object sender, EventArgs e)
        {
            var cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 0x8000);
                WMI.RunCommand(classInstance, cmdItem.value, 0x8100);
            }

            cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 1);
            }

            ConfirmWindowsRestart();
        }

        private void ConfirmWindowsRestart()
        {
            var result = MessageBox.Show(
                "A restart is required to apply the changes. Would you like to restart now?",
                "Confirm Restart",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                try
                {
                    // Restart Windows
                    Process.Start(new ProcessStartInfo("shutdown", "/r /t 0")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    HandleError($"Failed to restart: {ex.Message}");
                }
            }
        }

        private void RadioButtonManualCoreControl_CheckedChanged(object sender, EventArgs e)
        {
            bool manual = radioButtonManualCoreControl.Checked == true;
            panelManualCoreControl.Enabled = manual;
            panelX3D.Enabled = !manual;
        }

        private void ButtonApplyFMax_Click(object sender, EventArgs e)
        {
            if (cpu.SetFMax((uint)numericUpDownFmax.Value)) {
                numericUpDownFmax.Value = cpu.GetFMax();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

using FormsTimer = System.Windows.Forms.Timer;
using ThreadingTimer = System.Threading.Timer;

[assembly: AssemblyTitle("Hyper-V 托盘监控")]
[assembly: AssemblyProduct("Hyper-V 托盘监控")]
[assembly: AssemblyDescription("监视运行中的 Hyper-V 虚拟机，一键打开 ExHyperV 管理界面")]
[assembly: AssemblyCompany("wunian")]
[assembly: AssemblyCopyright("Copyright © 2026 wunian")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HyperVTray
{
    internal class VMInfo
    {
        public string Name;
        public string Guid;
        public int StateCode;
        public bool Running;
        public long UpTimeSeconds;
        public int CpuLoad = -1;
        public long MemoryUsedMB;
        public long MemoryLimitMB;
        public bool DynamicMemory;
    }

    internal sealed class UptimeEntry
    {
        public string Guid;
        public ToolStripItem Detail;
        public long BaseSeconds;
        public long AllocatedMB;
        public bool DynamicMemory;
    }

    internal sealed class StatusRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled && e.Item.ForeColor != SystemColors.ControlText)
            {
                TextRenderer.DrawText(e.Graphics, e.Item.Text, e.Item.Font,
                    e.TextRectangle, e.Item.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            base.OnRenderItemText(e);
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        private static readonly bool debugLog = string.Equals(
            Environment.GetEnvironmentVariable("HYPERV_TRAY_DEBUG"), "1", StringComparison.OrdinalIgnoreCase);
        private static readonly string StartupLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HyperV-Tray.lnk");

        private static NotifyIcon tray;
        private static Icon baseIcon;
        private static Icon greenIcon;
        private static Icon yellowIcon;
        private static Icon redIcon;
        private static FormsTimer refresh;
        private static ThreadingTimer debounce;
        private static SynchronizationContext sync;
        private static Mutex mutex;
        private static ThreadingTimer uptimeTick;
        private static readonly List<UptimeEntry> uptimeEntries = new List<UptimeEntry>();
        private static DateTime menuOpenTime;
        private static readonly Dictionary<string, int> prevRun = new Dictionary<string, int>();
        private static bool suppressNotify;
        private static bool confirmOpen;
        private static string exHyperVPath;
        private static DateTime exScanTime;
        private static bool firstSync = true;
        private static List<VMInfo> cachedVms;
        private static DateTime cacheTime = DateTime.MinValue;

        private static void Log(string msg)
        {
            if (!debugLog) return;
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "HyperV-Tray.log");
                using (var w = File.AppendText(path))
                    w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg);
            }
            catch { }
        }

        private static void LogEx(string where, Exception ex)
        {
            if (!debugLog) return;
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "HyperV-Tray.log");
                using (var w = File.AppendText(path))
                    w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + where + "] " + ex);
            }
            catch { }
        }

        [STAThread]
        private static void Main()
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (a.Equals("--test-notify", StringComparison.OrdinalIgnoreCase))
                {
                    RunTestNotify();
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            mutex = new Mutex(true, @"Global\HyperV-Tray_SingleInstance", out createdNew);
            if (!createdNew)
            {
                // 前一个实例异常退出后 mutex 会处于 abandoned 状态，此时应尝试接管而不是直接退出
                try { createdNew = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { createdNew = true; }
                if (!createdNew) return;
            }

            sync = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(sync);

            tray = new NotifyIcon();
            baseIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Icon = baseIcon;
            tray.Text = "Hyper-V 监控";
            tray.Visible = true;
            tray.DoubleClick += (s, e) => OpenExHyperV();
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Renderer = new StatusRenderer();
            tray.ContextMenuStrip.Opening += (s, e) => RefreshMenu();
            tray.ContextMenuStrip.Opened += (s, e) => AdjustMenuBounds();
            tray.ContextMenuStrip.Closed += (s, e) => uptimeTick.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("HyperVTray.Green"))
                    if (s != null) greenIcon = new Icon(s);
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("HyperVTray.Yellow"))
                    if (s != null) yellowIcon = new Icon(s);
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("HyperVTray.Red"))
                    if (s != null) redIcon = new Icon(s);
            }
            catch (Exception ex) { LogEx("LoadIcons", ex); }

            uptimeTick = new ThreadingTimer(delegate { TickUptime(); }, null, Timeout.Infinite, Timeout.Infinite);

            refresh = new FormsTimer { Interval = 60000 };
            refresh.Tick += (s, e) => UpdateStatus();
            refresh.Start();

            debounce = new ThreadingTimer(delegate { PostUpdate(); }, null, Timeout.Infinite, Timeout.Infinite);

            new Thread(WatchLoop) { IsBackground = true }.Start();

            UpdateStatus();
            Application.Run();
        }

        private static void RunTestNotify()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var n = new NotifyIcon())
                {
                    n.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    n.Visible = true;
                    n.ShowBalloonTip(3000, "Hyper-V 监控",
                        "测试通知：即使未启用 Hyper-V 服务也能正常收到", ToolTipIcon.None);

                    var t = new System.Windows.Forms.Timer();
                    t.Interval = 5000;
                    t.Tick += (s, e) =>
                    {
                        t.Stop();
                        n.Visible = false;
                        Application.Exit();
                    };
                    t.Start();
                    Application.Run();
                }
            }
            catch (Exception ex) { LogEx("RunTestNotify", ex); }
        }

        private static void UpdateStatus()
        {
            List<VMInfo> vms = GetVms();
            if (vms == null)
            {
                // WMI 查询失败：红色图标 + 明确提示，避免被误认为“没有虚拟机”
                tray.Icon = redIcon != null ? redIcon : baseIcon;
                tray.Text = "Hyper-V 监控（查询失败）";
                tray.Visible = true;
                return;
            }

            var running = new List<VMInfo>();
            bool anySaved = false;
            foreach (var v in vms)
            {
                if (v.Running) running.Add(v);
                if (v.StateCode == 6) anySaved = true;
            }

            if (!IsHyperVServiceRunning()) tray.Icon = redIcon != null ? redIcon : baseIcon;
            else if (running.Count > 0 && greenIcon != null) tray.Icon = greenIcon;
            else if (anySaved && yellowIcon != null) tray.Icon = yellowIcon;
            else tray.Icon = baseIcon;
            tray.Visible = true;
            if (running.Count > 0)
            {
                string tip = "Hyper-V 运行中: " + JoinNames(running);
                if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
                tray.Text = tip;
            }
            else
            {
                tray.Text = "Hyper-V 监控（全部已关闭）";
            }

            NotifyTransitions(vms);
        }

        private static void RefreshMenu()
        {
            List<VMInfo> vms = GetVms();
            bool hasRunning = RebuildMenu(vms);
            menuOpenTime = DateTime.Now;
            uptimeTick.Change(hasRunning ? 0 : Timeout.Infinite, hasRunning ? 1000 : Timeout.Infinite);
        }

        private static void AdjustMenuBounds()
        {
            try
            {
                var menu = tray.ContextMenuStrip;
                var wa = Screen.FromPoint(menu.Location).WorkingArea;
                int x = menu.Left, y = menu.Top;
                if (menu.Right > wa.Right) x = wa.Right - menu.Width;
                if (menu.Bottom > wa.Bottom) y = wa.Bottom - menu.Height;
                if (x < wa.Left) x = wa.Left;
                if (y < wa.Top) y = wa.Top;
                if (x != menu.Left || y != menu.Top) menu.Location = new Point(x, y);
            }
            catch (Exception ex) { LogEx("AdjustMenuBounds", ex); }
        }

        private static void TickUptime()
        {
            try
            {
                UptimeEntry[] entries;
                lock (uptimeEntries) entries = uptimeEntries.ToArray();
                if (entries.Length == 0) return;

                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                long nowSec = (long)(DateTime.Now - menuOpenTime).TotalSeconds;

                var texts = new string[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    int cpu = -1;
                    long used = 0;
                    // 一次 Msvm_SummaryInformation 查询同时取 CPU 与内存，替代两次查询
                    var q = new ObjectQuery("SELECT ProcessorLoad, MemoryUsage FROM Msvm_SummaryInformation WHERE Name='" + entries[i].Guid + "'");
                    using (var s = new ManagementObjectSearcher(scope, q))
                    {
                        foreach (ManagementObject mo in s.Get())
                        {
                            object load = mo["ProcessorLoad"];
                            if (load != null) cpu = Convert.ToInt32(load);
                            object mem = mo["MemoryUsage"];
                            if (mem != null) used = Convert.ToInt64(mem);
                        }
                    }
                    texts[i] = "CPU " + (cpu >= 0 ? cpu + "%" : "--")
                        + " | " + MemInfo(used, entries[i].AllocatedMB, entries[i].DynamicMemory)
                        + " | 已运行 " + FormatUptime(entries[i].BaseSeconds + nowSec);
                }

                sync.Post(delegate
                {
                    try
                    {
                        for (int i = 0; i < entries.Length; i++)
                            entries[i].Detail.Text = texts[i];
                    }
                    catch (Exception ex) { LogEx("TickUptime.UI", ex); }
                }, null);
            }
            catch (Exception ex) { LogEx("TickUptime", ex); }
        }

        private static string FormatUptime(long sec)
        {
            return string.Format("{0:D2}:{1:D2}:{2:D2}", sec / 3600, (sec / 60) % 60, sec % 60);
        }

        private static void NotifyTransitions(List<VMInfo> vms)
        {
            try
            {
                if (firstSync)
                {
                    firstSync = false;
                }
                else if (!suppressNotify)
                {
                    foreach (var v in vms)
                    {
                        int prev;
                        if (!prevRun.TryGetValue(v.Guid, out prev)) continue;
                        if (v.StateCode == 2 && prev != 2) ShowBalloon(v.Name, "已启动");
                        else if (v.StateCode == 6 && prev != 6) ShowBalloon(v.Name, "已保存");
                        else if (v.StateCode == 3 && prev != 3) ShowBalloon(v.Name, "已关闭");
                    }
                }

                prevRun.Clear();
                foreach (var v in vms)
                    prevRun[v.Guid] = v.StateCode;
            }
            catch (Exception ex) { LogEx("NotifyTransitions", ex); }
        }

        private static void ShowBalloon(string name, string action)
        {
            ShowBalloonText(name + " " + action);
        }

        private static void ShowBalloonText(string text)
        {
            try { tray.ShowBalloonTip(2500, "Hyper-V", text, ToolTipIcon.None); }
            catch (Exception ex) { LogEx("ShowBalloonText", ex); }
        }

        private static bool Confirm(string text)
        {
            if (confirmOpen) return false;
            confirmOpen = true;
            try
            {
                return MessageBox.Show(text, "Hyper-V 监控",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            finally { confirmOpen = false; }
        }

        private static string JoinNames(List<VMInfo> vms)
        {
            var names = new List<string>();
            foreach (var v in vms) names.Add(v.Name);
            return string.Join(", ", names.ToArray());
        }

        private static List<VMInfo> GetVms()
        {
            // 1.5 秒内复用上次查询结果：右键弹菜单、状态刷新立即响应，避免每次重复 WMI 查询卡顿
            if (cachedVms != null && (DateTime.Now - cacheTime).TotalSeconds < 1.5)
                return cachedVms;

            var list = new List<VMInfo>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                var query = new ObjectQuery("SELECT Name, ElementName, EnabledState, TimeOfLastStateChange FROM Msvm_ComputerSystem");
                using (var searcher = new ManagementObjectSearcher(scope, query))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            string id = Convert.ToString(mo["Name"]);
                            Guid g;
                            if (!Guid.TryParse(id, out g)) continue;
                            var v = new VMInfo();
                            v.Name = Convert.ToString(mo["ElementName"]);
                            v.Guid = id;
                            v.StateCode = Convert.ToInt32(mo["EnabledState"]);
                            v.Running = v.StateCode == 2;
                            if (v.Running) v.UpTimeSeconds = ComputeUpTime(mo["TimeOfLastStateChange"]);
                            if (!string.IsNullOrEmpty(v.Name)) list.Add(v);
                        }
                        catch (Exception ex) { LogEx("GetVms.Item", ex); }
                    }
                }

                foreach (var v in list)
                    if (v.Running) EnrichVmInfo(scope, v);
            }
            catch (Exception ex)
            {
                LogEx("GetVms", ex);
                return null;
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            cachedVms = list;
            cacheTime = DateTime.Now;
            return list;
        }

        private static long ComputeUpTime(object raw)
        {
            try
            {
                string s = raw as string;
                if (s == null) return 0;
                DateTime dt = ManagementDateTimeConverter.ToDateTime(s);
                long sec = (long)(DateTime.Now - dt).TotalSeconds;
                return sec > 0 ? sec : 0;
            }
            catch (Exception ex) { LogEx("ComputeUpTime", ex); return 0; }
        }

        private static void EnrichVmInfo(ManagementScope scope, VMInfo v)
        {
            try
            {
                // 一次 Msvm_SummaryInformation 查询同时取 CPU、内存、运行时长，替代多次查询
                var q = new ObjectQuery("SELECT ProcessorLoad, MemoryUsage, UpTime FROM Msvm_SummaryInformation WHERE Name='" + v.Guid + "'");
                using (var s = new ManagementObjectSearcher(scope, q))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object load = mo["ProcessorLoad"];
                        if (load != null) v.CpuLoad = Convert.ToInt32(load);
                        object mem = mo["MemoryUsage"];
                        if (mem != null) v.MemoryUsedMB = Convert.ToInt64(mem);
                        object up = mo["UpTime"];
                        if (up != null && Convert.ToInt64(up) > 0) v.UpTimeSeconds = Convert.ToInt64(up);
                    }
                }

                var memQuery = new ObjectQuery("SELECT Limit, DynamicMemoryEnabled FROM Msvm_MemorySettingData WHERE InstanceID LIKE '%" + v.Guid + "%'");
                using (var s = new ManagementObjectSearcher(scope, memQuery))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object limit = mo["Limit"];
                        if (limit != null && Convert.ToInt64(limit) > v.MemoryLimitMB) v.MemoryLimitMB = Convert.ToInt64(limit);
                        object dm = mo["DynamicMemoryEnabled"];
                        if (dm != null && Convert.ToBoolean(dm)) v.DynamicMemory = true;
                    }
                }
            }
            catch (Exception ex) { LogEx("EnrichVmInfo", ex); }
        }

        private static string FmtMem(long mb)
        {
            return mb > 0 ? (mb / 1024.0).ToString("0.#") + " GB" : "--";
        }

        private static string MemInfo(long used, long max, bool dynamic)
        {
            return "已使用 " + FmtMem(used) + " / " + (dynamic ? "最大" : "分配") + " " + FmtMem(max);
        }

        private static bool IsPaused(VMInfo v)
        {
            return v.StateCode == 9 || v.StateCode == 32768;
        }

        private static string StateText(VMInfo v)
        {
            switch (v.StateCode)
            {
                case 2: return "运行中";
                case 3: return "已关闭";
                case 4: return "停止中";
                case 5: return "启动中";
                case 6: return "已保存";
                case 9:
                case 32768: return "已暂停";
                case 10: return "暂停中";
                default: return "状态 " + v.StateCode;
            }
        }

        private static bool RebuildMenu(List<VMInfo> vms)
        {
            var menu = tray.ContextMenuStrip;
            menu.Items.Clear();
            lock (uptimeEntries) uptimeEntries.Clear();

            bool hasRunning = false;
            bool anySaved = false;
            bool hvEnabled = IsHyperVServiceRunning();
            var hvHeader = new ToolStripMenuItem(hvEnabled ? "已启用 Hyper-V 服务" : "未启用 Hyper-V 服务") { Enabled = false };
            hvHeader.ForeColor = hvEnabled ? Color.Green : Color.Red;
            menu.Items.Add(hvHeader);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("虚拟机") { Enabled = false });

            menu.Items.Add(new ToolStripSeparator());

            if (vms == null)
            {
                menu.Items.Add(new ToolStripMenuItem("(查询失败，请点“立即刷新”重试)") { Enabled = false });
            }
            else if (vms.Count == 0)
            {
                menu.Items.Add(new ToolStripMenuItem("(没有虚拟机)") { Enabled = false });
            }
            else
            {
                foreach (var v in vms)
                {
                    string vmName = v.Name;
                    string vmGuid = v.Guid;

                    var vmItem = new ToolStripMenuItem(vmName + "  [" + StateText(v) + "]");
                    vmItem.Click += (s, e) => OpenExHyperV();

                    if (v.Running)
                    {
                        var entry = new UptimeEntry();
                        entry.Guid = v.Guid;
                        entry.Detail = new ToolStripMenuItem { Enabled = false };
                        entry.BaseSeconds = v.UpTimeSeconds;
                        entry.AllocatedMB = v.MemoryLimitMB;
                        entry.DynamicMemory = v.DynamicMemory;
                        entry.Detail.Text = "CPU " + (v.CpuLoad >= 0 ? v.CpuLoad + "%" : "--")
                            + " | " + MemInfo(v.MemoryUsedMB, v.MemoryLimitMB, v.DynamicMemory)
                            + " | 已运行 " + FormatUptime(v.UpTimeSeconds);
                        vmItem.DropDownItems.Add(entry.Detail);
                        lock (uptimeEntries) uptimeEntries.Add(entry);
                    }

                    var connect = new ToolStripMenuItem("连接虚拟机");
                    connect.Click += (s, e) => ConnectVm(vmName);
                    vmItem.DropDownItems.Add(connect);

                    if (v.Running)
                    {
                        hasRunning = true;
                        var save = new ToolStripMenuItem("保存虚拟机状态");
                        save.Click += (s, e) => StartThread(delegate { SaveVm(vmGuid); });
                        vmItem.DropDownItems.Add(save);

                        var stop = new ToolStripMenuItem("关闭虚拟机");
                        stop.Click += (s, e) => StartThread(delegate { StopVm(vmGuid); });
                        vmItem.DropDownItems.Add(stop);
                    }
                    else if (v.StateCode == 6)
                    {
                        anySaved = true;
                        var restore = new ToolStripMenuItem("恢复虚拟机");
                        restore.Click += (s, e) => StartThread(delegate { StartVm(vmGuid); });
                        vmItem.DropDownItems.Add(restore);

                        var discard = new ToolStripMenuItem("销毁保存的虚拟机");
                        discard.Click += (s, e) =>
                        {
                            if (Confirm("确定要销毁「" + vmName + "」的保存状态吗？"))
                                StartThread(delegate { DiscardSavedVm(vmGuid); });
                        };
                        vmItem.DropDownItems.Add(discard);
                    }
                    else if (IsPaused(v))
                    {
                        // 已暂停：只能恢复（请求回到运行态），不提供其他操作
                        var resume = new ToolStripMenuItem("恢复虚拟机");
                        resume.Click += (s, e) => StartThread(delegate { StartVm(vmGuid); });
                        vmItem.DropDownItems.Add(resume);
                    }
                    else if (v.StateCode == 3)
                    {
                        var start = new ToolStripMenuItem("启动虚拟机");
                        start.Click += (s, e) => StartThread(delegate { StartVm(vmGuid); });
                        vmItem.DropDownItems.Add(start);
                    }
                    // 过渡状态（启动中/停止中/暂停中）及未知状态：仅提供连接，避免误操作

                    menu.Items.Add(vmItem);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            var openMgr = new ToolStripMenuItem(HasExHyperV() ? "打开 ExHyperV 界面" : "打开 Hyper-V 管理器");
            openMgr.Enabled = hvEnabled;
            openMgr.Click += (s, e) => OpenExHyperV();
            menu.Items.Add(openMgr);

            var stopAll = new ToolStripMenuItem("关闭全部虚拟机");
            stopAll.Enabled = hasRunning;
            stopAll.Click += (s, e) =>
            {
                if (Confirm("确定要关闭所有虚拟机吗？"))
                    StopAllVms();
            };
            menu.Items.Add(stopAll);

            var saveAll = new ToolStripMenuItem("保存全部虚拟机");
            saveAll.Enabled = hasRunning;
            saveAll.Click += (s, e) =>
            {
                if (Confirm("确定要保存所有虚拟机吗？"))
                    SaveAllVms();
            };
            menu.Items.Add(saveAll);

            var restoreAll = new ToolStripMenuItem("恢复所有保存的虚拟机");
            restoreAll.Enabled = anySaved;
            restoreAll.Click += (s, e) => RestoreAllSaved();
            menu.Items.Add(restoreAll);

            var discardAll = new ToolStripMenuItem("销毁所有保存的虚拟机");
            discardAll.Enabled = anySaved;
            discardAll.Click += (s, e) =>
            {
                if (Confirm("确定要销毁所有保存的虚拟机吗？"))
                    DiscardAllSaved();
            };
            menu.Items.Add(discardAll);

            var connectAll = new ToolStripMenuItem("连接所有运行中的虚拟机");
            connectAll.Enabled = hasRunning;
            connectAll.Click += (s, e) => ConnectAllRunning();
            menu.Items.Add(connectAll);

            menu.Items.Add(new ToolStripSeparator());

            var refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += (s, e) => UpdateStatus();
            menu.Items.Add(refresh);

            var auto = new ToolStripMenuItem("开机自启");
            auto.Checked = File.Exists(StartupLnk);
            auto.Click += (s, e) =>
            {
                ToggleAutostart();
                auto.Checked = File.Exists(StartupLnk);
            };
            menu.Items.Add(auto);

            var exit = new ToolStripMenuItem("退出");
            exit.Click += (s, e) =>
            {
                tray.Visible = false;
                tray.Dispose();
                Application.Exit();
            };
            menu.Items.Add(exit);

            return hasRunning;
        }

        private static bool HasExHyperV()
        {
            return FindExHyperV() != null;
        }

        private static bool IsHyperVServiceRunning()
        {
            try
            {
                using (var sc = new ServiceController("vmms"))
                    return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex) { LogEx("IsHyperVServiceRunning", ex); return false; }
        }

        private static string FindExHyperV()
        {
            if ((DateTime.Now - exScanTime).TotalSeconds < 30) return exHyperVPath;
            exScanTime = DateTime.Now;
            exHyperVPath = null;
            try
            {
                Process[] procs = Process.GetProcessesByName("ExHyperV");
                if (procs.Length > 0)
                {
                    try { exHyperVPath = procs[0].MainModule.FileName; return exHyperVPath; }
                    catch (Exception ex) { LogEx("FindExHyperV.Proc", ex); }
                }

                string[] dirs = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ExHyperV"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ExHyperV"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ExHyperV"),
                    @"C:\ExHyperV"
                };
                foreach (string dir in dirs)
                {
                    string p = Path.Combine(dir, "ExHyperV.exe");
                    if (File.Exists(p)) { exHyperVPath = p; return exHyperVPath; }
                }

                // 兜底：扫描 C:\ 根目录下所有 ExHyperV* 目录（兼容带版本号的安装目录，如 C:\ExHyperV_V1.5.0_x64）
                try
                {
                    foreach (string d in Directory.GetDirectories(@"C:\", "ExHyperV*"))
                    {
                        string p = Path.Combine(d, "ExHyperV.exe");
                        if (File.Exists(p)) { exHyperVPath = p; return exHyperVPath; }
                    }
                }
                catch (Exception ex) { LogEx("FindExHyperV.ScanRoot", ex); }

                string[] menuRoots = {
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs")
                };
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                foreach (string root in menuRoots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string lnk in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            dynamic sc = shell.CreateShortcut(lnk);
                            string target = sc.TargetPath;
                            if (!string.IsNullOrEmpty(target) && Path.GetFileName(target).Equals("ExHyperV.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                exHyperVPath = target;
                                return exHyperVPath;
                            }
                        }
                        catch (Exception ex) { LogEx("FindExHyperV.Lnk", ex); }
                    }
                }
            }
            catch (Exception ex) { LogEx("FindExHyperV", ex); }
            return exHyperVPath;
        }

        private static void OpenExHyperV()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("ExHyperV");
                if (procs.Length > 0)
                {
                    IntPtr h = procs[0].MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        ShowWindow(h, SW_RESTORE);
                        SetForegroundWindow(h);
                    }
                }
                else
                {
                    string p = FindExHyperV();
                    if (p != null) Process.Start(p);
                    else OpenHyperVManager();
                }
            }
            catch (Exception ex) { LogEx("OpenExHyperV", ex); OpenHyperVManager(); }
        }

        private static void OpenHyperVManager()
        {
            try { Process.Start("virtmgmt.msc"); }
            catch (Exception ex) { LogEx("OpenHyperVManager", ex); }
        }

        private static string QuoteArg(string arg)
        {
            // 按 Windows 命令行规则转义：引号包裹，内部的 \" 与 \\ 转义，避免 VM 名含特殊字符时被当作命令行解析（参数注入）
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void ConnectVm(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("vmconnect.exe", "localhost " + QuoteArg(name));
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex) { LogEx("ConnectVm", ex); }
        }

        private static void ConnectAllRunning()
        {
            try
            {
                var vms = GetVms();
                if (vms == null) return;
                foreach (var v in vms)
                    if (v.Running) ConnectVm(v.Name);
            }
            catch (Exception ex) { LogEx("ConnectAllRunning", ex); }
        }

        private static void StartThread(ThreadStart action)
        {
            new Thread(action) { IsBackground = true }.Start();
        }

        private static void ToggleAutostart()
        {
            try
            {
                if (File.Exists(StartupLnk))
                {
                    File.Delete(StartupLnk);
                }
                else
                {
                    string dir = Path.GetDirectoryName(StartupLnk);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    dynamic sc = shell.CreateShortcut(StartupLnk);
                    sc.TargetPath = Application.ExecutablePath;
                    sc.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                    sc.IconLocation = Application.ExecutablePath + ",0";
                    sc.Description = "Hyper-V 托盘监控";
                    sc.Save();
                }
            }
            catch (Exception ex) { LogEx("ToggleAutostart", ex); }
        }

        private static void WatchLoop()
        {
            while (true)
            {
                try
                {
                    var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                    scope.Connect();
                    string filter = "TargetInstance ISA 'Msvm_ComputerSystem'";
                    EventArrivedEventHandler handler = (s, e) => RaiseUpdate();

                    var mod = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM __InstanceModificationEvent WITHIN 1 WHERE " + filter));
                    var cre = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE " + filter));
                    var del = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE " + filter));

                    mod.EventArrived += handler;
                    cre.EventArrived += handler;
                    del.EventArrived += handler;

                    mod.Start();
                    cre.Start();
                    del.Start();

                    Thread.Sleep(Timeout.Infinite);
                }
                catch (Exception ex)
                {
                    LogEx("WatchLoop", ex);
                    Thread.Sleep(10000);
                }
            }
        }

        private static void PostUpdate()
        {
            try { sync.Post(delegate { UpdateStatus(); }, null); }
            catch (Exception ex) { LogEx("PostUpdate", ex); }
        }

        private static void RaiseUpdate()
        {
            try { debounce.Change(800, Timeout.Infinite); }
            catch (Exception ex) { LogEx("RaiseUpdate", ex); }
        }

        private static ManagementObject GetVmObject(ManagementScope scope, string guid)
        {
            var q = new ObjectQuery("SELECT * FROM Msvm_ComputerSystem WHERE Name='" + guid + "'");
            using (var s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                    return mo;
            }
            return null;
        }

        private static void StartVm(string guid)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                RequestState(scope, guid, 2);
            }
            catch (Exception ex) { LogEx("StartVm", ex); }
        }

        private static void RequestState(ManagementScope scope, string guid, int state)
        {
            try
            {
                using (ManagementObject mo = GetVmObject(scope, guid))
                {
                    if (mo == null) return;
                    var p = mo.GetMethodParameters("RequestStateChange");
                    p["RequestedState"] = state;
                    mo.InvokeMethod("RequestStateChange", p, null);
                }
            }
            catch (Exception ex) { LogEx("RequestState", ex); }
        }

        private static void SaveVm(string guid)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                RequestSave(scope, guid);
            }
            catch (Exception ex) { LogEx("SaveVm", ex); }
        }

        private static void RequestSave(ManagementScope scope, string guid)
        {
            RequestState(scope, guid, 6);
        }

        private static void DiscardSavedVm(string guid)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                RequestState(scope, guid, 3);
            }
            catch (Exception ex) { LogEx("DiscardSavedVm", ex); }
        }

        private static void SaveAllVms()
        {
            suppressNotify = true;
            StartThread(delegate
            {
                try
                {
                    var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                    scope.Connect();
                    var list = GetVmsByState(scope, 2);
                    if (list.Count == 0) return;

                    foreach (string guid in list)
                        RequestSave(scope, guid);

                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(2000);
                        bool anyNotSaved = false;
                        foreach (string guid in list)
                            if (!IsSaved(scope, guid)) { anyNotSaved = true; break; }
                        if (!anyNotSaved) break;
                    }

                    sync.Post(delegate { ShowBalloonText("已保存全部虚拟机"); }, null);
                }
                catch (Exception ex) { LogEx("SaveAllVms", ex); }
                finally { suppressNotify = false; }
            });
        }

        private static bool IsSaved(ManagementScope scope, string guid)
        {
            try
            {
                using (ManagementObject mo = GetVmObject(scope, guid))
                {
                    if (mo == null) return false;
                    mo.Get();
                    return Convert.ToInt32(mo["EnabledState"]) == 6;
                }
            }
            catch (Exception ex) { LogEx("IsSaved", ex); return false; }
        }

        private static List<string> GetVmsByState(ManagementScope scope, int state)
        {
            var list = new List<string>();
            var q = new ObjectQuery("SELECT Name, EnabledState FROM Msvm_ComputerSystem");
            using (var s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    try
                    {
                        string id = Convert.ToString(mo["Name"]);
                        Guid g;
                        if (!Guid.TryParse(id, out g)) continue;
                        if (Convert.ToInt32(mo["EnabledState"]) == state) list.Add(id);
                    }
                    catch (Exception ex) { LogEx("GetVmsByState.Item", ex); }
                }
            }
            return list;
        }

        private static void RestoreAllSaved()
        {
            suppressNotify = true;
            StartThread(delegate
            {
                try
                {
                    var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                    scope.Connect();
                    var list = GetVmsByState(scope, 6);
                    if (list.Count == 0) return;

                    foreach (string guid in list)
                        RequestState(scope, guid, 2);

                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(2000);
                        bool anyNotRunning = false;
                        foreach (string guid in list)
                            if (GetStateCode(scope, guid) != 2) { anyNotRunning = true; break; }
                        if (!anyNotRunning) break;
                    }

                    sync.Post(delegate { ShowBalloonText("已恢复所有虚拟机"); }, null);
                }
                catch (Exception ex) { LogEx("RestoreAllSaved", ex); }
                finally { suppressNotify = false; }
            });
        }

        private static void DiscardAllSaved()
        {
            suppressNotify = true;
            StartThread(delegate
            {
                try
                {
                    var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                    scope.Connect();
                    var list = GetVmsByState(scope, 6);
                    if (list.Count == 0) return;

                    foreach (string guid in list)
                        RequestState(scope, guid, 3);

                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(2000);
                        bool anyStill = false;
                        foreach (string guid in list)
                            if (GetStateCode(scope, guid) != 3) { anyStill = true; break; }
                        if (!anyStill) break;
                    }

                    sync.Post(delegate { ShowBalloonText("已销毁全部保存虚拟机"); }, null);
                }
                catch (Exception ex) { LogEx("DiscardAllSaved", ex); }
                finally { suppressNotify = false; }
            });
        }

        private static void StopVm(string guid)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                GracefulShutdown(scope, guid);

                for (int i = 0; i < 15; i++)
                {
                    Thread.Sleep(2000);
                    if (!IsRunning(scope, guid)) return;
                }

                ForceOff(scope, guid);
            }
            catch (Exception ex) { LogEx("StopVm", ex); }
        }

        private static void StopAllVms()
        {
            suppressNotify = true;
            StartThread(delegate
            {
                try
                {
                    var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                    scope.Connect();
                    var list = GetVmsByState(scope, 2);
                    if (list.Count == 0) return;

                    foreach (string guid in list)
                        GracefulShutdown(scope, guid);

                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(2000);
                        bool any = false;
                        foreach (string guid in list)
                            if (IsRunning(scope, guid)) { any = true; break; }
                        if (!any) break;
                    }

                    foreach (string guid in list)
                        if (IsRunning(scope, guid)) ForceOff(scope, guid);

                    sync.Post(delegate { ShowBalloonText("已关闭所有虚拟机"); }, null);
                }
                catch (Exception ex) { LogEx("StopAllVms", ex); }
                finally { suppressNotify = false; }
            });
        }

        private static void GracefulShutdown(ManagementScope scope, string guid)
        {
            var query = new ObjectQuery("SELECT * FROM Msvm_ShutdownComponent WHERE SystemName='" + guid + "'");
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                foreach (ManagementObject sh in searcher.Get())
                {
                    try
                    {
                        var p = sh.GetMethodParameters("InitiateShutdown");
                        p["Force"] = false;
                        p["Reason"] = 0;
                        sh.InvokeMethod("InitiateShutdown", p, null);
                    }
                    catch (Exception ex) { LogEx("GracefulShutdown", ex); }
                }
            }
        }

        private static void ForceOff(ManagementScope scope, string guid)
        {
            using (ManagementObject mo = GetVmObject(scope, guid))
            {
                if (mo == null) return;
                var p = mo.GetMethodParameters("RequestStateChange");
                p["RequestedState"] = 3;
                mo.InvokeMethod("RequestStateChange", p, null);
            }
        }

        private static bool IsRunning(ManagementScope scope, string guid)
        {
            return GetStateCode(scope, guid) == 2;
        }

        private static int GetStateCode(ManagementScope scope, string guid)
        {
            try
            {
                using (ManagementObject mo = GetVmObject(scope, guid))
                {
                    if (mo == null) return -1;
                    mo.Get();
                    return Convert.ToInt32(mo["EnabledState"]);
                }
            }
            catch (Exception ex) { LogEx("GetStateCode", ex); return -1; }
        }
    }
}

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

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;
        private const string ExHyperVPath = @"C:\ExHyperV_V1.5.0_x64\ExHyperV.exe";
        private const string StartupLnk = @"C:\Users\wunian\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\HyperV-Tray.lnk";

        private static NotifyIcon tray;
        private static Icon baseIcon;
        private static Icon greenIcon;
        private static FormsTimer refresh;
        private static ThreadingTimer debounce;
        private static SynchronizationContext sync;
        private static Mutex mutex;
        private static ThreadingTimer uptimeTick;
        private static readonly List<UptimeEntry> uptimeEntries = new List<UptimeEntry>();
        private static DateTime menuOpenTime;
        private static readonly Dictionary<string, bool> prevRun = new Dictionary<string, bool>();
        private static string exHyperVPath;
        private static DateTime exScanTime;
        private static bool firstSync = true;

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            mutex = new Mutex(true, @"Global\HyperV-Tray_SingleInstance", out createdNew);
            if (!createdNew) return;

            sync = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(sync);

            tray = new NotifyIcon();
            baseIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Icon = baseIcon;
            tray.Text = "Hyper-V 监控";
            tray.Visible = true;
            tray.DoubleClick += (s, e) => OpenExHyperV();
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += (s, e) => RefreshMenu();
            tray.ContextMenuStrip.Closed += (s, e) => uptimeTick.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("HyperVTray.Green"))
                    if (s != null) greenIcon = new Icon(s);
            }
            catch { }

            uptimeTick = new ThreadingTimer(delegate { TickUptime(); }, null, Timeout.Infinite, Timeout.Infinite);

            refresh = new FormsTimer { Interval = 60000 };
            refresh.Tick += (s, e) => UpdateStatus();
            refresh.Start();

            debounce = new ThreadingTimer(delegate { PostUpdate(); }, null, Timeout.Infinite, Timeout.Infinite);

            new Thread(WatchLoop) { IsBackground = true }.Start();

            UpdateStatus();
            Application.Run();
        }

        private static void UpdateStatus()
        {
            List<VMInfo> vms = GetVms();

            var running = new List<VMInfo>();
            foreach (var v in vms)
                if (v.Running) running.Add(v);

            tray.Icon = (running.Count > 0 && greenIcon != null) ? greenIcon : baseIcon;
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
            RebuildMenu(vms);
            menuOpenTime = DateTime.Now;
            bool hasRunning = false;
            foreach (var v in vms)
                if (v.Running) { hasRunning = true; break; }
            uptimeTick.Change(hasRunning ? 0 : Timeout.Infinite, hasRunning ? 1000 : Timeout.Infinite);
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
                    int cpu = QueryCpu(scope, entries[i].Guid);
                    long used = QueryUsedMem(scope, entries[i].Guid);
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
                    catch { }
                }, null);
            }
            catch { }
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
                else
                {
                    foreach (var v in vms)
                    {
                        bool prev;
                        if (!prevRun.TryGetValue(v.Guid, out prev)) continue;
                        if (v.Running && !prev) ShowBalloon(v.Name, "已启动");
                        else if (!v.Running && prev) ShowBalloon(v.Name, "已关闭");
                    }
                }

                prevRun.Clear();
                foreach (var v in vms)
                    prevRun[v.Guid] = v.Running;
            }
            catch { }
        }

        private static void ShowBalloon(string name, string action)
        {
            try { tray.ShowBalloonTip(2500, "Hyper-V", name + " " + action, ToolTipIcon.Info); }
            catch { }
        }

        private static string JoinNames(List<VMInfo> vms)
        {
            var names = new List<string>();
            foreach (var v in vms) names.Add(v.Name);
            return string.Join(", ", names.ToArray());
        }

        private static List<VMInfo> GetVms()
        {
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
                            v.Running = Convert.ToUInt16(mo["EnabledState"]) == 2;
                            if (v.Running) v.UpTimeSeconds = ComputeUpTime(mo["TimeOfLastStateChange"]);
                            if (!string.IsNullOrEmpty(v.Name)) list.Add(v);
                        }
                        catch { }
                    }
                }

                foreach (var v in list)
                    if (v.Running) EnrichVmInfo(scope, v);
            }
            catch { }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
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
            catch { return 0; }
        }

        private static void EnrichVmInfo(ManagementScope scope, VMInfo v)
        {
            try
            {
                v.CpuLoad = QueryCpu(scope, v.Guid);
                v.MemoryUsedMB = QueryUsedMem(scope, v.Guid);

                var memQuery = new ObjectQuery("SELECT VirtualQuantity, Limit, DynamicMemoryEnabled FROM Msvm_MemorySettingData WHERE InstanceID LIKE '%" + v.Guid + "%'");
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
            catch { }
        }

        private static int QueryCpu(ManagementScope scope, string guid)
        {
            long total = 0, count = 0;
            var q = new ObjectQuery("SELECT LoadPercentage FROM Msvm_Processor WHERE SystemName='" + guid + "'");
            using (var s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    object val = mo["LoadPercentage"];
                    if (val != null) { total += Convert.ToInt64(val); count++; }
                }
            }
            return count > 0 ? (int)(total / count) : -1;
        }

        private static long QueryUsedMem(ManagementScope scope, string guid)
        {
            long used = 0;
            var q = new ObjectQuery("SELECT MemoryUsage FROM Msvm_SummaryInformation WHERE Name='" + guid + "'");
            using (var s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    object val = mo["MemoryUsage"];
                    if (val != null) used = Convert.ToInt64(val);
                }
            }
            return used;
        }

        private static string FmtMem(long mb)
        {
            return mb > 0 ? (mb / 1024.0).ToString("0.#") + " GB" : "--";
        }

        private static string MemInfo(long used, long max, bool dynamic)
        {
            return "已使用 " + FmtMem(used) + " / " + (dynamic ? "最大" : "分配") + " " + FmtMem(max);
        }

        private static void RebuildMenu(List<VMInfo> vms)
        {
            var menu = tray.ContextMenuStrip;
            menu.Items.Clear();
            lock (uptimeEntries) uptimeEntries.Clear();

            bool hasRunning = false;
            bool hvEnabled = IsHyperVServiceRunning();
            var hvHeader = new ToolStripMenuItem(hvEnabled ? "已启用 Hyper-V 服务" : "未启用 Hyper-V 服务") { Enabled = false };
            if (hvEnabled) hvHeader.ForeColor = Color.Green;
            menu.Items.Add(hvHeader);

            menu.Items.Add(new ToolStripMenuItem("虚拟机") { Enabled = false });

            if (vms.Count == 0)
            {
                menu.Items.Add(new ToolStripMenuItem("(没有虚拟机)") { Enabled = false });
            }
            else
            {
                foreach (var v in vms)
                {
                    string vmName = v.Name;
                    string vmGuid = v.Guid;

                    var vmItem = new ToolStripMenuItem(vmName + "  [" + (v.Running ? "运行中" : "已关闭") + "]");
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

                    var connect = new ToolStripMenuItem("使用 vmconnect 连接");
                    connect.Click += (s, e) => ConnectVm(vmName);
                    vmItem.DropDownItems.Add(connect);

                    if (v.Running)
                    {
                        hasRunning = true;
                        var stop = new ToolStripMenuItem("关闭虚拟机");
                        stop.Click += (s, e) => StartThread(delegate { StopVm(vmGuid); });
                        vmItem.DropDownItems.Add(stop);
                    }
                    else
                    {
                        var start = new ToolStripMenuItem("启动虚拟机");
                        start.Click += (s, e) => StartThread(delegate { StartVm(vmGuid); });
                        vmItem.DropDownItems.Add(start);
                    }

                    menu.Items.Add(vmItem);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            var openMgr = new ToolStripMenuItem(HasExHyperV() ? "打开 ExHyperV 界面" : "打开 Hyper-V 管理器");
            openMgr.Click += (s, e) => OpenExHyperV();
            menu.Items.Add(openMgr);

            var stopAll = new ToolStripMenuItem("关闭全部虚拟机");
            stopAll.Enabled = hasRunning;
            stopAll.Click += (s, e) =>
            {
                if (MessageBox.Show("确定要关闭所有虚拟机吗？", "Hyper-V 监控",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    StopAllVms();
            };
            menu.Items.Add(stopAll);

            var connectAll = new ToolStripMenuItem("连接所有运行中的虚拟机");
            connectAll.Enabled = hasRunning;
            connectAll.Click += (s, e) => ConnectAllRunning();
            menu.Items.Add(connectAll);

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
            catch { return false; }
        }

        private static string FindExHyperV()
        {
            if ((DateTime.Now - exScanTime).TotalSeconds < 30) return exHyperVPath;
            exScanTime = DateTime.Now;
            exHyperVPath = null;
            try
            {
                if (File.Exists(ExHyperVPath)) { exHyperVPath = ExHyperVPath; return exHyperVPath; }

                Process[] procs = Process.GetProcessesByName("ExHyperV");
                if (procs.Length > 0)
                {
                    try { exHyperVPath = procs[0].MainModule.FileName; return exHyperVPath; }
                    catch { }
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
                        catch { }
                    }
                }
            }
            catch { }
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
            catch { OpenHyperVManager(); }
        }

        private static void OpenHyperVManager()
        {
            try { Process.Start("virtmgmt.msc"); }
            catch { }
        }

        private static void ConnectVm(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("vmconnect.exe", "localhost \"" + name + "\"");
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }

        private static void ConnectAllRunning()
        {
            try
            {
                foreach (var v in GetVms())
                    if (v.Running) ConnectVm(v.Name);
            }
            catch { }
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
            catch { }
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
                catch
                {
                    Thread.Sleep(10000);
                }
            }
        }

        private static void PostUpdate()
        {
            try { sync.Post(delegate { UpdateStatus(); }, null); }
            catch { }
        }

        private static void RaiseUpdate()
        {
            try { debounce.Change(800, Timeout.Infinite); }
            catch { }
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
                using (ManagementObject mo = GetVmObject(scope, guid))
                {
                    if (mo == null) return;
                    var p = mo.GetMethodParameters("RequestStateChange");
                    p["RequestedState"] = 2;
                    mo.InvokeMethod("RequestStateChange", p, null);
                }
            }
            catch { }
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
            catch { }
        }

        private static void StopAllVms()
        {
            StartThread(delegate
            {
                try
                {
                    var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                    scope.Connect();
                    var list = new List<string>();
                    var q = new ObjectQuery("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem");
                    using (var s = new ManagementObjectSearcher(scope, q))
                    {
                        foreach (ManagementObject mo in s.Get())
                        {
                            try
                            {
                                string id = Convert.ToString(mo["Name"]);
                                Guid g;
                                if (!Guid.TryParse(id, out g)) continue;
                                if (Convert.ToUInt16(mo["EnabledState"]) == 2) list.Add(id);
                            }
                            catch { }
                        }
                    }
                    if (list.Count == 0) return;

                    foreach (string guid in list)
                        GracefulShutdown(scope, guid);

                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(2000);
                        bool any = false;
                        foreach (string guid in list)
                            if (IsRunning(scope, guid)) { any = true; break; }
                        if (!any) return;
                    }

                    foreach (string guid in list)
                        if (IsRunning(scope, guid)) ForceOff(scope, guid);
                }
                catch { }
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
                    catch { }
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
            try
            {
                using (ManagementObject mo = GetVmObject(scope, guid))
                {
                    if (mo == null) return false;
                    mo.Get();
                    return Convert.ToUInt16(mo["EnabledState"]) == 2;
                }
            }
            catch { return false; }
        }
    }
}

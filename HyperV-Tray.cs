using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Hyper-V 托盘监控")]
[assembly: AssemblyProduct("Hyper-V 托盘监控")]
[assembly: AssemblyDescription("监视运行中的 Hyper-V 虚拟机，一键打开 ExHyperV 管理界面")]
[assembly: AssemblyCompany("wunian")]
[assembly: AssemblyCopyright("Copyright © 2026 wunian")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HyperVTray
{
    public class VMInfo
    {
        public string Name;
        public string Guid;
        public bool Running;
        public long UpTimeSeconds;
        public int CpuLoad = -1;
        public long MemoryMB;
        public string OsType = "Windows";
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
        private static System.Windows.Forms.Timer timer;
        private static System.Threading.Timer debounce;
        private static SynchronizationContext sync;
        private static string lastSig = "";
        private static Mutex mutex;
        private static List<VMInfo> lastVms = new List<VMInfo>();
        private static readonly Dictionary<string, bool> prevRun = new Dictionary<string, bool>();
        private static readonly Dictionary<string, Icon> osIcons = new Dictionary<string, Icon>();
        private static System.Windows.Forms.Timer iconRestore;
        private static Icon baseIcon;
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

            iconRestore = new System.Windows.Forms.Timer { Interval = 4000 };
            iconRestore.Tick += (s, e) =>
            {
                iconRestore.Stop();
                try { tray.Icon = baseIcon; } catch { }
            };

            timer = new System.Windows.Forms.Timer { Interval = 60000 };
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();

            debounce = new System.Threading.Timer(delegate { PostUpdate(); }, null, Timeout.Infinite, Timeout.Infinite);

            var watch = new Thread(WatchLoop) { IsBackground = true };
            watch.Start();

            UpdateStatus();
            Application.Run();
        }

        private static void UpdateStatus()
        {
            List<VMInfo> vms = GetVms();
            lastVms = vms;
            var running = new List<VMInfo>();
            foreach (var v in vms)
                if (v.Running) running.Add(v);

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

            string sig = "";
            foreach (var v in vms)
                sig += v.Name + ":" + (v.Running ? "1" : "0") + "|";
            if (sig != lastSig)
            {
                lastSig = sig;
                RebuildMenu(vms);
            }
        }

        private static void NotifyTransitions(List<VMInfo> vms)
        {
            try
            {
                var current = new Dictionary<string, bool>();
                foreach (var v in vms)
                    current[v.Guid] = v.Running;

                if (firstSync)
                {
                    firstSync = false;
                }
                else
                {
                    foreach (var kv in current)
                    {
                        bool prev;
                        if (!prevRun.TryGetValue(kv.Key, out prev)) continue;
                        if (kv.Value && !prev)
                        {
                            UseOsIcon(FindVm(vms, kv.Key));
                            tray.ShowBalloonTip(2500, "Hyper-V", FindName(vms, kv.Key) + " 已启动", ToolTipIcon.Info);
                        }
                        else if (!kv.Value && prev)
                        {
                            UseOsIcon(FindVm(vms, kv.Key));
                            tray.ShowBalloonTip(2500, "Hyper-V", FindName(vms, kv.Key) + " 已关闭", ToolTipIcon.Info);
                        }
                    }
                }

                prevRun.Clear();
                foreach (var kv in current)
                    prevRun[kv.Key] = kv.Value;
            }
            catch { }
        }

        private static string FindName(List<VMInfo> vms, string guid)
        {
            foreach (var v in vms)
                if (v.Guid == guid) return v.Name;
            return guid;
        }

        private static VMInfo FindVm(List<VMInfo> vms, string guid)
        {
            foreach (var v in vms)
                if (v.Guid == guid) return v;
            return null;
        }

        private static void UseOsIcon(VMInfo v)
        {
            try
            {
                if (v == null) return;
                Icon icon = GetOsIcon(v.OsType);
                if (icon != null) tray.Icon = icon;
                if (iconRestore != null)
                {
                    iconRestore.Stop();
                    iconRestore.Start();
                }
            }
            catch { }
        }

        private static Icon GetOsIcon(string osType)
        {
            try
            {
                if (string.IsNullOrEmpty(osType)) return null;
                Icon icon;
                if (osIcons.TryGetValue(osType, out icon)) return icon;
                string resName = "HyperVTray.OsIcons." + osType;
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    icon = new Icon(s);
                    osIcons[osType] = icon;
                    return icon;
                }
            }
            catch { return null; }
        }

        private static string ParseOsType(string notes)
        {
            try
            {
                if (string.IsNullOrEmpty(notes)) return "Windows";
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                    notes, @"OSType:([^\]]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) return "Windows";
                string t = m.Groups[1].Value.Trim();
                return t.Length == 0 ? "Windows" : t;
            }
            catch { return "Windows"; }
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
                            v.Running = (Convert.ToUInt16(mo["EnabledState"]) == 2);
                            if (v.Running) v.UpTimeSeconds = ComputeUpTime(mo["TimeOfLastStateChange"]);
                            if (!string.IsNullOrEmpty(v.Name)) list.Add(v);
                        }
                        catch { }
                    }
                }

                foreach (var v in list)
                    if (v.Running) EnrichVmInfo(scope, v);

                var notesMap = new Dictionary<string, string>();
                var notesQuery = new ObjectQuery("SELECT Name, Notes FROM Msvm_SummaryInformation");
                using (var searcher = new ManagementObjectSearcher(scope, notesQuery))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            string id = Convert.ToString(mo["Name"]);
                            string notes = Convert.ToString(mo["Notes"]);
                            if (!string.IsNullOrEmpty(id)) notesMap[id] = notes;
                        }
                        catch { }
                    }
                }
                foreach (var v in list)
                {
                    string notes;
                    if (notesMap.TryGetValue(v.Guid, out notes))
                        v.OsType = ParseOsType(notes);
                }
            }
            catch { }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return list;
        }

        private static long ComputeUpTime(object raw)
        {
            try
            {
                if (raw == null) return 0;
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
                var cpuQuery = new ObjectQuery("SELECT LoadPercentage FROM Msvm_Processor WHERE SystemName='" + v.Guid + "'");
                long total = 0, count = 0;
                using (var s = new ManagementObjectSearcher(scope, cpuQuery))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object val = mo["LoadPercentage"];
                        if (val != null) { total += Convert.ToInt64(val); count++; }
                    }
                }
                if (count > 0) v.CpuLoad = (int)(total / count);

                var memQuery = new ObjectQuery("SELECT VirtualQuantity FROM Msvm_MemorySettingData WHERE InstanceID LIKE '%" + v.Guid + "%'");
                using (var s = new ManagementObjectSearcher(scope, memQuery))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object val = mo["VirtualQuantity"];
                        if (val != null && Convert.ToInt64(val) > v.MemoryMB) v.MemoryMB = Convert.ToInt64(val);
                    }
                }
            }
            catch { }
        }

        private static void RebuildMenu(List<VMInfo> vms)
        {
            var menu = tray.ContextMenuStrip;
            menu.Items.Clear();

            var header = new ToolStripMenuItem("虚拟机") { Enabled = false };
            menu.Items.Add(header);

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
                        string detail = "CPU " + (v.CpuLoad >= 0 ? v.CpuLoad + "%" : "--");
                        detail += " | 内存 " + (v.MemoryMB > 0 ? (v.MemoryMB / 1024.0).ToString("0.#") + " GB" : "--");
                        long s0 = v.UpTimeSeconds;
                        detail += " | 已运行 " + string.Format("{0:D2}:{1:D2}:{2:D2}", s0 / 3600, (s0 / 60) % 60, s0 % 60);
                        vmItem.DropDownItems.Add(new ToolStripMenuItem(detail) { Enabled = false });
                    }

                    var open = new ToolStripMenuItem("打开 ExHyperV 界面");
                    open.Click += (s, e) => OpenExHyperV();
                    vmItem.DropDownItems.Add(open);

                    if (v.Running)
                    {
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
                    Process.Start(ExHyperVPath);
                }
            }
            catch { }
        }

        private static void StartThread(ThreadStart action)
        {
            var t = new System.Threading.Thread(action) { IsBackground = true };
            t.Start();
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
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(t);
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

                for (int i = 0; i < 15; i++)
                {
                    System.Threading.Thread.Sleep(2000);
                    if (!IsRunning(scope, guid)) return;
                }

                using (ManagementObject mo = GetVmObject(scope, guid))
                {
                    if (mo == null) return;
                    var p = mo.GetMethodParameters("RequestStateChange");
                    p["RequestedState"] = 3;
                    mo.InvokeMethod("RequestStateChange", p, null);
                }
            }
            catch { }
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

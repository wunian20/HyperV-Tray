using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;
        private const string ExHyperVPath = @"C:\ExHyperV_V1.5.0_x64\ExHyperV.exe";

        private static NotifyIcon tray;
        private static System.Windows.Forms.Timer timer;
        private static SynchronizationContext sync;
        private static readonly object gate = new object();
        private static long lastHandleTicks;
        private static string lastSig = "";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            sync = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(sync);

            tray = new NotifyIcon();
            tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Text = "Hyper-V 监控";
            tray.Visible = false;
            tray.DoubleClick += (s, e) => OpenExHyperV();
            tray.ContextMenuStrip = new ContextMenuStrip();

            timer = new System.Windows.Forms.Timer { Interval = 60000 };
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();

            var watch = new Thread(WatchLoop) { IsBackground = true };
            watch.Start();

            UpdateStatus();
            Application.Run();
        }

        private static void UpdateStatus()
        {
            List<VMInfo> vms = GetVms();
            var running = new List<VMInfo>();
            foreach (var v in vms)
                if (v.Running) running.Add(v);

            if (running.Count > 0)
            {
                if (!tray.Visible)
                {
                    tray.Visible = true;
                    tray.ShowBalloonTip(3000, "Hyper-V", "虚拟机正在运行: " + JoinNames(running), ToolTipIcon.Info);
                }
                string tip = "Hyper-V 运行中: " + JoinNames(running);
                if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
                tray.Text = tip;
            }
            else if (tray.Visible)
            {
                tray.Visible = false;
                tray.Text = "Hyper-V 监控";
            }

            string sig = "";
            foreach (var v in vms)
                sig += v.Name + ":" + (v.Running ? "1" : "0") + "|";
            if (sig != lastSig)
            {
                lastSig = sig;
                RebuildMenu(vms);
            }
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
                var query = new ObjectQuery("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem");
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
                            if (!string.IsNullOrEmpty(v.Name)) list.Add(v);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return list;
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

        private static void RaiseUpdate()
        {
            lock (gate)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - lastHandleTicks < TimeSpan.TicksPerSecond * 2) return;
                lastHandleTicks = now;
            }
            try { sync.Post(delegate { UpdateStatus(); }, null); }
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

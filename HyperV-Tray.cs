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
[assembly: AssemblyDescription("监视运行中的 Hyper-V 虚拟机，托盘菜单一键管理")]
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
        public IntPtr SubMenu;      // 所属 VM 子菜单句柄（用于每秒刷新详情行文本）
        public uint DetailId;       // 详情行菜单项 ID
        public long BaseSeconds;
        public long AllocatedMB;
        public bool DynamicMemory;
    }

    internal static class Program
    {
        // ---- Win32 原生菜单 P/Invoke（复刻 chrisant996/HyperVTray 的原生弹出菜单）----
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ModifyMenuW(IntPtr hMnu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2（Win10 1703+）：高 DPI 下按原生分辨率渲染，
        // 避免菜单/托盘图标被系统位图拉伸导致模糊
        private static readonly IntPtr DpiContextPerMonitorV2 = new IntPtr(-4);

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int appMode);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // 菜单项标志（WinUser.h MF_*）
        private const uint MF_STRING = 0x0000;
        private const uint MF_POPUP = 0x0010;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint MF_DISABLED = 0x0002;
        private const uint MF_GRAYED = 0x0001;
        private const uint MF_CHECKED = 0x0008;
        private const uint MF_BYCOMMAND = 0x0000;

        // TrackPopupMenu 标志（TPM_*）
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint TPM_RETURNCMD = 0x0100;

        private const uint WM_NULL = 0x0000;

        // 菜单 ID 规划：仿照 HyperVTray 的 idmBase + i*16 + op 编码。
        // 全局命令用低位 ID（1..8，0 保留表示取消），VM 区从 0x0100 起每台占 16 槽，
        // 菜单项 ID 底层是 16 位 WORD，故最多 4080 台，永不与全局 ID 冲突
        // （避免 VM 数量多时静默撞 ID 误触发批量操作）。
        private const uint IDM_FIRSTVM = 0x0100;
        private const uint VM_SLOT = 16;
        private const uint IDM_DETAIL = 15;      // 详情行占该 VM 槽的第 15 位（非命令，仅用于 ModifyMenuW 定位）
        private const uint OP_CONNECT = 0;       // 连接虚拟机
        private const uint OP_START = 1;         // 启动 / 恢复（已保存 / 已暂停 / 已关闭）
        private const uint OP_SAVE = 2;          // 保存虚拟机状态
        private const uint OP_STOP = 3;          // 关闭虚拟机
        private const uint OP_DISCARD = 4;       // 销毁保存的虚拟机
        private const uint IDM_STOPALL = 0x0001;
        private const uint IDM_SAVEALL = 0x0002;
        private const uint IDM_RESTOREALL = 0x0003;
        private const uint IDM_DISCARDALL = 0x0004;
        private const uint IDM_CONNECTALL = 0x0005;
        private const uint IDM_REFRESH = 0x0006;
        private const uint IDM_AUTOSTART = 0x0007;
        private const uint IDM_EXIT = 0x0008;
        private const uint IDM_CONFIRM_YES = 0x0009;   // 确认子菜单：确认执行
        private const uint IDM_CONFIRM_NO = 0x000A;     // 确认子菜单：取消

        private static readonly bool debugLog = string.Equals(
            Environment.GetEnvironmentVariable("HYPERV_TRAY_DEBUG"), "1", StringComparison.OrdinalIgnoreCase);
        private static readonly string StartupLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HyperV-Tray.lnk");

        private static NotifyIcon tray;
        private static AnchorWindow anchorWindow;
        private static Icon baseIcon;
        private static Icon greenIcon;
        private static Icon yellowIcon;
        private static Icon redIcon;
        private static FormsTimer refresh;
        private static FormsTimer blinkTimer;         // 混合状态（运行+保存）时绿↔黄交替闪烁
        private static bool blinking;                 // 当前是否处于闪烁中
        private static bool blinkGreen;               // 闪烁相位：true=绿，false=黄
        private static ThreadingTimer debounce;
        private static SynchronizationContext sync;
        private static Mutex mutex;
        private static ThreadingTimer uptimeTick;
        private static readonly List<UptimeEntry> uptimeEntries = new List<UptimeEntry>();
        private static DateTime menuOpenTime;
        private static volatile bool inContextMenu;   // 防 TrackPopupMenu 模态循环期间托盘消息重入
        private static volatile int menuGeneration;   // 菜单代际计数：关闭后使排队中的详情行刷新委托失效，避免对已销毁句柄操作
        private static readonly Dictionary<string, int> prevRun = new Dictionary<string, int>();
        private static volatile bool suppressNotify;
        private static volatile bool confirmOpen;
        private static volatile bool forceRefresh;
        private static readonly object cacheLock = new object();
        private static ManagementScope wmiScope;
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

        private static void TryEnableDpiAwareness()
        {
            // 必须在创建任何窗口或设置 DPI awareness 之前调用，否则返回 false；
            // 旧系统无此导出时抛 EntryPointNotFoundException，忽略即可（保持原渲染行为）
            try { SetProcessDpiAwarenessContext(DpiContextPerMonitorV2); }
            catch (Exception ex) { LogEx("DpiAwareness", ex); }
        }

        private static void TryEnableDarkMode()
        {
            // SetPreferredAppMode 需在创建任何窗口前调用；Win10 1809 之前不存在该导出，失败则忽略
            try { SetPreferredAppMode(1); }   // 1 = APPMODE_ALLOWDARK，让原生菜单/弹窗跟随系统深色
            catch (Exception ex) { LogEx("DarkMode", ex); }
        }

        [STAThread]
        private static void Main()
        {
            TryEnableDpiAwareness();   // 最优先：任何窗口/控件创建前声明 PerMonitorV2，高 DPI 下不模糊
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
            TryEnableDarkMode();   // 必须在创建任何窗口之前调用，否则原生菜单/弹窗不跟随系统深色

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
            tray.MouseUp += (s, e) =>
            {
                // 左右键均弹出原生菜单；TrackPopupMenu 前先激活隐藏窗口，否则菜单无法正确消失
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                    ShowTrayMenu();
            };
            anchorWindow = new AnchorWindow();
            anchorWindow.CreateHandle(new CreateParams { Caption = "HyperV-Tray" });

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

            blinkTimer = new FormsTimer { Interval = 800 };
            blinkTimer.Tick += (s, e) => BlinkTick(s, e);

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
            if (forceRefresh)
            {
                // WMI 事件驱动的刷新：先清缓存强制重新查询，图标/通知秒级响应
                forceRefresh = false;
                lock (cacheLock) cachedVms = null;
            }
            List<VMInfo> vms = GetVms();
            if (vms == null)
            {
                // WMI 查询失败：红色图标 + 明确提示，避免被误认为“没有虚拟机”
                StopBlink();
                tray.Icon = redIcon != null ? redIcon : baseIcon;
                tray.Text = "Hyper-V 监控（查询失败）";
                tray.Visible = true;
                return;
            }

            var running = new List<VMInfo>();
            int savedCount = 0;
            foreach (var v in vms)
            {
                if (v.Running) running.Add(v);
                if (v.StateCode == 6) savedCount++;
            }

            if (!IsHyperVServiceRunning()) { StopBlink(); tray.Icon = redIcon != null ? redIcon : baseIcon; }
            else if (running.Count > 0 && savedCount > 0) { StartBlink(); }   // 运行+保存混合：绿↔黄交替闪烁提醒
            else if (running.Count > 0 && greenIcon != null) { StopBlink(); tray.Icon = greenIcon; }
            else if (savedCount > 0 && yellowIcon != null) { StopBlink(); tray.Icon = yellowIcon; }
            else { StopBlink(); tray.Icon = baseIcon; }
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

        // ---- 混合状态（运行+保存）的绿↔黄交替闪烁 ----
        private static void StartBlink()
        {
            if (blinking) return;
            blinking = true;
            blinkGreen = true;
            tray.Icon = greenIcon ?? yellowIcon ?? baseIcon ?? SystemIcons.Application;
            blinkTimer.Start();
        }

        private static void StopBlink()
        {
            if (!blinking) return;
            blinking = false;
            blinkTimer.Stop();
        }

        private static void BlinkTick(object sender, EventArgs e)
        {
            if (!blinking) return;   // StopBlink 后已入队的 WM_TIMER 仍可能触发一次，直接忽略
            blinkGreen = !blinkGreen;
            tray.Icon = (blinkGreen ? greenIcon : yellowIcon) ?? baseIcon ?? SystemIcons.Application;
        }

        private static void ShowTrayMenu()
        {
            // TrackPopupMenu 模态循环期间托盘消息仍会派发，防止重入导致嵌套菜单
            if (inContextMenu) return;
            inContextMenu = true;
            IntPtr hmenu = IntPtr.Zero;
            try
            {
                ++menuGeneration;
                List<VMInfo> vms = GetVms();
                hmenu = BuildMenu(vms);
                if (hmenu == IntPtr.Zero) return;

                menuOpenTime = DateTime.Now;
                bool hasRunning;
                lock (uptimeEntries) hasRunning = uptimeEntries.Count > 0;
                uptimeTick.Change(hasRunning ? 0 : Timeout.Infinite, hasRunning ? 1000 : Timeout.Infinite);

                // 先激活隐藏窗口再弹菜单：TrackPopupMenu 的模态循环依赖前台窗口才能正确获得/释放输入焦点
                SetForegroundWindow(anchorWindow.Handle);
                POINT pt;
                GetCursorPos(out pt);
                uint id = TrackPopupMenu(hmenu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD,
                    pt.X, pt.Y, 0, anchorWindow.Handle, IntPtr.Zero);
                // 菜单消失 workaround：模态循环结束后补发一条 WM_NULL 让菜单正确收尾
                PostMessage(anchorWindow.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                // 破坏性批量操作：弹出原生确认子菜单，确认后才真正执行
                if (IsBatchConfirm(id))
                {
                    // 确认菜单模态期间无需刷新详情行：先停计时器并清空条目，避免对已关闭的不可见菜单做无用更新
                    uptimeTick.Change(Timeout.Infinite, Timeout.Infinite);
                    lock (uptimeEntries) uptimeEntries.Clear();
                    if (!ConfirmBatchAction(id))
                        return;   // 用户取消确认，不执行
                }

                HandleCommand(id, vms);
            }
            catch (Exception ex) { LogEx("ShowTrayMenu", ex); }
            finally
            {
                menuGeneration++;   // 使排队中的详情行刷新委托失效，避免对已销毁句柄 ModifyMenuW
                uptimeTick.Change(Timeout.Infinite, Timeout.Infinite);
                lock (uptimeEntries) uptimeEntries.Clear();
                if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
                inContextMenu = false;
            }
        }

        private static string EscapeAmp(string text)
        {
            // 菜单文本中的 & 会被当作加速键前缀吞掉，转义为 && 以原样显示
            return text.Replace("&", "&&");
        }

        // 破坏性批量操作的原生确认子菜单：提示行 + 确认执行/取消；返回 true 表示用户确认执行
        private static bool ConfirmBatchAction(uint id)
        {
            IntPtr hmenu = CreatePopupMenu();
            if (hmenu == IntPtr.Zero) return false;
            try
            {
                AppendMenuW(hmenu, MF_STRING | MF_DISABLED | MF_GRAYED, UIntPtr.Zero,
                    "确认" + BatchItemText(id) + "？");
                AppendMenuW(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);
                // “取消”在前：破坏性操作下回车默认落到取消，避免误触确认
                AppendMenuW(hmenu, MF_STRING, (UIntPtr)IDM_CONFIRM_NO, "取消");
                AppendMenuW(hmenu, MF_STRING, (UIntPtr)IDM_CONFIRM_YES, "确认执行");

                // 与主菜单同样的弹出协议：前台窗口 + TrackPopupMenu + WM_NULL 收尾
                SetForegroundWindow(anchorWindow.Handle);
                POINT pt;
                GetCursorPos(out pt);
                uint result = TrackPopupMenu(hmenu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD,
                    pt.X, pt.Y, 0, anchorWindow.Handle, IntPtr.Zero);
                PostMessage(anchorWindow.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                return result == IDM_CONFIRM_YES;
            }
            finally { DestroyMenu(hmenu); }
        }

        private static string BatchItemText(uint id)
        {
            switch (id)
            {
                case IDM_SAVEALL: return "保存全部虚拟机";
                case IDM_STOPALL: return "关闭全部虚拟机";
                case IDM_DISCARDALL: return "销毁所有保存的虚拟机";
                default: return "";
            }
        }

        private static bool IsBatchConfirm(uint id)
        {
            return id == IDM_SAVEALL || id == IDM_STOPALL || id == IDM_DISCARDALL;
        }

        private static void TickUptime()
        {
            try
            {
                UptimeEntry[] entries;
                lock (uptimeEntries) entries = uptimeEntries.ToArray();
                if (entries.Length == 0) return;

                var scope = GetScope();
                long nowSec = (long)(DateTime.Now - menuOpenTime).TotalSeconds;
                int gen = menuGeneration;   // 捕获打开本菜单时的代际，菜单关闭后不再更新已销毁句柄

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
                        if (gen != menuGeneration) return;   // 菜单已关闭，句柄已销毁，丢弃本次刷新
                        for (int i = 0; i < entries.Length; i++)
                        {
                            UptimeEntry e = entries[i];
                            ModifyMenuW(e.SubMenu, e.DetailId,
                                MF_BYCOMMAND | MF_STRING | MF_DISABLED | MF_GRAYED,
                                (UIntPtr)e.DetailId, texts[i]);
                        }
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

        private static ManagementScope GetScope()
        {
            if (wmiScope == null)
            {
                wmiScope = new ManagementScope(@"\\.\root\virtualization\v2");
                wmiScope.Connect();
            }
            return wmiScope;
        }

        private static List<VMInfo> GetVms()
        {
            // 1.5 秒内复用上次查询结果：右键弹菜单、状态刷新立即响应，避免每次重复 WMI 查询卡顿
            lock (cacheLock)
            {
                if (cachedVms != null && (DateTime.Now - cacheTime).TotalSeconds < 1.5)
                    return cachedVms;
            }

            var list = new List<VMInfo>();
            try
            {
                var scope = GetScope();
                var query = new ObjectQuery("SELECT Name, ElementName, EnabledState, TimeOfLastStateChange, OnTimeInMilliseconds FROM Msvm_ComputerSystem");
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
                            if (v.Running)
                            {
                                // 优先用 OnTimeInMilliseconds：单位明确(毫秒)、语义是“上次开机/重置/恢复以来”且排除暂停时间；
                                // 老系统无此属性时兜底用 TimeOfLastStateChange 差值（秒，语义为“状态变更时刻”，宿主睡眠/校时下可能虚增）
                                object onTime = mo["OnTimeInMilliseconds"];
                                if (onTime != null && Convert.ToInt64(onTime) > 0)
                                    v.UpTimeSeconds = Convert.ToInt64(onTime) / 1000;
                                else
                                    v.UpTimeSeconds = ComputeUpTime(mo["TimeOfLastStateChange"]);
                            }
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
            lock (cacheLock)
            {
                cachedVms = list;
                cacheTime = DateTime.Now;
            }
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
                // 一次 Msvm_SummaryInformation 查询同时取 CPU、内存，替代多次查询
                var q = new ObjectQuery("SELECT ProcessorLoad, MemoryUsage FROM Msvm_SummaryInformation WHERE Name='" + v.Guid + "'");
                using (var s = new ManagementObjectSearcher(scope, q))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object load = mo["ProcessorLoad"];
                        if (load != null) v.CpuLoad = Convert.ToInt32(load);
                        object mem = mo["MemoryUsage"];
                        if (mem != null) v.MemoryUsedMB = Convert.ToInt64(mem);
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

        private static IntPtr BuildMenu(List<VMInfo> vms)
        {
            IntPtr hmenu = CreatePopupMenu();
            if (hmenu == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                lock (uptimeEntries) uptimeEntries.Clear();

                bool hasRunning = false;
                bool anySaved = false;
                bool hvEnabled = IsHyperVServiceRunning();

                AppendMenuW(hmenu, MF_STRING | MF_DISABLED | MF_GRAYED, UIntPtr.Zero,
                    (hvEnabled ? "已启用" : "未启用") + " Hyper-V 服务");
                AppendMenuW(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenuW(hmenu, MF_STRING | MF_DISABLED | MF_GRAYED, UIntPtr.Zero, "虚拟机");
                AppendMenuW(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);

                if (vms == null)
                {
                    AppendMenuW(hmenu, MF_STRING | MF_DISABLED | MF_GRAYED, UIntPtr.Zero,
                        "(查询失败，请点“立即刷新”重试)");
                }
                else if (vms.Count == 0)
                {
                    AppendMenuW(hmenu, MF_STRING | MF_DISABLED | MF_GRAYED, UIntPtr.Zero, "(没有虚拟机)");
                }
                else
                {
                    for (int i = 0; i < vms.Count; i++)
                    {
                        uint idmBase = IDM_FIRSTVM + (uint)i * VM_SLOT;
                        // 菜单项 ID 底层为 16 位 WORD：i ≥ 4080 时 idmBase 回绕为 0 会撞上全局命令 ID，直接截断
                        if (idmBase + VM_SLOT > 0x10000)
                        {
                            Log("BuildMenu: VM 数量超过上限，菜单已截断");
                            break;
                        }

                        VMInfo v = vms[i];
                        string label = EscapeAmp(v.Name + "  [" + StateText(v) + "]");

                        IntPtr hsub = CreatePopupMenu();
                        if (hsub == IntPtr.Zero) continue;

                        if (v.Running)
                        {
                            var entry = new UptimeEntry();
                            entry.Guid = v.Guid;
                            entry.SubMenu = hsub;
                            entry.DetailId = idmBase + IDM_DETAIL;
                            entry.BaseSeconds = v.UpTimeSeconds;
                            entry.AllocatedMB = v.MemoryLimitMB;
                            entry.DynamicMemory = v.DynamicMemory;
                            AppendMenuW(hsub, MF_STRING | MF_DISABLED | MF_GRAYED,
                                (UIntPtr)entry.DetailId,
                                "CPU " + (v.CpuLoad >= 0 ? v.CpuLoad + "%" : "--")
                                + " | " + MemInfo(v.MemoryUsedMB, v.MemoryLimitMB, v.DynamicMemory)
                                + " | 已运行 " + FormatUptime(v.UpTimeSeconds));
                            lock (uptimeEntries) uptimeEntries.Add(entry);
                            hasRunning = true;
                        }

                        AppendMenuW(hsub, MF_STRING, (UIntPtr)(idmBase + OP_CONNECT), "连接虚拟机");

                        if (v.Running)
                        {
                            AppendMenuW(hsub, MF_STRING, (UIntPtr)(idmBase + OP_SAVE), "保存虚拟机状态");
                            AppendMenuW(hsub, MF_STRING, (UIntPtr)(idmBase + OP_STOP), "关闭虚拟机");
                        }
                        else if (v.StateCode == 6)
                        {
                            anySaved = true;
                            AppendMenuW(hsub, MF_STRING, (UIntPtr)(idmBase + OP_START), "恢复虚拟机");
                            AppendMenuW(hsub, MF_STRING, (UIntPtr)(idmBase + OP_DISCARD), "销毁保存的虚拟机");
                        }
                        else if (IsPaused(v))
                        {
                            // 已暂停：只能恢复（请求回到运行态），不提供其他操作
                            AppendMenuW(hsub, MF_STRING, (UIntPtr)(idmBase + OP_START), "恢复虚拟机");
                        }
                        else if (v.StateCode == 3)
                        {
                            AppendMenuW(hsub, MF_STRING, (UIntPtr)(idmBase + OP_START), "启动虚拟机");
                        }
                        // 过渡状态（启动中/停止中/暂停中）及未知状态：仅提供连接，避免误操作

                        if (!AppendMenuW(hmenu, MF_POPUP, new UIntPtr((ulong)hsub.ToInt64()), label))
                        {
                            // 挂载失败：销毁子菜单并同步移除已登记的详情行条目，避免刷新时操作悬垂句柄
                            lock (uptimeEntries) uptimeEntries.RemoveAll(e => e.SubMenu == hsub);
                            DestroyMenu(hsub);
                        }
                    }
                }

                AppendMenuW(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);

                AppendMenuW(hmenu, MF_STRING | (hasRunning ? 0 : MF_DISABLED | MF_GRAYED),
                    (UIntPtr)IDM_STOPALL, "关闭全部虚拟机");
                AppendMenuW(hmenu, MF_STRING | (hasRunning ? 0 : MF_DISABLED | MF_GRAYED),
                    (UIntPtr)IDM_SAVEALL, "保存全部虚拟机");
                AppendMenuW(hmenu, MF_STRING | (anySaved ? 0 : MF_DISABLED | MF_GRAYED),
                    (UIntPtr)IDM_RESTOREALL, "恢复所有保存的虚拟机");
                AppendMenuW(hmenu, MF_STRING | (anySaved ? 0 : MF_DISABLED | MF_GRAYED),
                    (UIntPtr)IDM_DISCARDALL, "销毁所有保存的虚拟机");
                AppendMenuW(hmenu, MF_STRING | (hasRunning ? 0 : MF_DISABLED | MF_GRAYED),
                    (UIntPtr)IDM_CONNECTALL, "连接所有运行中的虚拟机");

                AppendMenuW(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);

                AppendMenuW(hmenu, MF_STRING, (UIntPtr)IDM_REFRESH, "立即刷新");
                AppendMenuW(hmenu, MF_STRING | (File.Exists(StartupLnk) ? MF_CHECKED : 0),
                    (UIntPtr)IDM_AUTOSTART, "开机自启");
                AppendMenuW(hmenu, MF_STRING, (UIntPtr)IDM_EXIT, "退出");
            }
            catch (Exception ex)
            {
                LogEx("BuildMenu", ex);
                lock (uptimeEntries) uptimeEntries.Clear();
                DestroyMenu(hmenu);
                return IntPtr.Zero;
            }

            return hmenu;
        }

        private static void HandleCommand(uint id, List<VMInfo> vms)
        {
            try
            {
                switch (id)
                {
                    case 0: return;   // 用户取消了菜单
                    case IDM_EXIT:
                        tray.Visible = false;
                        tray.Dispose();
                        Application.Exit();
                        return;
                    case IDM_REFRESH:
                        UpdateStatus();
                        return;
                    case IDM_AUTOSTART:
                        ToggleAutostart();
                        return;
                    case IDM_STOPALL:
                        // 两步确认已在 ShowTrayMenu 菜单层完成，这里直接执行
                        StopAllVms();
                        return;
                    case IDM_SAVEALL:
                        // 两步确认已在 ShowTrayMenu 菜单层完成，这里直接执行
                        SaveAllVms();
                        return;
                    case IDM_RESTOREALL:
                        RestoreAllSaved();
                        return;
                    case IDM_DISCARDALL:
                        // 两步确认已在 ShowTrayMenu 菜单层完成，这里直接执行
                        DiscardAllSaved();
                        return;
                    case IDM_CONNECTALL:
                        ConnectAllRunning();
                        return;
                }

                if (id >= IDM_FIRSTVM && vms != null)
                {
                    uint off = id - IDM_FIRSTVM;
                    int index = (int)(off / VM_SLOT);
                    uint op = off % VM_SLOT;
                    if (index >= 0 && index < vms.Count)
                    {
                        VMInfo v = vms[index];
                        switch (op)
                        {
                            case OP_CONNECT: ConnectVm(v.Name); break;
                            case OP_START: StartThread(delegate { StartVm(v.Guid); }); break;
                            case OP_SAVE: StartThread(delegate { SaveVm(v.Guid); }); break;
                            case OP_STOP: StartThread(delegate { StopVm(v.Guid); }); break;
                            case OP_DISCARD:
                                if (Confirm("确定要销毁「" + v.Name + "」的保存状态吗？"))
                                    StartThread(delegate { DiscardSavedVm(v.Guid); });
                                break;
                        }
                    }
                }
            }
            catch (Exception ex) { LogEx("HandleCommand", ex); }
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
            try
            {
                forceRefresh = true;
                sync.Post(delegate { UpdateStatus(); }, null);
            }
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
                var scope = GetScope();
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
                var scope = GetScope();
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
                var scope = GetScope();
                RequestState(scope, guid, 3);
            }
            catch (Exception ex) { LogEx("DiscardSavedVm", ex); }
        }

        // 批量操作收尾（UI 线程执行）：先同步状态基准、再关闭抑制、最后弹汇总。
        // 若先关抑制，批量结束后迟到的状态事件会把 prevRun 里的旧状态当成刚变化，误弹单 VM 通知
        private static void FinishBatch(string text, List<string> guids, int finalState)
        {
            try
            {
                foreach (string guid in guids)
                    prevRun[guid] = finalState;
                suppressNotify = false;
                ShowBalloonText(text);
            }
            catch (Exception ex) { LogEx("FinishBatch", ex); }
        }

        private static void SaveAllVms()
        {
            suppressNotify = true;
            StartThread(delegate
            {
                try
                {
                    var scope = GetScope();
                    var list = GetVmsByState(scope, 2);
                    if (list.Count == 0) { suppressNotify = false; return; }

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

                    sync.Post(delegate { FinishBatch("已保存全部虚拟机", list, 6); }, null);
                }
                catch (Exception ex)
                {
                    LogEx("SaveAllVms", ex);
                    sync.Post(delegate { suppressNotify = false; }, null);
                }
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

            // 优先复用 GetVms 的 1.5 秒缓存，避免批量操作重复全量查询
            lock (cacheLock)
            {
                if (cachedVms != null && (DateTime.Now - cacheTime).TotalSeconds < 1.5)
                {
                    foreach (var v in cachedVms)
                        if (v.StateCode == state) list.Add(v.Guid);
                    return list;
                }
            }

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
                    var scope = GetScope();
                    var list = GetVmsByState(scope, 6);
                    if (list.Count == 0) { suppressNotify = false; return; }

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

                    sync.Post(delegate { FinishBatch("已恢复所有虚拟机", list, 2); }, null);
                }
                catch (Exception ex)
                {
                    LogEx("RestoreAllSaved", ex);
                    sync.Post(delegate { suppressNotify = false; }, null);
                }
            });
        }

        private static void DiscardAllSaved()
        {
            suppressNotify = true;
            StartThread(delegate
            {
                try
                {
                    var scope = GetScope();
                    var list = GetVmsByState(scope, 6);
                    if (list.Count == 0) { suppressNotify = false; return; }

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

                    sync.Post(delegate { FinishBatch("已销毁全部保存虚拟机", list, 3); }, null);
                }
                catch (Exception ex)
                {
                    LogEx("DiscardAllSaved", ex);
                    sync.Post(delegate { suppressNotify = false; }, null);
                }
            });
        }

        private static void StopVm(string guid)
        {
            try
            {
                var scope = GetScope();
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
                    var scope = GetScope();
                    var list = GetVmsByState(scope, 2);
                    if (list.Count == 0) { suppressNotify = false; return; }

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

                    sync.Post(delegate { FinishBatch("已关闭所有虚拟机", list, 3); }, null);
                }
                catch (Exception ex)
                {
                    LogEx("StopAllVms", ex);
                    sync.Post(delegate { suppressNotify = false; }, null);
                }
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

        // 用于激活菜单的隐藏窗口（仅需句柄，无需处理消息）
        private sealed class AnchorWindow : NativeWindow
        {
        }
    }
}

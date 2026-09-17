using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SolidEdge.Spy.McpServer
{
    /// <summary>Solid Edge 进程里当前挂着的一个对话框窗口。</summary>
    public sealed class DialogWindow
    {
        public long Hwnd { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }

        /// <summary>
        /// 是否模态:该对话框挡住了 SE 主窗口(主窗口被禁用),
        /// 意味着 Solid Edge 的 COM 调用也会被一起堵住。
        /// </summary>
        public bool Modal { get; set; }

        public long OwnerHwnd { get; set; }
        public string OwnerTitle { get; set; }
        public string ProcessName { get; set; }

        /// <summary>对话框当前是不是前台窗口(更可能是"刚弹出来、在等人工处理"的那个)。</summary>
        public bool Foreground { get; set; }

        /// <summary>按钮文本(如"确定"/"取消"/"是"/"否"),用来判断点掉它会有什么后果。</summary>
        public List<string> Buttons { get; set; } = new List<string>();

        /// <summary>对话框里的静态文本(提示信息正文)前若干条。</summary>
        public List<string> Texts { get; set; } = new List<string>();

        /// <summary>稳定标识(句柄+标题),用于跨两次快照判断"是不是新冒出来的"。</summary>
        public string Key
        {
            get { return Hwnd + "|" + (Title ?? ""); }
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("「").Append(string.IsNullOrWhiteSpace(Title) ? "(无标题对话框)" : Title).Append("」");
            sb.Append("(hwnd=").Append(Hwnd).Append(Modal ? ",模态" : ",非模态");
            if (Buttons != null && Buttons.Count > 0)
            {
                sb.Append(",按钮:").Append(string.Join("/", Buttons.ToArray()));
            }
            sb.Append(")");
            return sb.ToString();
        }
    }

    /// <summary>某一时刻 Solid Edge 窗口状态的进程外快照。</summary>
    public sealed class WindowSnapshot
    {
        public bool SeRunning { get; set; }

        /// <summary>当前挂着的对话框(可见窗口,含模态与非模态)。</summary>
        public List<DialogWindow> Dialogs { get; set; } = new List<DialogWindow>();

        /// <summary>SE 进程下所有可见顶层窗口的"类名 | 标题"(诊断用)。</summary>
        public List<string> SeWindows { get; set; } = new List<string>();

        public bool HasModalDialog
        {
            get { return Dialogs.Any(d => d.Modal); }
        }

        public HashSet<string> KeySet()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DialogWindow d in Dialogs)
            {
                set.Add(d.Key);
            }
            return set;
        }
    }

    /// <summary>
    /// 进程外窗口探针:用 Win32 窗口枚举(EnumWindows)看 Solid Edge 现在挂没挂对话框。
    ///
    /// 为什么必须有它:MCP 工具走 COM 调 Solid Edge,一旦 SE 弹了模态对话框,
    /// 该 COM 调用就会被一起堵住(请求-响应模型下 AI 只能干等到超时)。
    /// 而窗口枚举是**独立于 COM 的进程外手段**——SE 卡在对话框上时,
    /// 这个探针照样能秒回,于是"弹窗导致卡死"就能被及时发现并报告。
    /// </summary>
    public static class WindowProbe
    {
        private const uint GW_OWNER = 4;
        private const uint WM_CLOSE = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", SetLastError = false)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW", SetLastError = false)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW", SetLastError = false)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // 进程映像路径缓存(pid → 路径),避免每次快照都取 MainModule(有系统开销,且可能抛权限异常)。
        private static readonly Dictionary<uint, string> _imageCache = new Dictionary<uint, string>();
        private static readonly object _imageCacheLock = new object();

        /// <summary>
        /// 采一次快照。纯 Win32 调用,不碰 COM —— SE 卡在对话框上时同样可用。
        /// 任何异常都被吞掉并返回"未发现 SE"(探针自身的故障绝不能影响主流程)。
        /// </summary>
        public static WindowSnapshot Snapshot()
        {
            WindowSnapshot snapshot = new WindowSnapshot();
            List<WinInfo> topLevel = new List<WinInfo>();

            EnumWindows(delegate (IntPtr hwnd, IntPtr lparam)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                topLevel.Add(new WinInfo
                {
                    Hwnd = hwnd,
                    Pid = pid,
                    ClassName = ClassOf(hwnd),
                    Title = TextOf(hwnd),
                    Visible = IsWindowVisible(hwnd)
                });
                return true;
            }, IntPtr.Zero);

            // ① 认出哪些进程是 Solid Edge
            HashSet<uint> sePids = new HashSet<uint>();
            foreach (WinInfo w in topLevel)
            {
                if (IsSolidEdgeWindow(w, sePids.Contains(w.Pid)))
                {
                    sePids.Add(w.Pid);
                }
            }
            snapshot.SeRunning = sePids.Count > 0;
            if (!snapshot.SeRunning)
            {
                return snapshot;
            }

            // ② 找 SE 主框架窗口(判"是否被模态框挡住"要用它)
            IntPtr mainHwnd = IntPtr.Zero;
            foreach (WinInfo w in topLevel)
            {
                if (!w.Visible || !sePids.Contains(w.Pid))
                {
                    continue;
                }
                snapshot.SeWindows.Add((w.ClassName + " | " + w.Title).Trim(' ', '|'));
                if (mainHwnd == IntPtr.Zero && IsMainFrameClass(w.ClassName))
                {
                    mainHwnd = w.Hwnd;
                }
            }
            if (mainHwnd == IntPtr.Zero)
            {
                mainHwnd = MainWindowHandleOf(sePids);
            }
            bool mainDisabled = mainHwnd != IntPtr.Zero && !IsWindowEnabled(mainHwnd);

            // ③ 挑出对话框
            IntPtr foreground = GetForegroundWindow();
            foreach (WinInfo w in topLevel)
            {
                if (!w.Visible || !sePids.Contains(w.Pid))
                {
                    continue;
                }
                if (IsMainFrameClass(w.ClassName))
                {
                    continue; // 主框架自己不算对话框
                }

                IntPtr owner = GetWindow(w.Hwnd, GW_OWNER);
                bool dialogClass = IsDialogClass(w.ClassName);
                if (!dialogClass && owner == IntPtr.Zero)
                {
                    continue; // 无归属又不是对话框类(启动画面/工具窗),跳过
                }

                bool ownerDisabled = owner != IntPtr.Zero && !IsWindowEnabled(owner);
                DialogWindow dlg = new DialogWindow
                {
                    Hwnd = w.Hwnd.ToInt64(),
                    Title = w.Title,
                    ClassName = w.ClassName,
                    OwnerHwnd = owner.ToInt64(),
                    OwnerTitle = owner != IntPtr.Zero ? TextOf(owner) : "",
                    ProcessName = ProcessNameOf(w.Pid),
                    Foreground = w.Hwnd == foreground
                };
                CollectChildText(w.Hwnd, dlg);

                // 像不像对话框:标准对话框类(#32770 等),或带按钮的归属窗口。
                // 这条收紧很关键 —— SE 的浮动停靠面板(如"路径查找器" XTPDockingPaneMiniWnd)
                // 也有 owner,而且在主窗口被模态框禁用时会"看着像",但它既不是对话框类、
                // 也没有按钮,不能用"owner 被禁用"当判据(实测会误报)。
                bool hasButtons = dlg.Buttons.Count > 0;
                if (!dialogClass && !hasButtons)
                {
                    continue;
                }

                // 模态判据:owner 被禁用;或标准对话框且 SE 主窗口被禁用;
                // 或"SE 主窗口自己弹出、带按钮的窗口"(自定义报警框常见形态)。
                dlg.Modal = ownerDisabled
                    || (dialogClass && mainDisabled)
                    || (hasButtons && mainHwnd != IntPtr.Zero && owner == mainHwnd);
                snapshot.Dialogs.Add(dlg);
            }

            return snapshot;
        }

        /// <summary>给对话框发 WM_CLOSE(等同人工点窗口右上角的 X)。只投递消息,不等结果。</summary>
        public static bool CloseDialog(long hwnd)
        {
            if (hwnd <= 0)
            {
                return false;
            }
            IntPtr h = new IntPtr(hwnd);
            if (!IsWindowVisible(h))
            {
                return false;
            }
            return PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private static bool IsSolidEdgeWindow(WinInfo w, bool pidAlreadyKnown)
        {
            if (pidAlreadyKnown)
            {
                return true;
            }
            if (IsMainFrameClass(w.ClassName))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(w.Title) && w.Title.StartsWith("Solid Edge", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string image = ProcessImageOf(w.Pid);
            return image != null && image.IndexOf("Solid Edge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// SE 主框架窗口的窗口类名。实测 SE2022 = "EngineFrame"
        /// (其标题才是 "Solid Edge 2022 - 装配 - [装配3]"),这里两种都认。
        /// </summary>
        private static bool IsMainFrameClass(string className)
        {
            if (string.IsNullOrEmpty(className))
            {
                return false;
            }
            return className.IndexOf("EngineFrame", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Solid Edge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDialogClass(string className)
        {
            if (string.IsNullOrEmpty(className))
            {
                return false;
            }
            return className.IndexOf("32770", StringComparison.Ordinal) >= 0
                || className.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void CollectChildText(IntPtr parent, DialogWindow dlg)
        {
            List<string> buttons = new List<string>();
            List<string> texts = new List<string>();
            EnumChildWindows(parent, delegate (IntPtr child, IntPtr lparam)
            {
                string cls = ClassOf(child);
                string txt = TextOf(child);
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    string trimmed = txt.Trim();
                    if (cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (buttons.Count < 12 && !buttons.Contains(trimmed))
                        {
                            buttons.Add(trimmed);
                        }
                    }
                    else if (texts.Count < 15)
                    {
                        texts.Add(trimmed);
                    }
                }
                return true;
            }, IntPtr.Zero);
            dlg.Buttons = buttons;
            dlg.Texts = texts;
        }

        private static IntPtr MainWindowHandleOf(HashSet<uint> pids)
        {
            foreach (uint pid in pids)
            {
                try
                {
                    using (Process p = Process.GetProcessById((int)pid))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            return p.MainWindowHandle;
                        }
                    }
                }
                catch
                {
                }
            }
            return IntPtr.Zero;
        }

        private static string ProcessImageOf(uint pid)
        {
            lock (_imageCacheLock)
            {
                string cached;
                if (_imageCache.TryGetValue(pid, out cached))
                {
                    return cached;
                }
            }
            string image = null;
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    image = p.MainModule.FileName;
                }
            }
            catch
            {
                image = null;
            }
            lock (_imageCacheLock)
            {
                _imageCache[pid] = image;
            }
            return image;
        }

        private static string ProcessNameOf(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    return p.ProcessName;
                }
            }
            catch
            {
                return "";
            }
        }

        private static string ClassOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TextOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private sealed class WinInfo
        {
            public IntPtr Hwnd;
            public uint Pid;
            public string ClassName;
            public string Title;
            public bool Visible;
        }
    }
}

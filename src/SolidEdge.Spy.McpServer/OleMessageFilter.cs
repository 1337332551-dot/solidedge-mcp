using System;
using System.Runtime.InteropServices;
using System.Threading;
using SolidEdge.Spy.InteropServices;

namespace SolidEdge.Spy.McpServer
{
    /// <summary>
    /// OLE 消息过滤器:处理 Solid Edge 忙时的 COM 调用重入。
    /// 注意:原项目 InteropServices\OleMessageFilter.cs 整个文件被注释掉了,
    /// 这里在 MCP server 项目内新建一份功能等价的实现,不改原文件。
    /// 必须在 STA 线程上注册。
    /// </summary>
    internal sealed class OleMessageFilter : IMessageFilter
    {
        private OleMessageFilter() { }

        /// <summary>在当前 STA 线程注册消息过滤器。</summary>
        public static void Register()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                throw new InvalidOperationException("OLE 消息过滤器只能在 STA 线程上注册。");
            }

            IMessageFilter oldFilter;
            int hr = NativeMethods.CoRegisterMessageFilter(new OleMessageFilter(), out oldFilter);
            if (hr < 0)
            {
                // 注册失败一般是因为该线程已注册过,忽略即可
            }
        }

        /// <summary>注销当前线程的消息过滤器。</summary>
        public static void Unregister()
        {
            IMessageFilter oldFilter;
            NativeMethods.CoRegisterMessageFilter(null, out oldFilter);
        }

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        {
            // 接受所有入站调用
            return (int)NativeMethods.SERVERCALL.SERVERCALL_ISHANDLED;
        }

        public int RetryRejectedCall(IntPtr hTaskCaller, int dwTickCount, int dwRejectType)
        {
            // SE 忙时(RETRYLATER)立即重试。返回 0~99 表示立刻重试。
            if (dwRejectType == (int)NativeMethods.SERVERCALL.SERVERCALL_RETRYLATER)
            {
                return 99;
            }
            // 其它情况取消调用
            return -1;
        }

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        {
            // 继续等待,不分发鼠标/键盘消息(避免误操作)
            return (int)NativeMethods.PENDINGMSG.PENDINGMSG_WAITDEFPROCESS;
        }
    }
}

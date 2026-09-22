using System;
using System.Collections.Generic;

namespace SolidEdge.Spy.McpServer
{
    /// <summary>
    /// COM 副作用成员共享黑名单(两线合流:本地 ComSideEffectGuard 与远端
    /// BlockedMembers 条目完全一致,统一收敛到本类)。
    ///
    /// 背景(2026-09 真机实证):读取/调用这些成员会激活邮件·会签子系统(MAPI):
    /// 在未配置邮件系统的机器上 SE 弹出"会签单不可用"模态对话框并堵死整条 STA
    /// COM 通道(实测单次阻塞 27s)。
    ///
    /// 此名单必须被所有「主动触碰 COM 成员」的路径共享,缺一处就会踩雷:
    ///   - ObjectExplorer(FindPaths/FindByName 路径遍历)
    ///   - WalkTools / SelectionTools / SnapshotTools 属性枚举
    ///   - se_invoke_member / se_invoke_chain(InvokeTools.RunStep)
    /// 历史教训:MailSession 黑名单曾散落多处各自维护,find_paths 的 name 反查
    /// 模式仍然踩中未设防的 RoutingSlip 副作用成员——新发现一律 AddBlocked
    /// 收敛到这里,不许在调用点各自为政。
    /// </summary>
    public static class BlockedMembers
    {
        /// <summary>已知会触发全局副作用的 COM 成员名(大小写不敏感比较)。</summary>
        private static readonly HashSet<string> Members = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Application.MailSession:get 初始化 MAPI 邮件会话,未配置邮件系统时
            // 弹「会签单不可用」模态对话框(Spy 时代已知的雷)。
            "MailSession",

            // Document.RoutingSlip(2026-09-16 真机探针定位):get 返回会签单对象本身
            // 无害,但遍历会继续下钻,其 Approve 属性一读就发起 MAPI 会签流程,弹同样的
            // 模态对话框堵死 STA 通道。必须在 RoutingSlip 这一层拦截,对象都不该被创建。
            // 证据:LastProbe 报告卡在 Application.ActiveDocument.RoutingSlip.Approve。
            "RoutingSlip",
        };

        public static bool IsBlocked(string memberName)
        {
            return memberName != null && Members.Contains(memberName);
        }

        /// <summary>真机探针定位到新的副作用成员后,在调用点登记到这里。</summary>
        public static void AddBlocked(string memberName)
        {
            if (!string.IsNullOrWhiteSpace(memberName))
            {
                Members.Add(memberName.Trim());
            }
        }
    }
}

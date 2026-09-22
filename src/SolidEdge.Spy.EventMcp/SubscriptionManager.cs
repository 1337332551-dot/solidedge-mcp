using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using SolidEdgeFramework;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// 订阅管理:对每个目标事件接口按 4 条途径探测宿主并 Advise。
    /// 精简自 EventTester 的 HostProbe(2026-08-21 实测验证版),去掉控制台诊断,
    /// 结果进状态快照。全部方法只允许在泵线程上调用。
    ///
    /// 途径优先级(从权威到兜底):
    ///   1. Application 直接连接点(EnumConnectionPoints 枚举 IID 匹配)
    ///   2. ActiveDocument 直接连接点(装配文档 QI 失败,预期内)
    ///   3. Application.xxxEvents 属性
    ///   4. ActiveDocument.xxxEvents 属性(含 "Models.Item(1).ModelRecomputeEvents" 点分路径)
    /// </summary>
    public sealed class SubscriptionManager
    {
        /// <summary>订阅状态快照条目(暴露给 se_event_status,只读)。</summary>
        public sealed class SubscriptionInfo
        {
            public string Interface;
            public string Host;
            public string Result;
        }

        private sealed class Target
        {
            public Guid Id;
            public string Name;
            public string[] AppProps;
            public string[] DocProps;
            public bool DocumentLevel;
            /// <summary>true=完全不订阅(状态里显示禁用原因)。用于会干扰 SE 自身对话框的接口。</summary>
            public bool Skip;
        }

        private sealed class Subscription
        {
            public string InterfaceName;
            public Guid InterfaceId;
            public string HostDescription;
            public int Cookie;
            public object SourceObject;   // Advise 所用的连接点容器对象(保持引用)
            public object SinkObject;      // sink 本体(保持引用防 GC)
            public bool DocumentLevel;
        }

        // GUID 取自 Interop.SolidEdge 反射(编译级事实)。宿主路径经 EventTester 实测:
        //   Application 连接点:ISEFileUIEvents / ISEApplicationEvents
        //   ActiveDocument.DocumentEvents:ISEDocumentEvents(零件+装配均有效)
        //   Models.Item(1).ModelRecomputeEvents:仅零件文档
        //   AssemblyRecomputeEvents:仅装配文档(直属性)
        // ISEAssemblyFamilyEvents 按用户决定不订(无装配族场景,2026-08-21)。
        // ⚠️ ISEFileUIEvents 有意不订(2026-09-20):OnFileSaveAsUI 挂在"另存为"弹窗的
        // 同步调用路径上,event-mcp 泵线程忙(跑脚本/重扫)时 SE 的文件对话框被阻塞,
        // 即用户实测的"event mcp 阻止打开另存为窗口"。文件 UI 事件仅作日志,不订不影响
        // 设计事件感知;若将来要恢复,把下面 ISEFileUIEvents 条目加回并自担弹窗阻塞风险。
        private static readonly List<Target> _targets = new List<Target>
        {
            new Target { Id = new Guid("0ea0d1f1-a199-11d1-aecc-08003616ce02"), Name = "ISEDocumentEvents",
                AppProps = new string[0], DocProps = new string[] { "DocumentEvents" }, DocumentLevel = true },
            new Target { Id = new Guid("6a89dfd1-9e7d-11d1-aecc-08003616ce02"), Name = "ISEModelRecomputeEvents",
                AppProps = new string[0], DocProps = new string[] { "Models.Item(1).ModelRecomputeEvents" }, DocumentLevel = true },
            new Target { Id = new Guid("f865f7bd-8d49-11d3-a3e6-0004ac969a5d"), Name = "ISEAssemblyRecomputeEvents",
                AppProps = new string[0], DocProps = new string[] { "AssemblyRecomputeEvents", "Models.Item(1).AssemblyRecomputeEvents" }, DocumentLevel = true },
            // ISEFileUIEvents 2026-09-22 起彻底不订(Skip):只要 Advise了这个连接点,SE 就把
            // "另存为/打开/新建"当被接管处理,回调返回 E_NOTIMPL 也放行不了(实机验证),
            // 表现为命令 2ms 内结束、对话框不弹。不订阅 = SE 视作没人监听 = 必然弹框。
            new Target { Id = new Guid("ecc667a1-a4aa-11d1-aecc-08003616ce02"), Name = "ISEFileUIEvents",
                AppProps = new string[] { "FileUIEvents" }, DocProps = new string[0], DocumentLevel = false, Skip = true },
            new Target { Id = new Guid("90223887-09cd-11d1-ba07-080036230602"), Name = "ISEApplicationEvents",
                AppProps = new string[0], DocProps = new string[0], DocumentLevel = false },
        };

        private readonly EventHub _hub;
        private readonly List<Subscription> _subs = new List<Subscription>();
        private readonly Dictionary<string, string> _failureReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        private volatile SubscriptionInfo[] _status = new SubscriptionInfo[0];

        public SubscriptionManager(EventHub hub)
        {
            _hub = hub;
        }

        /// <summary>状态快照(替换式更新,可从任意线程读)。</summary>
        public SubscriptionInfo[] GetStatus()
        {
            return _status;
        }

        /// <summary>订阅清理结果(暴露给 CLI --cleanup)。</summary>
        public sealed class SweepResult
        {
            public string Interface;
            public int Found;
            public int Removed;
        }

        /// <summary>
        /// 订阅清理(静态,供 SubscribeAll 前置与 CLI --cleanup 共用):
        /// 前次 EventMcp 进程被强杀(客户端关闭/任务管理器)时来不及 Unadvise,
        /// SE 的连接点里留下悬空 sink;SE 弹同步对话框(如"另存为")时回调这些死
        /// sink 导致对话框卡死(§5.8)。按 cookie 逐个 Unadvise 全部现存订阅
        /// (cookie 方式不触碰 sink 指针,死活都能安全清理,活订阅随后重建)。
        /// </summary>
        public static List<SweepResult> SweepZombies(Application app)
        {
            var report = new List<SweepResult>();
            if (app == null) return report;

            object doc = null;
            try { doc = app.ActiveDocument; } catch { }

            foreach (Target t in _targets)
            {
                int found = 0, removed = 0;
                Tuple<int, int> r = ConnectionPointHelper.SweepConnectionCookies(app, t.Id);
                if (r != null) { found += r.Item1; removed += r.Item2; }
                if (doc != null)
                {
                    r = ConnectionPointHelper.SweepConnectionCookies(doc, t.Id);
                    if (r != null) { found += r.Item1; removed += r.Item2; }
                }
                report.Add(new SweepResult { Interface = t.Name, Found = found, Removed = removed });
            }
            return report;
        }

        /// <summary>全量订阅(首次连接 / SE 重启重连后调用)。订阅前先清僵尸订阅。</summary>
        public void SubscribeAll(Application app)
        {
            int swept = 0;
            foreach (SweepResult r in SweepZombies(app))
            {
                swept += r.Removed;
            }
            if (swept > 0)
            {
                _hub.RecordSynthetic("ZombiesSwept", "已清理 " + swept + " 个僵尸事件订阅(前次进程异常退出残留,会导致 SE 弹窗卡死)");
            }

            object doc = GetActiveDocument(app);
            List<Guid> appCps = ConnectionPointHelper.EnumConnectionPoints(app);
            List<Guid> docCps = doc != null ? ConnectionPointHelper.EnumConnectionPoints(doc) : null;

            foreach (Target t in _targets)
            {
                TrySubscribe(app, doc, appCps, docCps, t);
            }
            RebuildStatus();
        }

        /// <summary>
        /// 重扫文档级订阅:释放旧的,对当前活动文档重新探测订阅
        /// (文档切换 / 文档类型变化时调用;应用级订阅保持不动)。
        /// 零件文档上装配接口探测失败、装配文档上零件接口探测失败,均属预期(宿主绑定文档类型)。
        /// </summary>
        public void RescanDocument(Application app)
        {
            // 新活动文档的连接点可能挂着前次进程残留的僵尸 sink(文档一直开着时),先清
            object activeDoc = GetActiveDocument(app);
            foreach (Target t in _targets)
            {
                if (t.DocumentLevel) ConnectionPointHelper.SweepConnectionCookies(activeDoc, t.Id);
            }

            for (int i = _subs.Count - 1; i >= 0; i--)
            {
                if (_subs[i].DocumentLevel)
                {
                    ConnectionPointHelper.Unadvise(_subs[i].SourceObject, _subs[i].InterfaceId, _subs[i].Cookie);
                    _subs.RemoveAt(i);
                }
            }

            // 文档级目标的失败记录重新评估
            foreach (Target t in _targets)
            {
                if (t.DocumentLevel) _failureReasons.Remove(t.Name);
            }

            object doc = GetActiveDocument(app);
            List<Guid> docCps = doc != null ? ConnectionPointHelper.EnumConnectionPoints(doc) : null;

            foreach (Target t in _targets)
            {
                if (!t.DocumentLevel) continue;
                TrySubscribe(app, doc, null, docCps, t);
            }
            RebuildStatus();
        }

        /// <summary>释放全部订阅(干净退出)。</summary>
        public void UnsubscribeAll()
        {
            foreach (Subscription s in _subs)
            {
                ConnectionPointHelper.Unadvise(s.SourceObject, s.InterfaceId, s.Cookie);
            }
            _subs.Clear();
            RebuildStatus();
        }

        /// <summary>SE 进程已死,连接对象全部失效:直接清表(无从 Unadvise)。</summary>
        public void ClearForReconnect()
        {
            _subs.Clear();
            foreach (Target t in _targets) _failureReasons[t.Name] = "等待连接 Solid Edge";
            RebuildStatus();
        }

        // ---------------- 内部实现(全部在泵线程) ----------------

        private void TrySubscribe(Application app, object doc, List<Guid> appCps, List<Guid> docCps, Target t)
        {
            if (t.Skip)
            {
                _failureReasons[t.Name] = "已禁用:订阅会拦截另存为/打开/新建对话框(2026-09-22 实测返回 E_NOTIMPL 也放行不了)";
                return;
            }

            // 途径1:Application 直接连接点
            if (appCps != null && appCps.Contains(t.Id))
            {
                if (TryAdvise(app, t, "Application(直接连接点)", t.DocumentLevel)) return;
            }

            // 途径2:ActiveDocument 直接连接点
            if (doc != null && docCps != null && docCps.Contains(t.Id))
            {
                if (TryAdvise(doc, t, "ActiveDocument(直接连接点)", true)) return;
            }

            // 途径3:Application 上的候选属性路径
            foreach (string prop in t.AppProps)
            {
                object source = TryGetPath(app, prop);
                if (source != null && TryAdvise(source, t, "Application." + prop, false)) return;
            }

            // 途径4:ActiveDocument 上的候选属性路径
            if (doc != null)
            {
                foreach (string prop in t.DocProps)
                {
                    object source = TryGetPath(doc, prop);
                    if (source != null && TryAdvise(source, t, "ActiveDocument." + prop, true)) return;
                }
            }

            string reason;
            if (doc == null && t.DocumentLevel)
            {
                reason = "当前无活动文档(文档级事件源无法探测)";
            }
            else if (t.AppProps.Length == 0 && t.DocProps.Length == 0)
            {
                reason = "Application 连接点枚举中未发现该接口 IID";
            }
            else
            {
                var props = new List<string>(t.AppProps);
                props.AddRange(t.DocProps);
                reason = "当前文档类型不适用(候选属性 " + string.Join("/", props.ToArray()) + " 不可用)";
            }
            _failureReasons[t.Name] = reason;
        }

        private bool TryAdvise(object source, Target t, string hostDescription, bool documentLevel)
        {
            try
            {
                object sink = CreateSink(t.Name);
                if (sink == null)
                {
                    throw new InvalidOperationException("未知接口名,无法创建 sink:" + t.Name);
                }

                int cookie = AdviseByInterfaceName(t.Name, source, sink);

                _subs.Add(new Subscription
                {
                    InterfaceName = t.Name,
                    InterfaceId = t.Id,
                    HostDescription = hostDescription,
                    Cookie = cookie,
                    SourceObject = source,
                    SinkObject = sink,
                    DocumentLevel = documentLevel,
                });
                _failureReasons.Remove(t.Name);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RebuildStatus()
        {
            var list = new List<SubscriptionInfo>();
            foreach (Target t in _targets)
            {
                Subscription found = null;
                foreach (Subscription s in _subs)
                {
                    if (s.InterfaceName == t.Name) { found = s; break; }
                }

                var info = new SubscriptionInfo { Interface = t.Name };
                if (found != null)
                {
                    info.Host = found.HostDescription;
                    info.Result = "已订阅";
                }
                else
                {
                    info.Host = "(未找到宿主)";
                    string reason;
                    _failureReasons.TryGetValue(t.Name, out reason);
                    info.Result = reason ?? "未订阅";
                }
                list.Add(info);
            }
            _status = list.ToArray();
        }

        private static object GetActiveDocument(Application app)
        {
            try { return app.ActiveDocument; }
            catch { return null; }
        }

        /// <summary>
        /// 按点分路径取对象,如 "Models.Item(1).ModelRecomputeEvents"。
        /// 段格式:属性名,或 "属性名(索引)"(集合 Item 调用)。任何一段失败返回 null。
        /// </summary>
        private static object TryGetPath(object target, string path)
        {
            if (target == null || string.IsNullOrEmpty(path)) return null;

            object current = target;
            string[] segments = path.Split('.');
            foreach (string seg in segments)
            {
                if (current == null) return null;

                Match m = Regex.Match(seg, @"^\s*(\w*)\s*(?:\(\s*(\d+)\s*\))?\s*$");
                if (!m.Success) return null;

                string name = m.Groups[1].Value;
                bool hasIndex = m.Groups[2].Success;

                try
                {
                    if (hasIndex)
                    {
                        // 集合元素访问:Item(n)。COM 集合的 Item 可能是方法也可能是带参属性,组合 flag 兼容两种。
                        object[] args = new object[] { int.Parse(m.Groups[2].Value) };
                        string member = string.IsNullOrEmpty(name) ? "Item" : name;
                        current = current.GetType().InvokeMember(
                            member,
                            BindingFlags.InvokeMethod | BindingFlags.GetProperty,
                            null, current, args);
                    }
                    else
                    {
                        current = current.GetType().InvokeMember(
                            name, BindingFlags.GetProperty, null, current, null);
                    }
                }
                catch
                {
                    return null;
                }
            }

            return current;
        }

        private object CreateSink(string interfaceName)
        {
            switch (interfaceName)
            {
                case "ISEDocumentEvents": return new DocumentEventsSink(_hub);
                case "ISEModelRecomputeEvents": return new ModelRecomputeEventsSink(_hub);
                case "ISEAssemblyRecomputeEvents": return new AssemblyRecomputeEventsSink(_hub);
                case "ISEFileUIEvents": return new FileUIEventsSink(_hub);
                case "ISEApplicationEvents": return new ApplicationEventsSink(_hub);
                default: return null;
            }
        }

        private static int AdviseByInterfaceName(string interfaceName, object source, object sink)
        {
            switch (interfaceName)
            {
                case "ISEDocumentEvents":
                    return ConnectionPointHelper.AdviseSink<ISEDocumentEvents>(source, (ISEDocumentEvents)sink);
                case "ISEModelRecomputeEvents":
                    return ConnectionPointHelper.AdviseSink<ISEModelRecomputeEvents>(source, (ISEModelRecomputeEvents)sink);
                case "ISEAssemblyRecomputeEvents":
                    return ConnectionPointHelper.AdviseSink<ISEAssemblyRecomputeEvents>(source, (ISEAssemblyRecomputeEvents)sink);
                case "ISEFileUIEvents":
                    // 用自定义的非侵入声明(同 IID、同虚表顺序),以便把 E_NOTIMPL 真正还给 SE。
                    return ConnectionPointHelper.AdviseSink<IFileUIEventsNonIntrusive>(source, (IFileUIEventsNonIntrusive)sink);
                case "ISEApplicationEvents":
                    return ConnectionPointHelper.AdviseSink<ISEApplicationEvents>(source, (ISEApplicationEvents)sink);
                default:
                    throw new InvalidOperationException("未知接口名:" + interfaceName);
            }
        }
    }
}

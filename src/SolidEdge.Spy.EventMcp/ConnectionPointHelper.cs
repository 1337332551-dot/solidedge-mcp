using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// COM 连接点封装:枚举宿主支持的连接点 + Advise/Unadvise。
    /// 复制自 EventTester 的实测验证版(2026-08-21 全接口验证通过),仅改命名空间。
    /// 全部方法必须在泵线程上调用。
    /// </summary>
    internal static class ConnectionPointHelper
    {
        /// <summary>
        /// 枚举一个 COM 对象支持的全部连接点接口 IID。
        /// 返回 null 表示对象不支持 IConnectionPointContainer
        /// (装配文档 ActiveDocument 即如此——属性途径是唯一通路)。
        /// </summary>
        public static System.Collections.Generic.List<Guid> EnumConnectionPoints(object comObject)
        {
            if (comObject == null) return null;

            IConnectionPointContainer container = comObject as IConnectionPointContainer;
            if (container == null)
            {
                return null;
            }

            var list = new System.Collections.Generic.List<Guid>();
            IEnumConnectionPoints enumerator = null;
            try
            {
                container.EnumConnectionPoints(out enumerator);
                if (enumerator == null) return list;

                var buffer = new IConnectionPoint[1];
                // Next 返回 S_OK(0) 表示取到元素;S_FALSE(1) 表示枚举结束
                while (enumerator.Next(1, buffer, IntPtr.Zero) == 0)
                {
                    IConnectionPoint cp = buffer[0];
                    if (cp != null)
                    {
                        try
                        {
                            Guid iid;
                            cp.GetConnectionInterface(out iid);
                            if (!list.Contains(iid))
                            {
                                list.Add(iid);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(cp);
                        }
                    }
                    buffer[0] = null;
                }
            }
            finally
            {
                if (enumerator != null)
                {
                    Marshal.ReleaseComObject(enumerator);
                }
            }

            return list;
        }

        /// <summary>
        /// 对 source(连接点容器)Advise 一个实现了接口 T 的 sink。
        /// 成功返回 cookie(用于 Unadvise),失败抛异常。
        /// </summary>
        public static int AdviseSink<T>(object source, T sink) where T : class
        {
            if (source == null) throw new ArgumentNullException("source");
            if (sink == null) throw new ArgumentNullException("sink");

            IConnectionPointContainer container = (IConnectionPointContainer)source;
            Guid iid = typeof(T).GUID;
            IConnectionPoint cp;
            container.FindConnectionPoint(ref iid, out cp);
            if (cp == null)
            {
                throw new InvalidOperationException(
                    string.Format("宿主不支持连接点 {0}(FindConnectionPoint 返回 null)。", typeof(T).Name));
            }

            try
            {
                int cookie;
                cp.Advise(sink, out cookie);
                return cookie;
            }
            finally
            {
                Marshal.ReleaseComObject(cp);
            }
        }

        /// <summary>
        /// 僵尸订阅清理:枚举 source 上 iid 连接点的现有 sink,逐个对事件 IID 做
        /// QueryInterface——QI 自定义接口必须转发到 sink 所在进程,进程已死则立即失败
        /// (活 sink 零副作用,不会被误伤)。死 sink Unadvise 摘除。
        /// 返回 (现有 sink 总数, 清理数);宿主不支持该连接点返回 null。
        /// </summary>
        public static System.Tuple<int, int> SweepDeadSinks(object source, Guid iid)
        {
            if (source == null) return null;
            IConnectionPointContainer container = source as IConnectionPointContainer;
            if (container == null) return null;

            Guid target = iid;
            IConnectionPoint cp;
            try
            {
                container.FindConnectionPoint(ref target, out cp);
            }
            catch
            {
                return null;
            }
            if (cp == null) return null;

            int total = 0;
            int dead = 0;
            try
            {
                IEnumConnections enumerator;
                cp.EnumConnections(out enumerator);
                if (enumerator == null) return System.Tuple.Create(0, 0);

                // 先快照再清理:Unadvise 会改连接点列表,边枚举边删不可靠
                var items = new System.Collections.Generic.List<CONNECTDATA>();
                try
                {
                    var buffer = new CONNECTDATA[1];
                    // Next 返回 S_OK(0) 表示取到元素;S_FALSE(1) 表示枚举结束
                    while (enumerator.Next(1, buffer, IntPtr.Zero) == 0)
                    {
                        if (buffer[0].pUnk != null)
                        {
                            items.Add(buffer[0]);
                        }
                        buffer[0] = default(CONNECTDATA);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumerator);
                }

                foreach (CONNECTDATA cd in items)
                {
                    total++;
                    // .NET 8 里 CONNECTDATA.pUnk 是 object(RCW 包装的 IUnknown):
                    // 先取回 IUnknown 指针,再对事件 IID 做 QI 探活。RCW 已断开(宿主进程死)
                    // 时 GetIUnknownForObject 抛异常,同样判死。
                    bool deadSink = false;
                    IntPtr pUnk = IntPtr.Zero;
                    try
                    {
                        pUnk = Marshal.GetIUnknownForObject(cd.pUnk);
                        IntPtr ppv;
                        int hr = Marshal.QueryInterface(pUnk, ref target, out ppv);
                        if (hr >= 0)
                        {
                            Marshal.Release(ppv);
                        }
                        else
                        {
                            deadSink = true;
                        }
                    }
                    catch
                    {
                        deadSink = true;
                    }
                    finally
                    {
                        if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
                    }
                    if (deadSink)
                    {
                        try { cp.Unadvise(cd.dwCookie); dead++; }
                        catch { }
                        try { Marshal.ReleaseComObject(cd.pUnk); }
                        catch { }
                    }
                }
            }
            catch
            {
                // 尽力而为:清理失败不影响主流程
            }
            finally
            {
                Marshal.ReleaseComObject(cp);
            }

            return System.Tuple.Create(total, dead);
        }

        /// <summary>按接口 IID 取消订阅(尽力而为,SE 关闭等场景失败不抛)。</summary>
        public static void Unadvise(object source, Guid eventInterfaceId, int cookie)
        {
            if (source == null || cookie == 0) return;
            try
            {
                IConnectionPointContainer container = (IConnectionPointContainer)source;
                Guid iid = eventInterfaceId;
                IConnectionPoint cp;
                container.FindConnectionPoint(ref iid, out cp);
                if (cp != null)
                {
                    try { cp.Unadvise(cookie); }
                    finally { Marshal.ReleaseComObject(cp); }
                }
            }
            catch
            {
                // 尽力而为
            }
        }
    }
}

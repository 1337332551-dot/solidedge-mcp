using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// IEnumConnections 的裸声明(与 BCL 同 IID),但 pUnk 用 IntPtr 而非 object:
    /// 枚举僵尸 sink 时若把死指针 marshal 成 RCW,会在 unmarshal/探活阶段直接
    /// AccessViolation(2026-09-20 实测,客户端进程崩溃)。裸声明下 pUnk 只是
    /// 一个按值复制的指针数值,全程不触碰。
    /// </summary>
    [ComImport, Guid("B196B287-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumConnectionsRaw
    {
        [PreserveSig] int Next(int celt, [Out] RawConnectData[] rgelt, out int pceltFetched);
        [PreserveSig] int Skip(int celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumConnectionsRaw ppEnum);
    }

    /// <summary>与 native CONNECTDATA 布局一致,但 pUnk 为裸指针(只留数值,绝不解引用)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawConnectData
    {
        public IntPtr pUnk;
        public int dwCookie;
    }

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
        /// 僵尸订阅清理(cookie 方式):枚举 source 上 iid 连接点的全部 cookie,逐个 Unadvise。
        /// ⚠️ 不能走"QI 探活"路线——把 sink 指针 marshal 回本进程转 RCW 再 QI 时,
        /// QI 打向已死进程会直接 AccessViolation(2026-09-20 实测,客户端崩溃、SE 无恙)。
        /// 这里用裸枚举接口只取 cookie 数值,按 cookie Unadvise 全程不触碰 sink 指针。
        /// 副作用:活订阅也会被一并摘除——本工具链单实例运行,被误摘的活订阅在
        /// 下次 SubscribeAll 时立即重建,实际无害。
        /// 返回 (找到的 cookie 数, 摘除数);宿主不支持该连接点返回 null。
        /// </summary>
        public static System.Tuple<int, int> SweepConnectionCookies(object source, Guid iid)
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

            int found = 0;
            int removed = 0;
            try
            {
                // 经 BCL 接口取枚举器,再 QI 成裸接口(同 IID):cookie 数组按值 marshal,
                // pUnk 只是 IntPtr 数值,不存在 RCW 创建/探活路径,死活订阅都能安全枚举。
                IEnumConnections typedEnum;
                cp.EnumConnections(out typedEnum);
                if (typedEnum == null) return System.Tuple.Create(0, 0);

                IEnumConnectionsRaw rawEnum;
                IntPtr pEnum = Marshal.GetIUnknownForObject(typedEnum);
                try
                {
                    rawEnum = (IEnumConnectionsRaw)Marshal.GetTypedObjectForIUnknown(pEnum, typeof(IEnumConnectionsRaw));
                }
                finally
                {
                    Marshal.Release(pEnum);
                }
                Marshal.ReleaseComObject(typedEnum);

                try
                {
                    var buffer = new RawConnectData[1];
                    // Next 返回 S_OK(0) 表示取到元素;S_FALSE(1) 表示枚举结束
                    while (rawEnum.Next(1, buffer, out int fetched) == 0 && fetched > 0)
                    {
                        int cookie = buffer[0].dwCookie;
                        buffer[0] = default(RawConnectData);
                        found++;
                        if (cookie != 0)
                        {
                            try { cp.Unadvise(cookie); removed++; }
                            catch
                            {
                                // 逐个 cookie 尽力而为
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(rawEnum);
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

            return System.Tuple.Create(found, removed);
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

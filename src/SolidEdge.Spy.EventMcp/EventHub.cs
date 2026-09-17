using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SolidEdge.Spy.InteropServices;
using SolidEdgeFramework;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// 监视 server 的核心单例:连接 SE、管理订阅生命周期、维护过滤器与
    /// 当前文档名快照、把 sink 回调写入环形缓冲。
    ///
    /// 线程模型:
    /// - 全部 COM 调用只发生在泵线程(StaPump)的消息处理中:连接、订阅、
    ///   文档级重订、活性检查 —— 天然串行,无锁竞争;
    /// - MCP 工具(线程池 MTA)只碰纯托管状态(环形缓冲/过滤器/快照字段),
    ///   微秒级返回,永不卡在 COM 上;
    /// - sink 回调(泵线程)只调 Record/RequestDocumentRescan,零 COM 零阻塞。
    ///
    /// 自愈能力(WM_TIMER 5 秒一拍):
    /// - SE 关闭 → 探测失败 → 释放状态、合成 Disconnected 事件 → 周期重试
    /// - SE 重启 → GetActiveObject 成功 → 全量重订 → 合成 Reconnected 事件
    /// - 文档切换漏收 AfterActiveDocumentChange → 文档名快照对不上 → 兜底重订
    /// </summary>
    public sealed class EventHub : IDisposable
    {
        private readonly StaPump _pump = new StaPump();
        private readonly EventRing _ring;
        private readonly SubscriptionManager _subs;
        private readonly DateTime _startedAt = DateTime.Now;
        private readonly object _filterLock = new object();
        private readonly Dictionary<string, bool> _filters = EventCatalog.BuildDefaultFilters();

        // 以下字段:带 volatile 的由泵线程写、工具线程读;_app 仅泵线程访问
        private Application _app;
        private volatile bool _connected;
        private volatile string _docName = "";
        private volatile string _subscribedDoc = "";
        private int _rescanPending;
        private int _disposed;

        /// <summary>事件进入环形缓冲时触发(泵线程;CLI 模式用于实时打印)。</summary>
        public event Action<EventRecord> EventRecorded;

        public EventHub(int ringCapacity = 200)
        {
            _ring = new EventRing(ringCapacity);
            _subs = new SubscriptionManager(this);

            _pump.Tick += OnTimerTick;     // 泵线程:活性检查/重连
            _pump.Rescan += OnRescan;      // 泵线程:文档级重订

            // 干净退出兜底(僵尸订阅教训 §5.8):正常路径是 Main 的 finally 调
            // Shutdown();这里是 ProcessExit / Ctrl+C 的最后防线。
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
            Console.CancelKeyPress += (s, e) => Shutdown();
        }

        // ---------------- 对外状态(工具线程读) ----------------

        public bool IsConnected { get { return _connected; } }

        public string CurrentDocument { get { return _docName; } }

        public int UptimeSeconds { get { return (int)(DateTime.Now - _startedAt).TotalSeconds; } }

        public SubscriptionManager.SubscriptionInfo[] SubscriptionStatus { get { return _subs.GetStatus(); } }

        public RingStats GetRingStats() { return _ring.GetStats(); }

        public RingReadResult GetEvents(long afterSeq, int limit) { return _ring.Read(afterSeq, limit); }

        public List<string> AllEventKeys() { return EventCatalog.AllKeys(); }

        public string[] DisabledEvents
        {
            get
            {
                lock (_filterLock)
                {
                    var list = new List<string>();
                    foreach (var kv in _filters)
                    {
                        if (!kv.Value) list.Add(kv.Key);
                    }
                    return list.ToArray();
                }
            }
        }

        /// <summary>设置事件过滤器。event 名可为短名(须唯一)或全名。</summary>
        public bool TrySetFilter(string eventName, bool enabled, out string resolvedKey, out string error)
        {
            resolvedKey = null;
            error = null;
            lock (_filterLock)
            {
                string key = EventCatalog.ResolveKey(eventName);
                if (key == null)
                {
                    error = "无法解析事件名 '" + eventName + "'。短名必须在全部事件中唯一(如 BeforeRecompute 同时存在于零件级/装配级,须用全名 ISEModelRecomputeEvents.BeforeRecompute)";
                    return false;
                }
                _filters[key] = enabled;
                resolvedKey = key;
                return true;
            }
        }

        // ---------------- 生命周期 ----------------

        /// <summary>启动泵线程并尝试连接订阅(SE 未运行也不失败,周期重试)。</summary>
        public void Start()
        {
            _pump.Start();
            try
            {
                _pump.Invoke(ConnectAndSubscribe);
            }
            catch
            {
                // 初始连接失败:进入未连接状态,WM_TIMER 周期重试
            }
        }

        /// <summary>干净退出:Unadvise 全部订阅并停止泵(幂等,多路径调用安全)。</summary>
        public void Shutdown()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                _pump.TryInvoke(_subs.UnsubscribeAll, TimeSpan.FromSeconds(3));
            }
            catch { }

            _pump.Stop(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            Shutdown();
        }

        // ---------------- sink 回调入口(泵线程,零 COM) ----------------

        /// <summary>sink 事件入口:过过滤器 → 入环形缓冲 → 通知订阅者。</summary>
        public void Record(string source, string evt, string detail)
        {
            lock (_filterLock)
            {
                bool on;
                if (_filters.TryGetValue(source + "." + evt, out on) && !on) return;
            }

            EventRecord rec = _ring.Add(source, evt, detail, _docName);
            RaiseRecorded(rec);
        }

        /// <summary>合成运维事件(连接/断开/重订),不受过滤器控制。</summary>
        public void RecordSynthetic(string evt, string detail)
        {
            EventRecord rec = _ring.Add(EventCatalog.SyntheticSource, evt, detail, _docName);
            RaiseRecorded(rec);
        }

        /// <summary>请求重扫文档级订阅(sink 回调/兜底调用;仅 PostMessage,立即返回)。</summary>
        public void RequestDocumentRescan()
        {
            if (Interlocked.Exchange(ref _rescanPending, 1) == 0)
            {
                _pump.PostRescan();
            }
        }

        private void RaiseRecorded(EventRecord rec)
        {
            Action<EventRecord> h = EventRecorded;
            if (h != null)
            {
                try { h(rec); }
                catch { }
            }
        }

        // ---------------- 泵线程逻辑(可做 COM) ----------------

        private void ConnectAndSubscribe()
        {
            if (TryConnectCore())
            {
                _subs.SubscribeAll(_app);
                UpdateDocSnapshot();
                _subscribedDoc = _docName;
                RecordSynthetic("Connected", "已连接 Solid Edge,文档=" + _docName);
            }
        }

        private bool TryConnectCore()
        {
            try
            {
                ComPtr pApp = IntPtr.Zero;
                if (MarshalEx.Succeeded(MarshalEx.GetActiveObject("SolidEdge.Application", out pApp)))
                {
                    _app = pApp.TryGetUniqueRCW<Application>();
                    _connected = true;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private void OnTimerTick()
        {
            if (!_connected)
            {
                if (TryConnectCore())
                {
                    _subs.SubscribeAll(_app);
                    UpdateDocSnapshot();
                    _subscribedDoc = _docName;
                    RecordSynthetic("Reconnected", "Solid Edge 已恢复,重新订阅,文档=" + _docName);
                }
                return;
            }

            try
            {
                _ = _app.Visible;   // 活性检查:SE 进程死了这里抛异常
                UpdateDocSnapshot();

                // 文档切换兜底:正常靠 AfterActiveDocumentChange 触发重订,
                // 万一事件漏收,这里用文档名快照对比兜底。
                if (_docName != _subscribedDoc)
                {
                    RequestDocumentRescan();
                }
            }
            catch
            {
                HandleDisconnected();
            }
        }

        private void OnRescan()
        {
            _rescanPending = 0;
            if (!_connected)
            {
                _subscribedDoc = "";
                return;
            }

            _subs.RescanDocument(_app);
            UpdateDocSnapshot();
            _subscribedDoc = _docName;
            RecordSynthetic("DocumentResubscribed", "文档级事件源已跟随切换,文档=" + _docName);
        }

        private void HandleDisconnected()
        {
            _connected = false;
            _app = null;
            _docName = "";
            _subscribedDoc = "";
            _subs.ClearForReconnect();
            RecordSynthetic("Disconnected", "Solid Edge 已关闭或连接断开,每 5 秒自动重试连接");
        }

        private void UpdateDocSnapshot()
        {
            try
            {
                object doc = _app.ActiveDocument;
                _docName = doc == null ? "" : SafeDocName(doc);
            }
            catch
            {
                _docName = "";
            }
        }

        private static string SafeDocName(object doc)
        {
            try
            {
                object name = doc.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, doc, null);
                return name == null ? "" : name.ToString();
            }
            catch
            {
                return "";
            }
        }
    }
}

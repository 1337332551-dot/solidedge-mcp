using System;
using System.Collections.Generic;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// 事件描述符 —— 环形缓冲条目 / se_get_events 返回单元,全链路唯一数据形状。
    /// 只存可序列化字段,绝不存 COM 对象(跨进程指针无效,调研记录 §6)。
    /// </summary>
    public sealed class EventRecord
    {
        public long Seq { get; set; }          // 单调递增,agent 增量读取的游标
        public DateTime Time { get; set; }     // 本地时间戳
        public string Source { get; set; }     // "ISEApplicationEvents" 等;合成事件用 "EventRecorder"
        public string Event { get; set; }      // "AfterRecompute" / "AfterDocumentSave" ...
        public string Detail { get; set; }     // 托管安全摘要("CommandID=57603"),零 COM 调用产物
        public string Doc { get; set; }        // 事件发生时的活动文档名(hub 维护的字段快照)
    }

    /// <summary>环形缓冲的读取结果。</summary>
    public sealed class RingReadResult
    {
        public List<EventRecord> Events = new List<EventRecord>();
        public long LatestSeq;    // 缓冲内最新 seq(下次增量读取的游标)
        public long OldestSeq;    // 缓冲内最旧 seq(0=空)
        public long Lost;         // 请求的 seq 之后、缓冲最旧之前被覆盖掉的条数
    }

    /// <summary>
    /// 定长环形缓冲(默认 200 条):seq 单调递增,溢出覆盖最旧条目。
    /// 读路径纯托管 + lock(微秒级),MCP 工具线程直接调用,永不卡在 COM 上。
    /// </summary>
    public sealed class EventRing
    {
        private readonly EventRecord[] _buf;
        private readonly object _lock = new object();
        private int _head;     // 下一个写入位置
        private long _seq;     // 已分配的最大 seq

        public EventRing(int capacity)
        {
            if (capacity < 1) capacity = 1;
            _buf = new EventRecord[capacity];
        }

        public int Capacity { get { return _buf.Length; } }

        /// <summary>写入一条事件(过滤在 EventHub 层做,这里只管存)。</summary>
        public EventRecord Add(string source, string evt, string detail, string doc)
        {
            lock (_lock)
            {
                _seq++;
                var r = new EventRecord
                {
                    Seq = _seq,
                    Time = DateTime.Now,
                    Source = source,
                    Event = evt,
                    Detail = detail,
                    Doc = doc,
                };
                _buf[_head] = r;
                _head = (_head + 1) % _buf.Length;
                return r;
            }
        }

        /// <summary>
        /// 增量读取:
        ///   afterSeq = 0  → 返回最近 limit 条(首次调用的便捷模式);
        ///   afterSeq &gt; 0  → 返回 Seq &gt; afterSeq 的最多 limit 条,
        ///                   若 afterSeq 与缓冲最旧之间有被覆盖的条目,以 Lost 告知。
        /// </summary>
        public RingReadResult Read(long afterSeq, int limit)
        {
            if (limit < 1) limit = 1;
            var result = new RingReadResult();

            lock (_lock)
            {
                long latest = _seq;
                result.LatestSeq = latest;
                if (latest == 0) { result.OldestSeq = 0; return result; }

                long oldest = Math.Max(1, latest - _buf.Length + 1);
                result.OldestSeq = oldest;

                if (afterSeq >= latest) return result;               // 无新事件
                if (afterSeq > 0 && afterSeq + 1 < oldest)
                {
                    result.Lost = oldest - afterSeq - 1;             // 中间被覆盖的
                }

                long from = afterSeq <= 0 ? Math.Max(oldest, latest - limit + 1) : afterSeq + 1;
                long to = latest;

                // 从新到旧倒序找,再反转为时间序
                var list = new List<EventRecord>();
                for (long s = to; s >= from && list.Count < limit; s--)
                {
                    EventRecord r = FindBySeq(s);
                    if (r != null) list.Add(r);
                }
                list.Reverse();
                result.Events = list;
                return result;
            }
        }

        /// <summary>按 seq 找缓冲内条目(须在 lock 内调用)。容量小,直接扫。</summary>
        private EventRecord FindBySeq(long seq)
        {
            for (int i = 0; i < _buf.Length; i++)
            {
                EventRecord r = _buf[i];
                if (r != null && r.Seq == seq) return r;
            }
            return null;
        }

        /// <summary>缓冲统计。</summary>
        public RingStats GetStats()
        {
            lock (_lock)
            {
                int count = 0;
                foreach (EventRecord r in _buf) if (r != null) count++;
                var stats = new RingStats
                {
                    Capacity = _buf.Length,
                    Buffered = count,
                    LatestSeq = _seq,
                    OldestSeq = _seq == 0 ? 0 : Math.Max(1, _seq - _buf.Length + 1),
                };
                return stats;
            }
        }
    }

    /// <summary>环形缓冲统计快照。</summary>
    public sealed class RingStats
    {
        public int Capacity;
        public int Buffered;
        public long LatestSeq;
        public long OldestSeq;
    }
}

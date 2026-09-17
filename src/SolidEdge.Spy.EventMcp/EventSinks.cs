using System;
using System.Collections.Generic;
using SolidEdgeFramework;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// 事件目录:全部可过滤事件键(来源.事件名)与默认开关。
    /// 键清单与 EventSinks 实现的接口成员一一对应。
    /// </summary>
    internal static class EventCatalog
    {
        public static readonly Dictionary<string, string[]> Events = new Dictionary<string, string[]>
        {
            ["ISEApplicationEvents"] = new[]
            {
                "AfterActiveDocumentChange", "AfterCommandRun", "AfterDocumentOpen", "AfterDocumentPrint",
                "AfterDocumentSave", "AfterEnvironmentActivate", "AfterNewDocumentOpen", "AfterNewWindow",
                "AfterWindowActivate", "BeforeCommandRun", "BeforeDocumentClose", "BeforeDocumentPrint",
                "BeforeDocumentSave", "BeforeEnvironmentDeactivate", "BeforeQuit", "BeforeWindowDeactivate",
            },
            ["ISEDocumentEvents"] = new[]
            {
                "BeforeClose", "BeforeSave", "AfterSave", "SelectSetChanged",
            },
            ["ISEModelRecomputeEvents"] = new[]
            {
                "BeforeRecompute", "AfterRecompute", "AfterFeatureIsAdded",
                "AfterFeatureIsModified", "BeforeFeatureIsDeleted", "BeforeModelIsDeleted",
            },
            ["ISEAssemblyRecomputeEvents"] = new[]
            {
                "BeforeRecompute", "AfterRecompute", "AfterAdd", "AfterModify", "BeforeDelete",
            },
            ["ISEFileUIEvents"] = new[]
            {
                "OnFileOpenUI", "OnFileSaveAsUI", "OnFileNewUI",
                "OnFileSaveAsImageUI", "OnPlacePartUI", "OnCreateInPlacePartUI",
            },
        };

        /// <summary>合成事件源名(连接/断开/重订等运维信号,不受过滤)。</summary>
        public const string SyntheticSource = "EventRecorder";

        /// <summary>全部事件键(来源.事件名)。</summary>
        public static List<string> AllKeys()
        {
            var keys = new List<string>();
            foreach (var kv in Events)
            {
                foreach (string e in kv.Value)
                {
                    keys.Add(kv.Key + "." + e);
                }
            }
            return keys;
        }

        /// <summary>默认过滤器:全部启用,仅 SelectSetChanged 高频噪声默认关(调研记录 §2.1)。</summary>
        public static Dictionary<string, bool> BuildDefaultFilters()
        {
            var filters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in AllKeys())
            {
                filters[key] = true;
            }
            filters["ISEDocumentEvents.SelectSetChanged"] = false;
            return filters;
        }

        /// <summary>
        /// 解析事件名:全名("来源.事件")直接匹配;短名("事件")须唯一,
        /// 如 "BeforeRecompute" 在零件级/装配级都存在 → 歧义返回 null。
        /// </summary>
        public static string ResolveKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            // 全名匹配
            foreach (var kv in Events)
            {
                foreach (string e in kv.Value)
                {
                    string full = kv.Key + "." + e;
                    if (string.Equals(full, input, StringComparison.OrdinalIgnoreCase)) return full;
                }
            }

            // 短名唯一匹配
            string hit = null;
            foreach (var kv in Events)
            {
                foreach (string e in kv.Value)
                {
                    if (string.Equals(e, input, StringComparison.OrdinalIgnoreCase))
                    {
                        if (hit != null) return null; // 歧义
                        hit = kv.Key + "." + e;
                    }
                }
            }
            return hit;
        }
    }

    /// <summary>sink 公用工具:参数安全描述(只取托管类型名/值,绝不触发 COM 调用)。</summary>
    internal static class SinkUtil
    {
        public static string Describe(object o)
        {
            if (o == null) return "null";
            Type t = o.GetType();
            // 裸 RCW(__ComObject)拿不到类型名,回调内禁止 COM 调用(如读 Name),标为不透明
            if (t == typeof(object) || t.Name == "__ComObject") return "COM对象(不透明)";
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return o.ToString();
            return t.Name;
        }
    }

    // ------------------------------------------------------------------
    // 5 个事件 sink。铁律(EventTester 验证):回调里零 COM 调用、零阻塞,
    // 只调 EventHub.Record(纯托管入环形缓冲);需要 COM 的动作(文档级重订)
    // 通过 hub.RequestDocumentRescan() → PostMessage 延迟到泵线程干净上下文执行。
    // ------------------------------------------------------------------

    /// <summary>文档级事件(宿主:ActiveDocument.DocumentEvents 属性)。</summary>
    internal sealed class DocumentEventsSink : ISEDocumentEvents
    {
        private readonly EventHub _hub;
        public DocumentEventsSink(EventHub hub) { _hub = hub; }

        public void BeforeClose() { _hub.Record("ISEDocumentEvents", "BeforeClose", ""); }
        public void BeforeSave() { _hub.Record("ISEDocumentEvents", "BeforeSave", ""); }
        public void AfterSave() { _hub.Record("ISEDocumentEvents", "AfterSave", ""); }
        public void SelectSetChanged(object SelectSet)
        {
            _hub.Record("ISEDocumentEvents", "SelectSetChanged", "SelectSet=" + SinkUtil.Describe(SelectSet));
        }
    }

    /// <summary>零件级模型重算事件(宿主:ActiveDocument.Models.Item(1).ModelRecomputeEvents)。</summary>
    internal sealed class ModelRecomputeEventsSink : ISEModelRecomputeEvents
    {
        private readonly EventHub _hub;
        public ModelRecomputeEventsSink(EventHub hub) { _hub = hub; }

        public void BeforeRecompute() { _hub.Record("ISEModelRecomputeEvents", "BeforeRecompute", ""); }
        public void AfterRecompute() { _hub.Record("ISEModelRecomputeEvents", "AfterRecompute", ""); }
        public void AfterFeatureIsAdded(SeFeatureAddFlag AddFlag, object Feature)
        {
            _hub.Record("ISEModelRecomputeEvents", "AfterFeatureIsAdded",
                "AddFlag=" + AddFlag + " Feature=" + SinkUtil.Describe(Feature));
        }
        public void BeforeFeatureIsDeleted(SeFeatureDeleteFlag DeleteFlag, object Feature)
        {
            _hub.Record("ISEModelRecomputeEvents", "BeforeFeatureIsDeleted",
                "DeleteFlag=" + DeleteFlag + " Feature=" + SinkUtil.Describe(Feature));
        }
        public void AfterFeatureIsModified(SeFeatureModifyFlag ModifyFlag, object Feature)
        {
            _hub.Record("ISEModelRecomputeEvents", "AfterFeatureIsModified",
                "ModifyFlag=" + ModifyFlag + " Feature=" + SinkUtil.Describe(Feature));
        }
        public void BeforeModelIsDeleted(object Model)
        {
            _hub.Record("ISEModelRecomputeEvents", "BeforeModelIsDeleted",
                "Model=" + SinkUtil.Describe(Model));
        }
    }

    /// <summary>装配级重算事件(宿主:装配文档 AssemblyRecomputeEvents 直属性)。</summary>
    internal sealed class AssemblyRecomputeEventsSink : ISEAssemblyRecomputeEvents
    {
        private readonly EventHub _hub;
        public AssemblyRecomputeEventsSink(EventHub hub) { _hub = hub; }

        public void BeforeRecompute(object theDocument)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "BeforeRecompute", "");
        }
        public void AfterRecompute(object theDocument)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "AfterRecompute", "");
        }
        public void AfterAdd(object theDocument, object obj, ObjectType Type)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "AfterAdd",
                "Type=" + Type + " Obj=" + SinkUtil.Describe(obj));
        }
        public void BeforeDelete(object theDocument, object obj, ObjectType Type)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "BeforeDelete",
                "Type=" + Type + " Obj=" + SinkUtil.Describe(obj));
        }
        public void AfterModify(object theDocument, object obj, ObjectType Type, seAssemblyEventConstants ModifyType)
        {
            _hub.Record("ISEAssemblyRecomputeEvents", "AfterModify",
                "Type=" + Type + " ModifyType=" + ModifyType + " Obj=" + SinkUtil.Describe(obj));
        }
    }

    /// <summary>
    /// 文件 UI 事件(宿主:Application 连接点 / FileUIEvents 属性)。
    /// string 参数是 out:SE 在弹文件对话框前同步回调订阅方,期望"预填"对话框值。
    /// 纯观察模式:out 一律置 null(不干预),ref 参数保持原值。
    /// 注意:这是同步回调链路,僵尸订阅会毒化它导致对话框不弹(§5.8)——
    /// 本 server 的干净退出机制就是为保护这条链路设计的。
    /// </summary>
    internal sealed class FileUIEventsSink : ISEFileUIEvents
    {
        private readonly EventHub _hub;
        public FileUIEventsSink(EventHub hub) { _hub = hub; }

        public void OnFileOpenUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnFileOpenUI", "");
            Filename = null;
            AppendToTitle = null;
        }

        public void OnFileSaveAsUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnFileSaveAsUI", "");
            Filename = null;
            AppendToTitle = null;
        }

        public void OnFileNewUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnFileNewUI", "");
            Filename = null;
            AppendToTitle = null;
        }

        public void OnFileSaveAsImageUI(out string Filename, out string AppendToTitle, ref int Width, ref int Height, ref SeImageQualityType ImageQuality)
        {
            _hub.Record("ISEFileUIEvents", "OnFileSaveAsImageUI",
                "W=" + Width + " H=" + Height + " Quality=" + ImageQuality);
            Filename = null;
            AppendToTitle = null;
        }

        public void OnPlacePartUI(out string Filename, out string AppendToTitle)
        {
            _hub.Record("ISEFileUIEvents", "OnPlacePartUI", "");
            Filename = null;
            AppendToTitle = null;
        }

        public void OnCreateInPlacePartUI(out string Filename, out string AppendToTitle, out string Template)
        {
            _hub.Record("ISEFileUIEvents", "OnCreateInPlacePartUI", "");
            Filename = null;
            AppendToTitle = null;
            Template = null;
        }
    }

    /// <summary>应用级事件(宿主:Application 直接连接点)。文档切换时联动触发文档级重订。</summary>
    internal sealed class ApplicationEventsSink : ISEApplicationEvents
    {
        private readonly EventHub _hub;
        public ApplicationEventsSink(EventHub hub) { _hub = hub; }

        public void AfterActiveDocumentChange(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterActiveDocumentChange", "");
            // "每文档一订阅":切文档必须重订文档级事件源(调研记录 §2.1)。
            // 回调铁律:此处零 COM —— 只 PostMessage,由泵线程延迟重订。
            _hub.RequestDocumentRescan();
        }

        public void AfterCommandRun(int theCommandID)
        {
            _hub.Record("ISEApplicationEvents", "AfterCommandRun", "CommandID=" + theCommandID);
        }

        public void AfterDocumentOpen(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterDocumentOpen", "");
        }

        public void AfterDocumentPrint(object theDocument, int hDC, ref double ModelToDC, ref int Rect)
        {
            _hub.Record("ISEApplicationEvents", "AfterDocumentPrint", "");
        }

        public void AfterDocumentSave(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterDocumentSave", "");
        }

        public void AfterEnvironmentActivate(object theEnvironment)
        {
            _hub.Record("ISEApplicationEvents", "AfterEnvironmentActivate", "");
        }

        public void AfterNewDocumentOpen(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "AfterNewDocumentOpen", "");
        }

        public void AfterNewWindow(object theWindow)
        {
            _hub.Record("ISEApplicationEvents", "AfterNewWindow", "");
        }

        public void AfterWindowActivate(object theWindow)
        {
            _hub.Record("ISEApplicationEvents", "AfterWindowActivate", "");
        }

        public void BeforeCommandRun(int theCommandID)
        {
            _hub.Record("ISEApplicationEvents", "BeforeCommandRun", "CommandID=" + theCommandID);
        }

        public void BeforeDocumentClose(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "BeforeDocumentClose", "");
        }

        public void BeforeDocumentPrint(object theDocument, int hDC, ref double ModelToDC, ref int Rect)
        {
            _hub.Record("ISEApplicationEvents", "BeforeDocumentPrint", "");
        }

        public void BeforeDocumentSave(object theDocument)
        {
            _hub.Record("ISEApplicationEvents", "BeforeDocumentSave", "");
        }

        public void BeforeEnvironmentDeactivate(object theEnvironment)
        {
            _hub.Record("ISEApplicationEvents", "BeforeEnvironmentDeactivate", "");
        }

        public void BeforeQuit()
        {
            _hub.Record("ISEApplicationEvents", "BeforeQuit", "");
            // SE 要退了;活性检查(WM_TIMER)会探测到并转入重连等待模式
        }

        public void BeforeWindowDeactivate(object theWindow)
        {
            _hub.Record("ISEApplicationEvents", "BeforeWindowDeactivate", "");
        }
    }
}

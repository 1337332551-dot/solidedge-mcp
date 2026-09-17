using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdge.Spy.InteropServices;

namespace SolidEdge.Spy.McpServer
{
    /// <summary>
    /// 对象树遍历 + 身份比对 + 路径生成。
    ///
    /// 用户在 SE 里选了一个对象(target),我们要从 ActiveDocument 出发,
    /// BFS 遍历对象树,用 IUnknown 指针比对身份,找到 target 在树里的位置,
    /// 把走过的链拼成路径,如:
    ///   Application.ActiveDocument.Sketches.Item("草图 1").Line2ds.Item(12)
    /// </summary>
    public sealed class ObjectExplorer
    {
        private readonly int _maxNodes;
        private readonly int _maxPaths;
        private readonly int _maxDepth;
        private readonly int _maxSeconds;

        /// <summary>超时标记:FindPaths 因时间预算用尽而提前停止时为 true。</summary>
        public bool TimedOut { get; private set; }

        /// <summary>
        /// 插桩:遍历中最近一次正在触碰的 COM 成员(带路径)。
        /// 用途:SE 弹模态对话框堵住 COM 通道时,错误消息读取此字段,
        /// 直接报告「卡住前正在访问哪个成员」,把副作用成员定位从猜变成看。
        /// </summary>
        public static volatile string LastProbe;

        public ObjectExplorer(int maxNodes = 50000, int maxPaths = 10, int maxDepth = 20, int maxSeconds = 15)
        {
            _maxNodes = maxNodes;
            _maxPaths = maxPaths;
            _maxDepth = maxDepth;
            _maxSeconds = maxSeconds;
        }

        public List<string> FindPaths(object root, string rootPath, IntPtr targetIUnknown, string targetTypeName = null)
        {
            var paths = new List<string>();
            if (root == null || targetIUnknown == IntPtr.Zero) return paths;

            // 目标类型关键字,用于启发式剪枝(如 "Dimension"、"ModelEdge" 等短类名)
            string targetKey = ShortTypeName(targetTypeName);

            int visitedNodes = 0;
            var visited = new HashSet<IntPtr>();
            // 用 LinkedList 做"相关优先"队列:相关节点从头部出队(优先探索),
            // 无关节点从尾部出队。这样能先往目标类型所在的分支深挖,大幅减少无关遍历。
            var queue = new LinkedList<BfsNode>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            TimedOut = false;

            queue.AddLast(new BfsNode { Obj = root, Path = rootPath, Depth = 0, IsRelevant = true });

            while (queue.Count > 0 && paths.Count < _maxPaths && visitedNodes < _maxNodes)
            {
                // 时间预算:大文档 BFS 可能跑几十秒,超时会拖垮 MCP 客户端(请求超时),
                // 到点就停,返回已找到的路径并标记 truncated。
                if (stopwatch.Elapsed.TotalSeconds >= _maxSeconds)
                {
                    TimedOut = true;
                    break;
                }

                var current = queue.First.Value;
                queue.RemoveFirst();
                visitedNodes++;

                IntPtr currentPtr = SafeGetIUnknown(current.Obj);
                if (currentPtr == IntPtr.Zero) continue;

                // 命中目标?
                if (currentPtr == targetIUnknown)
                {
                    paths.Add(current.Path);
                    continue;
                }

                // 防环:已访问过跳过
                if (visited.Contains(currentPtr)) continue;
                visited.Add(currentPtr);

                if (current.Depth >= _maxDepth) continue;

                // 枚举子对象入队(带目标类型启发式剪枝 + 相关优先)
                foreach (var child in EnumerateChildren(current.Obj, current.Path, targetKey))
                {
                    if (child.Obj == null) continue;

                    var node = new BfsNode
                    {
                        Obj = child.Obj,
                        Path = child.Path,
                        Depth = current.Depth + 1,
                        IsRelevant = child.IsRelevant
                    };

                    if (child.IsRelevant)
                        queue.AddFirst(node);   // 相关节点优先探索
                    else
                        queue.AddLast(node);
                }
            }

            return paths;
        }

        // 把完整类型名缩短为便于匹配的关键字(去掉命名空间和单词变形)
        private static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            int dot = fullName.LastIndexOf('.');
            string last = dot >= 0 ? fullName.Substring(dot + 1) : fullName;
            // 只保留字母开头的部分(去掉泛型/后缀数字等干扰)
            return string.IsNullOrEmpty(last) ? null : last;
        }

        // ------------------------------------------------------------------
        // 按对象 Name/Key 全局反查路径(不依赖用户选中)。
        // 用途:知道对象名字(如 "38400"、"Dimension 38102")但拿不到句柄时,
        // BFS 全树搜索匹配 Name/Key 的对象并返回可达路径。
        // 剪枝比按类型搜索宽松(不知道目标类型):所有集合都扫,但每个集合限量。
        // ------------------------------------------------------------------
        public List<string> FindByName(object root, string rootPath, string targetName, int maxNodes = 30000, int maxDepth = 15)
        {
            var paths = new List<string>();
            if (root == null || string.IsNullOrWhiteSpace(targetName)) return paths;

            var visited = new HashSet<IntPtr>();
            var queue = new Queue<BfsNode>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            TimedOut = false;
            int nodes = 0;

            queue.Enqueue(new BfsNode { Obj = root, Path = rootPath, Depth = 0, IsRelevant = true });

            while (queue.Count > 0 && paths.Count < _maxPaths && nodes < maxNodes)
            {
                if (stopwatch.Elapsed.TotalSeconds >= _maxSeconds)
                {
                    TimedOut = true;
                    break;
                }

                var current = queue.Dequeue();
                nodes++;

                IntPtr ptr = SafeGetIUnknown(current.Obj);
                if (ptr == IntPtr.Zero) continue;
                if (!visited.Add(ptr)) continue;

                // 命中检查:Name 优先,其次 Key;精确相等,或目标长度>=3 时包含匹配
                // (用户常搜 "38400" 而对象名是 "Circle2d 38400")
                LastProbe = current.Path + ".Name";
                string n = SafeGetString(current.Obj, "Name");
                if (string.IsNullOrEmpty(n))
                {
                    LastProbe = current.Path + ".Key";
                    n = SafeGetString(current.Obj, "Key");
                }
                if (!string.IsNullOrEmpty(n))
                {
                    bool hit = string.Equals(n, targetName, StringComparison.OrdinalIgnoreCase)
                        || (targetName.Length >= 3 &&
                            n.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hit)
                    {
                        paths.Add(current.Path);
                        continue; // 命中的对象不再下钻
                    }
                }

                if (current.Depth >= maxDepth) continue;

                foreach (var child in EnumerateChildrenLoose(current.Obj, current.Path))
                {
                    if (child.Obj == null) continue;
                    queue.Enqueue(new BfsNode
                    {
                        Obj = child.Obj,
                        Path = child.Path,
                        Depth = current.Depth + 1,
                        IsRelevant = true
                    });
                }
            }

            return paths;
        }

        // 宽松子枚举:所有集合都扫(每集合限量),对象属性都入队。用于按名搜索。
        private IEnumerable<ChildInfo> EnumerateChildrenLoose(object parent, string parentPath)
        {
            var children = new List<ChildInfo>();
            Type type = parent.GetType();

            ComPtr comPtr = null;
            ComTypeInfo typeInfo = null;
            try
            {
                comPtr = ComPtr.FromRCW(parent);
                typeInfo = comPtr.TryGetComTypeInfo();
            }
            catch { }

            if (typeInfo != null)
            {
                foreach (var prop in typeInfo.Properties)
                {
                    if (prop.GetFunction == null || prop.GetFunctionHasParameters) continue;
                    string name = prop.Name;
                    if (ComSideEffectGuard.IsBlocked(name)) continue;

                    object value = null;
                    try
                    {
                        LastProbe = parentPath + "." + name;
                        value = type.InvokeMember(name,
                            BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                            null, parent, null);
                    }
                    catch { continue; }

                    if (value == null) continue;

                    if (IsCollection(value))
                    {
                        AddCollectionChildrenLoose(children, value, parentPath, name);
                    }
                    else if (Marshal.IsComObject(value))
                    {
                        children.Add(new ChildInfo
                        {
                            Obj = value,
                            Path = parentPath + "." + name,
                            IsRelevant = true
                        });
                    }
                }
            }

            try { comPtr?.Dispose(); } catch { }
            return children;
        }

        // 宽松集合展开:每集合最多 400 个元素
        private void AddCollectionChildrenLoose(List<ChildInfo> children, object collection, string parentPath, string propName)
        {
            int count = 0;
            try
            {
                object c = collection.GetType().InvokeMember("Count",
                    BindingFlags.GetProperty, null, collection, null);
                if (c is int) count = (int)c;
            }
            catch { return; }

            int scan = Math.Min(count, 400);
            for (int i = 1; i <= scan; i++)
            {
                object item = null;
                try
                {
                    LastProbe = parentPath + "." + propName + ".Item(" + i + ")";
                    item = collection.GetType().InvokeMember("Item",
                        BindingFlags.InvokeMethod, null, collection, new object[] { i });
                }
                catch { continue; }

                if (item == null) continue;

                string name = null;
                try
                {
                    LastProbe = parentPath + "." + propName + ".Item(" + i + ").Name";
                    name = item.GetType().InvokeMember("Name",
                        BindingFlags.GetProperty, null, item, null) as string;
                }
                catch { }

                string index = !string.IsNullOrEmpty(name) ? "\"" + name + "\"" : i.ToString();
                children.Add(new ChildInfo
                {
                    Obj = item,
                    Path = parentPath + "." + propName + ".Item(" + index + ")",
                    IsRelevant = true
                });
            }
        }

        // 安全读字符串属性(Name/Key 等),失败返回 null
        private static string SafeGetString(object obj, string name)
        {
            try
            {
                return obj.GetType().InvokeMember(name, BindingFlags.GetProperty, null, obj, null) as string;
            }
            catch { return null; }
        }

        // 枚举一个对象的子对象(对象型属性 + 集合元素),带目标类型启发式剪枝。
        // 剪枝原则(激进但正确):
        //  - 只遍历"可能通往目标"的属性。判据 = 属性名含目标类型关键字,或属性是已知的对象容器。
        //  - 对象容器白名单:Application/Document/Parent/Sheet/Section/Sheets/Sections/
        //    ActiveSheet/ActiveSection/SelectSet/DrawingObjects/DrawingViews/Blocks/ModelLinks 等。
        //  - 其余(几何集合、样式集合、事件集合等)一律跳过,避免遍历海量无关节点。
        // 这样从根出发能快速收敛到目标类型所在的分支,而不是全对象树深挖。
        private IEnumerable<ChildInfo> EnumerateChildren(object parent, string parentPath, string targetKey)
        {
            var children = new List<ChildInfo>();
            Type type = parent.GetType();

            ComPtr comPtr = null;
            ComTypeInfo typeInfo = null;
            try
            {
                comPtr = ComPtr.FromRCW(parent);
                typeInfo = comPtr.TryGetComTypeInfo();
            }
            catch { }

            if (typeInfo != null)
            {
                foreach (var prop in typeInfo.Properties)
                {
                    // 只读 Get 函数 + 不带参数的简单属性
                    if (prop.GetFunction == null || prop.GetFunctionHasParameters) continue;

                    string name = prop.Name;

                    // 副作用成员一律跳过(共享黑名单,见 ComSideEffectGuard)。
                    if (ComSideEffectGuard.IsBlocked(name)) continue;

                    // 剪枝判定:该属性是否可能通往目标。
                    bool relevant = !string.IsNullOrEmpty(targetKey) &&
                        name.IndexOf(targetKey, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool container = IsObjectContainer(name);

                    if (!relevant && !container)
                        continue; // 无关分支,直接跳过,不 get 不求值

                    object value = null;
                    try
                    {
                        LastProbe = parentPath + "." + name;
                        value = type.InvokeMember(name,
                            BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                            null, parent, null);
                    }
                    catch { continue; }

                    if (value == null) continue;

                    if (IsCollection(value))
                    {
                        AddCollectionChildren(children, value, parentPath, name, relevant);
                    }
                    else if (Marshal.IsComObject(value))
                    {
                        children.Add(new ChildInfo
                        {
                            Obj = value,
                            Path = parentPath + "." + name,
                            IsRelevant = relevant
                        });
                    }
                }
            }

            try { comPtr?.Dispose(); } catch { }
            return children;
        }

        // 是否是可继续下钻的对象容器(不含目标关键字时,这些仍可能是通往目标的中间节点)。
        private static bool IsObjectContainer(string propName)
        {
            switch (propName)
            {
                case "Application":
                case "Document":
                case "Parent":
                case "Sheet":
                case "Section":
                case "ActiveSheet":
                case "ActiveSection":
                case "ActiveSketch":
                case "Sheets":
                case "Sections":
                case "SelectSet":
                case "ActiveSelectSet":
                case "DrawingObjects":
                case "DrawingViews":
                case "Blocks":
                case "ModelLinks":
                case "RootStorage":
                case "SourceDrawingView":
                case "ModelLink":
                case "Model":
                case "Models":
                    // "Model"(2026-09-16 真机实证):PartDocument 的特征集合都挂在
                    // PartDocument.Model 之下(如 Model.RevolvedProtrusions),
                    // 白名单缺 Model 时 FindPaths 永远找不到特征的自然路径。
                    return true;
                default:
                    return false;
            }
        }

        // 判断是否是 COM 集合(有 Count 属性)
        private bool IsCollection(object obj)
        {
            try
            {
                obj.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, obj, null);
                return true;
            }
            catch { return false; }
        }

        // 把集合里每个元素作为子节点加入(大集合限制前 500 个,避免拖慢)
        private void AddCollectionChildren(List<ChildInfo> children, object collection, string parentPath, string propName, bool isRelevant)
        {
            int count = 0;
            try
            {
                object c = collection.GetType().InvokeMember("Count",
                    BindingFlags.GetProperty, null, collection, null);
                if (c is int) count = (int)c;
            }
            catch { return; }

            // 相关集合(属性名含目标类型关键字)可多扫;无关集合少扫
            int maxScan = isRelevant ? 1000 : 200;
            int scan = Math.Min(count, maxScan);

            for (int i = 1; i <= scan; i++)
            {
                object item = null;
                try
                {
                    LastProbe = parentPath + "." + propName + ".Item(" + i + ")";
                    item = collection.GetType().InvokeMember("Item",
                        BindingFlags.InvokeMethod, null, collection, new object[] { i });
                }
                catch { continue; }

                if (item == null) continue;

                // 优先用 Name 作为索引(可读),否则用数字
                string name = null;
                try
                {
                    LastProbe = parentPath + "." + propName + ".Item(" + i + ").Name";
                    name = item.GetType().InvokeMember("Name",
                        BindingFlags.GetProperty, null, item, null) as string;
                }
                catch { }

                string index = !string.IsNullOrEmpty(name) ? "\"" + name + "\"" : i.ToString();
                children.Add(new ChildInfo
                {
                    Obj = item,
                    Path = parentPath + "." + propName + ".Item(" + index + ")",
                    IsRelevant = isRelevant
                });
            }
        }

        private IntPtr SafeGetIUnknown(object obj)
        {
            try { return Marshal.GetIUnknownForObject(obj); }
            catch { return IntPtr.Zero; }
        }

        private sealed class BfsNode
        {
            public object Obj;
            public string Path;
            public int Depth;
            public bool IsRelevant;
        }

        private sealed class ChildInfo
        {
            public object Obj;
            public string Path;
            public bool IsRelevant;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 声明式建模的"语法单元级 IR":features JSON 解析后的中间表示。
    ///
    /// 构建器(ModelingTools)与静态校验器(FeatureValidator)【共用这一份解析】——
    /// 这是本层存在的唯一理由:若校验器另写一套解析,二者行为必然漂移,
    /// 校验就会退化成"看起来对但实际不管用"。
    ///
    /// 字段语义与单位:
    ///   坐标/长度一律【米】(UI 显示 mm,×1000);角度为弧度。
    ///   Loops 里的点是【目标平面的局部 u/v】,不是全局 XYZ。
    ///
    /// 2026-09-08 抽出。取数语义与改造前逐条对齐,刻意保留了几个历史行为:
    ///   - op 不是字符串(缺失/数字)时回退 "extrude";
    ///   - circle 半径 <= 0 视为"没有圆",回退走直线环;
    ///   - side/profileside/depth 缺字段或类型不可解析 → 记 null,由各 op 套自己的默认值
    ///     (extrude side 默认 2、cut side 默认 1,两边不一样,所以默认值留在 op 里)。
    ///
    /// 架构思想参考 SolidPilot(见 SolidPilot建模思路学习总结.md「报错带诊断」一条),
    /// 但本文件是面向 Solid Edge API 的独立实现,不含其代码或 schema 文本。
    /// </summary>
    public sealed class FeatureSpec
    {
        /// <summary>在 features 数组中的序号(0-based),用于报错定位。</summary>
        public int Index;

        /// <summary>原始 op 字符串(未改大小写,用于回显);缺失/非字符串时为 "extrude"。</summary>
        public string Op = "extrude";

        /// <summary>op 的小写形式,switch 用它。</summary>
        public string OpLower = "extrude";

        public string Name;

        /// <summary>"plane" 字段(草图所在面)。</summary>
        public string PlaneRef;

        /// <summary>"base" 字段(plane op 的基准面)。</summary>
        public string BaseRef;

        /// <summary>plane op 的偏移距离(米);缺字段时为 0。</summary>
        public double Distance;

        public bool HasDistance;

        /// <summary>extrude 必须、cut(mode=finite)可选;缺字段或类型不可解析时为 null。</summary>
        public double? Depth;

        /// <summary>ProfilePlaneSide。null = 未给,由 op 套默认值。</summary>
        public int? Side;

        /// <summary>ProfileSide。null = 未给,由 op 套默认值。</summary>
        public int? ProfileSide;

        /// <summary>cut 的除料方式:next / all / finite;缺失时为 null(op 套 "next")。</summary>
        public string Mode;

        /// <summary>
        /// 旋转轴(revolve 专用)两点:[[u1,v1],[u2,v2]] 或 [u1,v1,u2,v2]。
        /// 局部 u/v 坐标(米)。轴是【独立于截面】的构造线,不参与截面闭环约束。
        /// </summary>
        public double[] AxisP1, AxisP2;

        /// <summary>是否提供了有效旋转轴(写了且两点不退化)。</summary>
        public bool HasAxis;

        /// <summary>旋转角(弧度);null = 未给,由 op 套默认 2π(360°)。</summary>
        public double? Angle;

        /// <summary>旋转角(度);给了则换算成弧度覆盖 Angle,方便人写。 </summary>
        public double? Degrees;

        /// <summary>草图可见性。null = 未给,默认 false(创建即隐藏)。</summary>
        public bool? Visible;

        /// <summary>自动补全几何约束:按坐标推断 H/V(|dy|&lt;eps → 水平,|dx|&lt;eps → 垂直)。null = 未给,默认 false。</summary>
        public bool? AutoConstraint;

        /// <summary>固定首环首线起点(AddKeypointFix,消除整体平移自由度)。null = 未给,默认 false。</summary>
        public bool? FixOrigin;

        /// <summary>直线长度标注声明(轮廓 End 之前的开放上下文里应用,这是绑定生效的唯一窗口)。</summary>
        public List<DimSpec> Dims = new List<DimSpec>();

        /// <summary>是否为有效圆轮廓(radius &gt; 0 才算)。</summary>
        public bool HasCircle;

        public double CircleX, CircleY, CircleR;

        /// <summary>
        /// 多真圆(circles 数组):每项 [x,y,r]。用于一次切/拉多个真圆(如法兰螺栓孔阵列),
        /// 比 loops 多边形近似更准(孔壁是真圆柱面)。
        /// </summary>
        public List<double[]> Circles = new List<double[]>();

        /// <summary>是否提供了有效的 circles 数组(至少 1 个 r&gt;0)。</summary>
        public bool HasCircles;

        /// <summary>
        /// 是否为有效腰孔(长圆孔)轮廓。用【真圆弧】构造:两条直线边 + 两端半圆弧,
        /// 不像 polygon 那样用折线近似——折线近似会在两端留下可见的棱。
        /// </summary>
        public bool HasSlot;

        /// <summary>腰孔中心(局部 u/v,米)。</summary>
        public double SlotX, SlotY;

        /// <summary>腰孔总长(含两端半圆)与宽度(即两端半圆的直径),米。</summary>
        public double SlotLength, SlotWidth;

        /// <summary>腰孔长轴相对局部 u 轴的转角(弧度),默认 0。</summary>
        public double SlotAngle;

        /// <summary>形状来源:circle / loops / rect / polygon / ""(都没有)。</summary>
        public string ShapeSource = "";

        /// <summary>归一化后的闭合环点列(局部 u/v,米)。未走圆轮廓时才有值。</summary>
        public List<double[][]> Loops = new List<double[][]>();

        /// <summary>无 circle 且解析不出任何环时的错误信息;为 null 表示形状可用。</summary>
        public string ShapeError;

        /// <summary>已知字段白名单之外的字段名,供 W102 提示(可能拼错/使用了不支持的字段)。</summary>
        public List<string> UnknownFields = new List<string>();

        /// <summary>预留:Profile.End 模式。当前构建器硬编码 End(0),尚未启用本字段。</summary>
        public string EndMode;

        /// <summary>原始 JSON(Clone 过,可安全跨作用域持有),供校验器做类型/未知字段等细查。</summary>
        public JsonElement Raw;

        /// <summary>该特征是否带草图形状(plane op 不需要)。</summary>
        public bool NeedsShape
        {
            get { return OpLower == "extrude" || OpLower == "cut" || OpLower == "revolve"; }
        }
    }

    /// <summary>
    /// 直线长度标注声明。"element" 是跨环扁平的 0-based 线索引(第 0 环的线排最前,轴/构造线不占位)。
    /// "name" → PutName 进变量表(标注即变量);"value" → 直接值(如 "40 mm");"formula" → 公式(如 "Rad1 - 5 mm")。
    /// </summary>
    public sealed class DimSpec
    {
        public int Element;

        public string Name;

        public string Value;

        public string Formula;

        /// <summary>解析失败原因;null = 可用。</summary>
        public string ParseError;
    }

    /// <summary>
    /// features JSON → FeatureSpec 的解析器。
    ///
    /// 纯函数、不碰 COM,SE 没启动也能跑——这是校验层能"事前拦截"的前提。
    /// </summary>
    public static class FeatureSpecParser
    {
        /// <summary>顶层已知字段白名单(大小写不敏感)。center/radius 是 circle 的子字段,不在顶层。</summary>
        private static readonly HashSet<string> KnownFields =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "op", "name", "plane", "base", "distance", "depth",
                "side", "profileside", "mode", "visible",
                "circle", "circles", "slot", "rect", "polygon", "loops",
                "axis", "angle", "degrees",   // revolve 专用
                "autoconstraint", "fixorigin", "dims",   // 2026-09-13:完全约束+变量绑定(End 前应用)
                "endmode"   // 预留
            };

        public static List<FeatureSpec> ParseAll(JsonElement[] features)
        {
            var list = new List<FeatureSpec>();
            if (features == null) return list;
            for (int i = 0; i < features.Length; i++)
                list.Add(Parse(features[i], i));
            return list;
        }

        public static FeatureSpec Parse(JsonElement feat, int index)
        {
            var s = new FeatureSpec { Index = index };

            if (feat.ValueKind == JsonValueKind.Object)
                s.Raw = feat.Clone();

            s.Op = GetStr(feat, "op") ?? "extrude";
            s.OpLower = s.Op.ToLowerInvariant();
            s.Name = GetStr(feat, "name");
            s.PlaneRef = GetStr(feat, "plane");
            s.BaseRef = GetStr(feat, "base");
            s.Mode = GetStr(feat, "mode");
            s.EndMode = GetStr(feat, "endmode");

            // revolve 专用:旋转轴与角度
            if (TryGetAxis(feat, out double[] ap1, out double[] ap2))
            {
                s.HasAxis = true;
                s.AxisP1 = ap1;
                s.AxisP2 = ap2;
            }
            if (TryGetDbl(feat, "angle", out double ang)) s.Angle = ang;
            if (TryGetDbl(feat, "degrees", out double deg)) s.Degrees = deg;

            if (TryGetDbl(feat, "distance", out double dist)) { s.Distance = dist; s.HasDistance = true; }
            if (TryGetDbl(feat, "depth", out double d)) s.Depth = d;
            if (TryGetInt(feat, "side", out int sd)) s.Side = sd;
            if (TryGetInt(feat, "profileside", out int ps)) s.ProfileSide = ps;
            if (TryGetBool(feat, "visible", out bool vb)) s.Visible = vb;
            if (TryGetBoolCI(feat, "autoconstraint", out bool ac)) s.AutoConstraint = ac;
            if (TryGetBoolCI(feat, "fixorigin", out bool fo)) s.FixOrigin = fo;
            if (feat.TryGetProperty("dims", out var dimsEl) && dimsEl.ValueKind == JsonValueKind.Array)
            {
                int dimIdx = 0;
                foreach (var item in dimsEl.EnumerateArray())
                {
                    var ds = new DimSpec();
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        ds.ParseError = "第 " + dimIdx + " 项不是对象";
                    }
                    else
                    {
                        if (item.TryGetProperty("element", out var elEl) && elEl.ValueKind == JsonValueKind.Number)
                            ds.Element = elEl.GetInt32();
                        else
                            ds.ParseError = "缺 element(0-based 线索引)";
                        ds.Name = GetStr(item, "name");
                        ds.Value = GetStr(item, "value");
                        ds.Formula = GetStr(item, "formula");
                        if (ds.Name == null)
                            ds.ParseError = (ds.ParseError != null ? ds.ParseError + ";" : "") + "缺 name";
                        else if (ds.Value == null && ds.Formula == null)
                            ds.ParseError = (ds.ParseError != null ? ds.ParseError + ";" : "") + "value/formula 至少给一个";
                    }
                    s.Dims.Add(ds);
                    dimIdx++;
                }
            }

            // 形状:优先 circle(与构建器取形状的先后顺序完全一致)
            if (TryGetCircle(feat, out double cx, out double cy, out double r))
            {
                s.HasCircle = true;
                s.CircleX = cx; s.CircleY = cy; s.CircleR = r;
                s.ShapeSource = "circle";
            }
            else if (TryGetCircles(feat, out var circs))
            {
                s.Circles = circs;
                s.HasCircles = true;
                s.ShapeSource = "circles";
            }
            else if (TryGetSlot(feat, out double sx, out double sy, out double slen, out double swid, out double sang))
            {
                s.HasSlot = true;
                s.SlotX = sx; s.SlotY = sy; s.SlotLength = slen; s.SlotWidth = swid; s.SlotAngle = sang;
                s.ShapeSource = "slot";
            }
            else
            {
                // 构建器在这里会抛异常;解析器改为记录错误,由构建器在用时抛、校验器当 issue 报。
                try
                {
                    s.Loops = ParseLoops(feat);
                    s.ShapeSource = DetectShapeSource(feat);
                }
                catch (ArgumentException ex)
                {
                    s.ShapeError = ex.Message;
                    s.ShapeSource = "";
                }
            }

            if (feat.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in feat.EnumerateObject())
                {
                    if (!KnownFields.Contains(p.Name))
                        s.UnknownFields.Add(p.Name);
                }
            }

            return s;
        }

        private static string DetectShapeSource(JsonElement feat)
        {
            if (HasNonEmptyArray(feat, "loops")) return "loops";
            if (HasNonEmptyArray(feat, "polygon")) return "polygon";
            if (HasNonEmptyArray(feat, "rect")) return "rect";
            return "";
        }

        private static bool HasNonEmptyArray(JsonElement e, string key)
        {
            return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;
        }

        // ---------------- 形状解析(与改造前 ModelingTools 的实现逐条对齐) ----------------

        /// <summary>
        /// 解析圆形草图。两种写法:
        ///   "circle": {"center":[x,y],"radius":0.005}
        ///   "circle": [x, y, 0.005]
        /// 半径 &gt; 0 才算有效圆(与改造前一致:否则回退走直线环)。
        /// </summary>
        private static bool TryGetCircle(JsonElement feat, out double cx, out double cy, out double r)
        {
            cx = cy = r = 0;
            if (!feat.TryGetProperty("circle", out var v)) return false;

            if (v.ValueKind == JsonValueKind.Array)
            {
                var arr = new List<double>();
                foreach (var item in v.EnumerateArray())
                    arr.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0);
                if (arr.Count < 3) return false;
                cx = arr[0]; cy = arr[1]; r = arr[2];
                return r > 0;
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                TryGetCenter(v, out cx, out cy);
                r = GetDbl(v, "radius", 0);
                return r > 0;
            }

            return false;
        }

        /// <summary>
        /// 解析多真圆(circles)。写法:
        ///   "circles": [[x1,y1,r1],[x2,y2,r2], ...]
        /// 只保留 r&gt;0 的项;至少 1 个有效才算成功。用于一次切/拉多个真圆孔(如法兰螺栓孔阵列)。
        /// </summary>
        private static bool TryGetCircles(JsonElement feat, out List<double[]> circles)
        {
            circles = new List<double[]>();
            if (!feat.TryGetProperty("circles", out var v)) return false;
            if (v.ValueKind != JsonValueKind.Array) return false;

            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var arr = new List<double>();
                foreach (var x in item.EnumerateArray())
                    arr.Add(x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0);
                if (arr.Count < 3) continue;
                if (arr[2] > 0) circles.Add(new[] { arr[0], arr[1], arr[2] });
            }
            return circles.Count > 0;
        }

        /// <summary>
        /// 解析腰孔(长圆孔)草图。两种写法:
        ///   "slot": {"center":[x,y],"length":0.1,"width":0.03,"angle":0}
        ///   "slot": [x, y, 0.1, 0.03]          (简写:中心 + 总长 + 宽,angle 可加第 5 位)
        /// length 是【含两端半圆的总长】,width 是宽度(也是端头半圆的直径)。
        /// 用真圆弧构造,不是折线近似。
        /// </summary>
        private static bool TryGetSlot(JsonElement feat, out double cx, out double cy,
            out double len, out double wid, out double ang)
        {
            cx = cy = len = wid = ang = 0;
            if (!feat.TryGetProperty("slot", out var v)) return false;

            if (v.ValueKind == JsonValueKind.Array)
            {
                var arr = new List<double>();
                foreach (var item in v.EnumerateArray())
                    arr.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0);
                if (arr.Count < 4) return false;
                cx = arr[0]; cy = arr[1]; len = arr[2]; wid = arr[3];
                if (arr.Count >= 5) ang = arr[4];
                return wid > 0 && len > 0;
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                TryGetCenter(v, out cx, out cy);
                len = GetDbl(v, "length", 0);
                wid = GetDbl(v, "width", 0);
                ang = GetDbl(v, "angle", 0);
                return wid > 0 && len > 0;
            }

            return false;
        }

        /// <summary>
        /// 解析 circle / slot 的 "center" 字段。接受两种写法:扁平 [x,y](文档语法)、
        /// 嵌套 [[x,y]](按点列习惯写的)。
        /// 历史坑(2026-09-15 由单元测试抓出并修复):center 为 [x,y] 时曾走 GetPoints,
        /// 它要求"每个元素是点数组且至少 2 个点",扁平 [x,y] 被判无效 → 圆心/腰孔中心
        /// 静默变成 (0,0),特征画在原点。
        /// </summary>
        private static bool TryGetCenter(JsonElement obj, out double cx, out double cy)
        {
            cx = cy = 0;
            if (!obj.TryGetProperty("center", out var c) || c.ValueKind != JsonValueKind.Array) return false;

            if (c.GetArrayLength() >= 2 && c[0].ValueKind == JsonValueKind.Number)
            {
                cx = c[0].GetDouble();
                cy = c[1].ValueKind == JsonValueKind.Number ? c[1].GetDouble() : 0;
                return true;
            }
            if (c.GetArrayLength() >= 1 && c[0].ValueKind == JsonValueKind.Array)
            {
                foreach (var pair in c.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
                    var it = pair.EnumerateArray();
                    it.MoveNext();
                    cx = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                    it.MoveNext();
                    cy = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 解析旋转轴(revolve 专用)。两种写法:
        ///   "axis": [[u1,v1],[u2,v2]]
        ///   "axis": [u1, v1, u2, v2]
        /// 两点【不重合】才算有效轴——退化成点的轴会让 AddFinite 失败,所以这里直接判掉。
        /// </summary>
        private static bool TryGetAxis(JsonElement feat, out double[] p1, out double[] p2)
        {
            p1 = p2 = null;
            if (feat.ValueKind != JsonValueKind.Object) return false;
            if (!feat.TryGetProperty("axis", out var v)) return false;
            if (v.ValueKind != JsonValueKind.Array) return false;

            // 写法 1:[[u1,v1],[u2,v2]]
            double[][] pts = ParsePointArray(v);
            if (pts != null && pts.Length >= 2)
            {
                p1 = pts[0];
                p2 = pts[1];
                return !NearlySame(p1, p2);
            }

            // 写法 2:[u1,v1,u2,v2]
            var flat = new List<double>();
            foreach (var item in v.EnumerateArray())
                flat.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0);
            if (flat.Count < 4) return false;

            p1 = new[] { flat[0], flat[1] };
            p2 = new[] { flat[2], flat[3] };
            return !NearlySame(p1, p2);
        }

        private static bool NearlySame(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length < 2 || b.Length < 2) return false;
            return Math.Abs(a[0] - b[0]) < 1e-12 && Math.Abs(a[1] - b[1]) < 1e-12;
        }

        /// <summary>解析特征草图:优先 loops(多个闭合环),回退 rect / polygon(单个环)。</summary>
        public static List<double[][]> ParseLoops(JsonElement feat)
        {
            var loops = new List<double[][]>();

            if (feat.TryGetProperty("loops", out var loopsEl) && loopsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var loopEl in loopsEl.EnumerateArray())
                {
                    double[][] pts = ParsePointArray(loopEl);
                    if (pts != null && pts.Length >= 3) loops.Add(pts);
                }
                if (loops.Count > 0) return loops;
            }

            double[][] single = ResolveProfilePoints(feat);
            if (single != null) loops.Add(single);
            if (loops.Count == 0)
                throw new ArgumentException("特征缺少 rect(两角点)/polygon(>=3 点)/loops(多环)草图。");
            return loops;
        }

        private static double[][] ResolveProfilePoints(JsonElement feat)
        {
            double[][] pts = GetPoints(feat, "polygon");
            if (pts != null && pts.Length >= 3) return pts;

            pts = GetPoints(feat, "rect");
            if (pts != null && pts.Length >= 2)
            {
                double x1 = pts[0][0], y1 = pts[0][1], x2 = pts[1][0], y2 = pts[1][1];
                double minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2);
                double minY = Math.Min(y1, y2), maxY = Math.Max(y1, y2);
                return new[]
                {
                    new[] { minX, minY }, new[] { maxX, minY },
                    new[] { maxX, maxY }, new[] { minX, maxY }
                };
            }

            throw new ArgumentException("特征缺少 rect(两角点)或 polygon(>=3 点)草图。");
        }

        /// <summary>解析 [[x,y],...] 点列;不足 2 点返回 null。</summary>
        public static double[][] ParsePointArray(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Array) return null;
            var pts = new List<double[]>();
            foreach (var pt in el.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                var it = pt.EnumerateArray();
                it.MoveNext();
                double x = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                it.MoveNext();
                double y = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                pts.Add(new[] { x, y });
            }
            return pts.Count >= 2 ? pts.ToArray() : null;
        }

        // ---------------- 取值原语(nullable 版:区分"没给"与"给了 0") ----------------

        private static string GetStr(JsonElement e, string key)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        private static bool TryGetDbl(JsonElement e, string key, out double val)
        {
            val = 0;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) { val = d; return true; }
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2))
            { val = d2; return true; }
            return false;
        }

        private static bool TryGetInt(JsonElement e, string key, out int val)
        {
            val = 0;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) { val = i; return true; }
            if (v.ValueKind == JsonValueKind.String &&
                int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i2))
            { val = i2; return true; }
            return false;
        }

        /// <summary>大小写不敏感的布尔读取(Utf8JsonReader.TryGetProperty 默认大小写敏感,驼峰键会漏读)。</summary>
        private static bool TryGetBoolCI(JsonElement e, string key, out bool val)
        {
            val = false;
            if (e.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in e.EnumerateObject())
            {
                if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.True) { val = true; return true; }
                if (p.Value.ValueKind == JsonValueKind.False) { val = false; return true; }
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    if (bool.TryParse(p.Value.GetString(), out val)) return true;
                }
            }
            return false;
        }

        private static bool TryGetBool(JsonElement e, string key, out bool val)
        {
            val = false;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind == JsonValueKind.True) { val = true; return true; }
            if (v.ValueKind == JsonValueKind.False) { val = false; return true; }
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) { val = b; return true; }
            return false;
        }

        private static double GetDbl(JsonElement e, string key, double def)
        {
            return TryGetDbl(e, key, out var d) ? d : def;
        }

        /// <summary>解析 [[x,y],...] 二维点数组;不足 2 点返回 null。</summary>
        private static double[][] GetPoints(JsonElement e, string key)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;

            var pts = new List<double[]>();
            foreach (var pt in v.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                var it = pt.EnumerateArray();
                it.MoveNext();
                double x = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                it.MoveNext();
                double y = it.Current.ValueKind == JsonValueKind.Number ? it.Current.GetDouble() : 0;
                pts.Add(new[] { x, y });
            }
            return pts.Count >= 2 ? pts.ToArray() : null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 规则注册表。新增一条规则 = 加一个类 + 在这里 new 一行,不动其它规则。
    ///
    /// 编号体系:
    ///   E1xx/W1xx 结构   E2xx/W2xx 引用   E3xx/W3xx 几何   E4xx/W4xx 语义
    /// E000 = 规则自身异常(兜底)。
    ///
    /// 分级原则:**宁 warning 不 error**——只有"必然失败"的才 error,
    /// 否则误报会打断 LLM 的流程,比漏报更烦人。
    /// </summary>
    public static class RuleRegistry
    {
        public static List<IFeatureRule> Build()
        {
            return new List<IFeatureRule>
            {
                // ---- 结构层 ----
                new OpRule(),                 // E101 未知 op
                new RequiredShapeRule(),      // E102 缺草图形状
                new FieldTypeRule(),          // E103 字段类型错误 / E305 circle 半径非法
                new NonFiniteRule(),          // E104 NaN / Infinity
                new DimensionRule(),          // W101 量纲可疑
                new UnknownFieldRule(),       // W102 未知字段

                // ---- 引用层 ----
                new PlaneRefRule(),           // E201/E202/W202/E203/W204

                // ---- 几何层 ----
                new LoopGeometryRule(),       // E301/E302/E303/E304/W301

                // ---- 语义层 ----
                new CutBeforeExtrudeRule(),   // E401
                new ExtrudeDepthRule(),       // E402
                new RevolveAxisRule(),        // E403 revolve 缺轴 / 轴退化 / 角度越界
                new CutModeRule(),            // E404 finite 缺 depth / W405 未指定 mode(默认会切穿)
                new SideDirectionRule(),      // E105/W402
                new ConsecutiveCutRule(),     // W401 同平面多孔未合并(僵尸头号杀手)
                new CutOutsideStockRule(),    // W403 除料落在毛坯外
                new LoopOverlapRule(),        // W404 环重叠
            };
        }
    }

    // ==================== 结构层 ====================

    /// <summary>E101:op 必须是 plane / extrude / cut / revolve。</summary>
    public sealed class OpRule : IFeatureRule
    {
        public string Code { get { return "E101"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (s.OpLower == "plane" || s.OpLower == "extrude" || s.OpLower == "cut" || s.OpLower == "revolve")
                yield break;

            yield return ctx.Error("E101", "op",
                "未知 op \"" + s.Op + "\",仅支持 plane / extrude / cut / revolve。",
                new { action = "set", field = "op", allowed = new[] { "plane", "extrude", "cut", "revolve" } });
        }
    }

    /// <summary>E102:extrude / cut / revolve 必须有可解析的草图形状(circle / circles / slot / rect / polygon / loops 六选一)。</summary>
    public sealed class RequiredShapeRule : IFeatureRule
    {
        public string Code { get { return "E102"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut", "revolve" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (s.HasCircle || s.HasCircles || s.HasSlot) yield break;
            if (s.ShapeError == null) yield break;

            yield return ctx.Error("E102", "shape", s.ShapeError + " 或 circle(圆)/circles(多真圆)/slot(腰孔)/rect(矩形)/polygon(多边形)/loops(多环点列)。",
                new { action = "provide", field = "shape", oneOf = new[] { "circle", "circles", "slot", "rect", "polygon", "loops" } });
        }
    }

    /// <summary>
    /// E403:revolve 必须有旋转轴 axis,且轴不能退化、角度必须合法。
    ///
    /// 旋转是唯一"多一个自由度"的 op:轮廓之外还得有轴,少了轴整个特征无从定义,
    /// 所以是 error 而不是 warning。角度超过一整圈 AddFinite 也接受不了。
    /// </summary>
    public sealed class RevolveAxisRule : IFeatureRule
    {
        public string Code { get { return "E403"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "revolve" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (!s.HasAxis)
            {
                bool wrote = s.Raw.ValueKind == JsonValueKind.Object && s.Raw.TryGetProperty("axis", out _);

                yield return ctx.Error("E403", "axis",
                    wrote
                        ? "旋转轴两点重合(退化成点),无法确定旋转方向。"
                        : "revolve 缺少 axis(旋转轴两点)。",
                    new
                    {
                        action = wrote ? "fix" : "provide",
                        field = "axis",
                        format = "[[u1,v1],[u2,v2]] 或 [u1,v1,u2,v2]",
                        hint = "轴是草图平面内的直线,如沿局部 v 轴:[[0,0],[0,0.05]]"
                    });
                yield break;
            }

            double angle = s.Degrees.HasValue
                ? s.Degrees.Value * Math.PI / 180.0
                : (s.Angle.HasValue ? s.Angle.Value : 2.0 * Math.PI);

            if (!(angle > 0))
            {
                yield return ctx.Error("E403", "angle",
                    "旋转角必须 > 0(当前 " + GeoUtil.Fmt(angle) + " 弧度)。",
                    new { action = "set", field = "angle", value = 6.283185307179586 });
            }
            else if (angle > 2.0 * Math.PI + 1e-9)
            {
                yield return ctx.Error("E403", "angle",
                    "旋转角超过一整圈(2π 弧度 / 360 度)。",
                    new { action = "clamp", field = "angle", max = 6.283185307179586 });
            }
        }
    }

    /// <summary>
    /// E404 / W405:cut 的除料模式,两条都是"静默默认值"陷阱。
    ///
    ///   W405(警告)——没写 mode:默认走 AddThroughNext(切到下一个面)。在单块实体上
    ///     这等于【切穿】,而多数人开门窗要的是定深凹槽。不阻拦,但必须提醒。
    ///   E404(错误)——mode="finite" 却没给 depth:构建器会静默套用 0.2 米默认值,
    ///     几乎肯定不是用户想要的尺寸,直接拦下要求写明。
    /// </summary>
    public sealed class CutModeRule : IFeatureRule
    {
        public string Code { get { return "E404"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            string mode = (s.Mode ?? "").Trim().ToLowerInvariant();

            if (mode == "finite")
            {
                if (!s.Depth.HasValue)
                {
                    yield return ctx.Error("E404", "depth",
                        "mode=\"finite\" 必须给 depth(米);否则会静默套用 0.2 米的默认值。",
                        new { action = "provide", field = "depth", hint = "如 \"depth\":0.02 = 切 20mm 深的盲槽" });
                }
                yield break;
            }

            if (mode == "" || mode == "next")
            {
                yield return ctx.Warn("W405", "mode",
                    "cut 未指定 mode,默认走 AddThroughNext(切到下一个面)——在单块实体上就是切穿。" +
                    "若要定深凹槽/盲孔,请用 \"mode\":\"finite\" 并给出 depth。",
                    new { action = "set", field = "mode", value = "finite", also = "depth" });
            }
        }
    }

    /// <summary>
    /// E103:字段类型错误(给了但解析器读不出来,会被静默按默认值处理)。
    /// 这类错误最阴——表面"跑通了",实际参数根本没生效。
    /// </summary>
    public sealed class FieldTypeRule : IFeatureRule
    {
        public string Code { get { return "E103"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        private static readonly string[] StringFields = { "op", "name", "plane", "base", "mode", "endmode" };
        private static readonly string[] NumberFields = { "depth", "distance", "angle", "degrees" };
        private static readonly string[] IntFields = { "side", "profileside" };

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.Raw.ValueKind != JsonValueKind.Undefined && s.Raw.ValueKind != JsonValueKind.Object)
            {
                yield return ctx.Error("E103", null,
                    "特征必须是 JSON 对象,实际是 " + s.Raw.ValueKind + "。",
                    new { action = "wrap", field = (string)null });
                yield break;
            }

            foreach (var f in StringFields)
                if (BadString(s, f))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是字符串。",
                        new { action = "set_type", field = f, type = "string" });

            foreach (var f in NumberFields)
                if (BadNumber(s, f))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是数字(或数字字符串)。",
                        new { action = "set_type", field = f, type = "number" });

            foreach (var f in IntFields)
                if (BadInt(s, f))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是整数(或整数字符串)。",
                        new { action = "set_type", field = f, type = "int" });

            if (BadBool(s, "visible"))
                yield return ctx.Error("E103", "visible", "字段 \"visible\" 必须是布尔值。",
                    new { action = "set_type", field = "visible", type = "bool" });

            if (BadShapeKind(s, "circle", true))
                yield return ctx.Error("E103", "circle",
                    "字段 \"circle\" 必须是对象 {\"center\":[x,y],\"radius\":r} 或数组 [x,y,r]。", null);
            if (BadShapeKind(s, "circles", false))
                yield return ctx.Error("E103", "circles",
                    "字段 \"circles\" 必须是圆数组 [[x,y,r], ...]。", null);
            foreach (var f in new[] { "rect", "polygon", "loops" })
                if (BadShapeKind(s, f, false))
                    yield return ctx.Error("E103", f, "字段 \"" + f + "\" 必须是点列数组。", null);
        }

        private static bool TryRaw(FeatureSpec s, string name, out JsonElement el)
        {
            el = default(JsonElement);
            if (s.Raw.ValueKind != JsonValueKind.Object) return false;
            return s.Raw.TryGetProperty(name, out el);
        }

        private static bool BadString(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            return el.ValueKind != JsonValueKind.String && el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadNumber(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.Number) return false;
            if (el.ValueKind == JsonValueKind.String)
                return !double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            return el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadInt(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.Number) return !el.TryGetInt32(out _);
            if (el.ValueKind == JsonValueKind.String)
                return !int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            return el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadBool(FeatureSpec s, string f)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String) return !bool.TryParse(el.GetString(), out _);
            return el.ValueKind != JsonValueKind.Null;
        }

        private static bool BadShapeKind(FeatureSpec s, string f, bool allowObject)
        {
            JsonElement el;
            if (!TryRaw(s, f, out el)) return false;
            if (el.ValueKind == JsonValueKind.Array) return false;
            if (allowObject && el.ValueKind == JsonValueKind.Object) return false;
            return el.ValueKind != JsonValueKind.Null;
        }
    }

    /// <summary>E104:NaN / Infinity 会直接把 COM 调用带沟里,必须拦在前面。</summary>
    public sealed class NonFiniteRule : IFeatureRule
    {
        public string Code { get { return "E104"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            if (s.Depth.HasValue && !IsFinite(s.Depth.Value))
                yield return ctx.Error("E104", "depth", "depth 不是有限数值。", null);
            if (s.HasDistance && !IsFinite(s.Distance))
                yield return ctx.Error("E104", "distance", "distance 不是有限数值。", null);
            if (s.HasCircle && (!IsFinite(s.CircleR) || !IsFinite(s.CircleX) || !IsFinite(s.CircleY)))
                yield return ctx.Error("E104", "circle", "circle 的圆心/半径不是有限数值。", null);

            for (int i = 0; i < s.Loops.Count; i++)
            {
                foreach (var p in s.Loops[i])
                {
                    if (!IsFinite(p[0]) || !IsFinite(p[1]))
                    {
                        yield return ctx.Error("E104", "loops[" + i + "]",
                            "第 " + (i + 1) + " 个环含非有限坐标。", null);
                        break;
                    }
                }
            }
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }

    /// <summary>
    /// W101:量纲哨兵。单位是【米】,零件尺度下坐标 &gt;100m 或深度 &gt;10m 基本都是把 mm 当 m 写了。
    /// 这是 LLM 最高频的单位错误,值得单独一条。
    /// </summary>
    public sealed class DimensionRule : IFeatureRule
    {
        public string Code { get { return "W101"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            const double coordMax = FeatureValidator.SuspiciousCoordAbs;
            const double depthMax = FeatureValidator.SuspiciousDepthAbs;

            if (s.HasCircle)
            {
                if (Math.Abs(s.CircleX) > coordMax || Math.Abs(s.CircleY) > coordMax || Math.Abs(s.CircleR) > coordMax)
                    yield return ctx.Warn("W101", "circle",
                        "圆心/半径达到 " + GeoUtil.Fmt(Math.Max(Math.Abs(s.CircleX), Math.Abs(s.CircleR))) +
                        " 米,疑似单位用错(应为米,不是毫米)。",
                        new { action = "rescale", field = "circle", from = "mm", to = "m", factor = 0.001 });
            }

            if (s.HasSlot && (Math.Abs(s.SlotX) > coordMax || Math.Abs(s.SlotY) > coordMax ||
                              Math.Abs(s.SlotLength) > coordMax || Math.Abs(s.SlotWidth) > coordMax))
                yield return ctx.Warn("W101", "slot",
                    "slot 中心/尺寸达到 " + GeoUtil.Fmt(Math.Max(Math.Max(Math.Abs(s.SlotX), Math.Abs(s.SlotY)),
                        Math.Max(Math.Abs(s.SlotLength), Math.Abs(s.SlotWidth)))) + " 米,疑似单位用错(应为米,不是毫米)。",
                    new { action = "rescale", field = "slot", from = "mm", to = "m", factor = 0.001 });

            for (int i = 0; i < s.Loops.Count; i++)
            {
                var bb = GeoUtil.BBox(s.Loops[i]);
                double m = Math.Max(Math.Max(Math.Abs(bb[0]), Math.Abs(bb[1])), Math.Max(Math.Abs(bb[2]), Math.Abs(bb[3])));
                if (m > coordMax)
                    yield return ctx.Warn("W101", "loops[" + i + "]",
                        "第 " + (i + 1) + " 个环坐标达到 " + GeoUtil.Fmt(m) + " 米,疑似单位用错(应为米,不是毫米)。",
                        new { action = "rescale", field = "loops[" + i + "]", from = "mm", to = "m", factor = 0.001 });
            }

            if (s.Depth.HasValue && Math.Abs(s.Depth.Value) > depthMax)
                yield return ctx.Warn("W101", "depth",
                    "depth = " + GeoUtil.Fmt(s.Depth.Value) + " 米,疑似单位用错(应为米,不是毫米)。",
                    new { action = "rescale", field = "depth", from = "mm", to = "m", factor = 0.001 });

            if (s.HasDistance && Math.Abs(s.Distance) > depthMax)
                yield return ctx.Warn("W101", "distance",
                    "distance = " + GeoUtil.Fmt(s.Distance) + " 米,疑似单位用错。",
                    new { action = "rescale", field = "distance", from = "mm", to = "m", factor = 0.001 });
        }
    }

    /// <summary>W102:白名单外的字段。多半是拼错(如 "depht"),或用了尚未支持的字段。</summary>
    public sealed class UnknownFieldRule : IFeatureRule
    {
        public string Code { get { return "W102"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            foreach (var f in ctx.Current.UnknownFields)
            {
                yield return ctx.Warn("W102", f,
                    "未知字段 \"" + f + "\",将被忽略(可能是拼写错误)。",
                    new { action = "remove_or_rename", field = f });
            }
        }
    }

    // ==================== 引用层 ====================

    /// <summary>
    /// E201 缺 plane/base · E202 RefPlane_N 索引非法 · W202 索引超出默认面个数 ·
    /// E203 @别名未定义/前向引用 · W204 别名重复定义。
    ///
    /// obj-K 句柄引用【不判】——句柄表在 server 进程里,静态校验看不到,强行判只会误报。
    /// </summary>
    public sealed class PlaneRefRule : IFeatureRule
    {
        public string Code { get { return "E201"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "*" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            bool isPlaneOp = s.OpLower == "plane";

            string ref_ = isPlaneOp ? s.BaseRef : s.PlaneRef;
            string field = isPlaneOp ? "base" : "plane";

            if (!isPlaneOp && s.OpLower != "extrude" && s.OpLower != "cut") yield break;

            if (string.IsNullOrWhiteSpace(ref_))
            {
                yield return ctx.Error("E201", field,
                    "缺少 \"" + field + "\" 平面引用(支持 RefPlane_1/2/3、@别名、obj-K)。",
                    new { action = "provide", field = field });
                yield break;
            }

            if (ref_.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
                yield break;   // 句柄:静态校验不判

            if (ref_.StartsWith("@", StringComparison.Ordinal))
            {
                string key = ref_.Substring(1);
                int defIdx;
                if (!ctx.DefinedPlanes.TryGetValue(key, out defIdx))
                {
                    yield return ctx.Error("E203", field,
                        "未找到本批内命名平面 \"@" + key + "\"(需先用 op=plane 且带 name 创建)。",
                        new { action = "define_plane", name = key, beforeFeature = ctx.Index });
                }
                else if (defIdx >= ctx.Index)
                {
                    yield return ctx.Error("E203", field,
                        "\"@" + key + "\" 是在第 " + defIdx + " 个特征定义的,不能在当前第 " + ctx.Index +
                        " 个特征(前向引用)使用——必须把 op=plane 移到前面。",
                        new { action = "reorder", moveFeature = defIdx, beforeFeature = ctx.Index });
                }
                yield break;
            }

            if (ref_.StartsWith("RefPlane", StringComparison.OrdinalIgnoreCase))
            {
                string idxStr = ref_.Substring("RefPlane".Length).TrimStart('_', ' ', '-');
                int idx;
                if (!int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                {
                    yield return ctx.Error("E202", field,
                        "\"" + ref_ + "\" 解析不出平面索引,应为 RefPlane_1 / RefPlane_2 / RefPlane_3。",
                        new { action = "set", field = field, example = "RefPlane_1" });
                    yield break;
                }
                if (idx < 1)
                {
                    yield return ctx.Error("E202", field,
                        "RefPlane_" + idx + " 索引必须 >= 1(Item 是 1-based)。",
                        new { action = "set", field = field, min = 1 });
                    yield break;
                }
                if (idx > FeatureValidator.DefaultRefPlaneCount)
                {
                    yield return ctx.Warn("W202", field,
                        "RefPlane_" + idx + " 超出默认参考面个数(" + FeatureValidator.DefaultRefPlaneCount +
                        ")。若该面确实是文档里的局部/自建面(如 UI 在实体表面画图自动生成的)可忽略,否则会报\"找不到参考面\"。",
                        new { action = "verify", field = field, defaultPlaneCount = FeatureValidator.DefaultRefPlaneCount });
                }
                yield break;
            }

            // 其它:构建器会按 DisplayName 匹配。中文版是「参考平面_N」,英文名基本匹配不上。
            yield return ctx.Warn("W202", field,
                "\"" + ref_ + "\" 不是 RefPlane_N / @别名 / obj-K。构建器会退化为按 DisplayName 匹配," +
                "中文版 SE 的显示名是「参考平面_N」,通常匹配不上。",
                new { action = "set", field = field, example = "RefPlane_1" });
        }
    }

    // ==================== 几何层 ====================

    /// <summary>
    /// E301 点数不足 · E302 相邻点重合 · E303 自交(报第几条边 × 第几条边) ·
    /// E304 退化面积 · W301 同一特征内环绕向不一致。
    ///
    /// 全部在【平面局部 u/v 二维坐标】下判定——草图坐标不是全局 XYZ,别投影。
    /// </summary>
    public sealed class LoopGeometryRule : IFeatureRule
    {
        public string Code { get { return "E301"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;

            // 圆轮廓:只需校验半径
            if (s.HasCircle)
            {
                if (s.CircleR <= 0)
                    yield return ctx.Error("E305", "circle.radius",
                        "circle 半径必须 > 0(当前 " + GeoUtil.Fmt(s.CircleR) + ");半径 <= 0 会被当成\"没有圆\"而回退走直线环。",
                        new { action = "set", field = "circle.radius", must = "> 0" });
                yield break;
            }

            // 腰孔:length 是含两端半圆的总长,所以必须 >= width(width 就是端头半圆的直径)
            if (s.HasSlot)
            {
                if (s.SlotWidth <= 0)
                    yield return ctx.Error("E305", "slot.width",
                        "slot 的 width 必须 > 0(当前 " + GeoUtil.Fmt(s.SlotWidth) + ")。",
                        new { action = "set", field = "slot.width", must = "> 0" });

                if (s.SlotLength <= 0)
                    yield return ctx.Error("E305", "slot.length",
                        "slot 的 length 必须 > 0(当前 " + GeoUtil.Fmt(s.SlotLength) + ")。",
                        new { action = "set", field = "slot.length", must = "> 0" });
                else if (s.SlotLength < s.SlotWidth)
                    yield return ctx.Error("E306", "slot.length",
                        "slot 的 length(" + GeoUtil.Fmt(s.SlotLength) + ") 必须 >= width(" +
                        GeoUtil.Fmt(s.SlotWidth) + ")——length 是含两端半圆的总长,width 是端头半圆的直径。",
                        new { action = "set", field = "slot.length", must = ">= slot.width" });
                yield break;
            }

            // circle 字段存在但没解析成圆(半径 <= 0)→ 上面已覆盖;这里补一条"给了 circle 却走直线环"的提示
            if (s.Raw.ValueKind == JsonValueKind.Object && s.Raw.TryGetProperty("circle", out _) && !s.HasCircle)
                yield break;

            // --- 原始点列的数量校验(解析器会【静默丢弃】不足 3 点的环,必须在这里补报)---
            foreach (var iss in CheckRawPointCounts(ctx, s))
                yield return iss;

            for (int i = 0; i < s.Loops.Count; i++)
            {
                var pts = s.Loops[i];
                string fld = "loops[" + i + "]";

                if (pts.Length < 3)
                {
                    yield return ctx.Error("E301", fld,
                        "第 " + (i + 1) + " 个环只有 " + pts.Length + " 个点,闭合环至少需要 3 个点。", null);
                    continue;
                }

                // E302 相邻点重合
                for (int k = 0; k < pts.Length; k++)
                {
                    var a = pts[k];
                    var b = pts[(k + 1) % pts.Length];
                    if (GeoUtil.Dist(a, b) < FeatureValidator.CoincidentEpsilon)
                    {
                        yield return ctx.Error("E302", fld,
                            "第 " + (i + 1) + " 个环的第 " + (k + 1) + " 个点与第 " +
                            (((k + 1) % pts.Length) + 1) + " 个点重合(零长度边)。",
                            new { action = "remove_duplicate_point", field = fld, point = k });
                        break;
                    }
                }

                // E303 自交:非相邻边两两求交(相邻边共享端点,不算自交)
                int n = pts.Length;
                for (int a = 0; a < n; a++)
                {
                    for (int b = a + 1; b < n; b++)
                    {
                        bool adjacent = (b == a + 1) || (a == 0 && b == n - 1);
                        if (adjacent) continue;

                        double ix, iy;
                        if (GeoUtil.SegmentIntersect(pts[a], pts[(a + 1) % n], pts[b], pts[(b + 1) % n], out ix, out iy))
                        {
                            yield return ctx.Error("E303", fld,
                                "第 " + (i + 1) + " 个环自交:第 " + (a + 1) + " 条边 × 第 " + (b + 1) +
                                " 条边,交点 (" + GeoUtil.Fmt(ix) + ", " + GeoUtil.Fmt(iy) + ")。",
                                new { action = "fix_self_intersection", field = fld, edgeA = a + 1, edgeB = b + 1 });
                        }
                    }
                }

                // E304 退化面积
                double area = Math.Abs(GeoUtil.SignedArea(pts));
                if (area < FeatureValidator.DegenerateArea)
                {
                    yield return ctx.Error("E304", fld,
                        "第 " + (i + 1) + " 个环面积约 " + area.ToString("E2", CultureInfo.InvariantCulture) +
                        " m²,接近零——所有点共线或轮廓退化。",
                        new { action = "fix_degenerate", field = fld });
                }
            }

            // W301 绕向不一致:同一特征的多个环一会儿顺一会儿逆
            if (s.Loops.Count >= 2)
            {
                bool hasPos = false, hasNeg = false;
                foreach (var pts in s.Loops)
                {
                    if (pts.Length < 3) continue;
                    double sa = GeoUtil.SignedArea(pts);
                    if (sa > 0) hasPos = true;
                    else if (sa < 0) hasNeg = true;
                }
                if (hasPos && hasNeg)
                    yield return ctx.Warn("W301", "loops",
                        "同一特征内的环绕向不一致(既有逆时针也有顺时针)。多孔轮廓建议统一绕向," +
                        "否则 ProfileSide 的语义在各环上不一致。",
                        new { action = "unify_winding", field = "loops" });
            }
        }

        private static IEnumerable<Issue> CheckRawPointCounts(ValidationContext ctx, FeatureSpec s)
        {
            if (s.Raw.ValueKind != JsonValueKind.Object) yield break;

            JsonElement loopsEl;
            if (s.Raw.TryGetProperty("loops", out loopsEl) && loopsEl.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var loopEl in loopsEl.EnumerateArray())
                {
                    var pts = FeatureSpecParser.ParsePointArray(loopEl);
                    if (pts == null || pts.Length < 3)
                    {
                        yield return ctx.Error("E301", "loops[" + i + "]",
                            "loops 第 " + (i + 1) + " 个环只有 " + (pts == null ? 0 : pts.Length) +
                            " 个有效点(需 >= 3),会被解析器静默丢弃——多孔会少切一个孔。", null);
                    }
                    i++;
                }
                yield break;   // 有 loops 时以 loops 为准
            }

            JsonElement el;
            if (s.Raw.TryGetProperty("polygon", out el) && el.ValueKind == JsonValueKind.Array && el.GetArrayLength() < 3)
            {
                yield return ctx.Error("E301", "polygon",
                    "polygon 只有 " + el.GetArrayLength() + " 个点,至少需要 3 个点。", null);
            }

            if (s.Raw.TryGetProperty("rect", out el) && el.ValueKind == JsonValueKind.Array && el.GetArrayLength() < 2)
            {
                yield return ctx.Error("E301", "rect",
                    "rect 需要两个角点 [[x1,y1],[x2,y2]],当前只有 " + el.GetArrayLength() + " 个。", null);
            }
        }
    }

    // ==================== 语义层 ====================

    /// <summary>E401:cut 之前必须已有 extrude(除料得有料可除)。</summary>
    public sealed class CutBeforeExtrudeRule : IFeatureRule
    {
        public string Code { get { return "E401"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            for (int i = 0; i < ctx.Index; i++)
            {
                if (ctx.Specs[i].OpLower == "extrude") yield break;
            }

            yield return ctx.Error("E401", "op",
                "除料前必须先建实体(extrude)——当前特征之前没有任何 extrude。",
                new { action = "insert_extrude_before", beforeFeature = ctx.Index });
        }
    }

    /// <summary>E402:extrude 必须给 depth,且必须 &gt; 0。</summary>
    public sealed class ExtrudeDepthRule : IFeatureRule
    {
        public string Code { get { return "E402"; } }
        public Severity DefaultLevel { get { return Severity.Error; } }
        public string[] AppliesTo { get { return new[] { "extrude" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (!s.Depth.HasValue)
            {
                yield return ctx.Error("E402", "depth",
                    "extrude 必须提供 depth(米)。",
                    new { action = "provide", field = "depth", unit = "m" });
                yield break;
            }
            if (s.Depth.Value <= 0)
            {
                yield return ctx.Error("E402", "depth",
                    "depth 必须 > 0(当前 " + GeoUtil.Fmt(s.Depth.Value) + ")。",
                    new { action = "set", field = "depth", must = "> 0", unit = "m" });
            }
        }
    }

    /// <summary>
    /// E105 side 取值非法(SE 只认 1/2/3) · W402 方向/深度组合可疑。
    /// </summary>
    public sealed class SideDirectionRule : IFeatureRule
    {
        public string Code { get { return "W402"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            int side = s.Side ?? (s.OpLower == "cut" ? 1 : 2);

            if (side < 1 || side > 3)
            {
                yield return ctx.Error("E105", "side",
                    "side = " + side + " 非法。ProfilePlaneSide 只支持 1(igLeft) / 2(igRight) / 3(igBoth)。",
                    new { action = "set", field = "side", allowed = new[] { 1, 2, 3 } });
                yield break;
            }

            if (side == 3 && s.Depth.HasValue && s.Depth.Value > 0)
            {
                yield return ctx.Warn("W402", "side",
                    "side=3 是双向拉伸,实际总长约为 depth 的两倍(" + GeoUtil.Fmt(s.Depth.Value * 2) +
                    " 米)。若本意是单侧这个深度,改用 side=1 或 2。",
                    new { action = "set", field = "side", suggested = 2 });
            }

            // finite 除料缺 depth 已由 CutModeRule 以 E404(error) 覆盖——静默套 0.2m 是
            // 实打实的坑,该拦下而不是提醒,这里不再重复报 W402。
        }
    }

    /// <summary>
    /// W401:同一平面上出现两次及以上 cut 却没有合并成一个 loops —— **僵尸特征头号杀手**。
    /// 实测:同一模型第 2 个及以后的 AddThroughNext 必然 Status=1216476311(几何未生成),
    /// 与 mode=next/all/finite 无关。多孔必须画进同一个 Profile 的多个闭合环,一次切完。
    /// </summary>
    public sealed class ConsecutiveCutRule : IFeatureRule
    {
        public string Code { get { return "W401"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var s = ctx.Current;
            if (string.IsNullOrWhiteSpace(s.PlaneRef)) yield break;

            for (int i = 0; i < ctx.Index; i++)
            {
                var prev = ctx.Specs[i];
                if (prev.OpLower != "cut") continue;
                if (!string.Equals(prev.PlaneRef, s.PlaneRef, StringComparison.OrdinalIgnoreCase)) continue;

                yield return ctx.Warn("W401", "plane",
                    "第 " + i + " 个特征已经在同一平面 \"" + s.PlaneRef + "\" 上除过料了。" +
                    "同一模型上的第 2 次 AddThroughNext 必然生成僵尸特征(Status=1216476311)," +
                    "请把这些孔合并进【同一个】cut 的 loops 里一次切完。",
                    new
                    {
                        action = "merge_into_previous",
                        target = i,
                        field = "loops",
                        hint = "把本特征的环追加到目标特征的 loops 数组,再删掉本特征"
                    });
                yield break;
            }
        }
    }

    /// <summary>
    /// W403:除料轮廓落在毛坯之外(第五大坑:圆心/半径必须落在实心墙内,否则 6311)。
    /// 只在 plane 能解析成默认面(或基于默认面偏移的 @别名)时才判——
    /// obj-K 与无法解析的面没有确定的 u/v→全局映射,一律跳过(out of scope,不误报)。
    /// </summary>
    public sealed class CutOutsideStockRule : IFeatureRule
    {
        public string Code { get { return "W403"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var stock = StockEstimator.Estimate(ctx.Specs);
            if (stock == null) yield break;   // 毛坯无法估算(没有可解析的 extrude)→ 不判

            var s = ctx.Current;
            int planeIdx;
            double offset;
            if (!PlaneResolver.TryResolve(ctx.Specs, ctx.Index, s.PlaneRef, out planeIdx, out offset))
                yield break;

            double depth = s.Depth ?? 0;
            bool depthKnown = s.Depth.HasValue;

            var bb = StockEstimator.FeatureBBox(ctx.Specs, ctx.Index, stock);
            if (bb == null) yield break;

            if (!StockEstimator.Intersects(bb, stock))
            {
                yield return ctx.Warn("W403", "plane",
                    "除料轮廓落在毛坯之外(毛坯包围盒 [" + StockEstimator.FmtBox(stock) +
                    "],除料 [" + StockEstimator.FmtBox(bb) + "])。除不到任何材料 → 必然 6311 僵尸。" +
                    "注意草图坐标是【所在平面的局部 u/v】,不是全局 XYZ。",
                    new { action = "move_inside_stock", field = "loops", stockBox = stock, cutBox = bb });
            }

            // finite 且深度已知:深度方向也要够得着
            if (depthKnown && string.Equals(s.Mode, "finite", StringComparison.OrdinalIgnoreCase) && depth <= 0)
            {
                yield return ctx.Warn("W403", "depth",
                    "mode=finite 的 depth = " + GeoUtil.Fmt(depth) + ",切不到任何材料。",
                    new { action = "set", field = "depth", must = "> 0" });
            }
        }
    }

    /// <summary>W404:同一特征内的环相互重叠。重叠轮廓会让 Profile 闭合关系含糊,极易出僵尸。</summary>
    public sealed class LoopOverlapRule : IFeatureRule
    {
        public string Code { get { return "W404"; } }
        public Severity DefaultLevel { get { return Severity.Warning; } }
        public string[] AppliesTo { get { return new[] { "extrude", "cut" }; } }

        public IEnumerable<Issue> Check(ValidationContext ctx)
        {
            var loops = ctx.Current.Loops;
            for (int i = 0; i < loops.Count; i++)
            {
                for (int j = i + 1; j < loops.Count; j++)
                {
                    var bi = GeoUtil.BBox(loops[i]);
                    var bj = GeoUtil.BBox(loops[j]);
                    if (!GeoUtil.BBoxOverlap(bi, bj)) continue;

                    bool hit = false;
                    foreach (var p in loops[i])
                        if (GeoUtil.PointInPolygon(p, loops[j])) { hit = true; break; }
                    if (!hit)
                        foreach (var p in loops[j])
                            if (GeoUtil.PointInPolygon(p, loops[i])) { hit = true; break; }

                    if (hit)
                    {
                        yield return ctx.Warn("W404", "loops[" + i + "]",
                            "第 " + (i + 1) + " 个环与第 " + (j + 1) + " 个环重叠。",
                            new { action = "separate_loops", loopA = i, loopB = j });
                    }
                }
            }
        }
    }

    // ==================== 毛坯估算 / 平面解析(供 W403 用) ====================

    /// <summary>
    /// 默认参考面的 u/v → 全局轴映射(2026-09-13/15 Convert3DCoordinate 坐标级探针拍板;
    /// 2026-08-22 目检实测已作废——当时把 RefPlane_2/3 记反并在此固化过,订正时须同步排查代码引用):
    ///   RefPlane_1 = XY 面:u→X, v→Y, 法向轴 Z
    ///   RefPlane_2 = YZ 面:u→Y, v→Z, 法向轴 X
    ///   RefPlane_3 = XZ 面:u→X, v→Z, 法向轴 Y
    /// 只在默认面上做估算;@别名若最终能追到默认面,则按 base + distance 叠加偏移。
    /// </summary>
    public static class PlaneResolver
    {
        /// <summary>解析出"底层默认面索引(1..3)"与沿其法向的偏移(米)。</summary>
        public static bool TryResolve(List<FeatureSpec> specs, int beforeIndex, string planeRef,
            out int planeIdx, out double offset)
        {
            planeIdx = 0;
            offset = 0;
            if (string.IsNullOrWhiteSpace(planeRef)) return false;
            return Resolve(specs, beforeIndex, planeRef, 0, out planeIdx, out offset);
        }

        private static bool Resolve(List<FeatureSpec> specs, int beforeIndex, string planeRef, int depth,
            out int planeIdx, out double offset)
        {
            planeIdx = 0;
            offset = 0;
            if (depth > 8) return false;   // 防循环引用

            if (planeRef.StartsWith("RefPlane", StringComparison.OrdinalIgnoreCase))
            {
                string idxStr = planeRef.Substring("RefPlane".Length).TrimStart('_', ' ', '-');
                int idx;
                if (!int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return false;
                if (idx < 1 || idx > FeatureValidator.DefaultRefPlaneCount) return false;
                planeIdx = idx;
                offset = 0;
                return true;
            }

            if (planeRef.StartsWith("@", StringComparison.Ordinal))
            {
                string key = planeRef.Substring(1);
                for (int i = beforeIndex - 1; i >= 0; i--)
                {
                    var s = specs[i];
                    if (s.OpLower != "plane" || string.IsNullOrEmpty(s.Name)) continue;
                    if (!string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase)) continue;

                    int baseIdx;
                    double baseOffset;
                    if (!Resolve(specs, i, s.BaseRef, depth + 1, out baseIdx, out baseOffset)) return false;
                    planeIdx = baseIdx;
                    offset = baseOffset + s.Distance;
                    return true;
                }
                return false;
            }

            return false;   // obj-K 或其它:没有确定的全局映射,不判
        }

        /// <summary>取平面的 [u轴, v轴, 法向轴](0=X, 1=Y, 2=Z)。</summary>
        public static int[] Axes(int planeIdx)
        {
            switch (planeIdx)
            {
                case 1: return new[] { 0, 1, 2 };   // XY,法向 Z
                case 2: return new[] { 1, 2, 0 };   // YZ,法向 X(2026-09-13 探针拍板,旧记 XZ 已作废)
                case 3: return new[] { 0, 2, 1 };   // XZ,法向 Y(2026-09-13 探针拍板,旧记 YZ 已作废)
                default: return null;
            }
        }
    }

    /// <summary>毛坯包围盒估算:把所有可解析的 extrude 的"草图包围盒 × 深度"并起来。</summary>
    public static class StockEstimator
    {
        /// <summary>返回 [xmin,ymin,zmin,xmax,ymax,zmax],无法估算时返回 null。</summary>
        public static double[] Estimate(List<FeatureSpec> specs)
        {
            double[] stock = null;
            for (int i = 0; i < specs.Count; i++)
            {
                var s = specs[i];
                if (s.OpLower != "extrude") continue;
                if (!s.Depth.HasValue || s.Depth.Value <= 0) continue;

                var bb = FeatureBBox(specs, i, null);
                if (bb == null) continue;
                stock = stock == null ? bb : Union(stock, bb);
            }
            return stock;
        }

        /// <summary>
        /// 单个特征的世界包围盒。
        /// stock 传 null 表示"正在算毛坯自身";非 null 时,若深度未知(cut 的 next/all)
        /// 则沿法向轴借用毛坯的厚度。
        /// </summary>
        public static double[] FeatureBBox(List<FeatureSpec> specs, int index, double[] stock)
        {
            var s = specs[index];
            int planeIdx;
            double offset;
            if (!PlaneResolver.TryResolve(specs, index, s.PlaneRef, out planeIdx, out offset)) return null;

            var axes = PlaneResolver.Axes(planeIdx);
            if (axes == null) return null;
            int ua = axes[0], va = axes[1], na = axes[2];

            // 草图在 u/v 上的范围
            double[] local;
            if (s.HasCircle)
                local = new[] { s.CircleX - s.CircleR, s.CircleY - s.CircleR, s.CircleX + s.CircleR, s.CircleY + s.CircleR };
            else if (s.HasSlot)
            {
                // 旋转矩形的轴对齐包围盒:长轴半长与短轴半长各自在 u/v 上的投影之和
                double dx = Math.Cos(s.SlotAngle), dy = Math.Sin(s.SlotAngle);
                double eu = Math.Abs(dx) * s.SlotLength / 2.0 + Math.Abs(-dy) * s.SlotWidth / 2.0;
                double ev = Math.Abs(dy) * s.SlotLength / 2.0 + Math.Abs(dx) * s.SlotWidth / 2.0;
                local = new[] { s.SlotX - eu, s.SlotY - ev, s.SlotX + eu, s.SlotY + ev };
            }
            else if (s.Loops.Count > 0)
            {
                local = GeoUtil.BBox(s.Loops[0]);
                for (int k = 1; k < s.Loops.Count; k++)
                {
                    var b = GeoUtil.BBox(s.Loops[k]);
                    local[0] = Math.Min(local[0], b[0]);
                    local[1] = Math.Min(local[1], b[1]);
                    local[2] = Math.Max(local[2], b[2]);
                    local[3] = Math.Max(local[3], b[3]);
                }
            }
            else return null;

            var box = new double[6];
            box[ua] = local[0]; box[ua + 3] = local[2];
            box[va] = local[1]; box[va + 3] = local[3];

            int side = s.Side ?? (s.OpLower == "cut" ? 1 : 2);
            double d = s.Depth ?? 0;

            if (s.Depth.HasValue)
            {
                if (side == 2) { box[na] = offset; box[na + 3] = offset + d; }
                else if (side == 1) { box[na] = offset - d; box[na + 3] = offset; }
                else { box[na] = offset - d; box[na + 3] = offset + d; }
            }
            else if (stock != null)
            {
                // next / all:沿法向穿过去,借用毛坯在该轴上的范围
                box[na] = stock[na];
                box[na + 3] = stock[na + 3];
            }
            else
            {
                return null;
            }

            return box;
        }

        public static double[] Union(double[] a, double[] b)
        {
            return new[]
            {
                Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Min(a[2], b[2]),
                Math.Max(a[3], b[3]), Math.Max(a[4], b[4]), Math.Max(a[5], b[5])
            };
        }

        public static bool Intersects(double[] a, double[] b)
        {
            for (int i = 0; i < 3; i++)
            {
                if (a[i + 3] < b[i] || b[i + 3] < a[i]) return false;
            }
            return true;
        }

        public static string FmtBox(double[] b)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(GeoUtil.Fmt(b[i]));
            }
            return sb.ToString();
        }
    }
}

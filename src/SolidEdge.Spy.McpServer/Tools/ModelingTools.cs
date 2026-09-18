using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools
{
    /// <summary>
    /// 高级声明式建模工具:把"内嵌草图 + 端点闭合约束 + End + 拉伸/除料"整套已验证配方
    /// 封装成服务器端代码,AI 只需给特征描述,不再逐条拼 15~17 步 COM 原语 chain。
    ///
    /// 支持的操作(op):
    ///   plane   创建局部参考面(平行偏移),返回句柄并可命名复用
    ///   extrude 拉伸凸台(第一个特征走 Models.AddFiniteExtrudedProtrusion,后续走
    ///           Model.ExtrudedProtrusions.AddFinite,自动并入同一实体)
    ///   cut     除料挖孔 / 铣槽(Model.ExtrudedCutouts.AddThroughNext/AddFinite 等)
    ///           ★ 方向由两个参数共同决定(2026-09-10 实测,踩过坑):
    ///             profileside = ProfileSide,决定"切除轮廓【内侧还是外侧】的料",默认 1(=官方示例 igLeft);
    ///             side        = ProfilePlaneSide,决定"相对草图平面往哪一侧延伸"。
    ///             两者都只有 1/2 两值;调用方【没显式给】的维度,op 会自动按组合重试到几何正常为止。
    ///   revolve 旋转凸台(截面 + 独立旋转轴 → Model.RevolvedProtrusions.AddFinite;
    ///           轴是草图平面内的一条线,不参与截面闭环;建完自动隐藏草图);
    ///           mode:"cut" 走旋转切割 RevolvedCutouts.AddFinite(在已有实体上切除)
    ///
    /// 草图形状:circle 圆({center,radius} 或 [cx,cy,r];圆轮廓拉伸即真圆柱,无需旋转)/
    ///           circles 多真圆([[x,y,r],...],一个轮廓多环,一次切/拉多个真圆孔)/
    ///           rect 两角点矩形 / polygon 显式多边形点列(自动闭合)/ loops 多环。
    /// 所有内嵌轮廓自动加端点重合约束(Profile.Form==2)并设 Visible=False(创建即隐藏,
    /// 无需再跑 Python 隐藏脚本)。
    /// </summary>
    [McpServerToolType]
    public static class ModelingTools
    {
        /// <summary>特征 Status 正常值。不等于它 = 几何没生成或状态异常。</summary>
        private const long StatusOk = 1216476310;

        /// <summary>Status=1216476311:COM 调用"成功"但几何没生成——僵尸特征。</summary>
        private const long StatusZombie = 1216476311;

        /// <summary>
        /// 批量声明式建模:一次 MCP 调用完成整栋房子/整套模型的特征创建。
        /// 每个特征是一个 JSON 对象:
        ///   {"op":"plane","name":"front","base":"RefPlane_3","distance":-0.1}      // 建局部参考面
        ///   {"op":"extrude","name":"wall","plane":"RefPlane_1",
        ///    "rect":[[-0.15,-0.1],[0.15,0.1]],"side":2,"depth":0.2}               // 矩形拉伸
        ///   {"op":"extrude","name":"roof","plane":"RefPlane_3",
        ///    "polygon":[[-0.15,0.2],[0.15,0.2],[0,0.3]],"side":3,"depth":0.2}      // 多边形拉伸
        ///   {"op":"cut","name":"door","plane":"@front",
        ///    "rect":[[-0.03,0],[0.03,0.09]]}                                       // 矩形除料
        ///
        /// 字段说明:
        ///   plane:  "RefPlane_1/2/3"(按名称找默认面) / "@别名"(本批前面 plane 建的面) / "obj-K"(句柄表对象)
        ///   side:     ProfilePlaneSide(延伸方向)。extrude 默认 2;cut 默认 1
        ///   profileside: ProfileSide(切除轮廓【内/外】侧)。extrude 默认 1;cut 默认 1(=官方示例 igLeft)
        ///   ★ cut 的"切哪一侧" = side + profileside 共同决定;调用方【没显式给】的维度,
        ///     op 会自动按组合重试直到几何正常(只能纠正"切到实体外"的僵尸;合法但镜像的判不出来)
        ///   visible:  可选 bool,false=自动隐藏草图(默认),true=保留显示
        /// 返回每个特征的名称/Status(1216476310=正常,1216476311=几何未生成)/面数/句柄。
        /// 坐标单位为米。
        /// </summary>
        [McpServerTool, Description("高级声明式建模:一次调用创建多个特征(拉伸/除料/旋转/局部参考面)。" +
            "features 每项 {op, name?, plane|base, 形状, side, profileside, depth, axis?, angle?|degrees?, visible?}。" +
            "op=revolve 旋转凸台(默认)或旋转切割(mode:\"cut\"走 RevolvedCutout):截面用 rect/polygon/loops,必须给 axis 旋转轴两点(草图平面局部 u/v," +
            "如 \"axis\":[[0,0],[0,0.05]] 沿局部 v 轴);angle 弧度(默认 2π 整圈)或 degrees 度。" +
            "轴是独立构造线、不参与截面闭环;建完自动隐藏草图(COM 建特征不会自动隐藏)。" +
            "★ 每个特征建完【自动校验】Status:不等于正常值即判失败并自动删除该特征回滚," +
            "所以返回结果里 status=ok 就一定是健康的,你无需再检查 1216476310/1216476311 这类状态码;" +
            "任一特征失败即停止后续(避免连环僵尸),按该条 diagnosis 修正后重跑。" +
            "形状六选一:circle 圆(写 {\"center\":[x,y],\"radius\":r} 或简写 [x,y,r];圆轮廓拉伸=真圆柱,面数 3)、" +
            "circles 多真圆(写 [[x,y,r],[x,y,r],...],一个轮廓多环、一次切出多个真圆孔,如法兰螺栓孔阵列)、" +
            "slot 腰孔/长圆孔({\"center\":[x,y],\"length\":总长,\"width\":宽,\"angle\":弧度?} 或简写 [x,y,长,宽];" +
            "【真圆弧】构造,两端是半圆不是折线)、" +
            "rect 两角点矩形、polygon 多边形点列、loops 多环。plane 支持 RefPlane_1/2/3、@别名(前面 plane op 建的)、obj-K。" +
            "内部自动完成 画轮廓→端点闭合约束(仅直线需要/圆不需要)→End(0)→可见性隐藏→AddThroughNext/AddFinite 全链路," +
            "cut 方向由 side(ProfilePlaneSide=延伸方向) 与 profileside(ProfileSide=切轮廓内/外侧,默认 1) 共同决定:" +
            "调用方没显式给的维度,op 会自动按组合重试到几何正常;替代手工拼 se_invoke_chain 长链。示例见 ModelingTools.cs 类注释。" +
            "★ 执行前会先跑静态校验(见 se_validate_features):有 error 时直接拒绝执行并返回 issues;" +
            "dryRun=true 则只返回校验报告、不建任何特征。建议先跑一次校验再建。")]
        public static string se_model_build(
            SolidEdgeContext context,
            [Description("特征列表(JSON 数组),每项见工具描述")] JsonElement[] features,
            [Description("起始对象句柄(零件文档),可省略;省略时用当前活动文档")] string objectId = null,
            [Description("true=只做静态校验并返回报告,不建任何特征(不启动 COM);默认 false")] bool dryRun = false)
        {
            try
            {
                // 灌进 Solid Edge 之前先静态校验:纯函数、毫秒级、不碰 COM。
                // 僵尸特征(调用成功但几何未生成)事后才发现的成本,远高于事前拦下。
                var report = FeatureValidator.Validate(features);

                if (dryRun)
                    return JsonSerializer.Serialize(new { status = "dry-run", validate = report },
                        FeatureValidator.JsonOpts);

                if (report.HasError)
                    return JsonSerializer.Serialize(new
                    {
                        status = "error",
                        message = "静态校验未通过(" + report.errorCount + " 个 error),已拒绝执行," +
                                  "以免生成僵尸特征。请按 issues 修正后重试;只想看报告可用 se_validate_features。",
                        validate = report
                    }, FeatureValidator.JsonOpts);

                return context.Invoke(() =>
                {
                    object doc;
                    if (!string.IsNullOrEmpty(objectId))
                    {
                        var h = context.GetHandle(objectId);
                        if (h == null || h.ComObject == null)
                            return Error("找不到起始对象编号 " + objectId + "。请先调用 se_get_selection,或省略 objectId 使用活动文档。");
                        doc = h.ComObject;
                    }
                    else
                    {
                        var app = context.GetApplication();
                        doc = app.ActiveDocument;
                        if (doc == null)
                            return Error("没有活动文档。请打开一个零件文档,或提供 objectId。");
                    }

                    if (features == null || features.Length == 0)
                        return Error("features 不能为空。");

                    // 本批内命名局部参考面:name → RefPlane COM 对象
                    var namedPlanes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    var results = new List<object>();

                    // 统一走 FeatureSpec 解析:构建器与静态校验器(FeatureValidator)共用同一份取数语义,
                    // 避免"校验说没问题、构建却是另一套解释"。
                    var specs = FeatureSpecParser.ParseAll(features);

                    bool aborted = false;

                    for (int i = 0; i < specs.Count; i++)
                    {
                        var spec = specs[i];
                        string op = spec.Op;
                        string name = spec.Name;

                        object result;
                        try
                        {
                            switch (spec.OpLower)
                            {
                                case "plane":
                                    result = CreatePlaneOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "extrude":
                                    result = ExtrudeOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "cut":
                                    result = CutOp(context, doc, spec, name, namedPlanes);
                                    break;
                                case "revolve":
                                    result = RevolveOp(context, doc, spec, name, namedPlanes);
                                    break;
                                default:
                                    result = new { op = op, name = name, status = "error", message = "未知 op(仅支持 plane/extrude/cut/revolve)。" };
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            result = new { op = op, name = name, status = "error", message = DescribeException(ex) };
                        }

                        results.Add(result);

                        // 只要有一个特征没建成(参数错 / 建出来是僵尸且已回滚)就停:
                        // 后面的特征多半依赖前面的实体,硬着头皮继续只会连环失败、留一树僵尸。
                        if (!IsOk(result))
                        {
                            aborted = true;
                            break;
                        }
                    }

                    return JsonSerializer.Serialize(new
                    {
                        status = aborted ? "error" : "ok",
                        featureCount = results.Count,
                        aborted = aborted,
                        message = aborted
                            ? "第 " + results.Count + " 个特征未建成(失败特征已自动回滚删除),后续特征已停止执行," +
                              "以免连环失败。请按该条的 diagnosis 修正后重跑。"
                            : null,
                        results = results
                    });
                });
            }
            catch (Exception ex)
            {
                return Error("se_model_build 失败: " + DescribeException(ex));
            }
        }

        /// <summary>
        /// features JSON 的静态校验:【纯函数,不启动 COM、不碰文档】,SE 没启动也能跑。
        /// 与构建器共用 FeatureSpecParser 的解析结果,所以"校验通过"就等于"构建器会这么解释它"。
        /// 目的:把僵尸特征(调用成功但 Status=1216476311、几何没生成)的发现时点从事后提到事前。
        /// </summary>
        [McpServerTool, Description("声明式建模的静态校验:不建任何特征、不碰 Solid Edge,毫秒级检查 features JSON。" +
            "拦四类问题——①结构:未知 op / 缺必填 / 字段类型错 / NaN / 单位疑似把 mm 当 m;" +
            "②引用:plane 缺失 / RefPlane_N 越界 / @别名未定义或前向引用 / 别名重复;" +
            "③几何:环点数不足 / 相邻点重合 / 自交(报第几条边×第几条边)/ 退化面积 / 绕向不一致;" +
            "④语义:除料前没有拉伸 / extrude 缺 depth / 同一平面多次 cut 未合并(必然僵尸)/ 除料落在毛坯外 / 环重叠。" +
            "返回 status=ok|warning|error 与 issues 列表,每条带 code / level / feature 序号 / field / 中文 message / 结构化 fix。" +
            "有 error 时 se_model_build 会直接拒绝执行。SE 未启动也能调用,适合在动手前先跑一遍。")]
        public static string se_validate_features(
            SolidEdgeContext context,
            [Description("特征列表(JSON 数组),与 se_model_build 的 features 同格式")] JsonElement[] features)
        {
            try
            {
                return FeatureValidator.Validate(features).ToJson();
            }
            catch (Exception ex)
            {
                return Error("se_validate_features 失败: " + DescribeException(ex));
            }
        }

        /// <summary>
        /// 按面 ID 定位到一个面,并直接在该实体的面上做拉伸(凸台)——不新建参考平面。
        /// 流程:遍历 Model.Body.Shells.Faces 找 Face.ID==faceId 的面 →
        ///   QI 到强类型 Face → ProfileSets.Add().Profiles.Add(face)(直接在面上建轮廓) →
        ///   画闭合轮廓(rect/polygon) → Model.ExtrudedProtrusions.AddFinite 拉伸。
        /// 轮廓坐标是"该面局部坐标系"(平面 u/v),非全局 XYZ。
        /// 注:Profiles.Add(pRefPlaneDisp) 签名虽叫 refplane,但强类型 Face 可直接传入
        /// (Solid Edge 内部识别平面面),无需 AddParallelByDistance 建参考平面特征。
        /// </summary>
        [McpServerTool, Description("按面 ID 定位面并直接在该实体面上做拉伸凸台(不新建参考平面)。" +
            "输入 faceId(Face.ID,整数)、轮廓 rect(两角点)/polygon(≥3点,单位米,为该面局部坐标系 u/v)、" +
            "depth(拉伸深度米)、side(方向,默认2)、profileside(默认1)。内部自动:找面→直接在面上建轮廓→拉伸。" +
            "返回新特征名称/Status(1216476310=正常,1216476311=几何未生成)/面数。")]
        public static string se_extrude_on_face(
            SolidEdgeContext context,
            [Description("目标面的 Face.ID(整数,如 73)")] int faceId,
            [Description("草图形状(JSON):{\"rect\":[[u1,v1],[u2,v2]]} 或 {\"polygon\":[[u,v]...]}")] string shape,
            [Description("拉伸深度(米)")] double depth,
            [Description("起始对象句柄(零件文档),可省略;省略时用当前活动文档")] string objectId = null,
            [Description("ProfilePlaneSide 拉伸方向,默认 2(凸台向实体外)")] int side = 2,
            [Description("ProfileSide,默认 1")] int profileside = 1)
        {
            try
            {
                return context.Invoke(() =>
                {
                    object doc;
                    if (!string.IsNullOrEmpty(objectId))
                    {
                        var h = context.GetHandle(objectId);
                        if (h == null || h.ComObject == null)
                            return Error("找不到起始对象编号 " + objectId + "。");
                        doc = h.ComObject;
                    }
                    else
                    {
                        var app = context.GetApplication();
                        doc = app.ActiveDocument;
                        if (doc == null) return Error("没有活动文档。");
                    }

                    // 1) 遍历 Faces 找 Face.ID==faceId
                    object models = Get(doc, "Models");
                    if (Count(models) == 0) return Error("没有模型实体。");
                    object model = Get(models, "Item", 1);
                    object body = Get(model, "Body");
                    object shells = Get(body, "Shells");
                    if (Count(shells) == 0) return Error("没有外壳(Shell)。");
                    object shell = Get(shells, "Item", 1);
                    object faces = Get(shell, "Faces");
                    int faceCount = Count(faces);
                    object targetFace = null;
                    for (int i = 1; i <= faceCount; i++)
                    {
                        object f = Get(faces, "Item", i);
                        int id = SafeInt(Get(f, "ID"));
                        if (id == faceId) { targetFace = f; break; }
                    }
                    if (targetFace == null)
                        return Error("未找到 Face.ID=" + faceId + " 的面(当前共 " + faceCount + " 个面)。");

                    // 2) QI 到强类型 Face 接口(Profiles.Add 需要强类型才能识别为平面面)
                    var iFace = (SolidEdgeGeometry.Face)targetFace;

                    // 3) 解析轮廓
                    JsonElement feat = JsonDocument.Parse(shape).RootElement;
                    List<double[][]> loops = FeatureSpecParser.ParseLoops(feat);

                    // 4) 直接在面上建闭合轮廓(不新建参考平面)+ 拉伸
                    object profile = CreateProfileOnFace(doc, iFace, loops);
                    object extrudes = Get(model, "ExtrudedProtrusions");
                    object featObj = Call(extrudes, "AddFinite", new object[] { profile, profileside, side, depth });

                    return JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        faceId = faceId,
                        feature = SafeString(Get(featObj, "Name")),
                        featureStatus = SafeLong(Get(featObj, "Status")),
                        faces = FacesCount(featObj),
                        handle = context.AddHandle(featObj, "ExtrudedProtrusion", SafeString(Get(featObj, "Name"))),
                        hint = "Status=1216476310 正常;1216476311=几何未生成(僵尸),需检查草图是否落在实体/方向。"
                    });
                });
            }
            catch (Exception ex)
            {
                return Error("se_extrude_on_face 失败: " + DescribeException(ex));
            }
        }

        /// <summary>
        /// 直接在实体的面上建草图轮廓(不新建独立参考平面):用 Sketches.AddByPlanarFace(强类型 Face)
        /// 在面上直接建 Sketch,取其 Profile 画闭合轮廓→End。Solid Edge 交互"选面拉伸"即此内部机制,
        /// 生成的平面是隐藏的基于面关联平面(Type=732824896),不出现独立基准面特征。
        /// </summary>
        private static object CreateProfileOnFace(object doc, SolidEdgeGeometry.Face face, List<double[][]> loops)
        {
            object sketches = Get(doc, "Sketches");
            var iSketches = (SolidEdgePart.Sketchs)sketches;   // interop 类型名是 Sketchs(注意拼写)
            object sketch;
            try
            {
                sketch = iSketches.AddByPlanarFace(face);   // 优先:在面上直接建草图
            }
            catch
            {
                sketch = iSketches.AddByPlane(face);        // 回退:AddByPlane 可能也接受面
            }
            object profile = Get(sketch, "Profile");

            object lines = Get(profile, "Lines2d");
            object relations = Get(profile, "Relations2d");

            foreach (var pts in loops)
            {
                int n = pts.Length;
                var lineObjs = new object[n];
                for (int i = 0; i < n; i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % n];
                    lineObjs[i] = Call(lines, "AddBy2Points", new object[] { p0[0], p0[1], p1[0], p1[1] });
                }
                for (int i = 0; i < n; i++)
                {
                    Call(relations, "AddKeypoint", new object[] { lineObjs[i], 1, lineObjs[(i + 1) % n], 0 });
                }
            }

            Call(profile, "End", new object[] { 0 });
            SetVisible(profile, false);
            return profile;
        }

        // ---------------- op 实现 ----------------

        private static object CreatePlaneOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            object basePlane = ResolvePlane(context, doc, spec.BaseRef, namedPlanes);
            double distance = spec.Distance;

            object refPlanes = Get(doc, "RefPlanes");
            object plane = Call(refPlanes, "AddParallelByDistance",
                new object[] { basePlane, distance, 2, 0, 1, false, false });

            SetVisible(plane, spec.Visible ?? false);

            string handleId = context.AddHandle(plane, "RefPlane", SafeString(Get(plane, "DisplayName")) ?? "(局部面)");
            if (!string.IsNullOrEmpty(name)) namedPlanes[name] = plane;

            return new
            {
                op = "plane",
                name = name,
                status = "ok",
                displayName = SafeString(Get(plane, "DisplayName")),
                distance = distance,
                handle = handleId,
                hint = "后续特征可用 \"@name\" 引用此面。"
            };
        }

        private static object ExtrudeOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            bool visible = spec.Visible ?? false;

            var specWarnings = new List<string>();
            object profile = CreateProfileForFeature(context, doc, plane, spec, visible, specWarnings);
            int side = spec.Side ?? 2;                           // ProfilePlaneSide
            int profileSide = spec.ProfileSide ?? 1;             // ProfileSide
            double depth = spec.Depth ?? double.NaN;
            if (double.IsNaN(depth))
                return new { op = "extrude", name = name, status = "error", message = "extrude 必须提供 depth(米)。" };

            object models = Get(doc, "Models");
            int modelCount = Count(models);

            object featObj;
            if (modelCount == 0)
            {
                // 第一个特征:新建实体;AddFiniteExtrudedProtrusion 的返回值就是新 Model
                // (2026-09-13 实测:此处立刻回读 models.Item(1) 可能返回 null → NRE,必须用返回值)
                // 通道必须走 Call(ManualInvoke):裸 InvokeMember(binder 直传数组)对
                // SAFEARRAY(DISPATCH) 的 ProfileArray 必报 TYPEMISMATCH(0x80020005,
                // 2026-09-17 三通道对照实测;extrude_rect_profile 配方走 chain 同通道
                // ~90 次验证成功)。本方法枚举参数
                // (planeSide) 用 int 传不触发 VT_USERDEFINED 静默 null——那是 Revolve 的
                // RefAxis 才有的问题,所以这里不能照搬 RevolveOp 的 PIA 方案(PIA 直调实测
                // 返回僵尸 6311,同日实测)。
                object model = Call(models, "AddFiniteExtrudedProtrusion",
                    new object[] { 1, new object[] { profile }, side, depth });
                object extrudes = Get(model, "ExtrudedProtrusions");
                featObj = Get(extrudes, "Item", 1);
            }
            else
            {
                object model = Get(models, "Item", 1);
                object extrudes = Get(model, "ExtrudedProtrusions");
                featObj = Call(extrudes, "AddFinite", new object[] { profile, profileSide, side, depth });
            }

            return FeatureResult("extrude", name, featObj, context, profile, null, specWarnings);
        }

        private static object CutOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            bool visible = spec.Visible ?? false;

            // 多孔(门+多个窗)必须画进【同一个轮廓】的多个闭合环,一次 AddThroughNext 全切;
            // 分开多次 AddThroughNext 时,第二个及以后的除料会 6311 僵尸(实测)。
            int profileSide = spec.ProfileSide ?? 1;             // ProfileSide:1=切除轮廓【内侧】的料(官方示例 igLeft)
            int planeSide = spec.Side ?? 1;                      // ProfilePlaneSide:相对草图平面的延伸方向
            string mode = spec.Mode ?? "next";

            object models = Get(doc, "Models");
            if (Count(models) == 0)
                return new { op = "cut", name = name, status = "error", message = "除料前必须先建实体(extrude)。" };

            object model = Get(models, "Item", 1);
            object cutouts = Get(model, "ExtrudedCutouts");

            // ★ 方向自愈(2026-09-10 实测): cut "切哪一侧" 由 ProfileSide 和 ProfilePlaneSide 共同决定,
            //   任一个取反都可能把料切到实体外 → 僵尸 6311。这里对【调用方没显式给】的维度做组合尝试,
            //   取第一个几何正常的结果。两个都显式给了 = 完全听调用方的,不再猜。
            //   注意:只能自愈"切到实体外(僵尸)";若两个方向都能切到料(合法但镜像),本层判不出来,仍需人工看渲染。
            bool psFree = !spec.ProfileSide.HasValue;
            bool ppsFree = !spec.Side.HasValue;
            int[] psList = psFree ? new[] { profileSide, profileSide == 1 ? 2 : 1 } : new[] { profileSide };
            int[] ppsList = ppsFree ? new[] { planeSide, planeSide == 1 ? 2 : 1 } : new[] { planeSide };

            object profile = null;
            object featObj = null;
            bool built = false;
            var specWarnings = new List<string>();
            foreach (int ps in psList)
            {
                foreach (int pps in ppsList)
                {
                    profile = CreateProfileForFeature(context, doc, plane, spec, visible, specWarnings);
                    featObj = AddCutout(cutouts, profile, ps, pps, mode, spec);

                    long? st = SafeLong(Get(featObj, "Status"));
                    if (!st.HasValue || st.Value != StatusZombie) { built = true; break; }

                    // 这一组方向切到实体外了:删掉特征和它的草图,换下一组重试。
                    object oldSet = TryGetProfileSet(profile);
                    TryDelete(featObj);
                    if (oldSet != null) TryDelete(oldSet);
                }
                if (built) break;
            }

            return FeatureResult("cut", name, featObj, context, profile, null, specWarnings);
        }

        /// <summary>按 mode 调对应的除料 API(切穿所有 / 定深 / 切到下一面),供 cut 首次尝试与方向翻转重试共用。</summary>
        private static object AddCutout(object cutouts, object profile, int profileSide, int planeSide, string mode, FeatureSpec spec)
        {
            switch (mode.ToLowerInvariant())
            {
                case "all":
                    return Call(cutouts, "AddThroughAll", new object[] { profile, profileSide, planeSide });
                case "finite":
                    double depth = spec.Depth ?? 0.2;
                    return Call(cutouts, "AddFinite", new object[] { profile, profileSide, planeSide, depth });
                default:
                    return Call(cutouts, "AddThroughNext", new object[] { profile, profileSide, planeSide });
            }
        }

        /// <summary>
        /// 旋转凸台(revolve):截面(rect / polygon / loops)+ 独立旋转轴 → RevolvedProtrusions.AddFinite。
        ///
        /// 旋转轴是【独立于截面的构造线】(交互里的"中心线"角色),不参与截面闭环约束;
        /// 截面本身照常逐线 + 端点重合约束闭合成环。
        ///
        /// ★ 两个实测坑(2026-09-09):
        ///   1) SetAxisOfRevolution 的返回值 RefAxis 必须原样传给 AddFinite。丢了/传错就会
        ///      在 AddFinite 上抛"找不到成员"——看起来像方法名错,其实是参数对象不对。
        ///   2) COM 建特征【不会】像交互那样自动隐藏草图,必须显式 SetVisible(profile,false),
        ///      否则截面和轴线会一直挂在图形区。
        /// </summary>
        private static object RevolveOp(SolidEdgeContext context, object doc, FeatureSpec spec, string name,
            Dictionary<string, object> namedPlanes)
        {
            object plane = ResolvePlane(context, doc, spec.PlaneRef, namedPlanes);
            bool visible = spec.Visible ?? false;

            // 旋转截面走直线环:circle/slot 是闭合曲线,直接拉伸就是圆柱/腰孔,不必旋转。
            if (spec.HasCircle || spec.HasSlot)
                return new
                {
                    op = "revolve", name = name, status = "error",
                    message = "revolve 截面请用 rect/polygon/loops 点列;circle(圆)/slot(腰孔)直接拉伸即可,无需旋转。"
                };

            if (!spec.HasAxis)
                return new
                {
                    op = "revolve", name = name, status = "error",
                    message = "revolve 必须提供 axis(旋转轴两点),如 \"axis\":[[u1,v1],[u2,v2]]。"
                };

            if (spec.ShapeError != null)
                return new { op = "revolve", name = name, status = "error", message = spec.ShapeError };

            var specWarnings = new List<string>();
            object[] pair = CreateProfileRevolve(context, doc, plane, spec, visible, specWarnings);
            object profile = pair[0];
            object refAxis = pair[1];

            int profileSide = spec.ProfileSide ?? 1;      // ProfileSide
            int planeSide = spec.Side ?? 2;               // ProfilePlaneSide
            double angle = RevolveAngle(spec);            // 默认 2π(360°)

            object models = Get(doc, "Models");
            bool firstFeature = Count(models) == 0;   // 首特征旋转:走 Models 级 API(尚无 Model 可挂 RevolvedProtrusions)
            // ★ 首特征时 Models 集合为空,Item(1) 会拿到 null(model=null → 下一行 NRE,2026-09-13 实测),
            //   所以 model/revolves 的获取必须整个条件化。
            object model = null;
            object revolves = null;
            // "旋转切割" = RevolvedCutout;其余(含缺省)走旋转凸台 RevolvedProtrusion
            bool isRevolveCut = string.Equals(spec.Mode, "cut", StringComparison.OrdinalIgnoreCase);
            if (!firstFeature)
            {
                model = Get(models, "Item", 1);
                revolves = isRevolveCut
                    ? Get(model, "RevolvedCutouts")
                    : Get(model, "RevolvedProtrusions");
            }

            // 签名:AddFinite(Profile, RefAxis, ProfileSide, ProfilePlaneSide, AngleofRevolution)
            object featObj;
            if (firstFeature)
            {
                // Models.AddFiniteRevolvedProtrusion 的返回值就是新 Model
                // (2026-09-13 实测:建完立刻回读 models.Item(1) 返回 null → NRE,必须用返回值)。
                // 调用必须走 PIA 强类型:标准 binder 对 SAFEARRAY(DISPATCH) 参数报 TYPEMISMATCH,
                // ManualInvoke 对 VT_USERDEFINED 参数(RefAxis/枚举)会静默返回 null。
                var modelsTyped = (SolidEdgePart.Models)models;
                Array profArr = new object[] { profile };
                var refAxisTyped = (SolidEdgePart.RefAxis)refAxis;
                object m = modelsTyped.AddFiniteRevolvedProtrusion(1, ref profArr, refAxisTyped,
                    (SolidEdgePart.FeaturePropertyConstants)planeSide, angle);
                featObj = Get(Get(m, "RevolvedProtrusions"), "Item", 1);
            }
            else
            {
                featObj = Call(revolves, "AddFinite",
                    new object[] { profile, refAxis, profileSide, planeSide, angle });
            }

            string kindOverride = isRevolveCut ? "RevolvedCutout" : "RevolvedProtrusion";
            return FeatureResult("revolve", name, featObj, context, profile, kindOverride, specWarnings);
        }

        /// <summary>旋转角:degrees(度)优先并换算成弧度,其次 angle(弧度),都没有则 2π(整圈)。</summary>
        private static double RevolveAngle(FeatureSpec spec)
        {
            if (spec.Degrees.HasValue) return spec.Degrees.Value * Math.PI / 180.0;
            if (spec.Angle.HasValue) return spec.Angle.Value;
            return 2.0 * Math.PI;
        }

        /// <summary>
        /// 建旋转轮廓:ProfileSets.Add → Profiles.Add(plane) → 截面闭环(逐线 + 端点重合约束)
        /// → 单独画旋转轴(不进闭环)→ SetAxisOfRevolution(必须在 End 之前)→ End(0)→ 隐藏草图。
        /// 返回 [Profile, RefAxis]:RefAxis 要原样交给 AddFinite。
        /// </summary>
        private static object[] CreateProfileRevolve(SolidEdgeContext context, object doc, object plane, FeatureSpec spec, bool visible, List<string> specWarnings)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object lines = Get(profile, "Lines2d");
            object relations = Get(profile, "Relations2d");

            // 1) 截面:每个环逐线首尾相连 + 端点重合约束(与 CreateProfileMulti 同逻辑)
            var loopLines = new List<(object line, double[] p0, double[] p1)>();
            foreach (var pts in spec.Loops)
            {
                int n = pts.Length;
                var lineObjs = new object[n];
                for (int i = 0; i < n; i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % n];
                    lineObjs[i] = Call(lines, "AddBy2Points", new object[] { p0[0], p0[1], p1[0], p1[1] });
                    loopLines.Add((lineObjs[i], p0, p1));
                }
                for (int i = 0; i < n; i++)
                {
                    Call(relations, "AddKeypoint", new object[] { lineObjs[i], 1, lineObjs[(i + 1) % n], 0 });
                }
            }

            // 1.5) 声明式约束/标注(必须在 End 之前的开放上下文里;轴独立于截面,不占 dims 的 element 索引)
            ApplySpecConstraints(context, doc, profile, spec, loopLines, specWarnings);

            // 2) 旋转轴:独立构造线,不参与上面的闭环约束
            object axisLine = Call(lines, "AddBy2Points",
                new object[] { spec.AxisP1[0], spec.AxisP1[1], spec.AxisP2[0], spec.AxisP2[1] });

            // 2.5) AutoConstraint 时把轴线整体固定,否则轴自身 4 个自由度悬空(DOF 会计不为零)
            if (spec.AutoConstraint == true)
            {
                try { Call(relations, "AddFix", new object[] { axisLine }); }
                catch (Exception ex) { specWarnings.Add("axisFix: " + ex.Message); }
            }

            // 3) 指定旋转轴(End 之前),返回值 RefAxis 交给 AddFinite
            object refAxis = Call(profile, "SetAxisOfRevolution", new object[] { axisLine });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);

            return new object[] { profile, refAxis };
        }

        /// <summary>读取特征的全局包围盒 [xmin,ymin,zmin,xmax,ymax,zmax](米),用于诊断坐标。</summary>
        private static double[] TryRangeBox(object featObj)
        {
            try
            {
                dynamic d = featObj;
                var rb = (double[])d.RangeBox;
                if (rb != null && rb.Length >= 6)
                    return new[] { rb[0], rb[1], rb[2], rb[3], rb[4], rb[5] };
            }
            catch { }
            try
            {
                object o = Get(featObj, "RangeBox");
                if (o is Array a && a.Length >= 6)
                {
                    return new[]
                    {
                        Convert.ToDouble(a.GetValue(0)), Convert.ToDouble(a.GetValue(1)), Convert.ToDouble(a.GetValue(2)),
                        Convert.ToDouble(a.GetValue(3)), Convert.ToDouble(a.GetValue(4)), Convert.ToDouble(a.GetValue(5))
                    };
                }
            }
            catch { }
            return null;
        }

        // ---------------- 公共封装 ----------------

        /// <summary>
        /// 所有建模 op 的统一出口——【内置强制校验】:
        /// 特征建完立刻读 Status,不等于 StatusOk 就判失败,并【自动删除该特征】回滚,
        /// 不让僵尸特征留在模型树上(留着会污染模型树、误导后续特征)。
        ///
        /// 于是调用方看到的 status 只有 "ok" / "error",既不用认识 Solid Edge 的状态码,
        /// 也不用记得自己去检查——验证固化在 op 里,对调用方无感。
        /// </summary>
        private static object FeatureResult(string op, string name, object featObj, SolidEdgeContext context,
            object profile = null, string kindOverride = null, List<string> specWarnings = null)
        {
            string featureName = SafeString(Get(featObj, "Name"));
            long? status = SafeLong(Get(featObj, "Status"));

            if (status.HasValue && status.Value != StatusOk)
            {
                // 先把草图容器抓在手里:特征一删,profile 常常就取不到 Parent 了
                // (实测:先删特征再回头取 Parent 会失败,草图就漏删了)。
                object profileSet = profile != null ? TryGetProfileSet(profile) : null;

                bool rolledBack = TryDelete(featObj);

                // 特征删完再删草图:ProfileSet 不在 Features 集合里,不清理就会
                // 在文档里留下一堆草图垃圾(还会显示在图形区)。
                bool sketchCleaned = profileSet != null && TryDelete(profileSet);

                string reason = status.Value == StatusZombie ? "几何未生成(僵尸特征)" : "特征状态异常";
                string diagnosis = (op == "cut" || kindOverride == "RevolvedCutout")
                    ? "除料没切到实体:检查 plane 是否选对、side 方向是否朝实体内部、草图是否落在毛坯范围内。"
                    : "草图没长出实体:检查轮廓是否闭合、是否落在已有实体上、side/depth 方向是否正确。";

                return new
                {
                    op = op,
                    name = name,
                    status = "error",
                    feature = featureName,
                    featureStatus = status.Value,
                    rolledBack = rolledBack,
                    sketchCleaned = sketchCleaned,
                    message = reason + ":Status=" + status.Value + "(正常应为 " + StatusOk + ");" +
                              (rolledBack ? "已自动删除该特征" : "自动删除失败,请手动删除该僵尸特征") +
                              (profile == null ? "。" : (sketchCleaned ? ",并清理了它的草图。" : ",但草图清理失败。")),
                    diagnosis = diagnosis,
                    fix = new { action = "fix_and_retry", check = new[] { "plane", "side", "形状位置", "depth" } }
                };
            }

            string kind = kindOverride
                ?? (op == "cut" ? "ExtrudedCutout"
                : (op == "revolve" ? "RevolvedProtrusion" : "ExtrudedProtrusion"));
            string handleId = context.AddHandle(featObj, kind, featureName);

            return new
            {
                op = op,
                name = name,
                status = "ok",
                feature = featureName,
                featureStatus = status,
                faces = FacesCount(featObj),
                rangebox = TryRangeBox(featObj),
                handle = handleId,
                verified = true,
                warnings = (specWarnings != null && specWarnings.Count > 0) ? specWarnings : null,
                hint = "已内置校验(仅 Status=" + StatusOk + " 才返回 ok)。"
            };
        }

        /// <summary>删除刚建出来的特征(回滚用)。失败只返回 false 不抛——别让回滚失败盖掉真正的错误。</summary>
        private static bool TryDelete(object comObj)
        {
            try
            {
                Call(comObj, "Delete", null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 删掉草图所属的 ProfileSet(Profile → Profiles → ProfileSet)。
        ///
        /// 为什么必须单独做这件事:ProfileSet 【不在】 Features 集合里,所以删特征
        /// (不管是手动删还是回滚删)都不会连带删草图。不清理,文档里就会越堆越多
        /// 草图垃圾——它们还会显示在图形区,看起来就像"草图没隐藏"。
        /// </summary>
        private static object TryGetProfileSet(object profile)
        {
            try
            {
                // 实测(2026-09-09):Profile.Parent 直接就是 ProfileSet,不是 Profiles 集合。
                // 早先按"Profile→Profiles→ProfileSet"取了两层,第二层其实是文档级的
                // ProfileSets 集合——删它会失败(真删成功就会把所有草图一锅端)。
                object parent = Get(profile, "Parent");
                if (parent == null) return null;

                // 保险:若拿到的确实不是 ProfileSet(没有 Profiles 属性),再上溯一层。
                if (!HasMember(parent, "Profiles"))
                {
                    object upper = Get(parent, "Parent");
                    if (upper == null) return null;
                    parent = upper;
                }

                return parent;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>判断对象上是否有某个可读成员(用来区分 ProfileSet 与 Profiles 集合)。</summary>
        private static bool HasMember(object comObj, string name)
        {
            try
            {
                return Get(comObj, name) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读结果对象里的 status 字段。结果都是匿名对象,这里反射取一下即可——
        /// 省得为了判成败把每个 op 的返回类型都改一遍。
        /// </summary>
        private static bool IsOk(object result)
        {
            try
            {
                var p = result.GetType().GetProperty("status");
                return Equals(p?.GetValue(result) as string, "ok");
            }
            catch
            {
                return true;    // 取不到就不中止,保守放行
            }
        }

        /// <summary>
        /// 按特征描述建轮廓:给了 circle 就走圆轮廓(真圆柱/真圆孔),
        /// 否则走直线环(rect / polygon / loops)。extrude 与 cut 共用此入口。
        /// </summary>
        private static object CreateProfileForFeature(SolidEdgeContext context, object doc, object plane, FeatureSpec spec, bool visible, List<string> specWarnings)
        {
            if (spec.HasCircle)
                return CreateProfileCircle(doc, plane, spec.CircleX, spec.CircleY, spec.CircleR, visible);

            if (spec.HasCircles)
                return CreateProfileCircles(doc, plane, spec.Circles, visible);

            if (spec.HasSlot)
                return CreateProfileSlot(doc, plane, spec, visible);

            // 形状解析失败时在这里抛(与改造前 ParseLoops 抛异常的时机一致),
            // 由 se_model_build 的 per-feature try/catch 转成 status=error。
            if (spec.ShapeError != null)
                throw new ArgumentException(spec.ShapeError);

            return CreateProfileMulti(context, doc, plane, spec, visible, specWarnings);
        }

        /// <summary>
        /// 建圆形内嵌轮廓:ProfileSets.Add → Profiles.Add(plane) → Circles2d.AddByCenterRadius → End(0) → 隐藏。
        /// 圆是天然闭合曲线,【不需要】端点重合约束(直线轮廓才必须加)。
        /// 圆轮廓直接拉伸 = 真圆柱(1 侧面 + 顶 + 底 = 3 面),不必走旋转。
        /// </summary>
        private static object CreateProfileCircle(object doc, object plane, double cx, double cy, double r, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object circles = Get(profile, "Circles2d");
            Call(circles, "AddByCenterRadius", new object[] { cx, cy, r });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建【多个真圆】内嵌轮廓(同一 profile 的多个闭合环):Circles2d.AddByCenterRadius ×N → End(0) → 隐藏。
        /// 用于一次切/拉多个真圆孔(如法兰螺栓孔阵列),比 loops 多边形近似更准(孔壁是真圆柱面)。
        /// 实测(2026-09-10):一个 profile 放 3 个圆,AddThroughNext 一次切出全部孔(实体面数 = 2 + 外圆 + N)。
        /// </summary>
        private static object CreateProfileCircles(object doc, object plane, List<double[]> circles, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object circles2d = Get(profile, "Circles2d");
            foreach (var c in circles)
                Call(circles2d, "AddByCenterRadius", new object[] { c[0], c[1], c[2] });

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建【真圆弧】腰孔(长圆孔)轮廓:两条直线边 + 两端半圆弧 + 4 个端点重合约束 → End(0) → 隐藏。
        ///
        /// 为什么不用 polygon 近似:折线逼近会在两端半圆留下可见的棱(8 段/端就有 12° 的折角),
        /// 而且面数会随段数暴涨(18 段 = 18 个侧面)。真圆弧只有 4 个侧壁面(2 平面 + 2 柱面)。
        ///
        /// ★★ 端点重合约束的索引【按元素类型不同而不同】(2026-09-08 实测,别再凭猜):
        ///   Line2d: 0 = 起点, 1 = 终点
        ///   Arc2d : 0 = 圆心, 1 = 起点, 2 = 终点
        /// 实测方法:两段半圆弧拼整圆,只有索引取对时 Profile.Form 才 = 2(闭合),取错则 = 1。
        /// 圆弧方向:AddByCenterStartEnd 一律【逆时针】从起点走到终点。
        /// </summary>
        private static object CreateProfileSlot(object doc, object plane, FeatureSpec spec, bool visible)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            double r = spec.SlotWidth / 2.0;
            // 直边半长;length == width 时退化为 0(两端半圆直接相接 = 圆)
            double half = Math.Max((spec.SlotLength - spec.SlotWidth) / 2.0, 0.0);

            double dx = Math.Cos(spec.SlotAngle), dy = Math.Sin(spec.SlotAngle);   // 长轴方向
            double nx = -dy, ny = dx;                                              // 面内法向

            double c1x = spec.SlotX - dx * half, c1y = spec.SlotY - dy * half;     // 左端圆心
            double c2x = spec.SlotX + dx * half, c2y = spec.SlotY + dy * half;     // 右端圆心

            double ptrX = c2x + nx * r, ptrY = c2y + ny * r;   // 右上
            double ptlX = c1x + nx * r, ptlY = c1y + ny * r;   // 左上
            double pblX = c1x - nx * r, pblY = c1y - ny * r;   // 左下
            double pbrX = c2x - nx * r, pbrY = c2y - ny * r;   // 右下

            object lines = Get(profile, "Lines2d");
            object arcs = Get(profile, "Arcs2d");
            object relations = Get(profile, "Relations2d");

            // 逆时针一圈:上直边 → 左半圆 → 下直边 → 右半圆
            object lineTop = Call(lines, "AddBy2Points", new object[] { ptrX, ptrY, ptlX, ptlY });
            object arcLeft = Call(arcs, "AddByCenterStartEnd", new object[] { c1x, c1y, ptlX, ptlY, pblX, pblY });
            object lineBottom = Call(lines, "AddBy2Points", new object[] { pblX, pblY, pbrX, pbrY });
            object arcRight = Call(arcs, "AddByCenterStartEnd", new object[] { c2x, c2y, pbrX, pbrY, ptrX, ptrY });

            // 端点首尾相接:上一个的终点 ↔ 下一个的起点(索引见上面注释)
            Call(relations, "AddKeypoint", new object[] { lineTop, 1, arcLeft, 1 });      // 左上
            Call(relations, "AddKeypoint", new object[] { arcLeft, 2, lineBottom, 0 });   // 左下
            Call(relations, "AddKeypoint", new object[] { lineBottom, 1, arcRight, 1 });  // 右下
            Call(relations, "AddKeypoint", new object[] { arcRight, 2, lineTop, 0 });     // 右上

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 建内嵌轮廓(支持多环):ProfileSets.Add → Profiles.Add(plane) → 每环逐线 + 端点闭合 → End(0) → 隐藏。
        /// 多孔(门+窗)必须放同一轮廓的多个闭合环里一次切,否则第 2+ 个 AddThroughNext 会 6311 僵尸。
        /// </summary>
        private static object CreateProfileMulti(SolidEdgeContext context, object doc, object plane, FeatureSpec spec, bool visible, List<string> specWarnings)
        {
            object profileSets = Get(doc, "ProfileSets");
            object profileSet = Call(profileSets, "Add", null);
            object profiles = Get(profileSet, "Profiles");
            object profile = Call(profiles, "Add", new object[] { plane });

            object lines = Get(profile, "Lines2d");
            object relations = Get(profile, "Relations2d");

            var loopLines = new List<(object line, double[] p0, double[] p1)>();
            foreach (var pts in spec.Loops)
            {
                int n = pts.Length;
                var lineObjs = new object[n];
                for (int i = 0; i < n; i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % n];
                    lineObjs[i] = Call(lines, "AddBy2Points", new object[] { p0[0], p0[1], p1[0], p1[1] });
                    loopLines.Add((lineObjs[i], p0, p1));
                }
                for (int i = 0; i < n; i++)
                {
                    Call(relations, "AddKeypoint", new object[] { lineObjs[i], 1, lineObjs[(i + 1) % n], 0 });
                }
            }

            ApplySpecConstraints(context, doc, profile, spec, loopLines, specWarnings);

            Call(profile, "End", new object[] { 0 });
            if (!visible) SetVisible(profile, false);
            return profile;
        }

        /// <summary>
        /// 草图解析已上移到 <see cref="FeatureSpecParser"/>(构建器与校验器共用),
        /// 本文件不再保留私有副本——两份解析必然漂移,校验就失去意义。
        /// </summary>

        private static object ResolvePlane(SolidEdgeContext context, object doc, string planeRef,
            Dictionary<string, object> namedPlanes)
        {
            if (string.IsNullOrWhiteSpace(planeRef))
                throw new ArgumentException("特征缺少 plane(或 base)平面引用。");

            if (planeRef.StartsWith("@", StringComparison.Ordinal))
            {
                string key = planeRef.Substring(1);
                if (namedPlanes.TryGetValue(key, out var np)) return np;
                throw new ArgumentException("未找到本批内命名平面 \"@" + key + "\"(需先用 op=plane 创建)。");
            }

            if (planeRef.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
            {
                var h = context.GetHandle(planeRef);
                if (h == null || h.ComObject == null)
                    throw new ArgumentException("句柄表里找不到平面对象 " + planeRef + "。");
                return h.ComObject;
            }

            // 默认面:按名称解析
            object refPlanes = Get(doc, "RefPlanes");
            int count = Count(refPlanes);

            // "RefPlane_N" / "RefPlaneN" → 按索引 Item(N)(本机中文版 DisplayName 是"参考平面_N",不能用名称匹配)
            if (planeRef.StartsWith("RefPlane", StringComparison.OrdinalIgnoreCase))
            {
                string idxStr = planeRef.Substring("RefPlane".Length).TrimStart('_', ' ', '-');
                if (int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                    && idx >= 1 && idx <= count)
                {
                    return Get(refPlanes, "Item", idx);
                }
            }

            // 其它:按 DisplayName 匹配(含本地化名"参考平面_N")
            for (int i = 1; i <= count; i++)
            {
                object p = Get(refPlanes, "Item", i);
                string dn = SafeString(Get(p, "DisplayName"));
                if (string.Equals(dn, planeRef, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            throw new ArgumentException("找不到参考面 \"" + planeRef + "\"(支持 RefPlane_1/2/3、@name、obj-K)。");
        }

        // ---------------- COM 调用(裸 IDispatch,兼容强/弱类型 RCW) ----------------
        // 不用 Type.InvokeMember:app.ActiveDocument 等返回的强类型 RCW(System.Type 被
        // 固定为接口类型如 SolidEdgeFramework.Document)只暴露该接口成员,找不到零件文档
        // 才有的 RefPlanes/Models 等;裸 IDispatch 调用不挑 RCW 类型,与 se_invoke_chain 同路。

        /// <summary>无参属性读取。</summary>
        private static object Get(object obj, string name)
        {
            return InvokeRaw(obj, name, null);
        }

        /// <summary>带参属性(索引器)读取,如 Item(1)。</summary>
        private static object Get(object obj, string name, object arg)
        {
            return InvokeRaw(obj, name, new[] { arg });
        }

        /// <summary>方法调用(含 SAFEARRAY 数组参数,ManualInvoke 按元素类型构造数组)。</summary>
        private static object Call(object obj, string name, object[] args)
        {
            return InvokeRaw(obj, name, args);
        }

        private static object InvokeRaw(object obj, string name, object[] args)
        {
            if (ManualInvoke.TryInvoke(obj, name, args ?? Array.Empty<object>(), out var r, out var err))
                return r;
            throw err ?? new Exception("IDispatch 调用失败: " + name);
        }

        private static int Count(object coll)
        {
            try { return Convert.ToInt32(Get(coll, "Count")); }
            catch { return 0; }
        }

        /// <summary>写属性(属性名 + 值)。建模 IR 的 autoConstraint/dims 需要(Constraint 等)。</summary>
        private static void Put(object obj, string name, object value)
        {
            obj.GetType().InvokeMember(name, BindingFlags.SetProperty, null, obj,
                new object[] { value }, null, CultureInfo.InvariantCulture, null);
        }

        /// <summary>
        /// 轮廓 End 之前应用声明式约束/标注(必须在开放的轮廓编辑上下文内,这是绑定生效的唯一窗口):
        ///   ①autoConstraint:按坐标推断 H/V(|dy|&lt;eps → 水平,|dx|&lt;eps → 垂直);
        ///   ②fixOrigin:首环首线起点 AddKeypointFix(消除整体平移自由度);
        ///   ③dims:AddLength(线)→ Constraint=true(驱动化,缺了 Edit 静默无效,2026-09-13 实测)
        ///     → PutName 改名进变量表 → Edit 值/公式。
        /// 单项失败只记 warning 不中断(几何已成型,绑定失败可事后补);硬 COM 异常向上抛。
        /// </summary>
        private static void ApplySpecConstraints(SolidEdgeContext context, object doc, object profile,
            FeatureSpec spec, List<(object line, double[] p0, double[] p1)> loopLines, List<string> warnings)
        {
            object relations = Get(profile, "Relations2d");

            if (spec.AutoConstraint == true)
            {
                for (int i = 0; i < loopLines.Count; i++)
                {
                    var (line, p0, p1) = loopLines[i];
                    try
                    {
                        if (Math.Abs(p0[1] - p1[1]) <= 1e-9)
                            Call(relations, "AddHorizontal", new object[] { line });
                        else if (Math.Abs(p0[0] - p1[0]) <= 1e-9)
                            Call(relations, "AddVertical", new object[] { line });
                    }
                    catch (Exception ex) { warnings.Add("autoConstraint[" + i + "]: " + ex.Message); }
                }
            }

            if (spec.FixOrigin == true && loopLines.Count > 0)
            {
                try { Call(relations, "AddKeypointFix", new object[] { loopLines[0].line, 0 }); }
                catch (Exception ex) { warnings.Add("fixOrigin: " + ex.Message); }
            }

            if (spec.Dims == null || spec.Dims.Count == 0) return;

            object dimsCol = Get(profile, "Dimensions");
            object varsObj = Get(doc, "Variables");
            foreach (var ds in spec.Dims)
            {
                if (ds.ParseError != null) { warnings.Add("dims[" + (ds.Name ?? "?") + "]: " + ds.ParseError); continue; }
                if (ds.Element < 0 || ds.Element >= loopLines.Count)
                {
                    warnings.Add("dims[" + ds.Name + "]: element=" + ds.Element + " 越界(共 " + loopLines.Count + " 条线)");
                    continue;
                }
                try
                {
                    object dim = Call(dimsCol, "AddLength", new object[] { loopLines[ds.Element].line });
                    Put(dim, "Constraint", true);
                    Call(varsObj, "PutName", new object[] { dim, ds.Name });
                    Call(varsObj, "Edit", new object[] { ds.Name, ds.Value ?? ds.Formula });
                }
                catch (Exception ex) { warnings.Add("dims[" + ds.Name + "]: " + ex.Message); }
            }
        }

        private static void SetVisible(object comObj, bool value)
        {
            try
            {
                ((dynamic)comObj).Visible = value;
            }
            catch
            {
                try
                {
                    comObj.GetType().InvokeMember("Visible", BindingFlags.SetProperty, null, comObj,
                        new object[] { value }, null, CultureInfo.InvariantCulture, null);
                }
                catch { /* 忽略:隐藏失败只影响显示,不影响几何 */ }
            }
        }

        private static int FacesCount(object feat)
        {
            try
            {
                object faces = Get(feat, "Faces", 1);
                if (faces == null) return -1;
                return Convert.ToInt32(Get(faces, "Count"));
            }
            catch { return -1; }
        }

        // ---------------- JSON 解析 ----------------
        // 全部上移到 FeatureSpecParser:构建器与校验器必须共用同一份取数语义,
        // 本文件不再保留私有副本。

        private static string SafeString(object v)
        {
            try { return v?.ToString(); } catch { return null; }
        }

        private static long? SafeLong(object v)
        {
            try
            {
                if (v == null) return null;
                return Convert.ToInt64(v);
            }
            catch { return null; }
        }

        private static int SafeInt(object v)
        {
            try { return Convert.ToInt32(v); }
            catch { return -1; }
        }

        private static string DescribeException(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            Exception e = ex;
            int depth = 0;
            while (e != null && depth < 3)
            {
                if (depth > 0) sb.Append(" <- 内部: ");
                sb.Append(e.GetType().Name);
                if (!string.IsNullOrEmpty(e.Message)) sb.Append(": ").Append(e.Message);
                if (e.HResult != 0) sb.Append(" (HRESULT=0x").Append(e.HResult.ToString("X8")).Append(')');
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        private static string Error(string message)
        {
            return JsonSerializer.Serialize(new { status = "error", message = message });
        }
    }
}

using System.Linq;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// FeatureValidator 规则库回归测试。
    /// 纪律:新规则进 RuleRegistry 必须在这里补一组用例——规则回归是校验器最大的隐性风险。
    /// 分级原则(源码注释):宁 warning 不 error,只有"必然失败"的才 error。
    /// </summary>
    public class FeatureValidatorRuleTests
    {
        private static ValidationReport V(string json) => FeatureValidator.ValidateJson(json);

        private static bool Has(ValidationReport r, string code) => r.issues.Any(i => i.code == code);

        // ---------- 基线:合法特征必须全绿 ----------

        [Fact]
        public void 合法拉伸特征_status为ok且无任何issue()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.Equal("ok", r.status);
            Assert.Equal(0, r.errorCount);
            Assert.Equal(0, r.warningCount);
            Assert.Equal(1, r.featureCount);
        }

        [Fact]
        public void ValidateJson接受features包装格式()
        {
            var r = V("{\"features\":[{\"op\":\"plane\"},{\"op\":\"extrude\"}]}");
            Assert.Equal(2, r.featureCount);
        }

        // ---------- 结构层 E101~E105 / W101~W102 ----------

        [Fact]
        public void 未知op_E101报error()
        {
            var r = V("[{\"op\":\"sphere\",\"name\":\"x\"}]");
            Assert.True(Has(r, "E101"));
            Assert.Equal("error", r.status);
            // fix 结构化建议必须带 allowed 清单(给 LLM 消费的)
            var issue = r.issues.First(i => i.code == "E101");
            Assert.NotNull(issue.fix);
        }

        [Fact]
        public void 缺形状_E102报error()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05}]");
            Assert.True(Has(r, "E102"));
        }

        [Fact]
        public void depth为NaN字符串_E104拦截()
        {
            // JSON 原生无法表达 NaN,但数字字符串 "NaN" 能被 TryGetDbl 解析进来——E104 就是防它的
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":\"NaN\",\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E104"));
        }

        [Fact]
        public void depth为对象_E103字段类型错误()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":{},\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E103"));
        }

        [Fact]
        public void op为数字_E103报类型错误且行为回退extrude()
        {
            var r = V("[{\"op\":123}]");
            Assert.True(Has(r, "E103"));
            var r2 = V("[{\"op\":123,\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.False(Has(r2, "E101"));   // 回退成 extrude 后不应再报未知 op
        }

        [Fact]
        public void 量纲可疑_W101是warning不是error()
        {
            // depth 50 米:典型"把 mm 当 m 写",提醒但不拦
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":50,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "W101"));
            Assert.Equal(0, r.errorCount);
            Assert.Equal("warning", r.status);
        }

        [Fact]
        public void 未知字段_W102提示()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]],\"depht\":1}]");
            Assert.True(Has(r, "W102"));
        }

        [Fact]
        public void side越界_E105报error()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"side\":4,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E105"));
        }

        // ---------- 引用层 E201~E203 / W202 ----------

        [Fact]
        public void 缺plane引用_E201报error()
        {
            var r = V("[{\"op\":\"extrude\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E201"));
        }

        [Fact]
        public void plane缺base_E201报error()
        {
            var r = V("[{\"op\":\"plane\",\"name\":\"top\"}]");
            Assert.True(Has(r, "E201"));
        }

        [Fact]
        public void RefPlane索引为零_E202()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_0\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E202"));
        }

        [Fact]
        public void RefPlane超出默认3个_W202提示()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_9\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "W202"));
        }

        [Fact]
        public void 别名未定义_E203()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"@top\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E203"));
        }

        [Fact]
        public void 别名前向引用_E203()
        {
            // @top 在第 1 个特征使用,却在第 2 个才定义——必须报前向引用
            var r = V("[" +
                "{\"op\":\"extrude\",\"plane\":\"@top\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}," +
                "{\"op\":\"plane\",\"name\":\"top\",\"base\":\"RefPlane_1\",\"distance\":0.01}" +
                "]");
            Assert.True(Has(r, "E203"));
        }

        [Fact]
        public void 别名先定义后使用_不报E203()
        {
            var r = V("[" +
                "{\"op\":\"plane\",\"name\":\"top\",\"base\":\"RefPlane_1\",\"distance\":0.01}," +
                "{\"op\":\"extrude\",\"plane\":\"@top\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}" +
                "]");
            Assert.False(Has(r, "E203"));
            Assert.Equal("ok", r.status);
        }

        // ---------- 几何层 E301~E306 / W301~W304 ----------

        [Fact]
        public void polygon不足3点_E301()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"polygon\":[[0,0],[0.04,0]]}]");
            Assert.True(Has(r, "E301"));
        }

        [Fact]
        public void 相邻点重合_E302零长度边()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05," +
                      "\"polygon\":[[0,0],[0.05,0],[0.05,0],[0,0.05]]}]");
            Assert.True(Has(r, "E302"));
        }

        [Fact]
        public void 自交多边形_E303()
        {
            // 蝴蝶结四边形:第 2 条边与第 4 条边相交于 (0.05,0.05)
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05," +
                      "\"polygon\":[[0,0],[0.1,0],[0,0.1],[0.1,0.1]]}]");
            Assert.True(Has(r, "E303"));
        }

        [Fact]
        public void 共线退化面积_E304()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05," +
                      "\"polygon\":[[0,0],[0.05,0],[0.1,0]]}]");
            Assert.True(Has(r, "E304"));
        }

        [Fact]
        public void 多环绕向不一致_W301提示()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05," +
                      "\"loops\":[[[0,0],[0.05,0],[0.05,0.05],[0,0.05]],[[0.1,0],[0.1,0.05],[0.15,0]]]}]");
            Assert.True(Has(r, "W301"));
        }

        [Fact]
        public void 同特征内环重叠_W404提示()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05," +
                      "\"loops\":[[[0,0],[0.05,0],[0.05,0.05],[0,0.05]],[[0.01,0.01],[0.06,0.01],[0.06,0.06],[0.01,0.06]]]}]");
            Assert.True(Has(r, "W404"));
        }

        [Fact]
        public void 腰孔长小于宽_E306()
        {
            // length 是含两端半圆的总长,必须 >= width
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05," +
                      "\"slot\":{\"center\":[0,0],\"length\":0.02,\"width\":0.05}}]");
            Assert.True(Has(r, "E306"));
        }

        // ---------- 语义层 E401~E404 / W401~W403 ----------

        [Fact]
        public void 除料前无extrude_E401()
        {
            var r = V("[{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"circle\":[0,0,0.005]}]");
            Assert.True(Has(r, "E401"));
        }

        [Fact]
        public void extrude缺depth_E402()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E402"));
        }

        [Fact]
        public void extrude深度为零_E402()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "E402"));
        }

        [Fact]
        public void finite除料缺depth_E404()
        {
            // 静默套 0.2 米默认值是实打实的坑,必须 error 拦下
            var r = V("[" +
                "{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}," +
                "{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"mode\":\"finite\",\"circle\":[0.02,0.02,0.005]}]");
            Assert.True(Has(r, "E404"));
        }

        [Fact]
        public void finite除料给了depth_不报E404也不报W405()
        {
            var r = V("[" +
                "{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}," +
                "{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"mode\":\"finite\",\"depth\":0.01,\"circle\":[0.02,0.02,0.005]}]");
            Assert.False(Has(r, "E404"));
            Assert.False(Has(r, "W405"));
        }

        [Fact]
        public void 未指定mode_W405提醒默认切穿()
        {
            var r = V("[" +
                "{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}," +
                "{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"circle\":[0.02,0.02,0.005]}]");
            Assert.True(Has(r, "W405"));
        }

        [Fact]
        public void 同平面第二次除料_W401僵尸头号杀手()
        {
            var r = V("[" +
                "{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}," +
                "{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"circle\":[0.01,0.02,0.005]}," +
                "{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"circle\":[0.03,0.02,0.005]}]");
            Assert.True(Has(r, "W401"));
            // fix 建议必须是"合并进前一特征的 loops"(merge_into_previous)
            var issue = r.issues.First(i => i.code == "W401");
            Assert.NotNull(issue.fix);
        }

        [Fact]
        public void 双向拉伸带深度_W402提醒总长翻倍()
        {
            var r = V("[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"side\":3,\"rect\":[[0,0],[0.04,0.04]]}]");
            Assert.True(Has(r, "W402"));
        }

        [Fact]
        public void 除料落在毛坯外_W403提示()
        {
            // extrude 建 0.04×0.04 毛坯,cut 圆心在 (10,10)——完全够不着材料
            var r = V("[" +
                "{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0,0],[0.04,0.04]]}," +
                "{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"circle\":[10,10,0.005]}]");
            Assert.True(Has(r, "W403"));
        }

        // ---------- PlaneResolver / StockEstimator(W403 的地基) ----------

        [Fact]
        public void PlaneResolver_别名可追到默认面并叠加偏移()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                "[{\"op\":\"plane\",\"name\":\"top\",\"base\":\"RefPlane_2\",\"distance\":0.01}]");
            var arr = doc.RootElement.EnumerateArray().ToArray();
            var specs = FeatureSpecParser.ParseAll(arr);

            bool ok = PlaneResolver.TryResolve(specs, 1, "@top", out int planeIdx, out double offset);
            Assert.True(ok);
            Assert.Equal(2, planeIdx);          // 追到 RefPlane_2
            Assert.Equal(0.01, offset, 12);     // 叠加了 plane op 的偏移
        }

        [Fact]
        public void PlaneResolver_无法解析obj句柄时返回false不误报()
        {
            using var doc = System.Text.Json.JsonDocument.Parse("[{\"op\":\"extrude\"}]");
            var specs = FeatureSpecParser.ParseAll(doc.RootElement.EnumerateArray().ToArray());
            Assert.False(PlaneResolver.TryResolve(specs, 1, "obj-3", out _, out _));
        }

        [Fact]
        public void StockEstimator_单个拉伸的毛坯包围盒()
        {
            // RefPlane_1 = XY 面:u→X, v→Y,法向 Z;side 默认 2(朝 +Z)
            using var doc = System.Text.Json.JsonDocument.Parse(
                "[{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"depth\":0.05,\"rect\":[[0.1,0.2],[0.3,0.4]]}]");
            var specs = FeatureSpecParser.ParseAll(doc.RootElement.EnumerateArray().ToArray());
            var stock = StockEstimator.Estimate(specs);
            Assert.NotNull(stock);
            Assert.Equal(0.1, stock[0], 12);   // X min
            Assert.Equal(0.2, stock[1], 12);   // Y min
            Assert.Equal(0.0, stock[2], 12);   // Z min(从平面出发)
            Assert.Equal(0.3, stock[3], 12);   // X max
            Assert.Equal(0.4, stock[4], 12);   // Y max
            Assert.Equal(0.05, stock[5], 12);  // Z max(+depth)
        }
    }
}

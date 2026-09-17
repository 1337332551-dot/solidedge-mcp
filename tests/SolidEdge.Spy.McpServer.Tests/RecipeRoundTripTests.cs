using System;
using System.Linq;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 配方(RecipeSpec)解析与静态校验测试。
    /// 与 FeatureSpec 同一原则:解析器与校验器共用一份解析,漂移 = 校验失效。
    /// </summary>
    public class RecipeRoundTripTests
    {
        private static RecipeSpec Parse(string json)
            => RecipeSpecParser.Parse(JsonDocument.Parse(json).RootElement, "test.json");

        private static readonly string ValidRecipe = @"
{
  ""name"": ""make_plate"",
  ""title"": ""拉伸平板"",
  ""version"": 2,
  ""inputs"": [
    {""name"":""depth"",""type"":""double"",""required"":true,""unit"":""m""}
  ],
  ""preconditions"": [
    {""kind"":""docKind"",""value"":"" PartDocument ""}
  ],
  ""steps"": [
    {""member"":""Profiles"",""on"":""$$""},
    {""member"":""Add"",""on"":""$1""},
    {""member"":""AddFiniteExtrudedProtrusion"",""on"":""$2"",""args"":[""{{depth}}""]}
  ],
  ""verification"": {""kind"":""snapshotDiff"",""name"":""after_build"",""expect"":""identical=true""}
}";

        // ---------- 解析 ----------

        [Fact]
        public void 合法配方_字段全部映射()
        {
            var r = Parse(ValidRecipe);
            Assert.Equal("make_plate", r.Name);
            Assert.Equal("拉伸平板", r.Title);
            Assert.Equal(2, r.Version);
            Assert.Equal("draft", r.Status);          // 缺省 draft
            Assert.Single(r.Inputs);
            Assert.Equal("depth", r.Inputs[0].Name);
            Assert.Equal("double", r.Inputs[0].Type);
            Assert.True(r.Inputs[0].Required);
            Assert.Equal(3, r.Steps.Count);
            Assert.Equal("$$", r.Steps[0].On);
            Assert.Equal("{{depth}}", r.Steps[2].Args[0]);
            Assert.Equal("snapshotDiff", r.VerifyKind);
        }

        [Fact]
        public void 根节点非对象_抛ArgumentException()
        {
            Assert.Throws<ArgumentException>(() => RecipeSpecParser.Parse(
                JsonDocument.Parse("[1,2]").RootElement, "test.json"));
        }

        [Fact]
        public void 非字符串default_转为字符串保存()
        {
            var r = Parse("{\"name\":\"x\",\"inputs\":[{\"name\":\"n\",\"default\":5}],\"steps\":[{\"member\":\"m\"}]}");
            Assert.True(r.Inputs[0].HasDefault);
            Assert.Equal("5", r.Inputs[0].Default);
        }

        // ---------- 校验:合法路径必须零 issue ----------

        [Fact]
        public void 合法配方_校验零issue()
        {
            var issues = RecipeValidator.Validate(Parse(ValidRecipe));
            Assert.Empty(issues);
        }

        // ---------- 校验:结构 ----------

        [Fact]
        public void 缺name_报error()
        {
            var issues = RecipeValidator.Validate(Parse("{\"steps\":[{\"member\":\"m\"}]}"));
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "name");
        }

        [Fact]
        public void 非法name_报error()
        {
            var issues = RecipeValidator.Validate(Parse("{\"name\":\"../evil\",\"steps\":[{\"member\":\"m\"}]}"));
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "name");
            Assert.False(RecipeValidator.IsSafeName("a b"));       // 空格非法
            Assert.True(RecipeValidator.IsSafeName("my.recipe-1")); // 字母数字/_-. 合法
        }

        [Fact]
        public void 零步骤_报error()
        {
            var issues = RecipeValidator.Validate(Parse("{\"name\":\"x\"}"));
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "steps");
        }

        [Fact]
        public void 步骤缺member_报error()
        {
            var issues = RecipeValidator.Validate(Parse("{\"name\":\"x\",\"steps\":[{}]}"));
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "steps[0]");
        }

        // ---------- 校验:占位符引用 ----------

        [Fact]
        public void 引用未声明的input_报error()
        {
            var json = "{\"name\":\"x\",\"inputs\":[{\"name\":\"a\"}]," +
                       "\"steps\":[{\"member\":\"m\",\"args\":[\"{{nope}}\"]}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "error" && i.Message.Contains("nope"));
        }

        [Fact]
        public void 步骤引用前序返回值_合法()
        {
            var json = "{\"name\":\"x\"," +
                       "\"steps\":[{\"member\":\"a\"},{\"member\":\"b\",\"on\":\"$1\"}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.DoesNotContain(issues, i => i.Severity == "error");
        }

        [Fact]
        public void 美元N越界引用_报error()
        {
            // 第 1 步就引用 $2(只能引用前序步骤)→ 越界
            var json = "{\"name\":\"x\",\"steps\":[{\"member\":\"m\",\"on\":\"$2\"}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "error" && i.Message.Contains("越界"));
        }

        [Fact]
        public void 声明了未引用的input_报warn()
        {
            var json = "{\"name\":\"x\",\"inputs\":[{\"name\":\"a\"}]," +
                       "\"steps\":[{\"member\":\"m\",\"args\":[\"{{b}}\"]}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "warn" && i.Where == "inputs.a");
        }

        // ---------- 校验:inputs / preconditions / status ----------

        [Fact]
        public void input类型非法_报error()
        {
            var json = "{\"name\":\"x\",\"inputs\":[{\"name\":\"a\",\"type\":\"float\"}]," +
                       "\"steps\":[{\"member\":\"m\",\"args\":[\"{{a}}\"]}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "inputs.a");
        }

        [Fact]
        public void input名重复_报error()
        {
            var json = "{\"name\":\"x\",\"inputs\":[{\"name\":\"a\"},{\"name\":\"a\"}]," +
                       "\"steps\":[{\"member\":\"m\",\"args\":[\"{{a}}\"]}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "error" && i.Message.Contains("重复"));
        }

        [Fact]
        public void required与default同给_报warn()
        {
            var json = "{\"name\":\"x\",\"inputs\":[{\"name\":\"a\",\"required\":true,\"default\":\"1\"}]," +
                       "\"steps\":[{\"member\":\"m\",\"args\":[\"{{a}}\"]}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "warn" && i.Where == "inputs.a");
        }

        [Fact]
        public void precondition类型非法_报error()
        {
            var json = "{\"name\":\"x\",\"preconditions\":[{\"kind\":\"moonPhase\",\"value\":\"full\"}]," +
                       "\"steps\":[{\"member\":\"m\"}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "preconditions");
        }

        [Fact]
        public void verified状态缺verification_报error()
        {
            var json = "{\"name\":\"x\",\"status\":\"verified\",\"steps\":[{\"member\":\"m\"}]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "error" && i.Where == "verification");
        }

        [Fact]
        public void 步数超过提示阈值_报warn不拦截()
        {
            // 阈值 20:略高于实测可跑通的 19 步,只提示拆分不拦截
            var steps = string.Join(",", Enumerable.Range(0, 21).Select(_ => "{\"member\":\"m\"}"));
            var json = "{\"name\":\"x\",\"steps\":[" + steps + "]}";
            var issues = RecipeValidator.Validate(Parse(json));
            Assert.Contains(issues, i => i.Severity == "warn" && i.Where == "steps");
            Assert.DoesNotContain(issues, i => i.Severity == "error" && i.Where == "steps");
        }
    }
}

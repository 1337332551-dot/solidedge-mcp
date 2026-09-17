using System;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests
{
    /// <summary>
    /// 写能力护栏(Guardrail)的安全边界测试。
    /// 误放行 = 破坏模型;误拦截 = 建模配方瘫痪——两头都要钉死。
    /// 关键合同:分级只按成员名做静态判定,不解析参数。
    /// </summary>
    public class GuardrailTests
    {
        private static InvocationRisk Classify(string member, bool propertySet = false)
            => Guardrail.Classify(member, propertySet);

        // ---------- 分级:破坏性(默认拒绝,需 confirm) ----------

        [Theory]
        [InlineData("Delete")]
        [InlineData("delete")]        // 大小写不敏感
        [InlineData("Cut")]
        [InlineData("Drop")]
        [InlineData("Erase")]
        [InlineData("Purge")]
        [InlineData("RemoveAll")]     // Remove 前缀
        [InlineData("RemoveVariables")]
        public void 不可逆操作_分级为Destructive(string member)
        {
            Assert.Equal(InvocationRisk.Destructive, Classify(member));
        }

        [Fact]
        public void Delete以属性赋值形式调用_仍是Destructive()
        {
            // 精确匹配在 propertySet 判定之前——删除就是删除
            Assert.Equal(InvocationRisk.Destructive, Classify("Delete", propertySet: true));
        }

        // ---------- 分级:写操作(放行,但需审计) ----------

        [Theory]
        [InlineData("AddFiniteExtrudedProtrusion")]  // 建模主力
        [InlineData("AddFiniteRevolvedProtrusion")]
        [InlineData("Add")]                          // Profiles.Add
        [InlineData("Move")]
        [InlineData("Copy")]
        [InlineData("Insert")]
        [InlineData("SetName")]
        public void 建模与写成员_分级为ModelChanging(string member)
        {
            Assert.Equal(InvocationRisk.ModelChanging, Classify(member));
        }

        [Fact]
        public void End成员_当前分级为Normal_与Guardrail头部注释声明不一致()
        {
            // 已知文档/实现漂移(2026-09-15 单测发现):Guardrail.cs 头部注释声称
            // "AddFiniteExtrudedProtrusion / Profiles.Add / End 等属于 ModelChanging",
            // 但 ChangingExact/ChangingPrefix 两个清单里都没有 "End",实际走 Normal(不审计)。
            // 本用例钉住当前实际行为;若日后把 End 补进清单,这里会失败提醒同步更新注释。
            Assert.Equal(InvocationRisk.Normal, Classify("End"));
        }

        [Fact]
        public void 属性赋值一律视为ModelChanging()
        {
            Assert.Equal(InvocationRisk.ModelChanging, Classify("Visible", propertySet: true));
            Assert.Equal(InvocationRisk.ModelChanging, Classify("Height", propertySet: true));
        }

        // ---------- 分级:只读查询(放行) ----------

        [Theory]
        [InlineData("GetActiveDocument")]
        [InlineData("Item")]
        [InlineData("Count")]
        [InlineData("Name")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void 只读成员_分级为Normal(string member)
        {
            Assert.Equal(InvocationRisk.Normal, Classify(member));
        }

        // ---------- 门禁(Check) ----------

        [Fact]
        public void 只读成员_任何情况下放行()
        {
            Guardrail.ReadOnlyEnabled = true;
            try
            {
                string err = Guardrail.Check("se_invoke", "GetActiveDocument", false, false, out var risk);
                Assert.Null(err);
                Assert.Equal(InvocationRisk.Normal, risk);
            }
            finally { Guardrail.ReadOnlyEnabled = false; }
        }

        [Fact]
        public void 破坏性操作_未确认时拒绝并提示confirm()
        {
            string err = Guardrail.Check("se_invoke", "Delete", false, confirm: false, out var risk);
            Assert.NotNull(err);
            Assert.Equal(InvocationRisk.Destructive, risk);
            Assert.Contains("confirm", err);
        }

        [Fact]
        public void 破坏性操作_显式确认后放行()
        {
            string err = Guardrail.Check("se_invoke", "Delete", false, confirm: true, out _);
            Assert.Null(err);
        }

        [Fact]
        public void 写操作_默认放行_只读模式拒绝()
        {
            Assert.Null(Guardrail.Check("se_invoke", "Add", false, false, out _));   // 默认:放行建模

            Guardrail.ReadOnlyEnabled = true;
            try
            {
                string err = Guardrail.Check("se_invoke", "Add", false, false, out _);
                Assert.NotNull(err);   // SE_MCP_READONLY=1:一切写操作全拒
                Assert.Contains("SE_MCP_READONLY", err);
            }
            finally { Guardrail.ReadOnlyEnabled = false; }
        }
    }

    /// <summary>
    /// SolidEdgeContext.IsDisconnected:异常链上的"COM 对象已断连"判定。
    /// 区分"对象死了"(要重连/清句柄表)与"成员不适用"(对象还活着)——判错方向会误杀活对象。
    /// </summary>
    public class IsDisconnectedTests
    {
        private static bool IsDisconnected(Exception ex) => SolidEdgeContext.IsDisconnected(ex);

        [Theory]
        [InlineData(unchecked((int)0x80010108))]  // RPC_E_DISCONNECTED
        [InlineData(unchecked((int)0x800706BA))]  // RPC_S_SERVER_UNAVAILABLE
        [InlineData(unchecked((int)0x800706BE))]  // RPC_S_CALL_FAILED
        [InlineData(unchecked((int)0x800706BF))]  // RPC_S_CALL_FAILED_DNE
        [InlineData(unchecked((int)0x80010001))]  // RPC_E_CALL_CANCELLED
        public void 五个RPC断连HResult_判定为断连(int hr)
        {
            Assert.True(IsDisconnected(new System.Runtime.InteropServices.COMException("x", hr)));
        }

        [Fact]
        public void 断连COMException被外层包装_仍能识别()
        {
            var inner = new System.Runtime.InteropServices.COMException("x", unchecked((int)0x800706BA));
            var wrapped = new System.Reflection.TargetInvocationException("调用目标异常", inner);
            Assert.True(IsDisconnected(wrapped));
        }

        [Fact]
        public void 普通COMException不是断连()
        {
            // E_FAIL 之类的错误对象还活着,不能按断连处理
            var ex = new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80004005));
            Assert.False(IsDisconnected(ex));
        }

        [Fact]
        public void TargetInvocationException无COM内层_不是断连()
        {
            var ex = new System.Reflection.TargetInvocationException("x", null);
            Assert.False(IsDisconnected(ex));
        }

        [Fact]
        public void 普通托管异常_不是断连()
        {
            Assert.False(IsDisconnected(new System.InvalidOperationException("x")));
            Assert.False(IsDisconnected(null));
        }
    }
}

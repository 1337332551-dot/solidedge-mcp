# solidedge-mcp

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**让 AI 客户端（Claude Desktop、Cursor、Trae、Cline……）直接操作 Siemens Solid Edge 的 MCP server**——通过自然对话查询模型、构建参数化特征、自动化出图、监听文档事件。

Solid Edge 始终是唯一事实源：AI 不替代你的 CAD 工作流，而是操作它——像一双手，搭在你正在运行的 Solid Edge 实例上。

- [English](README.md) | [中文文档](README.zh-CN.md)

## 为什么做这个

机械工程师每天在重复的 CAD 操作上消耗大量时间：填参数化模型、改特征名、核对 BOM 一致性、导出图纸。本项目把 Solid Edge 的 COM API 暴露给任何支持 MCP 的 AI 客户端，让这些步骤变成一段对话，而不是一个你必须自己写、自己维护的宏。

当前已验证支持的工作流：

- **参数化建模**——用 JSON 描述特征序列，AI 负责填参数（`se_model_build` 先走静态 dry-run 校验，通过才真正动模型）
- **模型探查**——遍历对象树、读几何、读变量、读选中状态
- **出图自动化**——视图裁剪、图幅排版、中心线/中心标记、驱动尺寸
- **装配校核**——干涉检查、重量汇总
- **事件监听**——第二个 MCP server 把 Solid Edge 文档事件流式推给 AI

## 两个 MCP server

| Server | 可执行文件 | 用途 |
|---|---|---|
| 执行 | `solidedge-mcp` | 21 个工具：查询、文档、建模、脚本 |
| 事件 | `solidedge-event-mcp` | 4 个工具：订阅/等待/查询 Solid Edge 事件 |

## 工具总览（执行 server）

| 分类 | 工具 |
|---|---|
| 查询/只读 | `se_get_document` `se_get_selection` `se_find_paths` `se_describe_object` `se_walk_object` `se_batch_read` `se_read_geometry` `se_get_variables` `se_view_context` `se_capture_viewport` `se_snapshot_diff` `se_validate_features` |
| 文档会话 | `se_open_document` `se_new_document` `se_close_document` |
| 改动模型 | `se_model_build` `se_extrude_on_face` `se_invoke_member` `se_invoke_chain` `se_recipe_run` |
| 逃生通道 | `se_script_run`（对 COM API 跑一段 C# 脚本） |

## 权限模式

在 MCP 配置的 server 节点上设置 `SE_MCP_MODE` 环境变量：

| 值 | 行为 |
|---|---|
| `full`（默认） | 21 个工具全放行 |
| `readonly` | 只放行 12 个查询工具；建模/会话/脚本类调用在传输层直接拒绝，并提示如何切回 |
| 其他任意值 | fail-closed，按 `readonly` 处理 |

旧的 `SE_MCP_READONLY=1` 仍然兼容，等价 `readonly`。改模式后需要重启 AI 会话（客户端重载 MCP server 才生效）。

另外还有第二层洋葱：成员级护栏（`Guardrail`），并且每次工具调用都会写入审计日志 `%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log`。

## 环境要求

- Windows + 已安装并可运行的 **Siemens Solid Edge**（互操作包对应 SE2022 / 类型库 v108；其他版本见下文说明）
- **.NET 8 SDK**（自己编译），或直接用 Release 二进制
- 一个支持 MCP 的 AI 客户端

## 构建

```powershell
git clone https://github.com/<your-account>/solidedge-mcp.git
cd solidedge-mcp
dotnet build src/SolidEdge.Spy.McpServer -c Release
dotnet build src/SolidEdge.Spy.EventMcp  -c Release
dotnet test tests/SolidEdge.Spy.McpServer.Tests
```

不需要你提前准备任何 Siemens 文件：COM 互操作程序集来自社区发布的 [`Interop.SolidEdge`](https://www.nuget.org/packages/Interop.SolidEdge) NuGet 包（纯类型定义，本仓库不分发任何 Siemens 专有代码）。

如果你想基于本机安装的 Solid Edge 自己生成互操作程序集（例如 SE 版本不同），用 `scripts/gen_interop.ps1`（依赖 .NET Framework 的 `TlbImp.exe`），生成后改为引用产物 DLL。

## 配置 AI 客户端

把 server 指向编译好的二进制。示例：

**Claude Desktop / Cursor / Cline**（`claude_desktop_config.json` / `mcp.json`）：

```json
{
  "mcpServers": {
    "solidedge": {
      "command": "D:/path/to/solidedge-mcp/src/SolidEdge.Spy.McpServer/bin/Release/net8.0-windows/solidedge-mcp.exe",
      "env": { "SE_MCP_MODE": "readonly" }
    },
    "solidedge-events": {
      "command": "D:/path/to/solidedge-mcp/src/SolidEdge.Spy.EventMcp/bin/Release/net8.0-windows/solidedge-event-mcp.exe"
    }
  }
}
```

**Trae**（工作区 `.trae/mcp.json`）格式相同。

先启动 Solid Edge，再在客户端开新会话——server 会自动连接正在运行的实例。

## CLI 模式

执行二进制可以直接当一次性命令行工具用，不需要 AI 客户端，便于调试：

```powershell
solidedge-mcp.exe get_document
solidedge-mcp.exe invoke_member --objectId <id> --member Name
```

## 架构

```
AI 客户端 (Claude/Cursor/Trae)
   │  stdio JSON-RPC
   ▼
PermissionTap ── 模式 × 工具风险档位 门禁（拒绝在进 SDK 前伪造完成）
   ▼
JsonRpcTap ── 调用计量 / 审计
   ▼
MCP SDK 工具处理器 ── Guardrail 成员级检查
   ▼
COM 互操作 (IDispatch + PIA) ── 正在运行的 Solid Edge 实例
```

```
src/
├── SolidEdge.Spy.McpServer/    # 执行 MCP server（21 工具）
├── SolidEdge.Spy.EventMcp/     # 事件 MCP server（4 工具）
├── SolidEdge.Shared/           # COM 互操作基础设施（编译期共享，单一数据源）
tests/                          # 188 个单元测试（纯逻辑，不需要装 SE）
scripts/                        # 辅助脚本（互操作程序集生成）
```

## 开发

```powershell
dotnet test tests/SolidEdge.Spy.McpServer.Tests
```

测试是纯 .NET 的（不依赖 Solid Edge），覆盖解析、校验规则、权限档位表、传输层 tap。

## 致谢与许可

- COM 互操作基础设施（`InteropServices/*`、扩展方法）衍生自 [Jason Newell 的 SolidEdgeSpy](https://github.com/JWSingleton/SolidEdgeSpy)——本项目最初就是在那份代码基础上长出来的。
- 互操作程序集由 [Solid Edge Community](https://github.com/SolidEdgeCommunity) 发布。

MIT——见 [LICENSE](LICENSE)。

> 本项目与 Siemens 无关，也未经 Siemens 认可。Solid Edge 是 Siemens Digital Industries Software 的商标。

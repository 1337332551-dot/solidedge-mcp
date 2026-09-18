# SolidEdge.Spy.EventMcp（solidedge-event-mcp）

Solid Edge **事件监视 MCP**（耳朵）：独立进程常驻订阅 SE 事件接口，写入 200 条环形缓冲，
供 agent 增量读取。与执行 MCP（solidedge-mcp，眼+手）配合使用，构成"驱动 → 等事件 → 确认"闭环。

## 为什么独立进程（不并入现有 McpServer）

SE 是 STA COM 服务器，事件回调封送到订阅线程的 STA 单元，**必须有 `GetMessage/DispatchMessage`
消息泵**才能进 sink。现有 McpServer 的 STA 线程用 `BlockingCollection.Take` 阻塞等出站调用，
没有消息循环，收不到回调。改造会波及 8 个在产工具，故独立进程风险隔离。

## 构建

```
dotnet build src/SolidEdge.Spy.EventMcp/SolidEdge.Spy.EventMcp.csproj -c Release
```

输出：`bin/Release/net8.0-windows/solidedge-event-mcp.exe`

## 运行模式

- **MCP 模式（无参数）**：stdio 传输，由 host（opencode/CodeBuddy 等）拉起。
- **CLI 自测模式**：`solidedge-event-mcp.exe --listen [秒]`（默认 60）——连接、订阅、
  事件实时打印 JSON 行、结束输出统计并干净退出（Unadvise）。上线前手工验证通道。

## host 配置（opencode.json）

```json
{
  "mcp": {
    "solidedge": {
      "type": "local",
      "command": ["D:\\...\\solidedge-mcp.exe"]
    },
    "solidedge-event": {
      "type": "local",
      "command": ["D:\\path\\to\\solidedge-mcp\\src\\SolidEdge.Spy.EventMcp\\bin\\Release\\net8.0-windows\\solidedge-event-mcp.exe"]
    }
  }
}
```

## 工具面（4 个）

| 工具 | 作用 |
|---|---|
| `se_get_events(seq, limit)` | 增量读取：返回 seq 大于入参的事件 + latestSeq + dropped（溢出计数）。首次传 0 取全部 |
| `se_wait_event(waitFor, timeoutMs)` | 唯一的阻塞工具：轮询环形缓冲直到任一匹配事件到达（`*`=任意事件，可逗号分隔多个） |
| `se_event_status()` | 诊断：连接状态 / 当前文档 / 订阅映射 / 缓冲统计 / 过滤器表 |
| `se_set_event_filter(event, enabled)` | 事件级开关，短名 `SelectSetChanged` 或全名 `ISEDocumentEvents.SelectSetChanged` |

事件条目：`{seq, time, source, event, detail, doc}`；合成事件 `source="EventRecorder"`（连接/重连等）。

## 订阅白名单

| 接口 | 成员数 | 宿主 |
|---|---|---|
| ISEApplicationEvents | 16 | Application 连接点 |
| ISEModelRecomputeEvents | 5 | `ActiveDocument.Models.Item(1).ModelRecomputeEvents`（仅零件） |
| ISEAssemblyRecomputeEvents | 5 | `ActiveDocument.AssemblyRecomputeEvents`（仅装配） |
| ISEDocumentEvents | 4 | `ActiveDocument.DocumentEvents`（每文档一订阅，自动跟随切换重订） |
| ISEFileUIEvents | 6 | Application 连接点（out 参数置 null，纯观察） |

`ISEAssemblyFamilyEvents` 不订阅（无场景）。默认仅 `SelectSetChanged` 关闭（高频噪声）。

## 架构要点

- **StaPump**：STA 线程 + message-only 窗口消息泵，全部 COM 调用（订阅/重订/活性检查）在此串行；
  `WM_APP+1` 工作队列、`WM_APP+2` 重扫文档级订阅、`WM_TIMER`(5s) 活性检查 + SE 重启自动重连。
- **sink 铁律**：回调里零 COM 调用零阻塞，只写环形缓冲；重订等 COM 操作 PostMessage 延迟到泵线程。
- **干净退出**：Dispose + ProcessExit + CancelKeyPress 兜底 Unadvise，杜绝僵尸订阅毒化 FileUIEvents。
- 工具读路径纯托管零 COM，泵忙时工具调用不卡。

## 已验证（2026-08-21，CLI 模式）

- 改特征参数 → `BeforeRecompute → AfterFeatureIsModified(seDirectInputsChanged) → AfterRecompute`
- 新建特征 → `BeforeRecompute → AfterFeatureIsAdded(seNew) → AfterRecompute`
- Ctrl+S → `BeforeSave(文档) → BeforeDocumentSave(应用) → AfterSave(文档) → AfterDocumentSave(应用)`
- 事件经消息泵进环形缓冲（54 条/180 秒会话），干净退出 Unadvise 无残留。

> 注意：**重算完成只认 `AfterRecompute`**——实测 BeforeRecompute 与 AfterRecompute 计数不严格配对。

## 相关文档

- `SE事件接口调研记录.md` —— 事件接口调研与踩坑（§6 蓝图落地）
- `.codebuddy/skills/se-query-recipes/SKILL.md` —— 双 MCP 调度入口（查询 + 事件等待配方）

using System;
using System.Linq;
using System.Text.Json;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 弹窗探针的**非 MCP 入口**（CLI `--dialogs` 用）。
///
/// 刻意不做成 MCP 工具（2026-09-11 用户裁定）：
/// "有弹窗阻塞就把信息返回给 AI" 已实装为 MCP 的**默认机制** ——
/// <see cref="SolidEdgeContext.Invoke"/> 在等待期间用 WindowProbe 盯着 Solid Edge，
/// 一旦调用被模态框堵住就直接抛 <see cref="SolidEdgeDialogBlockedException"/>，
/// 消息里带框标题/按钮/正文/处理建议。**任何工具都自动获得这个能力**，
/// 不需要 AI 记得"卡住时要额外去调某个专用工具"，也不必多占一个工具额度。
/// （AI 想主动探一眼时，随便调一个现有只读工具即可兼任探针：被堵就拿到框信息，
/// 没堵就拿到有用数据。）
///
/// 探针不碰 COM，所以它同时是"SE 卡死时的最后一条通道"——
/// 这条通道留给 CLI（与 --usage / --snap / --recipes 同惯例，不占 MCP 工具额度）。
/// </summary>
internal static class DialogTools
{
	/// <summary>探当前 Solid Edge 挂着的对话框，返回 JSON 文本（CLI --dialogs）。</summary>
	public static string Probe()
	{
		try
		{
			WindowSnapshot snap = WindowProbe.Snapshot();
			DialogWindow[] modal = snap.Dialogs.Where(d => d.Modal).ToArray();

			var payload = new
			{
				status = "ok",
				seRunning = snap.SeRunning,
				dialogCount = snap.Dialogs.Count,
				modalCount = modal.Length,
				dialogs = snap.Dialogs.Select(d => new
				{
					hwnd = d.Hwnd,
					title = d.Title,
					className = d.ClassName,
					modal = d.Modal,
					foreground = d.Foreground,
					ownerTitle = d.OwnerTitle,
					process = d.ProcessName,
					buttons = d.Buttons.ToArray(),
					texts = d.Texts.ToArray()
				}).ToArray(),
				seWindows = snap.SeWindows.ToArray(),
				hint = BuildHint(snap, modal)
			};
			return JsonSerializer.Serialize(payload);
		}
		catch (Exception ex)
		{
			return JsonSerializer.Serialize(new
			{
				status = "error",
				message = "检测对话框失败(探针自身出错,不影响 Solid Edge): " + ex.Message
			});
		}
	}

	/// <summary>给对话框发 WM_CLOSE（等同人工点窗口右上角 X）。CLI --dialogs --close 用。</summary>
	public static bool CloseByHandle(long hwnd)
	{
		return WindowProbe.CloseDialog(hwnd);
	}

	private static string BuildHint(WindowSnapshot snap, DialogWindow[] modal)
	{
		if (!snap.SeRunning)
		{
			return "没找到运行中的 Solid Edge 进程或其窗口。若 SE 确实开着,可能是权限限制导致窗口枚举看不到。";
		}
		if (snap.Dialogs.Count == 0)
		{
			return "当前没有任何对话框。若刚才某个 se_* 调用长时间不返回,更可能是重算/加载大装配(而不是弹窗):"
				+ "可用 solidedge-event 的 se_wait_event 等 AfterRecompute 确认,或 se_event_status 看连接状态;"
				+ "不要盲目连续重试 —— 每次重试都会再等满一遍超时。";
		}
		if (modal.Length > 0)
		{
			return "检测到 " + modal.Length + " 个模态对话框,它们挡住了 SE 主窗口,会把 COM 调用一起堵住。"
				+ "①推荐人工到 SE 界面按 texts/buttons 的内容处理掉;"
				+ "②确需代为关闭:solidedge-mcp --dialogs --close " + modal[0].Hwnd + " --confirm"
				+ "(只对 errors/报警类框这么做,关闭前先看 texts 与 buttons 的语义);"
				+ "③框没关掉之前,其它 se_* 工具调用都会卡在同一处。";
		}
		return "检测到 " + snap.Dialogs.Count + " 个非模态对话框(没挡住 SE 主窗口)。"
			+ "它们不一定堵住 COM 调用,但会让界面上的人为操作步骤变长;批量自动操作前建议先人工清场。";
	}
}

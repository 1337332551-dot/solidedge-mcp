using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// MCP 写操作审计日志。
/// 写 MCP 进程自己的日志文件(不复用 skill 的 access-log.md:两者是不同进程,且 skill 文件有只读属性约束)。
/// 路径:%LOCALAPPDATA%\SolidEdgeSpy\mcp-audit.log
/// </summary>
internal static class AuditLog
{
	private static readonly object Sync = new object();

	internal static string LogPath
	{
		get
		{
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeSpy", "mcp-audit.log");
		}
	}

	/// <summary>记录一次(或被拒绝的)写操作。</summary>
	internal static void Write(string tool, string objectId, string member, bool propertySet, string argsSummary, InvocationRisk risk, bool allowed, string note)
	{
		try
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ");
			stringBuilder.Append(allowed ? "ALLOW " : "DENY  ");
			stringBuilder.Append(tool).Append(' ');
			stringBuilder.Append("obj=").Append(objectId ?? "-").Append(' ');
			stringBuilder.Append("member=").Append(member ?? "-");
			if (propertySet)
			{
				stringBuilder.Append(" (propertySet)");
			}
			stringBuilder.Append(" risk=").Append(risk.ToString());
			if (!string.IsNullOrEmpty(argsSummary))
			{
				stringBuilder.Append(" args=").Append(argsSummary);
			}
			if (!string.IsNullOrEmpty(note))
			{
				stringBuilder.Append(" note=").Append(note);
			}
			string value = stringBuilder.ToString();
			lock (Sync)
			{
				string directoryName = Path.GetDirectoryName(LogPath);
				if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
				{
					Directory.CreateDirectory(directoryName);
				}
				File.AppendAllText(LogPath, value + Environment.NewLine, Encoding.UTF8);
			}
		}
		catch
		{
			// 审计失败绝不能影响主流程
		}
	}

	/// <summary>把参数数组压成一行摘要,避免把大对象塞进日志。</summary>
	internal static string SummarizeArgs(string[] args)
	{
		if (args == null || args.Length == 0)
		{
			return null;
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append('[');
		for (int i = 0; i < args.Length && i < 8; i++)
		{
			if (i > 0)
			{
				stringBuilder.Append(", ");
			}
			string text = args[i] ?? "(null)";
			if (text.Length > 40)
			{
				text = text.Substring(0, 40) + "...";
			}
			stringBuilder.Append(text);
		}
		if (args.Length > 8)
		{
			stringBuilder.Append(", ...共 ").Append(args.Length).Append(" 项");
		}
		stringBuilder.Append(']');
		return stringBuilder.ToString();
	}
}

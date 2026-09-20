using System;
using System.Reflection;

namespace SolidEdge.Spy.McpServer.Telemetry;

/// <summary>
/// 构建标识:csproj 注入 AssemblyMetadata(BuildTime),每次 dotnet build 都会刷新。
/// --version / 启动日志据此识别"跑的是不是新二进制"(P0-1:防旧构建假部署)。
/// 私有仓库主项目直接编译本文件;EventMcp 与开源仓库经 csproj 链接共享同一份源码。
/// </summary>
internal static class BuildInfo
{
	private const string MetadataKey = "BuildTime";

	private static string _stamp;

	internal static string Stamp
	{
		get
		{
			if (_stamp == null)
			{
				string found = null;
				try
				{
					foreach (AssemblyMetadataAttribute attr in typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
					{
						if (attr.Key == MetadataKey)
						{
							found = attr.Value;
							break;
						}
					}
				}
				catch
				{
				}
				_stamp = string.IsNullOrEmpty(found) ? "unknown" : found;
			}
			return _stamp;
		}
	}

	internal static string Describe(string productName)
	{
		Version version = typeof(BuildInfo).Assembly.GetName().Version;
		return productName + " v" + version + " (build " + Stamp + ")";
	}
}

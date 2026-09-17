using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.EventMcp
{
    /// <summary>
    /// Solid Edge 事件监视 MCP Server 入口(solidedge-event-mcp)。
    ///
    /// 与执行 MCP(solidedge-mcp)分工:
    /// - 执行 MCP:出站 COM 调用(查询/执行),BlockingCollection 式 STA,无消息泵;
    /// - 本 server:入站 COM 事件(Advise 常驻订阅),STA 消息泵收回调,
    ///   事件入环形缓冲,3 个工具供 AI 增量轮询。
    /// 两者独立进程、互不依赖,可各自重启。
    ///
    /// 无参数:MCP stdio 模式;--listen [秒]:CLI 自测模式(事件实时打印 JSON 行)。
    /// </summary>
    internal static class Program
    {
        internal static async Task<int> Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                return RunCli(args);
            }
            await RunServerAsync();
            return 0;
        }

        // ---------------- MCP stdio 模式 ----------------

        private static async Task RunServerAsync()
        {
            // ⚠️ 走自定义流传输后,SDK 不再代管 Console 改道,必须把 Console.Out 指向 stderr,
            // 否则宿主日志会混进 stdout 的 MCP 协议通道(协议只允许 JSON-RPC)。
            Console.SetOut(Console.Error);

            var builder = Host.CreateApplicationBuilder();

            // 日志走 stderr,不干扰 stdio 上的 MCP 协议(照抄执行 MCP 的配置)
            builder.Logging.AddDebug();
            builder.Logging.SetMinimumLevel(LogLevel.Information);

            // 传输挂 JsonRpcTap 旁路统计工具调用(source=event,与执行 MCP 共用同一份源码,
            // 落同一个 tool-usage.jsonl)。工具代码零改动。
            builder.Services
                .AddMcpServer()
                .WithStreamServerTransport(
                    new SolidEdge.Spy.McpServer.Telemetry.JsonRpcTap(Console.OpenStandardInput(), isRequestSide: true, source: "event"),
                    new SolidEdge.Spy.McpServer.Telemetry.JsonRpcTap(Console.OpenStandardOutput(), isRequestSide: false, source: "event"))
                .WithToolsFromAssembly();

            builder.Services.AddSingleton<EventHub>();

            var host = builder.Build();

            var hub = host.Services.GetRequiredService<EventHub>();
            var logger = host.Services.GetRequiredService<ILogger<EventHub>>();
            logger.LogInformation("Solid Edge 事件监视 MCP Server 启动中...");
            hub.Start();
            logger.LogInformation("监视就绪(SE 未运行也不报错,周期重试)。");

            try
            {
                await host.RunAsync();
            }
            finally
            {
                // stdin 断开宿主退出 → 干净退出,Unadvise 全部订阅
                hub.Shutdown();
            }
        }

        // ---------------- CLI 自测模式 ----------------

        private static int RunCli(string[] args)
        {
            if (args[0].Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }

            if (!args[0].Equals("--listen", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("未知参数: " + args[0]);
                PrintUsage();
                return 1;
            }

            int seconds = 60;
            if (args.Length > 1 && !int.TryParse(args[1], out seconds))
            {
                Console.WriteLine("秒数解析失败: " + args[1]);
                PrintUsage();
                return 1;
            }

            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var counts = new Dictionary<string, int>();
            var countLock = new object();

            var hub = new EventHub();
            hub.EventRecorded += r =>
            {
                Console.WriteLine(JsonSerializer.Serialize(r, jsonOptions));
                lock (countLock)
                {
                    string key = r.Source + "." + r.Event;
                    int c;
                    counts.TryGetValue(key, out c);
                    counts[key] = c + 1;
                }
            };

            Console.WriteLine("=== Solid Edge 事件监视 CLI ===");
            Console.WriteLine(seconds > 0
                ? "监听 " + seconds + " 秒后在 SE 中操作(改特征参数/保存/切文档...),Ctrl+C 可提前结束"
                : "持续监听,Ctrl+C 退出");
            hub.Start();

            if (!hub.IsConnected)
            {
                Console.WriteLine("(尚未连接 Solid Edge,每 5 秒自动重试;启动 SE 并打开文档后自动接入)");
            }

            // 订阅映射(诊断:哪个接口订阅成功/为何失败)
            Console.WriteLine("--- 订阅映射 ---");
            foreach (var s in hub.SubscriptionStatus)
            {
                Console.WriteLine(" " + (s.Result == "已订阅" ? "[OK]" : "[--]") +
                    " " + s.Interface.PadRight(30) + " ← " + s.Host + "  " + s.Result);
            }

            try
            {
                if (seconds > 0)
                {
                    Thread.Sleep(seconds * 1000);
                }
                else
                {
                    while (true) Thread.Sleep(500);
                }
            }
            catch (ThreadInterruptedException)
            {
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("=== 事件统计 ===");
                lock (countLock)
                {
                    if (counts.Count == 0) Console.WriteLine(" (未捕获到任何事件)");
                    foreach (var kv in counts)
                    {
                        Console.WriteLine(" " + kv.Key.PadRight(58) + kv.Value + " 次");
                    }
                }

                RingStats ring = hub.GetRingStats();
                Console.WriteLine(" 环形缓冲: " + ring.Buffered + "/" + ring.Capacity +
                    ",latestSeq=" + ring.LatestSeq + ",oldestSeq=" + ring.OldestSeq);
                Console.WriteLine("=== 退出(执行 Unadvise 清理)===");

                hub.Shutdown();
            }
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  solidedge-event-mcp.exe                MCP server 模式(stdio,由 AI 客户端拉起)");
            Console.WriteLine("  solidedge-event-mcp.exe --listen [秒]  CLI 自测模式(默认 60 秒;0=持续,Ctrl+C 退出)");
            Console.WriteLine("  solidedge-event-mcp.exe --help         本帮助");
        }
    }
}

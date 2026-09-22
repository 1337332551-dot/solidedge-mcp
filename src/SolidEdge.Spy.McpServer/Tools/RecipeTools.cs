using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// se_recipe_run:按名执行一条已登记的配方。
///
/// 设计要点(与 <see cref="RecipeSpec"/> 头注释配套):
///   - **执行核零复制**:渲染占位符后直接调 <see cref="InvokeTools.se_invoke_chain"/>,
///     护栏判定/占位符语义/$N 解析/句柄登记全部沿用同一份实现,不存在"两套解析漂移"。
///   - **只读**:本工具永不写配方文件。落盘由 AI/人直接写 JSON(配方是纯数据)。
///   - **护栏不可绕过**:配方里的 risk 字段只是提示;这里按成员名重算一次(并可列出具体的危险步骤),
///     chain 内部还会再判一次。含 Destructive 步骤时仍要求 confirm=true。
///   - **能力边界**(必须让调用方知道):① steps 建议 ≤9(>9 静态校验会警告);② $N 只能引用前序步骤的
///     返回值,拿不到 out 参数里的对象;③ **本通道不支持写属性**(propertySet),需要写属性的流程请用
///     se_invoke_member(propertySet=true) 分步执行。
/// </summary>
[McpServerToolType]
public static class RecipeTools
{
	[McpServerTool]
	[Description("按名称执行一条已登记的配方(配方=一个 JSON 文件,内含 steps/inputs/preconditions/verification)。配方文件必须已存在,本工具【不会】创建或修改配方。参数 args 形如 [\"setback=0.001\",\"target=obj-3\"](缺省取配方里 inputs 的 default)。起始对象:配方中第一个 type=\"object\" 的 input(通常名为 target);调用前请先用 se_get_selection 取得 obj-N(注意它会清空句柄表)。执行前会做三件事:静态校验(引用完整性/步数/状态一致性)、前置条件校验(docKind/handleExists/handleType/selectionNonEmpty)、参数注入({{inputName}} 纯文本占位符)。护栏:配方里若含破坏性成员(Delete/Cut/Drop/Erase/Purge/Remove*),会先被拒绝并【列出具体的危险步骤位置】,确认请追加 confirm=true;配方里的 risk 字段只是提示,以重算结果为准。能力边界:① 单条配方建议 ≤9 步(se_invoke_chain 的经验是 >10 步易超时);② \"$N\" 只能引用前序步骤的返回值,拿不到 out 参数里的对象(这类对象需用 se_invoke_member 单独取);③ 本通道【不支持写属性】(如 Visible/Depth),写属性请用 se_invoke_member(propertySet=true)。验收:配方里 verification 字段声明的判据(通常是 snapshotDiff 的 identical=true)不会自动执行,需你随后自行调用 se_snapshot_diff 核对。只读:不修改配方文件。")]
	public static string se_recipe_run(
		SolidEdgeContext context,
		[Description("配方名(= 配方文件名去掉 .json);搜索路径见环境变量 SE_MCP_RECIPES_DIR 与 %LOCALAPPDATA%\\SolidEdgeSpy\\recipes,可用 CLI --recipes 列出全部")] string name,
		[Description("可选,形如 [\"setback=0.001\",\"target=obj-3\"] 的键值对列表;未提供的 input 取配方里的 default,无 default 且 required 则报错")] string[] args = null,
		[Description("可选,配方含破坏性成员(Delete/Cut/Drop/Erase/Purge/Remove* 等)时需显式置 true,否则整条配方在执行前被拒绝(报错会列出具体的危险步骤位置)。默认 false")] bool confirm = false)
	{
		try
		{
			return context.Invoke(delegate
			{
				RecipeRef recipeRef = RecipeStore.Find(name);
				if (recipeRef == null)
				{
					return Error("找不到配方 \"" + (name ?? "(空)") + "\"。已搜索目录: " + string.Join(" ; ", RecipeStore.SearchDirs()) + "。可用 CLI `solidedge-mcp --recipes` 列出全部配方。");
				}

				string text;
				try
				{
					text = File.ReadAllText(recipeRef.Path);
				}
				catch (Exception ex)
				{
					return Error("读取配方文件失败(" + recipeRef.Path + "): " + ex.Message);
				}

				RecipeSpec spec;
				try
				{
					using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions
					{
						CommentHandling = JsonCommentHandling.Skip,
						AllowTrailingCommas = true
					});
					spec = RecipeSpecParser.Parse(doc.RootElement, recipeRef.Path);
				}
				catch (JsonException ex2)
				{
					return Error("配方 JSON 语法错误(" + recipeRef.Path + "): " + ex2.Message);
				}
				catch (ArgumentException ex3)
				{
					return Error("配方结构错误(" + recipeRef.Path + "): " + ex3.Message);
				}

				List<RecipeIssue> issues = RecipeValidator.Validate(spec);
				List<object> errors = new List<object>();
				List<object> warnings = new List<object>();
				foreach (RecipeIssue it in issues)
				{
					if (it.Severity == "error")
					{
						errors.Add(new { where = it.Where, message = it.Message });
					}
					else
					{
						warnings.Add(new { where = it.Where, message = it.Message });
					}
				}
				if (errors.Count > 0)
				{
					return JsonSerializer.Serialize(new
					{
						status = "error",
						recipe = spec.Name,
						sourcePath = recipeRef.Path,
						message = "配方未通过静态校验,已拒绝执行(未触碰 Solid Edge)。",
						errors = errors,
						warnings = warnings,
						hint = "可用 CLI `solidedge-mcp --recipe-validate " + (spec.Name ?? name) + "` 复现同样的校验结果。"
					}, JsonOpts());
				}

				Dictionary<string, string> provided;
				string argError = ParseArgs(args, out provided);
				if (argError != null)
				{
					return Error(argError);
				}

				Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
				List<object> missing = new List<object>();
				List<string> unknown = new List<string>();
				HashSet<string> declaredNames = new HashSet<string>(StringComparer.Ordinal);
				foreach (InputSpec inp in spec.Inputs)
				{
					if (string.IsNullOrWhiteSpace(inp.Name))
					{
						continue;
					}
					declaredNames.Add(inp.Name);
					string val;
					if (provided.TryGetValue(inp.Name, out val))
					{
						values[inp.Name] = val;
					}
					else if (inp.HasDefault)
					{
						values[inp.Name] = inp.Default;
					}
					else if (inp.Required)
					{
						missing.Add(new
						{
							name = inp.Name,
							type = inp.Type,
							unit = inp.Unit,
							desc = inp.Desc
						});
					}
				}
				foreach (KeyValuePair<string, string> kv in provided)
				{
					if (!declaredNames.Contains(kv.Key))
					{
						unknown.Add(kv.Key);
					}
				}
				if (missing.Count > 0)
				{
					return JsonSerializer.Serialize(new
					{
						status = "error",
						recipe = spec.Name,
						message = "缺少必填参数,已拒绝执行。",
						missing = missing,
						hint = "按 args 格式补充,如 [\"target=obj-1\",\"setback=0.001\"];也可在配方里给该 input 加 default。"
					}, JsonOpts());
				}

				// 起始对象:优先 args 里的 target,否则取第一个 object 类型的 input
				string startId = null;
				string startInputName = null;
				string targetVal;
				if (values.TryGetValue("target", out targetVal) && !string.IsNullOrWhiteSpace(targetVal))
				{
					startId = targetVal.Trim();
					startInputName = "target";
				}
				else
				{
					foreach (InputSpec inp2 in spec.Inputs)
					{
						string v2;
						if (string.Equals(inp2.Type, "object", StringComparison.OrdinalIgnoreCase)
							&& !string.IsNullOrWhiteSpace(inp2.Name)
							&& values.TryGetValue(inp2.Name, out v2)
							&& !string.IsNullOrWhiteSpace(v2))
						{
							startId = v2.Trim();
							startInputName = inp2.Name;
							break;
						}
					}
				}
				if (string.IsNullOrWhiteSpace(startId))
				{
					return Error("无法确定起始对象:配方需要声明一个 type=\"object\" 的 input(通常名为 target),并在 args 里传入 obj-N。传递前请先调用 se_get_selection 取得句柄(注意它会清空旧句柄表)。");
				}

				string preError = CheckPreconditions(context, spec, startId);
				if (preError != null)
				{
					return Error(preError);
				}

				JsonElement[] steps;
				string renderError;
				if (!RenderSteps(spec, values, out steps, out renderError))
				{
					return Error(renderError);
				}

				string riskError = CheckRecipeRisk(spec, steps, startId, confirm);
				if (riskError != null)
				{
					return riskError;
				}

				string chainJson = InvokeTools.se_invoke_chain(context, startId, steps, confirm);
				return Decorate(chainJson, spec, recipeRef, warnings, startInputName, startId);
			});
		}
		catch (Exception ex)
		{
			if (SolidEdgeContext.IsDisconnected(ex))
			{
				return Error("COM 对象已断连(文档可能已被关闭,或对象已被删除/句柄失效)。请重新调用 se_get_selection 获取新句柄,不要继续用旧 obj-N。原始错误: " + DescribeException(ex));
			}
			return Error("执行配方失败: " + DescribeException(ex));
		}
	}

	// ---------------- 参数 ----------------

	private static string ParseArgs(string[] args, out Dictionary<string, string> map)
	{
		map = new Dictionary<string, string>(StringComparer.Ordinal);
		if (args == null || args.Length == 0)
		{
			return null;
		}
		foreach (string raw in args)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}
			int idx = raw.IndexOf('=');
			if (idx <= 0)
			{
				return "参数格式错误: \"" + raw + "\"。应形如 \"名字=值\",例如 \"target=obj-3\" / \"setback=0.001\"。";
			}
			string key = raw.Substring(0, idx).Trim();
			string val = raw.Substring(idx + 1);
			if (key.Length == 0)
			{
				return "参数格式错误: \"" + raw + "\"(等号左边没有名字)。";
			}
			map[key] = val;
		}
		return null;
	}

	private static JsonSerializerOptions JsonOpts()
	{
		return new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
	}

	// ---------------- 渲染 ----------------

	/// <summary>把 {{inputName}} 占位符替换成实参(做 JSON 转义),产出与 se_invoke_chain 完全同构的 steps。</summary>
	private static bool RenderSteps(RecipeSpec spec, Dictionary<string, string> values, out JsonElement[] steps, out string error)
	{
		steps = null;
		error = null;
		JsonElement rawSteps;
		if (!spec.Raw.TryGetProperty("steps", out rawSteps) || rawSteps.ValueKind != JsonValueKind.Array)
		{
			error = "配方没有 steps 数组。";
			return false;
		}
		string text = rawSteps.GetRawText();
		// 值是"数据",必须按 JSON 字符串转义后再落到文本里,否则含引号/反斜杠的实参会破坏 JSON 结构。
		text = RecipeValidator.PlaceholderPattern.Replace(text, delegate (Match m)
		{
			string nm = m.Groups[1].Value;
			string v;
			if (values.TryGetValue(nm, out v))
			{
				return JsonEncodedText.Encode(v ?? "", JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
			}
			return m.Value;
		});
		Match leftover = RecipeValidator.PlaceholderPattern.Match(text);
		if (leftover.Success)
		{
			error = "步骤里仍有未解析的占位符 " + leftover.Value + "(该 input 没有值,也没有 default)。";
			return false;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(text);
			List<JsonElement> list = new List<JsonElement>();
			foreach (JsonElement e in doc.RootElement.EnumerateArray())
			{
				list.Add(e.Clone());
			}
			steps = list.ToArray();
			return true;
		}
		catch (JsonException ex)
		{
			error = "占位符替换后的 steps 不是合法 JSON(通常是实参里含特殊字符): " + ex.Message;
			return false;
		}
	}

	// ---------------- 前置条件 ----------------

	private static string CheckPreconditions(SolidEdgeContext context, RecipeSpec spec, string startId)
	{
		foreach (PreconditionSpec p in spec.Preconditions)
		{
			string kind = (p.Kind ?? "").Trim();
			if (kind.Length == 0)
			{
				continue;
			}
			string hint = string.IsNullOrWhiteSpace(p.Hint) ? "" : ("(" + p.Hint + ")");
			if (kind.Equals("docKind", StringComparison.OrdinalIgnoreCase))
			{
				string actual = TryGetDocTypeShort(context);
				if (actual == null)
				{
					return "前置条件校验失败: 读不到活动文档类型,无法校验 docKind=" + p.Value + hint + "。";
				}
				if ((p.Value ?? "").Length > 0 && actual.IndexOf(p.Value, StringComparison.OrdinalIgnoreCase) < 0)
				{
					return "前置条件不满足: 配方要求 " + p.Value + " 文档,当前活动文档是 " + actual + "。" + hint;
				}
			}
			else if (kind.Equals("handleExists", StringComparison.OrdinalIgnoreCase))
			{
				if (string.IsNullOrWhiteSpace(startId) || context.GetHandle(startId) == null)
				{
					return "前置条件不满足: 起始对象句柄 " + (startId ?? "(未提供)") + " 不在句柄表里" + hint + "。请先 se_get_selection(会清空并重建句柄表)再重跑配方。";
				}
			}
			else if (kind.Equals("handleType", StringComparison.OrdinalIgnoreCase))
			{
				ObjectHandle handle = string.IsNullOrWhiteSpace(startId) ? null : context.GetHandle(startId);
				if (handle == null)
				{
					return "前置条件不满足: 起始对象句柄 " + (startId ?? "(未提供)") + " 不存在,无法校验 handleType=" + p.Value + "。";
				}
				if ((p.Value ?? "").Length > 0 && (handle.TypeName ?? "").IndexOf(p.Value, StringComparison.OrdinalIgnoreCase) < 0)
				{
					return "前置条件不满足: 起始对象类型是 " + handle.TypeName + ",配方要求类型名含 \"" + p.Value + "\"" + hint + "。";
				}
			}
			else if (kind.Equals("selectionNonEmpty", StringComparison.OrdinalIgnoreCase))
			{
				if (TryGetSelectionCount(context) <= 0)
				{
					return "前置条件不满足: 需要先在 Solid Edge 中选中对象" + hint + "。";
				}
			}
		}
		return null;
	}

	/// <summary>读活动文档的 COM 类型短名(如 PartDocument / DraftDocument)。走 se_get_document,失败返回 null。</summary>
	private static string TryGetDocTypeShort(SolidEdgeContext context)
	{
		try
		{
			string json = DocumentTools.se_get_document(context);
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement docEl;
			if (doc.RootElement.TryGetProperty("document", out docEl) && docEl.ValueKind == JsonValueKind.Object)
			{
				JsonElement t;
				if (docEl.TryGetProperty("typeShort", out t) && t.ValueKind == JsonValueKind.String)
				{
					return t.GetString();
				}
				if (docEl.TryGetProperty("type", out t) && t.ValueKind == JsonValueKind.String)
				{
					return t.GetString();
				}
			}
		}
		catch
		{
		}
		return null;
	}

	/// <summary>读 ActiveSelectSet.Count。刻意【不】调 se_get_selection —— 那会清空句柄表,把这次配方要用的 obj-N 一起毁掉。</summary>
	private static int TryGetSelectionCount(SolidEdgeContext context)
	{
		try
		{
			object application = context.GetApplication();
			object selectSet = application.GetType().InvokeMember("ActiveSelectSet", BindingFlags.GetProperty, null, application, null);
			if (selectSet == null)
			{
				return 0;
			}
			object count = selectSet.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, selectSet, null);
			return (count is int) ? ((int)count) : 0;
		}
		catch
		{
			return 0;
		}
	}

	// ---------------- 配方级护栏 ----------------

	/// <summary>
	/// 按成员名重算整条配方的最高危级别。含 Destructive 且未 confirm 时返回错误(并列出具体步骤位置)。
	/// 配方把步骤藏在文件里,调用方的"确认"必须针对列出来的具体步骤,而不是配方名。
	/// </summary>
	private static string CheckRecipeRisk(RecipeSpec spec, JsonElement[] steps, string startId, bool confirm)
	{
		InvocationRisk worst = InvocationRisk.Normal;
		string worstMember = null;
		List<object> destructiveSpots = new List<object>();
		for (int i = 0; i < steps.Length; i++)
		{
			JsonElement memberEl;
			if (!steps[i].TryGetProperty("member", out memberEl) || memberEl.ValueKind != JsonValueKind.String)
			{
				continue;
			}
			string member = memberEl.GetString();
			InvocationRisk risk = Guardrail.Classify(member, false);
			if (risk > worst)
			{
				worst = risk;
				worstMember = member;
			}
			if (risk == InvocationRisk.Destructive)
			{
				string on = null;
				JsonElement onEl;
				if (steps[i].TryGetProperty("on", out onEl) && onEl.ValueKind == JsonValueKind.String)
				{
					on = onEl.GetString();
				}
				destructiveSpots.Add(new { step = i + 1, member = member, on = on });
			}
		}

		if (worst == InvocationRisk.Destructive && !confirm)
		{
			return JsonSerializer.Serialize(new
			{
				status = "error",
				recipe = spec.Name,
				message = "配方含破坏性步骤,已在执行前拒绝。确认请追加 confirm=true 再调用一次。",
				destructiveSteps = destructiveSpots,
				hint = "这些步骤来自配方文件而非本次对话,请先核对上面列出的具体步骤(成员名/作用对象)再决定是否确认。"
			}, JsonOpts());
		}

		// 2026-09-14 台账改造：原先只在 worst != Normal 时写审计 → 纯 Normal 配方（全读类）在
		// 通道 A 里完全不可见，台账只能靠 B 手记（方案 §5.6 的"纯 Normal 全空"缺口）。
		// 现在总是写，并把 verification 声明写进 note。
		// ⚠️ note 只说明"判据是否声明"，**不代表判据结果**——配方不声明快照 target，
		// 判据无法在此自动执行，仍需按 recipes/README §五 的 save→run→diff 流程验收。
		string verifyNote = string.IsNullOrWhiteSpace(spec.VerifyName)
			? "verification=none"
			: "verification=declared:" + spec.VerifyKind + "/" + spec.VerifyName + "/" + spec.VerifyExpect;
		AuditLog.Write("se_recipe_run", startId, "(recipe:" + (spec.Name ?? "?") + ")", false,
			steps.Length + " steps, worst=" + worstMember, worst, confirm, verifyNote);
		return null;
	}

	// ---------------- 输出 ----------------

	/// <summary>在 se_invoke_chain 的结果上补配方元信息(不改动它的任何字段)。</summary>
	private static string Decorate(string chainJson, RecipeSpec spec, RecipeRef recipeRef, List<object> warnings, string startInputName, string startId)
	{
		try
		{
			JsonNode node = JsonNode.Parse(chainJson);
			JsonObject obj = node as JsonObject;
			if (obj == null)
			{
				return chainJson;
			}
			obj["recipe"] = spec.Name;
			obj["recipeSource"] = recipeRef.Path;
			obj["recipeStatus"] = spec.Status;
			obj["startObject"] = startId;
			if (startInputName != null && startInputName != "target")
			{
				obj["startInput"] = startInputName;
			}
			if (warnings != null && warnings.Count > 0)
			{
				obj["recipeWarnings"] = JsonSerializer.SerializeToNode(warnings, JsonOpts());
			}
			if (!string.IsNullOrWhiteSpace(spec.VerifyName))
			{
				obj["verification"] = new JsonObject
				{
					["kind"] = spec.VerifyKind,
					["name"] = spec.VerifyName,
					["expect"] = spec.VerifyExpect,
					["hint"] = "配方声明的验收判据,不会自动执行。请自行调用 se_snapshot_diff 核对(expect=" + (spec.VerifyExpect ?? "?") + " 即通过)。"
				};
			}
			return obj.ToJsonString(JsonOpts());
		}
		catch
		{
			// 装饰失败不影响结果本身
			return chainJson;
		}
	}

	// ---------------- CLI 入口(不是 MCP 工具:不碰 COM、不依赖常驻会话,故走 CLI 省工具额度) ----------------

	/// <summary>CLI `--recipes`:列出全部配方(名称/状态/锚定版本/参数/来源目录)并顺带做一次静态校验。纯本地。</summary>
	internal static string FormatRecipeList()
	{
		List<RecipeRef> refs = RecipeStore.List();
		StringBuilder sb = new StringBuilder();
		sb.Append("=== 配方清单 (").Append(refs.Count).Append(") ===").Append(Environment.NewLine);
		sb.Append("搜索路径: ").Append(string.Join(" ; ", RecipeStore.SearchDirs())).Append(Environment.NewLine);
		if (refs.Count == 0)
		{
			sb.Append("(没有找到配方文件。把 <配方名>.json 放进上面的任一目录即可;以 _ 开头的文件不登记。)").Append(Environment.NewLine);
			return sb.ToString();
		}
		sb.Append(Environment.NewLine);
		foreach (RecipeRef r in refs)
		{
			string status = "?";
			string title = null;
			string anchor = null;
			string inputs = null;
			int steps = 0;
			List<string> errs = new List<string>();
			try
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(r.Path), new JsonDocumentOptions
				{
					CommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true
				});
				RecipeSpec spec = RecipeSpecParser.Parse(doc.RootElement, r.Path);
				status = spec.Status;
				title = spec.Title;
				anchor = spec.AnchorSe;
				steps = spec.Steps.Count;
				List<string> ps = new List<string>();
				foreach (InputSpec i in spec.Inputs)
				{
					if (string.IsNullOrWhiteSpace(i.Name))
					{
						continue;
					}
					string mark = (i.HasDefault ? ("=" + i.Default) : (i.Required ? "(必填)" : ""));
					ps.Add(i.Name + mark);
				}
				inputs = string.Join(", ", ps);
				foreach (RecipeIssue it in RecipeValidator.Validate(spec))
				{
					if (it.Severity == "error")
					{
						errs.Add(it.Where + ": " + it.Message);
					}
				}
			}
			catch (Exception ex)
			{
				errs.Add("解析失败: " + ex.Message);
			}
			sb.Append("- ").Append(r.Name).Append("  [").Append(status).Append(']');
			if (title != null)
			{
				sb.Append("  ").Append(title);
			}
			sb.Append(Environment.NewLine);
			sb.Append("    步骤数=").Append(steps);
			if (anchor != null)
			{
				sb.Append("  锚定 SE=").Append(anchor);
			}
			sb.Append(Environment.NewLine);
			if (!string.IsNullOrEmpty(inputs))
			{
				sb.Append("    参数: ").Append(inputs).Append(Environment.NewLine);
			}
			sb.Append("    文件: ").Append(r.Path).Append(Environment.NewLine);
			if (errs.Count > 0)
			{
				sb.Append("    [校验错误] ").Append(string.Join(" | ", errs)).Append(Environment.NewLine);
			}
		}
		return sb.ToString();
	}

	/// <summary>CLI `--recipe-validate &lt;名字|路径&gt;`:静态校验(不碰 COM,SE 没开也能跑)。</summary>
	internal static string ValidateForCli(string nameOrPath)
	{
		if (string.IsNullOrWhiteSpace(nameOrPath))
		{
			return "用法: solidedge-mcp --recipe-validate <配方名|json 路径>";
		}
		RecipeRef r = RecipeStore.Find(nameOrPath);
		if (r == null)
		{
			return "找不到配方 \"" + nameOrPath + "\"。搜索路径: " + string.Join(" ; ", RecipeStore.SearchDirs()) + "。可用 --recipes 查看全部。";
		}
		string text;
		try
		{
			text = File.ReadAllText(r.Path);
		}
		catch (Exception ex)
		{
			return "读取失败(" + r.Path + "): " + ex.Message;
		}
		RecipeSpec spec;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions
			{
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true
			});
			spec = RecipeSpecParser.Parse(doc.RootElement, r.Path);
		}
		catch (JsonException jex)
		{
			return "JSON 语法错误: " + jex.Message;
		}
		catch (ArgumentException aex)
		{
			return "结构错误: " + aex.Message;
		}

		List<RecipeIssue> issues = RecipeValidator.Validate(spec);
		int errN = 0;
		int warnN = 0;
		StringBuilder sb = new StringBuilder();
		sb.Append("=== 配方校验: ").Append(spec.Name ?? r.Name).Append(" ===").Append(Environment.NewLine);
		sb.Append("文件: ").Append(r.Path).Append(Environment.NewLine);
		sb.Append("状态: ").Append(spec.Status).Append("  步骤数: ").Append(spec.Steps.Count).Append(Environment.NewLine);
		foreach (RecipeIssue it in issues)
		{
			if (it.Severity == "error")
			{
				errN++;
			}
			else
			{
				warnN++;
			}
			sb.Append('[').Append(it.Severity).Append("] ").Append(it.Where).Append(": ").Append(it.Message).Append(Environment.NewLine);
		}
		sb.Append(new string('-', 60)).Append(Environment.NewLine);
		sb.Append((errN == 0) ? "通过(errors=0" : ("不通过(errors=" + errN));
		sb.Append(", warnings=").Append(warnN).Append(")。");
		if (errN > 0)
		{
			sb.Append(" -> 该配方会被 se_recipe_run 拒绝执行。");
		}
		return sb.ToString();
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new
		{
			status = "error",
			message = message
		}, JsonOpts());
	}

	private static string DescribeException(Exception ex)
	{
		if (ex == null)
		{
			return "(未知错误)";
		}
		StringBuilder stringBuilder = new StringBuilder();
		for (Exception exception = ex; exception != null; exception = exception.InnerException)
		{
			if (stringBuilder.Length > 0)
			{
				stringBuilder.Append(" <- 内部: ");
			}
			stringBuilder.Append(exception.GetType().Name).Append(": ").Append(exception.Message);
			try
			{
				if (exception.HResult != 0 && exception.HResult != -2146232828)
				{
					stringBuilder.Append(" (HRESULT=0x").Append(exception.HResult.ToString("X8")).Append(')');
				}
			}
			catch
			{
			}
		}
		return stringBuilder.ToString();
	}
}

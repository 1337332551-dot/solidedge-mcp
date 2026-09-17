using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Server;
using SolidEdge.Spy.InteropServices;
using SolidEdgeFramework;

namespace SolidEdge.Spy.McpServer.Tools;

[McpServerToolType]
public static class SelectionTools
{
	private static int _typeLibsLoaded;

	[McpServerTool]
	[Description("获取用户在 Solid Edge 中当前选中的对象列表。用户说\"查SE\"\"用se查\"\"看看选了什么\"\"我选中了什么\"等需查询当前选中对象时,优先调用此工具获取对象编号;若 AI 在处理任务中需访问 Solid Edge 当前选中对象,也应调用此工具。会清空之前的句柄表,为每个选中对象分配编号(obj-1, obj-2...),返回类型和名称。若选择集为空但存在活动文档,则回退把 ActiveDocument 登记为 obj-1(结果带 fallback=\"ActiveDocument\" 标记),AI 可从文档层(Variables/Properties/特征集合)继续 invoke 查询材质、重量、变量表、特征树等,无需用户先选中;若需操作具体几何/特征对象,仍请用户在 SE 中选中后再调用本工具。调用后若需路径/详情,再调 se_find_paths 或 se_describe_object;后续调用必须在同一 MCP 会话进程内连续完成——不要改用命令行 exe 查询,CLI 每次是独立进程,句柄表不共享。单位约定:Solid Edge API 返回的长度/距离/坐标值为内部单位米(m),UI 通常显示毫米(mm),换算 ×1000;角度为弧度。")]
	public static string se_get_selection(SolidEdgeContext context)
	{
		try
		{
			return context.Invoke(delegate
			{
				Application application = context.GetApplication();
				object obj = SafeGetProperty(application, "ActiveSelectSet");
				if (obj == null)
				{
					return Error("无法获取 ActiveSelectSet。");
				}
				int num = SafeGetCount(obj);
				if (num == 0)
				{
					context.ClearHandles();
					object obj2 = SafeGetProperty(application, "ActiveDocument");
					if (obj2 != null)
					{
						string typeShort = "(未知)";
						string text = "(未知)";
						ComPtr comPtr = null;
						try
						{
							comPtr = ComPtr.FromRCW(obj2);
							ComTypeInfo comTypeInfo = comPtr.TryGetComTypeInfo();
							if (comTypeInfo != null)
							{
								typeShort = comTypeInfo.Name;
								text = comTypeInfo.FullName;
							}
						}
						catch
						{
						}
						try
						{
							comPtr?.Dispose();
						}
						catch
						{
						}
						string text2 = (SafeGetProperty(obj2, "Name") as string) ?? "(无名称)";
						string id = context.AddHandle(obj2, text, text2);
						return JsonSerializer.Serialize(new
						{
							status = "ok",
							count = 1,
							items = new object[1]
							{
								new
								{
									id = id,
									type = text,
									typeShort = typeShort,
									name = text2
								}
							},
							fallback = "ActiveDocument",
							hint = "SelectSet 为空,已回退把活动文档登记为 obj-1 作为查询起点。可对它 invoke Variables/Properties/DesignEdgebarFeatures 等继续查询;若需操作具体几何/特征对象,请在 Solid Edge 中选中后再调用本工具。"
						});
					}
					return JsonSerializer.Serialize(new
					{
						status = "ok",
						count = 0,
						items = Array.Empty<object>(),
						hint = "SelectSet 为空且无活动文档,请先在 Solid Edge 中打开文档(可选:选中对象)。"
					});
				}
				context.ClearHandles();
				List<object> list = new List<object>();
				for (int i = 1; i <= num; i++)
				{
					object obj5 = SafeGetItem(obj, i);
					if (obj5 != null)
					{
						string typeShort2 = "(未知)";
						string text3 = "(未知)";
						try
						{
							ComTypeInfo comTypeInfo2 = ComPtr.FromRCW(obj5).TryGetComTypeInfo();
							if (comTypeInfo2 != null)
							{
								typeShort2 = comTypeInfo2.Name;
								text3 = comTypeInfo2.FullName;
							}
						}
						catch
						{
						}
						string text4 = (SafeGetProperty(obj5, "Name") as string) ?? "(无名称)";
						string id2 = context.AddHandle(obj5, text3, text4);
						list.Add(new
						{
							id = id2,
							type = text3,
							typeShort = typeShort2,
							name = text4
						});
					}
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					count = list.Count,
					items = list
				});
			});
		}
		catch (Exception ex)
		{
			return Error("获取选择集失败: " + ex.Message);
		}
	}

	private static object SafeGetProperty(object obj, string name)
	{
		try
		{
			return obj.GetType().InvokeMember(name, BindingFlags.GetProperty, null, obj, null);
		}
		catch
		{
			return null;
		}
	}

	private static int SafeGetCount(object collection)
	{
		try
		{
			object obj = collection.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, collection, null);
			return (obj is int) ? ((int)obj) : 0;
		}
		catch
		{
			return 0;
		}
	}

	private static object SafeGetItem(object collection, int index)
	{
		try
		{
			return collection.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, collection, new object[1] { index });
		}
		catch
		{
			return null;
		}
	}

	private static string Error(string message)
	{
		return JsonSerializer.Serialize(new
		{
			status = "error",
			message = message
		});
	}

	[McpServerTool]
	[Description("查找对象在 Solid Edge 对象模型中的访问路径,返回从 Application 出发的可达路径(如 'Application.ActiveDocument.Sheets.Item(\"39339\").Lines2d.Item(12)'),可直接写进程序代码。同一对象可能有多条可达路径,全部列出。两种用法二选一:①传 objectId=查选中对象(se_get_selection 返回的 obj-N);②传 name=按对象 Name/Key 全局反查(如 name=\"38400\"、name=\"Dimension 38102\"),不需要用户选中——适用于'知道名字不知道挂在哪'的场景(如数字名的视图编辑上下文 sheet)。单位约定:SE 内部长度单位为米(UI 显示 mm,×1000)。")]
	public static string se_find_paths(SolidEdgeContext context, [Description("对象编号,如 obj-1(se_get_selection 返回);与 name 二选一")] string objectId = null, [Description("按对象 Name/Key 全局反查路径,如 \"38400\" 或 \"Circle2d 38400\";与 objectId 二选一")] string name = null)
	{
		try
		{
			bool byName = !string.IsNullOrWhiteSpace(name);
			if (string.IsNullOrWhiteSpace(objectId) && !byName)
			{
				return Error("objectId 和 name 至少提供一个。①查选中对象传 objectId(先 se_get_selection);②按名字全局反查传 name(如 name=\"38400\")。");
			}
			return context.Invoke(delegate
			{
				object activeDocForSearch = context.GetApplication()?.ActiveDocument;
				if (activeDocForSearch == null)
				{
					return Error("当前没有活动文档,无法搜索。");
				}
				EnsureTypeLibrariesLoaded();
				ObjectExplorer objectExplorer = new ObjectExplorer(50000, 10, 20, 30);

				if (byName)
				{
					string target = name.Trim();
					if (target.StartsWith("obj-", StringComparison.OrdinalIgnoreCase))
					{
						return Error("name 参数应传对象名字(如 \"38400\"),不是句柄编号;查句柄请用 objectId 参数。");
					}
					// 从 ActiveDocument 起扫:目标对象基本都在文档子树(含 Sheets 里的视图编辑上下文)
					List<string> listByName = objectExplorer.FindByName(activeDocForSearch, "Application.ActiveDocument", target);
					return JsonSerializer.Serialize(new
					{
						status = "ok",
						searchBy = "name",
						name = target,
						pathsFound = listByName.Count,
						paths = listByName,
						truncated = objectExplorer.TimedOut,
						hint = (listByName.Count == 0
							? (objectExplorer.TimedOut
								? "30 秒遍历预算用尽,可能在大文档深处。建议用 se_walk_object 逐层下钻缩小范围后重试。"
								: "未找到 Name/Key 等于该名字的对象。注意名字区分大小写不敏感但需完全相等;模糊查找可去掉前后缀重试(如只传数字部分)。")
							: (objectExplorer.TimedOut ? "已找到路径,但遍历超时提前停止,可能还有更多路径未列出。" : null))
					});
				}

				ObjectHandle handle = context.GetHandle(objectId);
				if (handle == null)
				{
					return Error("找不到对象编号 " + objectId + "。请先在本会话内调用 se_get_selection 获取选中对象编号。注意:句柄表存在 MCP server 进程内存中,se_get_selection 与后续工具必须在同一 MCP 会话进程内连续调用。若刚通过命令行 exe 调用过:CLI 每次是独立进程、句柄不共享,请改用 MCP 工具调用。也可以改用 name 参数按对象名字全局反查(无需选中)。");
				}
				object activeDocument = context.GetApplication().ActiveDocument;
				if (activeDocument == null)
				{
					return Error("当前没有活动文档。");
				}
				List<string> list = objectExplorer.FindPaths(activeDocument, "Application.ActiveDocument", handle.IUnknownPtr, handle.TypeName);
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					objectId = objectId,
					objectType = handle.TypeName,
					objectName = handle.DisplayName,
					pathsFound = list.Count,
					paths = list,
					truncated = objectExplorer.TimedOut,
					hint = (objectExplorer.TimedOut ? "遍历超出 30 秒时间预算已提前停止。可改用 se_walk_object 按路径逐层探索(快)。" : null)
				});
			});
		}
		catch (Exception ex)
		{
			return Error("查找路径失败: " + ex.Message);
		}
	}

	private static void EnsureTypeLibrariesLoaded()
	{
		if (Interlocked.CompareExchange(ref _typeLibsLoaded, 1, 0) == 0)
		{
			try
			{
				Version version = new Version(1, 0);
				ComTypeManager instance = ComTypeManager.Instance;
				instance.LoadRegTypeLib(TypeLibGuid.RevisionManager, version);
				instance.LoadRegTypeLib(TypeLibGuid.SEInstallDataLib, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeAssembly, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeConstants, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeDraft, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeFileProperties, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeFramework, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeFrameworkSupport, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgeGeometry, version);
				instance.LoadRegTypeLib(TypeLibGuid.SolidEdgePart, version);
				instance.LoadRegTypeLib(TypeLibGuid.StructureEditor, version);
			}
			catch
			{
			}
		}
	}

	[McpServerTool]
	[Description("详细描述选中对象(obj-N):列出它实现的全部接口/类、所有属性(含当前实时值)、所有方法(含签名)。同一 COM 对象可能实现多个接口,每个接口的成员都分别列出。属性除 name/type/value 外附:readable(本次是否读到值)、enumType/enumConstant(VT_USERDEFINED 裸数字翻译成的枚举类型名与常量名;没匹配到常量时 enumType 仍会给出)、unitNote(长度类附毫米、角度类附度)。readable=false 的属性读它会抛异常,名字见 unreadableProperties,不要反复尝试。方法列表已过滤 AddRef/Release/QueryInterface/GetIDsOfNames/GetTypeInfo/GetTypeInfoCount/Invoke 等 IDispatch 底层方法。注意:数值均为 SE 内部单位——长度米(UI 显示 mm,×1000)、角度弧度;枚举并非全部可解码,enumConstant 为 null 时请查离线 SDK 文档,不要猜。")]
	public static string se_describe_object(SolidEdgeContext context, [Description("对象编号,如 obj-1")] string objectId, [Description("可选,\"summary\"=精简模式:只返回可读属性(最多 60 个,仅 name/type/value)+方法名列表,适合先概览再下钻;默认 \"full\"=全量(含 readable/enumType/enumConstant/unitNote 与完整方法签名)")] string mode = null)
	{
		try
		{
			return context.Invoke(delegate
			{
				ObjectHandle handle = context.GetHandle(objectId);
				if (handle == null)
				{
					return Error("找不到对象编号 " + objectId + "。请先在本会话内调用 se_get_selection 获取选中对象编号。注意:句柄表存在 MCP server 进程内存中,se_get_selection 与后续工具必须在同一 MCP 会话进程内连续调用。若刚通过命令行 exe 调用过:CLI 每次是独立进程、句柄不共享,请改用 MCP 工具调用。");
				}
				EnsureTypeLibrariesLoaded();
				object comObject = handle.ComObject;
				if (comObject == null)
				{
					return Error("对象已失效。请重新调 se_get_selection 获取选中对象。");
				}
				ComPtr comPtr = ComPtr.FromRCW(comObject);
				ComTypeInfo comTypeInfo = comPtr.TryGetComTypeInfo();
				bool flag = handle.TypeName != null && handle.TypeName.IndexOf("Dimension", StringComparison.OrdinalIgnoreCase) >= 0;
				bool summary = string.Equals(mode, "summary", StringComparison.OrdinalIgnoreCase);
				List<object> properties = EnumerateProperties(comObject, comTypeInfo, flag, summary, out List<string> unreadableNames);
				if (summary && properties.Count > 60)
				{
					properties = properties.GetRange(0, 60);
				}
				return JsonSerializer.Serialize(new
				{
					status = "ok",
					objectId = objectId,
					objectType = handle.TypeName,
					objectName = handle.DisplayName,
					defaultInterface = (comTypeInfo?.FullName ?? "(未知)"),
					unitsNote = "Solid Edge API 返回的长度/距离值为内部单位米(m);UI 通常显示毫米(mm),换算 ×1000;角度值为弧度(rad)。长度类/角度类属性另见各自的 unitNote。",
					interfaces = EnumerateInterfaces(comPtr),
					properties = properties,
					propertiesSummary = new
					{
						total = properties.Count,
						unreadable = unreadableNames.Count,
						note = (summary
							? "精简模式:只列可读属性(最多 60 个,仅 name/type/value),方法仅名字;读不到的属性名仍在 unreadableProperties。要全量(含 readable/enumType/enumConstant/unitNote 与完整签名)请 mode=\"full\"。"
							: "readable=false 的属性本次读取失败(通常是不适用于当前对象状态),清单见 unreadableProperties;VT_USERDEFINED 属性已尽量附 enumType/enumConstant;长度/角度类数值见 unitNote。")
					},
					unreadableProperties = unreadableNames,
					methods = (summary ? ((object)EnumerateMethodNames(comTypeInfo)) : ((object)EnumerateMethods(comTypeInfo)))
				});
			});
		}
		catch (Exception ex)
		{
			if (SolidEdgeContext.IsDisconnected(ex))
			{
				return Error("COM 对象已断连(文档可能已被关闭,或对象已被删除)。请重新调用 se_get_selection 获取新句柄。原始错误: " + ex.Message);
			}
			return Error("描述对象失败: " + ex.Message);
		}
	}

	private static List<object> EnumerateInterfaces(ComPtr comPtr)
	{
		List<object> list = new List<object>();
		try
		{
			nint num = (nint)comPtr;
			if (num == IntPtr.Zero)
			{
				return list;
			}
			ComTypeLibrary[] comTypeLibraries = ComTypeManager.Instance.ComTypeLibraries;
			for (int i = 0; i < comTypeLibraries.Length; i++)
			{
				ComTypeInfo[] comTypeInfos = comTypeLibraries[i].ComTypeInfos;
				foreach (ComTypeInfo comTypeInfo in comTypeInfos)
				{
					if (!comTypeInfo.IsInterface && !comTypeInfo.IsDispatch)
					{
						continue;
					}
					try
					{
						Guid iid = comTypeInfo.Guid;
						nint ppv = IntPtr.Zero;
						if (Marshal.QueryInterface(num, in iid, out ppv) == 0)
						{
							list.Add(new
							{
								guid = iid.ToString(),
								name = comTypeInfo.FullName
							});
							Marshal.Release(ppv);
						}
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	/// <summary>
	/// 枚举属性。除 name/type/value 外补充:
	/// - readable:本次是否真的读到了值(false = 该属性不适用于当前对象状态,读它会抛 TargetInvocationException)
	/// - enumType / enumConstant:VT_USERDEFINED 的裸数字翻译成枚举类型名与常量名
	/// - unitNote:长度类/角度类数值的单位换算提示(内部单位米/弧度)
	/// summary=true 时只输出可读属性的 name/type/value(精简模式),但 unreadableNames 始终收集。
	/// </summary>
	private static List<object> EnumerateProperties(object target, ComTypeInfo typeInfo, bool isDimension, bool summary, out List<string> unreadableNames)
	{
		List<object> list = new List<object>();
		unreadableNames = new List<string>();
		if (typeInfo == null)
		{
			return list;
		}
		ComPropertyInfo[] properties = typeInfo.GetProperties(includeInherited: true);
		foreach (ComPropertyInfo comPropertyInfo in properties)
		{
			string name = comPropertyInfo.Name;
			if (ComSideEffectGuard.IsBlocked(name))
			{
				continue;
			}
			string type = "(未知)";
			ComParameterInfo returnParameter = null;
			try
			{
				returnParameter = comPropertyInfo.GetFunction?.ReturnParameter;
				if (returnParameter != null)
				{
					type = returnParameter.VariantType.ToString();
				}
			}
			catch
			{
			}
			string text = "(无法读取)";
			string enumType = null;
			string enumConstant = null;
			string unitNote = null;
			bool readable = false;
			try
			{
				object value = target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, null);
				readable = true;
				text = ((value == null) ? "(null)" : value.ToString());
				if (returnParameter != null)
				{
					ComEnumHelper.TryDecodeEnum(returnParameter, value, out enumType, out enumConstant);
				}
				unitNote = FormatUnitNote(name, value, isDimension);
			}
			catch (Exception ex)
			{
				text = "(读取失败: " + ex.GetType().Name + ")";
				unreadableNames.Add(name);
			}
			if (summary)
			{
				// 精简模式:读不到的不进列表(名字已在 unreadableNames),可读的只给三项
				if (readable)
				{
					list.Add(new { name = name, type = type, value = text });
				}
			}
			else
			{
				list.Add(new
				{
					name = name,
					type = type,
					value = text,
					readable = readable,
					enumType = enumType,
					enumConstant = enumConstant,
					unitNote = unitNote
				});
			}
		}
		return list;
	}

	/// <summary>给数值属性附单位提示:长度类附毫米,角度类附度。无法判定语义时返回 null(宁可不标,不可误标)。</summary>
	private static string FormatUnitNote(string name, object value, bool isDimension)
	{
		if (!(value is double d) || double.IsNaN(d) || double.IsInfinity(d))
		{
			return null;
		}
		if (IsAngleName(name))
		{
			// 超出 2π 的多半是哨兵值(如 TrackAngle=-99),换算成度没有意义
			if (Math.Abs(d) <= 6.283185307179586)
			{
				return "角度:弧度 = " + (d * 180.0 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + " 度";
			}
			return null;
		}
		if (IsLengthName(name) || (isDimension && name.Equals("Value")))
		{
			return "长度:内部单位米 = " + (d * 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " mm";
		}
		return null;
	}

	private static bool IsLengthName(string name)
	{
		return name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Depth", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Width", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Length", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Radius", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Diameter", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Thickness", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Offset", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Size", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Tolerance", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Crop", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Extent", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Margin", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Gap", StringComparison.OrdinalIgnoreCase) >= 0
			|| name.IndexOf("Spacing", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool IsAngleName(string name)
	{
		return name.IndexOf("Angle", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	/// <summary>IDispatch 自带的底层方法,对 AI 无业务语义且占篇幅,统一过滤。</summary>
	private static readonly HashSet<string> DispatchNoiseMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"AddRef",
		"Release",
		"QueryInterface",
		"GetIDsOfNames",
		"GetTypeInfo",
		"GetTypeInfoCount",
		"Invoke"
	};

	private static List<object> EnumerateMethods(ComTypeInfo typeInfo)
	{
		List<object> list = new List<object>();
		if (typeInfo == null)
		{
			return list;
		}
		ComFunctionInfo[] methods = typeInfo.GetMethods(includeInherited: true);
		foreach (ComFunctionInfo comFunctionInfo in methods)
		{
			if (DispatchNoiseMethods.Contains(comFunctionInfo.Name))
			{
				continue;
			}
			string signature = comFunctionInfo.ToString(includeParameters: true);
			list.Add(new
			{
				name = comFunctionInfo.Name,
				signature = signature
			});
		}
		return list;
	}

	/// <summary>精简模式用:只返回方法名列表(已过滤 IDispatch 噪音),省掉完整签名。</summary>
	private static List<string> EnumerateMethodNames(ComTypeInfo typeInfo)
	{
		List<string> list = new List<string>();
		if (typeInfo == null)
		{
			return list;
		}
		ComFunctionInfo[] methods = typeInfo.GetMethods(includeInherited: true);
		foreach (ComFunctionInfo comFunctionInfo in methods)
		{
			if (DispatchNoiseMethods.Contains(comFunctionInfo.Name))
			{
				continue;
			}
			list.Add(comFunctionInfo.Name);
		}
		return list;
	}
}

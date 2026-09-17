using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using SolidEdge.Spy.InteropServices;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// 对象"身份指纹"助手(供 se_batch_read / se_snapshot_diff 复用)。
///
/// 为什么需要它:Solid Edge 每次重建对象(重新标注、重新生成向心线)连对象 ID 都会变
/// (实测 35221 → 30634),幂等判据与回归对比都不能依赖 obj-N / Name / Key。本助手给两个层次的身份:
/// - Identity      :本次生命周期内的身份(类型 + Name + Key) —— 判"是不是同一个对象";
/// - GeometricKey  :与 ID 无关的几何身份(线段中点+方向 / 圆心+半径 / 包围盒) —— 重建后依然一致,
///                   是幂等判据与"改前改后"对比的可靠依据。
///
/// 全部读取走 try/catch 的反射 GET,读不到返回 null,绝不抛给调用方。
/// 数值统一用不变文化格式化,保证同一几何在不同调用下字符串逐字一致。
/// </summary>
internal static class ObjectFingerprint
{
	public static string TypeName(object obj)
	{
		if (obj == null) return null;
		// 注意:RCW 的 GetType().Name 只能拿到 "__ComObject",真实 COM 类型名必须走 TypeInfo
		// (实测踩过:用它判"当前文档是不是 DraftDocument"会永远失败)
		EnsureTypeLibrariesLoaded();
		ComPtr ptr = null;
		try
		{
			ptr = ComPtr.FromRCW(obj);
			ComTypeInfo ti = ptr.TryGetComTypeInfo();
			if (ti != null)
			{
				if (!string.IsNullOrEmpty(ti.FullName)) return ti.FullName;
				if (!string.IsNullOrEmpty(ti.Name)) return ti.Name;
			}
		}
		catch
		{
		}
		finally
		{
			try { if (ptr != null) ptr.Dispose(); } catch { }
		}
		try { return obj.GetType().Name; } catch { return null; }
	}

	/// <summary>确保类型库已加载(TypeInfo 查询的前提)。各工具文件各自持有一份,是本仓库的既有约定。</summary>
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

	private static int _typeLibsLoaded;

	public static string DisplayName(object obj)
	{
		object v = TryGet(obj, "Name");
		if (v == null) v = TryGet(obj, "Key");
		return (v == null) ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
	}

	public static string Key(object obj)
	{
		object v = TryGet(obj, "Key");
		return (v == null) ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
	}

	/// <summary>本次生命周期内的身份:类型|Name|Key。</summary>
	public static string Identity(object obj)
	{
		if (obj == null) return null;
		return TypeName(obj) + "|" + (DisplayName(obj) ?? "?") + "|" + (Key(obj) ?? "?");
	}

	/// <summary>
	/// 与对象 ID 无关的几何身份。优先"线段(中点+规范化方向)" → "圆(圆心+半径)" → "包围盒"。
	/// 取不到几何特征时返回 null(调用方应如实报告"该对象无几何指纹")。
	/// </summary>
	public static string GeometricKey(object obj)
	{
		if (obj == null) return null;

		double[] sp = TryPoint(obj, "GetStartPoint");
		double[] ep = TryPoint(obj, "GetEndPoint");
		if (sp != null && ep != null)
		{
			double x1 = sp[0], y1 = sp[1], x2 = ep[0], y2 = ep[1];
			double dx = x2 - x1;
			double dy = y2 - y1;
			// 规范化方向:让 (dx,dy) 落在右半平面(或正上方),使同一条线正反两种端点顺序指纹一致
			if (dx < 0 || (Math.Abs(dx) < 1e-12 && dy < 0))
			{
				double t = x1; x1 = x2; x2 = t;
				t = y1; y1 = y2; y2 = t;
				dx = -dx;
				dy = -dy;
			}
			double mx = (x1 + x2) / 2.0;
			double my = (y1 + y2) / 2.0;
			return "seg(mid=" + Fmt(mx) + "," + Fmt(my) + " dir=" + Fmt(dx) + "," + Fmt(dy) + ")";
		}

		double[] cp = TryPoint(obj, "GetCenterPoint");
		double? radius = TryDouble(obj, "Radius");
		if (cp != null && radius.HasValue)
		{
			return "circle(c=" + Fmt(cp[0]) + "," + Fmt(cp[1]) + " r=" + Fmt(radius.Value) + ")";
		}

		double[] r = TryRange(obj);
		if (r != null)
		{
			return "range(" + Fmt(r[0]) + "," + Fmt(r[1]) + ")-(" + Fmt(r[2]) + "," + Fmt(r[3]) + ")";
		}

		if (cp != null) return "pt(" + Fmt(cp[0]) + "," + Fmt(cp[1]) + ")";
		return null;
	}

	/// <summary>读取一个只读成员(属性/无参方法),失败返回 null 且不抛。</summary>
	public static object TryGet(object obj, string member)
	{
		if (obj == null || string.IsNullOrWhiteSpace(member)) return null;
		try
		{
			return obj.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, obj, null);
		}
		catch
		{
			return null;
		}
	}

	public static double? TryDouble(object obj, string member)
	{
		object v = TryGet(obj, member);
		if (v == null) return null;
		try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
	}

	/// <summary>调用 2 个 out 参数的点方法(GetStartPoint / GetEndPoint / GetCenterPoint)。</summary>
	public static double[] TryPoint(object obj, string method)
	{
		if (obj == null) return null;
		try
		{
			object[] args = new object[] { 0.0, 0.0 };
			ParameterModifier pm = new ParameterModifier(2);
			pm[0] = true;
			pm[1] = true;
			obj.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, obj, args, new ParameterModifier[] { pm }, null, null);
			return new double[]
			{
				Convert.ToDouble(args[0], CultureInfo.InvariantCulture),
				Convert.ToDouble(args[1], CultureInfo.InvariantCulture)
			};
		}
		catch
		{
			return null;
		}
	}

	/// <summary>调用 4 个 out 参数的 Range(x1,y1,x2,y2),失败返回 null。</summary>
	public static double[] TryRange(object obj)
	{
		if (obj == null) return null;
		try
		{
			object[] args = new object[] { 0.0, 0.0, 0.0, 0.0 };
			ParameterModifier pm = new ParameterModifier(4);
			for (int i = 0; i < 4; i++) pm[i] = true;
			obj.GetType().InvokeMember("Range", BindingFlags.InvokeMethod, null, obj, args, new ParameterModifier[] { pm }, null, null);
			return new double[]
			{
				Convert.ToDouble(args[0], CultureInfo.InvariantCulture),
				Convert.ToDouble(args[1], CultureInfo.InvariantCulture),
				Convert.ToDouble(args[2], CultureInfo.InvariantCulture),
				Convert.ToDouble(args[3], CultureInfo.InvariantCulture)
			};
		}
		catch
		{
			return null;
		}
	}

	/// <summary>集合成员数(1-based 集合的 Count),非集合返回 null。</summary>
	public static int? TryCount(object collection)
	{
		object v = TryGet(collection, "Count");
		if (v == null) return null;
		try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return null; }
	}

	/// <summary>集合第 index 个成员(SE 集合为 1-based),失败返回 null。</summary>
	public static object TryItem(object collection, int index)
	{
		if (collection == null) return null;
		try
		{
			return collection.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, collection, new object[] { index });
		}
		catch
		{
			return null;
		}
	}

	public static string Fmt(double v)
	{
		return v.ToString("0.######", CultureInfo.InvariantCulture);
	}

	public static string Str(object v)
	{
		if (v == null) return null;
		if (v is double d) return Fmt(d);
		if (v is bool b) return b ? "True" : "False";
		return Convert.ToString(v, CultureInfo.InvariantCulture);
	}
}

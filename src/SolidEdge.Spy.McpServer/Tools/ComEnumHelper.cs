using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SolidEdge.Spy.InteropServices;

namespace SolidEdge.Spy.McpServer.Tools;

/// <summary>
/// COM 枚举解析公共 helper。
/// - 参数侧:「常量名 -> 值」,供 se_invoke_member / se_invoke_chain 给 VT_USERDEFINED 入参占位。
/// - 返回侧:「值 -> 常量名」,供 se_describe_object 把 VT_USERDEFINED 的裸数字翻译成可读常量名,
///   避免 AI 拿到 DimensionType=1 之类的裸值只能靠猜。
/// </summary>
internal static class ComEnumHelper
{
	private const short VT_PTR_ALIAS = 26;
	private const short VT_USERDEFINED = 29;

	/// <summary>剥掉 VT_PTR 包装,取最内层的 TYPEDESC。</summary>
	internal static TYPEDESC? InnermostTypeDesc(ComParameterInfo p)
	{
		if (p == null)
		{
			return null;
		}
		try
		{
			TYPEDESC value = p.ELEMDESC.tdesc;
			int num = 0;
			while (value.vt == VT_PTR_ALIAS && value.lpValue != IntPtr.Zero && num < 2)
			{
				value = (TYPEDESC)Marshal.PtrToStructure(value.lpValue, typeof(TYPEDESC));
				num++;
			}
			return value;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>参数类型是不是枚举?是则给出该枚举的 ComTypeInfo。</summary>
	internal static bool TryGetEnumTypeInfo(ComParameterInfo p, out ComTypeInfo enumTypeInfo)
	{
		enumTypeInfo = null;
		try
		{
			TYPEDESC? tYPEDESC = InnermostTypeDesc(p);
			if (!tYPEDESC.HasValue)
			{
				return false;
			}
			TYPEDESC value = tYPEDESC.Value;
			if (value.vt != VT_USERDEFINED || value.lpValue == IntPtr.Zero)
			{
				return false;
			}
			p.ComFunctionInfo.ComTypeInfo.GetITypeInfo().GetRefTypeInfo(((IntPtr)value.lpValue).ToInt32(), out ITypeInfo ppTI);
			if (ppTI == null)
			{
				return false;
			}
			bool isEnum = false;
			IntPtr attr = IntPtr.Zero;
			try
			{
				ppTI.GetTypeAttr(out attr);
				isEnum = ((TYPEATTR)Marshal.PtrToStructure(attr, typeof(TYPEATTR))).typekind == TYPEKIND.TKIND_ENUM;
			}
			finally
			{
				if (attr != IntPtr.Zero)
				{
					ppTI.ReleaseTypeAttr(attr);
				}
			}
			if (!isEnum)
			{
				return false;
			}
			ComTypeInfo comTypeInfo = ComTypeManager.Instance.FromITypeInfo(ppTI);
			if (comTypeInfo == null)
			{
				return false;
			}
			enumTypeInfo = comTypeInfo;
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// 值 -> 常量名。命中返回 true。
	/// enumType 只要枚举类型解析成功就会返回(即便没匹配到常量),便于调用方展示"这确实是个枚举"。
	/// </summary>
	internal static bool TryDecodeEnum(ComParameterInfo p, object value, out string enumType, out string constantName)
	{
		enumType = null;
		constantName = null;
		if (value == null)
		{
			return false;
		}
		if (!TryGetEnumTypeInfo(p, out ComTypeInfo typeInfo))
		{
			return false;
		}
		enumType = typeInfo.FullName;
		long target;
		try
		{
			target = Convert.ToInt64(value, CultureInfo.InvariantCulture);
		}
		catch
		{
			return false;
		}
		ComMemberInfo[] members = typeInfo.Members;
		if (members == null)
		{
			return false;
		}
		ComMemberInfo[] array = members;
		foreach (ComMemberInfo comMemberInfo in array)
		{
			object obj = (comMemberInfo as ComVariableInfo)?.ConstantValue;
			if (obj == null)
			{
				continue;
			}
			try
			{
				if (Convert.ToInt64(obj, CultureInfo.InvariantCulture) == target)
				{
					constantName = comMemberInfo.Name;
					return true;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	/// <summary>常量名 -> 值,供 VT_USERDEFINED 入参占位使用(支持 "EnumName.Constant" 与裸常量名)。</summary>
	internal static int? ResolveEnumConstantValue(ComParameterInfo p, string name)
	{
		try
		{
			if (!TryGetEnumTypeInfo(p, out ComTypeInfo typeInfo))
			{
				return null;
			}
			string b = name;
			int num = name.LastIndexOf('.');
			if (num >= 0)
			{
				b = name.Substring(num + 1);
			}
			ComMemberInfo[] members = typeInfo.Members;
			foreach (ComMemberInfo comMemberInfo in members)
			{
				if (string.Equals(comMemberInfo.Name, b, StringComparison.OrdinalIgnoreCase))
				{
					object obj = (comMemberInfo as ComVariableInfo)?.ConstantValue;
					if (obj != null && (obj is int || obj is short || obj is ushort || obj is uint || obj is long))
					{
						return Convert.ToInt32(obj);
					}
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}
}

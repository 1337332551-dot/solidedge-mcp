using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SolidEdge.Spy.McpServer.Tools;

internal static class ManualInvoke
{
	private struct DISPPARAMS
	{
		public nint rgvarg;

		public nint rgdispidNamedArgs;

		public int cArgs;

		public int cNamedArgs;
	}

	private struct EXCEPINFO
	{
		public short wCode;

		public short wReserved;

		[MarshalAs(UnmanagedType.BStr)]
		public string bstrSource;

		[MarshalAs(UnmanagedType.BStr)]
		public string bstrDescription;

		[MarshalAs(UnmanagedType.BStr)]
		public string bstrHelpFile;

		public int dwHelpContext;

		public nint pvReserved;

		public nint pfnDeferredFillIn;

		public int scode;
	}

	[ComImport]
	[Guid("00020400-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDispatchFull
	{
		[PreserveSig]
		int GetTypeInfoCount(out int pctinfo);

		[PreserveSig]
		int GetTypeInfo(int iTInfo, int lcid, out ITypeInfo ppTInfo);

		[PreserveSig]
		int GetIDsOfNames(ref Guid riid, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] rgszNames, int cNames, int lcid, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] int[] rgDispId);

		[PreserveSig]
		int Invoke(int dispIdMember, ref Guid riid, int lcid, short wFlags, ref DISPPARAMS pDispParams, nint pVarResult, out EXCEPINFO pExcepInfo, out int puArgErr);
	}

	private const short VT_EMPTY = 0;

	private const short VT_I4 = 3;

	private const short VT_R8 = 5;

	private const short VT_BSTR = 8;

	private const short VT_DISPATCH = 9;

	private const short VT_ERROR = 10;

	private const short VT_BOOL = 11;

	private const short VT_VARIANT = 12;

	private const short VT_I8 = 20;

	private const short VT_ARRAY = 8192;

	private const int DISP_E_PARAMNOTFOUND = -2147352572;

	private const int LOCALE_USER_DEFAULT = 1024;

	private const short DISPATCH_METHOD = 1;

	private const short DISPATCH_PROPERTYGET = 2;

	private const short DISPATCH_PROPERTYPUT = 4;

	private const int DISPID_PROPERTYPUT = -3;

	private const int VariantSize = 24;

	[DllImport("oleaut32.dll")]
	private static extern nint SafeArrayCreateVector(short vt, int lLbound, int cElements);

	[DllImport("oleaut32.dll")]
	private static extern int SafeArrayPutElement(nint psa, int[] rgIndices, nint pv);

	[DllImport("oleaut32.dll")]
	private static extern int VariantClear(nint pvarg);

	public static bool TryInvoke(object target, string member, object[] values, out object result, out Exception error)
	{
		result = null;
		error = null;
		try
		{
			IDispatchFull dispatchFull = (IDispatchFull)target;
			Guid riid = Guid.Empty;
			int[] array = new int[1];
			int iDsOfNames = dispatchFull.GetIDsOfNames(ref riid, new string[1] { member }, 1, 1024, array);
			if (iDsOfNames != 0)
			{
				throw new COMException("成员 \"" + member + "\" GetIDsOfNames 失败 hr=0x" + iDsOfNames.ToString("X8"), iDsOfNames);
			}
			int num = ((values != null) ? values.Length : 0);
			nint num2 = Marshal.AllocCoTaskMem((num <= 0) ? 1 : (24 * num));
			nint num3 = Marshal.AllocCoTaskMem(24);
			try
			{
				for (int i = 0; i < num; i++)
				{
					WriteVariant(new IntPtr(((IntPtr)num2).ToInt64() + 24L * (long)(num - 1 - i)), values[i]);
				}
				DISPPARAMS pDispParams = new DISPPARAMS
				{
					rgvarg = num2,
					rgdispidNamedArgs = IntPtr.Zero,
					cArgs = num,
					cNamedArgs = 0
				};
				iDsOfNames = dispatchFull.Invoke(array[0], ref riid, 1024, 3, ref pDispParams, num3, out var pExcepInfo, out var puArgErr);
				if (iDsOfNames != 0)
				{
					int num4 = ((iDsOfNames == -2147352571) ? (num - 1 - puArgErr) : puArgErr);
					string text = "IDispatch::Invoke 失败 hr=0x" + iDsOfNames.ToString("X8") + ", puArgErr=" + puArgErr + "(正序第 " + (num4 + 1) + " 个参数)";
					if (!string.IsNullOrEmpty(pExcepInfo.bstrDescription))
					{
						text = text + ", 服务器描述: " + pExcepInfo.bstrDescription;
					}
					if (pExcepInfo.scode != 0)
					{
						text = text + ", scode=0x" + pExcepInfo.scode.ToString("X8");
					}
					throw new COMException(text, iDsOfNames);
				}
				result = Marshal.GetObjectForNativeVariant(num3);
				return true;
			}
			finally
			{
				VariantClear(num3);
				for (int j = 0; j < num; j++)
				{
					nint num5 = new IntPtr(((IntPtr)num2).ToInt64() + 24L * (long)j);
					if (Marshal.ReadInt16(num5, 0) != 0)
					{
						VariantClear(num5);
					}
				}
				Marshal.FreeCoTaskMem(num2);
				Marshal.FreeCoTaskMem(num3);
			}
		}
		catch (Exception ex)
		{
			error = ex;
			return false;
		}
	}

	public static bool TryInvokeSet(object target, string member, object value, out Exception error)
	{
		error = null;
		try
		{
			IDispatchFull dispatchFull = (IDispatchFull)target;
			Guid riid = Guid.Empty;
			int[] array = new int[1];
			int iDsOfNames = dispatchFull.GetIDsOfNames(ref riid, new string[1] { member }, 1, 1024, array);
			if (iDsOfNames != 0)
			{
				throw new COMException("成员 \"" + member + "\" GetIDsOfNames 失败 hr=0x" + iDsOfNames.ToString("X8"), iDsOfNames);
			}
			nint num = Marshal.AllocCoTaskMem(24);
			nint num2 = Marshal.AllocCoTaskMem(4);
			try
			{
				WriteVariant(num, value);
				Marshal.WriteInt32(num2, -3);
				DISPPARAMS pDispParams = new DISPPARAMS
				{
					rgvarg = num,
					rgdispidNamedArgs = num2,
					cArgs = 1,
					cNamedArgs = 1
				};
				iDsOfNames = dispatchFull.Invoke(array[0], ref riid, 1024, 4, ref pDispParams, IntPtr.Zero, out var pExcepInfo, out var _);
				if (iDsOfNames != 0)
				{
					string text = "IDispatch::Invoke(PROPERTYPUT) 失败 hr=0x" + iDsOfNames.ToString("X8");
					if (!string.IsNullOrEmpty(pExcepInfo.bstrDescription))
					{
						text = text + ", 服务器描述: " + pExcepInfo.bstrDescription;
					}
					if (pExcepInfo.scode != 0)
					{
						text = text + ", scode=0x" + pExcepInfo.scode.ToString("X8");
					}
					throw new COMException(text, iDsOfNames);
				}
				return true;
			}
			finally
			{
				if (Marshal.ReadInt16(num, 0) != 0)
				{
					VariantClear(num);
				}
				Marshal.FreeCoTaskMem(num);
				Marshal.FreeCoTaskMem(num2);
			}
		}
		catch (Exception ex)
		{
			error = ex;
			return false;
		}
	}

	private static void WriteVariant(nint v, object value)
	{
		if (value == null || value == Type.Missing)
		{
			Marshal.WriteInt16(v, 0, 10);
			Marshal.WriteInt32(v, 8, -2147352572);
		}
		else if (value is object[] arr)
		{
			WriteArrayVariant(v, arr);
		}
		else if (!(value is int val))
		{
			if (!(value is short val2))
			{
				if (!(value is long val3))
				{
					if (!(value is double value2))
					{
						if (!(value is float num))
						{
							if (!(value is bool flag))
							{
								if (value is string s)
								{
									Marshal.WriteInt16(v, 0, 8);
									Marshal.WriteIntPtr(v, 8, Marshal.StringToBSTR(s));
									return;
								}
								if (!Marshal.IsComObject(value))
								{
									throw new NotSupportedException("手工封送不支持的参数值类型: " + value.GetType().Name);
								}
								Marshal.WriteInt16(v, 0, 9);
								Marshal.WriteIntPtr(v, 8, Marshal.GetIDispatchForObject(value));
							}
							else
							{
								Marshal.WriteInt16(v, 0, 11);
								Marshal.WriteInt16(v, 8, (short)(flag ? (-1) : 0));
							}
						}
						else
						{
							Marshal.WriteInt16(v, 0, 5);
							Marshal.WriteInt64(v, 8, BitConverter.DoubleToInt64Bits(num));
						}
					}
					else
					{
						Marshal.WriteInt16(v, 0, 5);
						Marshal.WriteInt64(v, 8, BitConverter.DoubleToInt64Bits(value2));
					}
				}
				else
				{
					Marshal.WriteInt16(v, 0, 20);
					Marshal.WriteInt64(v, 8, val3);
				}
			}
			else
			{
				Marshal.WriteInt16(v, 0, 3);
				Marshal.WriteInt32(v, 8, val2);
			}
		}
		else
		{
			Marshal.WriteInt16(v, 0, 3);
			Marshal.WriteInt32(v, 8, val);
		}
	}

	private static void WriteArrayVariant(nint v, object[] arr)
	{
		bool flag = arr.Length != 0;
		bool flag2 = arr.Length != 0;
		foreach (object obj in arr)
		{
			if (obj == null || !Marshal.IsComObject(obj))
			{
				flag = false;
			}
			if (!(obj is int) && !(obj is double) && !(obj is float) && !(obj is long) && !(obj is short))
			{
				flag2 = false;
			}
		}
		if (flag)
		{
			nint num = SafeArrayCreateVector(9, 0, arr.Length);
			if (num == IntPtr.Zero)
			{
				throw new OutOfMemoryException("SafeArrayCreateVector(VT_DISPATCH) 失败");
			}
			for (int j = 0; j < arr.Length; j++)
			{
				nint iDispatchForObject = Marshal.GetIDispatchForObject(arr[j]);
				try
				{
					int num2 = SafeArrayPutElement(num, new int[1] { j }, iDispatchForObject);
					if (num2 != 0)
					{
						throw new COMException("SafeArrayPutElement(D) hr=0x" + num2.ToString("X8"), num2);
					}
				}
				finally
				{
					Marshal.Release(iDispatchForObject);
				}
			}
			Marshal.WriteInt16(v, 0, 8201);
			Marshal.WriteIntPtr(v, 8, num);
			return;
		}
		if (flag2)
		{
			nint num3 = SafeArrayCreateVector(5, 0, arr.Length);
			if (num3 == IntPtr.Zero)
			{
				throw new OutOfMemoryException("SafeArrayCreateVector(VT_R8) 失败");
			}
			nint num4 = Marshal.AllocHGlobal(8);
			try
			{
				for (int k = 0; k < arr.Length; k++)
				{
					Marshal.WriteInt64(num4, BitConverter.DoubleToInt64Bits(Convert.ToDouble(arr[k])));
					int num5 = SafeArrayPutElement(num3, new int[1] { k }, num4);
					if (num5 != 0)
					{
						throw new COMException("SafeArrayPutElement(R8) hr=0x" + num5.ToString("X8"), num5);
					}
				}
			}
			finally
			{
				Marshal.FreeHGlobal(num4);
			}
			Marshal.WriteInt16(v, 0, 8197);
			Marshal.WriteIntPtr(v, 8, num3);
			return;
		}
		nint num6 = SafeArrayCreateVector(12, 0, arr.Length);
		if (num6 == IntPtr.Zero)
		{
			throw new OutOfMemoryException("SafeArrayCreateVector(VT_VARIANT) 失败");
		}
		nint num7 = Marshal.AllocCoTaskMem(24);
		try
		{
			for (int l = 0; l < arr.Length; l++)
			{
				ZeroVariant(num7);
				WriteVariant(num7, arr[l]);
				int num8 = SafeArrayPutElement(num6, new int[1] { l }, num7);
				VariantClear(num7);
				if (num8 != 0)
				{
					throw new COMException("SafeArrayPutElement(V) hr=0x" + num8.ToString("X8"), num8);
				}
			}
		}
		finally
		{
			VariantClear(num7);
			Marshal.FreeCoTaskMem(num7);
		}
		Marshal.WriteInt16(v, 0, 8204);
		Marshal.WriteIntPtr(v, 8, num6);
	}

	private static void ZeroVariant(nint v)
	{
		for (int i = 0; i < 24; i += 8)
		{
			Marshal.WriteInt64(v, i, 0L);
		}
	}
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SolidEdge.Spy.McpServer.Tools;

internal static class CaptureStore
{
	public const int InlineMaxEdge = 1500;

	private const int MaxFiles = 50;

	private const long MaxTotalBytes = 104857600L;

	private static string _lastHashHex;

	private static bool _swept;

	public static string Dir
	{
		get
		{
			string text = Environment.GetEnvironmentVariable("SE_MCP_CAPTURE_DIR");
			if (string.IsNullOrWhiteSpace(text))
			{
				text = Path.Combine(Path.GetTempPath(), "se_mcp_captures");
			}
			return text;
		}
	}

	public static void StartupSweep()
	{
		if (!_swept)
		{
			_swept = true;
			try
			{
				Directory.CreateDirectory(Dir);
			}
			catch
			{
			}
			EnforceRetention();
		}
	}

	public static string NewFilePath(string docName, string label)
	{
		StartupSweep();
		string text = DateTime.Now.ToString("HHmmss");
		string text2 = Path.Combine(Dir, Sanitize(docName) + "_" + label + "_" + text + ".png");
		int num = 2;
		while (File.Exists(text2))
		{
			text2 = Path.Combine(Dir, Sanitize(docName) + "_" + label + "_" + text + "-" + num + ".png");
			num++;
		}
		return text2;
	}

	public static bool IsDuplicate(string path)
	{
		string text = HashFile(path);
		if (text == null)
		{
			return false;
		}
		bool result = text == _lastHashHex;
		_lastHashHex = text;
		return result;
	}

	public static void EnforceRetention()
	{
		try
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(Dir);
			if (!directoryInfo.Exists)
			{
				return;
			}
			List<FileInfo> list = (from f in directoryInfo.GetFiles("*.png")
				orderby f.LastWriteTimeUtc descending
				select f).ToList();
			long num = list.Sum((FileInfo f) => f.Length);
			int num2 = 0;
			foreach (FileInfo item in list)
			{
				num2++;
				if (num2 > 50 || num > 104857600)
				{
					num -= item.Length;
					try
					{
						item.Delete();
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
	}

	public static byte[] DownscaleToPngBytes(string path, int maxEdge)
	{
		using Bitmap bitmap = new Bitmap(path);
		int num = Math.Max(bitmap.Width, bitmap.Height);
		if (num <= maxEdge)
		{
			using (MemoryStream memoryStream = new MemoryStream())
			{
				bitmap.Save(memoryStream, ImageFormat.Png);
				return memoryStream.ToArray();
			}
		}
		double num2 = (double)maxEdge / (double)num;
		int width = Math.Max(1, (int)Math.Round((double)bitmap.Width * num2));
		int height = Math.Max(1, (int)Math.Round((double)bitmap.Height * num2));
		using Bitmap bitmap2 = new Bitmap(width, height);
		using Graphics graphics = Graphics.FromImage(bitmap2);
		graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
		graphics.SmoothingMode = SmoothingMode.HighQuality;
		graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
		graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height));
		using MemoryStream memoryStream2 = new MemoryStream();
		bitmap2.Save(memoryStream2, ImageFormat.Png);
		return memoryStream2.ToArray();
	}

	private static string HashFile(string path)
	{
		try
		{
			using FileStream inputStream = File.OpenRead(path);
			using SHA256 sHA = SHA256.Create();
			return BitConverter.ToString(sHA.ComputeHash(inputStream)).Replace("-", "");
		}
		catch
		{
			return null;
		}
	}

	private static string Sanitize(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "SE";
		}
		StringBuilder stringBuilder = new StringBuilder();
		string text = name.Trim();
		foreach (char c in text)
		{
			stringBuilder.Append((char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
		}
		string text2 = stringBuilder.ToString().TrimEnd('_');
		if (text2.Length == 0)
		{
			text2 = "SE";
		}
		if (text2.Length <= 40)
		{
			return text2;
		}
		return text2.Substring(0, 40);
	}
}

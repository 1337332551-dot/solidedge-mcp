using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SolidEdge.Spy.McpServer.Tools;
using Xunit;

namespace SolidEdge.Spy.McpServer.Tests;

/// <summary>
/// GeoUtil / PlaneResolver / StockEstimator 边角分支(主线已随规则测试覆盖,这里钉易漂移的边界)。
/// 坐标轴映射按 2026-09-13 拍板:RefPlane_1=XY(法向Z)、RefPlane_2=YZ(法向X)、RefPlane_3=XZ(法向Y)。
/// </summary>
public sealed class GeoUtilAndEstimatorTests
{
	// ---------- GeoUtil ----------

	[Fact]
	public void SegmentIntersect_十字相交_返回交点()
	{
		bool hit = GeoUtil.SegmentIntersect(new[] { 0d, 0 }, new[] { 2d, 2 }, new[] { 0d, 2 }, new[] { 2d, 0 }, out double ix, out double iy);
		Assert.True(hit);
		Assert.Equal(1, ix, 9);
		Assert.Equal(1, iy, 9);
	}

	[Fact]
	public void SegmentIntersect_平行_不算相交()
	{
		bool hit = GeoUtil.SegmentIntersect(new[] { 0d, 0 }, new[] { 1d, 0 }, new[] { 0d, 1 }, new[] { 1d, 1 }, out _, out _);
		Assert.False(hit);
	}

	[Fact]
	public void SegmentIntersect_共线重叠_不算相交()
	{
		// denom≈0 直接返回 false(交由退化面积规则处理);注释与实现不一致,此处钉实现行为
		bool hit = GeoUtil.SegmentIntersect(new[] { 0d, 0 }, new[] { 2d, 0 }, new[] { 1d, 0 }, new[] { 3d, 0 }, out _, out _);
		Assert.False(hit);
	}

	[Fact]
	public void SegmentIntersect_仅端点接触_开区间容差下不算相交()
	{
		bool hit = GeoUtil.SegmentIntersect(new[] { 0d, 0 }, new[] { 1d, 1 }, new[] { 1d, 1 }, new[] { 2d, 0 }, out _, out _);
		Assert.False(hit);
	}

	[Fact]
	public void SignedArea_逆时针为正_顺时针为负()
	{
		double[][] ccw = { new[] { 0d, 0 }, new[] { 1d, 0 }, new[] { 1d, 1 }, new[] { 0d, 1 } };
		Assert.Equal(1, GeoUtil.SignedArea(ccw), 9);
		Assert.Equal(-1, GeoUtil.SignedArea(Enumerable.Reverse(ccw).ToArray()), 9);
	}

	[Fact]
	public void PointInPolygon_内外与边界算内()
	{
		double[][] square = { new[] { 0d, 0 }, new[] { 1d, 0 }, new[] { 1d, 1 }, new[] { 0d, 1 } };
		Assert.True(GeoUtil.PointInPolygon(new[] { 0.5, 0.5 }, square));
		Assert.False(GeoUtil.PointInPolygon(new[] { 1.5, 1.5 }, square));
		Assert.True(GeoUtil.PointInPolygon(new[] { 0.5, 0.0 }, square), "底边上的点应算内(边界算内)");
	}

	[Fact]
	public void BBoxOverlap_贴边算重叠_分离不算()
	{
		Assert.True(GeoUtil.BBoxOverlap(new[] { 0d, 0, 1, 1 }, new[] { 1d, 0, 2, 1 }));
		Assert.False(GeoUtil.BBoxOverlap(new[] { 0d, 0, 1, 1 }, new[] { 1.0001, 0, 2, 1 }));
	}

	// ---------- PlaneResolver ----------

	[Fact]
	public void PlaneResolver_别名互相引用_返回false不死循环()
	{
		var specs = ParseAll(
			"{\"op\":\"plane\",\"name\":\"a\",\"base\":\"@b\",\"distance\":0.01}",
			"{\"op\":\"plane\",\"name\":\"b\",\"base\":\"@a\",\"distance\":0.02}");
		bool ok = PlaneResolver.TryResolve(specs, 2, "@a", out int planeIdx, out double offset);
		Assert.False(ok);
		Assert.Equal(0, planeIdx);
	}

	[Fact]
	public void PlaneResolver_前向引用别名_返回false()
	{
		// @top 的定义在 beforeIndex 之后 → 找不到(与 E203 语义一致)
		var specs = ParseAll(
			"{\"op\":\"extrude\",\"plane\":\"@top\",\"circle\":{\"center\":[0,0],\"radius\":0.01},\"depth\":0.02}",
			"{\"op\":\"plane\",\"name\":\"top\",\"base\":\"RefPlane_1\",\"distance\":0.05}");
		bool ok = PlaneResolver.TryResolve(specs, 1, "@top", out _, out _);
		Assert.False(ok);
	}

	[Fact]
	public void PlaneResolver_RefPlane垃圾后缀与越界_返回false()
	{
		var specs = new List<FeatureSpec>();
		Assert.False(PlaneResolver.TryResolve(specs, 0, "RefPlaneX", out _, out _));
		Assert.False(PlaneResolver.TryResolve(specs, 0, "RefPlane_99", out _, out _));
		Assert.False(PlaneResolver.TryResolve(specs, 0, "", out _, out _));
		Assert.True(PlaneResolver.TryResolve(specs, 0, "RefPlane_01", out int idx, out _), "补零后缀应可解析");
		Assert.Equal(1, idx);
	}

	[Fact]
	public void PlaneResolver_别名链偏移累加()
	{
		var specs = ParseAll(
			"{\"op\":\"plane\",\"name\":\"mid\",\"base\":\"RefPlane_1\",\"distance\":0.05}",
			"{\"op\":\"extrude\",\"plane\":\"@mid\",\"circle\":{\"center\":[0,0],\"radius\":0.01},\"depth\":0.02}");
		bool ok = PlaneResolver.TryResolve(specs, 2, "@mid", out int planeIdx, out double offset);
		Assert.True(ok);
		Assert.Equal(1, planeIdx);
		Assert.Equal(0.05, offset, 9);
	}

	// ---------- StockEstimator ----------

	[Fact]
	public void StockEstimator_slot旋转90度_长短轴投影互换()
	{
		// slot.angle 合同是弧度(revolve 的 degrees→radians 换算不适用于 slot)
		var specs = ParseAll(
			"{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"slot\":{\"center\":[0.1,0.1],\"length\":0.1,\"width\":0.02,\"angle\":" + (Math.PI / 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + "},\"depth\":0.05}");
		double[] bb = StockEstimator.FeatureBBox(specs, 0, null);
		Assert.NotNull(bb);
		// 90° 后:u(X) 方向只剩宽度投影 0.01,v(Y) 方向只剩长度投影 0.05
		Assert.Equal(0.09, bb[0], 9);
		Assert.Equal(0.11, bb[3], 9);
		Assert.Equal(0.05, bb[1], 9);
		Assert.Equal(0.15, bb[4], 9);
		// side 默认 2,法向 Z 从 0 到 depth
		Assert.Equal(0, bb[2], 9);
		Assert.Equal(0.05, bb[5], 9);
	}

	[Fact]
	public void StockEstimator_平面无法解析_返回null()
	{
		var specs = ParseAll(
			"{\"op\":\"extrude\",\"plane\":\"obj-1\",\"circle\":{\"center\":[0,0],\"radius\":0.01},\"depth\":0.02}");
		Assert.Null(StockEstimator.FeatureBBox(specs, 0, null));
		Assert.Null(StockEstimator.Estimate(specs));
	}

	[Fact]
	public void StockEstimator_多特征并集()
	{
		var specs = ParseAll(
			"{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"rect\":[[0,0],[0.1,0.1]],\"depth\":0.05}",
			"{\"op\":\"extrude\",\"plane\":\"RefPlane_3\",\"circle\":{\"center\":[0.2,0.01],\"radius\":0.01},\"depth\":0.02}");
		double[] bb = StockEstimator.Estimate(specs);
		Assert.NotNull(bb);
		Assert.Equal(0, bb[0], 9);    // X min
		Assert.Equal(0.21, bb[3], 9); // X max(RefPlane_3 圆在 X 方向)
		Assert.Equal(0, bb[1], 9);    // Y min
		Assert.Equal(0.1, bb[4], 9);  // Y max(矩形在 Y 方向)
		Assert.Equal(0, bb[2], 9);    // Z min
		Assert.Equal(0.05, bb[5], 9); // Z max
	}

	[Fact]
	public void StockEstimator_side1_深度向法向负方向()
	{
		var specs = ParseAll(
			"{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"side\":1,\"circle\":{\"center\":[0,0],\"radius\":0.01},\"depth\":0.05}");
		double[] bb = StockEstimator.FeatureBBox(specs, 0, null);
		Assert.Equal(-0.05, bb[2], 9);
		Assert.Equal(0, bb[5], 9);
	}

	[Fact]
	public void StockEstimator_cut未指深度_借用毛坯法向厚度()
	{
		var specs = ParseAll(
			"{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"circle\":{\"center\":[0,0],\"radius\":0.01},\"depth\":0.05}",
			"{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"circle\":{\"center\":[0,0],\"radius\":0.005}}");
		double[] stock = StockEstimator.Estimate(new List<FeatureSpec> { specs[0] });
		double[] bb = StockEstimator.FeatureBBox(specs, 1, stock);
		Assert.NotNull(bb);
		Assert.Equal(stock[2], bb[2], 9); // Z 借毛坯范围
		Assert.Equal(stock[5], bb[5], 9);
		Assert.Equal(-0.005, bb[0], 9);   // 草图范围仍是 cut 自己的圆
	}

	[Fact]
	public void StockEstimator_Intersects_贴合算相交_分离不算()
	{
		double[] a = { 0, 0, 0, 1, 1, 1 };
		double[] b = { 1, 0, 0, 2, 1, 1 };
		Assert.True(StockEstimator.Intersects(a, b));
		b[0] = 1.0001; b[3] = 2;
		Assert.False(StockEstimator.Intersects(a, b));
	}

	[Fact]
	public void StockEstimator_Estimate忽略cut与无深度特征()
	{
		var specs = ParseAll(
			"{\"op\":\"cut\",\"plane\":\"RefPlane_1\",\"rect\":[[9,9],[10,10]],\"depth\":1}",
			"{\"op\":\"extrude\",\"plane\":\"RefPlane_1\",\"rect\":[[0,0],[0.1,0.1]]}");
		double[] bb = StockEstimator.Estimate(specs);
		// cut 不进毛坯;无 depth 的 extrude 跳过 → 无法估算返回 null
		Assert.Null(bb);
	}

	// ---------- helpers ----------

	private static List<FeatureSpec> ParseAll(params string[] jsonFeatures)
	{
		string arr = "[" + string.Join(",", jsonFeatures) + "]";
		using var doc = JsonDocument.Parse(arr);
		return FeatureSpecParser.ParseAll(doc.RootElement.EnumerateArray().ToArray());
	}
}

using System;
using System.Runtime.CompilerServices;

namespace TerritoryWar.Game;

/// <summary>
/// 圆盘覆盖表：半径 r 的圆在每一行 dy 上覆盖哪一段 x（相对圆心）。
///
/// 为什么要预计算：一颗球涂色时要盖住几十到几万个格子。如果每次都现算距离判断，
/// 实测 100 万格 / 1 万颗球要 18 ms；预计算之后直接遍历只要 8.5 ms。
///
/// 为什么按"每行一段"存，而不是"逐个格子"存：
///   · 内存：逐个格子在半径 100 时要 3.1 万对 int（半径 0~100 全加起来约 8 MB）；
///     按行存只要 (2r+1) 段 × 12 字节，全表约 120 KB。
///   · 遍历顺序完全相同（dy 从小到大、每行内 x 从小到大），所以"能量刚好在盘中耗尽时
///     停在哪一格"这类顺序相关的行为不受影响。
/// 段的半宽用**整数判据**算，与"逐格 dx²+dy² ≤ r²"完全等价，不是近似。
/// </summary>
public static class Disk
{
    /// <summary>
    /// 半径上限 = **大球画出来的最大半径**（<see cref="BallSwarm.BigMaxRadius"/>）。
    ///
    /// ⚠ 这里必须是"视觉半径"，不能比它小：涂色盘小于看到的圆时，球压过去会留下
    ///   亮环以内的一圈死区（原来是写死的 48，而大球画到 100 —— 满能量的大球只有
    ///   23% 的可见面积真的被涂到）。所以这个值**从视觉那边取**，不再各写一份。
    ///
    /// 代价（放开 48 → 100 时实测的取舍）：涂色范围变宽 ⇒ 每步要付钱的新格子约 2.1 倍
    /// （144 → 300 格/步），所以同样能量的球**推进深度大约减半** —— 这是"对齐视觉"
    /// 必然要付的平衡代价，不是 bug。
    /// </summary>
    public const int MaxRadius = (int)BallSwarm.BigMaxRadius;

    /// <summary>盘内某一行的横向覆盖：[XMin, XMax] 都相对圆心，Dy 是行偏移。</summary>
    public readonly record struct Row(int Dy, int XMin, int XMax);

    /// <summary>Rows[r] = 半径 r 覆盖的所有行，程序启动时算一次。</summary>
    static readonly Row[][] Rows = Build(MaxRadius);

    static Row[][] Build(int maxRadius)
    {
        var table = new Row[maxRadius + 1][];

        for (int r = 0; r <= maxRadius; r++)
        {
            var rows = new Row[2 * r + 1];
            for (int dy = -r, k = 0; dy <= r; dy++, k++)
            {
                // half = 满足 dx² + dy² ≤ r² 的最大 |dx|（用整数修正掉 sqrt 的边界误差）
                int half = (int)MathF.Sqrt(r * r - dy * dy);
                while (half > 0 && half * half + dy * dy > r * r)
                {
                    half--;
                }

                while ((half + 1) * (half + 1) + dy * dy <= r * r)
                {
                    half++;
                }

                rows[k] = new Row(dy, -half, half);
            }

            table[r] = rows;
        }

        return table;
    }

    /// <summary>半径 <paramref name="radius"/> 覆盖的所有行（超出上限的会被夹到 <see cref="MaxRadius"/>）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Row[] RowsFor(int radius) => Rows[Math.Clamp(radius, 0, MaxRadius)];
}

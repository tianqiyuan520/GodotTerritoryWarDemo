using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TerritoryWar.Game;

/// <summary>
/// 圆盘偏移表：半径 r 的圆覆盖哪些相对格子 (dx, dy)。
///
/// 为什么要预计算：一颗球涂色时要盖住几十到几百个格子。如果每次都用"包围盒 + 逐格
/// 算距离"，实测 100 万格 / 1 万颗球要 18 ms；改成预计算偏移表直接遍历只要 8.5 ms。
/// 少了一半还多，而且代码更短。
/// </summary>
public static class Disk
{
    /// <summary>
    /// 半径上限。⚠ 它**比大球的最大半径(100)小**：大球的涂色盘会被 <see cref="Get"/> 夹到 48，
    /// 也就是"大球实际涂的范围比它画出来的环小一圈"。这个 48 是旧版"释放爆炸半径 40"留下的，
    /// 释放机制已删除，所以这个不一致现在是历史遗留 —— 要让涂色范围跟上视觉，把它改成
    /// <c>BallSwarm.MaxRadius</c>(100) 即可（代价是最大盘从 7250 格涨到 31400 格）。
    /// </summary>
    public const int MaxRadius = 48;

    /// <summary>Offsets[r] = 半径 r 覆盖的所有 (dx, dy)，程序启动时算一次。</summary>
    public static readonly (int Dx, int Dy)[][] Offsets = Build(MaxRadius);

    static (int, int)[][] Build(int maxRadius)
    {
        var table = new (int, int)[maxRadius + 1][];
        for (int r = 0; r <= maxRadius; r++)
        {
            var cells = new List<(int, int)>();
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy <= r * r)
                    {
                        cells.Add((dx, dy));
                    }
                }
            }
            table[r] = cells.ToArray();
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Dx, int Dy)[] Get(int radius)
        => Offsets[Math.Clamp(radius, 0, MaxRadius)];
}

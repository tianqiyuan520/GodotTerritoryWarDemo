using System;
using System.Threading.Tasks;
using Godot;

namespace TerritoryWar.Game;

/// <summary>
/// 势力配色，以及"owner 数组 → RGBA 像素"的查表转换。
///
/// 这是**全项目唯一的阵营配色来源**：领土像素、弹珠实例色、能量数字、领土面板、
/// 基地 Modulate、钉板上的球，全都从这里取，别再各写一张表。
///
/// 转换可以放心并行：每个输出像素只依赖它自己那一个格子，没有任何写冲突。
/// 实测 100 万格从 3.3 ms 降到 0.8 ms。（对比：染色本身有跨球写冲突，就不能这么干。）
/// </summary>
public static class Palette
{
    /// <summary>势力配色，下标 0 是中立。</summary>
    static readonly (byte R, byte G, byte B)[] TeamRgb =
    {
        (0, 0, 0),        // 0 中立：纯黑背景
        (33, 229, 62),    // 1 绿
        (36, 152, 242),   // 2 蓝
        (251, 20, 31),    // 3 红
        (247, 216, 21),   // 4 黄
        (168, 85, 247),   // 5 紫
        (236, 72, 153),   // 6 粉
        (34, 211, 238),   // 7 青
        (249, 115, 22),   // 8 橙
    };

    /// <summary>
    /// 调色板能表示的势力数量（不含中立）。
    /// ⚠ `GameRoot.TeamCount` 超过它就会在领土像素转换时**越界崩**（`lut[owner]`），
    ///   所以启动自检拿它当上限来夹。
    /// </summary>
    public static int MaxTeams => TeamRgb.Length - 1;

    /// <summary>某支势力的颜色（越界会被夹到表内，中立 = 黑）。</summary>
    public static Color TeamColor(int team)
    {
        var (r, g, b) = TeamRgb[Math.Clamp(team, 0, TeamRgb.Length - 1)];
        return new Color(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>
    /// 领土比弹珠暗一档。不这么做的话，球滚在自己地盘上时和背景完全同色，
    /// 看起来就像"球不见了" —— 实测截图里整屏绿色、一颗球都找不着。
    /// 暗底 + 亮球是这个题材的通用做法。
    /// </summary>
    const float TerritoryDim = 0.5f;

    /// <summary>
    /// 造一张 owner 值 → 打包 RGBA 的查表（小端序：R, G, B, A）。
    /// 有了它，整张地图的转换就退化成"取一个 uint、拆成 4 个字节"。
    /// </summary>
    public static uint[] BuildLut()
    {
        var lut = new uint[TeamRgb.Length];
        for (int i = 0; i < TeamRgb.Length; i++)
        {
            var (r, g, b) = TeamRgb[i];
            int dr = (int)(r * TerritoryDim);
            int dg = (int)(g * TerritoryDim);
            int db = (int)(b * TerritoryDim);
            lut[i] = (uint)(dr | (dg << 8) | (db << 16) | (255 << 24));
        }

        return lut;
    }

    /// <summary>把整张 owner 表查表写成 RGBA 像素（<paramref name="pixels"/> 的长度必须是 格数 × 4）。</summary>
    public static void WriteTerritoryPixels(byte[] owner, uint[] lut, byte[] pixels, int width, int height)
    {
        int stripes = Math.Max(1, System.Environment.ProcessorCount);
        int rowsPerStripe = (height + stripes - 1) / stripes;

        Parallel.For(0, stripes, stripe =>
        {
            int yEnd = Math.Min(height, (stripe + 1) * rowsPerStripe);
            for (int y = stripe * rowsPerStripe; y < yEnd; y++)
            {
                int cell = y * width;
                int pixel = cell * 4;

                for (int x = 0; x < width; x++, cell++, pixel += 4)
                {
                    uint c = lut[owner[cell]];
                    pixels[pixel] = (byte)c;
                    pixels[pixel + 1] = (byte)(c >> 8);
                    pixels[pixel + 2] = (byte)(c >> 16);
                    pixels[pixel + 3] = (byte)(c >> 24);
                }
            }
        });
    }
}

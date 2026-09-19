using System;
using System.Threading.Tasks;
using Godot;

namespace TerritoryWar.Game;

/// <summary>
/// 势力配色，以及"owner 数组 → RGBA 像素"的查表转换。
///
/// 转换可以放心并行：每个输出像素只依赖它自己那一个格子，没有任何写冲突。
/// 实测 100 万格从 3.3 ms 降到 0.8 ms。（对比：染色本身有跨球写冲突，就不能这么干。）
/// </summary>
public static class Palette
{
    /// <summary>势力配色，下标 0 是中立。</summary>
    static readonly (byte R, byte G, byte B)[] Rgb =
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

    /// <summary>可用的势力数量（不含中立）。</summary>
    public static int TeamCount => Rgb.Length - 1;

    public static Color TeamColor(int team)
    {
        var (r, g, b) = Rgb[Math.Clamp(team, 0, Rgb.Length - 1)];
        return new Color(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>
    /// 领土比弹珠暗一档。不这么做的话，球滚在自己地盘上时和背景完全同色，
    /// 看起来就像"球不见了" —— 实测截图里整屏绿色、一颗球都找不着。
    /// 暗底 + 亮球是这个题材的通用做法。
    /// </summary>
    const float TerritoryDim = 0.5f;

    /// <summary>owner 值 → 打包好的 RGBA（小端：R, G, B, A）。领土比弹珠暗一档。</summary>
    public static uint[] BuildLut()
    {
        var lut = new uint[Rgb.Length];
        for (int i = 0; i < Rgb.Length; i++)
        {
            var (r, g, b) = Rgb[i];
            int dr = (int)(r * TerritoryDim);
            int dg = (int)(g * TerritoryDim);
            int db = (int)(b * TerritoryDim);
            lut[i] = (uint)(dr | (dg << 8) | (db << 16) | (255 << 24));
        }
        return lut;
    }

    /// <summary>把整张 owner 表查表写成 RGBA 像素。按行分片并行，无写冲突。</summary>
    public static void ToRgba(byte[] owner, uint[] lut, byte[] rgba, int width, int height)
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
                    rgba[pixel] = (byte)c;
                    rgba[pixel + 1] = (byte)(c >> 8);
                    rgba[pixel + 2] = (byte)(c >> 16);
                    rgba[pixel + 3] = (byte)(c >> 24);
                }
            }
        });
    }
}
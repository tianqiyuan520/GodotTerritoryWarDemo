using System;
using System.Runtime.CompilerServices;

namespace TerritoryWar.Game;

/// <summary>
/// 领土网格。每个格子占两个字节：owner（0 = 中立）和 strength（防御强度）。
///
/// **实际生效的侵蚀规则只有三条**（<see cref="Attack"/> 的真值表，也是 <c>SelfCheck</c> 里
/// 用一个小地图跑一遍断言的那张表）：
///
///   打中立格       → 直接占领，强度 = min(<see cref="MaxCapture"/>, power)
///   打敌方格，power ≥ 强度 → 占领，残留强度 = min(180, power − 旧强度 + 14)（防止被第三方顺手捡走）
///   打敌方格，power &lt; 强度 → 打不动，只削对方强度（strength −= power）
///
/// ⚠ **"打自己格子 = 加固"这条规则已经删除**（历史上是对齐社区《领土战争》的规则写的，
///   但它从来没生效过）：唯一调用方 <c>BallSwarm.Paint</c> 在调用前就把自己格跳过了，
///   所以那个分支不可达、`ReinforceDivisor`/`MaxReinforce` 是死常量。
///   于是强度的**唯一来源就是"占领时残留"**，即"当初是谁用什么强度占下它"。
///   要重新启用加固：在 <c>Paint</c> 的 `continue` 之前对自己格调一次加固，
///   并连同 `ReinforceDivisor`（现为 power/8，会瞬间顶到上限，需要重调）与整套节奏一起验。
///
/// "先圈地、后血战"的节奏仍然成立：中立便宜、敌方贵。
/// </summary>
public sealed class TerritoryMap
{
    public const byte Neutral = 0;

    const int MaxCapture = 180;
    const int CaptureCarry = 14;      // 攻破后残留的防御

    public readonly int Width;
    public readonly int Height;

    readonly byte[] _owner;
    readonly byte[] _strength;
    readonly int[] _cellsByTeam;

    /// <summary>owner 数组，直接交给调色板转换用（不复制）。</summary>
    public byte[] Owner => _owner;

    /// <summary>每势力占了多少格，[0] 是中立格数。</summary>
    public int[] CellsByTeam => _cellsByTeam;

    public TerritoryMap(int width, int height, int teamCount)
    {
        Width = width;
        Height = height;
        _owner = new byte[width * height];
        _strength = new byte[width * height];
        _cellsByTeam = new int[teamCount + 1];
        _cellsByTeam[Neutral] = width * height;
    }

    /// <summary>
    /// 开局划地：无条件把这一格判给某队。不走侵蚀规则、不校验、不计成本 ——
    /// 只在开局按"离哪个基地最近"铺一次，用来消掉中立地。
    /// </summary>
    public void Claim(int x, int y, byte team)
    {
        int i = y * Width + x;
        byte owner = _owner[i];
        if (owner == team)
        {
            return;
        }

        _cellsByTeam[owner]--;
        _cellsByTeam[team]++;
        _owner[i] = team;
        _strength[i] = 0;   // 和之前的中立地一样脆：开局的地盘不设防，谁打到算谁的
    }

    /// <summary>用 power 点攻击力打这一格。返回是否易主。</summary>
    // 全项目调用最频繁的方法（每步每颗球几十次），跳过分层编译直接出优化代码。
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Attack(int x, int y, byte team, int power)
    {
        int i = y * Width + x;
        byte owner = _owner[i];

        if (owner == team)
        {
            // 自己的格子：**什么都不做**。
            // ⚠ 这里曾经是"加固（+power/8，上限 210）"，但唯一调用方 BallSwarm.Paint 在调用前
            //   就把自己格 continue 掉了 —— 那个分支从来没被执行过（详见类注释）。
            //   保留这个提前返回是必要的：否则自己的格子会走进下面"攻破"那一段，
            //   把强度按"被攻占"的公式重算一遍，变成一种静默的错行为。
            return false;
        }

        if (owner == Neutral)
        {
            _owner[i] = team;
            _strength[i] = (byte)Math.Min(MaxCapture, power);
            _cellsByTeam[Neutral]--;
            _cellsByTeam[team]++;
            return true;
        }

        if (power >= _strength[i])
        {
            _strength[i] = (byte)Math.Min(MaxCapture, power - _strength[i] + CaptureCarry);
            _owner[i] = team;
            _cellsByTeam[owner]--;
            _cellsByTeam[team]++;
            return true;
        }

        _strength[i] = (byte)(_strength[i] - power);
        return false;
    }

    /// <summary>读某一格的归属（0 = 中立）。开局之后正常不会再出现中立。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte OwnerAt(int x, int y) => _owner[y * Width + x];

    /// <summary>读某一格的防御强度。给启动自检的规则真值表用（游戏逻辑自己走 <see cref="Attack"/>）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte StrengthAt(int x, int y) => _strength[y * Width + x];
}

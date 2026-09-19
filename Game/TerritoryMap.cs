using System;
using System.Runtime.CompilerServices;

namespace TerritoryWar.Game;

/// <summary>
/// 领土网格。每个格子占两个字节：owner（0 = 中立）和 strength（防御强度）。
///
/// 侵蚀规则取自社区《领土战争 / Multiply or Release》的实测数值，见
/// docs/marble-territory-war-projects.md 第 5.2 节：
///
///   打自己的格子   → 加固（让已占的地越来越难被抢）
///   打中立格       → 直接占领
///   攻击力 ≥ 防御  → 攻破并占领，残留一点防御（防止被第三方顺手捡走）
///   攻击力 &lt; 防御  → 打不动，只削对方防御
///
/// 这套规则的价值：中立便宜、敌方贵，天然形成"先圈地、后血战"的节奏。
/// </summary>
public sealed class TerritoryMap
{
    public const byte Neutral = 0;

    const int ReinforceDivisor = 8;   // 打自己格子时的防御增量 = power / 8
    const int MaxReinforce = 210;     // 防御上限（防无限囤积）
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
    // 这个标注是从商业版学来的 —— 那边热路径上挂了十几处。
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Attack(int x, int y, byte team, int power)
    {
        int i = y * Width + x;
        byte owner = _owner[i];

        if (owner == team)
        {
            int reinforced = _strength[i] + Math.Max(1, power / ReinforceDivisor);
            _strength[i] = (byte)Math.Min(MaxReinforce, reinforced);
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

    /// <summary>某支势力出局：它的地盘全部变回中立。</summary>
    public void ReleaseTeam(byte team)
    {
        _cellsByTeam[Neutral] += _cellsByTeam[team];
        _cellsByTeam[team] = 0;

        for (int i = 0; i < _owner.Length; i++)
        {
            if (_owner[i] == team)
            {
                _owner[i] = Neutral;
                _strength[i] = 0;
            }
        }
    }
}

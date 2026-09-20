using System;
using Godot;
using TerritoryWar.Game;

namespace TerritoryWar;

/// <summary>
/// 一局的生命周期：开局分地 → 中途出局清算 → 胜负判定。
///
/// 这里有一条贯穿全篇的规则：**地图上永远没有中立地**。
/// 开局按"离哪个基地最近"一次分完；某家出局时，它的地盘立刻按同一条规则分给幸存者。
/// 两处用的是同一个 <see cref="ClaimByNearestBase"/>，所以规则不会走形。
/// </summary>
public partial class GameRoot
{
    /// <summary>
    /// **一局级复位**：把"跨局会残留状态"的东西全部清一遍。
    ///
    /// ⚠ 以后加组件时把它的复位挂到这里 —— 只靠"StartMatch 里记得清"是不够的：
    ///   `BaseVisuals` 的受击涟漪就漏过一次（上一局最后一击留下的 impact_age=0 会冻在那儿，
    ///   新局开局在**上一局最后挨打的位置**闪一圈亮环）；`_uiTimer` 与能量数字的节流相位同理。
    ///   钉板有自己的随机种子，所以它的"重新播种 + 清空"也放在这一个地方。
    /// </summary>
    void ResetRoundComponents()
    {
        _uiTimer = 0;
        _energyLabelTimer = 0;
        _bases.ResetRound();

        if (Drop != null)
        {
            Drop.Reseed(Seed + _generation * RngStreamStride + DropSeedOffset);
            Drop.Reset();
        }
    }

    /// <summary>
    /// 四个角相对地图中心的方向，顺时针：左上 → 右上 → 右下 → 左下。
    /// 屏幕坐标 y 向下，所以 -y 是"上"。只在场景里缺基地节点时用来兜底摆位。
    /// </summary>
    static readonly (float X, float Y)[] CornerSigns = { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) };

    /// <summary>
    /// 开一局（启动时一次，之后按 R 重开）：重新播种、建地图与弹珠群、按场景节点摆四家基地、分地。
    /// </summary>
    void StartMatch()
    {
        _generation++;
        _elapsed = 0f;
        _accumulator = 0f;
        _winner = 0;
        _state = MatchState.Running;
        _paused = false;   // 重开一局不该继承"暂停"：屏幕上没有暂停提示，留在暂停看起来就是卡死了

        _map = new TerritoryMap(MapSize, MapSize, TeamCount);
        _swarm = new BallSwarm(MaxBalls, Seed + _generation * RngStreamStride + SwarmSeedOffset);
        _swarm.SmallBallLifetime = SmallBallLifetime;
        _swarm.SmallBallCellLifeCost = SmallBallCellLifeCost;
        _swarm.ClashEnergyCost = BigBallClashEnergyCost;
        _swarm.ClashCooldown = BigBallClashCooldown;
        _matchRng = new Random(Seed + _generation * RngStreamStride);
        _teams = new Team[TeamCount + 1];

        ResetRoundComponents();

        // 四角各一家，顺时针：左上 → 右上 → 右下 → 左下。
        // **基地坐标直接从场景的 `Bases/Base1..4` 节点读**（节点顺序 = 势力号），
        // 所以想在编辑器里挪基地，拖那 4 个节点就行 —— 开局分地、炮口朝向、判定半径全都跟着走。
        float center = MapSize * 0.5f;

        for (int team = 1; team <= TeamCount; team++)
        {
            float baseX = center;
            float baseY = center;

            if (_bases.TryGetCenter(team, out Vector2 world))
            {
                // 场景节点在世界坐标里（以地图中心为原点），网格坐标 = 世界坐标 + 半图
                baseX = world.X + center;
                baseY = world.Y + center;
            }
            else
            {
                var corner = CornerSigns[(team - 1) % CornerSigns.Length];
                float inset = MapSize * 0.09f;
                baseX = center + corner.X * (center - inset);
                baseY = center + corner.Y * (center - inset);
            }

            // 炮口朝向地图中心，摆动就围绕这个角度来回
            float baseAngle = MathF.Atan2(center - baseY, center - baseX);

            // 相位和开火计时都随机错开，否则四角会整齐划一地齐射
            float phase = (float)(_matchRng.NextDouble() * Math.Tau);
            float fireTimer = (float)(_matchRng.NextDouble() * DropFeedInterval);
            float bigShotTimer = (float)(_matchRng.NextDouble() * BigShotInterval);

            var created = new Team((byte)team, baseX, baseY, BaseHitPoints, baseAngle, phase,
                fireTimer, bigShotTimer);
            created.EnergyPool = LaunchEnergy * TurretBallsPerShot;   // 开局能打一轮
            _teams[team] = created;
        }

        // 开局就把地图分完，不留中立地。
        // 四角布局下这正好切成四个象限（分界线是两条对角线），
        // 于是一开局就是四家接壤、直接开打，而不是先花二十秒圈无主地。
        ClaimByNearestBase(byte.MaxValue);
    }

    /// <summary>
    /// 把地图上"该处理"的格子判给**离得最近的那个还活着的基地**。
    ///
    /// <paramref name="onlyFromTeam"/> = <see cref="byte.MaxValue"/> 表示处理所有格子（开局分地）；
    /// 否则只处理当前属于该势力的格子（它出局时用）。
    ///
    /// 分给最近的一家而不是击杀者，是为了不让一次爆冷直接滚成雪球。
    /// </summary>
    void ClaimByNearestBase(byte onlyFromTeam)
    {
        for (int y = 0; y < MapSize; y++)
        {
            for (int x = 0; x < MapSize; x++)
            {
                if (onlyFromTeam != byte.MaxValue && _map.OwnerAt(x, y) != onlyFromTeam)
                {
                    continue;
                }

                byte nearest = 0;
                float bestDistance = float.MaxValue;

                for (int team = 1; team <= TeamCount; team++)
                {
                    if (!_teams[team].Alive)
                    {
                        continue;
                    }

                    float dx = x - _teams[team].BaseX;
                    float dy = y - _teams[team].BaseY;
                    float distance = dx * dx + dy * dy;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearest = (byte)team;
                    }
                }

                if (nearest != 0)
                {
                    _map.Claim(x, y, nearest);
                }
            }
        }
    }

    /// <summary>
    /// 一家出局。**清算清单**（照着这条清单加东西，别再漏掉某一边）：
    ///   ① 地盤：按"离哪个活着的基地最近"分给幸存者（<see cref="ClaimByNearestBase"/>）；
    ///   ② 世界侧弹珠：全清（<see cref="BallSwarm.KillTeam"/>，它会立刻收尸）；
    ///   ③ 钉板上的抽签球：清掉 —— 不清的话这些"死签"还会继续掉最多 8 秒，
    ///      而且让倍率门的计数继续涨（玩家看到"红队的签还在掉、×2 还在涨，却什么都不发生"）；
    ///   ④ 这家自己的待发状态：倍率、护盾归零。
    /// 基地外观（灰掉）由 <see cref="BaseVisuals"/> 每帧按 Alive 写。
    /// </summary>
    void Eliminate(int team)
    {
        ClaimByNearestBase((byte)team);
        _swarm.KillTeam((byte)team);
        Drop?.PurgeTeam((byte)team);

        _teams[team].PendingMultiplier = 1;
        _teams[team].ShieldTimer = 0f;
    }

    /// <summary>
    /// 数一下还剩几家，决定这一局是否结束。三态，**不要再用"winner == 0"兼任两种含义**：
    ///   一家不剩 = 平局（同一步里最后两家互相打掉基地，是真实可达的），也必须停下模拟 ——
    ///   以前这种情况会被写成 winner=0，而 0 又是"还没打完"，于是比赛永远不结束。
    /// </summary>
    void CheckVictory()
    {
        int alive = 0;
        int last = 0;

        for (int team = 1; team <= TeamCount; team++)
        {
            if (_teams[team].Alive)
            {
                alive++;
                last = team;
            }
        }

        if (alive == 1)
        {
            _state = MatchState.Won;
            _winner = last;
            ReportMatchEnd();
        }
        else if (alive == 0)
        {
            _state = MatchState.Drawn;
            _winner = 0;
            ReportMatchEnd();
        }
    }

    /// <summary>
    /// 一局结束时的收尾。屏幕上没有 HUD（按需求删掉了），所以结果打到控制台 ——
    /// 这也是 <see cref="_winner"/> **唯一的消费者**：没有它，胜负就只是一个没人读的字段
    /// （这一局结束时玩家什么都看不到）。
    /// </summary>
    void ReportMatchEnd()
    {
        GD.Print(_state == MatchState.Won
            ? $"[对局] 势力{_winner} 获胜（第 {_generation} 局，用时 {_elapsed:F1} 秒）"
            : $"[对局] 平局：最后两家在同一步里互相打掉了基地（第 {_generation} 局，{_elapsed:F1} 秒）");
    }
}

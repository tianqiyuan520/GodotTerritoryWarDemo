using System;
using Godot;
using TerritoryWar.Game;

namespace TerritoryWar;

/// <summary>
/// 一个模拟步里发生的一切。**调用顺序是有依赖的**（见 <see cref="Simulate"/>），别随手调换。
///
/// 三件事值得先知道：
///   1. 大球没有"一次性爆炸" —— 它压进敌境是逐格磨过去的，每格扣 cellCost，磨穿能量就跌回小球档。
///   2. 染色不能并行 —— 它同时改 owner 和 strength 两处共享单元格。
///   3. 开火要过钉板：投料频率 × 落门比例是弹珠数量唯一的闸门，而且每轮消耗必须小于同期收入。
/// </summary>
public partial class GameRoot
{
    /// <summary>
    /// 走一步固定时长的模拟。顺序：弹珠（移动 / 染色 / 碰撞 / 收尸）→ 投料 → 大球 → 基地攻防
    /// → 经济与计时 → 胜负 → 钉板。
    /// </summary>
    void Simulate(float dt)
    {
        _elapsed += dt;
        int cellCost = CurrentCellCost();

        _swarm.Step(dt, _map, BallSpeed, cellCost, BallUpkeepPerSecond);
        _swarm.ResolveCollisions();
        _swarm.Compact();

        TickTurrets(dt);
        TickBigShots(dt);
        AttackBases(dt);
        TickEconomy(dt);
        CheckVictory();
        Drop?.Step(dt);   // 钉板跟着模拟走，所以暂停 / 倍速对它一样有效
    }

    /// <summary>每格占领成本随时间从 1 涨到 <see cref="MaxCellCost"/> —— 这是压制无限扩张的关键机制。</summary>
    int CurrentCellCost()
        => Math.Clamp(1 + (int)(_elapsed / MathF.Max(1f, CostRampSeconds)), 1, MaxCellCost);

    /// <summary>收入（按领土面积）与各种倒计时。</summary>
    void TickEconomy(float dt)
    {
        int[] cells = _map.CellsByTeam;

        for (int team = 1; team <= TeamCount; team++)
        {
            var t = _teams[team];
            if (!t.Alive)
            {
                continue;
            }

            // 收入只负责攒能量；开火交给 TickTurrets（有节奏地齐射，不是攒够就立刻发）
            t.EnergyPool += cells[team] * IncomePerCell * dt;

            // 护盾倒计时（钉板"护盾"格点亮的）
            if (t.ShieldTimer > 0f)
            {
                t.ShieldTimer = MathF.Max(0f, t.ShieldTimer - dt);
            }

            // 基地受击涟漪的计时（跟模拟时间走，所以暂停 / 倍速一致）
            _bases.AdvanceImpactAge(team, dt);
        }
    }

    /// <summary>
    /// 炮塔：摆动炮口 + **往左侧钉板投料**（不再是直接齐射）。
    ///
    /// 这一版把"开火"拆成两段，对齐这个题材的核心循环（见 docs/research-territory-war.md §2.1）：
    ///   投料 → 球在钉板里掉落 → 穿过倍率门攒倍率 → 落进底排某格 → 才决定这一轮做什么。
    ///
    /// 为什么投料挂在炮塔上而不是做成一个全局计时器：这样"该谁投料"天然跟着势力走，
    /// 球自带颜色，落格时也天然知道该给哪家 —— 不需要额外的归属表。
    ///
    /// ⚠ 能量约束挪到了落格那一刻（见 <see cref="OnDropGateEntered"/>）：投料本身不花钱，
    ///   但这里判断"能不能投料"用的仍然是"能不能付得起一轮基础齐射" ——
    ///   否则能量见底的队伍会一直往板上堆球，板上全是死签。
    /// </summary>
    void TickTurrets(float dt)
    {
        float salvoCost = LaunchEnergy * TurretBallsPerShot;

        for (int team = 1; team <= TeamCount; team++)
        {
            var t = _teams[team];
            if (!t.Alive)
            {
                continue;
            }

            // 摆动：炮口在朝地图中心的方向上左右扫
            t.TurretAngle = t.BaseAngle + MathF.Sin(_elapsed * TurretSwingSpeed + t.SwingPhase) * TurretSwingRange;

            t.FireTimer -= dt;
            if (t.FireTimer > 0f)
            {
                continue;
            }

            if (t.EnergyPool < salvoCost || _swarm.Count >= MaxBalls)
            {
                continue;   // 哑火，等收入补上（不重置计时，能量一到就投）
            }

            t.FireTimer = DropFeedInterval;

            for (int k = 0; k < Math.Max(1, DropFeedPerRound); k++)
            {
                Drop?.Push(t.Id);
            }
        }
    }

    /// <summary>
    /// 蓄力射击：每隔 <see cref="BigShotInterval"/> 让每座炮塔单独打出一颗**大球**。
    ///
    /// 这是大球的两条产生途径之一（另一条是钉板"大球"格）。小球是固定能量的子弹
    /// （能量冻结），自己永远攒不到大球门槛，所以"变大"完全由炮塔 / 板决定。
    /// </summary>
    void TickBigShots(float dt)
    {
        for (int team = 1; team <= TeamCount; team++)
        {
            var t = _teams[team];
            if (!t.Alive)
            {
                continue;
            }

            t.BigShotTimer -= dt;
            if (t.BigShotTimer > 0f)
            {
                continue;
            }

            // ⚠ 只有**真的打出去了**才重置计时：能量不够或炮口被占住时计时留着，
            //   下一帧立刻再试 —— 这正是"大球实际发射量是额定值的 86~90%"的来源。
            if (FireBigShot(t))
            {
                t.BigShotTimer = BigShotInterval;
            }
        }
    }

    /// <summary>
    /// 打一颗大球。定时器（<see cref="TickBigShots"/>）和钉板"大球"格都走这里，
    /// 所以两条路径的规则必然一致：随机摇出膛能量、扣能量池、炮口被占住就不打。
    /// </summary>
    /// <returns>是否真的打出去了。</returns>
    bool FireBigShot(Team t)
    {
        float barrel = MuzzleDistance;

        // 出膛能量随机摇 —— 摇到多少就是这一颗的**全部预算**（能量只减不增），
        // 也就决定了它多大、能磨多远。炮塔按摇出来的数扣能量池。
        float rolled = BigBallEnergyMin
            + (float)_matchRng.NextDouble() * (BigBallEnergyMax - BigBallEnergyMin);
        rolled = MathF.Max(rolled, BallSwarm.BigBallEnergy);

        if (t.EnergyPool < rolled || _swarm.Count >= MaxBalls)
        {
            return false;   // 能量不够就打不出来
        }

        float muzzleX = Math.Clamp(t.BaseX + MathF.Cos(t.TurretAngle) * barrel, 1f, MapSize - 2f);
        float muzzleY = Math.Clamp(t.BaseY + MathF.Sin(t.TurretAngle) * barrel, 1f, MapSize - 2f);

        // ⚠ 炮口被已有的球占住就**先不打**（能量留着、计时也不重置，下一帧再看）。
        // 否则新球会"出生在别人身上"：一帧之内被推开几十格 —— 用户反馈的"碰撞很突兀"就是这个。
        // 同队连射特别容易踩到：两次射击间隔 1.2 秒，前一颗已经飞出 `1.2 × 90 = 108` 格，
        // 而大球的判定距离是 `r_a + r_b` = **107~150 格**，于是"稍大一点的两颗"一出生就叠在一起。
        // 实测代价：大球实际发射量是额定值的 **86~90%**（被挡时不是等几十毫秒，
        // 而是等前一颗偏航 / 撞墙为止 —— 同向等速的两颗球间距不会自己变大）。
        // 觉得亏就调大 BigShotInterval 或把大球调小，让"间距"回到判定距离之外。
        if (_swarm.BigBallBlockedAt(muzzleX, muzzleY, rolled))
        {
            return false;
        }

        t.EnergyPool -= rolled;

        // 大球不用散布：它就是一颗，沿着当前炮口方向直直打出去
        _swarm.Spawn(muzzleX, muzzleY, rolled, t.Id, t.TurretAngle);
        return true;
    }

    /// <summary>按炮口角度、在离基地 <paramref name="distance"/> 处放一颗球。</summary>
    void SpawnAt(Team t, float angle, float distance, float energy)
    {
        _swarm.Spawn(
            Math.Clamp(t.BaseX + MathF.Cos(angle) * distance, 1f, MapSize - 2f),
            Math.Clamp(t.BaseY + MathF.Sin(angle) * distance, 1f, MapSize - 2f),
            energy,
            t.Id,
            angle);
    }

    /// <summary>
    /// 敌方弹珠贴着基地时，把自己的能量转成伤害 —— 磨基地也是在磨自己。
    ///
    /// ⚠ 这里是**按球累加**的：贴上去的球越多，秒伤越高。小球改成"免费涂色 + 寿命制"
    /// 之后它们不再快速减员，上千颗会一窝蜂压到基地上，所以每颗的秒伤必须压得很低
    /// （原来 1500 时，10 颗球贴身就是 15000/s，8000 血的基地 0.5 秒就没了 —— 实测
    /// 整局 16 秒就打完）。另外**大球单独加成**：它是炮塔花本钱打出来的攻城武器。
    /// </summary>
    void AttackBases(float dt)
    {
        float half = MapSize * 0.5f;

        // 本步被打死的势力先记下来，**循环结束之后**才清算。
        // ⚠ 两个原因：① KillTeam 会收尸、改变弹珠数组顺序，在遍历中途调用会打乱下标；
        //   ② 出局那一刻还在场上的球**不允许再出手**（下面用 _swarm.IsDead 挡），
        //      否则"基地已经没了，它的球却还在同一步里打死别人"，最后两家同归于尽时
        //      会出现 0 家存活 —— 那个状态以前会被当成"还没打完"，比赛就永远不结束了。
        int eliminated = 0;

        for (int i = 0; i < _swarm.Count; i++)
        {
            // 被标记死亡的球（上一帧出局清算时标的）不允许再造成任何效果
            if (_swarm.IsDead(i))
            {
                continue;
            }

            byte attacker = _swarm.Team[i];

            for (int team = 1; team <= TeamCount; team++)
            {
                if (team == attacker)
                {
                    continue;
                }

                var t = _teams[team];
                if (!t.Alive)
                {
                    continue;
                }

                float reach = BaseRadius + _swarm.RadiusOf(i);
                float dx = _swarm.X[i] - t.BaseX;
                float dy = _swarm.Y[i] - t.BaseY;
                if (dx * dx + dy * dy > reach * reach)
                {
                    continue;
                }

                float rate = _swarm.Energy[i] >= BallSwarm.BigBallEnergy
                    ? BaseDamagePerSecond * BigBallBaseDamageScale
                    : BaseDamagePerSecond;

                // 护盾（钉板"护盾"格给的）期间只吃一小部分伤害。它只挡基地伤害，
                // 不挡涂色、不挡大球互撞 —— 所以护盾是"续命"，不是无敌。
                if (t.ShieldTimer > 0f)
                {
                    rate *= DropShieldReduction;
                }

                // 伤害走 BallSwarm 的**唯一能量入口**：它会顺手跑"跌破门槛就降级 / 销毁"的收尾。
                // ⚠ 直接写 _swarm.Energy[i] 会绕过那条规则 —— 曾经留下过"被基地磨到 0 能量、
                //   却因为 0 < 4000 被当成小球、靠出生时那 16 秒寿命继续活着"的球。
                float damage = MathF.Min(_swarm.Energy[i], rate * dt);
                _swarm.ApplyDamage(i, damage);
                t.TakeDamage(damage);

                // 受击涟漪：把弹珠的位置交给基地着色器，它从这个点往外画一圈扩散的亮环
                _bases.RegisterHit(team, _swarm.X[i] - half, _swarm.Y[i] - half);

                if (!t.Alive)
                {
                    eliminated |= 1 << team;
                }

                break;   // 一颗球一步只打一家
            }
        }

        for (int team = 1; team <= TeamCount; team++)
        {
            if ((eliminated & (1 << team)) != 0)
            {
                Eliminate(team);
            }
        }
    }

    /// <summary>
    /// 球碰到了门。两类门的分工（对齐参考作品的说法："球穿过彩色门并执行门上的数学运算；
    /// 赛道板下部有 Release 门，把球发射出去"，见 docs/reference-comparison.md §7.3）：
    ///
    ///   · **穿过式**（×2 / ×4 / ×8 / 转盘）：球不消失，只把倍率累乘到该队头上（带上限）。
    ///     所以倍率是**在路上攒**的，"先抽到倍率再落发射格"才是好签。
    ///   · **落格式**（底排 7 格）：球被消费，执行该格的效果。
    ///
    /// ⚠ 能量仍然是硬约束：发射 / 弹幕 / 扇形 / 大球都要兑付能量，能量不够就**这一签作废**
    ///   （倍率留着，等收入补上）。所以"丢地盘 → 收入少 → 抽到好签也打不出去"这条压制链没断。
    /// </summary>
    void OnDropGateEntered(int gate, byte teamId, int value)
    {
        if (teamId < 1 || teamId > TeamCount)
        {
            return;
        }

        var t = _teams[teamId];
        if (t == null || !t.Alive)
        {
            return;
        }

        // ---- 穿过式：倍率门 / 转盘门 ----
        if (gate < DropBoard.PassGateCount)
        {
            t.PendingMultiplier = Math.Min(DropMultiplierCap, Math.Max(1, t.PendingMultiplier * Math.Max(1, value)));
            return;
        }

        switch (gate)
        {
            case DropBoard.GateFire:
                FireWithMultiplier(t);
                break;

            case DropBoard.GateAmmo:
                // 白送一份齐射能量：相当于"这一签直接换一发"，所以它是底排里最稳的收益
                t.EnergyPool += LaunchEnergy * TurretBallsPerShot;
                break;

            case DropBoard.GateShield:
                t.ShieldTimer = MathF.Max(t.ShieldTimer, DropShieldSeconds);
                break;

            case DropBoard.GateBigBall:
                FireBigShot(t);
                break;

            case DropBoard.GateBarrage:
                FirePaidSalvo(t, DropBarrageBalls, 0.4f);   // 密集：球多、散布窄
                break;

            case DropBoard.GateFan:
                FirePaidSalvo(t, TurretBallsPerShot, DropFanSpreadScale);   // 大扇形铺开
                break;

            case DropBoard.GateFirework:
                FireFirework(t);
                break;
        }
    }

    /// <summary>
    /// 兑付一轮齐射的能量：返回**真的能打出几颗**（0 = 付不起 / 场上满了，这一签作废）。
    ///
    /// ⚠ **这是"往外打"唯一允许扣费的地方。** 曾经扣费写在调用点上，结果底排的
    ///   「弹幕」(48 颗) 与「扇形」(24 颗) 两格漏了扣费 —— 能量为 0 的队伍照样白拿 48 颗，
    ///   "丢地盘 → 收入少 → 抽到好签也打不出去"这条压制链直接从这两格漏掉。
    ///   凡是"生成一轮球"的效果都必须走这里，别再各自算钱。
    /// </summary>
    int PayForSalvo(Team t, int balls)
    {
        int count = Math.Min(MaxBalls - _swarm.Count, balls);
        if (count <= 0)
        {
            return 0;
        }

        float cost = LaunchEnergy * count;
        if (t.EnergyPool < cost)
        {
            return 0;   // 能量不够：这一签作废（倍率留着，等收入补上）
        }

        t.EnergyPool -= cost;
        return count;
    }

    /// <summary>
    /// 打出一轮"发射"：球数 = 基础 × 当前倍率，付得起才打、打完倍率清零
    /// （付不起就保留倍率，这一签作废）。
    /// </summary>
    void FireWithMultiplier(Team t)
    {
        int balls = PayForSalvo(t, TurretBallsPerShot * Math.Max(1, t.PendingMultiplier));
        if (balls == 0)
        {
            return;
        }

        t.PendingMultiplier = 1;
        FireSalvo(t, balls, 1f);
    }

    /// <summary>底排"弹幕"/"扇形"：走同一条付费入口，只是散布不同。</summary>
    void FirePaidSalvo(Team t, int balls, float spreadScale)
    {
        int paid = PayForSalvo(t, balls);
        if (paid > 0)
        {
            FireSalvo(t, paid, spreadScale);
        }
    }

    /// <summary>按当前炮口角度打出一轮齐射。<paramref name="spreadScale"/> 用来做"扇形"（放宽）与"弹幕"（收窄）。</summary>
    void FireSalvo(Team t, int balls, float spreadScale)
    {
        float barrel = MuzzleDistance;
        float spread = TurretSpread * MathF.Max(0.01f, spreadScale);

        for (int shot = 0; shot < balls && _swarm.Count < MaxBalls; shot++)
        {
            // 一轮内的散布：以炮口为中心均匀铺开，再叠一点随机，免得每次齐射都一模一样
            float offset = balls > 1
                ? (shot / (float)(balls - 1) - 0.5f) * 2f * spread
                : 0f;
            offset += (float)(_matchRng.NextDouble() - 0.5) * spread * 0.5f;
            SpawnAt(t, t.TurretAngle + offset, barrel, LaunchEnergy);
        }
    }

    /// <summary>360° 全向散射（"烟花"格）：把一轮球均匀铺满一圈，从炮口炸开。</summary>
    void FireFirework(Team t)
    {
        int balls = PayForSalvo(t, DropFireworkBalls);
        if (balls == 0)
        {
            return;
        }

        float barrel = MuzzleDistance;
        float step = MathF.Tau / balls;

        for (int i = 0; i < balls; i++)
        {
            SpawnAt(t, i * step, barrel, LaunchEnergy);
        }
    }
}

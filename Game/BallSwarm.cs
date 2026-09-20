using System;
using System.Runtime.CompilerServices;

namespace TerritoryWar.Game;

/// <summary>
/// 一群弹珠。每颗有自己的<b>能量</b>，能量同时决定三件事：
///
///   · 半径   —— **两段式**：小球恒定半径；大球在 50~100 之间随 √能量增长（见 <see cref="RadiusForEnergy"/>）
///   · 攻击力 —— 能量 ÷ 200（靠它打穿领土防御）
///   · 生命   —— 大球的生命池**就是能量**；小球另有一个寿命池（时间 + 敌方格，见 <see cref="Step"/>）
///
/// 而抢地盘本身要花代价。于是"扩张"和"存活"天然互相牵制 —— 这正是这个题材
/// 最核心的平衡来源，不需要额外设计什么惩罚机制。
///
/// **只有大球参与碰撞**：小球互相穿透、也穿透大球；两颗敌对的大球撞上则弹开、各掉一份能量。
///
/// 数组用"紧凑排列 + 死亡标记 + 收尸"管理。⚠ 不变式是「**收尸之后** 0..Count-1 里没有死球」，
/// 而**不是**"数组里永远没有死球"：<see cref="KillTeam"/> 是外部路径，标记与收尸之间隔着一个
/// 帧边界。所以**每个遍历弹珠的循环都必须自己查 `_dead[i]`**（见 <see cref="Step"/> 开头）。
/// </summary>
public sealed class BallSwarm
{
    // 半径是**两段式**的，中间没有连续过渡：
    //   · 小球（能量 < BigBallEnergy）：恒定 SmallRadius，**不随能量变化**。
    //   · 大球（能量 ≥ BigBallEnergy）：在 [BigMinRadius, BigMaxRadius] 之间随能量增长。
    // 也就是说"变大"这件事本身就等于"晋级成大球" —— 小球永远只有一种大小，
    // 一旦攒够就跳进大球档，之后才继续长。
    //
    // ⚠ 半径同时是**判定宽度**：每步消耗 ≈ 推进扫过的格子数 × 每格成本 ∝ r × 速度。
    //   小球半径 2 的涂色盘 13 格，所以小球在敌境上烧钱慢、活得久；
    //   大球半径 48，每步要为新盖到的上百格付钱，一进敌境就烧得飞快 ——
    //   这个"小球耐活、大球脆"的对比是两段式自然带出来的，不是额外设计的。
    //   ⚠ 半径 1 → 2 时，每步扫过的新格子数翻倍（≈ 2r × 速度），
    //     所以 SmallBallCellLifeCost 也一起减半，射程/寿命才保持不变。
    const float SmallRadius = 2f;
    const float EnergyPerPower = 200f;  // 攻击力 = 能量 / 200

    /// <summary>攒到多少能量才从"小球"晋级成"大球"。低于它是恒定的 SmallRadius。</summary>
    public const float BigBallEnergy = 4000f;

    /// <summary>
    /// 小球的寿命（秒）—— 时间那一份的**总预算**。
    ///
    /// 定它的标准是"**别让时间成为射程的闸门**"：小球从角上出发，最远的直线路径是对角
    /// 1414 格（1000√2），速度 90 格/秒 ⇒ 需要 15.7 秒。取 16 秒（= 1440 格）就覆盖了
    /// 任意直线路径；横向 1000 格只要 11.1 秒，更有余量。
    ///
    /// ⚠ 所以它**不再是射程**。真正决定"能推多远"的是踩敌方格的消耗
    /// （<see cref="SmallBallCellLifeCost"/>）：自家地盘上不花命，理论上可以横穿全图；
    /// 一进敌境就开始烧命，烧完为止。原来 6 秒（540 格 < 580 格的中线距离）是拿寿命
    /// 当射程用，属于用一个旋钮管两件事。
    ///
    /// 由 GameRoot 的 <c>[Export] SmallBallLifetime</c> 写入。小球能量是冻结的，
    /// 所以"能量耗尽"这条死法对它不适用，改用寿命（生命池）收尸；
    /// 人口的上限则是 <c>射速 × 寿命</c>。
    /// </summary>
    public float SmallBallLifetime = 16f;

    /// <summary>
    /// 小球每涂掉**一格敌方格**要付的生命（秒）。大球的敌方格成本从能量池里扣
    /// （<c>cellCost</c>），小球没有能量池、只有生命池，于是同一条规则换个池子：
    /// **踩敌境就是花命**。时间那份照常每秒扣，两份合起来才决定小球能推多深。
    ///
    /// 0.025 的读法：**40 格敌方格 = 1 秒生命**。速度 90 格/秒、涂色盘半径 2，
    /// 敌境里每秒大约涂掉 360 格，也就是每秒额外烧 9 秒生命 —— 合计 10 倍流失。
    /// 于是一颗满生命（16 秒）的球进入纯敌境后只撑约 1.6 秒、推进约 150 格；
    /// 而它在自家地盘上飞多少格都不花命，所以"能不能飞完整张图"由时间决定，
    /// "能打进对面多深"由这个数决定。
    ///
    /// ⚠ 这个值和 <see cref="SmallRadius"/> 是绑在一起的：半径翻倍 ⇒ 每步扫过的新格子翻倍，
    /// 所以半径从 1 改成 2 时这里从 0.05 减到 0.025，射程/寿命才保持不变。
    ///
    /// ⚠ 盘子里**染不动**的格子（对方强度 > 攻击力 10）会每步反复收钱 —— 那是"撞墙被磨死"，
    /// 不是重复计费：普通格子染成自己的之后，下一步就被同阵营判断跳过了。
    /// </summary>
    public float SmallBallCellLifeCost = 0.025f;

    /// <summary>
    /// 两颗**敌对**大球撞上时，双方各掉多少能量。
    ///
    /// 不是"谁大谁赢、小的直接消失"（那是旧规则）：现在撞上就是两边一起弹开、
    /// 一起掉一份能量。所以大球互撞是**互相消耗**，能量少的先跌回小球档 ——
    /// 但它不会当场蒸发，仍然会弹开继续走。
    ///
    /// 同队大球只弹开、不掉能量，否则自己人互相削弱太亏了。
    /// </summary>
    public float ClashEnergyCost = 1500f;

    /// <summary>
    /// 一次"接触"的冷却（秒）：敌对大球撞上后，这段时间内两颗球不再互相扣能量。
    ///
    /// 为什么需要它：擦身而过的两颗球可能**连续几十帧都还在重叠区里**，
    /// 逐帧扣的话一次接触会被算成几十次（能量瞬间见底）。冷却期间照旧弹开、只是不再扣。
    /// 商业版用的是 `_collisionCooldowns = 0.4`，这里对齐。
    /// </summary>
    public float ClashCooldown = 0.4f;

    /// <summary>
    /// 一帧最多把重叠的两颗球分开多少（每颗各一半）。正常重叠（≤ 一步的位移 ≈3 格）
    /// 用不到它；它只用来兜住"出生就重叠""角落堆成一堆"这类深度重叠，避免一帧瞬移。
    /// </summary>
    const float MaxSeparationPerStep = 6f;

    /// <summary>刚晋级时的大球半径。也是 GameRoot 判定"按大球画"的阈值来源。</summary>
    public const float BigMinRadius = 50f;

    /// <summary>
    /// 能量封顶时的大球半径 —— 也就是**大球画出来的最大半径**。
    /// ⚠ 涂色盘的上限 <see cref="Disk.MaxRadius"/> 就是从这个值来的：
    ///   两者必须一致，否则球压过去之后，亮环以内会留一圈没被涂掉的地。
    /// </summary>
    public const float BigMaxRadius = 100f;

    /// <summary>
    /// 航向抖动速度（弧度/秒）。这个值决定了弹珠是"滚"还是"飘"。
    /// **只作用于小球** —— 大球是出膛定死方向的"保龄球"（见 <see cref="Step"/>）。
    ///
    /// 取 0.15 是为了对齐同类游戏：那里的弹珠是**近乎直线滚动、撞墙反弹**，
    /// 靠直线横穿全图自然就会踩进敌境；踩到敌境才烧能量、才会死，人口因此自然受控。
    ///
    /// 早先取 0.8（方向每 1.25 秒就完全随机化）其实是把它做成了随机游走 ——
    /// 球只会在出生点附近打转，既不铺领土也不减员。当时的补救是加一个"远离基地"的
    /// 外推力（OutwardSteer）硬把球赶出去，但那会画出放射状辐条，而且完全没必要。
    /// 把转向率降下来，一个问题就解决了，外推力已删除。
    ///
    /// 实测（TurnRate=0.15、无外推力、IncomePerCell=0.8）：弹珠稳定在 ~3000，
    /// 8 队 76 秒淘汰到 3 队，人口远离 20000 上限。
    /// </summary>
    const float TurnRate = 0.15f;

    /// <summary>能量低于这个值就消失（太弱了，留着也没用）。</summary>
    const float MinEnergy = 100f;

    /// <summary>
    /// 半径曲线的**上端点** —— 注意它已经不是"能量上限"了。
    ///
    /// 半径映射是"√能量在 [√<see cref="BigBallEnergy"/>, √<see cref="MaxEnergy"/>] 上线性插值"，
    /// 所以这个值决定的是"多少能量才画到最大半径 <see cref="BigMaxRadius"/>=100"。
    ///
    /// ⚠ 现在**没有任何回能途径**（大球能量只减不增、小球出膛就冻结），实测能到的最高能量
    ///   就是 <c>GameRoot.BigBallEnergyMax</c>（默认 30000 ⇒ 半径 75、远远够不到 100）。
    ///   所以"想让大球更大"应该调 <c>BigBallEnergyMax</c>（多给能量），或者调这里
    ///   （把曲线压扁、同样能量画得更大）—— 但后者会把**所有**大球的尺寸一起放大。
    /// </summary>
    public const float MaxEnergy = 80000f;

    /// <summary>缓冲容量（= <c>GameRoot.MaxBalls</c>）；超过它 <see cref="Spawn"/> 会拒绝，外部不必读。</summary>
    readonly int _capacity;

    public int Count;

    /// <summary>活跃弹珠的数据列，永远紧凑排在 <c>0..Count-1</c>（死掉的由 <see cref="Compact"/> 收尸）。</summary>
    public readonly float[] X;
    public readonly float[] Y;
    public readonly float[] Energy;
    public readonly byte[] Team;

    /// <summary>本步被标记死亡的球（<see cref="Compact"/> 之后就不存在了，所以外部不必读）。</summary>
    readonly bool[] _dead;

    readonly float[] _heading;
    readonly float[] _life;      // 小球剩余寿命（大球不读它，只在降级时被重新赋值）
    readonly float[] _clashCooldown;   // 大球碰撞后的冷却，见 ClashCooldown
    readonly int[] _bigIndices;  // 碰撞枚举用的"大球下标"暂存区（复用，不每帧分配）
    readonly Random _rng;

    public BallSwarm(int capacity, int seed)
    {
        _capacity = capacity;
        X = new float[capacity];
        Y = new float[capacity];
        Energy = new float[capacity];
        Team = new byte[capacity];
        _dead = new bool[capacity];
        _heading = new float[capacity];
        _life = new float[capacity];
        _clashCooldown = new float[capacity];
        _bigIndices = new int[capacity];
        _rng = new Random(seed);
    }

    // 下面这几个是每步每颗球都要调的叶子方法，标上内联 / 跳过分层编译。
    // 标注方式抄自商业版（那边热路径上挂了十几处 MethodImpl）。
    /// <summary>
    /// 当前半径 —— **由能量现算**，不缓存。
    ///
    /// ⚠ 这里曾经缓存成一列 `_radius[]`、只在 Step 每颗球处理完时刷新一次。问题不在性能
    ///   （现算只是一次比较，大球多一次开方），而在**正确性**：`ResolveCollisions` 与
    ///   `GameRoot.AttackBases` 都会在 Step 之外改能量，缓存就会滞后 —— 表现为"被撞回小球档的
    ///   大球还以一帧的大尺寸出现、基地判定半径也大一帧"。派生量就现算，没有第二处需要同步。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float RadiusOf(int i) => RadiusForEnergy(Energy[i]);

    /// <summary>当前航向（弧度）。拖尾残影要沿运动反方向摆，所以要暴露出去。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float HeadingOf(int i) => _heading[i];

    /// <summary>攻击力 = 能量 / <see cref="EnergyPerPower"/>，夹在 1..255。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int PowerOf(int i) => Math.Clamp((int)(Energy[i] / EnergyPerPower), 1, 255);

    public bool Spawn(float x, float y, float energy, byte team, float heading)
    {
        if (Count >= _capacity)
        {
            return false;
        }

        int i = Count++;
        X[i] = x;
        Y[i] = y;
        Energy[i] = energy;
        Team[i] = team;
        _dead[i] = false;
        _heading[i] = heading;
        _life[i] = SmallBallLifetime;
        _clashCooldown[i] = 0f;
        return true;
    }

    /// <summary>
    /// 能量 → 半径。**这是"半径"的唯一来源**（视觉与涂色盘都走它），所以它也是
    /// "小球 / 大球"分界线的唯一来源（<see cref="BigBallEnergy"/> ⇔ <see cref="BigMinRadius"/>）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RadiusForEnergy(float energy)
    {
        // 小球：恒定大小，不随能量变化
        if (energy < BigBallEnergy)
        {
            return SmallRadius;
        }

        // 大球：用 √能量 插值而不是能量本身 —— 前段涨得快、后段放缓，
        // 和"刚晋级时最有存在感"的直觉一致，也避免大部分区间挤在曲线的平坦端。
        float t = Math.Clamp(
            (MathF.Sqrt(energy) - BigBallSqrtMin) / BigBallSqrtSpan, 0f, 1f);

        return BigMinRadius + (BigMaxRadius - BigMinRadius) * t;
    }

    /// <summary>√BigBallEnergy 与 √MaxEnergy − √BigBallEnergy，预先算好免得每颗球每步都开方。</summary>
    static readonly float BigBallSqrtMin = MathF.Sqrt(BigBallEnergy);
    static readonly float BigBallSqrtSpan = MathF.Sqrt(MaxEnergy) - BigBallSqrtMin;

    /// <summary>
    /// 走一步。
    ///
    /// **两档球各有一个"生命池"，规则同一条；扣的来源也同一条：时间 + 敌方格。**
    ///   · 小球（能量 &lt; BigBallEnergy）：生命池就是剩余寿命（<see cref="SmallBallLifetime"/> 秒），
    ///     能量冻结（不扣维持费、不回收）。
    ///     时间是每秒 −1 秒，敌方格是每格 −<see cref="SmallBallCellLifeCost"/> 秒，≤0 销毁。
    ///   · 大球（能量 ≥ BigBallEnergy）：生命池就是能量。时间是维持费每秒 −120，
    ///     敌方格是每格 −cellCost，&lt; MinEnergy 销毁。**能量只减不增** ——
    ///     没有任何回复途径，所以出膛那一刻的能量就是它这一辈子的全部预算。
    /// **没有"一次性爆炸"**：大球压进敌境就是一路逐格磨过去，磨穿能量就跌回小球档。
    /// 所以"变大"完全由炮塔决定（见 GameRoot.TickBigShots 的蓄力射击），小球自己攒不出来。
    /// </summary>
    public void Step(float dt, TerritoryMap map, float speed, int cellCost,
        float upkeepPerSecond)
    {
        for (int i = 0; i < Count; i++)
        {
            // ⚠ 不变式守卫：`_dead` 是"收尸前"的软标记，而置位不只发生在 Step 里
            //   （KillTeam 是外部路径，一局里在 AttackBases 结算时触发）。
            //   收尸（Compact）一帧只跑一次，所以**任何遍历弹珠的地方都不能假设"活着的才在数组里"** ——
            //   这里不查的话，被淘汰那家的球会在被标记后的下一步继续移动、涂色，
            //   把刚分给幸存者的地重新涂成已出局势力的颜色（地图上的"鬼影领地"）。
            if (_dead[i])
            {
                continue;
            }

            bool isBig = Energy[i] >= BigBallEnergy;

            // 碰撞冷却（只有大球会被设置，但判断很便宜，直接对所有球走一遍）
            if (_clashCooldown[i] > 0f)
            {
                _clashCooldown[i] -= dt;
            }

            if (isBig)
            {
                // 维持费：大球是会老化的。没有这一条，缩在自己地盘上的大球永远不掉能量。
                Energy[i] -= upkeepPerSecond * dt;
            }

            // **大球直线横推，小球才随机抖。**
            // 大球是"保龄球"：出膛定死一个方向，一路直着推过去，撞墙 / 撞球才改向
            // （改向由 BounceOffWalls 和 ResolveCollisions 负责，都不是随机的）。
            // 保留抖动的话它会在路上慢慢拐弯，玩家没法预测它去哪 —— 而大球是花本钱打出来的，
            // 方向可预测才有意义。小球是散开的子弹，抖一点反而让弹幕更自然。
            if (!isBig)
            {
                _heading[i] += (float)(_rng.NextDouble() - 0.5) * TurnRate * dt;
            }

            X[i] += MathF.Cos(_heading[i]) * speed * dt;
            Y[i] += MathF.Sin(_heading[i]) * speed * dt;
            BounceOffWalls(i, map.Width, map.Height);

            if (isBig)
            {
                // ⚠ 这里**没有回能**。能量只减不增 ——
                // 出膛那一刻的能量（GameRoot 随机摇出来的）就是它这一辈子的全部预算：
                // 时间扣维持费、敌方格按格扣，扣完跌回小球档。
                //
                // 出膛就染色 —— **没有"解除保险"那种光飞不染的窗口**。
                // 那段窗口是当年为"一次性爆炸"加的：出膛第一步踩到敌境就会自爆，所以要让它
                // 先飞一段。释放删掉之后它只剩"前 1.5 秒什么都不做"这一个效果，
                // 实测表现是"大球扫过敌方领土却不填色"（玩家一眼就能看出来）。
                // 实测把窗口去掉后大球数量 5~14，和保留 1.5 秒时（7~14）没有区别，
                // 因为它本来就是在自己地盘上出膛的 —— 自家格子不收费。
                //
                // 逐格花钱染色：压进敌境就是一路磨过去，每格扣 cellCost，
                // 能量磨穿就跌回小球档。每步只对**新盖到**的格子收钱
                // （上一步染成自己的格子会被跳过），所以是"按推进速度烧钱"而不是
                // "按盘子面积烧钱"：盘子 48、每步走 1.5 格 ⇒ 每步约 2×48×1.5 ≈ 144 格。
                // ⚠ 池子（能量 / 生命）由**这里**决定并传进去 —— 见 Paint 的注释：
                //   让 Paint 自己再判一次"是不是大球"会和这一步的维持费打架。
                Paint(i, map, cellCost, 0f, usesEnergyPool: true);

                // 能量掉回小球档（被敌境磨穿了）：降级成小球并重新给一段寿命，
                // 否则它会带着"大球的倒计时"立刻消失。
                DowngradeOrKill(i);
            }
            else
            {
                // 小球：能量不随时间变，但**踩敌境要花生命**（能量那份是 0）。
                Paint(i, map, 0, SmallBallCellLifeCost, usesEnergyPool: false);
                _life[i] -= dt;
                if (_life[i] <= 0f)
                {
                    _dead[i] = true;
                }
            }
        }
    }

    void BounceOffWalls(int i, int width, int height)
    {
        float r = RadiusOf(i);

        if (X[i] < r) { X[i] = r; _heading[i] = MathF.PI - _heading[i]; }
        else if (X[i] > width - r) { X[i] = width - r; _heading[i] = MathF.PI - _heading[i]; }

        if (Y[i] < r) { Y[i] = r; _heading[i] = -_heading[i]; }
        else if (Y[i] > height - r) { Y[i] = height - r; _heading[i] = -_heading[i]; }
    }

    /// <summary>
    /// 涂一遍圆盘。半径取**当前能量**映射出来的半径（与画出来的圆同一个值，见 <see cref="Disk"/>）。
    ///
    /// <paramref name="usesEnergyPool"/> 决定"这一格记在哪个池子上"：大球扣能量、小球扣生命。
    /// ⚠ **这个判定必须由调用方（<see cref="Step"/>）给出，不能在这里重算**：
    ///   大球每步先扣维持费，若这里用扣完的能量再判一次，就会出现"Step 认为是大球（走能量分支）、
    ///   Paint 认为是小球（改扣生命）"的错账 —— 盘内敌方格超过 640 时那一步的生命会被直接扣光，
    ///   于是"该扣能量"的大球在敌境里凭空暴毙（实测踩过）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void Paint(int i, TerritoryMap map, int cellCost, float cellLifeCost, bool usesEnergyPool)
    {
        int cx = (int)X[i];
        int cy = (int)Y[i];
        byte team = Team[i];
        int power = PowerOf(i);
        Disk.Row[] rows = Disk.RowsFor((int)RadiusOf(i));

        // 逐行扫：一行内 x 是连续的，所以每行只需要把横向那一段夹进地图，
        // 顺序与"逐格 dy 从小到大、每行 dx 从小到大"完全一致。
        for (int k = 0; k < rows.Length; k++)
        {
            Disk.Row row = rows[k];
            int y = cy + row.Dy;
            if ((uint)y >= (uint)map.Height)
            {
                continue;
            }

            int x0 = cx + row.XMin;
            int x1 = cx + row.XMax;
            if (x0 < 0)
            {
                x0 = 0;
            }

            if (x1 >= map.Width)
            {
                x1 = map.Width - 1;
            }

            if (x0 > x1)
            {
                continue;
            }

            int rowStart = y * map.Width;
            for (int x = x0; x <= x1; x++)
            {
                if (map.Owner[rowStart + x] == team)
                {
                    continue;   // 自己的地不花钱
                }

                // 涂一格敌方格要付代价 —— 大球付能量，小球付生命。池子不同，规则是同一条。
                // 已经染成自己的格子在下一步会被上面那句跳过，所以这里天然就是"每格只收一次"，
                // 只有**染不动**的格子（对方强度高于攻击力）才会每步反复收钱 —— 那是"撞墙被磨死"。
                if (usesEnergyPool)
                {
                    Energy[i] -= cellCost;
                    if (Energy[i] < MinEnergy)
                    {
                        Energy[i] = 0f;
                        return;     // 能量耗尽，这一颗打到头了
                    }
                }
                else
                {
                    _life[i] -= cellLifeCost;
                    if (_life[i] <= 0f)
                    {
                        _dead[i] = true;
                        return;     // 生命耗尽，死在敌境里
                    }
                }

                map.Attack(x, y, team, power);
            }
        }
    }

    /// <summary>
    /// 把一个额外的球放到 (x, y)（能量为 energy）会不会压到已有的球上？
    /// 炮塔发射前用它检查炮口 —— 见 GameRoot.TickBigShots。
    /// </summary>
    public bool BigBallBlockedAt(float x, float y, float energy)
    {
        float r = RadiusForEnergy(energy);

        for (int i = 0; i < Count; i++)
        {
            if (_dead[i] || Energy[i] < BigBallEnergy)
            {
                continue;   // 小球不参与碰撞，压上去也没关系
            }

            float reach = r + RadiusOf(i);
            float dx = x - X[i];
            float dy = y - Y[i];
            if (dx * dx + dy * dy < reach * reach)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 大球碰撞：**弹开**，如果对方是敌人则两边各掉一份能量。
    ///
    /// ─── 为什么是"直接两两枚举"，不用空间哈希 ────────────────────────────────
    /// 只有大球互撞，而大球**数量天然很少**（炮塔每 1.2 秒才打一颗，实测同时在场 10~15 颗），
    /// 所以两两比对只要 ~100 次距离判断 —— 比维护一张哈希表还便宜。
    /// 参考实现（商业版 `BallManager.ProcessBigBallCollisions`）也是这么做的：
    /// 先把大球下标收进一个列表，再双重循环。
    ///
    /// ⚠ **这里曾经用空间哈希，那是个真缺陷。** 哈希格子边长是 `BigMinRadius`(50)，
    /// 而大球的判定半径是 53~75 ⇒ 判定距离 `r_a + r_b` 高达 **106~150**，
    /// 远超"3×3 邻域保证能找到"的范围(50)。后果就是两颗球要**深深重叠之后**才被判定成碰撞：
    /// 一帧里被推开几十格（突兀），而且"要贴上走一段才触发"。
    /// 哈希那套 O(n) 是为上万颗小球准备的，但小球根本不参与碰撞 —— 规模假设用错了地方。
    ///
    /// 弹开用的是**等质量弹性碰撞**：两颗球速度大小相同，所以只需要交换
    /// "沿碰撞法线方向"的速度分量，切向不动 —— 就是台球那一套。
    /// 位置把重叠量对半推开：判定准了之后重叠最多只有一步的位移（≈1.5 格），
    /// 所以这一步推得很轻，不会看出"跳一下"。
    /// </summary>
    public void ResolveCollisions()
    {
        // 收大球下标。小球互相穿透（固定能量的子弹，让它们参与碰撞的话，
        // 大球还没走到敌境就被弹幕啃回小球档了），所以整张哈希表都是多余的。
        int bigCount = 0;
        for (int i = 0; i < Count; i++)
        {
            if (!_dead[i] && Energy[i] >= BigBallEnergy)
            {
                _bigIndices[bigCount++] = i;
            }
        }

        if (bigCount < 2)
        {
            return;
        }

        for (int ai = 0; ai < bigCount - 1; ai++)
        {
            int a = _bigIndices[ai];

            for (int bi = ai + 1; bi < bigCount; bi++)
            {
                int b = _bigIndices[bi];

                // 前面的碰撞可能已经把它们中的一颗降级/销毁了
                if (_dead[a] || _dead[b]
                    || Energy[a] < BigBallEnergy || Energy[b] < BigBallEnergy)
                {
                    continue;
                }

                float reach = RadiusOf(a) + RadiusOf(b);
                float dx = X[a] - X[b];
                float dy = Y[a] - Y[b];
                float distSq = dx * dx + dy * dy;

                if (distSq >= reach * reach)
                {
                    continue;   // 没碰上
                }

                float dist = MathF.Sqrt(distSq);
                float nx, ny;

                if (dist > 1e-4f)
                {
                    nx = dx / dist;
                    ny = dy / dist;
                }
                else
                {
                    // 完全重合：法线取不到，随手给一条固定方向（不然会除以 0）
                    nx = 1f;
                    ny = 0f;
                    dist = 0f;
                }

                // 1) 把重叠量对半推开。判定准了之后重叠只有一步的位移量（≤3 格），
                //    所以这一步很轻；不推开的话下一帧还在重叠区里、会被反复判定成碰撞。
                //
                // ⚠ 再套一个**每帧上限**兜底：正常情况（重叠 ≤3 格）根本碰不到它，
                //    但万一是"出生时就叠在一起"或角落里挤成一堆（重叠能到上百格），
                //    一帧推上百格就是"瞬移"—— 用户反馈的"碰撞很突兀"正是这个。
                //    有上限之后最坏也是几十帧内平滑分开，不会闪现。
                float push = MathF.Min(reach - dist, MaxSeparationPerStep) * 0.5f;
                X[a] += nx * push;
                Y[a] += ny * push;
                X[b] -= nx * push;
                Y[b] -= ny * push;

                // 2) 等质量弹性碰撞：交换法线方向的速度分量，切向保留。
                //    速度大小两颗球一样（都是 speed），所以这里只用单位方向向量算。
                float cax = MathF.Cos(_heading[a]);
                float say = MathF.Sin(_heading[a]);
                float cbx = MathF.Cos(_heading[b]);
                float sby = MathF.Sin(_heading[b]);

                float an = cax * nx + say * ny;
                float bn = cbx * nx + sby * ny;

                // an < bn 才是"正在互相接近"：n 是从 b 指向 a 的单位向量，
                // 两者的距离变化率 = (v_a − v_b)·n = an − bn，小于 0 才是靠近。
                //
                // ⚠ 这里**曾经写成 an > bn（反了）**，后果是"接近时一次都不改航向"：
                // 球只会被"把重叠量对半推开"那点位移慢慢挤开，看起来就是**粘在一起、
                // 几乎不反弹**（实测平均偏转只有 1.4°~6.2°，22 次重叠里只有 7 次真改了方向）。
                // 已经分开的两颗（被前面的碰撞弹过了）不能再交换一次，
                // 否则会被反复弹来弹去。
                if (an < bn)
                {
                    float swap = bn - an;
                    _heading[a] = MathF.Atan2(say + swap * ny, cax + swap * nx);
                    _heading[b] = MathF.Atan2(sby - swap * ny, cbx - swap * nx);
                }

                // 3) 敌方大球：两边各掉一份能量。**不掉血、不吞噬、也不判谁赢** ——
                //    撞一下就是两败俱伤，能量少的先跌回小球档。
                //    同队只弹不开火（下面这个 if）。
                //
                // ⚠ 用**冷却**把"一次接触"和"一帧"解耦：擦身而过的两颗球可能连续几十帧
                //    都还在重叠，逐帧扣就会把一次接触算成几十次。冷却期间照旧弹开、只是不再扣能量。
                //    （商业版的 `_collisionCooldowns` 是 0.4 秒，这里对齐。）
                if (Team[a] != Team[b] && _clashCooldown[a] <= 0f && _clashCooldown[b] <= 0f)
                {
                    Energy[a] = MathF.Max(0f, Energy[a] - ClashEnergyCost);
                    Energy[b] = MathF.Max(0f, Energy[b] - ClashEnergyCost);

                    _clashCooldown[a] = ClashCooldown;
                    _clashCooldown[b] = ClashCooldown;

                    DowngradeOrKill(a);
                    DowngradeOrKill(b);
                }
            }
        }
    }

    /// <summary>
    /// 大球能量跌破门槛后的收尾：还在大球门槛以上就什么都不做；
    /// 跌破 BigBallEnergy 就降级成小球（顺手给一段小球寿命），跌破 MinEnergy 就销毁。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void DowngradeOrKill(int i)
    {
        if (Energy[i] < MinEnergy)
        {
            _dead[i] = true;
        }
        else if (Energy[i] < BigBallEnergy)
        {
            _life[i] = SmallBallLifetime;
        }
    }

    /// <summary>把死掉的弹珠用末尾的顶掉，保持 0..Count-1 紧凑。</summary>
    public void Compact()
    {
        int i = 0;
        while (i < Count)
        {
            if (!_dead[i])
            {
                i++;
                continue;
            }

            int last = --Count;
            if (i != last)
            {
                X[i] = X[last];
                Y[i] = Y[last];
                Energy[i] = Energy[last];
                Team[i] = Team[last];
                _dead[i] = _dead[last];
                _heading[i] = _heading[last];
                _life[i] = _life[last];
                _clashCooldown[i] = _clashCooldown[last];
            }
        }
    }

    /// <summary>
    /// 这颗球是否已被标记死亡（还没被 <see cref="Compact"/> 收尸）。
    ///
    /// ⚠ 这是给"遍历弹珠的其他系统"用的不变式查询：`KillTeam` 是外部路径、标记与收尸之间
    ///   隔着一个帧边界，所以**任何**遍历弹珠的循环都必须先查它，不能假设数组里只有活球。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDead(int i) => _dead[i];

    /// <summary>清掉某支势力的所有弹珠（它出局时用）—— **立刻收尸**，而不是只打死亡标记。
    ///
    /// ⚠ 只打标记的话，这些球在下一次 <see cref="Step"/> 里还会再走一步（移动 + 涂色），
    ///   把刚分给幸存者的地重新涂成已出局势力的颜色 —— 地图上会出现"鬼影领地"，
    ///   而且该队的 <c>CellsByTeam</c> 会重新变成非 0（面板上"已出局"却还有格数）。
    ///
    /// ⚠ 本方法会**改变数组顺序**（收尸用的是"末尾顶掉"），所以不能在遍历弹珠的循环中途调用 ——
    ///   `GameRoot.AttackBases` 因此把出局清算推迟到那一趟循环结束之后。
    /// </summary>
    public void KillTeam(byte team)
    {
        for (int i = 0; i < Count; i++)
        {
            if (Team[i] == team)
            {
                _dead[i] = true;
            }
        }

        Compact();
    }

    /// <summary>
    /// 从外部扣一颗球的能量（基地伤害走这里）。
    ///
    /// ⚠ **能量只有这一条外部写入口**：能量的跨档规则（跌破 <see cref="BigBallEnergy"/> 降级、
    ///   跌破 <see cref="MinEnergy"/> 销毁）只由 <see cref="DowngradeOrKill"/> 维护，
    ///   外部直接写 <c>Energy[i]</c> 会绕过它 —— 曾经就是这样漏出一条"被基地磨到 0 能量、
    ///   却因为是 0 而被当成小球、靠出生时那 16 秒寿命继续活着"的球。
    ///
    /// ⚠ 收尾只对**原本是大球**的球做：小球按规则就该待在小球档，对它跑一次收尾
    ///   会把它的寿命重置成满格（变成永动机）。
    /// </summary>
    public void ApplyDamage(int i, float amount)
    {
        bool wasBig = Energy[i] >= BigBallEnergy;
        Energy[i] = MathF.Max(0f, Energy[i] - amount);

        if (wasBig)
        {
            DowngradeOrKill(i);
        }
    }
}

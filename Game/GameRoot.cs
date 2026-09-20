using System;
using Godot;
using TerritoryWar.Game;

namespace TerritoryWar;

/// <summary>
/// 弹珠领土战争（CPU 版）—— 主控 / 组合根。**一局的规则都在这个类里**；
/// 纯数据与算法在 <see cref="TerritoryWar.Game"/> 命名空间下的几个类里。
///
/// 按职责拆成 5 个 partial 文件，改哪一块就开哪一块：
///   · `GameRoot.cs`（本文件）—— 所有可调旋钮（[Export]）、运行时状态、每帧顺序、按键
///   · `GameRoot.Setup.cs` —— 开局前的一次性装配：认领节点、地图贴图、着色器参数、摄像机
///   · `GameRoot.Round.cs` —— 一局的生命周期：开局分地、出局清算、胜负判定
///   · `GameRoot.Simulation.cs` —— 单个模拟步里的一切：经济、炮塔投料、大球、基地攻防、落门结算
///   · `GameRoot.Presentation.cs` —— 把状态推给场景：领土贴图、基地材质、能量数字、右侧面板
///
/// 一局的循环：
///   四角各一家、各一座炮塔，开局地图就按"离哪个基地最近"切成四个象限，**没有中立地**
///   按领土面积收入能量 → 炮塔把能量投成**抽签球**，掉进左侧钉板
///   落进"×2/×4/×8"或"转盘"门则该队下一轮齐射按倍率放大，落进"发射"格才真的打出一轮齐射
///   （底排另外 6 格是弹药/护盾/大球/弹幕/扇形/烟花，板面与实测见 docs/tuning-log.md §5.2）
///   弹珠滚出去抢地盘；**踩敌境要花命** —— 小球花寿命、大球花能量，花完就没了
///   两颗敌对的球撞上就弹开、双方各掉一份能量（只有大球之间才互撞）
///   打掉对方炮塔基地就灭掉一家，最后存活者获胜
///
/// 版面（世界空间，**没有任何 CanvasLayer**）：左 = 掉落球板，中 = 地图，右 = 领土占领。
///   摄像机在 <see cref="SetupCamera"/> 里一次性把三块内容全框进来，之后不动。
///
/// **场景节点全部搭在 Scenes/Main.tscn 里，脚本一个节点都不建。**
///   面板的位置/尺寸/配色/字号直接在编辑器里调；连钉板的几何（钉、门、槽、横板）也是
///   <see cref="DropBoard"/> 从自己的子节点里读出来的，所以拖动那些节点就等于改物理。
///   脚本只做三件事：更新数据（文字、条宽、闪光、计数），模拟，画球。
///
/// 几条改代码前值得先知道的约束（详细原因见各字段旁的注释）：
///   1. 大球没有"一次性爆炸" —— 它压进敌境是逐格磨过去的，每格扣 cellCost。
///   2. 染色不能直接并行 —— 它同时改 owner 和 strength 两处共享单元格。
///   3. 弹珠层缓冲的长度必须严格等于 实例数 × 步长，且颜色槽偏移固定是 8（见 BallMeshWriter）。
///   4. 开火要过钉板：投料频率 × 落门比例是弹珠数量唯一的闸门，而且每轮消耗
///      必须小于同期收入，否则会一直哑火。
///
/// 实测性能、踩坑清单、玩法数值、节奏旋钮：见 docs/tuning-log.md
/// 换 GPU compute 方案的路线：见 docs/compute-architecture.md
/// </summary>
public partial class GameRoot : Node2D
{
    // ==================================================================
    // 可调旋钮（全在这一个文件里，检查器里按下面这些分组显示）
    // ==================================================================

    [ExportGroup("地图与势力")]
    [Export] public int MapSize = 1000;
    [Export] public int TeamCount = 4;               // 一家守一个角落，所以是 4
    [Export] public int Seed = 20260815;

    // ---- 经济：收入 → 炮塔开火 ----
    [ExportGroup("经济")]
    [Export] public float LaunchEnergy = 2000f;      // 发射一颗弹珠消耗的能量（也就是它的初始能量）
    [Export] public float IncomePerCell = 0.8f;      // 每秒每格领土产出多少能量
    [Export] public float BaseHitPoints = 8000f;
    [Export] public float BaseDamagePerSecond = 30f;     // 每颗小球贴着敌方基地时每秒转成多少伤害
                                                         // ⚠ 这是**按球累加**的，别调高：上千颗小球压上去
                                                         // 会线性叠乘（曾经 1500 时整局 16 秒就打完）
    [Export] public float BigBallBaseDamageScale = 12f;  // 大球的秒伤倍率（炮塔花本钱打出来的攻城武器）

    // ---- 炮塔：摆在四个角，摆动 + 往钉板投料 ----
    // 人口闸门 = 投料频率 × 落进"发射"格的比例 × 每轮球数 × 存活时长（见下面 DropFeedInterval）。
    // 实测 4 队 × 10 颗 / 0.4 秒只能维持约 550 颗球，画面太稀；每轮 24 颗后约 1400 颗。
    // 能量仍然是硬约束：一轮齐射消耗 LaunchEnergy × 球数，必须小于
    // "本方领土面积 × IncomePerCell × 投料间隔" 的量级，否则炮塔会一直哑火。
    [ExportGroup("炮塔")]
    [Export] public int TurretBallsPerShot = 24;       // 每轮打几颗（一轮齐射，不是单发）
    [Export] public float TurretSpread = 0.30f;        // 一轮内的角度散布（弧度，约 ±17°）
    [Export] public float TurretSwingSpeed = 0.5f;     // 摆动快慢（弧度/秒的相位速度）
    [Export] public float TurretSwingRange = 0.75f;    // 摆动幅度（弧度，约 ±43°）

    // ---- 左侧钉板（掉落球）：投料 → 掉落 → 穿过倍率门 → 落进底排某格 ----
    // 发射改成"先投料抽签、落格才结算"之后，人口的闸门从"齐射间隔"变成了
    // **投料频率 × 落进"发射"格的比例 × 每轮球数 × 倍率**（docs/tuning-log.md 那句
    // "炮塔射速是弹珠数量的唯一闸门"形态没变，只是中间插了一块板）。
    // 这几个数按**实测弹珠数**来配：投料太稀 → 画面空；太密 → 世界侧顶到 MaxBalls。
    // 实测（1000×1000、其余默认、仿真 13 秒；富板 = ×2/×4/×8 + 转盘 + 底排 7 格）：
    //   0.25 秒 / 上限 8（两门版）→ 弹珠 ~2140
    //   0.4  秒 / 上限 4（两门版）→ 弹珠 ~1300
    //   0.5  秒 / 上限 4（富板）  → 弹珠 ~1100（27.7 秒实测 1105）、板上 13~15、染色 0.30~0.35 ms/步
    // 倍率是"落格前连乘"的（穿过几枚门就乘几次），所以**上限就是发射格的球数倍数、
    // 也几乎就是人口的倍数** —— 这个 DropMultiplierCap 是人口的隐形旋钮。
    // ⚠ 富板底排有 7 格、"发射"只占 1/7，但人口不会跟着掉 7 倍：另外三格
    //   （弹幕 48 / 扇形 24 / 烟花 24）同样在生成球，两边基本抵消。
    [ExportGroup("掉落球板（投料）")]
    [Export] public float DropFeedInterval = 0.5f;     // 每队每隔多久往板里投一颗抽签球
    [Export] public int DropFeedPerRound = 1;          // 每个投料周期投几颗
    [Export] public int DropMultiplierCap = 4;         // 倍率门的连乘上限（防止攒出 ×32）

    // 底排照参考作品的 6 格（扇形/烟花/弹药/护盾/大球/弹幕）加上"发射"（Release 门）：
    //   发射 = 立刻打一轮齐射（球数 = 24 × 当前倍率，清空倍率）
    //   弹药 = 白送一份齐射能量（等于"这一签直接换一发"）
    //   护盾 = 基地护盾 N 秒（期间伤害按 DropShieldReduction 衰减）
    //   大球 = 立刻打出一颗大球（绕过 BigShotInterval，但照样摇能量、照样扣池子）
    //   弹幕 = 一轮密集齐射（球数 = DropBarrageBalls、散布收窄）
    //   扇形 = 一轮大扇形齐射（散布 × DropFanSpreadScale）
    //   烟花 = 360° 全向散射（球向四面八方飞出去）
    [ExportGroup("掉落球板（底排 7 格）")]
    [Export] public float DropShieldSeconds = 8f;
    [Export] public float DropShieldReduction = 0.25f;  // 护盾期间实际吃到的伤害比例
    [Export] public int DropBarrageBalls = 48;
    [Export] public float DropFanSpreadScale = 3f;
    [Export] public int DropFireworkBalls = 24;

    // ---- 蓄力射击：大球的唯一来源 ----
    // 小球是固定能量的子弹，自己攒不到大球门槛；大球由炮塔每隔一段时间单独打出来，
    // 出来就是半径 ≥ BigMinRadius（现 50）。所以"变大"是基地花本钱买的，不是野生的。
    [ExportGroup("大球")]
    [Export] public float BigShotInterval = 1.2f;      // 每座炮塔每隔多久打一颗大球
                                                       // 大球一直在场上滚（撞墙、撞球反弹），
                                                       // 所以"同时在场的大球数 ≈ 存活时长 ÷ 这个值"。
    [Export] public float BigBallEnergyMin = 6000f;    // 出膛能量**下限**（随机摇）
    [Export] public float BigBallEnergyMax = 30000f;   // 出膛能量**上限**
                                                       // 每颗大球出膛时在 [Min, Max] 之间随机摇一个能量，
                                                       // 摇到多少就是它这一辈子的全部预算（能量只减不增），
                                                       // 也就决定了它多大（半径 50~100 随 √能量）、能磨多远。
                                                       // ⚠ Min 必须 ≥ BallSwarm.BigBallEnergy(4000)，
                                                       // 否则摇出来的其实是小球。
                                                       // 炮塔按**摇出来的那个数**扣能量池。
    [Export] public float BigBallClashEnergyCost = 1500f; // 两颗敌对大球撞一下，各掉多少能量
    [Export] public float BigBallClashCooldown = 0.4f;   // 撞上后多久之内不再互相扣能量
                                                         // 擦身而过的两颗球会连续几十帧都还在重叠区里，
                                                         // 没有冷却的话一次接触会被算成几十次。
                                                         // 冷却期间照旧弹开，只是不再扣能量。

    // ---- 战场 ----
    // ⚠ MaxBalls 不只影响人口上限，还直接决定弹珠层的上传量：
    //   缓冲长度 = MaxBalls × (1 + TrailGhosts) × 步长，而且 Godot 的 Buffer 赋值是
    //   整块上传（VisibleInstanceCount 只控制画几个、不省上传）。调大它请连带上调成本。
    [ExportGroup("战场（弹珠层）")]
    [Export] public int MaxBalls = 8000;
    [Export] public float BallSpeed = 90f;           // 格/秒
    [Export] public float SmallBallLifetime = 16f;   // 小球寿命（秒）—— 时间那一份的总预算
                                                     // 定它的标准是"**别让时间成为射程的闸门**"：
                                                     // 最远直线路径是对角 1414 格（1000√2），
                                                     // 速度 90 格/秒 ⇒ 需要 15.7 秒，取 16 秒
                                                     // （= 1440 格）覆盖任意直线；横向 1000 格
                                                     // 才 11.1 秒，余量更大。
                                                     // 所以穿透深度**不看它**，看下一项。
                                                     // 它同时是人口上限：射速 × 阵营数 × 寿命。
    [Export] public float SmallBallCellLifeCost = 0.025f; // 小球每涂掉一格敌方格扣掉的生命（秒）
                                                     // **40 格敌方格 = 1 秒生命**。小球没有能量池，
                                                     // 敌境的代价从生命里扣（大球是扣能量）——
                                                     // 两档球的规则是同一条：时间 + 敌方格都扣，≤0 销毁。
                                                     // ⚠ 它和小球半径绑在一起：半径翻倍 ⇒ 每步扫过的
                                                     // 新格子翻倍 ⇒ 这个值要减半，射程才不变。
                                                     // （半径 1→2 时就是 0.05 → 0.025）
    [Export] public float BallUpkeepPerSecond = 120f; // 每颗球的维持费，用来稳定弹珠总数
    [Export] public float BaseSizeScale = 3f;        // 基地尺寸倍数（1 = 上一版的大小）
                                                     // 同时缩放基地判定半径、炮口距离、护盾环标识
    // ⚠ 这里原来还有一个 `BigBallRadius` 旋钮（"半径达到这个值就按大球画"）。它已经被删掉：
    //   半径本来就是能量的函数，"小球 / 大球"的分界线只有 `BallSwarm.BigBallEnergy` 一处，
    //   再多一个旋钮只会让"打起来是大球、看起来是小球"这种错配成为可能（见 BallMeshWriter）。
    [Export] public float SmallBallAlpha = 0.42f;    // 小球的透明度。要能看见、又不能糊住领土
    [Export] public int TrailGhosts = 0;             // 每颗球身后拖几个残影（0 = 关闭拖尾）
                                                     // 默认关：残影是一串分立的圆点，看起来像毛虫而不是拖尾，
                                                     // 而且会把弹珠层上传量乘以 (1+残影数)。想要真正的拖尾，
                                                     // 得沿路径画一条渐细的带状多边形，而不是叠几个圆。
                                                     // 想看效果就设 3~4，并把 TrailSpacing 调到 3~4。
    [Export] public float TrailSpacing = 7f;         // 残影间距（格）
    [Export] public float SimHz = 60f;
    [Export] public float CostRampSeconds = 25f;     // 每格成本**每这么多秒 +1**（1 → MaxCellCost）
                                                     // ⚠ 不是"从 1 涨到 16 需要多久"：现公式是
                                                     //   cost = clamp(1 + (int)(elapsed / 本值), 1, MaxCellCost)
                                                     //   所以涨满要 15 × 25 = 375 秒。实测 t=27s 时是 2。
    // ⚠ **大球没有回能**：能量只减不增（时间 + 敌方格），所以出膛摇到的那个数
    // 就是它这一辈子的全部预算。这里原来是"增殖门"（地图上撒 32 个圆圈、进去能量翻倍），
    // 后来改成"自家领土补给"，现在连补给也去掉了 ——
    // 大球的强弱完全由出膛时那一次随机决定。

    [ExportGroup("能量数字（大球上的数字）")]
    [Export] public float EnergyLabelMinEnergy = 4500f;  // 能量超过这个值才在球上显示数字
                                                     // ⚠ 必须**高于** BallSwarm.BigBallEnergy(4000)：
                                                     // 半径映射是两段式的，小球恒定（屏幕上约 1.4 px），
                                                     // 阈值压得比大球门槛低的话，就会出现"一串数字挂在小点上"。
                                                     // 需求本来就是"**大球上**显示能量"，所以跟着大球门槛走。
    [Export] public int EnergyLabelMaxCount = 24;    // 最多同时显示多少个数字（太多就是噪声）

    // ---- 场景节点（在 Main.tscn 里连好；node_paths 元数据不能少，否则解析不出节点）----
    [ExportGroup("场景节点")]
    [Export] public Sprite2D MapLayer;
    [Export] public MultiMeshInstance2D BallLayer;
    [Export] public Camera2D ViewCamera;
    [Export] public WorldEnvironment GlowLayer;
    [Export] public Node2D Bases;

    /// <summary>右侧"领土占领"面板（世界空间 Node2D）。</summary>
    [Export] public TerritoryPanel Territory;

    /// <summary>左侧钉板。模拟、绘制与落门事件都在它里面，这里只负责投料和消化事件。</summary>
    [Export] public DropBoard Drop;

    /// <summary>大球上的能量数字（世界空间 Node2D，自绘）。</summary>
    [Export] public EnergyLabels Labels;

    // ==================================================================
    // 常量
    // ==================================================================

    const int MaxCellCost = 16;
    const int MaxStepsPerFrame = 4;

    /// <summary>每重开一局，随机种子前进这么多个（保证同一 Seed 的下一局和上一局不同）。</summary>
    const int RngStreamStride = 7919;

    /// <summary>钉板随机源的种子偏移。钉板有自己的 <c>Random</c>，和世界侧错开取数。</summary>
    const int DropSeedOffset = 991;

    /// <summary>
    /// 弹珠群随机源的种子偏移。⚠ 必须和 <c>_matchRng</c> 错开：这两个 <c>System.Random</c>
    /// 曾经用**同一个表达式**播种，于是"小球航向抖动"和"炮口散布抖动"抽到的是同一串数。
    /// </summary>
    const int SwarmSeedOffset = 101;

    /// <summary>能量数字：文本节的流间隔（秒）。位置是每帧跟的，见 <c>_Process</c>。</summary>
    const double EnergyLabelInterval = 0.05;

    /// <summary>能量数字的文本刷新计时（秒）。</summary>
    double _energyLabelTimer;

    // ==================================================================
    // 运行时状态
    // ==================================================================

    TerritoryMap _map = null!;
    BallSwarm _swarm = null!;
    Team[] _teams = null!;

    /// <summary>弹珠层的整块缓冲写入器（布局契约 + 每帧上传）。</summary>
    BallMeshWriter _ballWriter = null!;

    /// <summary>四个基地的外观与位置（认领场景节点、写材质 uniform）。</summary>
    BaseVisuals _bases = null!;

    byte[] _mapPixels = null!;
    uint[] _lut = null!;
    Image _mapImage = null!;
    ImageTexture _mapTexture = null!;

    float _step;
    float _accumulator;
    float _elapsed;
    float _speedMultiplier = 1f;
    bool _paused;
    int _generation;

    /// <summary>
    /// 这一局是不是还在打。
    /// ⚠ **不要用"<see cref="_winner"/> == 0"兼任"还没打完"和"平局"两种含义**：
    ///   同一步里最后两家互相打掉基地是可达的（双方各剩一颗球贴身），那时 0 家存活 =
    ///   平局，而 0 又会被当成"还没打完"，于是模拟永远不停、比赛无法结束。
    /// </summary>
    enum MatchState
    {
        /// <summary>正在打。</summary>
        Running,

        /// <summary>有且只有一家活着，<see cref="_winner"/> 是它。</summary>
        Won,

        /// <summary>同一步里全灭 —— 平局。</summary>
        Drawn,
    }

    MatchState _state = MatchState.Running;

    /// <summary>
    /// 获胜的势力号；0 = 平局或还没打完（看 <see cref="_state"/>）。
    /// 唯一的消费者是结束时的控制台播报（见 <c>ReportMatchEnd</c>）—— 屏幕上没有 HUD 了。
    /// </summary>
    int _winner;

    /// <summary>
    /// **一局唯一的随机源**：摇大球出膛能量、炮口摆动相位、齐射散布抖动都从它取。
    /// 每局按 <see cref="Seed"/> 重新播种，所以同一 Seed 的一局是**完全可复现**的
    /// （曾经散布抖动用的是 <c>Random.Shared</c>，那让同一 Seed 每跑一次都不一样）。
    /// 钉板有自己独立的种子，不受这里的取数顺序影响。
    /// </summary>
    Random _matchRng = new(0);

    /// <summary>右侧面板的刷新节流（0.2 秒一次）。</summary>
    double _uiTimer;

    /// <summary>基地当前的判定半径 = <see cref="Team.BaseRadius"/> × <see cref="BaseSizeScale"/>。</summary>
    float BaseRadius => Team.BaseRadius * BaseSizeScale;

    /// <summary>炮口离基地中心的距离。基地和炮管一起缩放，不然基地放大后炮口会埋在球里。</summary>
    float MuzzleDistance => (Team.BaseRadius + Team.BarrelLength) * BaseSizeScale;

    /// <summary>弹珠层的外观参数（打包给 <see cref="BallMeshWriter"/>）。</summary>
    BallStyle BallStyle => new(TrailGhosts, TrailSpacing, SmallBallAlpha);

    /// <summary>
    /// 全部装配成功才会置 true。缺关键节点时 <see cref="_Ready"/> 会提前返回并把它留在 false ——
    /// 这样每帧与按键都能直接挡掉，不会拿着 null 一路崩（那种崩看不出根因）。
    /// </summary>
    bool _started;

    public override void _Ready()
    {
        _step = 1f / Mathf.Max(1f, SimHz);
        _selfCheck = new SelfCheck();
        CheckConfiguration();   // 先夹取/校验配置（必须发生在用它们构造任何东西之前）

        // 这几个节点是"没有它连构造都做不下去"的，缺了就在这里停住并说清楚。
        bool wired = MapLayer != null && BallLayer?.Multimesh != null && ViewCamera != null && Bases != null;
        _selfCheck.Check(wired, "场景连接",
            "MapLayer / BallLayer / ViewCamera / Bases 必须都在 Main.tscn 上连好（node_paths 元数据不能少）");
        if (!wired)
        {
            _selfCheck.Report();
            return;
        }

        _lut = Palette.BuildLut();
        _ballWriter = new BallMeshWriter(BallLayer.Multimesh, MaxBalls, TeamCount, BallStyle, MapSize);
        _bases = new BaseVisuals(Bases, TeamCount, BaseHitPoints);

        // 辉光需要 HDR：只有把超过 1.0 的亮度保留到后处理阶段，Environment 的辉光阈值
        // 才能把"发光的球"和"不发光的领土"区分开（领土颜色来自调色板，都 ≤ 1.0）。
        GetViewport().UseHdr2D = true;

        CreateMapLayer();
        BindPanels();
        ConfigureBallShader();
        ConfigureGlow();
        ConfigureSampling();

        CheckSceneAndComponents();   // 节点都认领完了，收组件自报的问题
        if (!_selfCheck.Report())    // 一次性打印（免得每个类各自 PrintErr、没人汇总）
        {
            GD.PrintErr("[自检] 本次仍会继续跑 —— 但上面每一条都代表"
                + "「一条规则没生效 / 一处静默失效」，玩法不保证和文档一致。");
        }

        StartMatch();
        SetupCamera();
        _started = true;

        // 窗口尺寸变了就重新框一次（不是让玩家调镜头，而是保证三栏永远完整可见）
        GetViewport().SizeChanged += SetupCamera;
    }

    /// <summary>
    /// 每帧的顺序（改顺序前先想清楚，这里是有依赖的）：
    ///   定步推进模拟 → 上传领土贴图 → 写基地材质 → 写弹珠层 → 数字与面板 → 钉板重画并插值。
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_started)
        {
            return;
        }

        double frameDelta = Math.Min(delta, 0.25);   // 卡顿一下不要累积成几十步

        // ⚠ 累加器是"**时间债**"，只在真正推进世界的时候记账。
        //   以前无论暂停与否都累加，于是暂停 30 秒就欠下 1800 步，解除暂停后每帧最多补 4 步，
        //   世界会以几倍速快进一段把债还完 —— 而且那段时间 boardAlpha 远大于 1（超出插值契约）。
        if (_paused || _state != MatchState.Running)
        {
            _accumulator = 0f;
        }
        else
        {
            _accumulator += (float)frameDelta * _speedMultiplier;

            int steps = 0;
            while (_accumulator >= _step && steps < MaxStepsPerFrame)
            {
                _accumulator -= _step;
                steps++;
                Simulate(_step);
            }
        }

        UploadTerritoryPixels();
        _bases.Update(_elapsed, _teams, DropShieldSeconds, BigShotInterval);
        _ballWriter.Write(_swarm);

        // 能量数字：**文本**变化很慢（要选 Top-N、拼字符串、排版），按时间节流；
        // 但**位置**必须每帧跟，否则数字会落在球的后面（屏幕刷新率越低越明显）。
        _energyLabelTimer += frameDelta;
        if (_energyLabelTimer >= EnergyLabelInterval)
        {
            _energyLabelTimer = 0;
            UpdateEnergyLabels();
        }

        Labels?.FollowPositions(_swarm, MapSize);

        UpdateUi(frameDelta);

        // 钉板必须**每帧**重画，而且在两个模拟步之间插值：
        // 屏幕 300+ FPS、模拟固定 60Hz，只按模拟节奏画就是"一步一跳"（用户反馈的"一卡一卡"）。
        // ⚠ 别把它放进 UpdateUi —— 那个函数开头有个 0.2 秒的节流（5Hz），板会整段跳。
        float boardAlpha = _step > 0f ? _accumulator / _step : 1f;
        Drop?.Render(boardAlpha);
    }

    /// <summary>
    /// 按键。摄像机是**固定**的：没有 WASD 平移、没有滚轮缩放（见 <see cref="SetupCamera"/>），
    /// 版面靠"世界空间的三栏"来排，不靠玩家调镜头。
    /// ⚠ 屏幕上没有按键提示（HUD 已按需求删除），所以这几个键只有按了才知道：
    ///   Space 暂停 / R 重开一局 / 1·2·3 速度 1×·2×·4× / Esc 退出。
    /// </summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_started)
        {
            return;
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Space: _paused = !_paused; break;
                case Key.R: StartMatch(); break;
                case Key.Key1: _speedMultiplier = 1f; break;
                case Key.Key2: _speedMultiplier = 2f; break;
                case Key.Key3: _speedMultiplier = 4f; break;
                case Key.Escape: GetTree().Quit(); break;
            }
        }
    }
}

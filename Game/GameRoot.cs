using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using TerritoryWar.Game;

namespace TerritoryWar;

/// <summary>
/// 弹珠领土战争（CPU 版）—— 主控。地图、经济、炮塔、基地攻防、渲染、输入、HUD
/// 都在这一个类里；纯数据和算法在 <see cref="TerritoryWar.Game"/> 命名空间下的几个小类里。
///
/// 一局的循环：
///   四角各一家、各一座炮塔，开局地图就按"离哪个基地最近"切成四个象限，**没有中立地**
///   按领土面积收入能量 → 炮塔摆动着齐射，把能量打成一颗颗弹珠
///   弹珠滚出去抢地盘；**踩敌境要花命** —— 小球花寿命、大球花能量，花完就没了
///   两颗敌对的球撞上，能量小的死、大的掉等量能量（只有大球之间才互撞）
///   打掉对方炮塔基地就灭掉一家，最后存活者获胜
///
/// 场景节点全部在 Scenes/Main.tscn 里搭好，脚本不创建任何节点。
///
/// 几条改代码前值得先知道的约束（详细原因见各字段旁的注释）：
///   1. 大球没有"一次性爆炸" —— 它压进敌境是逐格磨过去的，每格扣 cellCost。
///   2. 染色不能直接并行 —— 它同时改 owner 和 strength 两处共享单元格。
///   3. 弹珠层缓冲的长度必须严格等于 实例数 × 步长，且颜色槽偏移固定是 8（不是"步长 - 4"）。
///   4. 炮塔射速是弹珠数量唯一的闸门，而且每轮消耗必须小于同期收入，否则会一直哑火。
///
/// 实测性能、踩坑清单、玩法数值、节奏旋钮：见 docs/tuning-log.md
/// 换 GPU compute 方案的路线：见 docs/compute-architecture.md
/// </summary>
public partial class GameRoot : Node2D
{
    // ---- 地图与势力 ----
    [Export] public int MapSize = 1000;
    [Export] public int TeamCount = 4;               // 一家守一个角落，所以是 4
    [Export] public int Seed = 20260815;

    // ---- 经济：收入 → 炮塔开火 ----
    [Export] public float LaunchEnergy = 2000f;      // 发射一颗弹珠消耗的能量（也就是它的初始能量）
    [Export] public float IncomePerCell = 0.8f;      // 每秒每格领土产出多少能量
    [Export] public float BaseHitPoints = 8000f;
    [Export] public float BaseDamagePerSecond = 30f;     // 每颗小球贴着敌方基地时每秒转成多少伤害
                                                         // ⚠ 这是**按球累加**的，别调高：上千颗小球压上去
                                                         // 会线性叠乘（曾经 1500 时整局 16 秒就打完）
    [Export] public float BigBallBaseDamageScale = 12f;  // 大球的秒伤倍率（炮塔花本钱打出来的攻城武器）

    // ---- 炮塔：摆在四个角，摆动 + 齐射 ----
    // 射速是现在唯一的人口闸门（原来是"收入攒够就发"）。实测 4 队 × 10 颗 / 0.4 秒
    // 只能维持约 550 颗球，画面太稀；调到每轮 24 颗后约 1400 颗。
    // 能量仍然是硬约束：每队每轮消耗 LaunchEnergy × TurretBallsPerShot，
    // 必须小于"本方领土面积 × IncomePerCell × TurretFireInterval"，否则炮塔会一直哑火。
    [Export] public float TurretFireInterval = 0.4f;   // 每隔多久打一轮
    [Export] public int TurretBallsPerShot = 24;       // 每轮打几颗（一轮齐射，不是单发）
    [Export] public float TurretSpread = 0.30f;        // 一轮内的角度散布（弧度，约 ±17°）
    [Export] public float TurretSwingSpeed = 0.5f;     // 摆动快慢（弧度/秒的相位速度）
    [Export] public float TurretSwingRange = 0.75f;    // 摆动幅度（弧度，约 ±43°）

    // ---- 蓄力射击：大球的唯一来源 ----
    // 小球是固定能量的子弹，自己攒不到大球门槛；大球由炮塔每隔一段时间单独打出来，
    // 出来就是半径 ≥ BigMinRadius（现 50）。所以"变大"是基地花本钱买的，不是野生的。
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
    [Export] public float BigBallRadius = 50f;       // 半径达到这个值就按"大球"画：黑核 + 阵营色细环
                                                     // 它必须和 BallSwarm.BigMinRadius 一致 ——
                                                     // 半径映射是两段式的（小球恒定 1.0、大球 30~100），
                                                     // 所以这个阈值实际上就是"小球 / 大球"的分界线本身。
    [Export] public float SmallBallAlpha = 0.42f;    // 小球的透明度。要能看见、又不能糊住领土
    [Export] public float EnergyLabelMinEnergy = 4500f;  // 能量超过这个值才在球上显示数字
                                                     // ⚠ 必须**高于** BallSwarm.BigBallEnergy(4000)：
                                                     // 半径映射是两段式的，小球恒定 1.0（屏幕上约 1.4 px），
                                                     // 阈值压得比大球门槛低的话，就会出现"一串数字挂在小点上"。
                                                     // 需求本来就是"**大球上**显示能量"，所以跟着大球门槛走。
    [Export] public int EnergyLabelMaxCount = 24;    // 最多同时显示多少个数字（太多就是噪声）
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

    // ---- 场景节点（在 Main.tscn 里连好；node_paths 元数据不能少，否则解析不出节点）----
    [Export] public Sprite2D MapLayer;
    [Export] public MultiMeshInstance2D MarkerLayer;
    [Export] public MultiMeshInstance2D BallLayer;
    [Export] public Camera2D ViewCamera;
    [Export] public Label Hud;
    [Export] public WorldEnvironment GlowLayer;

    const int MaxCellCost = 16;
    const int MaxStepsPerFrame = 4;

    // 能量数字的排版。字号是**屏幕像素**（标签整体按 1/zoom 缩放，所以不随镜头变）。
    // ⚠ 盒子高度必须 ≥ 字号的行高，否则数字会被裁掉上下沿。
    const int EnergyLabelFontSize = 16;
    const int EnergyLabelOutlineSize = 5;
    const float EnergyLabelBoxHeight = 21f;
    const float MinZoom = 0.12f;
    const float MaxZoom = 16f;

    /// <summary>
    /// 四个角相对地图中心的方向，顺时针：左上 → 右上 → 右下 → 左下。
    /// 屏幕坐标 y 向下，所以 -y 是"上"。
    /// </summary>
    static readonly (float X, float Y)[] CornerSigns = { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) };

    TerritoryMap _map = null!;
    BallSwarm _swarm = null!;
    Team[] _teams = null!;
    MultiMesh _ballMesh = null!;
    MultiMesh _markerMesh = null!;

    byte[] _rgba = null!;
    uint[] _lut = null!;
    Image _image = null!;
    ImageTexture _texture = null!;

    float _step;
    float _accumulator;
    float _elapsed;
    float _fitZoom = 1f;
    float _speedMultiplier = 1f;
    bool _paused;
    int _generation;
    int _winner;          // 0 = 还没分出胜负

    /// <summary>摇大球出膛能量用的随机源。每局用 Seed 重新播种，保证同一 Seed 可复现。</summary>
    Random _shotRng = new(0);

    // 性能统计。染色/碰撞按"每步"平均 —— 大部分帧一步都没跑（渲染 300 FPS、模拟 60Hz），
    // 按帧报出来的数字会忽大忽小、完全没法看。其余几项是每帧一次，按帧计。
    // UpdateHud 每 0.2 秒汇报一次，然后清零窗口。
    double _framePaint, _frameCollide, _frameRest;
    double _windowPaint, _windowCollide, _windowRest;
    int _windowSteps;
    double _convertMs, _uploadMs, _markerMs, _ballsMs, _hudTimer;

    // 弹珠层的整块缓冲。实例数固定为 MaxBalls、靠 VisibleInstanceCount 控制画几个，
    // 这样缓冲长度恒定，不必每帧重新分配（Buffer 的长度必须严格等于 实例数 × 步长）。
    // MultiMesh 的缓冲布局是：变换 → 颜色 → 自定义数据。
    // 所以颜色槽偏移是固定的（2D 变换占 8 个 float），**不能用"步长 - 4"去推** ——
    // 一旦自定义数据被启用，步长变成 16，"步长 - 4" 就落到自定义数据槽上，
    // 着色器读到的颜色全零，整层球因为 alpha = 0 直接消失（实测踩过，而且不报任何错）。
    const int Transform2DFloats = 8;

    // 实例的"形态"，写进自定义数据的 x 通道，Disc.gdshader 按它分支。
    // 必须和着色器里的数字一致。
    const int BallKindSmall = 0;
    const int BallKindBig = 1;
    const int BallKindTurret = 2;

    /// <summary>基地当前的判定半径 = 默认半径 × <see cref="BaseSizeScale"/>。</summary>
    float BaseRadius => Team.BaseRadius * BaseSizeScale;

    /// <summary>炮口离基地中心的距离。基地和炮管一起缩放，不然基地放大后炮口会埋在球里。</summary>
    float MuzzleDistance => (Team.BaseRadius + Team.BarrelLength) * BaseSizeScale;

    float[] _ballBuffer = null!;
    int _ballStride;
    int _ballColorOffset;
    int _ballCustomOffset;
    int _ballInstancesPerBall;   // 1 = 只有球本身，>1 表示每颗球后面还跟几个拖尾残影
    float[] _teamRgba = null!;

    // 大球上显示的"能量"数字。
    //
    // 沿用商业版的做法（那边 Turret.HPLabel 就是 `Label` + `TopLevel = true`，
    // 每帧把节点位置对到物体上）：能量是实打实的数值，用文本最直接，
    // 不用为字宽去折腾纹理图集。
    //
    // 只在能量最高的若干颗球上显示（EnergyLabelMaxCount）—— 全显示就是满屏数字。
    // 这些 Label 是本节点（Main）的子节点，加在最后所以画在球层之上。
    // 位置对齐时按 1/相机缩放 反向缩放，字号在任何缩放下都保持屏幕像素恒定。
    Label[] _energyLabels = null!;
    int _energyLabelTick;

    /// <summary>能量数字的刷新间隔（帧）。数字变得慢，没必要每帧重排。</summary>
    const int EnergyLabelInterval = 4;

    public override void _Ready()
    {
        _step = 1f / Mathf.Max(1f, SimHz);
        _lut = Palette.BuildLut();
        _ballMesh = BallLayer.Multimesh;
        _markerMesh = MarkerLayer.Multimesh;

        // 辉光需要 HDR：只有把超过 1.0 的亮度保留到后处理阶段，Environment 的辉光阈值
        // 才能把"发光的球"和"不发光的领土"区分开（领土颜色来自调色板，都 ≤ 1.0）。
        GetViewport().UseHdr2D = true;

        BuildMap();
        StartMatch();
        AllocateBallBuffer();
        AllocateEnergyLabels();
        ConfigureMaterials();
        VerifyBallBufferLayout();

        // 每队一个实例：基地（实心核心 + 薄护盾环）
        _markerMesh.InstanceCount = TeamCount;

        Vector2 viewport = GetViewportRect().Size;
        _fitZoom = Mathf.Min(viewport.X / MapSize, viewport.Y / MapSize);
        ResetView();
    }

    /// <summary>
    /// 材质参数也在代码里强制一遍。`style = -1` 是"形态由实例的自定义数据决定"，
    /// 这是功能性的开关，不能依赖场景文件（编辑器会把它覆盖回去）。
    /// 两个材质只有亮度不同：增殖门是大面积的半透明块，亮了会糊住整片领土。
    /// </summary>
    void ConfigureMaterials()
    {
        if (BallLayer.Material is ShaderMaterial ball)
        {
            ball.SetShaderParameter("style", -1f);
            ball.SetShaderParameter("brightness", 1.35f);
        }

        if (MarkerLayer.Material is ShaderMaterial marker)
        {
            marker.SetShaderParameter("style", -1f);
            marker.SetShaderParameter("brightness", 0.9f);
        }

        ConfigureGlow();
        ConfigureSampling();
    }

    /// <summary>
    /// 采样过滤在代码里强制成 **nearest**（最近邻）。
    ///
    /// 项目默认（`rendering/textures/canvas_textures/default_texture_filter=0`）和场景里
    /// MapLayer 的 `texture_filter` 本来就已经是 nearest，但球层和标记层只是"继承默认值"——
    /// 而编辑器会覆盖场景文件，所以这三层都在这里显式钉死，免得哪天被改回线性插值
    /// （线性会让领土边界发糊、球边缘发虚）。
    /// </summary>
    void ConfigureSampling()
    {
        const CanvasItem.TextureFilterEnum Nearest = CanvasItem.TextureFilterEnum.Nearest;

        MapLayer.TextureFilter = Nearest;
        BallLayer.TextureFilter = Nearest;
        MarkerLayer.TextureFilter = Nearest;
    }

    /// <summary>
    /// 启动自检：在**用手写缓冲之前**，先用引擎自带的 setter 写一行、回读、逐 float 比对，
    /// 确认代码里假设的「行/列序 + 步长 + 各通道偏移」和引擎真实布局一致。
    ///
    /// 这条做法来自 ComputeShaderBattleSimulation 项目的 `CpuMultiMeshInit.cs`，
    /// 那里把它称为"列序/stride 契约探针"，并写明：**行/列混淆是静默 bug** ——
    /// 引擎不报任何错，只是画出来不对。本项目正好栽过两次：
    ///   ① `use_custom_data` 被打开后步长从 12 变 16，而颜色槽用"步长 − 4"去推，
    ///      结果写到自定义数据槽上，实例颜色全零、**整层球一颗都看不见**、不报错；
    ///   ② 颜色槽偏移算错，同样是静默的。
    /// 有了这个自检，这类错误启动时就报出来，不用靠截图去猜。
    ///
    /// 顺带澄清一个常见误解：**实例级 setter 不是"缓冲能上传"的前提** ——
    /// ComputeShaderBattleSimulation 用 GPU 回读做过对照实验，跳过 setter 直接写缓冲
    /// 依然成功。setter 在这里的作用是"提供一份权威布局供比对"。
    ///
    /// 探测写在最后一个实例槽位上：那个槽位永远不在 VisibleInstanceCount 范围内、不会被画，
    /// 而且每帧整块重写缓冲时会被覆盖掉，不影响真实数据。
    /// </summary>
    void VerifyBallBufferLayout()
    {
        const float OriginX = 11f, OriginY = 22f;
        const float ColorR = 0.11f, ColorG = 0.22f, ColorB = 0.33f, ColorA = 0.44f;
        const float CustomX = 0.55f;

        int probe = _ballMesh.InstanceCount - 1;
        if (probe < 0 || (probe + 1) * _ballStride > _ballMesh.Buffer.Length)
        {
            GD.PrintErr($"[缓冲自检] 槽位越界：probe={probe} stride={_ballStride} len={_ballMesh.Buffer.Length}");
            return;
        }

        _ballMesh.SetInstanceTransform2D(probe, new Transform2D(0f, new Vector2(OriginX, OriginY)));
        _ballMesh.SetInstanceColor(probe, new Color(ColorR, ColorG, ColorB, ColorA));
        _ballMesh.SetInstanceCustomData(probe, new Color(CustomX, 0f, 0f, 0f));

        float[] buf = _ballMesh.Buffer;
        int o = probe * _ballStride;

        bool ok = true;
        ok &= Check(buf, o + 3, OriginX, "Transform2D.origin.x @ 3");
        ok &= Check(buf, o + 7, OriginY, "Transform2D.origin.y @ 7");
        ok &= Check(buf, o + _ballColorOffset + 0, ColorR, $"颜色.r @ {_ballColorOffset}");
        ok &= Check(buf, o + _ballColorOffset + 1, ColorG, $"颜色.g @ {_ballColorOffset + 1}");
        ok &= Check(buf, o + _ballCustomOffset + 0, CustomX, $"自定义数据.x @ {_ballCustomOffset}");

        if (ok && _ballStride != 16)
        {
            GD.PrintErr($"[缓冲自检] 步长 {_ballStride}，但 Transform2D + 颜色 + 自定义数据应当是 16");
            ok = false;
        }

        GD.Print(ok
            ? $"[缓冲自检] 通过：步长 {_ballStride}，颜色槽 @{_ballColorOffset}，自定义数据槽 @{_ballCustomOffset}"
            : "[缓冲自检] **失败**：手写缓冲的布局和引擎不一致，渲染会是静默错误的，先修布局再用");
    }

    static bool Check(float[] buffer, int index, float expected, string what)
    {
        if (MathF.Abs(buffer[index] - expected) < 1e-4f)
        {
            return true;
        }

        GD.PrintErr($"[缓冲自检] {what}：读到 {buffer[index]}，期望 {expected}（偏移算错了）");
        return false;
    }

    /// <summary>
    /// 辉光后处理参数在代码里强制一遍（编辑器会覆盖场景里的 Environment）。
    ///
    /// 现在**没有边界辉光了**，辉光只为弹珠服务：领土填充是调色板 × 0.5（亮度 0.2~0.5），
    /// 弹珠点亮到 1.35，所以阈值卡在 1.15 就只让弹珠发光、领土完全不糊。
    /// 低 2 级模糊足够做出"小光点"的观感；开更多级会把光晕摊得很大很糊。
    /// </summary>
    void ConfigureGlow()
    {
        if (GlowLayer?.Environment is not Godot.Environment env)
        {
            return;
        }

        env.GlowEnabled = true;
        env.GlowIntensity = 0.55f;
        env.GlowBloom = 0.04f;
        env.GlowHdrThreshold = 1.15f;
        env.GlowHdrScale = 3.0f;
        env.GlowHdrLuminanceCap = 1.5f;

        // ⚠ SetGlowLevel 的 idx 是 **0 起**的（idx 0 对应界面上的 glow_levels/1）。
        // 写成 1..7 会把第 2~7 级全打开，那是最宽范围的模糊 —— 实测整屏被糊成一片惨白。
        for (int idx = 0; idx < 7; idx++)
        {
            env.SetGlowLevel(idx, idx < 2 ? 1.0f : 0.0f);
        }
    }

    /// <summary>
    /// 建能量数字的 Label 池，并把标记层的 MultiMesh 也在代码里强制配置一遍
    /// （理由同 <see cref="AllocateBallBuffer"/>：编辑器会覆盖场景文件）。
    /// 一次性建满、之后只改文字和位置，不反复创建销毁。
    /// </summary>
    void AllocateEnergyLabels()
    {
        _markerMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
        _markerMesh.UseColors = true;
        _markerMesh.UseCustomData = true;

        int count = Math.Max(0, EnergyLabelMaxCount);
        _energyLabels = new Label[count];

        for (int i = 0; i < count; i++)
        {
            var label = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visible = false,
                ZIndex = 10,
            };
            label.AddThemeFontSizeOverride("font_size", EnergyLabelFontSize);
            label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
            label.AddThemeConstantOverride("outline_size", EnergyLabelOutlineSize);   // 黑描边，压在亮领土上也读得出

            AddChild(label);
            _energyLabels[i] = label;
        }
    }

    /// <summary>
    /// 准备弹珠层的实例缓冲。
    ///
    /// ⚠ 这几项配置**在代码里强制**，不依赖 Scenes/Main.tscn 里的值。
    /// 原因是编辑器一旦开着，它会用自己的内存副本覆盖整个 .tscn —— 实测 `use_custom_data`
    /// 被编辑器改回 true 之后，缓冲步长从 12 变成 16，而颜色槽位置就变了，
    /// 结果整层球的实例颜色全读到 0，**alpha 为 0、一颗都看不见**，而且不报任何错。
    /// 功能性的配置写在代码里，美术参数才留在场景里。
    ///
    /// 布局是「变换 → 颜色 → 自定义数据」，所以两个偏移都是**累加算出来的固定值**，
    /// 不能用"步长 - 4"去推 —— 那样一旦开关变化就会算错。
    /// 自定义数据现在确实要用了（传 kind 和能量），所以下面显式打开。
    ///
    /// 实例数固定为 MaxBalls × (1 + 拖尾残影数)，靠 VisibleInstanceCount 只画前 N 个 ——
    /// 这样缓冲长度恒定，不必每帧重新分配（Buffer 的长度必须严格等于 实例数 × 步长）。
    /// </summary>
    void AllocateBallBuffer()
    {
        _ballMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
        _ballMesh.UseColors = true;
        _ballMesh.UseCustomData = true;

        _ballInstancesPerBall = 1 + Math.Max(0, TrailGhosts);
        _ballMesh.InstanceCount = MaxBalls * _ballInstancesPerBall;

        _ballColorOffset = Transform2DFloats;
        _ballCustomOffset = _ballColorOffset + (_ballMesh.UseColors ? 4 : 0);
        _ballStride = _ballCustomOffset + (_ballMesh.UseCustomData ? 4 : 0);
        _ballBuffer = new float[_ballMesh.InstanceCount * _ballStride];

        // 预先把每队颜色摊成 float[4]，避免热循环里反复构造 Color
        _teamRgba = new float[(TeamCount + 1) * 4];
        for (int team = 1; team <= TeamCount; team++)
        {
            Color c = Palette.TeamColor(team);
            _teamRgba[team * 4] = c.R;
            _teamRgba[team * 4 + 1] = c.G;
            _teamRgba[team * 4 + 2] = c.B;
            _teamRgba[team * 4 + 3] = c.A;
        }
    }

    /// <summary>
    /// 实例缓冲的写入契约：**每次整块写 Buffer 之前，先调一次实例级 setter。**
    ///
    /// 抄自 `ComputeShaderBattleSimulation/CPUBattle/Scripts/CpuMultiMeshInit.cs` 的
    /// `SetupIdentityBuffer`，并沿用它对"为什么"的澄清：
    /// setter **不是**"缓冲能上传"的前提 —— 那边用 GPU 缓冲回读做过对照实验，
    /// 跳过 setter 直接写 `MultimeshSetBuffer` 依然成功上传（回读实例 0/1 均为恒等）。
    /// 它承担两件事：
    ///   ① 触发引擎的 `_multimesh_make_local()` + `_multimesh_mark_dirty()`
    ///      （`mesh_storage.cpp:1908 → :1711/:1781`），让实例缓冲走上"dirty 区"那条兜底路径，
    ///      而不是只依赖 `_multimesh_set_buffer` 的裸 `buffer_update`（`:2097`）；
    ///   ② 作为列序/stride 的契约探针，配合 <see cref="VerifyBallBufferLayout"/> 使用。
    ///
    /// ⚠ 两边上下文不同，所以调用时机也不同：那边位置存在纹理里、实例缓冲**只写一次**，
    /// 于是 setter 只在初始化时调；这边每帧整块重写缓冲，就每帧调一次。
    ///
    /// 代价可以忽略：这里只有 2 次托管↔引擎调用（0 号哨兵 + 1 号退化），
    /// 和"逐实例 setter"是两回事 —— 后者对 MaxBalls 颗球才是每颗两次调用。
    /// </summary>
    void MarkBallBufferDirty()
    {
        // 0 号哨兵：先放到很远的地方。它随后会被整块写覆盖，所以不承担"隐藏实例"职责 ——
        // 它的意义是把"实例级 setter 与整块写的列序/stride 是否一致"写进代码：
        // 一旦两者列序不同，0 号会出现在远处（看得见），而不是以错误变换混在画面里（静默）。
        _ballMesh.SetInstanceTransform2D(0, new Transform2D(0f, SentinelOrigin));

        // 1 号退化：全 0 变换 = scale 0，永不参与绘制。
        if (_ballMesh.InstanceCount > 1)
        {
            _ballMesh.SetInstanceTransform2D(1, default);
        }
    }

    /// <summary>哨兵实例（0 号）的变换位置。列序万一错位时它会出现在远处而不是混在画面里。</summary>
    static readonly Vector2 SentinelOrigin = new(100000f, 100000f);

    // ==================================================================
    // 开局
    // ==================================================================
    void BuildMap()
    {
        _map = new TerritoryMap(MapSize, MapSize, TeamCount);
        _rgba = new byte[MapSize * MapSize * 4];

        _image = Image.CreateEmpty(MapSize, MapSize, false, Image.Format.Rgba8);
        _image.SetData(MapSize, MapSize, false, Image.Format.Rgba8, _rgba);
        _texture = ImageTexture.CreateFromImage(_image);
        MapLayer.Texture = _texture;

        // 地图的采样方式钉在着色器里（`filter_nearest`），不依赖节点属性 ——
        // 理由见 Shaders/Map.gdshader 顶部。这是纯直通，不改变任何颜色。
        var mapShader = GD.Load<Shader>("res://Shaders/Map.gdshader");
        if (mapShader != null)
        {
            var mapMaterial = new ShaderMaterial { Shader = mapShader };
            mapMaterial.SetShaderParameter("map_texture", _texture);
            MapLayer.Material = mapMaterial;
        }
    }

    void StartMatch()
    {
        _generation++;
        _elapsed = 0f;
        _accumulator = 0f;
        _winner = 0;
        _map = new TerritoryMap(MapSize, MapSize, TeamCount);
        _swarm = new BallSwarm(MaxBalls, Seed + _generation * 7919);
        _swarm.SmallBallLifetime = SmallBallLifetime;
        _swarm.SmallBallCellLifeCost = SmallBallCellLifeCost;
        _swarm.ClashEnergyCost = BigBallClashEnergyCost;
        _swarm.ClashCooldown = BigBallClashCooldown;
        _shotRng = new Random(Seed + _generation * 7919 + 101);
        _teams = new Team[TeamCount + 1];



        var rng = new Random(Seed + _generation * 7919);

        // 四角各一家，顺时针：左上 → 右上 → 右下 → 左下。
        // 屏幕坐标是 y 向下，所以"上"对应相对中心的负 y。
        float inset = MapSize * 0.09f;
        float center = MapSize * 0.5f;
        float barrel = MuzzleDistance;

        for (int team = 1; team <= TeamCount; team++)
        {
            var corner = CornerSigns[(team - 1) % CornerSigns.Length];
            float baseX = center + corner.X * (center - inset);
            float baseY = center + corner.Y * (center - inset);

            // 炮口朝向地图中心（归一化），摆动就围绕这个角度来回
            float baseAngle = MathF.Atan2(center - baseY, center - baseX);

            // 相位和开火计时都随机错开，否则四角会整齐划一地齐射
            float phase = (float)(rng.NextDouble() * Math.Tau);
            float fireTimer = (float)(rng.NextDouble() * TurretFireInterval);
            float bigShotTimer = (float)(rng.NextDouble() * BigShotInterval);

            var created = new Team((byte)team, baseX, baseY, BaseHitPoints, baseAngle, phase, fireTimer, bigShotTimer);
            created.EnergyPool = LaunchEnergy * TurretBallsPerShot;   // 开局能打一轮
            _teams[team] = created;
        }

        // 开局就把地图按"离哪个基地最近"分完，不留中立地。
        // 四角布局下这正好切成四个象限（分界线是两条对角线）。
        // 这样一开局就是四家接壤、直接开打，而不是先花二十秒圈无主地。
        for (int y = 0; y < MapSize; y++)
        {
            for (int x = 0; x < MapSize; x++)
            {
                byte nearest = 1;
                float bestDistance = float.MaxValue;

                for (int team = 1; team <= TeamCount; team++)
                {
                    float dx = x - _teams[team].BaseX;
                    float dy = y - _teams[team].BaseY;
                    float distance = dx * dx + dy * dy;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearest = (byte)team;
                    }
                }

                _map.Claim(x, y, nearest);
            }
        }
    }

    // ==================================================================
    // 主循环
    // ==================================================================

    public override void _Process(double delta)
    {
        HandlePan(delta);

        double frameDelta = Math.Min(delta, 0.25);   // 卡顿一下不要累积成几十步
        _accumulator += (float)frameDelta * _speedMultiplier;

        _framePaint = 0;
        _frameCollide = 0;
        _frameRest = 0;
        if (!_paused && _winner == 0)
        {
            int steps = 0;
            while (_accumulator >= _step && steps < MaxStepsPerFrame)
            {
                _accumulator -= _step;
                steps++;
                Simulate(_step);
            }
            _windowSteps += steps;
            _windowPaint += _framePaint;
            _windowCollide += _frameCollide;
            _windowRest += _frameRest;
        }

        var uploadWatch = Stopwatch.StartNew();
        Palette.ToRgba(_map.Owner, _lut, _rgba, _map.Width, _map.Height);
        _convertMs = uploadWatch.Elapsed.TotalMilliseconds;
        _image.SetData(_map.Width, _map.Height, false, Image.Format.Rgba8, _rgba);
        _texture.Update(_image);
        _uploadMs = uploadWatch.Elapsed.TotalMilliseconds - _convertMs;

        var markerWatch = Stopwatch.StartNew();
        UpdateMarkers();
        _markerMs = markerWatch.Elapsed.TotalMilliseconds;

        var ballWatch = Stopwatch.StartNew();
        UpdateBalls();
        _ballsMs = ballWatch.Elapsed.TotalMilliseconds;

        // 能量数字变化很慢，不必每帧重排（还要拼字符串、改 Control 排版）
        if (++_energyLabelTick >= EnergyLabelInterval)
        {
            _energyLabelTick = 0;
            UpdateEnergyLabels();
        }

        UpdateHud(frameDelta);
    }

    /// <summary>
    /// 把能量数字对到能量最高的若干颗球上。
    ///
    /// 选取用的是"大小为 N 的插入排序"而不是全排序：球有上千颗、N 只有几十，
    /// 维护一个小数组顺带记录当前门槛，一趟就能拿到前 N 名。
    /// 阈值（<see cref="EnergyLabelMinEnergy"/>）是下限，避免给一堆小球队也挂数字。
    /// </summary>
    void UpdateEnergyLabels()
    {
        int slotCount = _energyLabels.Length;
        if (slotCount == 0 || _swarm.Count == 0)
        {
            return;
        }

        Span<int> picked = stackalloc int[slotCount];
        Span<float> pickedEnergy = stackalloc float[slotCount];
        int found = 0;
        float floor = EnergyLabelMinEnergy;

        for (int i = 0; i < _swarm.Count; i++)
        {
            float energy = _swarm.Energy[i];
            if (energy < floor)
            {
                continue;
            }

            // 插到已选队列里合适的位置（队首最大、队尾最小）
            int at = found < slotCount ? found++ : slotCount - 1;
            while (at > 0 && pickedEnergy[at - 1] < energy)
            {
                pickedEnergy[at] = pickedEnergy[at - 1];
                picked[at] = picked[at - 1];
                at--;
            }

            pickedEnergy[at] = energy;
            picked[at] = i;

            if (found == slotCount)
            {
                floor = MathF.Max(EnergyLabelMinEnergy, pickedEnergy[slotCount - 1]);
            }
        }

        float half = MapSize * 0.5f;
        float invZoom = 1f / MathF.Max(0.001f, ViewCamera.Zoom.X);
        const float labelWidth = 150f;   // 屏幕像素宽：够放下 5 位数并居中

        for (int k = 0; k < slotCount; k++)
        {
            Label label = _energyLabels[k];
            if (k >= found)
            {
                label.Visible = false;
                continue;
            }

            int ball = picked[k];
            float radius = _swarm.RadiusOf(ball);
            float height = EnergyLabelBoxHeight * invZoom;

            label.Visible = true;
            label.Text = FormatEnergy(pickedEnergy[k]);
            label.Modulate = Palette.TeamColor(_swarm.Team[ball]);
            label.Size = new Vector2(labelWidth, EnergyLabelBoxHeight);
            label.Scale = new Vector2(invZoom, invZoom);   // 屏幕上的字号恒定，不随缩放变

            // 数字压在球的正中央（水平居中靠 labelWidth，垂直居中靠 height/2）。
            // 标签宽度是屏幕像素，换算回世界单位要乘 1/zoom；高度同理。
            label.Position = new Vector2(
                _swarm.X[ball] - half - labelWidth * 0.5f * invZoom,
                _swarm.Y[ball] - half - height * 0.5f);
        }
    }

    /// <summary>
    /// 能量数字的显示格式：千进位用 K、百万用 M，最多一位小数。
    ///   999 → "999"    6198 → "6.2K"    80000 → "80K"    1_500_000 → "1.5M"
    ///
    /// 用 `"0.#"` 而不是 `"0.0"`：整数时不会拖一个没意义的 ".0"（`80K` 比 `80.0K` 干净）。
    /// 显式指定不变文化 —— 小数点分隔符不该随系统区域设置变（项目也开了
    /// `InvariantGlobalization`，这里只是把意图写清楚）。
    /// </summary>
    static string FormatEnergy(float energy)
    {
        if (energy >= 1_000_000f)
        {
            return (energy / 1_000_000f).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (energy >= 1_000f)
        {
            return (energy / 1_000f).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        return ((int)energy).ToString(CultureInfo.InvariantCulture);
    }

    void Simulate(float dt)
    {
        _elapsed += dt;
        int cellCost = CurrentCellCost();

        var paintWatch = Stopwatch.StartNew();
        _swarm.Step(dt, _map, BallSpeed, cellCost, BallUpkeepPerSecond);
        _framePaint += paintWatch.Elapsed.TotalMilliseconds;

        var collideWatch = Stopwatch.StartNew();
        _swarm.ResolveCollisions();
        _swarm.Compact();
        _frameCollide += collideWatch.Elapsed.TotalMilliseconds;

        // 其余模拟逻辑合并计时（炮塔 / 基地攻防 / 经济 / 胜负判定）
        var restWatch = Stopwatch.StartNew();
        TickTurrets(dt);
        TickBigShots(dt);
        AttackBases(dt);
        TickEconomy(dt);
        CheckVictory();
        _frameRest += restWatch.Elapsed.TotalMilliseconds;
    }


    /// <summary>每格占领成本随时间从 1 涨到 16 —— 这是压制无限扩张的关键机制。</summary>
    int CurrentCellCost()
        => Math.Clamp(1 + (int)(_elapsed / MathF.Max(1f, CostRampSeconds)), 1, MaxCellCost);

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
        }
    }

    /// <summary>
    /// 炮塔：摆动的炮口 + 有节奏的齐射。
    ///
    /// 和之前"收入攒够就随机方向喷一颗"的区别：
    ///   · 方向由炮口决定，所以球会**沿着炮口指向的那片扇区**压出去，形成推进锋线；
    ///   · 一轮打多颗（<see cref="TurretBallsPerShot"/>）并带角度散布，
    ///     这样既能看清"齐射"，又不用把射速提到机枪级别才够球用；
    ///   · 各家相位与计时都错开，四角不会同时开火。
    /// 能量仍然是硬约束：能量不够就哑火，所以丢地盘会直接反映成火力下降。
    /// </summary>
    void TickTurrets(float dt)
    {
        float barrel = MuzzleDistance;

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

            float salvoCost = LaunchEnergy * TurretBallsPerShot;
            if (t.EnergyPool < salvoCost)
            {
                continue;   // 哑火，等收入补上（不重置计时，能量一到就打）
            }

            t.FireTimer = TurretFireInterval;
            t.EnergyPool -= salvoCost;

            for (int shot = 0; shot < TurretBallsPerShot && _swarm.Count < MaxBalls; shot++)
            {
                // 一轮内的散布：以炮口为中心均匀铺开，再叠一点随机，免得每次齐射都一模一样
                float offset = TurretBallsPerShot > 1
                    ? (shot / (float)(TurretBallsPerShot - 1) - 0.5f) * 2f * TurretSpread
                    : 0f;
                offset += (float)(Random.Shared.NextDouble() - 0.5) * TurretSpread * 0.5f;
                float angle = t.TurretAngle + offset;

                SpawnAt(t, angle, barrel, LaunchEnergy);
            }
        }
    }

    /// <summary>
    /// 蓄力射击：每隔 <see cref="BigShotInterval"/> 让每座炮塔单独打出一颗**大球**。
    ///
    /// 这是大球**唯一**的产生途径。小球是固定能量的子弹（能量冻结、涂色免费），
    /// 自己永远攒不到大球门槛，所以"变大"完全由炮塔决定 —— 这也让大球成了
    /// "基地花了本钱打出去的东西"，而不是随机涌现的。
    /// </summary>
    void TickBigShots(float dt)
    {
        float barrel = MuzzleDistance;

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

            // 出膛能量随机摇 —— 摇到多少就是这一颗的**全部预算**（能量只减不增），
            // 也就决定了它多大、能磨多远。炮塔按摇出来的数扣能量池。
            float rolled = BigBallEnergyMin
                + (float)_shotRng.NextDouble() * (BigBallEnergyMax - BigBallEnergyMin);
            rolled = MathF.Max(rolled, BallSwarm.BigBallEnergy);

            if (t.EnergyPool < rolled || _swarm.Count >= MaxBalls)
            {
                continue;   // 能量不够就打不出来（同样不重置计时，能量一到就打）
            }

            float muzzleX = Math.Clamp(t.BaseX + MathF.Cos(t.TurretAngle) * barrel, 1f, MapSize - 2f);
            float muzzleY = Math.Clamp(t.BaseY + MathF.Sin(t.TurretAngle) * barrel, 1f, MapSize - 2f);

            // ⚠ 炮口被已有的球占住就**先不打**（能量留着、计时也不重置，下一帧再看）。
            // 否则新球会"出生在别人身上"：一帧之内被推开几十格 —— 用户反馈的
            // "碰撞很突兀"就是这个。同队连射特别容易踩到：两次射击间隔 1.2 秒，
            // 前一颗已经飞出 `1.2 × 90 = 108` 格，而大球的判定距离是 `r_a + r_b` = **107~150 格**，
            // 于是"稍大一点的两颗"一出生就叠在一起。
            // 实测代价：大球实际发射量是额定值的 **86~90%**（被挡时不是等几十毫秒，
            // 而是等前一颗偏航/撞墙为止 —— 同向等速的两颗球间距不会自己变大）。
            // 觉得亏就调大 `BigShotInterval` 或把大球调小，让"间距"回到判定距离之外。
            if (_swarm.BigBallBlockedAt(muzzleX, muzzleY, rolled))
            {
                continue;
            }

            t.BigShotTimer = BigShotInterval;
            t.EnergyPool -= rolled;

            // 大球不用散布：它就是一颗，沿着当前炮口方向直直打出去
            _swarm.Spawn(muzzleX, muzzleY, rolled, t.Id, t.TurretAngle);
        }
    }

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
        for (int i = 0; i < _swarm.Count; i++)
        {
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

                float damage = MathF.Min(_swarm.Energy[i], rate * dt);
                _swarm.Energy[i] -= damage;
                t.TakeDamage(damage);

                if (!t.Alive)
                {
                    Eliminate(team);
                }
                break;
            }
        }
    }

    void Eliminate(int team)
    {
        // 这家出局了，地盘按"离哪个还活着的基地最近"分给幸存者 ——
        // 这样地图上**永远不会留中立地**（"中立"只在开局之前存在，而开局也是四家分完的）。
        // 分给最近的两家而不是击杀者，是为了不让一次爆冷直接滚成雪球。
        byte dying = (byte)team;

        for (int y = 0; y < MapSize; y++)
        {
            for (int x = 0; x < MapSize; x++)
            {
                if (_map.OwnerAt(x, y) != dying)
                {
                    continue;
                }

                byte nearest = 0;
                float bestDistance = float.MaxValue;

                for (int other = 1; other <= TeamCount; other++)
                {
                    if (other == team || !_teams[other].Alive)
                    {
                        continue;
                    }

                    float dx = x - _teams[other].BaseX;
                    float dy = y - _teams[other].BaseY;
                    float distance = dx * dx + dy * dy;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearest = (byte)other;
                    }
                }

                if (nearest != 0)
                {
                    _map.Claim(x, y, nearest);
                }
            }
        }

        _swarm.KillTeam(dying);
    }

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

        if (alive <= 1)
        {
            _winner = last;
        }
    }

    // ==================================================================
    // 渲染
    // ==================================================================

    /// <summary>
    /// 一次性把整块实例缓冲写进 MultiMesh，并且按线程分段并行填。
    ///
    /// 为什么能并行：填 buffer 是纯 C# 数组写，每颗球只写自己那几个 float，
    /// 线程之间没有任何共享写入；写引擎（Buffer 赋值）只在最后做一次、留在主线程。
    /// 对比逐实例 SetInstanceTransform2D/SetInstanceColor：那是每颗球两次托管↔引擎调用，
    /// 实测 1 万颗要 1.9 ms，而整块写只要 0.14 ms。
    ///
    /// 拖尾就写在同一块缓冲里：每颗球占 (1 + TrailGhosts) 个连续槽位，
    /// 前面几个是从远到近的残影、最后一个是球本身（同一个 MultiMesh 里后面的实例画在上面，
    /// 所以球自然压在残影之上）。
    /// </summary>
    void UpdateBalls()
    {
        int count = _swarm.Count;
        int perBall = _ballInstancesPerBall;
        _ballMesh.VisibleInstanceCount = count * perBall;   // 实例数固定，只画前 N 个

        if (count == 0)
        {
            return;
        }

        float[] buffer = _ballBuffer;
        float[] rgba = _teamRgba;
        float[] xs = _swarm.X;
        float[] ys = _swarm.Y;
        byte[] teams = _swarm.Team;
        int stride = _ballStride;
        int colorOffset = _ballColorOffset;
        int customOffset = _ballCustomOffset;
        int ghosts = perBall - 1;
        float half = MapSize * 0.5f;
        float bigRadius = BigBallRadius;
        float spacing = TrailSpacing;

        int stripes = Math.Min(Math.Max(1, System.Environment.ProcessorCount), count);
        int perStripe = (count + stripes - 1) / stripes;

        Parallel.For(0, stripes, stripe =>
        {
            int end = Math.Min(count, (stripe + 1) * perStripe);
            for (int i = stripe * perStripe; i < end; i++)
            {
                float radius = _swarm.RadiusOf(i);
                float size = radius * 2f;
                float x = xs[i] - half;   // 网格坐标 → 以地图中心为原点的世界坐标
                float y = ys[i] - half;
                int kind = radius >= bigRadius ? BallKindBig : BallKindSmall;
                int c = teams[i] * 4;
                int slot = i * perBall;

                // 传给着色器的能量（归一化到 0~1）：大球用它决定内核漩涡转多急、多亮。
                // 归一化放在 CPU 端做，省掉每个片元一次除法。
                float energy = Math.Clamp(_swarm.Energy[i] / BallSwarm.MaxEnergy, 0f, 1f);

                if (ghosts > 0)
                {
                    // 残影沿航向的反方向摆。因为球的运动已经接近直线（TurnRate 很小），
                    // 身后这条直线就是它刚才走过的大致路径，不需要存历史位置。
                    float dirX = MathF.Cos(_swarm.HeadingOf(i));
                    float dirY = MathF.Sin(_swarm.HeadingOf(i));

                    // 从最远的一个开始写，保证越近的残影画在越上面
                    for (int g = ghosts; g >= 1; g--, slot++)
                    {
                        float shrink = 1f - g * 0.16f;                // 越远越小
                        float alpha = 1f - g / (float)(ghosts + 1);   // 越远越淡
                        WriteInstance(buffer, slot * stride, colorOffset, customOffset,
                            size * shrink, x - dirX * spacing * g, y - dirY * spacing * g,
                            rgba, c, kind, alpha * SmallBallAlpha, energy);
                    }
                }

                WriteInstance(buffer, slot * stride, colorOffset, customOffset,
                    size, x, y, rgba, c, kind, kind == BallKindBig ? 1f : SmallBallAlpha, energy);
            }
        });

        // 每次用这块缓冲之前，先走一遍实例级 setter（理由见 MarkBallBufferDirty）。
        MarkBallBufferDirty();
        _ballMesh.Buffer = buffer;
    }

    /// <summary>
    /// 写一个实例。这里就是 Disc.gdshader 里那套编码：
    /// 颜色 rgb = 阵营色（所有形态共用，所以不能拿"rgb 是否为零"当标记），
    /// 颜色 a = 透明度/淡出，自定义 x = 形态（kind），自定义 y = 该形态自己的参数。
    /// </summary>
    static void WriteInstance(float[] buffer, int o, int colorOffset, int customOffset,
        float size, float x, float y, float[] rgba, int c, int kind, float alpha, float param)
    {
        // Transform2D 占前 8 个 float：两行 (xx, xy, 0, ox) / (yx, yy, 0, oy)
        buffer[o] = size; buffer[o + 1] = 0f; buffer[o + 2] = 0f; buffer[o + 3] = x;
        buffer[o + 4] = 0f; buffer[o + 5] = size; buffer[o + 6] = 0f; buffer[o + 7] = y;

        // 颜色紧跟变换（偏移累加得出，不是"步长 - 4"）
        buffer[o + colorOffset] = rgba[c];
        buffer[o + colorOffset + 1] = rgba[c + 1];
        buffer[o + colorOffset + 2] = rgba[c + 2];
        buffer[o + colorOffset + 3] = alpha;

        // 自定义数据：x = 形态，y = 形态参数（能量 / 蓄力 / 冲击波进度），z = 闪光
        buffer[o + customOffset] = kind;
        buffer[o + customOffset + 1] = param;
        buffer[o + customOffset + 2] = 0f;
        buffer[o + customOffset + 3] = 0f;
    }

    /// <summary>
    /// 炮塔基地 = 实心核心 + 薄护盾环。纯静态外观，没有蓄力/闪光的动画。
    /// </summary>
    void UpdateMarkers()
    {
        int index = 0;

        for (int team = 1; team <= TeamCount; team++, index++)
        {
            var t = _teams[team];
            Color color = t.Alive ? Palette.TeamColor(team) : new Color(0.25f, 0.25f, 0.25f);

            // 四边形边长取 BaseRadius × 4.2 —— 这个倍数**同时是着色器里 dist 的换算基准**：
            // dist 0.5 对应半径 `BaseRadius × 2.1 ≈ BaseRadius`，所以实心核心画到 0.5
            // 正好和基地的判定半径对齐，护盾环紧贴在它外面一点（0.505~0.575）。
            // 改这个倍数必须同步改 Disc.gdshader 里 kind == 2 的那三个区间。
            _markerMesh.SetInstanceTransform2D(index,
                MarkerTransform(t.BaseX, t.BaseY, 0f, BaseRadius * 4.2f, BaseRadius * 4.2f));
            _markerMesh.SetInstanceColor(index, color);
            _markerMesh.SetInstanceCustomData(index, new Color(BallKindTurret, 0f, 0f, 0f));
        }
    }

    /// <summary>网格坐标 → 以地图中心为原点、可旋转的四边形实例变换。</summary>
    Transform2D MarkerTransform(float gridX, float gridY, float angle, float sizeX, float sizeY)
        => new(angle, new Vector2(sizeX, sizeY), 0f, new Vector2(gridX - MapSize * 0.5f, gridY - MapSize * 0.5f));

    // ==================================================================
    // 输入与相机
    // ==================================================================

    void HandlePan(double delta)
    {
        var direction = Vector2.Zero;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) direction.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) direction.X += 1;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) direction.Y -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) direction.Y += 1;

        if (direction == Vector2.Zero)
        {
            return;
        }

        // 除以 zoom，让"按住一秒移动多少屏幕像素"手感恒定
        float speed = 800f / ViewCamera.Zoom.X * (float)delta;
        ViewCamera.Position += direction.Normalized() * speed;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.Pressed)
        {
            if (button.ButtonIndex == MouseButton.WheelUp)
            {
                ZoomBy(1.15f);
            }
            else if (button.ButtonIndex == MouseButton.WheelDown)
            {
                ZoomBy(1f / 1.15f);
            }
        }
        else if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Space: _paused = !_paused; break;
                case Key.F: ResetView(); break;
                case Key.R: StartMatch(); break;
                case Key.Key1: _speedMultiplier = 1f; break;
                case Key.Key2: _speedMultiplier = 2f; break;
                case Key.Key3: _speedMultiplier = 4f; break;
                case Key.Escape: GetTree().Quit(); break;
            }
        }
    }

    void ZoomBy(float factor)
    {
        float zoom = Mathf.Clamp(ViewCamera.Zoom.X * factor, MinZoom, MaxZoom);
        ViewCamera.Zoom = new Vector2(zoom, zoom);
    }

    void ResetView()
    {
        ViewCamera.Position = Vector2.Zero;
        ViewCamera.Zoom = new Vector2(_fitZoom, _fitZoom);
    }

    // ==================================================================
    // HUD
    // ==================================================================

    void UpdateHud(double frameDelta)
    {
        _hudTimer += frameDelta;
        if (_hudTimer < 0.2)
        {
            return;
        }
        _hudTimer = 0;

        int total = _map.Width * _map.Height;
        int[] cells = _map.CellsByTeam;
        int alive = 0;

        for (int team = 1; team <= TeamCount; team++)
        {
            if (_teams[team].Alive)
            {
                alive++;
            }
        }

        // 领土地位前 3 名
        Span<int> top = stackalloc int[3];
        Span<int> topCells = stackalloc int[3];
        for (int team = 1; team <= TeamCount; team++)
        {
            int value = cells[team];
            for (int slot = 0; slot < 3; slot++)
            {
                if (value > topCells[slot])
                {
                    for (int shift = 2; shift > slot; shift--)
                    {
                        top[shift] = top[shift - 1];
                        topCells[shift] = topCells[shift - 1];
                    }
                    top[slot] = team;
                    topCells[slot] = value;
                    break;
                }
            }
        }

        string leaders = "";
        for (int slot = 0; slot < 3; slot++)
        {
            if (topCells[slot] > 0)
            {
                leaders += $"   势力{top[slot]} {topCells[slot] * 100.0 / total:F1}%";
            }
        }

        string status = _winner > 0 ? $"   ★ 势力{_winner} 获胜！" : _paused ? "   [已暂停]" : "";

        double steps = Math.Max(1, _windowSteps);

        Hud.Text =
            $"FPS {Engine.GetFramesPerSecond(),3}   存活 {alive}/{TeamCount}   弹珠 {_swarm.Count}   " +
            $"{_speedMultiplier:F0}x{status}\n" +
            $"染色 {_windowPaint / steps,5:F2}   碰撞 {_windowCollide / steps,5:F2}   " +
            $"其他 {_windowRest / steps,5:F2}ms/步   小球层 {_ballsMs,5:F2}ms/帧\n" +
            $"转换 {_convertMs,5:F2}   上传 {_uploadMs,5:F2}   标记 {_markerMs,5:F2}ms/帧\n" +
            $"中立 {cells[TerritoryMap.Neutral] * 100.0 / total:F1}%   每格成本 {CurrentCellCost()}{leaders}\n" +
            "滚轮缩放   WASD 平移   F 复位   R 重开   1/2/3 倍速   Space 暂停";

        _windowPaint = 0;
        _windowCollide = 0;
        _windowRest = 0;
        _windowSteps = 0;
    }
}

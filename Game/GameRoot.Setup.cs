using System;
using Godot;
using TerritoryWar.Game;

namespace TerritoryWar;

/// <summary>
/// 一局开始**之前**的一次性装配：认领场景节点、造地图贴图、把功能性的着色器参数钉死、框摄像机。
/// 这些都是"编辑器会覆盖场景文件"或"启动时只该做一次"的东西，所以写在代码里。
///
/// 另外这里是**启动自检**的所在：所有跨文件（旋钮 ↔ 常量、代码 ↔ 场景、代码 ↔ 着色器）
/// 的约定都在这里集中验证一次，见 <see cref="SelfCheck"/> 的注释。
/// </summary>
public partial class GameRoot
{
    /// <summary>启动自检的收集器（<c>_Ready</c> 里建，装完配后统一打印）。</summary>
    SelfCheck _selfCheck = null!;

    /// <summary>
    /// 第一阶段：**校验并夹取配置** —— 必须在"用这些值构造任何东西之前"跑。
    ///
    /// 这里的每一项都是"只写在注释里、以前没有任何检查"的约定。挑两个最典型的：
    ///   · `TeamCount` 同时受三方约束：调色板颜色数（超了会在领土像素转换里越界崩）、
    ///     场景里的基地数、右侧面板的行数。以前设成 9 就是一个延迟到几秒后才崩的崩。
    ///   · `EnergyLabelMinEnergy` 必须高于大球门槛，否则一串数字会挂在小球的小点上
    ///     （不崩、也不会报错，只是难看 —— 属于"静默地不按需求跑"）。
    /// </summary>
    void CheckConfiguration()
    {
        // ---- 势力数：夹进调色板容量，并核对场景能提供几个基地 / 面板几行 ----
        int paletteMax = Palette.MaxTeams;
        if (TeamCount > paletteMax)
        {
            _selfCheck.Check(false, "TeamCount",
                $"{TeamCount} 超过调色板容量 {paletteMax}（领土像素查表 lut[owner] 会越界崩），已夹到 {paletteMax}");
            TeamCount = paletteMax;
        }

        _selfCheck.Check(TeamCount >= 1, "TeamCount", $"{TeamCount} 必须 ≥ 1");

        // ---- 弹珠两档球的边界：这里的每一对都必须自洽 ----
        _selfCheck.Check(BigBallEnergyMin >= BallSwarm.BigBallEnergy, "BigBallEnergyMin",
            $"{BigBallEnergyMin} < 大球门槛 {BallSwarm.BigBallEnergy}，摇出来的「大球」其实是小球");
        _selfCheck.Check(BigBallEnergyMax >= BigBallEnergyMin, "BigBallEnergyMax",
            $"{BigBallEnergyMax} < BigBallEnergyMin {BigBallEnergyMin}，摇能量会反向");
        _selfCheck.Check(EnergyLabelMinEnergy > BallSwarm.BigBallEnergy, "EnergyLabelMinEnergy",
            $"{EnergyLabelMinEnergy} ≤ 大球门槛 {BallSwarm.BigBallEnergy}，会出现「一串数字挂在小点上」");
        _selfCheck.Check(Disk.MaxRadius >= MathF.Ceiling(BallSwarm.BigMaxRadius), "涂色盘上限",
            $"Disk.MaxRadius {Disk.MaxRadius} < 视觉半径上限 {BallSwarm.BigMaxRadius}，球压过去会在环内留一圈没涂到的地");
        _selfCheck.Check(SmallBallAlpha is >= 0f and <= 1f, "SmallBallAlpha",
            $"{SmallBallAlpha} 不在 [0,1]：它会被当成实例 alpha 写进缓冲，>1 会过曝、<0 无意义");

        // ---- 经济与节奏：除零 / 反向 / 全灭的可能性 ----
        _selfCheck.Check(SimHz > 0f, "SimHz", $"{SimHz} ≤ 0");
        _selfCheck.Check(CostRampSeconds >= 1f, "CostRampSeconds", $"{CostRampSeconds} < 1，成本会在开局瞬间涨满");
        _selfCheck.Check(DropFeedInterval > 0f, "DropFeedInterval", $"{DropFeedInterval} ≤ 0，投料计时永远不推进");
        _selfCheck.Check(BigShotInterval > 0f, "BigShotInterval", $"{BigShotInterval} ≤ 0，蓄力射击永远不推进");
        _selfCheck.Check(LaunchEnergy > 0f && TurretBallsPerShot >= 1, "一轮齐射的成本",
            $"LaunchEnergy {LaunchEnergy} × TurretBallsPerShot {TurretBallsPerShot} 必须为正（否则开火免费/无意义）");
        _selfCheck.Check(IncomePerCell >= 0f, "IncomePerCell", $"{IncomePerCell} < 0 会让领土变成负债");
        _selfCheck.Check(MaxBalls >= 1, "MaxBalls", $"{MaxBalls} < 1");
        _selfCheck.Check(BaseHitPoints > 0f, "BaseHitPoints", $"{BaseHitPoints} ≤ 0，开局即全灭");
        _selfCheck.Check(MapSize >= 16 && MapSize <= 4096, "MapSize",
            $"{MapSize} 超出可用区间（贴图与圆盘/物理都按它算）");
        _selfCheck.Check(EnergyLabelMaxCount >= 0, "EnergyLabelMaxCount", $"{EnergyLabelMaxCount} < 0");
        _selfCheck.Check(TrailGhosts >= 0, "TrailGhosts", $"{TrailGhosts} < 0");

        // ---- 半径映射的两个端点：它们是"能量 ↔ 半径"的唯一约定，改了映射就要连着看 ----
        _selfCheck.Check(
            MathF.Abs(BallSwarm.RadiusForEnergy(BallSwarm.BigBallEnergy) - BallSwarm.BigMinRadius) < 0.001f,
            "半径曲线下端点",
            $"能量 {BallSwarm.BigBallEnergy} 应对应 BigMinRadius({BallSwarm.BigMinRadius})，实得 {BallSwarm.RadiusForEnergy(BallSwarm.BigBallEnergy)}");
        _selfCheck.Check(
            MathF.Abs(BallSwarm.RadiusForEnergy(BallSwarm.MaxEnergy) - BallSwarm.BigMaxRadius) < 0.001f,
            "半径曲线上端点",
            $"能量 {BallSwarm.MaxEnergy} 应对应 BigMaxRadius({BallSwarm.BigMaxRadius})，实得 {BallSwarm.RadiusForEnergy(BallSwarm.MaxEnergy)}");

        _selfCheck.Note("大球半径",
            $"出膛能量 {BigBallEnergyMin}~{BigBallEnergyMax} ⇒ 半径 " +
            $"{BallSwarm.RadiusForEnergy(BallSwarm.BigBallEnergy):F0}~{BallSwarm.RadiusForEnergy(BigBallEnergyMax):F0}"
            + $"（曲线顶端 {BallSwarm.BigMaxRadius} 需要 {BallSwarm.MaxEnergy} 能量，现在到不了）");

        CheckTerritoryRules();
    }

    /// <summary>
    /// 领土规则的真值表 —— 用一张 4×4 的临时地图，把**实际生效的**规则跑一遍。
    ///
    /// 为什么必须有它：`Attack` 曾经有一个"打自己格 = 加固"的分支，代码本身写得没错，
    /// 但唯一调用方（`BallSwarm.Paint`）在调用前就把自己格筛掉了 —— 于是那条规则
    /// **一辈子没生效过**，而类注释、玩法文档都在承诺它。这种"两份代码各自看着没问题、
    /// 组合起来缺一档"的缺陷，光读代码很难发现；写成可执行断言，改坏了启动就会响。
    /// </summary>
    void CheckTerritoryRules()
    {
        const string Table = "（领土规则真值表）";
        var map = new TerritoryMap(4, 4, 3);   // 真值表要用到势力 1/2/3，容量必须够

        // 中立格：直接占领，强度 = power
        map.Attack(0, 0, 1, 10);
        _selfCheck.Check(map.OwnerAt(0, 0) == 1 && map.StrengthAt(0, 0) == 10, "规则·打中立格" + Table,
            $"应被占领且强度 = power(10)，实得 owner={map.OwnerAt(0, 0)} strength={map.StrengthAt(0, 0)}");

        // 敌方格（强度 0，用 Claim 铺一张无防的地）：占领，残留 = power − 旧强度 + 14
        map.Claim(1, 0, 2);
        map.Attack(1, 0, 1, 10);
        _selfCheck.Check(map.OwnerAt(1, 0) == 1 && map.StrengthAt(1, 0) == 24, "规则·打敌方格（打得穿）" + Table,
            $"应易主且强度 = 10-0+14 = 24，实得 owner={map.OwnerAt(1, 0)} strength={map.StrengthAt(1, 0)}");

        // 敌方格（强度高于攻击力）：打不动，只削强度
        map.Attack(1, 0, 3, 5);
        _selfCheck.Check(map.OwnerAt(1, 0) == 1 && map.StrengthAt(1, 0) == 19, "规则·打敌方格（打不穿）" + Table,
            $"应保持 owner=1 且强度 24-5=19，实得 owner={map.OwnerAt(1, 0)} strength={map.StrengthAt(1, 0)}");

        // 自己的格：**不加固**（这条规则已删除，见 TerritoryMap 类注释）
        map.Attack(1, 0, 1, 10);
        _selfCheck.Check(map.StrengthAt(1, 0) == 19, "规则·打自己格（不加固）" + Table,
            $"强度应保持 19（加固规则已删），实得 {map.StrengthAt(1, 0)} —— 若你刚恢复了加固，把这条断言改成期望 20");
    }

    /// <summary>
    /// 第二阶段：**场景绑定 + 组件自报问题** —— 必须在节点都认领完之后跑（基地、弹珠层写入器已构造）。
    /// 以前这些检查散落在各个类里各自 <c>GD.PrintErr</c>，没人汇总；现在统一收口。
    /// </summary>
    void CheckSceneAndComponents()
    {
        _selfCheck.Check(MapLayer != null, "场景节点 MapLayer", "没连上（领土贴图无处可画）");
        _selfCheck.Check(BallLayer?.Multimesh != null, "场景节点 BallLayer", "没连上或没有 MultiMesh（整层球都不会出现）");
        _selfCheck.Check(ViewCamera != null, "场景节点 ViewCamera", "没连上（摄像机不会取景）");
        _selfCheck.Check(Bases != null, "场景节点 Bases", "没连上（四个基地不会被认领）");
        _selfCheck.Check(Territory != null, "场景节点 Territory", "没连上（右侧领土面板不会更新）");
        _selfCheck.Check(Drop != null, "场景节点 Drop", "没连上（左侧钉板不会工作、也不会有落门事件）");
        _selfCheck.Check(Labels != null, "场景节点 Labels", "没连上（大球上的能量数字不会显示）");

        _selfCheck.AddRange(_ballWriter.Problems);
        _selfCheck.AddRange(_bases.Problems);
        if (Drop != null)
        {
            _selfCheck.AddRange(Drop.Problems);
        }

        if (Labels != null)
        {
            _selfCheck.Check(Labels.Capacity > 0, "EnergyLabels 槽位",
                "场景里没有 Num1..N，能量数字永远不显示");
            _selfCheck.Note("能量数字槽位",
                $"场景提供 {Labels.Capacity} 个，EnergyLabelMaxCount = {EnergyLabelMaxCount}"
                + (Labels.Capacity < EnergyLabelMaxCount ? "（不够，会被夹到场景的数量）" : ""));
        }

        if (Territory != null)
        {
            _selfCheck.AddRange(Territory.Problems);
            _selfCheck.Check(Territory.RowCount >= TeamCount, "领土面板行数",
                $"只有 {Territory.RowCount} 行，装不下 {TeamCount} 支势力（多出来的写不进去）");
        }

        _selfCheck.Note("势力数", $"{TeamCount}（调色板容量 {Palette.MaxTeams}）");
        _selfCheck.Note("弹珠层缓冲", _ballWriter.LayoutSummary);
    }

    /// <summary>
    /// 造地图那一层：一张 <see cref="MapSize"/>² 的 RGBA8 贴图（领土像素每帧整张上传）+ 直通着色器。
    /// 只在启动时调一次 —— 换局只重置 <see cref="TerritoryMap"/> 的内容，不重建贴图。
    /// </summary>
    void CreateMapLayer()
    {
        _mapPixels = new byte[MapSize * MapSize * 4];
        _mapImage = Image.CreateEmpty(MapSize, MapSize, false, Image.Format.Rgba8);
        _mapImage.SetData(MapSize, MapSize, false, Image.Format.Rgba8, _mapPixels);
        _mapTexture = ImageTexture.CreateFromImage(_mapImage);
        MapLayer.Texture = _mapTexture;

        // 地图的采样方式钉在着色器里（`filter_nearest`），不依赖节点属性 ——
        // 理由见 Shaders/Map.gdshader 顶部。这是纯直通，不改变任何颜色。
        var mapShader = GD.Load<Shader>("res://Shaders/Map.gdshader");
        if (mapShader == null)
        {
            GD.PrintErr("[地图] 载入 res://Shaders/Map.gdshader 失败，地图会按线性插值显示");
            return;
        }

        var mapMaterial = new ShaderMaterial { Shader = mapShader };
        mapMaterial.SetShaderParameter("map_texture", _mapTexture);
        MapLayer.Material = mapMaterial;
    }

    /// <summary>
    /// 认领两块面板（**不创建任何节点**）并接上事件。
    ///
    /// 版面是固定的三栏：**左 = 掉落球板 / 中 = 地图 / 右 = 领土占领**
    /// （摄像机在 <see cref="SetupCamera"/> 里一次性把三块内容全框进来，之后不再动）。
    /// 两块面板的尺寸/配色/字体/内部几何都是它们自己节点上的 `[Export]`，
    /// 在编辑器里拖节点改位置、在检查器里改参数即可。
    /// </summary>
    void BindPanels()
    {
        // 缺节点的报错统一交给启动自检（见 CheckSceneAndComponents）——这里只负责"能接的接上"。
        if (Drop == null)
        {
            return;
        }

        // 落门事件是**唯一**的耦合点：板只报"哪个门、哪支势力"，效果在这里决定
        // （对齐参考作品 RandomBallUI → Turret 的事件式耦合，权威数据留在世界侧）。
        Drop.GateEntered += OnDropGateEntered;
    }

    /// <summary>
    /// 弹珠层的材质参数：`style = -1` 是"形态由实例的自定义数据决定"（见 `Disc.gdshader`），
    /// 这是功能性的开关，不能依赖场景文件（编辑器会把它覆盖回去）。
    /// </summary>
    void ConfigureBallShader()
    {
        if (BallLayer.Material is ShaderMaterial ball)
        {
            ball.SetShaderParameter("style", -1f);
            ball.SetShaderParameter("brightness", 1.35f);
        }
    }

    /// <summary>
    /// 采样过滤在代码里强制成 **nearest**（最近邻）。
    ///
    /// 项目默认（`rendering/textures/canvas_textures/default_texture_filter=0`）和场景里
    /// MapLayer 的 `texture_filter` 本来就已经是 nearest，但球层只是"继承默认值"——
    /// 而编辑器会覆盖场景文件，所以这里显式钉死，免得哪天被改回线性插值
    /// （线性会让领土边界发糊、球边缘发虚）。基地那几个节点由 <see cref="BaseVisuals"/> 自己钉。
    /// </summary>
    void ConfigureSampling()
    {
        const CanvasItem.TextureFilterEnum Nearest = CanvasItem.TextureFilterEnum.Nearest;
        MapLayer.TextureFilter = Nearest;
        BallLayer.TextureFilter = Nearest;
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
    /// 固定摄像机：把"左板 + 地图 + 右面板"三块内容的世界包围盒一次性框进视口，
    /// 之后**不再动**（没有 WASD 平移、没有滚轮缩放）。
    ///
    /// ⚠ 三块内容的位置都来自场景节点，所以想调版面就直接拖节点 —— 摄像机会跟着重新框
    ///   （启动时一次 + 视口尺寸变化时一次）。
    /// </summary>
    void SetupCamera()
    {
        var content = new Rect2(-MapSize * 0.5f, -MapSize * 0.5f, MapSize, MapSize);
        if (Drop != null)
        {
            content = content.Merge(Drop.WorldRect);
        }

        if (Territory != null)
        {
            content = content.Merge(Territory.WorldRect);
        }

        Vector2 viewport = GetViewportRect().Size;
        float zoom = MathF.Min(
            viewport.X / MathF.Max(1f, content.Size.X),
            viewport.Y / MathF.Max(1f, content.Size.Y));

        ViewCamera.Zoom = new Vector2(zoom, zoom);
        ViewCamera.Position = content.GetCenter();
    }
}

using System;
using System.Collections.Generic;
using Godot;

namespace TerritoryWar.Game;

/// <summary>
/// 左侧「掉落球」钉板（Plinko）—— 世界空间的 Node2D。
///
/// **板上的每个部件、每一行文字都是场景里的节点**（`Scenes/Main.tscn` 的 `Board` 下面）：
///   · 部件：`Bar`（横板）、`Pegs/Peg1..11`（钉，3 行 4+3+4）、
///     `PassGates/GateX2·X4·X8·Roulette`（穿过式门）、`Slots/SlotFire…SlotFirework`（底排落格式）；
///     每个门/槽是一个 `Node2D`（位置 = 矩形左上角），里面的 `Bg` 是单位方块 `Polygon2D`（`scale` = 尺寸）；
///   · 文字：各门下的 `Label` / `Count`、`Hint` —— 都是 `Label`，内容在场景里改就行。
///
/// 几何**从这个节点树读出来**（矩形 = `Bg.Scale`、钉的半径 = `Scale.X/2`），
/// 所以在编辑器里拖 / 缩放部件就等于改物理。本脚本只做三件事：
///   1. 模拟（<see cref="Step"/>）；
///   2. 把状态写回节点（横板位置、门的闪光色、落球计数、转盘文字、提示显隐）；
///   3. 落门时发 <see cref="GateEntered"/>。
/// 唯一仍由代码画的是**球**（板上几十个动态圆，做成节点不现实）。
///
/// ⚠ 单位是**世界单位**：地图 1000×1000、摄像机固定缩放 0.72，所以板子做成 748×640 世界单位。
///   改版面尺寸记得同比改 <see cref="Gravity"/>，否则单颗球的掉落时间（也就是人口）会漂。
/// </summary>
public partial class DropBoard : Node2D
{
    // ==================================================================
    // 门的编号：前 4 个是"穿过式"（球不消失），后 7 个是底排"落格式"（球被吃掉）
    // ==================================================================

    public const int GateMultiply2 = 0;
    public const int GateMultiply4 = 1;
    public const int GateMultiply8 = 2;
    public const int GateRoulette = 3;
    public const int GateFire = 4;
    public const int GateAmmo = 5;
    public const int GateShield = 6;
    public const int GateBigBall = 7;
    public const int GateBarrage = 8;
    public const int GateFan = 9;
    public const int GateFirework = 10;

    /// <summary>门总数（含穿过式）。</summary>
    public const int GateCount = 11;

    /// <summary>穿过式的门有几个（0..3）。底排落格式从它开始，判定里到处都用这个边界。</summary>
    public const int PassGateCount = GateRoulette + 1;

    /// <summary>带"穿过次数"计数的倍率门有几个（0..2，转盘门有自己的文字）。</summary>
    const int MultiplyGateCount = GateMultiply8 + 1;

    /// <summary>场景里的节点名 → 门编号（**名字是契约，改名就失效**）。</summary>
    static readonly (string Path, int Gate)[] GateNodes =
    {
        ("PassGates/GateX2", GateMultiply2),
        ("PassGates/GateX4", GateMultiply4),
        ("PassGates/GateX8", GateMultiply8),
        ("PassGates/GateRoulette", GateRoulette),
        ("Slots/SlotFire", GateFire),
        ("Slots/SlotAmmo", GateAmmo),
        ("Slots/SlotShield", GateShield),
        ("Slots/SlotBigBall", GateBigBall),
        ("Slots/SlotBarrage", GateBarrage),
        ("Slots/SlotFan", GateFan),
        ("Slots/SlotFirework", GateFirework),
    };

    /// <summary>三个倍率门各自代表的值（下标 = 门编号）。</summary>
    static readonly int[] MultiplyGateValue = { 2, 4, 8 };

    // ---- 物理（世界单位 / 秒；⚠ 改版面尺寸记得同比改 Gravity）----
    [ExportGroup("球")]
    [Export] public int Capacity = 160;
    [Export] public float Gravity = 900f;
    [Export] public float MaxSpeed = 1000f;
    [Export] public float BallMaxLife = 8f;
    [Export] public int SubSteps = 2;
    [Export] public float SpawnInset = 30f;
    [Export] public float SpawnSideSpeed = 145f;
    [Export] public float SpawnDropSpeed = 83f;

    [ExportGroup("碰撞")]
    [Export] public float Restitution = 0.62f;
    [Export] public float WallRestitution = 0.72f;
    [Export] public float PegKick = 60f;

    [ExportGroup("横板运动")]
    [Export] public float BarSwingAmplitude = 164f;
    [Export] public float BarSwingSpeed = 1.6f;
    [Export] public float BarPush = 0.35f;

    [ExportGroup("门")]
    [Export] public int[] RouletteValues = { 1, 2, 4, 8 };
    [Export] public float FlashSeconds = 0.3f;
    [Export] public float RollShowSeconds = 0.9f;

    /// <summary>落格 / 过门事件：(哪个门, 哪支势力, 倍率值)。倍率值只对倍率门与转盘门有意义，其余为 0。</summary>
    public event Action<int, byte, int> GateEntered = null;

    /// <summary>这块板在世界坐标里占的矩形（摄像机用它来算"三块内容要全部装下"的缩放）。</summary>
    public Rect2 WorldRect => new(GlobalPosition, _panelSize);

    /// <summary>
    /// 绑定场景时发现的问题（缺横板 / 缺某个门 / 缺 <c>Bg</c>）。由 <see cref="SelfCheck"/> 汇总打印。
    /// ⚠ 这些问题**不会崩**：缺哪个门就只是那一门静默失效 —— 所以必须在启动时说清楚。
    /// </summary>
    public IReadOnlyList<string> Problems => _problems;

    readonly List<string> _problems = new();

    // ==================================================================
    // 从场景读出来的版面
    // ==================================================================

    /// <summary>底板（`Frame/Bg`）的尺寸 —— 它多大，可玩区域就多大。</summary>
    Vector2 _panelSize = new(748f, 640f);

    /// <summary>球的半径。由第一颗钉的尺寸推出来（钉 : 球 ≈ 1 : 1.2），保证球不会被钉子穿透。</summary>
    float _ballRadius = 10.8f;

    Vector2[] _pegCenter = Array.Empty<Vector2>();
    float[] _pegRadius = Array.Empty<float>();

    readonly Rect2[] _gateRect = new Rect2[GateCount];
    readonly Polygon2D[] _gateBg = new Polygon2D[GateCount];
    readonly Label[] _gateLabel = new Label[GateCount];
    readonly Label[] _gateCount = new Label[GateCount];

    /// <summary>场景里给门配的颜色 —— 闪光是以它为基准往白里插值，所以要记下来。</summary>
    readonly Color[] _gateBaseColor = new Color[GateCount];

    /// <summary>上一次写进 `Count` Label 的数字（只有变化时才写，省掉每帧的排版开销）。</summary>
    readonly int[] _shownHits = new int[GateCount];

    int[] _rouletteValues = { 1 };

    Polygon2D _bar = null!;
    float _barSceneY;
    float _barHalfWidth;
    float _barHalfThickness;
    float _barCenterX;
    float _barY;

    /// <summary>落点线 = 底排槽位的上沿。</summary>
    float _slotTop;

    Label _rouletteLabel = null!;
    Label _hint = null!;
    string _shownRollText = "";
    bool _sceneBound;

    // ==================================================================
    // 模拟状态：紧凑数组 + 每颗球自己那几列
    // ==================================================================

    float[] _x = Array.Empty<float>();
    float[] _y = Array.Empty<float>();
    float[] _px = Array.Empty<float>();     // 上一步的坐标（渲染插值用，见 Render）
    float[] _py = Array.Empty<float>();
    float[] _vx = Array.Empty<float>();
    float[] _vy = Array.Empty<float>();
    float[] _age = Array.Empty<float>();
    byte[] _team = Array.Empty<byte>();

    /// <summary>每颗球已穿过哪些门（位掩码），保证一颗球对同一个门只结算一次。</summary>
    int[] _passed = Array.Empty<int>();

    int _count;
    readonly float[] _flash = new float[GateCount];
    readonly int[] _hits = new int[GateCount];

    Random _rng = new(0);
    float _time;
    float _barX;
    float _prevBarX;            // 物理用：本次 Step 开始前的板位置（算子步内的板速度）
    float _barPrevRender;       // 渲染用：上一次 Step 结束时的板位置
    float _renderAlpha = 1f;
    int _lastRoll;
    float _rollFlash;

    public override void _Ready()
    {
        Allocate();
        BindSceneNodes();
        Reset();
    }

    /// <summary>换一局重新播种（<c>GameRoot.StartMatch</c> 里调，保证同一 Seed 可复现）。</summary>
    public void Reseed(int seed) => _rng = new Random(seed);

    void Allocate()
    {
        Capacity = Math.Max(1, Capacity);
        _x = new float[Capacity];
        _y = new float[Capacity];
        _px = new float[Capacity];
        _py = new float[Capacity];
        _vx = new float[Capacity];
        _vy = new float[Capacity];
        _age = new float[Capacity];
        _team = new byte[Capacity];
        _passed = new int[Capacity];
    }

    /// <summary>认领场景节点并读出几何（板子尺寸、钉、门/槽的矩形、横板、各门的文字）。</summary>
    void BindSceneNodes()
    {
        if (GetNodeOrNull<Polygon2D>("Frame/Bg") is { } frame)
        {
            _panelSize = frame.Scale;                      // 底板多大，可玩区域就多大
        }

        if (GetNodeOrNull<Polygon2D>("Bar") is { } bar)
        {
            _bar = bar;
            _barSceneY = bar.Position.Y;
            _barHalfWidth = bar.Scale.X * 0.5f;
            _barHalfThickness = bar.Scale.Y * 0.5f;
            _barCenterX = bar.Position.X + _barHalfWidth;
            _barY = bar.Position.Y + _barHalfThickness;
        }

        BindPegs();
        BindGates();

        _hint = GetNodeOrNull<Label>("Hint");
        _sceneBound = _bar != null;

        if (!_sceneBound)
        {
            _problems.Add("钉板：场景里找不到 Bar 节点，整块板都不会工作");
        }
    }

    void BindPegs()
    {
        var centers = new List<Vector2>();
        var radii = new List<float>();
        float scaleOfFirstPeg = 0f;

        if (GetNodeOrNull<Node2D>("Pegs") is { } pegRoot)
        {
            foreach (Node child in pegRoot.GetChildren())
            {
                if (child is not Node2D peg)
                {
                    continue;
                }

                if (centers.Count == 0)
                {
                    scaleOfFirstPeg = peg.Scale.X;
                }

                centers.Add(ToBoard(peg.GlobalPosition));
                radii.Add(MathF.Min(peg.Scale.X, peg.Scale.Y) * 0.5f);
            }
        }

        _pegCenter = centers.ToArray();
        _pegRadius = radii.ToArray();

        // 球的半径跟着第一颗钉走（场景里把钉调大，球一起变大，碰撞比例不变）
        _ballRadius = scaleOfFirstPeg > 0f ? scaleOfFirstPeg * 0.6f : 7f;
    }

    void BindGates()
    {
        for (int g = 0; g < GateNodes.Length; g++)
        {
            (string path, int gate) = GateNodes[g];
            if (GetNodeOrNull<Node2D>(path) is not { } part)
            {
                _problems.Add($"钉板：场景里找不到门节点「{path}」，这一门静默失效");
                continue;
            }

            Polygon2D bg = part.GetNodeOrNull<Polygon2D>("Bg");
            if (bg == null)
            {
                _problems.Add($"钉板：「{path}」下没有 Bg（单位方块 Polygon2D），这一门静默失效");
                continue;
            }

            _gateBg[gate] = bg;
            _gateRect[gate] = new Rect2(ToBoard(part.GlobalPosition), bg.Scale);
            _gateBaseColor[gate] = bg.Color;

            // 文字宽度跟着格子走：在编辑器里拉宽格子，字也跟着居中
            _gateLabel[gate] = part.GetNodeOrNull<Label>("Label");
            _gateCount[gate] = part.GetNodeOrNull<Label>("Count");
            FitLabel(_gateLabel[gate], bg.Scale.X);
            FitLabel(_gateCount[gate], bg.Scale.X);
        }

        _rouletteLabel = _gateLabel[GateRoulette];

        // 转盘的可选值：缓存一份，"每颗球过门时"就不必再判断/分配数组了
        _rouletteValues = RouletteValues is { Length: > 0 } ? RouletteValues : new[] { 1 };

        // 落点线 = 底排槽位的上沿（底排一个都没找到就退化成"板底"）
        float slotTop = float.MaxValue;
        for (int g = GateFire; g < GateCount; g++)
        {
            if (_gateBg[g] != null)
            {
                slotTop = MathF.Min(slotTop, _gateRect[g].Position.Y);
            }
        }

        _slotTop = slotTop == float.MaxValue ? _panelSize.Y : slotTop;
    }

    /// <summary>把 Label 的宽度对齐到格子宽度（居中排版由 Label 自己的 horizontal_alignment 负责）。</summary>
    static void FitLabel(Label label, float width)
    {
        if (label != null)
        {
            label.Size = new Vector2(width, label.Size.Y);
        }
    }

    /// <summary>全局画布坐标 → 板内局部坐标（用变换求逆，这样部件放在几层容器里都对）。</summary>
    Vector2 ToBoard(Vector2 global) => GetGlobalTransform().AffineInverse() * global;

    // ==================================================================
    // 模拟
    // ==================================================================

    /// <summary>重开一局时清空（球、横板相位、闪光、计数）。</summary>
    public void Reset()
    {
        _count = 0;
        _time = 0f;
        Array.Clear(_flash);
        Array.Clear(_hits);

        // ⚠ `_shownHits` 是"变化才写 Label"的缓存，必须清成**不可能的值**：
        //   清成 0 的话，SyncText 的守卫 `_hits[g] != _shownHits[g]` 会因为 0 == 0 而不放行，
        //   于是三个 Count Label 会一直显示上一局的总数（实测：重开一局后仍显示 37）。
        //   转盘文字那边用的是 ""，天然不同，所以一直是对的 —— 这里照它办。
        for (int g = 0; g < GateCount; g++)
        {
            _shownHits[g] = -1;
        }
        _lastRoll = 0;
        _rollFlash = 0f;
        _shownRollText = "";
        _barX = _barPrevRender = _barCenterX;
        _renderAlpha = 1f;
        SyncGateColors();
        SyncText();
    }

    /// <summary>
    /// 投一颗球进板。由炮塔按节奏调用 —— 一颗球 = 一次"抽签"。
    /// 出膛位置在顶部随机、初速带一点随机横移，落点才不会每次都一样。
    /// </summary>
    public void Push(byte team)
    {
        if (_count >= Capacity)
        {
            RemoveAt(0);   // 满了就顶掉最老的一颗
        }

        int i = _count++;
        float left = SpawnInset;
        float right = MathF.Max(left + 1f, _panelSize.X - SpawnInset);
        _x[i] = left + (float)_rng.NextDouble() * (right - left);
        _y[i] = _ballRadius + 6f;
        _vx[i] = (float)(_rng.NextDouble() - 0.5) * SpawnSideSpeed;
        _vy[i] = (float)_rng.NextDouble() * SpawnDropSpeed;
        _age[i] = 0f;
        _team[i] = team;
        _passed[i] = 0;

        // 新球没有"上一步"可言，插值起点就是它自己 —— 否则会从上一位占用这个槽位的球那里拉一条线
        _px[i] = _x[i];
        _py[i] = _y[i];
    }

    /// <summary>
    /// 跟着模拟走（<c>GameRoot.Simulate</c> 里调），所以暂停 / 倍速对本板一样有效。
    ///
    /// ⚠ 模拟是固定 60Hz、而屏幕通常 300+ FPS，所以**必须**配合 <see cref="Render"/> 做插值：
    ///   只按模拟节奏画，球看起来就是"一步一跳"（用户反馈过的"一卡一卡"）。
    /// </summary>
    public void Step(float dt)
    {
        if (!_sceneBound)
        {
            return;
        }

        // 记下这一步开始前的位置：渲染时在 [旧, 新] 之间按累加器余量插值
        for (int i = 0; i < _count; i++)
        {
            _px[i] = _x[i];
            _py[i] = _y[i];
        }

        _barPrevRender = _barX;

        _time += dt;
        _prevBarX = _barX;
        _barX = _barCenterX + BarSwingAmplitude * MathF.Sin(_time * MathF.Max(0.01f, BarSwingSpeed));

        for (int g = 0; g < GateCount; g++)
        {
            _flash[g] = MathF.Max(0f, _flash[g] - dt);
        }

        _rollFlash = MathF.Max(0f, _rollFlash - dt);

        int subSteps = Math.Max(1, SubSteps);
        float sub = dt / subSteps;
        for (int s = 0; s < subSteps; s++)
        {
            Integrate(sub);
        }
    }

    void Integrate(float dt)
    {
        // 量纲（别被这个写法骗了）：这里的 dt 是**子步** dt = 整帧 / SubSteps，
        // 所以 `dt * SubSteps` 正好是整帧时长 ⇒ 这个式子算出来的就是横板的**真实平均速度**。
        // （⚠ 曾经把注释写成"多除了一个 SubSteps"并告诫别人别改 —— 那是个错误的量纲推导；
        //   真按它去"修正"（再乘 SubSteps）会把 BarPush 的效果放大 2 倍。）
        float barVx = dt > 0f ? (_barX - _prevBarX) / (dt * Math.Max(1, SubSteps)) : 0f;

        for (int i = 0; i < _count; i++)
        {
            _age[i] += dt;
            _vy[i] += Gravity * dt;

            // 限速：撞钉时速度叠加可能失稳，夹一下比调参便宜
            float speed = MathF.Sqrt(_vx[i] * _vx[i] + _vy[i] * _vy[i]);
            if (speed > MaxSpeed && speed > 0f)
            {
                float k = MaxSpeed / speed;
                _vx[i] *= k;
                _vy[i] *= k;
            }

            _x[i] += _vx[i] * dt;
            _y[i] += _vy[i] * dt;

            BounceOffWalls(i);
            BounceBar(i, barVx);

            for (int p = 0; p < _pegCenter.Length; p++)
            {
                BouncePeg(i, p);
            }

            TryPassGates(i);

            // 落格：够到底排的上沿就按落点分格，然后这颗球就没了
            if (_y[i] + _ballRadius >= _slotTop)
            {
                LandInSlot(i);
                continue;
            }

            // 兜底回收：卡住 / 超时
            if (_age[i] > BallMaxLife || _y[i] > _panelSize.Y + 40f)
            {
                RemoveAt(i);
                i--;
            }
        }
    }

    void BounceOffWalls(int i)
    {
        float left = _ballRadius;
        float right = _panelSize.X - _ballRadius;

        if (_x[i] < left)
        {
            _x[i] = left;
            _vx[i] = MathF.Abs(_vx[i]) * WallRestitution;
        }
        else if (_x[i] > right)
        {
            _x[i] = right;
            _vx[i] = -MathF.Abs(_vx[i]) * WallRestitution;
        }
    }

    /// <summary>球撞横板：把板当成一个轴对齐矩形，取球心的最近点求法线。</summary>
    void BounceBar(int i, float barVx)
    {
        float left = _barX - _barHalfWidth;
        float right = _barX + _barHalfWidth;
        float top = _barY - _barHalfThickness;
        float bottom = _barY + _barHalfThickness;

        float closestX = Math.Clamp(_x[i], left, right);
        float closestY = Math.Clamp(_y[i], top, bottom);
        float dx = _x[i] - closestX;
        float dy = _y[i] - closestY;
        float distSq = dx * dx + dy * dy;

        if (distSq >= _ballRadius * _ballRadius)
        {
            return;
        }

        float dist = MathF.Sqrt(distSq);
        float nx;
        float ny;

        if (dist > 0.0001f)
        {
            nx = dx / dist;
            ny = dy / dist;
        }
        else
        {
            // 球心正好落在板内（穿透了）：按"从板上面顶出来"处理
            nx = 0f;
            ny = _y[i] < _barY ? -1f : 1f;
        }

        _x[i] = closestX + nx * (_ballRadius + 0.01f);
        _y[i] = closestY + ny * (_ballRadius + 0.01f);

        float vn = _vx[i] * nx + _vy[i] * ny;
        if (vn < 0f)
        {
            _vx[i] -= (1f + Restitution) * vn * nx;
            _vy[i] -= (1f + Restitution) * vn * ny;
        }

        // 运动的板会"抽"球一下，这是它存在的意义：给横速
        _vx[i] += barVx * BarPush;
    }

    /// <summary>球撞钉：圆-圆，推出重叠 + 沿法线反射。</summary>
    void BouncePeg(int i, int p)
    {
        Vector2 peg = _pegCenter[p];
        float reach = _ballRadius + _pegRadius[p];
        float dx = _x[i] - peg.X;
        float dy = _y[i] - peg.Y;
        float distSq = dx * dx + dy * dy;

        if (distSq >= reach * reach || distSq < 0.0001f)
        {
            return;
        }

        float dist = MathF.Sqrt(distSq);
        float nx = dx / dist;
        float ny = dy / dist;

        _x[i] = peg.X + nx * (reach + 0.01f);
        _y[i] = peg.Y + ny * (reach + 0.01f);

        float vn = _vx[i] * nx + _vy[i] * ny;
        if (vn < 0f)
        {
            _vx[i] -= (1f + Restitution) * vn * nx;
            _vy[i] -= (1f + Restitution) * vn * ny;
        }

        // 钉上给一点随机横移，否则球会沿着钉子垂直弹、永远落在同一条线上
        _vx[i] += (float)(_rng.NextDouble() - 0.5) * PegKick;
    }

    /// <summary>
    /// 倍率门与转盘门是**穿过**的：球不消失，只把倍率记到该队头上。
    /// 用位掩码保证一颗球对同一个门只结算一次 —— 否则门带有几十单位高，一帧内会命中好多次。
    /// </summary>
    void TryPassGates(int i)
    {
        float y = _y[i];
        float x = _x[i];

        for (int g = 0; g < PassGateCount; g++)
        {
            if (_gateBg[g] == null || (_passed[i] & (1 << g)) != 0)
            {
                continue;
            }

            Rect2 r = _gateRect[g];
            if (y < r.Position.Y || y > r.Position.Y + r.Size.Y
                || x < r.Position.X || x > r.Position.X + r.Size.X)
            {
                continue;
            }

            _passed[i] |= 1 << g;

            if (g == GateRoulette)
            {
                int roll = _rouletteValues[_rng.Next(_rouletteValues.Length)];
                _lastRoll = roll;
                _rollFlash = RollShowSeconds;
                Resolve(i, g, roll);
            }
            else
            {
                Resolve(i, g, MultiplyGateValue[g]);
            }
        }
    }

    /// <summary>
    /// 球到底排了：落在哪个槽位，就结算哪个门（槽位之间有缝时取最近的中心）。
    ///
    /// ⚠ 兜底值是 **-1（不结算）而不是"发射格"**：槽位全都没绑定时，球应该默默消失，
    ///   而不是每一颗都白拿一轮齐射（见 <see cref="Fire"/> 的注释）。
    /// </summary>
    void LandInSlot(int i)
    {
        int best = -1;
        float bestDistance = float.MaxValue;

        for (int g = GateFire; g < GateCount; g++)
        {
            if (_gateBg[g] == null)
            {
                continue;
            }

            Rect2 r = _gateRect[g];
            if (_x[i] >= r.Position.X && _x[i] <= r.Position.X + r.Size.X)
            {
                best = g;
                break;
            }

            float d = MathF.Abs(_x[i] - (r.Position.X + r.Size.X * 0.5f));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = g;
            }
        }

        byte team = _team[i];
        RemoveAt(i);
        Fire(best, team, 0);
    }

    /// <summary>球还在板上时结算一个门（团队从球上读）。</summary>
    void Resolve(int ballIndex, int gate, int value) => Fire(gate, _team[ballIndex], value);

    /// <summary>
    /// 结算一个门：点亮闪光、记一次流量、把事件发出去。
    ///
    /// ⚠ **这是唯一的结算入口，所以绑定校验放在这里**：没绑定 <c>Bg</c> 的门一律不结算
    ///   （不发光、不计数、不发事件）。以前只有"穿过式"那条路径校验了绑定，
    ///   而落格式的兜底值取的是 `GateFire` —— 于是把 `Board/Slots` 整块改名之后，
    ///   每颗落到底的球都会白拿一轮齐射（**fail-open**：坏得比不结算更严重）。
    /// </summary>
    void Fire(int gate, byte team, int value)
    {
        if (gate < 0 || gate >= GateCount || _gateBg[gate] == null)
        {
            return;
        }

        _flash[gate] = FlashSeconds;
        _hits[gate]++;
        GateEntered?.Invoke(gate, team, value);
    }

    /// <summary>
    /// 清掉某支势力在板上的抽签球（它出局时调）。
    ///
    /// ⚠ 不清的话这些"死签"还会继续掉最多 <see cref="BallMaxLife"/> 秒：占着 Capacity
    ///   （满了会顶掉别人最老的一颗），而且让倍率门的计数继续涨 —— 但效果会被
    ///   `GameRoot.OnDropGateEntered` 的存活判断吃掉，表现为"红队的签还在掉、×2 还在涨，
    ///   却什么都不发生"。
    /// </summary>
    public void PurgeTeam(byte team)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_team[i] != team)
            {
                continue;
            }

            RemoveAt(i);
            i--;   // 末尾顶上来的那颗也要看一遍
        }

        SyncText();
    }

    void RemoveAt(int i)
    {
        int last = --_count;
        if (i == last)
        {
            return;
        }

        _x[i] = _x[last];
        _y[i] = _y[last];
        _px[i] = _px[last];
        _py[i] = _py[last];
        _vx[i] = _vx[last];
        _vy[i] = _vy[last];
        _age[i] = _age[last];
        _team[i] = _team[last];
        _passed[i] = _passed[last];
    }

    // ==================================================================
    // 渲染：部件与文字都是场景节点，脚本只写回状态 + 画球
    // ==================================================================

    /// <summary>
    /// 每帧调一次：先把横板在两个模拟步之间插值，再把状态写回场景节点
    /// （横板位置、门的闪光色、落球计数、转盘文字、提示显隐），最后重画球。
    ///
    /// <paramref name="alpha"/> = 累加器余量 ÷ 步长（0~1，见 <c>GameRoot._Process</c>）。
    /// ⚠ 必须每帧调、且必须插值：模拟 60Hz、屏幕 300+ FPS，只按模拟节奏画就是"一卡一卡"。
    /// </summary>
    public void Render(float alpha)
    {
        _renderAlpha = Math.Clamp(alpha, 0f, 1f);

        if (_sceneBound)
        {
            float barX = _barPrevRender + (_barX - _barPrevRender) * _renderAlpha;
            _bar.Position = new Vector2(barX - _barHalfWidth, _barSceneY);
            SyncGateColors();
            SyncText();
        }

        QueueRedraw();
    }

    /// <summary>把闪光写回门的颜色（以场景里配的颜色为基准往白里插值）。</summary>
    void SyncGateColors()
    {
        for (int g = 0; g < GateCount; g++)
        {
            if (_gateBg[g] == null)
            {
                continue;
            }

            float f = FlashSeconds > 0f ? _flash[g] / FlashSeconds : 0f;
            _gateBg[g].Color = _gateBaseColor[g].Lerp(Colors.White, f);
        }
    }

    /// <summary>只有变化时才写 Label（省掉每帧的排版开销）。</summary>
    void SyncText()
    {
        for (int g = 0; g < MultiplyGateCount; g++)
        {
            if (_gateCount[g] != null && _hits[g] != _shownHits[g])
            {
                _shownHits[g] = _hits[g];
                _gateCount[g].Text = _hits[g].ToString();
            }
        }

        if (_rouletteLabel != null)
        {
            string text = _rollFlash > 0f && _lastRoll > 0 ? $"转盘 → ×{_lastRoll}" : "转盘 ?";
            if (text != _shownRollText)
            {
                _shownRollText = text;
                _rouletteLabel.Text = text;
            }
        }

        if (_hint != null)
        {
            _hint.Visible = _count == 0;
        }
    }

    /// <summary>只画球 —— 板的部件与文字全是场景节点。</summary>
    public override void _Draw()
    {
        for (int i = 0; i < _count; i++)
        {
            float x = _px[i] + (_x[i] - _px[i]) * _renderAlpha;
            float y = _py[i] + (_y[i] - _py[i]) * _renderAlpha;
            DrawCircle(new Vector2(x, y), _ballRadius, Palette.TeamColor(_team[i]));
        }
    }
}

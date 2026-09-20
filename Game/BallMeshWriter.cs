using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace TerritoryWar.Game;

/// <summary>弹珠层的外观参数。都是 <c>GameRoot</c> 上的 [Export]，在检查器里调。</summary>
public readonly record struct BallStyle(
    int TrailGhosts,
    float TrailSpacing,
    float SmallBallAlpha);

/// <summary>
/// 弹珠层（`BallLayer` 的 MultiMesh）的写入器 —— 负责两件事：
/// **守住实例缓冲的布局契约**，以及**每帧把整块缓冲一次写给引擎**。
///
/// ─── 为什么要整块写，而不是逐实例 SetInstance* ───────────────────────────
/// 逐实例是每颗球两次"托管 ↔ 引擎"调用，实测 1 万颗要 1.9 ms；整块写只要 0.14 ms。
/// 填 buffer 是纯 C# 数组写，每颗球只写自己那几个 float，线程之间没有共享写入，
/// 所以按球数分段并行填；写引擎（<c>Buffer</c> 赋值）只在最后做一次、留在主线程。
///
/// ─── 布局契约（⚠ 改这里之前先读 <see cref="VerifyLayout"/> 的注释）────────────────
/// 缓冲布局固定是「Transform2D(8) → 颜色(4) → 自定义数据(4)」，颜色槽偏移是 **8**。
/// 两处偏移都是**累加算出来的固定值**，不能用"步长 − 4"去推：一旦自定义数据被关掉，
/// 步长变成 12，"步长 − 4" 就落到颜色通道的最后一个 float 上，着色器读到的颜色全零、
/// 整层球因为 alpha = 0 直接消失，而且不报任何错。
///
/// 实例数固定为 `容量 × (1 + 残影数)`，靠 <c>VisibleInstanceCount</c> 控制画几个 ——
/// 这样缓冲长度恒定，不必每帧重新分配（Buffer 的长度必须严格等于 实例数 × 步长）。
/// </summary>
public sealed class BallMeshWriter
{
    /// <summary>实例的"形态"，写进自定义数据的 x 通道，`Disc.gdshader` 按它分支。</summary>
    public const int KindSmall = 0;
    public const int KindBig = 1;

    /// <summary>2D 变换占 8 个 float：两行 (xx, xy, 0, ox) / (yx, yy, 0, oy)。</summary>
    const int Transform2DFloats = 8;

    /// <summary>
    /// 0 号哨兵实例的变换位置。
    ///
    /// ⚠ 说清楚它到底顶什么用：整块写成功后它会**被覆盖**，所以它平时既不参与绘制、
    ///   也不承担"隐藏实例"的职责。它剩下两个作用：
    ///   ① 顺带触发一次实例级 setter（见 <see cref="MarkBufferDirty"/>）；
    ///   ② 万一整块写没能生效（异常/被跳过），0 号会出现在很远的地方（**看得见**），
    ///      而不是以错误变换和真球混在一起（那是静默的）。
    ///   列序本身的问题由启动自检 <see cref="VerifyLayout"/> 权威判定，不靠这颗哨兵。
    /// </summary>
    static readonly Vector2 SentinelOrigin = new(100000f, 100000f);

    readonly MultiMesh _mesh;
    readonly float[] _buffer;
    readonly float[] _teamRgba;
    readonly int _stride;
    readonly int _colorOffset;
    readonly int _customOffset;
    readonly int _instancesPerBall;   // 1 = 只有球本身，>1 表示每颗球后面还跟几个拖尾残影
    readonly int _ghostCount;
    readonly float _trailSpacing;
    readonly float _smallBallAlpha;
    readonly float _mapHalfSize;

    /// <summary>布局自检发现的问题（空 = 通过）。由 <see cref="SelfCheck"/> 汇总打印。</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>布局自检是否通过。不通过时 <see cref="Write"/> 会直接罢工（见那里的注释）。</summary>
    public bool LayoutValid => _problems.Count == 0;

    /// <summary>布局的实测值，报告里记一笔（"步长 16，颜色槽 @8，自定义数据槽 @12"）。</summary>
    public string LayoutSummary { get; private set; } = "未知";

    readonly List<string> _problems = new();

    public BallMeshWriter(MultiMesh mesh, int capacity, int teamCount, BallStyle style, float mapSize)
    {
        _mesh = mesh;
        _ghostCount = Math.Max(0, style.TrailGhosts);
        _trailSpacing = style.TrailSpacing;
        _smallBallAlpha = style.SmallBallAlpha;
        _mapHalfSize = mapSize * 0.5f;
        _instancesPerBall = 1 + _ghostCount;

        // ⚠ 这几项配置**在代码里强制**，不依赖 Scenes/Main.tscn 里的值 ——
        //   编辑器一旦开着，它会用自己的内存副本覆盖整个 .tscn，
        //   而 use_custom_data 被改回别的值时步长就变了、颜色槽位置也跟着变（见类注释）。
        //   功能性的配置写在代码里，美术参数才留在场景里。
        _mesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
        _mesh.UseColors = true;
        _mesh.UseCustomData = true;
        _mesh.InstanceCount = capacity * _instancesPerBall;

        _colorOffset = Transform2DFloats;
        _customOffset = _colorOffset + (_mesh.UseColors ? 4 : 0);
        _stride = _customOffset + (_mesh.UseCustomData ? 4 : 0);
        _buffer = new float[_mesh.InstanceCount * _stride];

        // 预先把每队颜色摊成 float[4]，避免热循环里反复构造 Color
        _teamRgba = new float[(teamCount + 1) * 4];
        for (int team = 1; team <= teamCount; team++)
        {
            Color c = Palette.TeamColor(team);
            _teamRgba[team * 4] = c.R;
            _teamRgba[team * 4 + 1] = c.G;
            _teamRgba[team * 4 + 2] = c.B;
            _teamRgba[team * 4 + 3] = c.A;
        }

        VerifyLayout();
    }
    /// <summary>把当前的 <paramref name="swarm"/> 整块写进 MultiMesh（每帧调一次）。</summary>
    public void Write(BallSwarm swarm)
    {
        // ⚠ 自检没过就**不写**：布局错的时候写出去的是一堆被引擎按别的列序解释的 float，
        //   表现是"整层球一颗都看不见"或"位置全乱"，而且不报任何错。宁可什么都不画 ——
        //   自检已经把原因打在控制台了。这就是这条自检的"牙齿"（只打印就等于没有自检）。
        if (!LayoutValid)
        {
            _mesh.VisibleInstanceCount = 0;
            return;
        }

        int count = swarm.Count;
        _mesh.VisibleInstanceCount = count * _instancesPerBall;   // 实例数固定，只画前 N 个

        if (count == 0)
        {
            return;
        }

        float[] buffer = _buffer;
        float[] rgba = _teamRgba;
        float[] xs = swarm.X;
        float[] ys = swarm.Y;
        byte[] teams = swarm.Team;
        int stride = _stride;
        int colorOffset = _colorOffset;
        int customOffset = _customOffset;
        int ghosts = _ghostCount;
        int perBall = _instancesPerBall;
        float half = _mapHalfSize;
        float spacing = _trailSpacing;

        int stripes = Math.Min(Math.Max(1, System.Environment.ProcessorCount), count);
        int perStripe = (count + stripes - 1) / stripes;

        Parallel.For(0, stripes, stripe =>
        {
            int end = Math.Min(count, (stripe + 1) * perStripe);
            for (int i = stripe * perStripe; i < end; i++)
            {
                float radius = swarm.RadiusOf(i);
                float size = radius * 2f;
                float x = xs[i] - half;   // 网格坐标 → 以地图中心为原点的世界坐标
                float y = ys[i] - half;

                // ⚠ "小球 / 大球"的分界线**只有一处**：能量门槛 BallSwarm.BigBallEnergy。
                //   以前这里还有一个独立的半径旋钮（BigBallRadius），靠注释和它保持同步 ——
                //   一旦有人把它调大（比如 60），能量 4000~11485 的球就会"打起来是大球
                //   （吃维持费、12 倍基地伤害、参与互撞），看起来却是小球（半透明、没有黑核）"。
                //   半径本来就是能量的函数（RadiusForEnergy），所以直接问能量即可。
                int kind = swarm.Energy[i] >= BallSwarm.BigBallEnergy ? KindBig : KindSmall;
                int colorIndex = teams[i] * 4;
                int slot = i * perBall;

                if (ghosts > 0)
                {
                    // 残影沿航向的反方向摆。因为球的运动已经接近直线（TurnRate 很小），
                    // 身后这条直线就是它刚才走过的大致路径，不需要存历史位置。
                    float dirX = MathF.Cos(swarm.HeadingOf(i));
                    float dirY = MathF.Sin(swarm.HeadingOf(i));

                    // 从最远的一个开始写，保证越近的残影画在越上面
                    for (int g = ghosts; g >= 1; g--, slot++)
                    {
                        float shrink = 1f - g * 0.16f;                // 越远越小
                        float alpha = 1f - g / (float)(ghosts + 1);   // 越远越淡
                        WriteInstance(buffer, slot * stride, colorOffset, customOffset,
                            size * shrink, x - dirX * spacing * g, y - dirY * spacing * g,
                            rgba, colorIndex, kind, alpha * _smallBallAlpha);
                    }
                }

                WriteInstance(buffer, slot * stride, colorOffset, customOffset,
                    size, x, y, rgba, colorIndex, kind,
                    kind == KindBig ? 1f : _smallBallAlpha);
            }
        });

        // 每次用这块缓冲之前，先走一遍实例级 setter（理由见 MarkBufferDirty）
        MarkBufferDirty();
        _mesh.Buffer = buffer;
    }

    /// <summary>
    /// 整块写 Buffer 之前先调一次实例级 setter。它承担两件事：
    ///   ① 触发引擎的 `_multimesh_make_local()` + `_multimesh_mark_dirty()`
    ///      （`mesh_storage.cpp:1908 → :1711/:1781`），让实例缓冲走上"dirty 区"那条兜底路径，
    ///      而不是只依赖 `_multimesh_set_buffer` 的裸 `buffer_update`（`:2097`）；
    ///   ② 作为列序 / 步长的契约探针，配合 <see cref="VerifyLayout"/> 使用。
    ///
    /// ⚠ setter **不是**"缓冲能上传"的前提：参考项目用 GPU 缓冲回读做过对照实验，
    ///   跳过 setter 直接写 Buffer 依然成功上传。这里调它只为上面那两件事。
    ///
    /// 代价可以忽略：只有 2 次托管↔引擎调用（0 号哨兵 + 1 号退化），
    /// 和"逐实例 setter"是两回事 —— 后者对每颗球都要两次调用。
    /// </summary>
    void MarkBufferDirty()
    {
        _mesh.SetInstanceTransform2D(0, new Transform2D(0f, SentinelOrigin));

        // 1 号退化：全 0 变换 = scale 0，永不参与绘制。
        if (_mesh.InstanceCount > 1)
        {
            _mesh.SetInstanceTransform2D(1, default);
        }
    }

    /// <summary>
    /// 启动自检：在**用手写缓冲之前**，先用引擎自带的 setter 写一行、回读、逐 float 比对，
    /// 确认代码里假设的「行/列序 + 步长 + 各通道偏移」和引擎真实布局一致。
    ///
    /// 这条做法来自 ComputeShaderBattleSimulation 项目的 `CpuMultiMeshInit.cs`，
    /// 那里把它称为"列序 / stride 契约探针"，并写明：**行/列混淆是静默 bug** ——
    /// 引擎不报任何错，只是画出来不对。本项目正好栽过两次：
    ///   ① `use_custom_data` 打开后步长从 12 变 16，而颜色槽用"步长 − 4"去推，
    ///      结果写到自定义数据槽上，实例颜色全零、**整层球一颗都看不见**、不报错；
    ///   ② 颜色槽偏移算错，同样是静默的。
    /// 有了这个自检，这类错误启动时就报出来，不用靠截图去猜。
    ///
    /// 探测写在最后一个实例槽位上：那个槽位永远不在 VisibleInstanceCount 范围内、不会被画，
    /// 而且每帧整块重写缓冲时会被覆盖掉，不影响真实数据。
    /// </summary>
    void VerifyLayout()
    {
        const float OriginX = 11f, OriginY = 22f;
        const float ColorR = 0.11f, ColorG = 0.22f, ColorB = 0.33f, ColorA = 0.44f;
        const float CustomX = 0.55f;

        int probe = _mesh.InstanceCount - 1;
        if (probe < 0 || (probe + 1) * _stride > _mesh.Buffer.Length)
        {
            _problems.Add($"缓冲布局：槽位越界 probe={probe} stride={_stride} len={_mesh.Buffer.Length}");
            return;
        }

        _mesh.SetInstanceTransform2D(probe, new Transform2D(0f, new Vector2(OriginX, OriginY)));
        _mesh.SetInstanceColor(probe, new Color(ColorR, ColorG, ColorB, ColorA));
        _mesh.SetInstanceCustomData(probe, new Color(CustomX, 0f, 0f, 0f));

        float[] buf = _mesh.Buffer;
        int o = probe * _stride;

        bool ok = true;
        ok &= Check(buf, o + 3, OriginX, "Transform2D.origin.x @ 3");
        ok &= Check(buf, o + 7, OriginY, "Transform2D.origin.y @ 7");
        ok &= Check(buf, o + _colorOffset + 0, ColorR, $"颜色.r @ {_colorOffset}");
        ok &= Check(buf, o + _colorOffset + 1, ColorG, $"颜色.g @ {_colorOffset + 1}");
        ok &= Check(buf, o + _customOffset + 0, CustomX, $"自定义数据.x @ {_customOffset}");

        if (ok && _stride != 16)
        {
            _problems.Add($"缓冲布局：步长 {_stride}，但 Transform2D + 颜色 + 自定义数据应当是 16");
            ok = false;
        }

        if (!ok)
        {
            _problems.Add("缓冲布局：手写缓冲和引擎不一致，渲染会是静默错误的（整层球可能一颗都看不见），先修布局再用");
            return;
        }

        LayoutSummary = $"步长 {_stride}，颜色槽 @{_colorOffset}，自定义数据槽 @{_customOffset}";
    }

    bool Check(float[] buffer, int index, float expected, string what)
    {
        if (MathF.Abs(buffer[index] - expected) < 1e-4f)
        {
            return true;
        }

        _problems.Add($"缓冲布局：{what} 读到 {buffer[index]}，期望 {expected}（偏移算错了）");
        return false;
    }

    /// <summary>
    /// 写一个实例。这里就是 `Disc.gdshader` 读的那套编码：
    /// 颜色 rgb = 阵营色（所有形态共用，所以不能拿"rgb 是否为零"当标记），
    /// 颜色 a = 透明度 / 淡出，自定义 **x = 形态**（kind）。
    ///
    /// ⚠ 自定义数据的 y/z/w 目前**没有任何着色器读**（`Disc.gdshader` 只取 `INSTANCE_CUSTOM.x`）——
    ///   以前 y 通道传的是"归一化能量"，驱动一个早就删掉的"大球内核漩涡"效果，CPU 每帧白算。
    ///   这里仍然显式写 0（缓冲是整块重写的，不写会留下上一帧的垃圾值）；
    ///   要往这几个通道加东西时，先确认着色器真的读它。
    /// </summary>
    static void WriteInstance(float[] buffer, int o, int colorOffset, int customOffset,
        float size, float x, float y, float[] rgba, int colorIndex, int kind, float alpha)
    {
        // Transform2D 占前 8 个 float：两行 (xx, xy, 0, ox) / (yx, yy, 0, oy)
        buffer[o] = size; buffer[o + 1] = 0f; buffer[o + 2] = 0f; buffer[o + 3] = x;
        buffer[o + 4] = 0f; buffer[o + 5] = size; buffer[o + 6] = 0f; buffer[o + 7] = y;

        // 颜色紧跟变换（偏移累加得出，不是"步长 - 4"）
        buffer[o + colorOffset] = rgba[colorIndex];
        buffer[o + colorOffset + 1] = rgba[colorIndex + 1];
        buffer[o + colorOffset + 2] = rgba[colorIndex + 2];
        buffer[o + colorOffset + 3] = alpha;

        // 自定义数据：x = 形态，y/z/w 保留为 0
        buffer[o + customOffset] = kind;
        buffer[o + customOffset + 1] = 0f;
        buffer[o + customOffset + 2] = 0f;
        buffer[o + customOffset + 3] = 0f;
    }
}

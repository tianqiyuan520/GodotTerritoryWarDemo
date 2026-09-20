using System;
using System.Globalization;
using Godot;

namespace TerritoryWar.Game;

/// <summary>
/// 大球上的能量数字 —— 世界空间的 Node2D。**数字是场景里的 `Label` 节点**
/// （`Scenes/Main.tscn` 的 `EnergyLabels/Num1..Num24`）：字号、描边、宽高全在场景里配，
/// 网格结构一目了然；本脚本只负责把「哪几个数字、写什么、什么颜色、放在哪」写进去，
/// 一个节点都不建。
///
/// 数据由 <c>GameRoot</c> 每 4 帧推一次（<see cref="Present"/>）：挑能量最高的若干颗球。
/// 阈值必须高于大球门槛，否则会出现"一串数字挂在小点上"（见 <c>GameRoot.EnergyLabelMinEnergy</c>）。
/// </summary>
public partial class EnergyLabels : Node2D
{
    /// <summary>场景里最多准备了几个数字槽（`Num1..Num24`）。</summary>
    const int MaxSlots = 24;

    readonly Label[] _slots = new Label[MaxSlots];

    /// <summary>当前每个槽位挂着哪颗球（<see cref="FollowPositions"/> 靠它每帧跟位置）。</summary>
    readonly int[] _shownBall = new int[MaxSlots];
    int _shownCount;

    /// <summary>场景里实际作者化了几个槽位（槽位连续编号，遇到第一个空的就停）。</summary>
    public int Capacity { get; private set; }

    public override void _Ready()
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            _slots[i] = GetNodeOrNull<Label>($"Num{i + 1}");
            if (_slots[i] == null)
            {
                break;
            }

            _slots[i].Visible = false;
            Capacity = i + 1;
        }
    }

    /// <summary>
    /// 推一次数据：挑出能量最高的 <paramref name="maxCount"/> 颗球（且能量 ≥ <paramref name="minEnergy"/>），
    /// 把数字挂到它们身上；其余的槽位隐藏。传 0 就是全隐藏。
    /// </summary>
    /// <param name="mapSize">地图边长（球坐标是以地图中心为原点的世界坐标，要减掉半图）。</param>
    public void Present(BallSwarm swarm, int maxCount, float minEnergy, float mapSize)
    {
        HideAll();

        int slotCount = Math.Clamp(maxCount, 0, Capacity);
        if (slotCount == 0 || swarm.Count == 0)
        {
            return;
        }
        // 选取用的是"大小为 N 的插入排序"，而不是全排序：球有上千颗、N 只有几十，
        // 维护一个小数组顺带记录当前门槛，一趟就能拿到前 N 名。
        // 队首最大、队尾最小；floor 是"当前第 N 名的能量"，比它低的直接跳过。
        Span<int> picked = stackalloc int[slotCount];
        Span<float> pickedEnergy = stackalloc float[slotCount];
        int found = 0;
        float floor = minEnergy;

        for (int i = 0; i < swarm.Count; i++)
        {
            float energy = swarm.Energy[i];
            if (energy < floor)
            {
                continue;
            }

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
                floor = MathF.Max(minEnergy, pickedEnergy[slotCount - 1]);
            }
        }

        // 网格坐标 → 以地图中心为原点的世界坐标
        float half = mapSize * 0.5f;
        _shownCount = found;
        for (int k = 0; k < found; k++)
        {
            int ball = picked[k];
            _shownBall[k] = ball;
            Write(
                _slots[k],
                new Vector2(swarm.X[ball] - half, swarm.Y[ball] - half),
                FormatEnergy(pickedEnergy[k]),
                Palette.TeamColor(swarm.Team[ball]));
        }
    }

    /// <summary>
    /// 让已经挂上的数字**每帧**跟着球走（<c>GameRoot._Process</c> 每帧调一次）。
    ///
    /// ⚠ 为什么位置不能一起节流：文本是慢变量（选 Top-N、拼字符串），位置是快变量
    ///   （球每步都在动）。两件事捆在一个节流里时，数字会落在球的后面（屏幕刷新率越低越明显）。
    ///   这里只改 `Position`，不碰 Text / Modulate——那两样的开销才需要节流。
    /// </summary>
    public void FollowPositions(BallSwarm swarm, float mapSize)
    {
        float half = mapSize * 0.5f;

        for (int k = 0; k < _shownCount; k++)
        {
            int ball = _shownBall[k];

            // 两次 Present 之间球可能已经死了/被收尸顶掉 —— 这时先把槽位藏起来，
            // 等下一次 Present 重新分配（不要把数字挂到"换了个球"的位置上）
            if (ball >= swarm.Count)
            {
                _slots[k].Visible = false;
                continue;
            }

            _slots[k].Position =
                new Vector2(swarm.X[ball] - half, swarm.Y[ball] - half) - _slots[k].Size * 0.5f;
        }
    }

    void HideAll()
    {
        _shownCount = 0;

        for (int i = 0; i < Capacity; i++)
        {
            _slots[i].Visible = false;
        }
    }

    static void Write(Label slot, Vector2 center, string text, Color color)
    {
        slot.Text = text;
        slot.Modulate = color;
        slot.Visible = true;

        // Label 的 Position 是左上角，这里要的是"数字中心落在球心上"
        slot.Position = center - slot.Size * 0.5f;
    }

    /// <summary>
    /// 能量数字的显示格式：千进位用 K、百万用 M，最多一位小数。
    ///   999 → "999"    6198 → "6.2K"    80000 → "80K"    1_500_000 → "1.5M"
    ///
    /// 用 `"0.#"` 而不是 `"0.0"`：整数时不会拖一个没意义的 ".0"（`80K` 比 `80.0K` 干净）。
    /// 显式指定不变文化 —— 小数点分隔符不该随系统区域设置变
    /// （项目也开了 `InvariantGlobalization`，这里只是把意图写清楚）。
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
}

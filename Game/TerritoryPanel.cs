using System;
using System.Collections.Generic;
using Godot;

namespace TerritoryWar.Game;

/// <summary>
/// 右侧「领土占领」面板 —— 世界空间的 Node2D。
///
/// **每一行的部件、每一行文字都是场景里的节点**（`Scenes/Main.tscn` 的 `Territory` 下面）：
/// `Row1..N` 各带 `Swatch`（色块）、`Track`（底槽）、`Fill`（前景条）三个 `Polygon2D`，
/// 以及 `Name` / `Percent` 两个 `Label`。底槽的宽度决定条形图的满格宽度 ——
/// 在编辑器里把底槽拉宽，条和文字一起跟着变。
///
/// 行序**按势力号固定，不按占比排序** —— 排序会让每行上下乱跳（两家接近时尤其明显）。
/// 名次只做文字前缀，位置稳定、信息不丢。
///
/// 为什么要有它（而不是继续用一行文字）：只列前几名的文字**看不到"已出局"**；
/// 而占比是每 0.2 秒都在跳的数字，条状图能一眼看出趋势与差距，不必去读数字。
/// </summary>
public partial class TerritoryPanel : Node2D
{
    /// <summary>场景里最多准备了几行（按 `Row1..RowN` 连续编号读取）。</summary>
    const int MaxRows = 8;

    /// <summary>出局的势力使用的灰色 —— 阵营色不该再出现在这一行。</summary>
    static readonly Color OutOfMatch = new(0.34f, 0.34f, 0.37f);

    readonly Polygon2D[] _swatch = new Polygon2D[MaxRows];
    readonly Polygon2D[] _track = new Polygon2D[MaxRows];
    readonly Polygon2D[] _fill = new Polygon2D[MaxRows];
    readonly Label[] _name = new Label[MaxRows];
    readonly Label[] _percent = new Label[MaxRows];
    int _rows;

    /// <summary>面板尺寸：由最宽的那条底槽决定（在编辑器里把底槽拉宽，面板就变宽）。</summary>
    Vector2 _panelSize = new(260f, 264f);

    /// <summary>场景里实际作者化了几行（<c>Row1..RowN</c> 连续）。势力数超过它时会有一行写不下。</summary>
    public int RowCount => _rows;

    /// <summary>绑定场景时发现的问题（缺 <c>RowN</c> 或它下面的零件）。由 <see cref="SelfCheck"/> 汇总打印。</summary>
    public IReadOnlyList<string> Problems => _problems;

    readonly List<string> _problems = new();

    /// <summary>这块面板在世界坐标里占的矩形（摄像机用它来算"三块内容要全部装下"的缩放）。</summary>
    public Rect2 WorldRect => new(GlobalPosition, _panelSize);

    public override void _Ready() => BindSceneNodes();

    /// <summary>认领各行节点并读出几何（面板尺寸、底槽宽度、文字宽度）。</summary>
    void BindSceneNodes()
    {
        float widest = 0f;
        float lowest = 0f;

        for (int i = 0; i < MaxRows; i++)
        {
            if (GetNodeOrNull<Node2D>($"Row{i + 1}") is not { } row)
            {
                break;
            }

            _swatch[i] = row.GetNodeOrNull<Polygon2D>("Swatch");
            _track[i] = row.GetNodeOrNull<Polygon2D>("Track");
            _fill[i] = row.GetNodeOrNull<Polygon2D>("Fill");
            _name[i] = row.GetNodeOrNull<Label>("Name");
            _percent[i] = row.GetNodeOrNull<Label>("Percent");
            _rows++;

            // 零件缺失只影响这一行的某一列，不会崩 —— 正因为"不会崩"，必须在启动时说一声，
            // 否则改名之后只是"这一行不显示色块/条/名字"，没人会发现
            if (_swatch[i] == null) _problems.Add($"领土面板：Row{i + 1} 下缺少 Swatch（色块）");
            if (_track[i] == null) _problems.Add($"领土面板：Row{i + 1} 下缺少 Track（底槽）");
            if (_fill[i] == null) _problems.Add($"领土面板：Row{i + 1} 下缺少 Fill（前景条）");
            if (_name[i] == null) _problems.Add($"领土面板：Row{i + 1} 下缺少 Name（势力名 Label）");
            if (_percent[i] == null) _problems.Add($"领土面板：Row{i + 1} 下缺少 Percent（占比 Label）");

            if (_track[i] == null)
            {
                continue;
            }

            widest = MathF.Max(widest, _track[i].Position.X + _track[i].Scale.X);
            lowest = MathF.Max(lowest, _track[i].Position.Y + _track[i].Scale.Y);

            // 两个 Label 的宽度跟着底槽走（文字对齐由 Label 自己的 horizontal_alignment 负责）
            FitLabel(_name[i], _track[i].Scale.X);
            FitLabel(_percent[i], _track[i].Scale.X);
        }

        _panelSize = new Vector2(MathF.Max(1f, widest), lowest + 12f);
    }

    static void FitLabel(Label label, float width)
    {
        if (label != null)
        {
            label.Size = new Vector2(width, label.Size.Y);
        }
    }

    /// <summary>
    /// 刷一次内容（<c>GameRoot</c> 每 0.2 秒调一遍）。
    ///
    /// 格数直接用 <see cref="TerritoryMap.CellsByTeam"/> 里的现成数字（在 Claim/Attack 里增量维护，
    /// 不扫全图），所以频率再高也不贵，但没必要：占比重排到屏幕上人手也跟不上。
    /// </summary>
    public void UpdateFrom(TerritoryMap map, Team[] teams, int teamCount)
    {
        int total = Math.Max(1, map.Width * map.Height);
        int[] cells = map.CellsByTeam;

        // 名次：按领土格数排（只算还活着的），然后当作文字前缀 —— 不挪动行的位置。
        Span<int> rank = stackalloc int[teamCount + 1];
        for (int team = 1; team <= teamCount; team++)
        {
            rank[team] = 1;
            for (int other = 1; other <= teamCount; other++)
            {
                if (other != team && teams[other].Alive
                    && (!teams[team].Alive || cells[other] > cells[team]))
                {
                    rank[team]++;
                }
            }
        }

        for (int team = 1; team <= teamCount; team++)
        {
            bool alive = teams[team].Alive;
            float share = cells[team] / (float)total;

            SetRow(team - 1,
                alive ? $"{rank[team]}. 势力{team}" : $"— 势力{team} 出局",
                $"{share * 100f:F1}%",
                share,
                alive ? Palette.TeamColor(team) : OutOfMatch);
        }
    }

    /// <summary>写一行的内容：名字、百分比、色块与前景条（条长 = 底槽宽 × 占比）。</summary>
    void SetRow(int slot, string name, string percent, float share, Color color)
    {
        if (slot < 0 || slot >= _rows)
        {
            return;
        }

        if (_name[slot] != null)
        {
            _name[slot].Text = name;
        }

        if (_percent[slot] != null)
        {
            _percent[slot].Text = percent;
        }

        if (_swatch[slot] != null)
        {
            _swatch[slot].Color = color;
        }

        if (_fill[slot] != null && _track[slot] != null)
        {
            _fill[slot].Color = color;
            _fill[slot].Scale = new Vector2(
                _track[slot].Scale.X * Math.Clamp(share, 0f, 1f), _fill[slot].Scale.Y);
        }
    }
}

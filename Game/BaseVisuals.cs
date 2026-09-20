using System;
using System.Collections.Generic;
using Godot;

namespace TerritoryWar.Game;

/// <summary>
/// 四个基地（"炮塔"）的外观 —— 认领场景里的 `Bases/Base1..4`
/// （`MeshInstance2D` + `Shaders/Base.gdshader`），把玩法状态写进各自的材质 uniform。
///
/// 它同时是"基地在哪、多大"的唯一来源：<c>GameRoot.StartMatch</c> 从这里的节点读世界坐标当基地坐标，
/// 所以**在编辑器里拖 `Base1..4` 就等于改基地位置、改它们的 `scale` 就等于改基地大小**。
///
/// 写进去的东西：
///   · `Modulate` = 阵营色（出局 → 灰掉）—— 着色器读的就是 `COLOR`
///   · `shield`   = 护盾剩余时间比例 —— 罩上六边形能量球面（越少越淡）
///   · `health`   = 血量比例 —— 低血量时核心变暗并闪烁
///   · `charge`   = 距下一次"大球"出膛的进度 —— 蓄力脉冲
///   · `impact_pos` / `impact_age` = 最近一次被打到的位置与时间 —— 扩散涟漪
///   · `time`     = 模拟时间（所以暂停 / 倍速时护盾与涟漪动画跟着停 / 加速）
/// </summary>
public sealed class BaseVisuals
{
    /// <summary>出局后的染色。</summary>
    static readonly Color EliminatedTint = new(0.25f, 0.25f, 0.25f, 0.85f);

    /// <summary>涟漪计时超过它就不画了（着色器里 >0.9 就没有涟漪）。</summary>
    const float ImpactGone = 99f;

    readonly Node2D[] _nodes;
    readonly ShaderMaterial[] _materials;

    /// <summary>最近一次受击的 UV 位置与"距上次受击过了多久"，都按势力号索引（[0] 不用）。</summary>
    readonly Vector2[] _impactPos;
    readonly float[] _impactAge;

    readonly float _hitPoints;

    /// <summary>绑定场景时发现的问题（缺节点 / 材质类型不对）。由 <see cref="SelfCheck"/> 汇总打印。</summary>
    public IReadOnlyList<string> Problems => _problems;

    readonly List<string> _problems = new();

    public BaseVisuals(Node2D root, int teamCount, float hitPoints)
    {
        _hitPoints = MathF.Max(1f, hitPoints);
        _nodes = new Node2D[teamCount];
        _materials = new ShaderMaterial[teamCount];
        _impactPos = new Vector2[teamCount + 1];
        _impactAge = new float[teamCount + 1];

        for (int i = 0; i < teamCount; i++)
        {
            _impactAge[i + 1] = ImpactGone;
            _impactPos[i + 1] = new Vector2(0.5f, 0.5f);

            _nodes[i] = root?.GetNodeOrNull<Node2D>($"Base{i + 1}");
            if (_nodes[i] == null)
            {
                _problems.Add($"基地：场景里找不到 Bases/Base{i + 1}，势力{i + 1} 的基地不会显示、也不会被弹珠打到");
                continue;
            }

            // 采样过滤钉死 nearest：这些是"纯色圆"，线性插值会让边缘发虚。
            _nodes[i].TextureFilter = CanvasItem.TextureFilterEnum.Nearest;

            // ⚠ 4 个基地共用同一个 ShaderMaterial 资源的话，护盾 / 血量 / 涟漪会互相覆盖 ——
            //   所以每个基地拿一份副本（Duplicate 的是资源，不是节点）。
            if (_nodes[i].Material is ShaderMaterial shared)
            {
                _materials[i] = (ShaderMaterial)shared.Duplicate();
                _nodes[i].Material = _materials[i];
            }
            else
            {
                _problems.Add($"基地：Base{i + 1} 上没有 ShaderMaterial，护盾 / 血量 / 蓄力不会显示");
            }
        }
    }

    /// <summary>
    /// 一局级复位：把受击涟漪清掉（<c>GameRoot.ResetRoundComponents</c> 在开局时调）。
    ///
    /// ⚠ 不清的话：上一局最后一击留下的 <c>impact_age = 0</c> 会**冻在那里** ——
    ///   已出局势力的涟漪计时不再推进、分胜负后模拟也停了，而 <see cref="Update"/> 每帧照写，
    ///   于是新局开局会在上一局最后挨打的位置闪一圈亮环。
    /// </summary>
    public void ResetRound()
    {
        for (int i = 1; i < _impactAge.Length; i++)
        {
            _impactAge[i] = ImpactGone;
            _impactPos[i] = new Vector2(0.5f, 0.5f);
        }
    }

    /// <summary>
    /// 读某座基地的世界坐标。**这是基地坐标的唯一来源**，取不到时返回 false
    /// （调用方退回"按角落摆"的兜底位置）。
    /// </summary>
    public bool TryGetCenter(int team, out Vector2 center)
    {
        Node2D node = NodeOf(team);
        center = node?.GlobalPosition ?? Vector2.Zero;
        return node != null;
    }

    /// <summary>
    /// 记一次受击：把弹珠的世界坐标换算成基地四边形的 UV
    /// （基地四边形以节点为圆心、边长 = 节点 scale），着色器从这个点往外画一圈扩散的亮环。
    /// </summary>
    public void RegisterHit(int team, float worldX, float worldY)
    {
        Node2D node = NodeOf(team);
        if (node == null)
        {
            return;
        }

        Vector2 offset = new Vector2(worldX, worldY) - node.GlobalPosition;
        Vector2 size = node.Scale;
        _impactPos[team] = new Vector2(
            0.5f + (size.X > 0.001f ? offset.X / size.X : 0f),
            0.5f + (size.Y > 0.001f ? offset.Y / size.Y : 0f));
        _impactAge[team] = 0f;
    }

    /// <summary>
    /// 受击涟漪的计时。⚠ 它跟的是**模拟时间**（由 <c>GameRoot.Simulate</c> 推），
    /// 而且只对还活着的势力推进 —— 出局那一刻的涟漪就停在原地。
    /// </summary>
    public void AdvanceImpactAge(int team, float dt)
    {
        if (_impactAge[team] < 9f)
        {
            _impactAge[team] += dt;
        }
    }

    /// <summary>每帧把玩法状态写进材质（<c>GameRoot._Process</c> 调）。</summary>
    public void Update(float elapsed, Team[] teams, float shieldSeconds, float bigShotInterval)
    {
        float shieldSpan = MathF.Max(0.01f, shieldSeconds);

        for (int i = 0; i < _nodes.Length; i++)
        {
            Node2D node = _nodes[i];
            ShaderMaterial material = _materials[i];
            if (node == null || material == null)
            {
                continue;
            }

            Team t = teams[i + 1];
            node.Modulate = t.Alive ? Palette.TeamColor(t.Id) : EliminatedTint;

            material.SetShaderParameter("time", elapsed);
            material.SetShaderParameter("shield", t.Alive ? Math.Clamp(t.ShieldTimer / shieldSpan, 0f, 1f) : 0f);
            material.SetShaderParameter("health", Math.Clamp(t.Health / _hitPoints, 0f, 1f));
            material.SetShaderParameter("charge", t.Alive && bigShotInterval > 0f
                ? Math.Clamp(1f - t.BigShotTimer / bigShotInterval, 0f, 1f)
                : 0f);
            material.SetShaderParameter("impact_pos", _impactPos[t.Id]);
            material.SetShaderParameter("impact_age", _impactAge[t.Id]);
        }
    }

    Node2D NodeOf(int team) => team >= 1 && team <= _nodes.Length ? _nodes[team - 1] : null;
}

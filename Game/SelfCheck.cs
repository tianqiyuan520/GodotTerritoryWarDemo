using System.Collections.Generic;
using Godot;

namespace TerritoryWar.Game;

/// <summary>
/// 启动自检：把**只写在注释里、靠人记住**的约定变成可执行的断言。
///
/// 为什么需要它 —— 这个项目里大量约束是跨文件、跨资源的：
///   · 旋钮与常量之间：能量数字阈值必须高于大球门槛、涂色盘上限必须 ≥ 视觉半径上限……
///   · 代码与场景之间：`Bases/Base1..4`、`Pegs/Peg1..11`、`PassGates/GateX2` 这些**名字就是契约**
///   · 代码与着色器之间：MultiMesh 的步长/偏移、实例自定义数据的 kind 编码
/// 这类约束破掉之后，症状通常是"**静默地不对**"或"跑一会儿才崩"——
/// 缓冲布局那次就是整层球一颗都看不见、却一个错都不报（见 `BallMeshWriter.VerifyLayout`）。
///
/// 所以约定必须自己能说话，而且要**在一处**说清楚：以前每个类各自 `GD.PrintErr`，
/// 既没人汇总也容易被忽略；现在各组件把"我缺什么"报上来，由这里一次性裁决并打印。
///
/// 能安全修正的（例如势力数超过调色板容量会越界崩）由调用方夹取后再记一条说明。
/// </summary>
public sealed class SelfCheck
{
    readonly List<string> _problems = new();
    readonly List<string> _notes = new();
    int _checks;

    /// <summary>记一条检查。无论通过与否都计数，报告里能看到"一共查了多少项"。</summary>
    public void Check(bool ok, string what, string detailWhenBad)
    {
        _checks++;
        if (!ok)
        {
            _problems.Add($"✗ {what} —— {detailWhenBad}");
        }
    }

    /// <summary>通过也值得记一笔的检查（例如"缓冲布局的实际数值"）。</summary>
    public void Note(string what, string detail)
    {
        _checks++;
        _notes.Add($"· {what}：{detail}");
    }

    /// <summary>把组件自报的问题（"我缺哪个节点/哪个门"）并进来。</summary>
    public void AddRange(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        _checks++;
        foreach (string p in problems)
        {
            _problems.Add($"✗ {p}");
        }
    }

    /// <summary>
    /// 一次性打印。通过时只报一句（免得刷屏），有问题时逐条列出来；返回是否全部通过。
    ///
    /// ⚠ 措辞上刻意强调"不会崩、是静默失效" —— 这个项目的坑几乎都是这一类，
    ///   看到一条 ✗ 就应该当成"规则没生效"来读，而不是"游戏坏了"。
    /// </summary>
    public bool Report()
    {
        if (_problems.Count == 0)
        {
            GD.Print($"[自检] 通过 {_checks} 项（场景绑定 / 缓冲与着色器契约 / 跨文件数值约定 / 领土规则真值表）");
            foreach (string note in _notes)
            {
                GD.Print($"[自检] {note}");
            }

            return true;
        }

        GD.PrintErr($"[自检] {_problems.Count} 项不满足（共检查 {_checks} 项）。"
            + "这类问题一般不崩，而是静默地不按规则跑 —— 按下面逐条修：");
        foreach (string problem in _problems)
        {
            GD.PrintErr($"[自检] {problem}");
        }

        foreach (string note in _notes)
        {
            GD.PrintErr($"[自检] {note}");
        }

        return false;
    }
}

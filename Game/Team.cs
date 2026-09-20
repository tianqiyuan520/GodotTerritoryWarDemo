using System;

namespace TerritoryWar.Game;

/// <summary>
/// 一支势力：角落的一个炮塔基地 + 一个能量池。
///
/// 循环：领土 → 按面积收入能量 → 炮塔摆动着开火，把能量打成一颗颗弹珠 → 弹珠出去抢领土。
/// 基地被打掉（血量归零）就出局，它的地盘全部变回中立。
/// </summary>
public sealed class Team
{
    public readonly byte Id;
    public readonly float BaseX;
    public readonly float BaseY;

    /// <summary>炮口摆动时围绕的中心角度，也就是"朝向地图中心"。</summary>
    public readonly float BaseAngle;

    public float Health;
    public float EnergyPool;   // 攒着用来发射弹珠
    public bool Alive = true;

    /// <summary>炮口当前角度（弧度）。每步由摆动算出来，渲染和发射都读它。</summary>
    public float TurretAngle;

    /// <summary>摆动相位偏移。让各家炮塔不同步，否则四角会齐射，画面很假。</summary>
    public float SwingPhase;

    /// <summary>距离下一次开火还剩多久。开局随机错开，避免同时开火。</summary>
    public float FireTimer;

    /// <summary>距离下一次"蓄力射击"（打大球）还剩多久。</summary>
    public float BigShotTimer;

    /// <summary>
    /// 下一轮齐射的球数倍率（左侧钉板的"×2/×4/×8"与转盘门穿过时会累乘，打出后清零）。
    /// 它只影响**下一轮**齐射，所以是"抽到就赚一发"，而不是永久增益。
    /// </summary>
    public int PendingMultiplier = 1;

    /// <summary>
    /// 基地护盾剩余时间（秒）。落在钉板"护盾"格上时被点亮，期间受到的伤害大幅衰减。
    /// 这是底排 6 个功能格里唯一一个"防守向"的效果 —— 其余几格都是往外打（对应参考作品的
    /// 扇形/烟花/弹药/护盾/大球/弹幕）。
    /// </summary>
    public float ShieldTimer;


    public Team(byte id, float baseX, float baseY, float baseHealth, float baseAngle, float swingPhase, float fireTimer, float bigShotTimer)
    {
        Id = id;
        BaseX = baseX;
        BaseY = baseY;
        BaseAngle = baseAngle;
        SwingPhase = swingPhase;
        FireTimer = fireTimer;
        BigShotTimer = bigShotTimer;
        TurretAngle = baseAngle;
        Health = baseHealth;
    }

    /// <summary>基地半径（只用于判定被弹珠碰到）。</summary>
    public const float BaseRadius = 14f;

    /// <summary>炮管长度（画出来是根细长条，从基地中心向外伸）。</summary>
    public const float BarrelLength = 26f;

    public void TakeDamage(float amount)
    {
        Health = Math.Max(0, Health - amount);
        if (Health <= 0)
        {
            Alive = false;
        }
    }
}

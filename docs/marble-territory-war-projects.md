# 弹珠领土战争 —— 资料与项目调研

> 本文是 [research-territory-war.md](./research-territory-war.md) 的补充：那份文档解决"这是什么玩法、Godot 怎么实现"；
> 这一份解决"**已经有哪些资料和项目可以直接研究**"，并且从开源实现里**抠出了可用的核心算法与数值**。
>
> 检索方式：`anysearch` skill（`batch_search` / `extract`）+ GitHub Search / Contents API（直接读源码与 README）。

---

## 1. 一句话结论

| 结论 | 说明 |
| --- | --- |
| 题材谱系清楚 | **Carson Jay Marbles《Territory Wars - The Original》(2018, 56 万播放)** → **MIKAN《Multiply or Release》(2021, Algodoo)** → 全球模仿者 → 中文《球球领土战争/百万像素》 |
| 有 **C# 参考实现** | [wushupei/TerritorialWar](https://github.com/wushupei/TerritorialWar)（Unity C#，模块极简，最适合当骨架）；[k0laa/TerritoryWars-Unity](https://github.com/k0laa/TerritoryWars-Unity)（**MIT**，Unity+Photon 联机） |
| 有 **完整规则实现**可以照读 | [wan300/Four_Color_Marbles_Territory_War](https://github.com/wan300/Four_Color_Marbles_Territory_War)（TS，中文，四色 + 5 种武器 + 侵蚀模型，是开源里**规则最完整**的一个） |
| 可安全复用代码的只有少数 | Apache-2.0：maybe-raven；MIT：Ryan4G、k0laa、Zylann、MechanicalFlower。**其余多个项目没有 LICENSE＝保留所有权利，只能读不能抄** |
| **Godot 版该题材目前没有开源实现** | 搜遍 GitHub 只有 Godot 的"弹珠竞速"（非领土），没有"弹珠领土战争"。做出来是填补空白，但**没有 Godot 先例可抄，得自己趟** |
| 规模真相 | 开源圈最大也就 **256×256（6.5 万格）**，社区"Giant"版是 **100×100（1 万格）**。中文"百万像素"的 **1000×1000（100 万格）是整个圈子的 4~200 倍** |

---

## 2. 题材谱系（时间线）

| 时间 | 事件 | 来源 |
| --- | --- | --- |
| 2018 | **Carson Jay Marbles《Territory Wars - (The Original)》** —— "每颗弹珠必须一边防守自己的领土一边进攻别人的领土，基地被摧毁就出局" | [YouTube](https://www.youtube.com/watch?v=LOOflhF1Bjk)（56 万播放，7 年前） |
| 2021-07-15 | **MIKAN《Multiply or Release》**（Algodoo 首发）。战场自述"partly inspired by **Carson Jay Marbles's Territory Wars**"，基地初始各 1 颗弹珠，2 个基础 ×2 板 | [Algodoo Wiki](https://algodoo.fandom.com/wiki/Multiply_or_Release) |
| 2021 起 | 大量模仿者：Algodoo / Unity / Physion / Bevy / JS / Scratch | [Physion 综述](https://physion.net/blog/multiply-or-release-in-physion) |
| ~2022 | Carson Jay Marbles《**Giant** Multiply or Release - **100×100 Grid**》34 万播放 | [YouTube](https://www.youtube.com/watch?v=vPFzFfHe6hM) |
| 2021-09 | [Ryan4G/phaser3-typescript-marblerace](https://github.com/Ryan4G/phaser3-typescript-marblerace) 创建（README 明确写"It's also called '**Territory Wars**' on the BiliBili"） | GitHub API |
| 2022-08 | [wushupei/TerritorialWar](https://github.com/wushupei/TerritorialWar)（Unity C#） | GitHub API |
| 2023-07 | B 站《**[Unity自制] 领土战争 -- 经典领土争夺**》——"小球碰到绿色区域子弹翻倍、蓝色区域 ×3、红色为发射区域"，**制作资源包共享在评论区** | [B站](https://www.bilibili.com/video/BV18N41127kd/) |
| 2025-08 起 | 中文**《球球领土战争 / 百万像素》**系列（MarblePi 等，Unity），**1000×1000 = 100 万格** | [B站](https://www.bilibili.com/video/BV1xFetzdEYw/) |
| 2026 | [wan300/Four_Color_Marbles_Territory_War](https://github.com/wan300/Four_Color_Marbles_Territory_War)（TS 复刻，仍在更新） | GitHub API（pushed 2026-08） |

中文创作社区主要在 **共创世界 ccw.site**（Scratch/Turbowarp），例如"【四潜】领土战争-终极大战！- 子聪Marbles × 10⁸"（[ccw.site](https://www.ccw.site/)）。

---

## 3. 开源项目总表

按"对我们的价值"排序：

| 项目 | 语言 | ★ | License | 规模/形态 | 能提供什么 |
| --- | --- | --- | --- | --- | --- |
| [wan300/Four_Color_Marbles_Territory_War](https://github.com/wan300/Four_Color_Marbles_Territory_War) | TypeScript | 0 | **无（保留所有权利）** | 领地 **256×256**，4 队，5 武器 | ⭐**规则最完整**：Plinko 闸口/倍率槽、炮塔 360° 扫射、子弹反弹、**领地侵蚀模型**、测试齐全（单测 40 KB） |
| [wushupei/TerritorialWar](https://github.com/wushupei/TerritorialWar) | **C# / Unity** | 4 | **无** | 小地图，4 队 | ⭐**C# 骨架**：`Cell/Bullet/BulletPool/Ball/Gun/PlayerSystem/MainGame` 七个脚本，每格一个 `Image` + 按队伍分 Layer |
| [k0laa/TerritoryWars-Unity](https://github.com/k0laa/TerritoryWars-Unity) | C# / Unity | 1 | **MIT** | tile-based，20 人联机 | 可商用参考：Photon 联机、**移动涂色**、道具系统（冰冻/加速/双倍分/减速）、60–240 秒计时 |
| [maybe-raven/multiply-or-release](https://github.com/maybe-raven/multiply-or-release) | Rust / Bevy | 5 | **Apache-2.0** | 经典炮台版 | 规则文本权威 + 可复用的**触发区→炮台弹药→格子转化→炮台互扣**闭环 |
| [Ryan4G/phaser3-typescript-marblerace](https://github.com/Ryan4G/phaser3-typescript-marblerace) | TS / Phaser3 | 3 | **MIT** | 小网格 | 可商用参考；中英命名对应关系的证据 |
| [techstay/marble-territory-war](https://github.com/techstay/marble-territory-war) | JS / Canvas | 0 | **无** | **72×72** | 自研粒子与物理引擎、**分区征服(sector)**、模块划分（main/config/territory/plinko/battle/fx/audio） |
| [MarchBeta2087/Landwar](https://github.com/MarchBeta2087/Landwar) | Python | 3 | **GPL-2.0** | 小网格 | 规则描述："炮弹击中他国领土或无主之地，被击中的领土被发射国占领" |
| [Liangmf11/3D-marble-territory-war](https://github.com/Liangmf11/3D-marble-territory-war) | C++ / OpenGL | 0 | **无** | 3D 课程项目 | 碰撞检测 + "**marble changes colors of the floor**" + 粒子系统的 3D 做法 |
| [CheezyOne/MultiplyOrRelease](https://github.com/CheezyOne/MultiplyOrRelease) | **C#** | 0 | **无** | 无 README | 仅作为"存在 C# 实现"的线索 |
| [Zylann/marbles](https://github.com/Zylann/marbles) | **GDScript / Godot** | 6 | **MIT** | 弹珠竞速（无领土） | Godot 侧弹珠物理/相机的可复用代码 |
| [MechanicalFlower/Marble](https://github.com/MechanicalFlower/Marble) | **GDScript / Godot** | 17 | **MIT** | 弹珠竞速（无领土） | 最成熟的 Godot 弹珠项目：程序化赛道、实时排名、淘汰模式 |

> **Godot 空白**：以上没有一个是"Godot + 弹珠领土战争"。Godot 侧只有**弹珠竞速**（Zylann、MechanicalFlower 两个 MIT 项目）。这意味着领土侵蚀 + 海量粒子的 Godot 方案需要我们自己做，但可以拿这两个项目对照 Godot 里弹珠的手感与性能。

---

## 4. 规模对照表（最重要的工程结论）

| 实现 | 领地网格 | 格数 | 备注 |
| --- | --- | --- | --- |
| techstay/marble-territory-war | 72×72 | 5,184 | 视觉很华丽，但格子极小 |
| Carson Jay Marbles "Giant" | 100×100 | 10,000 | 社区"巨型"版，34 万播放 |
| wushupei/TerritorialWar | 小网格 | — | 每格一个 Unity `Image` 对象 |
| **wan300 四色弹珠领土战争** | **256×256** | **65,536** | 开源里规则最完整，规模也最"正经" |
| YouTube《Massive 500×500》 | 500×500 | 250,000 | 作者必须做"一次吞多格 + 分区"才扛得住 |
| **中文《百万像素》** | **1000×1000** | **1,000,000** | 开源圈的 4~200 倍 |

**一条旁证（关于"百万像素到底有多重"）**：233乐园转载的《俄罗斯方块》百万像素版本规格说明里写着「**本程序由 Turbowarp 编写**」（[出处](https://www.233leyuan.com/post-detail/2075607960217792512)）。Turbowarp 是 Scratch 的运行时 —— 如果连 Scratch 都能跑 1000×1000，说明其每帧工作量必然极小（涂色纯局部、没有全图 pass）。另有多期标题直接写「**自制动画**」，说明至少有一部分是**离线渲染/录制**，完全不受实时预算约束。以上为旁证而非实证（无法查看其源码），仅供参考。

**结论**：不要被"百万像素"这个名字绑住。整个开源生态没人做到 100 万格；**256×256 已经是"标准做法"，1000×1000 是需要专门工程优化的目标**。建议先按 256×256 把玩法调爽（有先例、平衡数据可对照），再把网格变成配置项往上顶。

---

## 5. ⭐ 核心算法：领地侵蚀（从 wan300 抠出，可直接落地）

这是整个游戏的心脏。它的数据结构极其简单，但平衡性很好：

### 5.1 数据结构

```
owner     : Int8Array  // 每格的势力索引，-1 = 中立
strength  : Uint8Array // 每格的"防御强度/血量"
index     = y * width + x
```

两字节一格的平行字节数组 —— **比位域打包更朴素，但对 256×256~1000×1000 完全够用，而且可读性极佳**。

### 5.2 侵蚀规则（`paintDisk` / `paintLine` 的核心分支）

子弹/炮弹落在某格时，先算一个带随机浮动的攻击强度：

```
appliedPower = max(1, round(power × (0.72 + random() × 0.28)))      // 0.72~1.0 倍浮动 → 战局不可预测
```

然后按当前归属分四种情况：

```
if 该格是自己的:
    strength[i] = min(210, strength[i] + max(1, round(appliedPower × 0.12)))   // 加固自己
elif 该格是中立:
    owner[i]    = team
    strength[i] = min(180, appliedPower)                                       // 直接占领
elif appliedPower >= strength[i]:                                              // 攻破敌方
    owner[i]    = team
    strength[i] = min(180, appliedPower - strength[i] + 14)                     // 余威残留
else:                                                                          // 攻不破
    strength[i] = max(0, strength[i] - appliedPower)                            // 只削防御
```

最后结算能量：

```
新占领格数 × 1 点能量/格   ← FOREIGN_TERRITORY_ENERGY_COST = 1
能量耗尽则该次攻击提前结束
```

这套规则的妙处：

- **中立格便宜、敌方格贵**（敌方格要先削 `strength` 再夺取），天然形成"先圈地、后血战"的节奏；
- **打己方格是加固**，所以有余力的势力会反复"刷"自己的领地，形成视觉上的脉动；
- `+14` 的余威让刚被攻破的格子不容易被第三方顺手拿走；
- 强度封顶（180/210）保证游戏不会因为无限囤积而卡死；
- **每格 1 点能量**这个常数就是中文变体"每格消耗 N 点能量"的直系祖先 —— 中文版只是把它做成了**随时间 1→16 递增**。

### 5.3 可以照抄的参数表（来自该项目 `config.ts` 与 `combat.ts` 常量）

| 参数 | 值 | 说明 |
| --- | --- | --- |
| 领地网格 | 256×256 | 战场板 446×446 px |
| 舞台尺寸 | 854×480 | 左侧 Plinko 板 286×456 + 右侧战场 446×446 |
| tickRate / 逻辑时钟 | 60 / 4 | |
| 每队 Plinko 弹珠数 | 5 | |
| 基地血量 | 8,000,000 | |
| 能量上限 | 2,400,000,000 | |
| 炮塔初始弹药 | 100,000 | |
| 同屏子弹上限 | 384/队（全局 1,800） | |
| 炮塔开火间隔 | 0.08 s | |
| 炮塔转速 | 0.62 | 360° 扫射（对比 wushupei 是 0→90° 来回扫） |
| 每格侵蚀能量 | **1** | 中文变体改为随时间 1→16 |
| 常规子弹 | 伤害 1 / 涂色半径 1.15 / 速度 32 | |
| 大球 | 伤害系数 143 / 弹性 0.92 / 碰撞能量损失 18% | |
| 基地半径 | **∝ 领土大小** | 领土越大基地越大（更容易被打中）→ 天然的滚雪球抑制 |
| 武器 | 散弹 / 机枪 / 护盾 / 大球 / 狙击 | 机枪射速随剩余能量变化 |

**Plinko 侧机制**（该项目 README）：左侧只有**中央闸口**允许弹珠进入武器区，**随时间增强的上推力**会筛出越来越高的能量值；**两侧倍率槽**会把原球增值后从顶部随机重发。四座基地各有 360° 旋转炮塔；普通子弹能在**己方领地内撞墙反弹**，接触黑色或敌方领地时完成一次侵蚀并消失。

---

## 6. C# 骨架参考：wushupei/TerritorialWar

虽然它没有 LICENSE（**只能读不能抄**），但模块划分值得照着想 —— 一个 Unity 弹珠领土战争只需要 7 个脚本：

| 脚本 | 职责 | 关键做法 |
| --- | --- | --- |
| `Cell.cs` | 单个领地格 | 每格一个 `Image`；**用 Layer 区分队伍**；`SwitchColor(layer, 新玩家)`：先给旧主 `SetTerritory(-1)`，改颜色和 layer，再给新主 `SetTerritory(+1)` |
| `Bullet.cs` | 子弹 | `Rigidbody2D` + `AddForce(-transform.up × 500)`；`OnCollisionEnter2D` 若撞到 `Cell` 就调 `SwitchColor` 并回池 |
| `BulletPool.cs` | 子弹对象池 | 单例 + `Queue<Bullet>`，`InPool` 只 `SetActive(false)`，`OutPool` 复用 |
| `Ball.cs` | Plinko 弹珠 | 撞到 `X2collider` → `SetMultiple(true)`（弹药翻倍）；撞到 `Firecollider` → `Fire()` 并重置倍率 |
| `Gun.cs` | 炮台 | `Fire(multiple)` 协程：循环 `multiple` 次，每次从池里取子弹发射、`surplus--`、`WaitForSeconds(0.1)`；死亡时全部染黑并通知 `MainGame` |
| `PlayerSystem.cs` | 每队数据与 HUD | 三个计数 `territory / multiple / surplus`；开局 `territory = 224`；**`territory == 0` 即死亡** |
| `MainGame.cs` | 主控 | 炮台在 **0°↔90°** 之间来回扫（`MoveTowardsAngle` 45°/s）；有人出局时给剩余队伍各加一颗弹珠；只剩 1 队则结束 |

**最有价值的三点**：

1. **Multiply/Release 的全部实现只有约 10 行**：`multiple *= 2`（弹珠撞倍率区）→ `surplus = multiple` 然后按颗发射（撞发射区）。这个题材听起来复杂，核心代码其实极少。
2. **用 Layer 分队伍**：子弹与格子的碰撞过滤交给物理层，省掉手写判定。
3. **领土归零即死亡** —— 比"基地血量归零"更好懂，也让 HUD 上那个 `Territory:224` 成为唯一的生命线。

> ⚠️ 注意它对每个格子建了 GameObject。在 256×256（6.5 万格）时勉强能跑，到 1000×1000（100 万格）**必然崩**，所以它的做法只能当逻辑参照，**不能当性能方案**。

---

## 7. 商业 App 的机制细节（做设计时的对照）

| 来源 | 关键机制 |
| --- | --- |
| [Marble Race and Territory War](https://play.google.com/store/apps/details?id=marble.race.territory.war.multiply.or.release&hl=zh) | 弹珠在两条数学门赛道循环，直到抵达 Release 门；进竞技场后**滚动并改变沿途每格颜色**；**占领会逐渐缩小弹珠体积**（长太慢到不了远处，太大则统治地图直到耗尽）；**碰撞时小球消失、大球损失相当于对手体积的大小**；尺寸可从普通数涨到 K/M/G/T/P/E；模式：碰撞分裂、额外生成器、可调速度、2/4 人、可配最大体积、自定义颜色、**自定义 512×512 PNG 头像** |
| [Samsung Galaxy Store 页](https://galaxystore.samsung.com/detail/marble.race.territory.war.multiply.or.release) | 最精确的一条："**每被重新染色一个格子，球的体积减 1**" |
| [Marble Wars!（App Store）](https://apps.apple.com/us/app/marble-wars/id1633700915) | 明示模式划分：**classic / multiply or release** |
| [Marble Kingdoms Wiki](https://marble-kingdoms.fandom.com/wiki/Maps) | 地图会**随战斗位置动态变化**，格子绿/墙棕；城内白、城外绿 |

**设计要点提炼**：

- **球的体积 = 弹药 = 血量 = 占领货币**，四合一。这一条是整个题材最优雅的地方，也是平衡的全部来源。
- **Must 有"倍速 + 时间轴 + 排行"**：这是观赏型模拟，观众要能快进、回看、看趋势。
- **Must 有自定义头像/国旗**：商店版把它当核心卖点（512×512 PNG），国内变体全是"球球/国家球/俄罗斯方块"换皮。

---

## 8. 视频与教程资源

| 资源 | 内容 |
| --- | --- |
| [Carson Jay Marbles 频道](https://www.youtube.com/c/carsonjay) | 题材祖师爷：Marble Races、**tutorials**、Territory Wars。《Territory Wars - The Original》(56 万)、《Giant Multiply or Release 100×100》(34 万)、《Multiply or Release - Tournament》(330 万) |
| [Lost Marbles **Dev Channel**](https://marble-kingdoms.fandom.com/wiki/Lost_Marbles) | **教学向**：公开讲 Marble Kingdoms 是怎么写出来的。例：[To make marble kingdoms, you need marble fighters [tutorial]](https://www.youtube.com/watch?v=pv2lMf0MxDM) |
| B 站《手把手教你制作属于自己的领土战争》 | **Algodoo 教程，从入门到精通，已完结**（[BV1mq4y1N7Ni](https://www.bilibili.com/video/BV1mq4y1N7Ni/)） |
| B 站《【领土战教程01】用 Unity 做出你专属的领土战争》 | Unity 方向教程（[相关搜索页](https://search.bilibili.com/all?keyword=%E9%A2%86%E5%9C%9F%E6%88%98%E6%95%99%E7%A8%8B)） |
| B 站《[Unity自制] 领土战争 -- 经典领土争夺》 | 规则直白：绿区翻倍、蓝区 ×3、红区发射；**制作资源包共享在评论区**（[BV18N41127kd](https://www.bilibili.com/video/BV18N41127kd/)） |
| [Physion: Multiply or Release](https://physion.net/blog/multiply-or-release-in-physion) | 题材综述 + 可交互场景（四色、旋转炮台、连续扫射） |
| [共创世界 ccw.site](https://www.ccw.site/) | 中文 Scratch/Turbowarp 版《球球领土战争》作品的发布地 |

---

## 9. License 风险表（动手前必须过一遍）

| 项目 | License | 能否抄代码 |
| --- | --- | --- |
| maybe-raven/multiply-or-release | **Apache-2.0** | ✅ 可以（保留声明） |
| Ryan4G/phaser3-typescript-marblerace | **MIT** | ✅ 可以 |
| k0laa/TerritoryWars-Unity | **MIT** | ✅ 可以 |
| Zylann/marbles · MechanicalFlower/Marble | **MIT** | ✅ 可以（Godot 参考） |
| MarchBeta2087/Landwar | **GPL-2.0** | ⚠️ 传染，闭源项目别碰 |
| wan300/Four_Color_Marbles_Territory_War | **无** | ❌ 默认保留所有权利 → **只读规则与数值，不复制代码** |
| wushupei/TerritorialWar | **无** | ❌ 同上（本文第 6 节是**职责描述**，不是代码） |
| techstay/marble-territory-war | **无** | ❌ 同上 |
| Liangmf11 / CheezyOne | **无** | ❌ 同上 |
| OpenFrontIO（前一份文档） | **AGPL-3.0** | ❌ 只借鉴思想 |

> "没有 LICENSE 文件"在法律上等于**保留所有权利**，不是"随便用"。若本项目将来闭源或商用，第 5 节的算法请按**规则描述重新实现**（算法与数值本身不受版权保护，代码表达受保护），不要整段移植源码。

---

## 10. 对本站项目的结论

1. **算法基线用第 5 节的侵蚀模型**：`owner: Int8Array` + `strength: Uint8Array` + 四分支侵蚀 + 每格 1 能量。它已被一个完整的开源项目验证过平衡性，而且实现量极小。中文特色的"每格能量 1→16 随时间递增"只需把 `FOREIGN_TERRITORY_ENERGY_COST` 换成 `min(1 + floor(t/180), 16)`。
2. **C# 骨架照第 6 节的七个模块**划分（Cell 层 / 子弹与对象池 / 弹珠与触发区 / 炮台 / 每队状态 / 主控），但**按 Godot 的方式重写**：Cell 不建节点，用平面数组 + 一张纹理。
3. **规模按 256×256 起步**（wan300 的先例），网格尺寸做成配置项，M4 再冲 1000×1000。
4. **把"球的体积 = 弹药 = 血量 = 占领货币"当作不可动摇的核心**（第 7 节），所有数值围绕它调。
5. **观赏性功能优先级要高于操作**：倍速、时间轴、排行、自定义头像 —— 商店版与国内系列都证明了这几项才是留存来源。
6. **Godot 没有先例**：弹珠物理/相机去读 [Zylann/marbles](https://github.com/Zylann/marbles) 与 [MechanicalFlower/Marble](https://github.com/MechanicalFlower/Marble)（都 MIT），领土层的方案沿用 [research-territory-war.md](./research-territory-war.md) 第 5 节。

---

## 11. 参考链接汇总

**规则与谱系**
- [Territory Wars - The Original (Carson Jay Marbles, 2018)](https://www.youtube.com/watch?v=LOOflhF1Bjk) · [频道](https://www.youtube.com/c/carsonjay) · [Giant 100×100](https://www.youtube.com/watch?v=vPFzFfHe6hM)
- [Algodoo Wiki: Multiply or Release](https://algodoo.fandom.com/wiki/Multiply_or_Release) · [Marble Kingdoms Wiki](https://marble-kingdoms.fandom.com/wiki/Multiply_or_Release) · [Physion 综述](https://physion.net/blog/multiply-or-release-in-physion)
- [maybe-raven/multiply-or-release 规则](https://maybe-raven.itch.io/multiply-or-release)

**项目**
- [wan300/Four_Color_Marbles_Territory_War](https://github.com/wan300/Four_Color_Marbles_Territory_War) · [wushupei/TerritorialWar](https://github.com/wushupei/TerritorialWar) · [k0laa/TerritoryWars-Unity](https://github.com/k0laa/TerritoryWars-Unity) · [maybe-raven/multiply-or-release](https://github.com/maybe-raven/multiply-or-release) · [Ryan4G/phaser3-typescript-marblerace](https://github.com/Ryan4G/phaser3-typescript-marblerace) · [techstay/marble-territory-war](https://github.com/techstay/marble-territory-war) · [MarchBeta2087/Landwar](https://github.com/MarchBeta2087/Landwar) · [Zylann/marbles](https://github.com/Zylann/marbles) · [MechanicalFlower/Marble](https://github.com/MechanicalFlower/Marble)

**中文资料**
- [《球球领土战争》S1 EP51 规则详解](https://www.233leyuan.com/post-detail/2066115955610001408) · [第二十季](https://www.233leyuan.com/post-detail/2075607960217792512) · [B站 EP33（Unity）](https://www.bilibili.com/video/BV11N9yBKEyk/)
- [B站 Algodoo 教程（已完结）](https://www.bilibili.com/video/BV1mq4y1N7Ni/) · [B站 Unity 自制领土战争](https://www.bilibili.com/video/BV18N41127kd/) · [共创世界 ccw.site](https://www.ccw.site/)

**商店页（机制描述）**
- [Marble Race and Territory War](https://play.google.com/store/apps/details?id=marble.race.territory.war.multiply.or.release&hl=zh) · [Galaxy Store](https://galaxystore.samsung.com/detail/marble.race.territory.war.multiply.or.release) · [Marble Wars!](https://apps.apple.com/us/app/marble-wars/id1633700915)

> 说明：`fandom.com` 系站点（algodoo / marble-kingdoms / territorial）的正文抓取多次被反爬拒绝，相关内容以搜索结果摘要为准；
> 各项目的 License 为本次通过 GitHub API 实时读取的 `license.spdx_id` 字段。

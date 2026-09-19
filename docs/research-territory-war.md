# 百万像素领土战争（弹珠/球球领土战争）—— 调研与技术方案

> **姊妹文档**：[marble-territory-war-projects.md](./marble-territory-war-projects.md) —— 该题材的**资料与开源项目调研**，
> 包含题材谱系、11 个项目的 License 与规模对照、以及从开源实现里抠出的**领地侵蚀算法与完整数值表**。
>
> **修正说明**：本文第一版把目标游戏误判成了 territorial.io 那种"点击目标 → 领土从边界 BFS 生长"的即时策略。
> 你指出的"球运动然后占领领地"才是对的 —— 这个题材的真正源头是 **Multiply or Release（弹珠增殖/释放）**，
> 领土是靠**实体小球物理移动、沿途涂色**产生的，不是靠点击扩张。本文已按这个题材重写。

---

## 1. 结论速览

| 问题 | 结论 |
| --- | --- |
| 题材源头 | **Multiply or Release**，2021-07-15 由 **MIKAN** 在 Algodoo 首发；其战场设计明确"partly inspired by Carson Jay Marbles's Territory Wars"（[Algodoo Wiki](https://algodoo.fandom.com/wiki/Multiply_or_Release)、[Marble Kingdoms Wiki](https://marble-kingdoms.fandom.com/wiki/Multiply_or_Release)、[Physion 博客](https://physion.net/blog/multiply-or-release-in-physion)）。B 站/国内叫**领土战争**、**球球领土战争** |
| 核心循环 | 弹珠走**钉板(Plinko)** → 落在 **Multiply 区**（弹药 ×2/×4）或 **Release 区**（把攒的弹药全部打出去）→ 子弹打在地图格子上**把格子染成自己的颜色** → 最后存活者胜 |
| 中文"百万像素"变体 | **Unity** 制作（作者 @Fishf / MarblePi 等）：地图 **1000×1000 = 100 万格**，**每格消耗能量才能占领**，S1 规则为初始 1 点、**每 3 分钟 +1、上限 16**，后续赛季直接固定 16 点（[233乐园 S1 EP51](https://www.233leyuan.com/post-detail/2066115955610001408)、[第二十季](https://www.233leyuan.com/post-detail/2075607960217792512)） |
| 另一种变体（滚动涂色） | [Marble Race and Territory War](https://play.google.com/store/apps/details?id=marble.race.territory.war.multiply.or.release&hl=zh)：弹珠进战场后**滚动，经过的每个格子都变色**；**占领会消耗弹珠体积**，形成"扩张 vs 生存"的自然平衡；**小球撞大球会被吞掉，大球减掉相当于对手体积的大小** |
| 技术难点（与 territorial.io 完全不同） | ① **海量粒子的物理与碰撞**（几千~几万颗球，不能用引擎内置刚体）② **每帧把大量格子染色**并低成本送到 GPU ③ **几万个球的渲染**（必须 GPU 实例化）④ 领土与粒子的**耦合**（能量/体积既是血量又是占领货币） |
| 最稳的参考实现 | [maybe-raven/multiply-or-release](https://github.com/maybe-raven/multiply-or-release)（Bevy/Rust，**经典规则实现最完整**）、[techstay/marble-territory-war](https://github.com/techstay/marble-territory-war)（JS+Canvas，**自研粒子与物理引擎**）、[Ryan4G/phaser3-typescript-marblerace](https://github.com/Ryan4G/phaser3-typescript-marblerace)（明确标注"Multiply or Release / B站领土战争"） |

---

## 2. 这个游戏到底是什么

### 2.1 经典版（炮台/钉板）——规则的权威描述

来自参考实现的官方规则（[itch.io](https://maybe-raven.itch.io/multiply-or-release) + [GitHub README](https://github.com/maybe-raven/multiply-or-release)）：

1. 弹珠沿障碍赛道（钉板/Plinko）滚落，落入某几个**触发区(trigger zone)**之一。
2. 每颗弹珠在战场上对应一座**炮台(turret)**；弹珠落进触发区，对应炮台就执行该区的动作：
   - **Multiply**：把当前弹药(charge) **×2 或 ×4**
   - **Release**：把攒的弹药**一次性强射**，或**连成一串小射**
3. 战场是**格子(tile)网格**，每格关联一座炮台。**子弹打中敌方格子时，消耗一点弹药把该格转化**为自己的颜色。
4. **子弹打中炮台**：子弹和炮台**等量互扣弹药**；炮台弹药归零即死亡。

Physion 版本补充了观感与张力来源："每多乘一次就多一分悬念 —— 提前 Release 是弱攻，等到乘到几百再 Release 就是翻盘级总攻"，"四个队伍各有持续旋转的炮台位于自己领土中心，子弹连续扫射战场网格，接触即转化敌方格子"，"**最后一个存活队伍获胜**"（[Physion](https://physion.net/blog/multiply-or-release-in-physion)）。

### 2.2 滚动版（球球领土战争 / 百万像素）

来自 App 商店页的机制描述（[Google Play](https://play.google.com/store/apps/details?id=marble.race.territory.war.multiply.or.release&hl=zh)）：

- 弹珠在**两条数学门赛道**上循环穿梭，门按弹珠大小做增减，直到抵达 **Release 门**。
- 进入竞技场后，弹珠**在战场上滚动，并改变沿途每个方格的颜色**。
- **占领领土会逐渐缩小弹珠的大小** —— 扩张与生存形成自然平衡：长太慢的球永远到不了远处，太大的球能统治地图但会被不断占领最终耗尽。
- 弹珠大小可以从普通数字一路增长到 K/M/G/T/P/E 量级。
- **不同玩家的弹珠碰撞时：较小的一方消失，较大的一方损失相当于对手大小的体积。**
- 多种模式：碰撞后分裂成两颗、额外弹珠生成器、可调模拟速度、双人/四人对战、可配置最大弹珠体积、自定义颜色、**可为每位玩家上传 512×512 PNG 头像**（国家球/球队/国旗/meme）。

"要么增殖，要么释放。谁会赢？" —— 这就是这个名字的全部含义。

### 2.3 "百万像素"的确切规格（中文变体）

| 项 | 值 | 来源 |
| --- | --- | --- |
| 地图 | **1000×1000 = 1,000,000 格** | [233乐园 S1 EP51](https://www.233leyuan.com/post-detail/2066115955610001408) |
| 占领成本 | 初始 **1 点能量/格**，**每 3 分钟 +1**，**上限 16** | 同上 |
| 后期赛季 | 直接固定 **16 点能量/格** | [第二十季](https://www.233leyuan.com/post-detail/2075607960217792512) |
| 其他卖点 | "百万像素 + **海量粒子**特效"，引擎为 **Unity** | [B站 EP33 简介](https://www.bilibili.com/video/BV11N9yBKEyk/)（标注 `[App] Unity`） |

成本 1→16 的爬升是个很聪明的设计：**开局快速铺开、后期扩张成本翻 16 倍**，天然制造僵持与终局，避免百万格被瞬间涂满。

### 2.4 社区在规模上的折中（值得抄的工程细节）

做 500×500 的创作者在视频说明里写了三条优化（[YouTube: Massive 500x500 Territory War](https://www.youtube.com/watch?v=1cTbETc5bmU)）：

- 地图 **500×500**（25 万格）而不是 100 万格；
- **球一次可以吞下多于一格**（不是一格一格涂）；
- **地图切成分区(sector)**，"当一个分区里的领土全部[被占领]…" → 用分区归属缓存来做快速判断/整块换色。

这正是我们做 100 万格时必须采用的思路：**不要让每次涂色都变成逐格随机写**。

---

## 3. 规则表（可直接当配置项）

| 模块 | 规则 | 我们的建议默认值（可调） |
| --- | --- | --- |
| 地图 | 战场格数 | 1000×1000（先做 256×256 跑通，再上 100 万） |
| 数学门 | ×2 / ×4，可扩展 ÷2、+N | 4 个 ×2、2 个 ×4、1 个 Release |
| 弹药 charge | 每个炮台持有；Multiply 翻倍、Release 全打出 | 起始 1 |
| 子弹 | 每颗子弹 = 1 点 charge；命中敌格扣 1 点转化 | 命中友方格不消耗 |
| 占领成本 | 每格消耗 charge，随时间上升 | `cost = min(1 + floor(t / 180s), 16)` |
| 炮台受击 | 子弹与炮台等量互扣 | 归零死亡 |
| 滚动涂色（变体） | 球半径内格子变色，每格扣对应能量 | 每格扣 `cost` |
| 碰撞 | 小球消失；大球损失对手体积 | 体积差 <5% 时双方都重伤（可选，更戏剧化） |
| 收入 | 按领土比例定期补充能量 | 每 5 秒补 `领土格数 × 0.1` 点（可选） |
| 胜负 | 最后存活 / 领土占比最高 | 最后存活；或超时按领土排名 |
| 观感 | 粒子、拖尾、震屏、爆炸、倍速、时间轴 | 全部要有；这是这个题材的**核心卖点**，不是加分项 |

---

## 4. 参考实现与可借鉴点

### 4.1 [maybe-raven/multiply-or-release](https://github.com/maybe-raven/multiply-or-release)（Bevy / Rust）

- 实现了**经典规则的完整闭环**：trigger zone → turret charge → tile convert → turret 互扣致死。
- 战场是"格子网格，每格关联一座炮台"，即**格子 ↔ 炮台**是绑定关系 —— 这解释了为什么早期版本的战场看起来像很多小炮台拼成的棋盘。
- 用了 `bevy_hanabi` 做粒子（作者注明 WASM 构建缺特效）。
- **用什么语言写的不重要，重要的是它是唯一把规则写清楚的开源实现** —— 建议直接对着它的规则做数值对齐。

### 4.2 [techstay/marble-territory-war](https://github.com/techstay/marble-territory-war)（JS + Canvas，自研引擎）

自述是 "high-performance, visually polished Marble Territory War ... **Custom Particle & Physics Engine**"，文件划分值得直接照搬成 C# 的模块划分：

```
main.js        # 应用协调 + 60 FPS requestAnimationFrame 主循环
config.js      # 势力配色、常量、单位参数
territory.js   # 网格征服的空间引擎 + 渲染逻辑
plinko.js      # 钉板投放、乘法门、触发槽
battle.js      # 4 势力单位战斗、弹珠碰撞、基地血量
fx.js          # 粒子池、冲击波、震屏
audio.js       # 程序化音效合成
```

关键卖点：**"Sector Conquest (72×72 High-Resolution Grid)"、"瞬时空间网格征服 + 动态占领脉冲动画 + 实时百分比仪表"**、多层级粒子系统（爆炸/火花/拖尾/冲击波/飘字/震屏）。注意它的网格只有 72×72 —— **格子数有多小、特效就有多重**，这是这个题材的真实工程配比。

### 4.3 [Ryan4G/phaser3-typescript-marblerace](https://github.com/Ryan4G/phaser3-typescript-marblerace)

Phaser3 + TypeScript，作者自述"a marble race game like 'Multiply or Release' which is made by Algodoo. **It's also called 'Territory Wars' on the BiliBili**" —— 直接印证了中英命名对应关系。

### 4.4 数据布局仍可借鉴 OpenFrontIO

领土格子的表示问题在"涂色版"和"点击版"里是一样的，所以 [OpenFrontIO 的 GameMap](https://raw.githubusercontent.com/openfrontio/openfrontio/main/src/core/game/GameMap.ts) 那套**平面 `ushort[]` 位域 + 直接上传纹理**依然适用（详见 5.3）。但它的**攻击算法（边界优先队列）在本题材里用不上**，那是点击扩张版的算法。

> ⚠️ **License**：OpenFrontIO 是 **AGPL-3.0**，不要抄代码，只借鉴思想。其余三个参考实现的授权请在动手前逐个确认（本次调研未能稳定取到其 LICENSE 文件）。

---

## 5. Godot 4.7 + C# 技术方案（修正版）

现有工程基线：Godot `4.7-stable_mono`（`D:\Godot\GODOT\Godot_v4.7-stable_mono_win64`）、`Godot.NET.Sdk/4.7.0`、`net8.0`、Forward+/D3D12。
你已确认：**只做单机，不追求确定性** → 可以放心用 `float`、`System.Random`、多线程 `Parallel.For`/`Task`。

### 5.1 三个互相解耦的子系统

```
┌─ 赛道/钉板 (Plinko) ──┐   少量刚体（几十颗弹珠）→ 可以用 Godot 内置 2D 物理，甚至手写
│  ×2 / ×4 / Release   │   产出：给某势力 charge *= 2 / 发射子弹
└──────────┬───────────┘
           ↓ 事件
┌─ 战场 (Battlefield) ──┐
│  ① 单位层：海量粒子/弹珠/子弹（SoA + 空间哈希，自研，不用内置物理）
│  ② 领土层：1000×1000 ushort[] 网格 + 脏矩形
└──────────┬───────────┘
           ↓ 状态
┌─ 渲染层 ──────────────┐
│  领土：1 张纹理（shader 查调色板 + 边界描边）
│  单位：MultiMeshInstance2D（几万个球 = 1 次 draw call）
│  特效：GPUParticles2D + CPU 粒子池
└───────────────────────┘
```

### 5.2 单位层（本题材唯一的新增重头戏）

**绝对不要**给每颗球建一个 `Node2D`/`RigidBody2D`。几千个刚体圆的物理在世界里是灾难，几万个节点光是同步就会掉帧。

正确做法 —— **SoA（数组结构）自研积分**：

```csharp
// 全部是并列的平面数组，索引 = 单位 ID；删除用 swap-remove 保持紧凑
float[] x, y, vx, vy, radius, energy;
int[]   faction;      // 0 = 中立/无效
byte[]  kind;         // 弹珠 / 子弹 / 碎片
int     count;        // 活跃单位数，数组按 cap 预分配，不 new
```

- **积分**：定步长（如 1/60 s）半隐式欧拉；球墙反弹、阻力、吸引/散射都很便宜。
- **碰撞**：**空间哈希网格**（格子边长取 2×最大半径），每帧重建桶，只测同桶及邻桶。
  参考 [Gorillasun 的空间哈希教程](https://www.gorillasun.de/blog/particle-system-optimization-grid-lookup-spatial-hashing/) 与 [Carmen Cincotti 的 spatial hash maps](https://carmencincotti.com/2022-10-31/spatial-hash-maps-part-one/)。
- **参考量级**：社区实测"朴素两两检测 + Pixi 渲染，2 万粒子能到 120 FPS"（[HN 讨论](https://news.ycombinator.com/item?id=40809010)）—— 说明**只要做了空间划分，几万单位完全在预算内**。
- 想更狠：把积分+碰撞搬进 compute shader（Godot 4 的 `RenderingDevice` 支持），但那是 M5 的优化项，不是 M1 该做的事。

### 5.3 领土层（从 OpenFrontIO 借来的布局）

```csharp
const int W = 1000, H = 1000;          // 100 万格
ushort[] owner = new ushort[W * H];    // bit0-11 势力ID(0=中立) / bit12-15 标志
byte[]   cost  = new byte[W * H];      // 可选：该格当前的占领成本（阶梯 cost）
int[]    factionTiles;                 // 每势力格数（领土计数，避免扫全图）
// 脏矩形：本帧被改动的格子范围
int dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY;
```

**涂色 = 圆盘 stamp**：为每种半径预计算一次偏移表 `offsets[r] = int[] {dx,dy,...}`（半径 r 的圆内所有偏移），每帧对每颗球按自己的半径 stamp 一遍：

```
for each 单位 u:
    for each (dx,dy) in offsets[u.radius]:
        i = (u.y+dy) * W + (u.x+dx)
        if owner[i] 属于敌方: 扣 u.energy 的 cost，够则 owner[i] = u.faction，更新 factionTiles，扩展脏矩形
```

**关键优化（对应 500×500 作者的"sector"）**：
- 半径越小 → stamp 的格数按 r² 增长，**radius 一定要设上限**（例如最大 8，超过就改成"一次吞多格但按矩形块"）。
- 地图切 **32×32 的分区**，缓存每分区的"是否已被某势力独占 / 各势力占比"，可跳过大量无变化的 stamp 与整块换色。
- 格子数本身就是最有效的画质调节旋钮：**72×72 也能做出很好看的作品**（见 4.2）。

### 5.4 渲染层

**领土纹理（走与第一版相同的三阶段路线）**

```
ushort[] owner ──(调色板 LUT: ushort→RGBA 4 字节拷贝)──> byte[] rgba
              ──Image.SetData()──> ImageTexture.Update()──> Sprite2D(nearest, 相机缩放)
```

- 1000×1000 RGBA8 = 4 MB。**已实测（见 5.8）：`SetData` 0.75 ms + `Update` 0.94 ms ≈ 1.7 ms** —— [godot#76994](https://github.com/godotengine/godot/issues/76994) 报告的 16~38 ms **在本机不复现**，全量上传是可接受的起点。
- 脏矩形阶段：⚠️ **Godot 4.7 的 `TextureUpdate(Rid, layer, data)` 没有 x/y/w/h 参数，无法直接传子区域**（实测 API，见 5.8）。局部更新的唯一路径是「小 staging 纹理 `TextureUpdate`（每块 16 KB）+ `TextureCopy` 到目标区域」。另外 C# 类型名是 **`Texture2Drd`**（属性 `TextureRdRid`），不是 `Texture2DRD`。
- Shader 阶段：GPU 只收 OwnerID 纹理，**颜色在 shader 里查调色板**，采样上下左右 4 邻居做**边界描边**，采样 terrain 做海岸线；相机缩放到像素级时保留"格子颗粒感"（这是百万像素的观感来源）。
- 读整数纹理要用 `usampler2D` + `filter_nearest`，且格式必须是整数格式 —— **先用 32×32 的小 demo 验证格式能吃**，别在 100 万格上才发现。

**单位渲染（几万个球）**

- `MultiMeshInstance2D`：**一次 draw call 画几万个球**，用 `MultiMesh.SetInstanceTransform2D` / `SetInstanceColor` 每帧写变换与颜色。官方明确它适合这种"大量相同网格"的场景（[MultiMeshInstance2D 文档](https://docs.godotengine.org/en/stable/classes/class_multimeshinstance2d.html)）。
- 注意：每帧写几万个 instance 变换本身有 CPU 成本，量级大的话考虑把位置放进纹理、用顶点着色器取（`INSTANCE_CUSTOM` / 数据纹理）。
- 拖尾/火花/爆炸走 `GPUParticles2D`（GPU 端，几乎零 CPU 成本），震屏和飘字走 CPU 侧小粒子池。

### 5.5 性能预算

| 项 | 量级 | 判断 |
| --- | --- | --- |
| 领土内存 | `ushort` 2 MB + `byte` 1 MB | 随便放 |
| 领土纹理上传 | 4 MB/次 | **实测 1.7 ms**（SetData 0.75 + Update 0.94），chunk 位块更新 0.28~0.83 ms → **不是瓶颈**（5.8） |
| 单位积分 | 2 万球 × 60 FPS = 120 万次/秒 | 纯 SoA 算术，C# 绰绰有余 |
| 单位碰撞 | 空间哈希后每球只测邻桶（常数个） | 同上 |
| 涂色写入 | 每球 `π r²` 格；2 万球 × r=3 ≈ 56 万格/帧 | **这是最大的热点**，必须靠半径上限 + 分区跳过 + 脏矩形控制 |
| 球渲染 | 1 次 draw call（MultiMesh） | 不是瓶颈 |
| 反面案例 | 每球一个 Node2D/RigidBody2D；逐格 `Image.SetPixel` | 前者几千个就卡，后者 1024×1024 逐帧写实测掉到 15 FPS（[Godot Forums](https://godotforums.org/d/22104-writing-dynamically-inside-texture-fastest-alternative-to-setpixel)） |

**结论：本题材的算力风险集中在"涂色写入量"和"单位数量"，而不是领土格数。** 半径上限、分区跳过、脏矩形三件事决定了能不能上 100 万格 + 海量粒子。

### 5.6 Godot C# 踩坑清单

| 坑 | 对策 |
| --- | --- |
| 海量节点 | 单位用 SoA 数组 + MultiMesh，绝不建 Node |
| 内置 2D 物理扛不住几千刚体 | 自研积分 + 空间哈希；只有钉板那几十颗球可以用内置物理 |
| `Image.SetPixel` | 一律走 `Image.SetData(byte[])` 批量写 |
| `ImageTexture.Update()` 只能整张更新 | 全量够用就全量；不够就上 `RenderingDevice.TextureUpdate` |
| 每帧 new 数组/List | 全部预分配 + `count` 游标 + swap-remove；`Span<T>` 遍历 |
| 跨线程调 Godot API | 模拟线程只碰纯 C# 数组，主线程负责上传纹理/更新 MultiMesh |
| `Color` 结构体在热循环 | 调色板直接存 `byte[4096*4]`，逐像素做 4 字节拷贝 |
| 相机缩放导致的采样噪点 | 用 nearest 采样 + 整数倍缩放，或者干脆拥抱颗粒感 |

### 5.7 计算着色器：需不需要？

> **⚠️ 决策已变更（最终）：使用 compute shader。**
> 本节下面"不用 compute"的论证基于 5.8 的实测数字，数字本身没错（CPU 全通路 ≈ 2.8 ms、约 6 倍余量），
> 但**权重估计错了**：我把 RenderingDevice 样板估成了一次性沉没的大山，而实际上参考项目
> `E:\GODOT\Project\ComputeShaderBattleSimulation`（同引擎 Godot 4.7 C#、同 GPU RTX 4060 Laptop）
> **已经把整套样板跑通**，可以直接移植。加上子弹是**短命高并发**的，峰值远超 1 万，CPU 方案在峰值没有余量。
>
> **架构方案见 [compute-architecture.md](./compute-architecture.md)。**
>
> 下面保留的原论证仍然有价值 —— 它记录了 CPU 路径的真实成本，而 CPU 版要**保留**下来做正确性对照与降级路径（Compatibility/GLES3 没有 compute）。

**结论：模拟侧不需要；唯一值得 GPU 化的是领土网格的数据通路，而且第一步也不该是 compute。**

这个问题其实是两个独立的问题，混在一起就会答错：

| 问题 | 答案 | 依据 |
| --- | --- | --- |
| **Q1 粒子/子弹的物理与碰撞要不要 compute？** | **不需要，且差得很远** | 这个题材的"海量粒子"是**视觉特效**（`GPUParticles2D` 本来就在 GPU 上）。真正的**模拟实体**极少：wan300 全局子弹上限 1,800、每队 384，弹珠每队 5 颗 —— 而它是在**浏览器里用 JavaScript 单线程**跑 60 Hz 的。JS 在此类紧循环上约比 C# 慢 10~30 倍，C# 单线程余量是两位数倍数级 |
| **Q2 100 万格领土网格怎么送到屏幕？** | **这是唯一的真问题** | 只有"把 `owner`/`strength` 变成像素"这件事与网格规模成正比，而它可以被脏矩形转化成"与**本帧改动格数**成正比" |

**先分清两件事：存储规模 ≠ 每帧工作量**（这是整个判断的枢纽）

| | 数字 | 性质 |
| --- | --- | --- |
| 网格**存储** | 100 万格 × 2 字节 = **2 MB** | 只是"占多大地方"，与算力无关 |
| 每帧**真实涂色写入** | 参考实现约 **1 万格/帧**（1800 子弹 × 半径 1.15 的圆盘/线段扫过，每颗约 2~6 格） | 只有**变化的**格子才算工作量 |
| 每帧**"变像素 + 上传"** | 全量方案下 = **100 万格/帧** | ← **唯一真正与网格规模成正比的环节**，且它由"全量 vs 脏矩形"决定，不由模拟决定 |

> 所以"100 万格应该上 GPU"这个直觉**指向的问题是真的，但药方不是 compute**：那个每帧处理 100 万格的环节是**纹理上传**，它的解药是脏矩形（L1）；而 compute（L3）之所以"正确"，是因为它**把上传这件事永久消灭**，不是因为模拟算不动。

**CPU 涂色的容量与 crossover**

以 C# 平面数组上一次"比较 + 赋值"约 3~5 ns 计：

| 每帧真实涂色格数 | 建议（**已按 5.8 实测的 11.8 ns/格 校正**） |
| --- | --- |
| < 70 万 | CPU 单线程就够（< 8.3 ms） |
| 70 万 ~ 600 万 | CPU + `Parallel.For` 分区并行（实测 8.5x） |
| > 600 万 | 才轮到 L3（GPU 常驻 + compute 涂色） |

参考实现落在 **约 1 万格/帧**（≈ 0.12 ms）→ 完全无压力。**但 10000 颗实体球是 72 万格/帧，正好压在单线程上限上** —— 见 5.8，这正是需要并行（或 GPU）的分界线。

**⚠️ 脏矩形的陷阱（必须用 chunk，不能用单一 bbox）**

单一全局 bbox 在"**四角同时开战**"时会退化成全图 —— 四个角的并集就是整张地图，脏矩形完全白做。而本题材恰恰是四色混战、四面开花。所以脏矩形要按 **chunk（64×64 或 128×128 一块）** 记录，只上传脏块。

**如果 L3 是终局，那它"正确"在哪**

不是算力，而是它**永久消灭上传**：CPU 每帧只丢几十 KB 的子弹线段进 storage buffer，compute 负责 stamp 进常驻显存的网格，显示 shader 直接读。**没有 4 MB 全量上传、没有调色板转换 pass、没有脏矩形维护** —— 整个类别的问题一次性消失。准确表述是：**L3 不是"必须"，但它是"终局正确"**（如同不需要一开始就上 ECS，但 ECS 可能是终局形态）。


**优化阶梯（走到哪一级由实测决定）**

| 级别 | 做法 | 何时够用 | 代价 |
| --- | --- | --- | --- |
| **L0 原型** | `Image.SetData` 全量上传 4 MB | 仅用于**测真实耗时** | 可能撞上 [godot#76994](https://github.com/godotengine/godot/issues/76994) 的 16~19 ms |
| **L1 脏矩形** | `RenderingDevice.TextureUpdate` + `Texture2DRD` 只传变动子矩形 | **大概率够用，推荐默认停在这里** | 维护 dirty rect；**不是 compute** |
| **L2 数据纹理** | 只上传 `owner`+`strength`（2 MB 或更小），**颜色与边界描边都在显示 shader 里算** | 想白送"任意缩放的清晰边界线" | 上传量减半，仍有上传；**不是 compute** |
| **L3 网格常驻 GPU + compute 涂色** | CPU 每帧只发"本帧的子弹线段"（storage buffer，几十 KB），compute 负责 stamp 进网格 | 涂色量爆炸，或 L1/L2 实测仍不够 | 见下方三个代价 |
| **L4 compute 也算粒子物理** | 位置/速度全在 GPU | 单元数到 **10⁵** 才需要 | 调试成本极高 |

> 注意 **L1/L2 都不是 compute shader** —— 只是 `RenderingDevice` 的局部上传 + 显示着色器。跳过这两级直接上 compute 是常见的过度设计。

**L3 的三个真实代价（决定前必须知道）**

1. **与 Web 导出互斥。** Godot 的 compute 只在 **Forward+ / Mobile** 渲染器可用，**Compatibility（GLES3）不支持**（[官方提案 #1027](https://github.com/godotengine/godot-proposals/issues/1027)、[论坛确认](https://forum.godotengine.org/t/compatibility-mode-doesnt-support-compute-shaders-nor-dynamic-buffers/110002)），而 Web 导出走的正是 Compatibility。本题材是**观赏型模拟**，发 Web 让观众直接点开看是很现实的发行方式 —— 选了 compute 就等于放弃这条路。本项目当前是 Forward+/D3D12，桌面无碍，但这个取舍要先定。
2. **网格上 GPU 后 CPU 就不认识领土数了。** HUD 的领土计数、排行、胜负判定都要每队格数。解法是让 compute 顺便做**原子计数**写进计数器 buffer，CPU 再**小批量回读**（每队 4 字节，每秒数次）。成熟套路，但是额外工作量。
3. **调试成本高一个数量级。** compute 里没有断点、没有 `Log`，只能把 buffer 回读出来看。

**触发上 compute 的阈值**

| 条件 | 阈值 |
| --- | --- |
| 模拟单元数（需要两两碰撞） | > 5 万~10 万 → **先试 `Parallel.For`**，再考虑 compute |
| 每帧涂色格数 | > 500 万~1000 万次写入/秒 |
| 全图上传 | 每帧 > 2~4 MB 且脏矩形覆盖不住（全图扩散/流动类规则） |
| 每格都要独立跑规则 | 网格扩散、流体、strength 传导 —— 这类才天然属于 compute |
| 目标包含 Web / 低端 GLES3 | **直接排除 compute** |

**✅ 平台决定（已定）：不做 Web 导出。**
→ compute 的否决权解除（Compute 在 Forward+ / Mobile 可用）。但注意：Android 上**仅支持 GLES3 的老设备会退回 Compatibility → 无 compute**；若将来要发低端安卓机，L3 仍有风险。

**⚠️ 一个改变判断的发现：这个题材几乎没有 AI。**

复核参考实现与商业版机制描述后确认：**这个题材的"策略"不是算出来的，而是物理涌现的。**

- 商业版商店页原文："玩家无需直接操控弹珠…**所有决策都由游戏机制自动做出**"（[Google Play](https://play.google.com/store/apps/details?id=marble.race.territory.war.multiply.or.release&hl=zh)）
- `wan300` 的符号表里**没有任何 AI 函数**：炮塔按固定转速（0.62）自己 360° 扫射，倍率/释放由**弹珠物理落点**决定（掉进 ×2 区就翻倍）
- `wushupei` 的 Unity 版炮台在 0°↔90° 之间来回摆，撞到 `X2collider` 就翻倍

**后果**：上一版"AI 需要读地图 → 反对网格常驻 GPU"的最强理由**不成立**。领土数据只需"每队格数"供 HUD/胜负使用，**原子计数 + 小批量回读就够**。所以 **L3 比上一版评估的更可行**。

**但最终决定权在「要多少颗滚着涂色的实体球」**（注意："百万像素"作品的"海量粒子"是**特效**，不是上万颗滚着涂色的实体球，两者可以分开选）。**下表已按 5.8 实测校正**（实测 **11.8 ns/格**，其中侵蚀逻辑本身约占 1/3）：

| 粒子规模 | 每帧真实涂色格数 | 单线程 | 结论 |
| --- | --- | --- | --- |
| 参考实现（几十颗弹珠 + ~2000 子弹） | ~1 万 | 0.12 ms | 完全无压力 |
| **10,000 颗（85% r=3 / 15% r=10）← 本项目选择** | **~72 万** | **8.5 ms** | **正好压在单线程上限，必须并行或上 GPU** |
| 10,000 颗全部 r=10 | ~317 万 | 37 ms | 单线程不可行 |
| 50,000 颗（同分布） | ~360 万 | 42 ms | 即使并行也吃紧 → compute |

**推荐落点：L1 + L2 组合（chunk 位块上传 + 显示 shader 查色与描边），网格真相留在 CPU，涂色走分区并行。** 理由：

1. **实测总预算够**：涂色并行 1.0 + 调色板并行 0.8 + 位块上传 0.8 + MultiMesh 整块 0.14 ≈ **2.7 ms**，16.67 ms 预算下约 6 倍余量（见 5.8）；
2. **全部可断点调试**——compute 里没有断点、没有 `Log`，只能回读 buffer，对本项目这种"Plinko + 5 种武器 + 多队混战"的复杂度，这个代价容易被低估；
3. **不需要原子计数与异步回读**——领土数、排行、胜负判定都是内存里读一下；
4. **关键：它是增量可升级的。** 将来实体数翻到 3~5 万时，只需把"涂色"这**一个函数**换成 compute dispatch，网格纹理与显示 shader 都不用动；反之若一上来就 L3，得先啃完 RenderingDevice 样板才能看到第一个像素。

**⚠️ 一条硬前提：涂色并行必须做「无冲突分区」。** 按实体分片并行能 8.5× 提速，但同一格被两线程并发写会丢更新（`owner` 与 `strength` 可能变成互相矛盾的组合）。正确做法是**按网格区域切分线程**，只并行处理 bbox 完全落在本区域内的实体，跨界实体走串行（10k 实体下串行余量很小）。

---

### 5.8 实测结果

> 复现：`bench/Benchmark.cs` + `bench/Benchmark.tscn`，原始输出 `bench/results.txt`。
> 环境：Godot 4.7-stable / D3D12 / Forward+ / RTX 4060 Laptop / 16 逻辑核 / 1000×1000 网格 / 10,000 实体。

```powershell
dotnet build
Godot_v4.7-stable_mono_win64_console.exe --path <项目> res://bench/Benchmark.tscn
```

**[A] 全量上传 4 MB**

| 项 | 耗时 | 帧预算 |
| --- | --- | --- |
| `Image.SetData(byte[4MB])` | 0.75 ms | 4.5% |
| `ImageTexture.Update()` | 0.94 ms | 5.7% |
| 合计（现实路径） | **~1.7 ms** | **~10%** |

**[B] 调色板转换（100 万格 → 4 MB RGBA）**

| 项 | 耗时 | 帧预算 |
| --- | --- | --- |
| 顺序 | 3.28 ms | 19.7% |
| `Parallel.For` 分行 | **0.82 ms** | 4.9% |
| 基线：纯 4 MB `BlockCopy` | 0.14 ms | 0.8% |

**[C] 局部位块更新（16×16 = 256 块，每块 64×64）**

| 脏块比例 | Gather 拷贝 | staging Update | TextureCopy | **合计** | 相对全量 |
| --- | --- | --- | --- | --- | --- |
| 25% | 0.14 ms | 0.07 ms | 0.03 ms | **0.28 ms** | 0.11x |
| 50% | 0.14 ms | 2.40 ms | 0.02 ms | **0.51 ms** | 0.20x |
| 100% | 0.30 ms | 3.70 ms | 0.07 ms | **0.83 ms** | 0.33x |
| 对照：整张 `TextureUpdate(4MB)` | — | — | — | 2.48 ms | 1.00x |

正确性：读回抽样 132 点，**不一致 0 点**；`Texture2Drd` 可正常挂到 `Sprite2D`。

**[D] 10,000 颗球涂色（85% r=3 / 15% r=10，速度 1.5 格/帧，含完整侵蚀模型）**

| 项 | 耗时 | 帧预算 |
| --- | --- | --- |
| **D1 偏移表 stamp（推荐做法）** | **8.48 ms** | **50.9%** |
| D2 扫过段 bbox + 距离测试 | 18.29 ms | 109.7% |
| D3 只写 owner 的最简圆盘（成本下限） | 5.43 ms | 32.6% |
| **D4 偏移表 stamp 并行（吞吐上限）** | **1.00 ms** | **6.0%** |

**[E] 10,000 个 MultiMesh 实例每帧更新**

| 项 | 耗时 | 帧预算 |
| --- | --- | --- |
| E1 逐实例 `SetInstanceTransform2D`+`SetInstanceColor` | 1.91 ms | 11.5% |
| **E2 整块写 `Buffer`** | **0.14 ms** | **0.8%** |

步长实测 **12 float/实例**（2D 变换 8 + 颜色 4）→ 468 KB/帧。

**四条修正（推翻本文档此前写下的判断）**

1. **~~godot#76994 的 16~38 ms~~ 不复现。** 4 MB 全量上传实测 ≈ **1.7 ms**，之前"可能吃掉整个帧预算"的警告撤销。
2. **`TextureUpdate` 在 Godot 4.7 不能传子区域。** 实测签名只有 `TextureUpdate(Rid, layer, byte[])` / `(Rid, layer, ReadOnlySpan<byte>)`。局部更新的唯一路径是 **staging 纹理 + `TextureCopy`**，实测这条路径**又对又便宜**（100% 脏时 0.83 ms，比整张 `TextureUpdate` 的 2.48 ms 还快，单次大更新疑似要付更重的 barrier）。C# 类型名是 **`Texture2Drd`**（属性 `TextureRdRid`）。
3. **"扫过段涂色"比"整圆盘偏移表"更贵（18.3 vs 8.5 ms）** —— 与直觉相反。因为当**速度(1.5 格/帧) << 半径(3~10)** 时包围盒由半径主导，扫过段省不下面积，却要付逐像素点到线段距离的代价。**只有 速度 >> 半径 时才该用扫过段。**
4. **"CPU 有 200 倍余量"只在参考实现规模成立。** 10,000 颗实体 + 完整侵蚀模型 = **8.48 ms**，单线程扛不住；去掉侵蚀逻辑只要 5.43 ms → **侵蚀的四分支判断本身值约 3 ms**。并行 8.5× 后 **1.00 ms**。

**60 FPS 预算表（10k 实体 + 100 万格）**

| 项 | 实测 |
| --- | --- |
| 涂色（并行、分区无冲突） | ~1.0 ms |
| 调色板转换（并行，只做脏区） | 0.2 ~ 0.8 ms |
| 位块上传（chunk，最坏 100% 脏） | 0.83 ms |
| MultiMesh 整块写 Buffer | 0.14 ms |
| **已知项合计** | **~2.2 ~ 2.8 ms** |
| 实体物理（10k + 空间哈希） | 未测，M1 补测 |

**结论：10k 实体 + 100 万格，CPU 并行 + chunk 位块更新可以稳稳 60 FPS，compute 不是必需。** 它会**在实体数继续翻到 3~5 万时变成下一个杠杆** —— 按 11.8 ns/格 推算，届时涂色即便并行也要 5 ms 以上，叠加实体的空间哈希与碰撞，compute 才真正划算。

**两条不可违背的实现细节**

- **MultiMesh 必须整块写 `Buffer`**：逐实例写要 1.91 ms，整块写只要 0.14 ms（**13.5×**）。
- **调色板转换必须 `Parallel.For`**：顺序 3.28 → 并行 0.82 ms（4x，受内存带宽限制，16 核也上不去线性）。

---

## 6. 里程碑建议（修正版）

| 阶段 | 目标 | 验收标准 |
| --- | --- | --- |
| **M0** | ✅ **已完成**：数据通路实测 | `bench/Benchmark.cs` 已产出全部关键数字（见 5.8）→ **路线确定：L1 chunk 位块更新 + L2 显示 shader + 分区并行涂色，不上 compute** |
| **M1** | 单位层跑通 | 1 万颗球 SoA 积分 + 空间哈希碰撞，稳定 60 FPS；球能沿途涂色并扣能量 |
| **M2** | 钉板与门 | Plinko 弹珠落进 ×2 / Release 区，正确触发炮台弹药变化与子弹发射 |
| **M3** | 战斗闭环 | 子弹转化敌格、球体碰撞吞并、炮台/球归零死亡、最后存活判定 |
| **M4** | 上 100 万格 | 1000×1000 + 2 万粒子稳定 60 FPS（脏矩形 + 分区） |
| **M5** | 表现层 | 调色板 shader、边界描边、拖尾/爆炸/震屏、倍速与时间轴、排行榜 |
| **M6** | 定制化 | 每位玩家 512×512 自定义头像/配色（对齐商店版的卖点） |

---

## 7. 待你确认的点

1. **做哪一种变体**：经典炮台版（弹珠走钉板 + 炮台齐射，规则最清晰）／滚动涂色版（球在战场上滚、沿途染色、碰撞吞噬，观感更"海量粒子"）／两者融合（球滚着涂色，同时有 Multiply/Release 增益门）。
2. **地图规模**：直接上 1000×1000，还是先 256×256 把玩法调爽再放大？（成本 1→16 的时间曲线在 100 万格和 6.5 万格上手感完全不同）
3. **是否有玩家操作**：纯观赏（选颜色、加速、重开）还是可干预（手动 Release、指定攻击方向）？

---

## 8. 参考资料

**题材与规则**
- [Physion: Multiply or Release 是什么](https://physion.net/blog/multiply-or-release-in-physion)（起源 MIKAN 2021、机制概述、四色版本说明）
- [maybe-raven/multiply-or-release](https://github.com/maybe-raven/multiply-or-release) · [itch.io 规则页](https://maybe-raven.itch.io/multiply-or-release)（**权威规则文本**）
- [Marble Race and Territory War（Google Play）](https://play.google.com/store/apps/details?id=marble.race.territory.war.multiply.or.release&hl=zh)（滚动涂色 + 体积消耗 + 碰撞吞噬规则）
- [Algodoo Wiki](https://algodoo.fandom.com/wiki/Multiply_or_Release) · [Marble Kingdoms Wiki](https://marble-kingdoms.fandom.com/wiki/Multiply_or_Release)（历史与系列）
- [Massive 500x500 Territory War](https://www.youtube.com/watch?v=1cTbETc5bmU)（分区 + 一次多格 的工程折中）

**中文"百万像素"变体**
- [233乐园 S1 EP51 规则详解](https://www.233leyuan.com/post-detail/2066115955610001408)（1000×1000、每格 1→16 能量、每 3 分钟 +1）
- [233乐园 第二十季](https://www.233leyuan.com/post-detail/2075607960217792512) · [第二十一季](https://www.233leyuan.com/post-detail/2077035218908430336)（固定 16 能量/格）
- [B站 EP33](https://www.bilibili.com/video/BV11N9yBKEyk/)（`[App] Unity`、MarbleRace 标签）· [MarblePi 系列](https://www.bilibili.com/video/BV1xFetzdEYw/)

**工程参考**
- [techstay/marble-territory-war](https://github.com/techstay/marble-territory-war)（自研粒子/物理引擎 + 模块划分参考）
- [Ryan4G/phaser3-typescript-marblerace](https://github.com/Ryan4G/phaser3-typescript-marblerace)
- [OpenFrontIO GameMap.ts](https://raw.githubusercontent.com/openfrontio/openfrontio/main/src/core/game/GameMap.ts)（平面位域数组 + 直传纹理的布局；**AGPL-3.0，仅借鉴思想**）
- 空间哈希：[Gorillasun 教程](https://www.gorillasun.de/blog/particle-system-optimization-grid-lookup-spatial-hashing/) · [Carmen Cincotti](https://carmencincotti.com/2022-10-31/spatial-hash-maps-part-one/) · [2 万粒子 120FPS 实测讨论](https://news.ycombinator.com/item?id=40809010)
- Godot：[MultiMeshInstance2D](https://docs.godotengine.org/en/stable/classes/class_multimeshinstance2d.html) · [RenderingDevice](https://docs.godotengine.org/en/4.4/classes/class_renderingdevice.html) · [ImageTexture](https://docs.godotengine.org/en/4.4/classes/class_imagetexture.html) · [局部更新提案 #4017](https://github.com/godotengine/godot-proposals/discussions/4017) · [逐像素写入慢的实测](https://godotforums.org/d/22104-writing-dynamically-inside-texture-fastest-alternative-to-setpixel)

> **检索途径说明**：按全局指令优先使用 `anysearch` skill（`batch_search` / `extract`），全程未回退到 `web_search`。
> `fandom.com` 系（territorial / algodoo / marble-kingdoms）的 `extract` 多次被反爬拒绝，其内容以搜索摘要为准；
> 各参考实现的 LICENSE 文件未能稳定取到，采用前请自行确认授权。

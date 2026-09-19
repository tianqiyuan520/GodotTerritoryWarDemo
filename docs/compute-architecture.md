# 计算着色器架构（弹珠领土战争）

> 本文承接 [research-territory-war.md](./research-territory-war.md)。那份文档的 5.7/5.8 节论证了"CPU 也够用"，
> 但**决策最终改为使用 compute shader**（理由见 §1）。本文只讲"用 compute 怎么做"，并把参考项目里可直接复用的部分提取出来。
>
> 参考项目：`E:\GODOT\Project\ComputeShaderBattleSimulation`（Godot 4.7 C# + compute，同引擎同 GPU）

---

## 1. 决策记录：为什么改了

| | |
| --- | --- |
| **结论** | **使用 compute shader**，领土网格常驻显存，涂色在 GPU 完成，CPU 不再上传纹理 |
| **触发** | 你指出"可能子弹多" —— 子弹是**短命且高并发**的，1 万同时在场只是常态，峰值远高于此。CPU 方案在 10k 实体上单线程已 8.48 ms（50.9% 帧预算），峰值没有余量 |
| **我之前反对的核心理由，已被参考项目消除** | 我最大的顾虑是"RenderingDevice 样板会让你在看不到第一个像素之前先啃几天"。但 `ComputeShaderBattleSimulation` **已经把整套样板跑通了**（同引擎、同 GPU），可以直接移植而不是从零摸索 |
| **仍然存在、但现在只是"要处理"而非"要避免"的三个代价** | ① 领土计数需要 GPU 原子计数 + **低频**异步回读 ② GLSL 里没有断点 ③ 设备重置时全部 RID 失效，要能重建 |

**决策改变的诚实说明**：CPU 路线的实测数字本身没错（2.8 ms 总计、约 6 倍余量），错的是我对"代价"的权重估计 —— 我把样板成本估成了一次性沉没的大山，而实际上你已经有现成的实现可以抄。参考项目还把"sim 只占 GPU 时间 1%"这个事实摆出来了，说明**算力根本不是这个项目的约束**。

---

## 2. 参考项目的性能基线（同 GPU：RTX 4060 Laptop）

| 项 | 数值 |
| --- | --- |
| 单位数 | 1,048,576（2²⁰） |
| 模拟步 | **~1.2 ms/步 @ 8Hz** → 每秒约 12 ms GPU |
| InstanceBake | ~0.48 ms/帧 |
| MultiMesh 1M 实例绘制 | ~1.06 ms/帧 |
| 显存 | ~80 MB |
| **时间分布** | **sim ~1%，渲染 ~99%** |

**对本品类的推论（重要）**：

- 我们的实体只有 **1 万**，是参考项目的 1/100。**模拟会小到可以忽略**（0.1 ms 量级）。
- 所以我们**不是为了"算力"上 compute**，而是为了**把整条纹理上传路径删掉** —— 领土网格常驻显存后，再也没有 4 MB 调色板转换、没有 chunk 上传、没有脏矩形维护。这才是真正的收益。
- 反过来说：**别指望 compute 让画面变快**。参考项目在 1M 单位时 GPU 已经被渲染路径吃满（99%），我们的渲染压力（1 万实例 + 1 张贴图）小得多，但这是两回事。

---

## 3. 可直接移植的 Godot compute 样板（C# 侧清单）

以下全部来自参考项目已验证的写法：

```csharp
// ── 拿到设备 ──
RenderingDevice _rd = RenderingServer.GetRenderingDevice();   // Forward+ / Mobile 才有；Compatibility 返回 null

// ── 加载着色器 → 管线 ──
var shaderFile = GD.Load<RDShaderFile>("res://Shaders/Compute/Paint.glsl");
var spirV  = shaderFile.GetSpirV();
Rid shader   = _rd.ShaderCreateFromSpirV(spirV);
Rid pipeline = _rd.ComputePipelineCreate(shader);

// ── 缓冲 ──
Rid buf = _rd.StorageBufferCreate((uint)bytes.Length, bytes);
_rd.BufferUpdate(buf, 0, (uint)newBytes.Length, newBytes);

// ── Uniform（StorageBuffer / Image 两种）──
RDUniform StorageUniform(Rid b, int binding) => new RDUniform {
    UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding }.AddId(b);
RDUniform ImageUniform(Rid t, int binding) => new RDUniform {
    UniformType = RenderingDevice.UniformType.Image, Binding = binding }.AddId(t);

Rid set = _rd.UniformSetCreate(uniforms, shader, 0);   // set=0

// ── 调度：整步放进一个 ComputeList ──
long list = _rd.ComputeListBegin();
_rd.ComputeListBindComputePipeline(list, pipelineA);
_rd.ComputeListBindUniformSet(list, set, 0);
_rd.ComputeListDispatch(list, groupsA, 1, 1);

_rd.ComputeListSetPushConstant(list, pushBytes, (uint)pushBytes.Length);  // 免重建 buffer
_rd.ComputeListBindComputePipeline(list, pipelineB);
_rd.ComputeListBindUniformSet(list, set, 0);
_rd.ComputeListDispatch(list, groupsB, 1, 1);
_rd.ComputeListEnd();
```

**六条必须记住的规则**

1. **同一个 ComputeList 内，Dispatch 之间 Godot 自动插 barrier** —— 前一个 pass 的写入对后一个 pass 立即可见，**不要手动管理同步**。这是它能把 200+ 个 dispatch 塞进一个 list 的原因（每次 CPU-GPU 往返 0.1~1 ms，213 次就是 20~200 ms，完全不可接受）。
2. **固定时间步要用两个 ComputeList**：逻辑步可能一帧跑 0~N 次，渲染步只跑一次。`ComputeListEnd()` 之后的结果对下一个 list 可见。
3. **共用同一个 Uniform Set 的所有 shader，layout 必须完全一致** —— 用一份 `Common.gdshaderinc` 强制统一。哪怕某个 pass 用不到前几个 binding，也要 include 它。
4. **Push Constant 传频繁变化的小参数**（相机、level 等），不要每帧重建 Uniform Buffer。
5. **显示走 `Texture2Drd` 零拷贝**：`new Texture2Drd { TextureRdRid = texRid }` → `sprite.Texture = ...`，`TextureFilter = Nearest`（线性过滤会把亮像素与背景插值变暗）。
6. **必须能重建全部 GPU 资源**：窗口切换 / 驱动重置会让所有 RID 失效。

**GLSL 侧约定**

```glsl
#[compute]                       // ← 这两个头不能少
#version 450
layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;
#include "../Include/Common.gdshaderinc"     // ← Godot 的 include，不支持嵌套！

layout(std430, binding = 0) restrict buffer UnitBuf { Unit units[]; } unitBuf;
layout(r32ui,   binding = 4)          uniform uimage2D territory;   // 可读可写整数像
```

**GPU 计时**：`RenderingDevice.CaptureTimestamp` + `GetCapturedTimestampGpuTime`（渲染帧阶段可捕获）；
算逻辑步用 `_rd.Sync()` 把 `RunSimulationLogic()` 包起来测（参考项目就是这么定位出"渲染占 99%"的）。

---

## 4. 本项目的 compute 管线设计

### 4.1 GPU 资源布局

| 资源 | 类型 | 尺寸 | 用途 |
| --- | --- | --- | --- |
| `territoryTex` | `r32ui` image2D | 1000×1000 × 4 B = **4 MB** | **权威领土状态**：每 texel 打包 `owner(12) \| strength(16) \| flags(4)`。Usage: `StorageBit \| SamplingBit` |
| `unitsBuf` | StorageBuffer | 实体 SoA/AoS，10k+ 起步，可扩到 100k | 子弹/弹珠：pos, prevPos, vel, radius, team, energy, life |
| `attackAccumBuf` | StorageBuffer (`uint`) | 100 万 × 4 B（按格累加）或稀疏 | 两趟方案的中间累加器（见 4.3） |
| `countersBuf` | StorageBuffer (`uint`) | 势力数 × 4 B | 每势力领土计数，原子累加 |
| `paramsBuf` | StorageBuffer | 几百字节 | 时间、每格成本曲线、相机、网格尺寸 |

**打包成 `r32ui` 而不是 owner/strength 两张纹理**，原因：整数单像素才能用 `imageAtomic*`（含 `imageAtomicCompSwap`），这是 GPU 上做"读-改-写"的唯一正确手段。解包只在显示 shader 里做一次。

### 4.2 每个逻辑步的 Pass（全部塞进一个 ComputeList）

```
Pass 1  EntityIntegrate   实体积分 + 边界反弹 + 能量/寿命
Pass 2  Hash              空间哈希（若做球球碰撞吞噬才需要）
Pass 3  计数排序 + Indices （参考项目已用计数排序取代双调排序，见其 Sort/ 目录）
Pass 4  TerritoryPaint    实体 → 领土 texel（见 4.3 的两种方案）
Pass 5  EntityResolve     碰撞吞噬（大球吃小球）——若 Pass 2/3 做了才需要
────────────── 渲染步（另一个 ComputeList）──────────────
Pass 6  InstanceBake      直写 MultiMesh 的 Buffer（1 万实例）
Pass 7  Display           由 canvas_item shader 完成，不是 compute
```

### 4.3 领土涂色的两种方案（关键决策）

**方案 A：CAS 原子（实体驱动）**

每颗弹珠遍历自己的圆盘偏移表，对每个 texel 用 `imageAtomicCompSwap` 循环套用侵蚀规则：

```glsl
uint old = imageLoad(territory, p).r;
while (true) {
    uint neu = erode(old, team, power);          // 四分支侵蚀规则
    uint prev = imageAtomicCompSwap(territory, p, old, neu);
    if (prev == old) break;
    old = prev;                                   // 被别的线程抢先改了，重试
}
```

- ✅ 只处理**真正被涂到的格子**，没有全图 pass
- ✅ 天然无冲突，语义与 CPU 版完全一致
- ⚠️ 同一格被大量实体同时命中时 CAS 会重试（不过在弹珠题材里热点不严重）

**方案 B：两趟（先累加、后结算）**

```
Pass 4a  PaintAccum：imageAtomicAdd 把攻击力按 (格, 队) 累加
Pass 4b  CellResolve：一格一线程（100 万线程），读累加值，**无冲突地**跑侵蚀规则
```

- ✅ **正确性最容易保证**（每格恰好一个线程写）
- ✅ 白送"每格独立规则" —— 强度自然衰减、跨格扩散、火势蔓延，都是顺手的事
- ✅ 这正是参考项目"先 Hash+Sort、再一格/一单位结算"的思路
- ⚠️ 每步要过一遍全图 100 万格。但**在 GPU 上这很便宜**：参考项目跑 100 万单位 × 上百次浮点也只要 1.2 ms，而这里每格只是一次读 + 一次写 + 几条分支，量级在 **0.1~0.3 ms**

**推荐：先从方案 B 起步**（正确性优先，且白送每格规则），把方案 A 作为后续优化。两者可以共存 —— 显示层和数据结构完全一样，只是 Pass 4 的写法不同。

### 4.4 领土计数与回读（必须处理的代价）

HUD 领土数、排行、胜负判定都需要每队格数。

- 方案 B 的 `CellResolve` 里顺便 `atomicAdd(counters[team], 1u)` / `atomicSub`（换主时）
- **绝不能每帧回读** —— 回读会打断 GPU 流水线。用 `BufferGetDataAsync` **低频**（每秒 2~4 次）取回，HUD 用插值显示即可
- 别用 `_rd.Sync()` 做常规回读，那会把流水线排空

### 4.5 显示层

- `territoryTex` 挂到 `Texture2Drd` → 一个 `Sprite2D`/`TextureRect`，配 `map.gdshader`
- shader 里：解包 owner → 查调色板 LUT → 颜色；采样上下左右 4 邻居 → **边界描边**；strength 可映射成明暗（刚打下来的格子偏亮）
- 相机缩放走 `paramsBuf` 的 push constant，**nearest 过滤保留像素颗粒感** —— 这正是"百万像素"的观感来源

---

## 5. 保留 CPU 路径的价值

不要因为改用 compute 就把 CPU 实现删掉。参考项目自己就保留了整个 `CPUBattle/` 目录做 CPU/GPU 对照。CPU 版有三个用途：

1. **正确性验证**：同样的侵蚀规则跑两边，逐格比对。compute 里没有断点，这是唯一可靠的调试手段。
2. **黄金基线**：性能对比时知道"GPU 到底快了多少"。
3. **降级路径**：Compatibility/GLES3（含 Web）没有 compute，CPU 版是唯一的退路。

我们 bench 里已经实测好的数字（`bench/results.txt`）就是这条 CPU 路径的起点。

---

## 6. 踩坑清单（GLSL 与 Godot 特有）

| 坑 | 后果 | 对策 |
| --- | --- | --- |
| **GLSL 越界读写** | 未定义行为 / GPU 崩溃（WGSL 会自动兜底，GLSL 不会） | 每次索引都要手动 clamp/检查 |
| **`normalize(vec2(0))`** | 未定义（GLSL 除零） | 先判 `length > 0` |
| **`#include` 不支持嵌套** | 编译失败 | 写成扁平的包含顺序，依赖关系写在注释里 |
| 忘了 `#[compute]` / `#version 450` | 编译失败 | 每个 compute shader 头两行固定 |
| 非整数 image 上用 `imageAtomic*` | 编译失败 | 原子操作只能用 `r32i` / `r32ui` |
| 声明成 `writeonly` 却要读 | 读不到数据 | 需要读就不能 `writeonly` |
| Uniform Set layout 与参考 shader 不一致 | 绑定错乱或崩溃 | 用一份 `Common.gdshaderinc` 统一 |
| 每个 Dispatch 各起一个 ComputeList | CPU-GPU 往返累积，掉到几十 ms | 一个逻辑步一个 ComputeList |
| 每帧 `Sync()` / 回读 | 流水线排空，帧率崩 | 原子计数 + 低频异步回读 |
| 设备重置后继续用旧 RID | 崩溃 / 黑屏 | 实现 `RecreateGpuResources()` |

---

## 7. 里程碑（compute 版修订）

| 阶段 | 目标 | 验收标准 |
| --- | --- | --- |
| **M0** ✅ | 数据通路实测 | 已完成，见 `bench/`（`results.txt`） |
| **M1** | **GPU 管线骨架** | `territoryTex` 创建成功 → 一个 `CellResolve`-style pass 写入 → `Texture2Drd` 显示出地图。**先不做实体物理**，用假数据把"compute → 显存纹理 → 屏幕"这条链路打通 |
| **M2** | 实体层 + 涂色 | 1 万实体（SoA + 整块 MultiMesh Buffer）滚动涂色；两趟方案跑通；侵蚀规则与 CPU 版逐格一致 |
| **M3** | 碰撞与战斗 | 空间哈希 + 计数排序 + 球体吞噬；炮台/子弹（按玩法变体取舍） |
| **M4** | 计数与 HUD | 原子计数 + 低频异步回读；领土数/排行/胜负判定 |
| **M5** | 显示层 | 调色板 shader、边界描边、攻击波、strength 明暗、倍速与时间轴 |
| **M6** | 规模与对照 | 实体翻到 3~10 万验证缩放；与 CPU 路径逐格比对；GPU 计时接入（CaptureTimestamp） |

**M1 是关键一步**：它把"compute → 显存纹理 → 屏幕"这条最容易踩坑的链路先打通，后面所有 pass 都是往这个骨架上挂。

---

## 8. 参考项目里值得单独读的文件

| 文件 | 价值 |
| --- | --- |
| `docs/01-architecture.md` | 整体架构 + 数据流 + 关键设计决策 |
| `docs/02-compute-pipeline.md` | **最核心**：RenderingDevice、ComputeList、Uniform Set、Push Constant、barrier、完整流程图 |
| `docs/03-spatial-hashing.md` | 空间哈希为什么能把 O(n²) 降到 O(n) |
| `docs/06-godot-implementation.md` | C# 样板 + GLSL 坑（越界 / normalize / include）+ 固定时间步与插值 |
| `docs/14-performance-analysis.md` | 实测定位"渲染 99% / sim 1%"的方法与结论 |
| `Shaders/Compute/Simulation/ClearAll.glsl` | 最短的完整 compute pass 范例（50 行，含 struct/binding/边界检查） |
| `Shaders/Compute/Render/ProjectileUpdate.glsl` | 扫掠命中检测（线段最近距离）—— 我们的子弹命中格子的同款数学 |
| `Scripts/BattleSimulation.cs` | GPU 资源管理 / 调度 / 设备重建的大型实战参考（174 KB） |
| `CPUBattle/` 目录 + `docs/CPU-百万同屏-*.md` | CPU/GPU 对照实现与对比方法 |

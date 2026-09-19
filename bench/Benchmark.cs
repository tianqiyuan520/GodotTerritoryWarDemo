using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// 决策用最小基准测试：只测「数据通路」的真实代价，不含任何游戏逻辑。
///
/// 运行（先 build 后跑）：
///   dotnet build
///   Godot_v4.7-stable_mono_win64_console.exe --path &lt;项目目录&gt; res://bench/Benchmark.tscn
///
/// 它回答五个问题：
///   A. 1000x1000 全量上传到底多贵？（验证 godot#76994 的 16~19ms 是否真实）
///   B. 调色板转换在 C# 里要多少毫秒？并行能省多少？
///   C. Godot 4.7 的 TextureUpdate 不支持子区域 → 局部位块更新必须走
///      「staging 纹理 + TextureCopy」，这条路值不值？
///   D. 10000 颗球「扫过段涂色」vs「整圆盘重涂」，CPU 各要多少毫秒？
///   E. 10000 个 MultiMesh 实例：逐实例 SetInstance* vs 整块写 Buffer
/// </summary>
public partial class Benchmark : Node2D
{
    // ---- 规模（对齐百万像素目标）----
    private const int GridW = 1000;
    private const int GridH = 1000;
    private const int ChunkSize = 64;      // 1000 不是 64 的整数倍，末块会变窄，正好一并测掉
    private const int EntityCount = 10_000;

    private const int Warmup = 3;
    private const int Iterations = 20;

    // ---- 模拟真实数据布局：两字节/格 ----
    private byte[] _rgba = null!;          // 显示缓冲 RGBA8，4 MB
    private byte[] _strength = null!;      // 每格防御强度
    private ushort[] _owner = null!;       // owner 12bit + 标志
    private uint[] _palette = null!;       // owner -> packed RGBA
    private byte[] _scratchCopy = null!;   // 纯拷贝基线用

    // ---- 实体（10000 颗球）----
    private float[] _ex = null!, _ey = null!, _epx = null!, _epy = null!, _er = null!;
    private byte[] _eteam = null!;

    // ---- GPU 资源 ----
    private Image _image = null!;
    private ImageTexture _imageTexture = null!;
    private readonly Dictionary<(int, int), byte[]> _chunkBufs = new();
    private readonly Dictionary<(int, int), Rid> _stagingTex = new();
    private RenderingDevice _rd;
    private Rid _rdTexture = default;
    private Texture2Drd _texture2Drd;

    // ---- 输出 ----
    private readonly List<string> _lines = new();
    private Sprite2D _sprite = null!;
    private Label _label = null!;
    private readonly Random _rng = new(20260815);

    public override void _Ready()
    {
        SetupDisplay();
        SetupData();

        var sw = Stopwatch.StartNew();

        Report($"Godot {Engine.GetVersionInfo()["string"]} | 适配器 {RenderingServer.GetVideoAdapterName()} | 逻辑核心 {OS.GetProcessorCount()}");
        Report($"网格 {GridW}x{GridH} = {GridW * GridH:N0} 格 | chunk {ChunkSize} | 实体 {EntityCount:N0}");
        Report(new string('-', 94));

        RunPartA_FullUpload();
        RunPartB_Palette();
        RunPartC_ChunkUpload();
        RunPartD_PaintSegments();
        RunPartE_MultiMesh();

        sw.Stop();
        Report(new string('-', 94));
        Report($"总计 {sw.Elapsed.TotalSeconds:F1} s");
        Report("");
        Report("解读：");
        Report("  A3 > 5ms      → 全量上传(P0)不可用，必须局部位块更新或 L3 网格常驻");
        Report("  B2 < 1ms      → 调色板转换不是问题；B1-B3 的差 = LUT 查找本身的成本");
        Report("  C  若 C5 << C4 → 「staging+TextureCopy」值得做；否则宁可直接全量上传");
        Report("  D1 < 6ms      → 10000 颗球涂色 CPU 扛得住；D3 是去掉侵蚀逻辑后的成本下限");
        Report("  D4 加速比      → 并行能救回多少（但同格并发写会丢更新，需权衡）");
        Report("  E2 << E1      → MultiMesh 必须整块写 Buffer，绝不逐实例 SetInstance*");

        Flush();
        CallDeferred(nameof(QuitSoon));
    }

    private async void QuitSoon()
    {
        await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        GetTree().Quit();
    }

    // =====================================================================
    // 显示与数据准备
    // =====================================================================

    private void SetupDisplay()
    {
        _sprite = new Sprite2D { Centered = false, TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
        AddChild(_sprite);
        _sprite.Position = new Vector2(8, 8);

        _label = new Label { Position = new Vector2(8, 8) };
        _label.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_label);
    }

    private void SetupData()
    {
        int cells = GridW * GridH;
        _rgba = new byte[cells * 4];
        _scratchCopy = new byte[cells * 4];
        _strength = new byte[cells];
        _owner = new ushort[cells];
        _palette = new uint[4096];

        _palette[0] = Pack(28, 30, 36);   // owner 0 = 中立深灰
        var teamColors = new (byte r, byte g, byte b)[]
        {
            (33, 229, 62), (36, 152, 242), (251, 20, 31), (247, 216, 21),
            (168, 85, 247), (236, 72, 153), (34, 211, 238), (249, 115, 22),
        };
        for (int i = 0; i < 8; i++)
        {
            var c = teamColors[i];
            _palette[1 + i] = Pack(c.r, c.g, c.b);
        }

        // 8 个圆形势力领地 + 中立，让画面不是噪声
        for (int y = 0; y < GridH; y++)
        {
            for (int x = 0; x < GridW; x++)
            {
                int owner = 0;
                for (int t = 0; t < 8; t++)
                {
                    float cx = GridW * (0.18f + 0.28f * (t % 3));
                    float cy = GridH * (0.18f + 0.30f * (t / 3));
                    float dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy < 110f * 110f) { owner = 1 + t; break; }
                }
                int i = y * GridW + x;
                _owner[i] = (ushort)owner;
                _strength[i] = (byte)(owner == 0 ? 0 : 120);
            }
        }
        PaletteSequential();

        _image = Image.CreateEmpty(GridW, GridH, false, Image.Format.Rgba8);
        UploadImageData();
        _imageTexture = ImageTexture.CreateFromImage(_image);

        _ex = new float[EntityCount]; _ey = new float[EntityCount];
        _epx = new float[EntityCount]; _epy = new float[EntityCount];
        _er = new float[EntityCount]; _eteam = new byte[EntityCount];
        BuildDiskTables();
        for (int e = 0; e < EntityCount; e++)
        {
            float x = (float)(_rng.NextDouble() * GridW);
            float y = (float)(_rng.NextDouble() * GridH);
            _ex[e] = x; _ey[e] = y;
            _epx[e] = x - 1.5f; _epy[e] = y;      // 速度 1.5 格/帧
            _er[e] = _rng.NextDouble() < 0.85 ? 3f : 10f;   // 多数小球，少数大球
            _eteam[e] = (byte)(1 + _rng.Next(8));
        }
    }

    private static uint Pack(byte r, byte g, byte b)
        => (uint)(r | (g << 8) | (b << 16) | (255 << 24));

    /// <summary>Godot 4.7 的 Image.SetData 需要显式 w/h/mipmaps/format。</summary>
    private void UploadImageData()
        => _image.SetData(GridW, GridH, false, Image.Format.Rgba8, _rgba);

    private void Report(string line)
    {
        _lines.Add(line);
        GD.Print(line);
        if (_label != null)
        {
            int from = Math.Max(0, _lines.Count - 48);
            _label.Text = string.Join("\n", _lines.GetRange(from, _lines.Count - from));
        }
    }

    private void Flush()
    {
        using var f = FileAccess.Open("user://bench_results.txt", FileAccess.ModeFlags.Write);
        f?.StoreString(string.Join("\n", _lines) + "\n");
        GD.Print($"[结果已写入] {ProjectSettings.GlobalizePath("user://bench_results.txt")}");
    }

    private double Bench(string name, Action body, int iterations = Iterations)
    {
        for (int i = 0; i < Warmup; i++) body();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) body();
        sw.Stop();

        double ms = sw.Elapsed.TotalMilliseconds / iterations;
        Report($"{name,-46} {ms,9:F3} ms   ({(ms / 16.667 * 100),5:F1}% 帧预算)");
        return ms;
    }

    // =====================================================================
    // A. 全量上传
    // =====================================================================

    private void RunPartA_FullUpload()
    {
        Report("[A] 全量上传 1000x1000 RGBA8 = 4 MB");
        Bench("A1 Image.SetData(byte[4MB])", UploadImageData);
        Bench("A2 ImageTexture.Update()", () => _imageTexture.Update(_image));
        Bench("A3 SetData+Update 合计(现实路径)", () =>
        {
            TouchSomeCells();
            PaletteSequential();
            UploadImageData();
            _imageTexture.Update(_image);
        });
        Report("");
    }

    // =====================================================================
    // B. 调色板转换
    // =====================================================================

    private void PaletteSequential()
    {
        ushort[] o = _owner;
        uint[] pal = _palette;
        byte[] d = _rgba;
        for (int i = 0, j = 0; i < o.Length; i++, j += 4)
        {
            uint c = pal[o[i]];
            d[j] = (byte)c;
            d[j + 1] = (byte)(c >> 8);
            d[j + 2] = (byte)(c >> 16);
            d[j + 3] = (byte)(c >> 24);
        }
    }

    private void PaletteParallel()
    {
        ushort[] o = _owner;
        uint[] pal = _palette;
        byte[] d = _rgba;
        int h = GridH, w = GridW;
        int stripes = Math.Max(1, OS.GetProcessorCount());
        int rowsPer = (h + stripes - 1) / stripes;
        Parallel.For(0, stripes, s =>
        {
            int y0 = s * rowsPer;
            int y1 = Math.Min(h, y0 + rowsPer);
            for (int y = y0; y < y1; y++)
            {
                int i = y * w;
                int j = i * 4;
                for (int x = 0; x < w; x++, i++, j += 4)
                {
                    uint c = pal[o[i]];
                    d[j] = (byte)c;
                    d[j + 1] = (byte)(c >> 8);
                    d[j + 2] = (byte)(c >> 16);
                    d[j + 3] = (byte)(c >> 24);
                }
            }
        });
    }

    private void RunPartB_Palette()
    {
        Report("[B] 调色板转换（100 万格 -> 4 MB RGBA）");
        Bench("B1 顺序", PaletteSequential);
        Bench("B2 Parallel.For 分行", PaletteParallel);
        Bench("B3 基线：纯 4MB BlockCopy", () => Buffer.BlockCopy(_rgba, 0, _scratchCopy, 0, _rgba.Length));
        Report("");
    }

    // =====================================================================
    // C. 局部位块更新（Godot 4.7 的 TextureUpdate 无区域参数 → staging + TextureCopy）
    // =====================================================================

    private bool InitRenderingDevice()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) return false;

        var fmt = new RDTextureFormat
        {
            Width = GridW,
            Height = GridH,
            Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
            TextureType = RenderingDevice.TextureType.Type2D,
            Samples = RenderingDevice.TextureSamples.Samples1,
            Mipmaps = 1,
            ArrayLayers = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                      | RenderingDevice.TextureUsageBits.CanUpdateBit
                      | RenderingDevice.TextureUsageBits.CanCopyFromBit
                      | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _rdTexture = _rd.TextureCreate(fmt, new RDTextureView());
        if (!_rdTexture.IsValid) return false;

        // 首次全量填充
        _rd.TextureUpdate(_rdTexture, 0, _rgba);

        _texture2Drd = new Texture2Drd { TextureRdRid = _rdTexture };
        return true;
    }

    private int _chunksX, _chunksY;

    private byte[] GetChunkBuffer(int w, int h)
    {
        var key = (w, h);
        if (!_chunkBufs.TryGetValue(key, out var buf))
        {
            buf = new byte[w * h * 4];
            _chunkBufs[key] = buf;
        }
        return buf;
    }

    /// <summary>每种 chunk 尺寸配一张 staging 纹理（只建一次）。</summary>
    private Rid GetStagingTexture(int w, int h)
    {
        var key = (w, h);
        if (_stagingTex.TryGetValue(key, out var rid)) return rid;

        var fmt = new RDTextureFormat
        {
            Width = (uint)w,
            Height = (uint)h,
            Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
            TextureType = RenderingDevice.TextureType.Type2D,
            Samples = RenderingDevice.TextureSamples.Samples1,
            Mipmaps = 1,
            ArrayLayers = 1,
            UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit
                      | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        rid = _rd!.TextureCreate(fmt, new RDTextureView());
        _stagingTex[key] = rid;
        return rid;
    }

    /// <summary>把 rgba 里某个 chunk 的行拷进紧凑缓冲（真实实现必须付这个 CPU 拷贝成本）。</summary>
    private void GatherChunk(int cx, int cy)
    {
        int x0 = cx * ChunkSize, y0 = cy * ChunkSize;
        int w = Math.Min(ChunkSize, GridW - x0);
        int h = Math.Min(ChunkSize, GridH - y0);
        byte[] buf = GetChunkBuffer(w, h);
        int off = 0;
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(_rgba, ((y0 + y) * GridW + x0) * 4, buf, off, w * 4);
            off += w * 4;
        }
    }

    /// <summary>staging 纹理整体更新（每次只传 16KB 级别）。</summary>
    private void StageChunk(int cx, int cy)
    {
        int x0 = cx * ChunkSize, y0 = cy * ChunkSize;
        int w = Math.Min(ChunkSize, GridW - x0);
        int h = Math.Min(ChunkSize, GridH - y0);
        _rd!.TextureUpdate(GetStagingTexture(w, h), 0, GetChunkBuffer(w, h));
    }

    /// <summary>staging -> 大纹理的区域拷贝（这就是 Godot 4.7 唯一的部分更新手段）。</summary>
    private void CopyChunk(int cx, int cy)
    {
        int x0 = cx * ChunkSize, y0 = cy * ChunkSize;
        int w = Math.Min(ChunkSize, GridW - x0);
        int h = Math.Min(ChunkSize, GridH - y0);
        _rd!.TextureCopy(
            GetStagingTexture(w, h), _rdTexture,
            Vector3.Zero, new Vector3(x0, y0, 0), new Vector3(w, h, 1),
            0, 0, 0, 0);
    }

    private void ForDirtyChunks(double ratio, Action<int, int> fn)
    {
        int total = _chunksX * _chunksY;
        int want = Math.Max(1, (int)(total * ratio));
        int step = Math.Max(1, total / want);
        int done = 0;
        for (int cy = 0; cy < _chunksY && done < want; cy++)
            for (int cx = 0; cx < _chunksX && done < want; cx++)
            {
                if (((cy * _chunksX + cx) % step) != 0) continue;
                fn(cx, cy);
                done++;
            }
    }

    private void RunPartC_ChunkUpload()
    {
        _chunksX = (GridW + ChunkSize - 1) / ChunkSize;
        _chunksY = (GridH + ChunkSize - 1) / ChunkSize;
        Report($"[C] 局部位块更新（{_chunksX}x{_chunksY}={_chunksX * _chunksY} 块，每块 {ChunkSize}x{ChunkSize}）");
        Report("    注意：Godot 4.7 的 TextureUpdate 无 x/y/w/h 参数 → 只能 staging 纹理 + TextureCopy");

        if (!InitRenderingDevice())
        {
            Report("  ⚠ 跳过：RenderingServer.GetRenderingDevice() 不可用或纹理创建失败");
            Report("    （请用 Forward+/Mobile + Vulkan/D3D12 运行）");
            Report("");
            return;
        }

        double full = Bench("C0 对照：整张 TextureUpdate(4MB)", () => _rd!.TextureUpdate(_rdTexture, 0, _rgba));

        foreach (double ratio in new[] { 0.25, 0.50, 1.00 })
        {
            string tag = $"{(int)(ratio * 100)}% 脏块";
            Bench($"C1 Gather 拷贝      {tag}", () => ForDirtyChunks(ratio, GatherChunk));
            Bench($"C2 staging Update   {tag}", () => ForDirtyChunks(ratio, StageChunk));
            Bench($"C3 TextureCopy      {tag}", () => ForDirtyChunks(ratio, CopyChunk));
            double total = Bench($"C4 三步合计         {tag}", () => ForDirtyChunks(ratio, (cx, cy) =>
            {
                GatherChunk(cx, cy);
                StageChunk(cx, cy);
                CopyChunk(cx, cy);
            }));
            Report($"    → 相对全量 C0 的比值：{total / full:F2}x");
        }
        Report("");

        // 正确性验证：读回大纹理，抽样比对
        try
        {
            byte[] back = _rd!.TextureGetData(_rdTexture, 0);
            int mismatches = 0, sampled = 0;
            for (int y = 0; y < GridH; y += 97)
                for (int x = 0; x < GridW; x += 89)
                {
                    int i = (y * GridW + x) * 4;
                    sampled++;
                    for (int k = 0; k < 4; k++)
                        if (back[i + k] != _rgba[i + k]) { mismatches++; break; }
                }
            Report($"  验证 TextureCopy 正确性：抽样 {sampled} 点，不一致 {mismatches} 点");
        }
        catch (Exception ex)
        {
            Report($"  验证读回失败：{ex.Message}");
        }

        try
        {
            _sprite.Texture = _texture2Drd;
            Report("  验证 Texture2Drd 已挂到 Sprite2D（无异常）");
        }
        catch (Exception ex)
        {
            Report($"  验证 Texture2Drd 挂载失败：{ex.Message}");
        }
        Report("");
    }

    // =====================================================================
    // D. 10000 颗球的涂色：四种实现的真实代价
    // =====================================================================

    private int[][] _diskDx = null!, _diskDy = null!;

    /// <summary>预计算圆盘偏移表（设计文档里推荐的"半径上限 + 偏移表"做法）。</summary>
    private void BuildDiskTables()
    {
        _diskDx = new int[17][];
        _diskDy = new int[17][];
        for (int r = 1; r <= 16; r++)
        {
            var dxs = new List<int>();
            var dys = new List<int>();
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r) { dxs.Add(dx); dys.Add(dy); }
            _diskDx[r] = dxs.ToArray();
            _diskDy[r] = dys.ToArray();
        }
    }

    /// <summary>D1：预计算偏移表直接 stamp。没有距离测试，没有除法。</summary>
    private void PaintDiskTable()
    {
        for (int e = 0; e < EntityCount; e++)
            PaintOneDiskTable(e);
    }

    private void PaintOneDiskTable(int e)
    {
        int r = (int)_er[e];
        if (r < 1) r = 1;
        if (r > 16) r = 16;
        int ix = (int)_ex[e], iy = (int)_ey[e];
        byte team = _eteam[e];
        int[] dxs = _diskDx[r], dys = _diskDy[r];
        ushort[] o = _owner;
        byte[] st = _strength;

        for (int k = 0; k < dxs.Length; k++)
        {
            int x = ix + dxs[k], y = iy + dys[k];
            if ((uint)x >= (uint)GridW || (uint)y >= (uint)GridH) continue;
            int i = y * GridW + x;
            ushort cur = o[i];
            if (cur == team) { if (st[i] < 210) st[i] += 1; }
            else if (cur == 0) { o[i] = team; st[i] = 180; }
            else if (st[i] <= 1) { o[i] = team; st[i] = 14; }
            else { st[i] -= 1; }
        }
    }

    /// <summary>D2：扫过段（bbox + 点到线段距离），已预除 len2 去掉逐像素除法。</summary>
    private void PaintSweptSegments()
    {
        ushort[] o = _owner;
        byte[] st = _strength;
        int w = GridW, h = GridH;
        for (int e = 0; e < EntityCount; e++)
        {
            float x0 = _epx[e], y0 = _epy[e], x1 = _ex[e], y1 = _ey[e];
            float r = _er[e];
            byte team = _eteam[e];

            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1) - r));
            int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(x0, x1) + r));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, y1) - r));
            int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(y0, y1) + r));
            if (minX > maxX || minY > maxY) continue;

            float dx = x1 - x0, dy = y1 - y0;
            float len2 = dx * dx + dy * dy;
            float invLen2 = len2 > 1e-6f ? 1f / len2 : 0f;   // 关键：除法移到循环外
            float r2 = r * r;

            for (int y = minY; y <= maxY; y++)
            {
                int row = y * w;
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float t = ((px - x0) * dx + (py - y0) * dy) * invLen2;
                    t = t < 0f ? 0f : (t > 1f ? 1f : t);
                    float qx = x0 + t * dx - px, qy = y0 + t * dy - py;
                    if (qx * qx + qy * qy > r2) continue;

                    int i = row + x;
                    ushort cur = o[i];
                    if (cur == team) { if (st[i] < 210) st[i] += 1; }
                    else if (cur == 0) { o[i] = team; st[i] = 180; }
                    else if (st[i] <= 1) { o[i] = team; st[i] = 14; }
                    else { st[i] -= 1; }
                }
            }
        }
    }

    /// <summary>D3：只写 owner 的最简整圆盘（没有侵蚀逻辑，作为成本下限基线）。</summary>
    private void PaintSimpleDisks()
    {
        ushort[] o = _owner;
        int w = GridW, h = GridH;
        for (int e = 0; e < EntityCount; e++)
        {
            int cx = (int)_ex[e], cy = (int)_ey[e];
            int r = (int)_er[e];
            byte team = _eteam[e];
            int minX = Math.Max(0, cx - r), maxX = Math.Min(w - 1, cx + r);
            int minY = Math.Max(0, cy - r), maxY = Math.Min(h - 1, cy + r);
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * w;
                int dy = y - cy;
                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dy * dy > r * r) continue;
                    int i = row + x;
                    if (o[i] != team) o[i] = team;
                }
            }
        }
    }

    /// <summary>D4：D1 的并行版（按实体分片）。注意：同一格被并发写会有丢更新，这里只测吞吐上限。</summary>
    private void PaintDiskTableParallel()
    {
        int stripes = Math.Max(1, OS.GetProcessorCount());
        int per = (EntityCount + stripes - 1) / stripes;
        Parallel.For(0, stripes, s =>
        {
            int e0 = s * per;
            int e1 = Math.Min(EntityCount, e0 + per);
            for (int e = e0; e < e1; e++) PaintOneDiskTable(e);
        });
    }

    private void RunPartD_PaintSegments()
    {
        Report($"[D] {EntityCount:N0} 颗球涂色（85% r=3 / 15% r=10，速度 1.5 格/帧，含完整侵蚀模型）");
        double d1 = Bench("D1 偏移表 stamp（推荐做法）", PaintDiskTable);
        Bench("D2 扫过段 bbox+距离", PaintSweptSegments);
        Bench("D3 只写 owner 的最简圆盘(成本下限)", PaintSimpleDisks);
        double d4 = Bench("D4 偏移表 stamp 并行(吞吐上限)", PaintDiskTableParallel);
        Report($"    → 并行加速比 {d1 / d4:F1}x（代价：同格并发写会丢更新）");
        Report("");
    }

    // =====================================================================
    // E. 10000 个实例的渲染更新
    // =====================================================================

    private MultiMesh _mm;
    private MultiMeshInstance2D _mmi;
    private float[] _mmBuf = null!;
    private int _bufferStride;

    private void RunPartE_MultiMesh()
    {
        Report($"[E] {EntityCount:N0} 个 MultiMesh 实例每帧更新");
        if (!InitMultiMesh())
        {
            Report("  ⚠ 跳过：MultiMesh 初始化失败");
            Report("");
            return;
        }

        Bench("E1 逐实例 SetInstanceTransform2D+Color", () =>
        {
            for (int i = 0; i < EntityCount; i++)
            {
                _mm!.SetInstanceTransform2D(i, new Transform2D(0f, new Vector2(_ex[i], _ey[i])));
                _mm.SetInstanceColor(i, new Color(1, 1, 1, 1));
            }
        });

        Bench("E2 整块写 Buffer(推荐)", UpdateBufferWhole);

        Report($"  （实测步长 = {_bufferStride} float/实例 → {EntityCount * _bufferStride * 4 / 1024} KB/帧）");
        Report("");
    }

    private bool InitMultiMesh()
    {
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = false,
            Mesh = new QuadMesh { Size = new Vector2(6, 6) },
            InstanceCount = EntityCount,
        };
        var probe = _mm.Buffer;                  // 读出真实步长，不靠猜
        if (probe.Length == 0 || probe.Length % EntityCount != 0) return false;
        _bufferStride = probe.Length / EntityCount;
        _mmBuf = new float[EntityCount * _bufferStride];

        _mmi = new MultiMeshInstance2D { Multimesh = _mm, Position = new Vector2(8, 8) };
        AddChild(_mmi);
        UpdateBufferWhole();
        return true;
    }

    private void UpdateBufferWhole()
    {
        int s = _bufferStride;
        float[] b = _mmBuf;
        for (int i = 0; i < EntityCount; i++)
        {
            int o = i * s;
            float x = _ex[i], y = _ey[i];
            // Transform2D 在 buffer 里的前 8 个 float
            b[o + 0] = 1f; b[o + 1] = 0f; b[o + 2] = 0f; b[o + 3] = x;
            b[o + 4] = 0f; b[o + 5] = 1f; b[o + 6] = 0f; b[o + 7] = y;
            if (s >= 12)
            {
                b[o + s - 4] = 1f; b[o + s - 3] = 1f; b[o + s - 2] = 1f; b[o + s - 1] = 1f;
            }
        }
        _mm!.Buffer = b;
    }

    // =====================================================================
    // 辅助：制造真实噪声（避免走"数据没变"的优化路径）
    // =====================================================================

    private int _tick;
    private void TouchSomeCells()
    {
        _tick++;
        for (int n = 0; n < 20_000; n++)
        {
            int i = (n * 7919 + _tick * 104729) % _owner.Length;
            _owner[i] = (ushort)(1 + ((n + _tick) % 8));
        }
    }
}

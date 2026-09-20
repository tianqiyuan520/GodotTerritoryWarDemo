using Godot;
using TerritoryWar.Game;

namespace TerritoryWar;

/// <summary>
/// 把模拟状态推给场景：领土贴图、能量数字、右侧面板。
/// 基地材质在 <see cref="BaseVisuals.Update"/> 里写，弹珠层在 <see cref="BallMeshWriter.Write"/> 里写，
/// 钉板由它自己的 <c>Render</c> 写 —— 这个文件只管"地图那一层 + 两块面板"。
/// </summary>
public partial class GameRoot
{
    /// <summary>右侧面板的刷新间隔（秒）。占比是人眼跟不上的数字，没必要每帧重排。</summary>
    const double PanelRefreshSeconds = 0.2;

    /// <summary>
    /// 把整张领土图查表写成 RGBA 像素，再整张上传给贴图（每帧一次）。
    ///
    /// 逐格转换是并行的（每个输出像素只依赖自己那个格子），但**上传只能在主线程**：
    /// 实测过子区域更新那条路（staging 纹理 + TextureCopy）在这里是亏的，见 docs/tuning-log.md §1。
    /// </summary>
    void UploadTerritoryPixels()
    {
        Palette.WriteTerritoryPixels(_map.Owner, _lut, _mapPixels, _map.Width, _map.Height);
        _mapImage.SetData(_map.Width, _map.Height, false, Image.Format.Rgba8, _mapPixels);
        _mapTexture.Update(_mapImage);
    }

    /// <summary>把能量数字对到能量最高的若干颗球上（交给场景里的 <see cref="EnergyLabels"/> 自己排）。</summary>
    void UpdateEnergyLabels()
    {
        Labels?.Present(_swarm, EnergyLabelMaxCount, EnergyLabelMinEnergy, MapSize);
    }

    /// <summary>
    /// 界面刷新：只有右侧领土面板需要定期更新（0.2 秒一次）。
    ///
    /// ⚠ 这里**没有** FPS / 逐项耗时那些调试文字了 —— 按需求删掉了（数据都在 docs/tuning-log.md 里）。
    ///   要临时看性能就自己插一个 Stopwatch 探针，别再挂回屏幕 HUD。
    /// ⚠ 钉板的重画也不在这里（它要每帧画，见 <c>_Process</c> 末尾）。
    /// </summary>
    void UpdateUi(double frameDelta)
    {
        _uiTimer += frameDelta;
        if (_uiTimer < PanelRefreshSeconds)
        {
            return;
        }

        _uiTimer = 0;
        Territory?.UpdateFrom(_map, _teams, TeamCount);
    }
}

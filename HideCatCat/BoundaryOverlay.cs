using System.Numerics;
using Dalamud.Plugin;
using Pictomancy;

namespace HideCatCat;

/// <summary>
/// 使用 PctService (Pictomancy) 在世界空间中绘制躲猫猫边界范围圈。
/// 仅一条干净的边界线，贴合地形。
/// </summary>
internal sealed class BoundaryOverlay : IDisposable
{
    private readonly PctContext _pctContext;
    private readonly Func<bool> _isActive;
    private readonly Func<Vector3> _getCenter;
    private readonly Func<float> _getRadius;

    private const uint LineColor = 0xCC_0080FF;       // 亮橙色边界线
    private const uint CircleSegments = 128;
    private const float LineThickness = 2.0f;

    private static readonly PctDxParams LineParams = new()
    {
        OccludedAlpha = 0.4f,       // 被遮挡时保留 40% 不透明度
        OcclusionTolerance = 0.4f,  // 容忍 0.4 yalms 深度差（贴合地面）
        FadeStart = 60f,            // 60 yalms 外开始淡出
        FadeStop = 100f,            // 100 yalms 完全消失
    };

    public BoundaryOverlay(
        IDalamudPluginInterface pluginInterface,
        Func<bool> isActive,
        Func<Vector3> getCenter,
        Func<float> getRadius)
    {
        _pctContext = PctService.Initialize(pluginInterface);
        _isActive = isActive;
        _getCenter = getCenter;
        _getRadius = getRadius;
    }

    public void Draw()
    {
        if (!_isActive()) return;

        var center = _getCenter();
        var radius = _getRadius();
        if (radius <= 0) return;

        using var drawList = PctService.Draw();
        if (drawList == null) return;

        drawList.AddCircle(
            center, radius,
            LineColor,
            CircleSegments, LineThickness,
            LineParams);
    }

    public void Dispose()
    {
        _pctContext.Dispose();
    }
}

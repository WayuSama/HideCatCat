using Dalamud.Configuration;

namespace HideCatCat;

[Serializable]
public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool IsOpen { get; set; } = true;

    // WebSocket 服务器地址
    public string ServerUrl { get; set; } = "wss://your-server:port/ws";

    // 悬浮 HUD 位置（屏幕百分比 0~1）
    public float OverlayX { get; set; } = 0.5f;
    public float OverlayY { get; set; } = 0.3f;
}

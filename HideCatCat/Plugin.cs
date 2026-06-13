using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using HideCatCat.Windows;

namespace HideCatCat;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private const string CommandName = "/hidecatcat";

    private readonly IDalamudPluginInterface _pi;
    private readonly ITargetManager _target;
    private readonly IPartyList _party;
    private readonly IClientState _client;
    private readonly IObjectTable _objects;
    private readonly IPlayerState _playerState;
    private readonly IFramework _framework;
    private readonly ICommandManager _command;
    private readonly IPluginLog _log;
    private readonly INamePlateGui _namePlateGui;
    private readonly ITextureProvider _textureProvider;

    public static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem _windowSystem;
    private readonly MainWindow _mainWindow;
    private readonly PluginConfig _config;
    private readonly GameClient _gameClient;
    /// <summary>管理头顶名牌的隐藏/恢复，仅在游戏中且猫队时启用。</summary>
    private readonly NamePlateHider _namePlateHider;

    /// <summary>当前选中目标的检测结果。</summary>
    public TargetInfo CurrentTarget { get; } = new();

    /// <summary>本地玩家名称（从游戏客户端获取）</summary>
    public string LocalPlayerName => _objects.LocalPlayer?.Name.TextValue ?? "";

    /// <summary>本地玩家坐标</summary>
    public Vector3 LocalPlayerPosition => _objects.LocalPlayer?.Position ?? Vector3.Zero;

    /// <summary>玩家原服（创建角色时所在服务器）</summary>
    public string HomeWorldName => _playerState.HomeWorld.ValueNullable?.Name.ToString() ?? "";

    /// <summary>当前所在服务器（跨服后取实际所在服）</summary>
    public string CurrentWorldName => _objects.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString() ?? HomeWorldName;

    /// <summary>当前地图 ID（TerritoryType）</summary>
    public uint CurrentTerritoryId => _client.TerritoryType;

    /// <summary>WebSocket 服务器地址（持久化）</summary>
    public string ServerUrl
    {
        get => _config.ServerUrl;
        set { _config.ServerUrl = value; _pi.SavePluginConfig(_config); }
    }

    /// <summary>自定义服务器地址历史（持久化，不含默认地址）</summary>
    public List<string> ServerHistory => _config.ServerHistory;

    /// <summary>添加自定义地址到历史（去重，持久化）</summary>
    public void AddServerToHistory(string url)
    {
        _config.ServerHistory.Remove(url);
        _config.ServerHistory.Insert(0, url);
        _pi.SavePluginConfig(_config);
    }

    /// <summary>HUD 编辑模式（持久化）</summary>
    public bool OverlayEditMode
    {
        get => _config.OverlayEditMode;
        set { _config.OverlayEditMode = value; _pi.SavePluginConfig(_config); }
    }

    private IGameObject? _lastTarget;

    // HUD 拖拽状态机
    private bool _dragging;
    private Vector2 _dragOffset;

    // 边界蒙版径向渐变纹理
    private IDalamudTextureWrap? _boundaryGradientTexture;
    private float _cachedBoundaryPower = -1f;

    public Plugin(
        IDalamudPluginInterface pi,
        ITargetManager target,
        IPartyList party,
        IClientState client,
        IObjectTable objects,
        IPlayerState playerState,
        IFramework framework,
        ICommandManager command,
        IPluginLog log,
        INamePlateGui namePlateGui,
        ITextureProvider textureProvider)
    {
        _pi = pi;
        _target = target;
        _party = party;
        _client = client;
        _objects = objects;
        _playerState = playerState;
        _framework = framework;
        _command = command;
        _log = log;
        // 注入 INamePlateGui，用于修改游戏原生头顶名牌
        _namePlateGui = namePlateGui;
        _textureProvider = textureProvider;
        Log = log;

        _config = pi.GetPluginConfig() as PluginConfig ?? new PluginConfig();

        _gameClient = new GameClient();
        // 创建名牌隐藏控制器，后续根据游戏状态和阵营决定是否启用
        _namePlateHider = new NamePlateHider(_namePlateGui, _objects);

        _windowSystem = new WindowSystem("HideCatCat");
        _mainWindow = new MainWindow(this, _gameClient);
        _mainWindow.IsOpen = _config.IsOpen;
        _windowSystem.AddWindow(_mainWindow);
    }

    public Task LoadAsync(CancellationToken ct)
    {
        _pi.UiBuilder.Draw += _windowSystem.Draw;
        _pi.UiBuilder.Draw += DrawOverlay;

        _command.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Hide Cat Cat window"
        });

        _framework.Update += OnFrameworkUpdate;
        // 在插件安装器中显示"打开"按钮，点击后打开主窗口
        _pi.UiBuilder.OpenMainUi += OnOpenMainUi;

        _log.Info("HideCatCat loaded");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // 释放边界蒙版纹理
        _boundaryGradientTexture?.Dispose();
        // 释放名牌隐藏控制器，取消事件订阅并恢复名牌显示
        _namePlateHider.Dispose();
        _pi.UiBuilder.OpenMainUi -= OnOpenMainUi;
        _framework.Update -= OnFrameworkUpdate;
        _pi.UiBuilder.Draw -= _windowSystem.Draw;
        _pi.UiBuilder.Draw -= DrawOverlay;
        _command.RemoveHandler(CommandName);

        _gameClient.Dispose();

        _mainWindow.IsOpen = false;
        _windowSystem.RemoveAllWindows();

        _config.IsOpen = _mainWindow.IsOpen;
        _pi.SavePluginConfig(_config);

        _log.Info("HideCatCat unloaded");
    }

    // 插件安装器中点击"打开"按钮时触发，与 /hidecatcat 命令行为一致
    private void OnOpenMainUi() => _mainWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        _mainWindow.Toggle();
    }

    /// <summary>屏幕上绘制可拖拽的游戏 HUD 或队友距离。</summary>
    private void DrawOverlay()
    {
        var io = ImGui.GetIO();

        // 游戏进行中 → 显示躲猫猫 HUD
        if (_mainWindow.OverlayVisible)
        {
            DrawBoundaryMask();

            var gx = _config.GameOverlayX;
            var gy = _config.GameOverlayY;
            DrawDraggableOverlay(_mainWindow.OverlayText, ColorWithDist(_mainWindow.NearestCatDistance), ref gx, ref gy);
            _config.GameOverlayX = gx;
            _config.GameOverlayY = gy;
            return;
        }

        // 非游戏状态 → 显示选中队友距离
        if (!CurrentTarget.IsTeammate) return;
        var dist = CurrentTarget.Distance;
        var t = $"{dist:F1} yalms";
        var ox = _config.OverlayX;
        var oy = _config.OverlayY;
        DrawDraggableOverlay(t, ColorWithDist(dist), ref ox, ref oy);
        _config.OverlayX = ox;
        _config.OverlayY = oy;
    }

    /// <summary>边界蒙版：鼠队距边缘 ≤20 yalms 时径向渐变（中心透明→边缘红）</summary>
    private void DrawBoundaryMask()
    {
        if (!_mainWindow.BoundaryWarning) return;

        // 透明范围变化时重建纹理
        var power = _config.BoundaryMaskPower;
        if (Math.Abs(power - _cachedBoundaryPower) > 0.01f)
        {
            _boundaryGradientTexture?.Dispose();
            _boundaryGradientTexture = CreateRadialGradientTexture(256, power);
            _cachedBoundaryPower = power;
        }

        if (_boundaryGradientTexture == null) return;

        // 距边界越近 → 蒙版越浓（10 yalms 时几乎透明，0 yalms 时全红）
        var dist = _mainWindow.DistanceToBoundary;
        var alpha = Math.Clamp(1f - dist / 10f, 0f, 1f);

        var draw = ImGui.GetForegroundDrawList();
        var screenPos = Vector2.Zero;
        var screenSize = ImGui.GetIO().DisplaySize;

        draw.AddImage(
            _boundaryGradientTexture.Handle,
            screenPos,
            screenPos + screenSize,
            Vector2.Zero,
            Vector2.One,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0f, 0f, alpha)));
    }

    /// <summary>生成径向渐变 RGBA 纹理（中心透明 → 边缘不透明）。</summary>
    private IDalamudTextureWrap CreateRadialGradientTexture(int size, float sliderPower)
    {
        // 滑块 0→100 映射到 power 0.3→5.0
        var p = 0.3f + sliderPower / 100f * 4.7f;

        var bytes = new byte[size * size * 4];
        var cx = size / 2f;
        var cy = size / 2f;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                var t = Math.Clamp(dist / maxDist, 0f, 1f);
                var alpha = (byte)(Math.Pow(t, p) * 255);

                var i = (y * size + x) * 4;
                bytes[i + 0] = 255; // B
                bytes[i + 1] = 255; // G
                bytes[i + 2] = 255; // R
                bytes[i + 3] = alpha; // A
            }
        }

        var specs = RawImageSpecification.Bgra32(size, size);
        return _textureProvider.CreateFromRaw(specs, bytes, "HideCatCatBoundaryGradient");
    }

    private static uint ColorWithDist(float dist) => dist switch
    {
        < 10 => 0xFF_0000FF,  // 红
        < 20 => 0xFF_00FFFF,  // 黄
        < 30 => 0xFF_FF8080,  // 淡蓝
        _    => 0xFF_00FF00,  // 绿
    };

    private void DrawDraggableOverlay(string text, uint color, ref float configX, ref float configY)
    {
        if (string.IsNullOrEmpty(text))
        {
            _dragging = false;
            return;
        }

        var io = ImGui.GetIO();
        var draw = ImGui.GetForegroundDrawList();
        var editMode = _config.OverlayEditMode;

        var lines = text.Split('\n');
        var lineH = ImGui.GetTextLineHeight() + 2;
        var totalW = 0f;
        foreach (var line in lines) { var w = ImGui.CalcTextSize(line).X; if (w > totalW) totalW = w; }
        var totalH = lines.Length * lineH;
        var pad = 6f;

        var x = io.DisplaySize.X * configX;
        var y = io.DisplaySize.Y * configY;
        var pos = new Vector2(x, y);

        var bgMin = pos - new Vector2(pad, pad);
        var bgMax = pos + new Vector2(totalW + pad, totalH + pad);

        // ── 命中检测 ──
        var mp = io.MousePos;
        var hovered = mp.X >= bgMin.X && mp.X <= bgMax.X
                   && mp.Y >= bgMin.Y && mp.Y <= bgMax.Y;

        if (editMode)
        {
            // ── 编辑模式：拖动状态机 ──
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _dragging = true;
                _dragOffset = mp - pos;
            }

            if (_dragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var newPos = mp - _dragOffset;
                configX = Math.Clamp(newPos.X / io.DisplaySize.X, 0f, 1f);
                configY = Math.Clamp(newPos.Y / io.DisplaySize.Y, 0f, 1f);
            }

            if (_dragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                _dragging = false;
                _pi.SavePluginConfig(_config);
            }

            // 拦截鼠标（编辑模式：hover 或拖拽中都要拦截）
            if (hovered || _dragging)
                io.WantCaptureMouse = true;

            // ── 编辑模式视觉 ──
            var borderColor = (hovered || _dragging) ? 0xFF00FFFF : 0x80FFFFFF;
            draw.AddRect(bgMin, bgMax, borderColor, 4f);

            var bgAlpha = (hovered || _dragging) ? 0xAA000000u : 0x66000000u;
            draw.AddRectFilled(bgMin, bgMax, bgAlpha, 4f);

            // 左上角拖动提示
            var hintText = _dragging ? " 释放以放置 " : " 拖动以调整位置 ";
            var hintSize = ImGui.CalcTextSize(hintText);
            draw.AddRectFilled(
                bgMin,
                bgMin + new Vector2(hintSize.X + 8, hintSize.Y + 4),
                0xCC000000, 4f);
            draw.AddText(bgMin + new Vector2(4, 2), 0xFF_FFFF00, hintText);
        }
        else
        {
            // ── 正常模式：仅显示，不响应鼠标 ──
            _dragging = false;
            draw.AddRectFilled(bgMin, bgMax, 0x88000000, 4f);
        }

        // ── 文字（两种模式都绘制）──
        var lp = pos;
        foreach (var line in lines)
        {
            draw.AddText(lp + Vector2.One, 0xCC_000000, line);
            draw.AddText(lp, color, line);
            lp.Y += lineH;
        }
    }

    /// <summary>每帧检测：① 根据游戏状态和阵营切换名牌隐藏 ② 目标变化时更新队友距离信息。</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        // 猫队 + 游戏进行中 → 启用名牌隐藏；否则 → 禁用
        if (_mainWindow.IsGameStarted && _mainWindow.IsCatTeam)
            _namePlateHider.Enable();
        else
            _namePlateHider.Disable();

        var target = _target.Target;
        if (target == _lastTarget) return;
        _lastTarget = target;

        if (target == null)
        {
            CurrentTarget.Reset();
            return;
        }

        CurrentTarget.Name = target.Name.TextValue;
        CurrentTarget.ObjectKind = target.ObjectKind.ToString();
        CurrentTarget.HasTarget = true;

        var isTeammate = false;

        if (target is ICharacter ch)
        {
            if (ch.StatusFlags.HasFlag(StatusFlags.PartyMember) ||
                ch.StatusFlags.HasFlag(StatusFlags.AllianceMember))
                isTeammate = true;
        }

        if (!isTeammate)
        {
            foreach (var member in _party)
            {
                if (member.EntityId == target.EntityId)
                {
                    isTeammate = true;
                    break;
                }
            }
        }

        CurrentTarget.IsTeammate = isTeammate;

        if (isTeammate)
        {
            var player = _objects.LocalPlayer;
            if (player != null)
            {
                CurrentTarget.Distance = Vector3.Distance(player.Position, target.Position);
            }
        }
    }
}

/// <summary>当前目标检测结果。</summary>
public sealed class TargetInfo
{
    public bool HasTarget { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ObjectKind { get; set; } = string.Empty;
    public bool IsTeammate { get; set; }
    public float Distance { get; set; }

    public void Reset()
    {
        HasTarget = false;
        Name = string.Empty;
        ObjectKind = string.Empty;
        IsTeammate = false;
        Distance = 0;
    }
}

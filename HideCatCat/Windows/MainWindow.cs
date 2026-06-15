using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace HideCatCat.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;
    private readonly GameClient _gameClient;
    private readonly GameClient _lobbyClient;

    // UI state
    private string _password = "";
    private string _playerName = "";
    private string _selectedTeam = "";
    private bool _hasJoined;
    private float _radius = 50f;
    private string _winCondition = "ALL";
    private int _winCount = 1;
    private int _timeLimitMin = 5;
    private string _errorMessage = "";
    private string _roomServer = "";
    private uint _roomTerritoryId;

    private const string DefaultServerUrl = "wss://hidecatcat.19730123.xyz:19174/ws";

    // 关于/设置面板折叠状态
    private bool _showAbout;
    private bool _showSettings;

    // 自定义服务器地址输入缓冲区
    private string _customUrlInput = "";

    private static string ServerDisplayName(string url) =>
        url == DefaultServerUrl ? "默认躲猫猫服务器地址" : url;

    // Game state (from server)
    private readonly object _playersLock = new();
    private List<PlayerInfo> _players = new();
    private string _gameState = "WAITING";
    private string _hostName = "";
    private bool _settingsLocked;
    private bool _gameStarted;
    private DateTime _gameStartTime;
    private int _timeLimitSec;
    private double _startX, _startY, _startZ;
    private float _boundaryRadius = 50f;
    private string _lastEvent = "";
    private bool _gameOver;
    private int _catWins;
    private int _mouseWins;

    private bool _showLobby = true;  // 默认展开大厅
    private bool _lobbyPendingConnect;  // 大厅连接进行中，避免重复连接
    private string _lobbyError = "";
    private readonly List<PublicRoomEntry> _publicRooms = new();
    private readonly List<JoinResultEntry> _joinResults = new();
    private readonly List<JoinRequestEntry> _joinRequests = new();
    private bool _isPublicRoom;
    private string _appliedRoom = "";

    public MainWindow(Plugin plugin, GameClient gameClient) : base("Hide Cat Cat")
    {
        _plugin = plugin;
        _gameClient = gameClient;
        _lobbyClient = new GameClient();

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(350, 280),
            MaximumSize = new Vector2(500, 700),
        };

        _gameClient.OnMessage += OnServerMessage;
        _gameClient.OnConnectionChanged += OnConnectionChanged;
        _gameClient.OnError += OnError;

        _lobbyClient.OnMessage += OnLobbyMessage;
        _lobbyClient.OnConnectionChanged += OnLobbyConnectionChanged;
        _lobbyClient.OnError += OnLobbyError;
    }

    /// <summary>游戏是否正在进行中。供 Plugin.OnFrameworkUpdate 判断是否启用名牌隐藏。</summary>
    public bool IsGameStarted => _gameStarted;
    /// <summary>当前玩家是否选择猫队。仅在猫队时启用名牌隐藏，避免鼠队暴露自己位置。</summary>
    public bool IsCatTeam => _selectedTeam == "CAT";
    /// <summary>游戏悬浮 HUD 数据，供 Plugin.DrawOverlay 读取（仅鼠队显示）</summary>
    public bool OverlayVisible => _gameStarted && _selectedTeam == "MOUSE";
    public string OverlayText { get; private set; } = "";
    /// <summary>最近猫的距离（鼠队HUD着色用）</summary>
    public float NearestCatDistance { get; private set; }
    /// <summary>鼠队距边界剩余距离（yalms），负值=已越界</summary>
    public float DistanceToBoundary
    {
        get
        {
            if (!_gameStarted || _selectedTeam != "MOUSE") return float.MaxValue;
            var dist = Math.Sqrt(
                Math.Pow(_plugin.LocalPlayerPosition.X - _startX, 2) +
                Math.Pow(_plugin.LocalPlayerPosition.Y - _startY, 2) +
                Math.Pow(_plugin.LocalPlayerPosition.Z - _startZ, 2));
            return _boundaryRadius - (float)dist;
        }
    }

    /// <summary>边界警告：鼠队距边缘 ≤10 yalms 时触发，供 Plugin 绘制蒙版</summary>
    public bool BoundaryWarning => DistanceToBoundary <= 10f;

    public override void Draw()
    {
        // 顶栏：连接状态 + 按钮
        if (_gameClient.IsConnected)
        {
            if (!string.IsNullOrEmpty(_hostName))
            {
                var roomName = $"{_hostName}@{_roomServer} - {_plugin.CurrentTerritoryName}";
                ImGui.TextColored(new Vector4(0.2f, 0.9f, 0.3f, 1f), $"● 已进入房间 | {roomName}");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.2f, 0.9f, 0.3f, 1f), "● 已进入房间");
            }
            ImGui.SameLine();
            if (ImGui.Button("断开")) _ = _gameClient.DisconnectAsync();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), "● 未进入房间");
        }

        // 大厅状态（仅在未进入房间时显示）
        if (!_gameClient.IsConnected)
        {
            ImGui.SameLine();
            string lobbyText;
            bool lobbyClickable = false;
            
            if (_lobbyClient.IsConnected)
            {
                lobbyText = _showLobby ? " | 大厅已连（点击关闭）" : " | 大厅已连（点击展开）";
                lobbyClickable = true;
            }
            else if (_lobbyPendingConnect)
            {
                lobbyText = " | 大厅连接中...";
            }
            else
            {
                lobbyText = " | 大厅已连（点击展开）";
                lobbyClickable = true;
            }
            
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 1f), lobbyText);
            var itemSize = ImGui.GetItemRectSize();
            ImGui.SetCursorScreenPos(cursor);
            if (lobbyClickable && ImGui.InvisibleButton("##LobbyToggle", itemSize))
            {
                _showLobby = !_showLobby;
                if (!_showLobby)
                {
                    _lobbyPendingConnect = false;
                    _ = _lobbyClient.DisconnectAsync();
                }
            }
        }

        // 圆形 ? 按钮（紧挨连接状态）
        ImGui.SameLine();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 999f);
        if (ImGui.SmallButton("?")) _showAbout = !_showAbout;
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("关于躲猫猫");

        ImGui.Separator();
        if (_gameStarted && _selectedTeam == "MOUSE" && BoundaryWarning)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.2f, 0.2f, 1f));
            ImGui.TextWrapped($"⚠ 接近边界！距边缘还有 {DistanceToBoundary:F1} yalms");
            ImGui.PopStyleColor();
        }

        // HUD 编辑模式开关（仅鼠队+游戏开始后显示）
        if (_gameStarted && _selectedTeam == "MOUSE")
        {
            var editMode = _plugin.OverlayEditMode;
            if (ImGui.Checkbox("编辑 HUD 位置", ref editMode))
                _plugin.OverlayEditMode = editMode;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("开启后可直接拖拽屏幕上的 HUD 调整位置");
        }

        ImGui.Separator();

        // ── 关于面板 ──
        if (_showAbout)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.15f, 0.2f, 0.85f));
            ImGui.BeginChild("##AboutPanel", new Vector2(0, 140), true);
            ImGui.TextWrapped("躲猫猫 HideCatCat");
            ImGui.Spacing();
            ImGui.TextWrapped("猫队需要在限定时间内抓住所有鼠队玩家，鼠队需要躲避猫队的追捕存活到时间结束。");
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), "QQ群: 710780045");
            ImGui.SameLine();
            ImGui.TextDisabled("— 提交建议 / 反馈Bug");
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        // ── 设置面板 ──
        if (_showSettings)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.15f, 0.2f, 0.85f));
            ImGui.BeginChild("##SettingsPanel", new Vector2(0, 80), true);
            ImGui.TextWrapped("躲猫猫服务器地址（可自建服务端后修改）:");
            var preview = ServerDisplayName(_plugin.ServerUrl);
            if (ImGui.BeginCombo("##ServerUrl", preview))
            {
                // 默认选项
                if (ImGui.Selectable("默认躲猫猫服务器地址", _plugin.ServerUrl == DefaultServerUrl))
                    _plugin.ServerUrl = DefaultServerUrl;

                // 历史记录
                foreach (var url in _plugin.ServerHistory)
                {
                    if (ImGui.Selectable(url, url == _plugin.ServerUrl))
                        _plugin.ServerUrl = url;
                }

                ImGui.Separator();

                // 自定义输入
                ImGui.InputText("##CustomUrl", ref _customUrlInput, 200);
                ImGui.SameLine();
                if (ImGui.SmallButton("添加") && !string.IsNullOrWhiteSpace(_customUrlInput))
                {
                    var url = _customUrlInput.Trim();
                    _plugin.AddServerToHistory(url);
                    _plugin.ServerUrl = url;
                    _customUrlInput = "";
                }

                ImGui.EndCombo();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        // 空闲时自动连接大厅（展开状态下）
        if (!_gameClient.IsConnected && _showLobby && !_lobbyClient.IsConnected && !_lobbyPendingConnect)
        {
            _lobbyPendingConnect = true;
            _ = _lobbyClient.ConnectLobbyAsync(_plugin.ServerUrl);
        }

        // Lobby 面板（公开房间浏览）
        if (_showLobby && _lobbyClient.IsConnected)
        {
            DrawLobbyPanel();
            ImGui.Separator();
        }

        // 连接面板
        if (!_gameClient.IsConnected)
        {
            DrawConnectPanel();
            return;
        }

        // 已连接 → 选择阵营
        if (!_hasJoined)
        {
            DrawTeamSelection();
            return;
        }

        // 已加入房间 → 游戏主面板
        DrawGamePanel();
    }

    private void DrawLobbyPanel()
    {
        ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 1f), "── 公开房间大厅 ──");

        if (!string.IsNullOrEmpty(_lobbyError))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.TextWrapped(_lobbyError);
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        ImGui.Separator();

        if (_publicRooms.Count == 0)
        {
            ImGui.TextDisabled("暂无公开房间");
            return;
        }

        ImGui.Text($"共 {_publicRooms.Count} 个公开房间:");
        ImGui.Separator();

        foreach (var room in _publicRooms)
        {
            // 隐藏自己的房间
            if (room.hostName == _playerName) continue;

            var stateLabel = room.state switch { "PLAYING" => "游戏中", "FINISHED" => "已结束", _ => "等待中" };
            ImGui.TextWrapped($"{room.roomName}");
            ImGui.TextDisabled($"  房主: {room.hostName} | {room.roomServer} | {room.territoryName}");
            ImGui.SameLine();
            ImGui.TextColored(room.state == "PLAYING" ? new Vector4(1f, 0.5f, 0.2f, 1f) : new Vector4(0.2f, 0.9f, 0.3f, 1f), $"[{stateLabel}]");
            ImGui.SameLine();
            ImGui.Text($" {room.playerCount}人");

            ImGui.SameLine();
            ImGui.PushID($"apply_{room.roomName}");
            if (ImGui.SmallButton("申请加入"))
            {
                _ = _lobbyClient.SendAsync(new
                {
                    type = "APPLY_JOIN_ROOM",
                    roomId = room.roomId,
                    playerName = _playerName,
                    playerServer = _plugin.CurrentWorldName,
                    territoryId = _plugin.CurrentTerritoryId
                });
                _appliedRoom = room.roomId;
            }
            ImGui.PopID();

            ImGui.Separator();
        }

        if (_joinResults.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), "── 申请结果 ──");
            foreach (var r in _joinResults)
            {
                var mark = r.accepted ? "✓" : "✗";
                var color = r.accepted ? new Vector4(0.2f, 0.9f, 0.3f, 1f) : new Vector4(0.9f, 0.3f, 0.3f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextWrapped($"{mark} {r.roomName}  房主: {r.hostName}");
                ImGui.PopStyleColor();
                if (r.accepted && !string.IsNullOrEmpty(r.password))
                {
                    ImGui.TextDisabled($"  密码: {r.password} | {r.roomServer}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("复制##cp" + r.requestId))
                        ImGui.SetClipboardText(r.password);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("加入##join" + r.requestId))
                    {
                        _showLobby = false;
                        _password = r.password;
                        _ = _lobbyClient.DisconnectAsync();
                        _ = _gameClient.ConnectAsync(_plugin.ServerUrl, r.password);
                    }
                }
            }
        }
    }

    private void DrawConnectPanel()
    {
        _playerName = _plugin.LocalPlayerName;

        ImGui.Text($"玩家: {_playerName}");
        if (string.IsNullOrEmpty(_playerName))
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), "请先登录游戏角色");
            return;
        }

        // 服务器地址（只读 + 齿轮按钮修改）
        ImGui.Text("服务器:");
        ImGui.SameLine();
        ImGui.TextDisabled(ServerDisplayName(_plugin.ServerUrl));
        ImGui.SameLine();
        DrawGearButton();

        // 自动生成口令
        if (string.IsNullOrEmpty(_password))
            _password = GeneratePassword();

        ImGui.Text("口令:");
        ImGui.SameLine();
        ImGui.InputText("##password", ref _password, 12);
        ImGui.SameLine();
        if (ImGui.Button("Roll"))
            _password = GeneratePassword();
        ImGui.SameLine();
        if (ImGui.Button("Copy"))
            ImGui.SetClipboardText(_password);

        if (ImGui.Button("连接", new Vector2(200, 30)) && !string.IsNullOrEmpty(_password))
        {
            _errorMessage = "";
            Plugin.Log.Info($"[UI] 点击连接 url={_plugin.ServerUrl} password=*** player={_playerName}");
            _ = _gameClient.ConnectAsync(_plugin.ServerUrl, _password);
        }

        ImGui.Spacing();

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.TextWrapped(_errorMessage);
            ImGui.PopStyleColor();
        }
    }

    private static string GeneratePassword()
    {
        var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rand = new Random();
        return new string(Enumerable.Range(0, 5).Select(_ => chars[rand.Next(chars.Length)]).ToArray());
    }

    private void DrawTeamSelection()
    {
        ImGui.Separator();

        // 显示房间基准服务器/地图
        if (!string.IsNullOrEmpty(_roomServer))
        {
            var myServer = _plugin.CurrentWorldName;
            var myTerritory = _plugin.CurrentTerritoryId;
            var serverMatch = myServer == _roomServer;
            var mapMatch = myTerritory == _roomTerritoryId;

            ImGui.Text($"房间: {_roomServer} (地图ID: {_roomTerritoryId})");
            ImGui.TextColored(serverMatch ? new Vector4(0.2f, 0.9f, 0.3f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f),
                $"你的服务器: {myServer} {(serverMatch ? "✓" : "✗")}");
            ImGui.TextColored(mapMatch ? new Vector4(0.2f, 0.9f, 0.3f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f),
                $"你的地图: {myTerritory} {(mapMatch ? "✓" : "✗")}");
            ImGui.Spacing();
        }

        ImGui.Text("选择阵营:");

        // 错误提示
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.TextWrapped(_errorMessage);
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        if (ImGui.Button("[猫队]", new Vector2(200, 40)))
            _ = JoinRoomAsync("CAT");
        if (ImGui.Button("[鼠队]", new Vector2(200, 40)))
            _ = JoinRoomAsync("MOUSE");
    }

    private async Task JoinRoomAsync(string team)
    {
        _selectedTeam = team;
        Plugin.Log.Info($"[UI] 选择 {team} 队，等待连接就绪...");
        // 等连接稳定
        for (int i = 0; i < 20 && _gameClient.IsConnected == false; i++)
            await Task.Delay(100);
        if (!_gameClient.IsConnected)
        {
            Plugin.Log.Warning("[UI] 连接未就绪，放弃发送 JOIN_ROOM");
            return;
        }
        Plugin.Log.Info($"[UI] 发送 JOIN_ROOM team={team}");
        var server = _plugin.CurrentWorldName;
        var territoryId = _plugin.CurrentTerritoryId;
        var territoryName = _plugin.CurrentTerritoryName;
        await _gameClient.SendAsync(new { type = "JOIN_ROOM", password = _password, playerName = _playerName, playerServer = server, territoryId, territoryName, team });
    }

    private void DrawGamePanel()
    {
        ImGui.Separator();

        // 房主设置
        if (_playerName == _hostName && !_settingsLocked)
        {
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), "[房主设置]");
            if (ImGui.Button("[设为当前位置]")) SetStartPoint();

            ImGui.Text("半径 (yalms):");
            ImGui.SameLine();
            ImGui.InputFloat("##radius", ref _radius);

            ImGui.Text("胜利条件:");
            var conds = new[] { "ALL", "COUNT", "PERCENT" };
            var labels = new[] { "全部抓到", "抓到 N 个", "抓到 X%" };
            var curIdx = Array.IndexOf(conds, _winCondition);
            if (curIdx < 0) curIdx = 0;
            if (ImGui.Combo("##cond", ref curIdx, labels, labels.Length))
            {
                _winCondition = conds[curIdx];
                _winCount = 1;
            }
            if (_winCondition != "ALL")
            {
                ImGui.SameLine();
                ImGui.InputInt("##winCount", ref _winCount);
            }

            ImGui.Text("时间限制 (分钟):");
            ImGui.SameLine();
            ImGui.InputInt("##time", ref _timeLimitMin);
            if (_timeLimitMin < 1) _timeLimitMin = 1;

            if (ImGui.Button("[应用设置]", new Vector2(200, 25)))
                _ = SendSettings();

            ImGui.Spacing();
            if (ImGui.Checkbox("公开房间", ref _isPublicRoom))
            {
                _ = _gameClient.SendAsync(new { type = "SET_ROOM_PUBLIC", password = _password, isPublic = _isPublicRoom });
            }
            if (_isPublicRoom)
            {
                var roomName = $"{_hostName}@{_roomServer} - {_plugin.CurrentTerritoryName}";
                ImGui.TextDisabled($"  房间名: {roomName}");
            }
            ImGui.Separator();
        }

        ImGui.Separator();

        // 玩家列表
        ImGui.Text($"玩家: 猫{CountTeam("CAT")} 鼠{CountTeam("MOUSE")}");
        List<PlayerInfo> playersSnapshot;
        lock (_playersLock) { playersSnapshot = _players.ToList(); }
        foreach (var p in playersSnapshot)
        {
            var icon = p.team switch { "CAT" => "[猫]", "MOUSE" => "[鼠]", _ => "?" };
            var ready = p.ready ? "[R]" : "...";
            var host = p.isHost ? "[H]" : "";
            ImGui.Text($"  {host}{icon} {p.name} {ready}");
        }

        ImGui.Separator();

        DrawJoinRequests();

        // 准备 & 开始 / 重新开始
        if (!_gameStarted)
        {
            if (_gameOver)
            {
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "游戏结束");
                ImGui.Text($"猫队 {_catWins} 胜  —  鼠队 {_mouseWins} 胜");

                // 选择重新开始的队伍
                var teams = new[] { "CAT", "MOUSE" };
                var teamNames = new[] { "猫队", "鼠队" };
                var curIdx = Array.IndexOf(teams, _selectedTeam);
                if (curIdx < 0) curIdx = 0;
                ImGui.Text("重新开始队伍:");
                ImGui.SameLine();
                ImGui.PushItemWidth(100);
                if (ImGui.Combo("##restartTeam", ref curIdx, teamNames, teamNames.Length))
                    _selectedTeam = teams[curIdx];
                ImGui.PopItemWidth();

                if (ImGui.Button("[重新开始]", new Vector2(200, 30)))
                {
                    Plugin.Log.Info($"[UI] 点击重新开始 team={_selectedTeam}");
                    ResetLocalState();
                    _gameOver = false;
                    // 通知服务器重置房间（可带换队）
                    _ = _gameClient.SendAsync(new { type = "RESET_ROOM", password = _password, team = _selectedTeam });
                }
            }
            else
            {
                if (ImGui.Button("[准备]", new Vector2(200, 30)))
                {
                    Plugin.Log.Info("[UI] 点击准备");
                    _ = _gameClient.SendAsync(new { type = "READY", password = _password });
                }

                if (_playerName == _hostName && _gameState == "ALL_READY")
                {
                    if (ImGui.Button("[开始游戏]", new Vector2(200, 30)))
                    {
                        var pos = _plugin.LocalPlayerPosition;
                        Plugin.Log.Info($"[UI] 房主点击开始游戏 pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1})");
                        _ = _gameClient.SendAsync(new { type = "START_GAME", password = _password, position = new { x = pos.X, y = pos.Y, z = pos.Z } });
                    }
                }
            }
        }
        else
        {
            var remaining = Math.Max(0, _timeLimitSec - (int)(DateTime.Now - _gameStartTime).TotalSeconds);
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), $"[{remaining / 60}:{remaining % 60:D2}]");

            var pos = _plugin.LocalPlayerPosition;
            var sb = new System.Text.StringBuilder();

            if (_selectedTeam == "MOUSE")
            {
                List<PlayerInfo> catSnapshot;
                lock (_playersLock) { catSnapshot = _players.Where(p => p.team == "CAT").ToList(); }
                var nearestCat = catSnapshot
                    .Where(p => !p.eliminated && (p.x != 0 || p.y != 0 || p.z != 0))
                    .Select(p => new { p.name, d = Math.Sqrt(Math.Pow(pos.X - p.x, 2) + Math.Pow(pos.Y - p.y, 2) + Math.Pow(pos.Z - p.z, 2)) })
                    .OrderBy(p => p.d)
                    .FirstOrDefault();
                if (nearestCat != null)
                {
                    NearestCatDistance = (float)nearestCat.d;
                    sb.AppendLine($"Cat: {nearestCat.name} {nearestCat.d:F1} yalms");
                }
            }
            OverlayText = sb.ToString().TrimEnd();

            _ = SendPositionIfNeededAsync(pos);
        }

        if (!string.IsNullOrEmpty(_lastEvent))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), _lastEvent);
        }
    }

    private void SetStartPoint()
    {
        var pos = _plugin.LocalPlayerPosition;
        _ = _gameClient.SendAsync(new
        {
            type = "UPDATE_SETTINGS",
            password = _password,
            startPos = new { x = pos.X, y = pos.Y, z = pos.Z },
            radius = _radius,
            winCondition = _winCondition,
            winCount = _winCount,
            timeLimitSec = _timeLimitMin * 60
        });
    }

    private async Task SendSettings()
    {
        var pos = _plugin.LocalPlayerPosition;
        await _gameClient.SendAsync(new
        {
            type = "UPDATE_SETTINGS",
            password = _password,
            startPos = new { x = pos.X, y = pos.Y, z = pos.Z },
            radius = _radius,
            winCondition = _winCondition,
            winCount = _winCount,
            timeLimitSec = _timeLimitMin * 60
        });
    }

    private int CountTeam(string team) { lock (_playersLock) { return _players.Count(p => p.team == team); } }

    private static double jsonTryGetDouble(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return 0;
    }

    private DateTime _lastPosSend;
    private async Task SendPositionIfNeededAsync(Vector3 pos)
    {
        if ((DateTime.Now - _lastPosSend).TotalMilliseconds < 500) return;
        _lastPosSend = DateTime.Now;

        // 猫队：检查当前选中目标是否是鼠队玩家
        string? targetPlayer = null;
        if (_selectedTeam == "CAT")
        {
            var targetName = _plugin.CurrentTarget.Name;
            if (!string.IsNullOrEmpty(targetName) && GetPlayersSnapshot().Any(p => p.name == targetName && p.team == "MOUSE" && !p.eliminated))
                targetPlayer = targetName;
        }

        // 只发送非 null 的 targetPlayer（不发 null 避免服务端解析异常）
        var payload = targetPlayer != null
            ? (object)new { type = "POSITION_UPDATE", password = _password, position = new { x = pos.X, y = pos.Y, z = pos.Z }, targetPlayer }
            : new { type = "POSITION_UPDATE", password = _password, position = new { x = pos.X, y = pos.Y, z = pos.Z } };
        await _gameClient.SendAsync(payload);
    }

    private void DrawJoinRequests()
    {
        if (_joinRequests.Count == 0) return;

        var isHost = _playerName == _hostName;
        ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 1f), "── 加入申请 ──");

        foreach (var req in _joinRequests.ToList())
        {
            if (req.status == "PENDING")
            {
                ImGui.TextWrapped($"  申请人: {req.applicantName} | 服务器: {req.applicantServer} | 地图: {req.applicantTerritoryId}");
                if (isHost)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"接受##acc{req.requestId}"))
                    {
                        _ = _gameClient.SendAsync(new { type = "RESPOND_JOIN_REQUEST", password = _password, requestId = req.requestId, accepted = true });
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"拒绝##rej{req.requestId}"))
                    {
                        _ = _gameClient.SendAsync(new { type = "RESPOND_JOIN_REQUEST", password = _password, requestId = req.requestId, accepted = false });
                    }
                }
                else
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("等待房主处理...");
                }
            }
            else
            {
                var mark = req.status == "ACCEPTED" ? "✓" : "✗";
                var color = req.status == "ACCEPTED" ? new Vector4(0.2f, 0.9f, 0.3f, 1f) : new Vector4(0.9f, 0.3f, 0.3f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextWrapped($"  {mark} 申请人: {req.applicantName}  {(req.status == "ACCEPTED" ? "已接受" : "已拒绝")}");
                ImGui.PopStyleColor();
            }
        }
        ImGui.Separator();
    }

    private void ResetLocalState()
    {
        _gameOver = false;
        _gameStarted = false;
        lock (_playersLock) { _players.Clear(); }
        _lastEvent = "";
    }

    private List<PlayerInfo> GetPlayersSnapshot()
    {
        lock (_playersLock) { return _players.ToList(); }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            // 进入游戏房间 → 断开大厅
            _showLobby = false;
            _lobbyPendingConnect = false;
            _ = _lobbyClient.DisconnectAsync();
        }
        else
        {
            // 离开游戏房间 → 恢复大厅
            _showLobby = true;
            _selectedTeam = "";
            _hasJoined = false;
            _hostName = "";
            _roomServer = "";
            _roomTerritoryId = 0;
            _isPublicRoom = false;
            lock (_playersLock) { _players.Clear(); }
            _joinRequests.Clear();
            _gameStarted = false;
            _gameOver = false;
        }
    }

    private void DrawGearButton()
    {
        var gearSize = ImGui.GetFrameHeight();
        var gearPos = ImGui.GetCursorScreenPos();
        var gearCenter = gearPos + new Vector2(gearSize / 2, gearSize / 2);
        var clicked = ImGui.InvisibleButton("##GearBtn", new Vector2(gearSize, gearSize));
        if (clicked) _showSettings = !_showSettings;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("修改服务器地址");

        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        var col = hovered ? 0xFFFFFFFF : 0xFFCCCCCC;
        dl.AddCircleFilled(gearCenter, gearSize * 0.32f, col);
        dl.AddCircleFilled(gearCenter, gearSize * 0.16f, 0xFF000000);
        for (int i = 0; i < 8; i++)
        {
            var angle = i * MathF.PI / 4;
            var outer = gearCenter + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * gearSize * 0.42f;
            var inner = gearCenter + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * gearSize * 0.28f;
            var perp = new Vector2(-MathF.Sin(angle), MathF.Cos(angle)) * gearSize * 0.08f;
            dl.AddQuadFilled(inner - perp, inner + perp, outer + perp, outer - perp, col);
        }
    }

    private void OnError(string error)
    {
        _errorMessage = error;
    }

    private void OnServerMessage(JsonElement json)
    {
        try
        {
            if (!json.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                Plugin.Log.Warning("[UI] 收到无 type 字段的消息，已忽略");
                return;
            }
            var type = typeEl.GetString()!;
            Plugin.Log.Info($"[UI] 收到 {type}");
            switch (type.ToUpperInvariant())
            {
                case "PLAYER_LIST":
                {
                    _hostName = jsonTryGetString(json, "hostName");
                    _gameState = jsonTryGetString(json, "gameState");
                    _settingsLocked = json.TryGetProperty("settingsLocked", out var sl) && sl.ValueKind == JsonValueKind.True;
                    var newList = new List<PlayerInfo>();
                    if (json.TryGetProperty("players", out var playersEl) && playersEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in playersEl.EnumerateArray())
                        {
                            newList.Add(new PlayerInfo
                            {
                                name = jsonTryGetString(p, "name"),
                                team = jsonTryGetString(p, "team"),
                                ready = p.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True,
                                isHost = p.TryGetProperty("isHost", out var h) && h.ValueKind == JsonValueKind.True,
                                eliminated = p.TryGetProperty("eliminated", out var el) && el.ValueKind == JsonValueKind.True,
                                x = jsonTryGetDouble(p, "x"),
                                y = jsonTryGetDouble(p, "y"),
                                z = jsonTryGetDouble(p, "z"),
                            });
                        }
                    }
                    lock (_playersLock) { _players = newList; }
                    _hasJoined = GetPlayersSnapshot().Any(p => p.name == _playerName);
                    if (_hasJoined) _errorMessage = "";
                    // 从服务器同步当前玩家队伍（换队后确保本地一致）
                    var me = newList.FirstOrDefault(p => p.name == _playerName);
                    if (me != null) _selectedTeam = me.team;
                    // 同步房间基准服务器和地图
                    _roomServer = jsonTryGetString(json, "roomServer");
                    if (json.TryGetProperty("roomTerritoryId", out var rt) && rt.ValueKind == JsonValueKind.Number)
                        _roomTerritoryId = rt.GetUInt32();
                    // 房间级别胜负累计
                    _catWins = jsonTryGetInt(json, "catWins");
                    _mouseWins = jsonTryGetInt(json, "mouseWins");
                    break;
                }

                case "ALL_READY":
                    _gameState = "ALL_READY";
                    break;

                case "START_GAME":
                    _gameOver = false;
                    _gameStarted = true;
                    _gameStartTime = DateTime.Now;
                    _timeLimitSec = TryGetInt32(json, "timeLimitSec", 300);
                    if (json.TryGetProperty("startPos", out var sp))
                    {
                        _startX = jsonTryGetDouble(sp, "x");
                        _startY = jsonTryGetDouble(sp, "y");
                        _startZ = jsonTryGetDouble(sp, "z");
                    }
                    _boundaryRadius = TryGetSingle(json, "radius", 50f);
                    _lastEvent = $"游戏开始！半径: {_boundaryRadius:F0} yalms";
                    break;

                case "CATCH_EVENT":
                    _lastEvent = $"[Cat]{jsonTryGetString(json, "catName")} caught [Mouse]{jsonTryGetString(json, "mouseName")}! " +
                                 $"Mice left: {jsonTryGetInt(json, "miceRemaining")}/{jsonTryGetInt(json, "miceTotal")}";
                    break;

                case "ERROR":
                    _errorMessage = jsonTryGetString(json, "message");
                    break;

                case "GAME_OVER":
                    _gameStarted = false;
                    _gameOver = true;
                    _catWins = jsonTryGetInt(json, "catWins");
                    _mouseWins = jsonTryGetInt(json, "mouseWins");
                    _lastEvent = $"Winner: {jsonTryGetString(json, "winner")}! {jsonTryGetString(json, "reason")}";
                    break;

                case "JOIN_REQUEST":
                {
                    var req = new JoinRequestEntry
                    {
                        requestId = jsonTryGetString(json, "requestId"),
                        applicantName = jsonTryGetString(json, "applicantName"),
                        applicantServer = jsonTryGetString(json, "applicantServer"),
                        applicantTerritoryId = jsonTryGetLong(json, "applicantTerritoryId"),
                        status = "PENDING"
                    };
                    _joinRequests.Add(req);
                    break;
                }
                case "JOIN_REQUEST_UPDATE":
                {
                    var rid = jsonTryGetString(json, "requestId");
                    var accepted = json.TryGetProperty("accepted", out var ac) && ac.ValueKind == JsonValueKind.True;
                    var existing = _joinRequests.FirstOrDefault(r => r.requestId == rid);
                    if (existing != null)
                        existing.status = accepted ? "ACCEPTED" : "REJECTED";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[UI] 处理消息异常: {ex.Message}");
        }
    }

    private static string jsonTryGetString(JsonElement el, string key)
    {
        return el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static int jsonTryGetInt(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return 0;
    }

    private static long jsonTryGetLong(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt64();
        return 0;
    }

    private static int TryGetInt32(JsonElement el, string key, int defaultValue)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return defaultValue;
    }

    private static float TryGetSingle(JsonElement el, string key, float defaultValue)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetSingle();
        return defaultValue;
    }

    private class PlayerInfo
    {
        public string name = "";
        public string team = "";
        public bool ready;
        public bool isHost;
        public bool eliminated;
        public double x, y, z;
    }

    private class PublicRoomEntry
    {
        public string roomId = "";
        public string roomName = "";
        public string hostName = "";
        public string roomServer = "";
        public long territoryId;
        public string territoryName = "";
        public string state = "";
        public int playerCount;
    }

    private class JoinResultEntry
    {
        public string requestId = "";
        public bool accepted;
        public string password = "";
        public string roomName = "";
        public string hostName = "";
        public string roomServer = "";
        public long territoryId;
    }

    private class JoinRequestEntry
    {
        public string requestId = "";
        public string applicantName = "";
        public string applicantServer = "";
        public long applicantTerritoryId;
        public string status = ""; // PENDING / ACCEPTED / REJECTED
    }

    private void OnLobbyMessage(JsonElement json)
    {
        try
        {
            if (!json.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return;
            var type = typeEl.GetString()!;
            Plugin.Log.Info($"[UI-Lobby] 收到 {type}");
            switch (type.ToUpperInvariant())
            {
                case "PUBLIC_ROOMS_LIST":
                {
                    _publicRooms.Clear();
                    if (json.TryGetProperty("rooms", out var roomsEl) && roomsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in roomsEl.EnumerateArray())
                        {
                            _publicRooms.Add(new PublicRoomEntry
                            {
                                roomId = jsonTryGetString(r, "roomId"),
                                roomName = jsonTryGetString(r, "roomName"),
                                hostName = jsonTryGetString(r, "hostName"),
                                roomServer = jsonTryGetString(r, "roomServer"),
                                territoryId = jsonTryGetLong(r, "territoryId"),
                                territoryName = jsonTryGetString(r, "territoryName"),
                                state = jsonTryGetString(r, "state"),
                                playerCount = jsonTryGetInt(r, "playerCount")
                            });
                        }
                    }
                    break;
                }
                case "JOIN_REQUEST_RESULT":
                {
                    var result = new JoinResultEntry
                    {
                        requestId = jsonTryGetString(json, "requestId"),
                        accepted = json.TryGetProperty("accepted", out var ac) && ac.ValueKind == JsonValueKind.True,
                        roomName = jsonTryGetString(json, "roomName"),
                        hostName = jsonTryGetString(json, "hostName"),
                    };
                    if (result.accepted)
                    {
                        result.password = jsonTryGetString(json, "password");
                        result.roomServer = jsonTryGetString(json, "roomServer");
                        result.territoryId = jsonTryGetLong(json, "territoryId");
                    }
                    _joinResults.Add(result);
                    break;
                }
                case "ERROR":
                    _lobbyError = jsonTryGetString(json, "message");
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[UI-Lobby] 处理消息异常: {ex.Message}");
        }
    }

    private void OnLobbyConnectionChanged(bool connected)
    {
        _lobbyPendingConnect = false;
        if (connected)
        {
            _lobbyError = "";
            _ = _lobbyClient.SendAsync(new { type = "LIST_PUBLIC_ROOMS" });
        }
        else
        {
            _publicRooms.Clear();
            _joinResults.Clear();
        }
    }
    private void OnLobbyError(string error) { _lobbyError = error; _lobbyPendingConnect = false; }

    public void DisposeLobby()
    {
        _lobbyClient.Dispose();
    }
}

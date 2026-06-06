# Hide Cat Cat

FFXIV Dalamud 躲猫猫插件 — 判断点击选中的物体是不是队友，并在屏幕中央显示距离。

## 功能

- 点击游戏内任意目标，实时判断是否为队伍/同盟成员
- 判定为队友后，屏幕正中央显示直线距离浮层
- 距离颜色随远近变化：
  - ≤ 10m 🔴 红色
  - ≤ 20m 🟢 绿色
  - ≤ 30m 🟡 黄色
  - > 30m ⚪ 灰色
- `/hidecatcat` 命令 — 打开/关闭信息窗口
- 窗口显示目标名称、类型及判定结果

## 判断逻辑

1. 优先检查目标的 `StatusFlags`（`PartyMember` / `AllianceMember`），O(1)
2. 兜底比对 `EntityId` 与队伍列表

## 使用

1. 加载插件后进入队伍/团队
2. 在游戏里点击任意角色或物体
3. 屏幕中央自动显示距离（仅队友时）
4. `/hidecatcat` 可打开详情窗口查看目标信息

## ⚠️ 运行前配置

**必须修改以下内容才能正常使用：**

- `HideCatCat/PluginConfig.cs` — 将 `ServerUrl` 默认值改为你的 WebSocket 服务器地址：
  ```csharp
  public string ServerUrl { get; set; } = "wss://your-server:port/ws";
  ```

## 构建

```bash
dotnet build HideCatCat.slnx
```

依赖 `Dalamud.CN.NET.Sdk/15.0.0`，需要在 `nuget.config` 中配置源。

## 项目结构

```
HideCatCat/
├── Directory.Build.props
├── nuget.config
├── HideCatCat.slnx
├── README.md
└── HideCatCat/
    ├── HideCatCat.csproj    # SDK 15.0.0
    ├── HideCatCat.json      # 插件清单
    ├── Plugin.cs            # IAsyncDalamudPlugin 入口、构造注入、目标检测、屏幕浮层
    ├── PluginConfig.cs      # 配置持久化
    └── Windows/
        └── MainWindow.cs    # ImGui 信息窗口
```

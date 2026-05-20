# SephiriaReconnect

《Sephiria》原生 AddOn 断线重连 Mod。

这个项目不是 BepInEx 插件，也不依赖 Belnex。Mod 使用游戏自带的 AddOn 加载方式，并随包附带 `0Harmony.dll` 用于 Harmony patch。

当前目标是尽量改善多人游戏中途掉线后的恢复体验：房主保存每层入口检查点，成员掉线后保留 SteamID 与槽位信息；房主可以先执行本层恢复，也可以先通过 Steam 邀请或上次房间数据让成员回到房主会话，再由握手流程尝试恢复原槽位。

## 功能

- 每层入口自动保存“本层恢复”检查点。
- 掉线成员按 SteamID 保留玩家槽位，减少重连后占错槽位的风险。
- 支持房主在成员未回房时先执行本层恢复。
- 恢复后继续刷新 Steam lobby 重连数据，方便成员稍后再回房。
- 支持 Steam 邀请重连和客户端“加入上次房间”。
- 使用 Unity UI 面板，不使用 IMGUI。
- 左下角兔头图标自动跟随原生 HUD 图标组，并在过场或其他界面打开时隐藏。
- 提供文件日志，并按数量、大小和保留天数自动清理旧日志。

## 安装

下载 release 压缩包后，把里面的 `SephiriaReconnect` 文件夹放到游戏 AddOns 目录：

```text
Steam\steamapps\common\Sephiria\AddOns\SephiriaReconnect
```

安装后目录应类似：

```text
Sephiria\AddOns\SephiriaReconnect
├─ SephiriaReconnect.dll
├─ 0Harmony.dll
├─ metadata.json
├─ config.json
├─ README.md
└─ assets
   ├─ reconnect-rabbit-green.png
   ├─ reconnect-rabbit-green-hover.png
   ├─ reconnect-rabbit-amber.png
   └─ reconnect-rabbit-gray.png
```

建议所有参与联机的玩家安装同一版本 Mod。房主安装 Mod 是断线恢复链路的核心；客户端安装同版本 Mod 后可以发送重连握手，让房主更准确地按 SteamID 恢复槽位。

未安装 Mod 的玩家仍可能通过原版 Steam 邀请进入房间，但不会参与 Mod 握手，稳定性和槽位恢复能力不如双方同版本安装。

## 使用

进入游戏后，左下角原生图标右侧会出现一个兔头图标。点击图标打开中文重连面板。

### 房主

- `本层恢复`：恢复到最近一次楼层入口检查点。恢复时会重启当前 host，并尽量保留/刷新 Steam lobby 重连数据。
- `邀请重连`：打开 Steam 邀请窗口，并尝试对历史成员发送 Steam 邀请。

### 客户端

- `加入上次房间`：尝试读取上次记录的重连 lobby，再连接到房主。
- `发送握手`：向房主发送当前 SteamID、会话与检查点信息，用于重新绑定原玩家槽位。

一般流程：

1. 房主和成员正常联机进入关卡。
2. 房主进入每层时，Mod 自动保存本层入口检查点。
3. 成员掉线后，房主面板中会显示该成员为离线或待重连。
4. 房主可先执行 `本层恢复`，也可以先 `邀请重连`。
5. 成员通过 Steam 邀请或 `加入上次房间` 回到房主会话。
6. 成员回房后发送握手，房主按 SteamID 尝试绑定回原槽位。

## 恢复逻辑

本 Mod 不尝试在掉线瞬间强行复制所有运行中对象。核心策略是复用游戏原本的读档与开局流程：

1. Harmony 监听楼层进入、读档、联机和 Steam 邀请相关流程。
2. 房主在楼层入口复制当前 `CurrentRun` 临时存档，生成本层检查点。
3. 房主记录当前会话、检查点、Steam lobby、成员 SteamID 和玩家槽位。
4. 成员掉线后，房主把对应 SteamID 标记为离线保留。
5. 执行本层恢复时，Mod 停止 host、载入检查点、重新 StartHost。
6. 恢复后重新发布 Steam lobby 重连数据。
7. 成员回房并握手后，房主把该 SteamID 重新绑定到原来的槽位。

恢复目标是“进入本楼层时的状态”，不是掉线瞬间原地恢复。血量、背包、神器、果串、临时加成等内容以本层入口检查点为准。

## Steam 房间

原版游戏在本局开始后通常会限制中途加入，因此只靠 Steam 好友列表右键加入并不稳定。

Mod 会在需要重连时短时间打开 join window，并写入 Steam lobby 数据：

- 当前重连会话 ID
- 检查点 ID
- 房主 SteamID
- 游戏服务端 ID
- 是否允许地牢中加入
- Mod 版本信息

如果单机状态下使用 `本层恢复` 触发了 Steam lobby 创建，Mod 会把 lobby 设置为私密房间，默认最大人数为 4。

## 配置

配置文件：

```text
Sephiria\AddOns\SephiriaReconnect\config.json
```

常用配置：

```json
{
  "AutoSendHello": true,
  "AutoCaptureFloorCheckpoint": true,
  "ClientHelloIntervalSeconds": 15,
  "MaxAutoHelloAttempts": 3,
  "ReconnectTimeoutSeconds": 300,
  "RequireAllPlayersBeforeRestore": false,
  "MaxCheckpointCount": 12,
  "HostSessionRefreshSeconds": 5,
  "HostPlayerRefreshSeconds": 3,
  "HostLobbyRefreshSeconds": 10,
  "ReconnectJoinWindowSeconds": 180,
  "EnableFileLogging": true,
  "MaxLogFiles": 8,
  "MaxLogFileBytes": 1048576,
  "LogRetentionDays": 7
}
```

说明：

- `AutoSendHello`：客户端在确认需要重连时自动发送握手。
- `ClientHelloIntervalSeconds`：自动握手间隔。
- `MaxAutoHelloAttempts`：自动握手最大次数，成功后停止。
- `RequireAllPlayersBeforeRestore`：是否要求所有历史成员回房后才允许恢复。
- `MaxCheckpointCount`：最多保留多少个检查点。
- `ReconnectJoinWindowSeconds`：重连窗口持续时间。
- `EnableFileLogging`：是否启用 Mod 文件日志。

## 日志

日志目录：

```text
Sephiria\AddOns\SephiriaReconnect\logs
```

默认日志策略：

- 最多保留 8 个日志文件。
- 单个日志超过 1 MB 后轮转。
- 自动删除 7 天前的旧日志。
- 每 600 秒执行一次日志清理检查。

排查联机问题时，优先查看：

- 游戏主机的 `logs/reconnect-*.log`
- 客户端的 `logs/reconnect-*.log`
- Unity `Player.log`

如果客户端掉线并出现 `Unknown message id`，通常代表双方 Mod 版本不一致，或主机的网络消息处理器没有正确注册。请确认双方使用同一 release。

## 已知限制

- 原版对“本局已开始后新增玩家”的容错有限，本 Mod 主要面向原本参与过本局、掉线后回来的玩家。
- 完全新玩家中途加入可能出现初始状态、槽位或同步数据不完整。
- 本层恢复会重启 host，Steam lobby 和连接状态会经历一次重建。
- 恢复目标是楼层入口检查点，不是实时存档。
- Steam 邀请链路依赖 Steam overlay、Steam lobby 状态、双方游戏版本和网络状态。

## 开发

需要本机存在《Sephiria》Managed 程序集。默认路径：

```text
E:\steam\steamapps\common\Sephiria\Sephiria_Data\Managed
```

如果游戏安装在其他目录，可以通过环境变量或 MSBuild 属性指定：

```text
SEPHIRIA_MANAGED_DIR=你的路径\Sephiria_Data\Managed
```

构建：

```text
dotnet build SephiriaReconnect.csproj -c Release
```

构建产物输出到：

```text
dist
```

打包 release 时应包含：

```text
SephiriaReconnect.dll
0Harmony.dll
metadata.json
config.json
README.md
assets
```

`dist/`、`release/`、运行日志、检查点和本地会话文件不应提交到 Git 仓库。发布压缩包建议上传到 GitHub Releases。

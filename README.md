# SephiriaReconnect

《Sephiria》原生 AddOn 断线重连 Mod。

当前版本：`v0.1.10`

这个项目不是 BepInEx 插件，也不需要额外安装 BepInEx、Belnex 之类的加载器。Mod 使用游戏自带的 AddOn 加载方式；发布包内已经附带 `0Harmony.dll`，玩家不需要额外安装 Harmony。

本 Mod 的目标是改善多人游戏中途掉线后的恢复体验。它不是实时同步回滚系统，而是围绕“楼层入口检查点”工作：房主在每个有效地牢楼层入口保存检查点，成员掉线后保留 SteamID 与玩家槽位；房主通常先执行本层恢复重建房间，再通过 Steam 邀请、上次房间数据和 Mod 握手尽量让成员回到原槽位。

## 目录

- [功能概览](#功能概览)
- [安装](#安装)
- [联机安装建议](#联机安装建议)
- [游戏内面板](#游戏内面板)
- [面板按钮](#面板按钮)
- [推荐重连流程](#推荐重连流程)
- [恢复逻辑](#恢复逻辑)
- [本局判断和章节缓存](#本局判断和章节缓存)
- [Steam 房间和邀请](#steam-房间和邀请)
- [配置](#配置)
- [日志](#日志)
- [已知限制](#已知限制)
- [常见问题](#常见问题)
- [开发](#开发)

## 功能概览

- 原生 AddOn 加载，不使用 BepInEx。
- 使用 Harmony patch 监听楼层入口、Steam 邀请、玩家初始化和暂停面板。
- 房主自动保存有效楼层入口检查点。
- 检查点数量有限制，旧检查点会自动清理；当前“本层恢复”目标不会在本局中被清掉。
- 掉线成员按 SteamID 保留槽位，减少回房后占错槽位的风险。
- 允许房主在成员未回房时先执行本层恢复。
- 恢复后继续保留/刷新 Steam lobby 重连数据，成员可以稍后再回房。
- 支持 Steam 邀请重连、定向邀请历史掉线成员、客户端加入上次房间。
- 支持客户端自动握手：默认最多 3 次，间隔 15 秒，成功后停止。
- 记录本局章节缓存，例如 `5-4`，恢复后尽量保持原 Steam lobby 章节。
- 使用 Unity UI 面板，不使用 IMGUI。
- 左下角重连图标挂在原生 HUD 图标组后方，尺寸跟随原生图标。
- 图标在过场、加载、HUD 隐藏时隐藏；原生面板打开时保持显示但禁用点击，尽量表现得像原版图标。
- 中文面板显示成员槽位、游戏内名字、Steam 昵称、在线状态和 Mod 握手状态。
- 文件日志采用内存缓冲批量写盘，并按数量、大小和保留天数清理。

## 安装

下载 GitHub Releases 中的压缩包，例如：

```text
SephiriaReconnect-v0.1.10.zip
```

游戏目录通常类似：

```text
Steam\steamapps\common\Sephiria
```

如果游戏目录下没有 `AddOns` 文件夹，需要自己创建：

```text
Steam\steamapps\common\Sephiria\AddOns
```

推荐安装路径：

```text
Steam\steamapps\common\Sephiria\AddOns\SephiriaReconnect
```

Release 压缩包里的顶层文件夹通常带版本号，例如 `SephiriaReconnect-v0.1.10`。安装时可以：

1. 把这个文件夹解压到 `AddOns` 下后重命名为 `SephiriaReconnect`。
2. 或者手动创建 `AddOns\SephiriaReconnect`，再把压缩包内的文件复制进去。

安装后目录应类似：

```text
Sephiria\AddOns\SephiriaReconnect
├─ SephiriaReconnect.dll
├─ 0Harmony.dll
├─ metadata.json
├─ config.json
├─ README.md
└─ assets
   └─ reconnect-icon-*.png
```

升级旧版本时，通常只需要替换：

```text
SephiriaReconnect.dll
0Harmony.dll
metadata.json
README.md
assets
```

`config.json`、`logs`、`checkpoints`、`host_session.json`、`last_session.json` 可以保留。遇到异常时再考虑备份后删除这些运行时文件。

## 联机安装建议

建议所有参与联机的玩家安装同一版本 Mod。

房主安装 Mod 是断线恢复链路的核心。客户端安装同版本 Mod 后，可以发送握手，房主能更准确地按 SteamID 识别成员并恢复槽位。

未安装 Mod 的玩家仍可能通过原版 Steam 邀请回到房间，但不会参与 Mod 握手，也不能使用“发送握手”或“加入上次房间”。这种情况下重连稳定性、槽位恢复能力和错误提示都不如双方同版本安装。

如果客户端提示 `Unknown message id` 或 `模组协议版本不匹配`，通常代表双方 Mod 版本不一致，或其中一方没有正确加载 Mod。请确认双方使用同一 release。

## 游戏内面板

进入联机或 host 激活后，左下角原生 HUD 图标右侧会出现重连图标。点击图标打开中文重连面板。

面板会显示：

- 当前状态
- 当前身份：房主、客户端或单机/未连接
- 存档槽
- 局内标识
- 网络状态
- Steam 房间 ID
- 当前日志路径
- 当前会话、检查点和检查点哈希
- 联机成员列表
- 上次会话信息

成员列表会显示：

- 槽位
- 游戏内名字
- Steam 昵称
- 在线、掉线保留、等待重连、已回房待恢复等状态
- 是否已完成 Mod 握手

面板不会显示完整 SteamID。SteamID 只用于内部槽位绑定、邀请和日志排查。

## 面板按钮

### 本层恢复

房主使用。

恢复到最近一次有效楼层入口检查点。执行时会停止当前 host，载入检查点临时存档，再重新 StartHost。恢复目标是进入本楼层时的状态，不是掉线瞬间原地状态。

默认配置允许“成员未回房时先恢复”。恢复后 Mod 会继续打开一段重连窗口并刷新 Steam lobby 数据，让成员稍后再回房。

注意：本层恢复会重启 host。若成员已经回到房间，房主再次点击“本层恢复”可能会让这些成员再次断开。因此推荐流程是先恢复，再邀请成员回房。

### 邀请重连

房主使用。

功能包括：

- 打开 Steam 邀请窗口。
- 尝试对历史掉线成员发送 Steam lobby 邀请。
- 短时间允许地牢中加入。
- 如果当前没有 Steam lobby，房主侧会尝试创建一个私密 lobby，默认最大人数为 4。

如果面板没有历史掉线成员，仍会打开 Steam 邀请窗口，方便手动邀请好友。

### 发送握手

客户端使用。

向房主发送当前 SteamID、游戏内名字、存档槽、局内标识、会话 ID、检查点 ID 和重连令牌。房主收到后会尝试登记成员或把重连成员绑定回原槽位。

房主点击这个按钮不会发送握手。

### 加入上次房间

客户端使用。

读取本地记录的上次 lobby 与房主信息，尝试重新加入上次房间并连接到房主。

注意：原版 Steam 邀请流程在未进入存档时可能只提示“必须先开始游戏”。因此客户端通常应先选择对应存档进入游戏，再接受邀请或点击“加入上次房间”。

## 推荐重连流程

1. 房主和成员都安装同一版本 Mod。
2. 房主正常开联机房间，成员加入。
3. 大家进入地牢关卡。
4. 房主进入每个有效楼层时，Mod 自动保存本层入口检查点。
5. 成员掉线后，房主面板会把成员标记为掉线保留。
6. 房主先点“本层恢复”，让自己回到本楼层入口检查点，并重建 host。
7. 恢复完成后，Mod 会继续保留/刷新 Steam lobby 重连数据，并打开一段重连窗口。
8. 房主再点“邀请重连”，Steam 邀请窗口会打开，并会尝试自动邀请历史掉线成员。
9. 成员先选择对应存档进入游戏，再通过 Steam 邀请或“加入上次房间”回到房主会话。
10. 安装了 Mod 的成员会自动握手，或手动点击“发送握手”。房主确认成员回到原槽位后继续游玩，不建议再点一次“本层恢复”。

## 恢复逻辑

本 Mod 不保存运行中的所有对象，也不保存 UI 图标。核心策略是复用游戏原本的存档和开局流程。

当前实现流程：

1. Harmony 监听 `DungeonManager.HandleStartFloorAfterSavingServerside()`。
2. 房主在有效楼层入口复制 `SaveManager.CurrentRun`，生成检查点 `.sav` 和 `.json` 元数据。
3. 检查点保存楼层 guid、楼层名、stage 名、存档槽、局内标识、检查点哈希和本局章节缓存。
4. 房主记录会话 ID、当前检查点、成员 SteamID、玩家槽位、重连令牌和成员状态。
5. 成员掉线时，房主把该 SteamID 标记为“掉线保留”。
6. 执行本层恢复时，Mod 把检查点复制为当前存档的 `TMP.sav`。
7. Mod 停止 host，清理部分运行时状态，再调用游戏读档流程。
8. Mod 重新 StartHost，打开重连窗口，恢复或重建 Steam lobby 数据。
9. 成员重新加入并握手后，房主按 SteamID 尝试绑定回原玩家槽位。

恢复前会清理旧玩家运行时 miracle、背包临时加成、fruit skewer 加成、buff、orphaned status、全局道具属性表和效果 HUD。Mod 不保存 buff 图标本身，读档后由游戏按角色状态、背包和道具重新生成。

## 本局判断和章节缓存

Mod 使用 `Seed + CurrentGame` 判断是否仍是同一局。

`FloorCount` 会随着楼层推进变化，所以它不会再用于清空会话成员。这样从第 1 层进入第 2 层时，不会因为 `FloorCount` 改变就清掉成员和检查点。

Steam lobby 的章节字符串，例如 `5-4`，不是简单等于 `CurrentGame`。原版会从任务进度和章节数据推导 lobby `Chapter`。为了避免本层恢复后章节退化成 `5` 或错误值，Mod 会在本局首次有效楼层入口缓存一次章节，并把缓存绑定到 `Seed + CurrentGame`。换局或换存档后，这个缓存会自动失效。

## Steam 房间和邀请

原版游戏在本局开始后通常会限制中途加入，因此只靠 Steam 好友列表右键加入并不稳定。

Mod 在需要重连时会短时间打开 join window：

- `HorayNetworkAuthenticator.allowConnection = true`
- `HorayNetworkAuthenticator.AccessDeny_InDungeon = false`
- Steam lobby 设置为可加入

窗口持续时间由 `ReconnectJoinWindowSeconds` 控制，默认 180 秒。窗口结束后，Mod 会尝试恢复原本的地牢中加入限制和 lobby joinable 状态。

Mod 发布到 Steam lobby 的重连数据包括：

- `ReconnectMod`
- `ReconnectSessionId`
- `ReconnectCheckpointId`
- `ReconnectCheckpointHash`

恢复时还会捕获并恢复原 lobby 的：

- 房间名
- 游戏版本
- lobby 类型
- 最大人数
- `Chapter`
- GameServer/owner 连接信息

如果本层恢复或邀请流程发现没有 active lobby，房主侧会尝试创建新的私密 lobby，默认最大人数为 4。这个行为主要服务于重连窗口，不等同于完整支持“单机中途开房拉新玩家”。

## 配置

配置文件：

```text
Sephiria\AddOns\SephiriaReconnect\config.json
```

默认配置：

```json
{
  "AutoSendHello": true,
  "AutoCaptureFloorCheckpoint": true,
  "ClientHelloIntervalSeconds": 15,
  "MaxAutoHelloAttempts": 3,
  "ReconnectTimeoutSeconds": 300,
  "RequireAllPlayersBeforeRestore": false,
  "AllowHostPrepareCheckpointRestore": true,
  "AutoPlaceIconAfterLowerLeftHud": true,
  "MaxCheckpointCount": 12,
  "MaxCheckpointPruneBatch": 4,
  "HostSessionRefreshSeconds": 5,
  "HostPlayerRefreshSeconds": 3,
  "HostLobbyRefreshSeconds": 10,
  "HostLobbyPublishSeconds": 5,
  "ForceOpenInDungeonJoinForReconnect": true,
  "ForceLobbyJoinableForReconnect": true,
  "ReconnectJoinWindowSeconds": 180,
  "EnableFileLogging": true,
  "MaxLogFiles": 8,
  "MaxLogFileBytes": 1048576,
  "LogRetentionDays": 7,
  "LogPruneIntervalSeconds": 600,
  "IconGap": 8.0,
  "IconSize": 56.0,
  "IconOffsetX": 226.0,
  "IconOffsetY": 22.0
}
```

常用说明：

- `AutoSendHello`：客户端连接后自动发送握手。
- `ClientHelloIntervalSeconds`：自动握手间隔。
- `MaxAutoHelloAttempts`：自动握手最大次数，成功后停止。
- `RequireAllPlayersBeforeRestore`：是否要求所有历史成员回房后才允许恢复。
- `MaxCheckpointCount`：最多保留多少个检查点。
- `HostLobbyRefreshSeconds`：房主刷新 Steam lobby 成员的间隔。
- `HostLobbyPublishSeconds`：房主发布重连 lobby 数据的最小间隔。
- `ForceOpenInDungeonJoinForReconnect`：重连窗口期间是否临时允许地牢中连接。
- `ForceLobbyJoinableForReconnect`：重连窗口期间是否把 lobby 设为可加入。
- `ReconnectJoinWindowSeconds`：重连窗口持续时间。
- `EnableFileLogging`：是否写入 Mod 文件日志。
- `IconGap` 和 `IconSize`：重连图标布局参数。通常不需要改。

## 日志

日志目录：

```text
Sephiria\AddOns\SephiriaReconnect\logs
```

默认日志策略：

- 日志先进入内存缓冲，约每 2 秒或累计到 32 KB 后批量写盘。
- 最多保留 8 个日志文件。
- 单个日志超过 1 MB 后轮转。
- 自动删除 7 天前的旧日志。
- 每 600 秒执行一次日志清理检查。

排查联机问题时，优先查看：

- 房主的 `logs/reconnect-*.log`
- 客户端的 `logs/reconnect-*.log`
- Unity `Player.log`

## 已知限制

- 恢复目标是楼层入口检查点，不是掉线瞬间状态。
- 本层恢复会 StopHost 再 StartHost，成员连接会经历一次重建。
- 如果成员未安装 Mod，只能走原版 Steam 邀请路径，不会发送 Mod 握手。
- 原版对“本局开始后新增玩家”的容错有限，本 Mod 主要面向原本参与过本局、掉线后回来的玩家。
- 完全新玩家中途加入可能出现初始状态、槽位或同步数据不完整。
- Steam 邀请链路依赖 Steam overlay、Steam lobby 状态、双方游戏版本和网络状态。
- 客户端通常需要先选择对应存档进入游戏，再接受重连 lobby 或使用“加入上次房间”。
- 如果双方游戏版本不同，原版仍可能拒绝加入。
- 如果双方 Mod 版本不同，Mod 握手会被拒绝。

## 常见问题

### 没有 AddOns 文件夹怎么办？

在游戏根目录手动创建：

```text
Sephiria\AddOns
```

然后把 Mod 放到：

```text
Sephiria\AddOns\SephiriaReconnect
```

### 需要安装 0Harmony 吗？

不需要单独安装。发布包已经附带 `0Harmony.dll`，放在 `SephiriaReconnect.dll` 同目录即可。

### 需要 BepInEx 吗？

不需要。本 Mod 是原生 AddOn，不是 BepInEx 插件。

### 房主和客户端都要安装吗？

强烈建议都安装同一版本。房主安装是核心，客户端安装后才能握手、记录上次房间，并提高槽位恢复成功率。

### 未安装 Mod 的朋友能进来吗？

有可能能通过 Steam 邀请进来，但不会参与 Mod 握手，不能保证槽位恢复和状态同步稳定。

### 为什么重连后回到本层入口？

这是设计目标。Mod 保存的是楼层入口检查点，不是实时战斗状态。

### 为什么面板不显示完整 SteamID？

SteamID 太长，不适合面板显示。Mod 内部仍使用 SteamID 做成员识别、邀请和槽位绑定。

## 开发

需要本机存在《Sephiria》Managed 程序集。默认路径：

```text
E:\steam\steamapps\common\Sephiria\Sephiria_Data\Managed
```

这是游戏安装目录自带的 Managed 程序集，不是反编译产物。

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

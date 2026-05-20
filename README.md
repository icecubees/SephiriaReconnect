# SephiriaReconnect

SephiriaReconnect 是一个为《Sephiria》制作的原生 AddOn 断线重连 Mod。它不是 BepInEx 插件；当前项目使用游戏自带 AddOn 加载方式，并随 Mod 一起放置 Harmony 运行库。

当前目标是尽量解决多人游戏中途掉线后的恢复问题：

- 房主在每层入口自动保存“本层恢复”检查点。
- 成员掉线后，房主保留该成员的 SteamID、玩家槽位和重连状态。
- 房主可以通过 Steam 邀请机制把历史掉线成员拉回房间。
- 掉线成员回到房间后，Mod 会发送握手，房主把该 SteamID 重新绑定到原玩家槽位。
- 房主可以执行“本层恢复”，让本局回到进入该楼层时的状态。
- UI 使用 Unity 面板，不使用 IMGUI。
- 左下角重连图标会自动跟随原生 HUD 图标组，并在其他界面/过场隐藏。

## 安装

把发布目录复制到游戏 AddOns 目录：

```text
E:\steam\steamapps\common\Sephiria\AddOns\SephiriaReconnect
```

目录内至少应包含：

```text
SephiriaReconnect.dll
0Harmony.dll
metadata.json
config.json
assets
README.md
```

其他玩家如果要完整参与断线重连，也需要安装同一份 Mod。未安装 Mod 的玩家可能仍可通过原版联机加入，但无法参与 Mod 的握手、槽位保留和状态恢复流程。

## 面板功能

点击左下角绿色兔头图标可打开重连面板。

- `本层恢复`：房主使用。把当前局恢复到最近一次楼层入口检查点。当前实现允许成员还没回房时先恢复，恢复后会继续保留/刷新 Steam lobby 数据。
- `邀请重连`：房主使用。打开 Steam 邀请窗口，并尝试对历史掉线成员发送定向 Steam 邀请。
- `发送握手`：客户端使用。向房主发送重连握手，包含本机 SteamID、上次会话和检查点信息。
- `加入上次房间`：客户端使用。尝试加入上次记录的 Steam lobby，再走原版 Steam 连接流程连接房主。

## 重连逻辑

本 Mod 的核心思路不是在运行中强行修补所有活体对象，而是复用游戏原版读档和开局流程：

1. 房主进入新楼层时，Harmony 监听游戏原本的楼层保存流程。
2. Mod 复制当前 `CurrentRun` 临时存档，生成本层入口检查点。
3. 玩家掉线后，房主记录该玩家为“掉线保留”。
4. 玩家通过 Steam 邀请或“加入上次房间”回到房主房间。
5. 握手成功后，房主把该 SteamID 绑定回原来的玩家存档槽位。
6. 房主执行“本层恢复”时，Mod 停止主机、载入检查点、重新 StartHost，并重新发布 Steam lobby 重连数据。

这样可以让血量、背包、神器、果串、临时加成等状态尽量回到进入本楼层时的保存状态。

## Steam 房间说明

原版游戏在本局开始后会把 Steam lobby 设为不可加入，并让客户端离开 lobby，因此中途加入不是原版正式支持的流程。Mod 会在需要邀请/恢复时重新确保 lobby 存在，并写入重连数据。

如果单机状态下使用“本层恢复”触发了 Steam lobby 创建，Mod 会把该 lobby 设置为私密房间，最大人数默认为 4。

## 配置

配置文件位置：

```text
AddOns\SephiriaReconnect\config.json
```

常用字段：

```json
{
  "AutoSendHello": true,
  "AutoCaptureFloorCheckpoint": true,
  "ClientHelloIntervalSeconds": 15,
  "MaxAutoHelloAttempts": 3,
  "RequireAllPlayersBeforeRestore": false,
  "AutoPlaceIconAfterLowerLeftHud": true,
  "MaxCheckpointCount": 12,
  "MaxCheckpointPruneBatch": 4,
  "HostSessionRefreshSeconds": 5,
  "HostPlayerRefreshSeconds": 3,
  "HostLobbyRefreshSeconds": 10,
  "HostLobbyPublishSeconds": 5,
  "EnableFileLogging": true,
  "MaxLogFiles": 8,
  "MaxLogFileBytes": 1048576,
  "LogRetentionDays": 7
}
```

## 日志

运行日志会写入：

```text
AddOns\SephiriaReconnect\logs
```

默认策略：

- 最多保留 8 个日志文件。
- 单个日志超过 1 MB 自动轮转。
- 删除 7 天前的旧日志。
- 清理检查默认每 600 秒执行一次。

日志只捕获带 `[SephiriaReconnect]` 前缀的 Mod 日志，避免把游戏整体日志无限写入文件。

## 当前限制

- 原版对“本局已经开始后新增玩家”的容错很弱。Mod 主要面向原本参与过本局、掉线后回来的玩家。
- 中途拉入完全新玩家仍可能出现初始状态不完整、槽位缺少存档数据等问题。
- 当前恢复目标是“本楼层入口状态”，不是掉线瞬间原地恢复。
- Steam 邀请链路依赖 Steam overlay、Steam lobby 状态和双方游戏版本一致。

## 开发

构建：

```text
dotnet build SephiriaReconnect.csproj -c Release
```

构建产物会输出到：

```text
dist
```

项目源码主要位于：

```text
src
```

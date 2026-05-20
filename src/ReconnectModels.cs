using System;
using System.Collections.Generic;

namespace SephiriaReconnect;

public enum ReconnectMemberState
{
    Online,
    OfflineReserved,
    ReconnectPending,
    ReconnectedWaiting,
    ExitedGracefully,
    Removed
}

public sealed class ReconnectConfig
{
    public bool AutoSendHello = true;
    public bool AutoCaptureFloorCheckpoint = true;
    public int ClientHelloIntervalSeconds = 15;
    public int MaxAutoHelloAttempts = 3;
    public int ReconnectTimeoutSeconds = 300;
    public bool RequireAllPlayersBeforeRestore = false;
    public bool AllowHostPrepareCheckpointRestore = true;
    public bool AutoPlaceIconAfterLowerLeftHud = true;
    public int MaxCheckpointCount = 12;
    public int MaxCheckpointPruneBatch = 4;
    public int HostSessionRefreshSeconds = 5;
    public int HostPlayerRefreshSeconds = 3;
    public int HostLobbyRefreshSeconds = 10;
    public int HostLobbyPublishSeconds = 5;
    public bool ForceOpenInDungeonJoinForReconnect = true;
    public bool ForceLobbyJoinableForReconnect = true;
    public int ReconnectJoinWindowSeconds = 180;
    public bool EnableFileLogging = true;
    public int MaxLogFiles = 8;
    public int MaxLogFileBytes = 1048576;
    public int LogRetentionDays = 7;
    public int LogPruneIntervalSeconds = 600;
    public float IconGap = 8f;
    public float IconSize = 56f;
    public float IconOffsetX = 226f;
    public float IconOffsetY = 22f;
}

public sealed class ReconnectMemberRecord
{
    public ulong SteamId;
    public string PlayerName;
    public int PlayerSlot = -1;
    public string ReconnectToken;
    public string SessionId;
    public string SaveSlotId;
    public string RunId;
    public string CurrentFloorId;
    public string CheckpointId;
    public bool ModInstalled;
    public DateTime LastHelloUtc;
    public DateTime LastSeenUtc;
    public ReconnectMemberState State;
}

public sealed class ReconnectCheckpointRecord
{
    public string CheckpointId;
    public string CheckpointHash;
    public string Reason;
    public bool IsRestoreTarget;
    public string FloorGuid;
    public string FloorName;
    public string StageName;
    public string RunId;
    public string SaveSlotId;
    public string SnapshotPath;
    public DateTime CreatedUtc;
}

public sealed class ReconnectSessionRecord
{
    public int SchemaVersion = 1;
    public string ProtocolVersion = ReconnectController.ProtocolVersion;
    public ulong HostSteamId;
    public ulong LobbyId;
    public string SessionId;
    public string SaveSlotId;
    public string RunId;
    public string CurrentFloorId;
    public string CurrentCheckpointId;
    public string CurrentCheckpointHash;
    public DateTime CreatedUtc;
    public DateTime UpdatedUtc;
    public Dictionary<ulong, ReconnectMemberRecord> Members = new Dictionary<ulong, ReconnectMemberRecord>();
    public List<ReconnectCheckpointRecord> Checkpoints = new List<ReconnectCheckpointRecord>();
}

public sealed class LastSessionRecord
{
    public int SchemaVersion = 1;
    public string ProtocolVersion = ReconnectController.ProtocolVersion;
    public ulong PlayerSteamId;
    public ulong HostSteamId;
    public ulong LobbyId;
    public string SessionId;
    public string ReconnectToken;
    public string SaveSlotId;
    public string RunId;
    public string CurrentFloorId;
    public string CheckpointId;
    public string CheckpointHash;
    public DateTime LastSeenUtc;
}

using Mirror;

namespace SephiriaReconnect;

public struct ReconnectHelloMessage : NetworkMessage
{
    public string protocolVersion;
    public ulong playerSteamId;
    public string playerName;
    public string saveSlotId;
    public string runId;
    public string sessionId;
    public string reconnectToken;
    public string checkpointId;
    public string checkpointHash;
    public bool reconnectAttempt;
}

public struct ReconnectHelloReplyMessage : NetworkMessage
{
    public bool accepted;
    public string reason;
    public ulong hostSteamId;
    public ulong lobbyId;
    public string saveSlotId;
    public string runId;
    public string sessionId;
    public string reconnectToken;
    public string checkpointId;
    public string checkpointHash;
}

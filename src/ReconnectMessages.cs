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

public static class ReconnectMessageSerializers
{
    private static bool registered;

    public static void Register()
    {
        if (registered)
        {
            return;
        }

        Writer<ReconnectHelloMessage>.write = WriteHello;
        Reader<ReconnectHelloMessage>.read = ReadHello;
        Writer<ReconnectHelloReplyMessage>.write = WriteHelloReply;
        Reader<ReconnectHelloReplyMessage>.read = ReadHelloReply;
        registered = true;
    }

    private static void WriteHello(NetworkWriter writer, ReconnectHelloMessage value)
    {
        writer.WriteString(value.protocolVersion ?? "");
        writer.WriteULong(value.playerSteamId);
        writer.WriteString(value.playerName ?? "");
        writer.WriteString(value.saveSlotId ?? "");
        writer.WriteString(value.runId ?? "");
        writer.WriteString(value.sessionId ?? "");
        writer.WriteString(value.reconnectToken ?? "");
        writer.WriteString(value.checkpointId ?? "");
        writer.WriteString(value.checkpointHash ?? "");
        writer.WriteBool(value.reconnectAttempt);
    }

    private static ReconnectHelloMessage ReadHello(NetworkReader reader)
    {
        return new ReconnectHelloMessage
        {
            protocolVersion = reader.ReadString(),
            playerSteamId = reader.ReadULong(),
            playerName = reader.ReadString(),
            saveSlotId = reader.ReadString(),
            runId = reader.ReadString(),
            sessionId = reader.ReadString(),
            reconnectToken = reader.ReadString(),
            checkpointId = reader.ReadString(),
            checkpointHash = reader.ReadString(),
            reconnectAttempt = reader.ReadBool()
        };
    }

    private static void WriteHelloReply(NetworkWriter writer, ReconnectHelloReplyMessage value)
    {
        writer.WriteBool(value.accepted);
        writer.WriteString(value.reason ?? "");
        writer.WriteULong(value.hostSteamId);
        writer.WriteULong(value.lobbyId);
        writer.WriteString(value.saveSlotId ?? "");
        writer.WriteString(value.runId ?? "");
        writer.WriteString(value.sessionId ?? "");
        writer.WriteString(value.reconnectToken ?? "");
        writer.WriteString(value.checkpointId ?? "");
        writer.WriteString(value.checkpointHash ?? "");
    }

    private static ReconnectHelloReplyMessage ReadHelloReply(NetworkReader reader)
    {
        return new ReconnectHelloReplyMessage
        {
            accepted = reader.ReadBool(),
            reason = reader.ReadString(),
            hostSteamId = reader.ReadULong(),
            lobbyId = reader.ReadULong(),
            saveSlotId = reader.ReadString(),
            runId = reader.ReadString(),
            sessionId = reader.ReadString(),
            reconnectToken = reader.ReadString(),
            checkpointId = reader.ReadString(),
            checkpointHash = reader.ReadString()
        };
    }
}

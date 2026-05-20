using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SephiriaReconnect;

public sealed class ReconnectStore
{
    public string RootDirectory { get; }
    public string CheckpointDirectory { get; }
    public string ConfigPath => Path.Combine(RootDirectory, "config.json");
    public string HostSessionPath => Path.Combine(RootDirectory, "host_session.json");
    public string LastSessionPath => Path.Combine(RootDirectory, "last_session.json");

    private readonly JsonSerializerSettings jsonSettings = new JsonSerializerSettings
    {
        Formatting = Formatting.Indented
    };

    public ReconnectStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        CheckpointDirectory = Path.Combine(rootDirectory, "checkpoints");
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(CheckpointDirectory);
    }

    public ReconnectConfig LoadConfig()
    {
        return LoadJson(ConfigPath, new ReconnectConfig());
    }

    public void SaveConfig(ReconnectConfig config)
    {
        SaveJson(ConfigPath, config);
    }

    public ReconnectSessionRecord LoadHostSession()
    {
        return LoadJson<ReconnectSessionRecord>(HostSessionPath, null);
    }

    public void SaveHostSession(ReconnectSessionRecord session)
    {
        SaveJson(HostSessionPath, session);
    }

    public LastSessionRecord LoadLastSession()
    {
        return LoadJson<LastSessionRecord>(LastSessionPath, null);
    }

    public void SaveLastSession(LastSessionRecord session)
    {
        SaveJson(LastSessionPath, session);
    }

    public string GetCheckpointSavePath(string checkpointId)
    {
        return Path.Combine(CheckpointDirectory, checkpointId + ".sav");
    }

    public string GetCheckpointMetaPath(string checkpointId)
    {
        return Path.Combine(CheckpointDirectory, checkpointId + ".json");
    }

    public void SaveCheckpointMeta(ReconnectCheckpointRecord checkpoint)
    {
        SaveJson(GetCheckpointMetaPath(checkpoint.CheckpointId), checkpoint);
    }

    public void DeleteCheckpointFiles(ReconnectCheckpointRecord checkpoint)
    {
        if (checkpoint == null)
        {
            return;
        }

        DeleteCheckpointFiles(checkpoint.CheckpointId, checkpoint.SnapshotPath);
    }

    public void DeleteCheckpointFiles(string checkpointId, string snapshotPath = null)
    {
        if (string.IsNullOrEmpty(checkpointId))
        {
            return;
        }

        TryDeleteFile(GetCheckpointMetaPath(checkpointId));
        TryDeleteFile(string.IsNullOrEmpty(snapshotPath) ? GetCheckpointSavePath(checkpointId) : snapshotPath);
    }

    public IEnumerable<string> EnumerateCheckpointIds()
    {
        if (!Directory.Exists(CheckpointDirectory))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(CheckpointDirectory, "*.*", SearchOption.TopDirectoryOnly))
        {
            string extension = Path.GetExtension(file);
            if (string.Equals(extension, ".sav", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFileNameWithoutExtension(file);
            }
        }
    }

    private T LoadJson<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), jsonSettings);
        }
        catch (Exception ex)
        {
            Debug.LogError("[SephiriaReconnect] Failed to read " + path + ": " + ex);
            return fallback;
        }
    }

    private void SaveJson<T>(string path, T data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(data, jsonSettings));
        }
        catch (Exception ex)
        {
            Debug.LogError("[SephiriaReconnect] Failed to write " + path + ": " + ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SephiriaReconnect] Failed to delete " + path + ": " + ex.Message);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SephiriaReconnect;

public static class ReconnectLogger
{
    private const string LogPrefix = "[SephiriaReconnect]";
    private static readonly object Sync = new object();

    private static string logDirectory;
    private static string currentLogPath;
    private static int currentLogIndex;
    private static bool initialized;
    private static bool subscribed;
    private static bool fileLoggingEnabled = true;
    private static int maxLogFiles = 8;
    private static long maxLogFileBytes = 1048576;
    private static int logRetentionDays = 7;
    private static float pruneIntervalSeconds = 600f;
    private static float nextPruneAt;

    public static string CurrentLogPath => currentLogPath;

    public static void Initialize(string rootDirectory, ReconnectConfig config)
    {
        lock (Sync)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                return;
            }

            logDirectory = Path.Combine(rootDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            ApplyConfig(config);
            currentLogIndex = 0;
            currentLogPath = CreateLogPath();
            initialized = true;

            if (!subscribed)
            {
                Application.logMessageReceived += HandleUnityLog;
                subscribed = true;
            }

            PruneLogs(force: true);
        }
    }

    public static void Configure(ReconnectConfig config)
    {
        lock (Sync)
        {
            ApplyConfig(config);
            PruneLogs(force: true);
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (subscribed)
            {
                Application.logMessageReceived -= HandleUnityLog;
                subscribed = false;
            }

            initialized = false;
            currentLogPath = null;
            logDirectory = null;
        }
    }

    public static void Info(string message)
    {
        Debug.Log(LogPrefix + " " + message);
    }

    public static void Warning(string message)
    {
        Debug.LogWarning(LogPrefix + " " + message);
    }

    public static void Error(string message)
    {
        Debug.LogError(LogPrefix + " " + message);
    }

    private static void ApplyConfig(ReconnectConfig config)
    {
        config ??= new ReconnectConfig();
        fileLoggingEnabled = config.EnableFileLogging;
        maxLogFiles = Math.Max(1, config.MaxLogFiles);
        maxLogFileBytes = Math.Max(64 * 1024, config.MaxLogFileBytes);
        logRetentionDays = Math.Max(0, config.LogRetentionDays);
        pruneIntervalSeconds = Math.Max(60f, config.LogPruneIntervalSeconds);
    }

    private static void HandleUnityLog(string condition, string stackTrace, LogType type)
    {
        if (!initialized || !fileLoggingEnabled || string.IsNullOrEmpty(condition) || !condition.Contains(LogPrefix))
        {
            return;
        }

        lock (Sync)
        {
            try
            {
                RotateIfNeeded();
                PruneLogs(force: false);

                StringBuilder builder = new StringBuilder(256);
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.Append(" [").Append(type).Append("] ");
                builder.AppendLine(condition);

                if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !string.IsNullOrEmpty(stackTrace))
                {
                    builder.AppendLine(stackTrace);
                }

                File.AppendAllText(currentLogPath, builder.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Logging must never interfere with gameplay or reconnect handling.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (string.IsNullOrEmpty(currentLogPath))
        {
            currentLogPath = CreateLogPath();
            return;
        }

        FileInfo info = new FileInfo(currentLogPath);
        if (info.Exists && info.Length >= maxLogFileBytes)
        {
            currentLogIndex++;
            currentLogPath = CreateLogPath();
        }
    }

    private static string CreateLogPath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string suffix = currentLogIndex > 0 ? "-" + currentLogIndex : "";
        return Path.Combine(logDirectory, "reconnect-" + stamp + suffix + ".log");
    }

    private static void PruneLogs(bool force)
    {
        if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory))
        {
            return;
        }

        if (!force && Time.unscaledTime < nextPruneAt)
        {
            return;
        }

        nextPruneAt = Time.unscaledTime + pruneIntervalSeconds;

        FileInfo[] files = new DirectoryInfo(logDirectory)
            .EnumerateFiles("reconnect-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        if (logRetentionDays > 0)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-logRetentionDays);
            foreach (FileInfo file in files)
            {
                if (!IsCurrentLog(file) && file.LastWriteTimeUtc < cutoff)
                {
                    TryDelete(file);
                }
            }
        }

        files = new DirectoryInfo(logDirectory)
            .EnumerateFiles("reconnect-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        int kept = 0;
        foreach (FileInfo file in files)
        {
            if (IsCurrentLog(file))
            {
                kept++;
                continue;
            }

            if (kept >= maxLogFiles)
            {
                TryDelete(file);
                continue;
            }

            kept++;
        }
    }

    private static bool IsCurrentLog(FileInfo file)
    {
        return !string.IsNullOrEmpty(currentLogPath) &&
            string.Equals(file.FullName, currentLogPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
        }
    }
}

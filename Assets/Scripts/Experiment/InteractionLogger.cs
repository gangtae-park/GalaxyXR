using System;
using System.Globalization;
using System.IO;
using UnityEngine;

[Serializable]
public class InteractionLogEntry
{
    public string timestamp;
    public string input_mode;
    public string source;
    public string raw_transcript;
    public string parsed_command;
    public string target_strategy;
    public string sent_packet_type;
    public string request_id;
    public bool success;
    public string error_message;
    public float latency_ms;
}

public class InteractionLogger : MonoBehaviour
{
    public bool logToConsole = true;
    public bool appendCsv = false;
    public string csvFileName = "interaction_log.csv";
    public bool logCsvPathOnStart = true;

    private string _csvPath;

    void Awake()
    {
        _csvPath = Path.Combine(Application.persistentDataPath, csvFileName);
        if (appendCsv) EnsureCsvHeader();
        if (appendCsv && logCsvPathOnStart) Debug.Log($"[InteractionLogger] CSV path: {_csvPath}");
    }

    public string NewRequestId()
    {
        return Guid.NewGuid().ToString("N");
    }

    public void LogModeChanged(InputMode mode)
    {
        LogInteraction(new InteractionLogEntry
        {
            input_mode = mode.ToString(),
            source = "mode",
            parsed_command = "ModeChanged",
            request_id = NewRequestId(),
            success = true
        });
    }

    public void LogInteraction(InteractionLogEntry entry)
    {
        if (entry == null) return;
        if (string.IsNullOrEmpty(entry.timestamp)) entry.timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(entry.request_id)) entry.request_id = NewRequestId();

        if (logToConsole)
        {
            Debug.Log(
                $"[InteractionLog] request_id={entry.request_id} mode={entry.input_mode} source={entry.source} " +
                $"command={entry.parsed_command} target={entry.target_strategy} packet={entry.sent_packet_type} " +
                $"success={entry.success} transcript='{entry.raw_transcript}' error='{entry.error_message}'");
        }

        if (appendCsv) AppendCsv(entry);
    }

    void EnsureCsvHeader()
    {
        try
        {
            string directory = Path.GetDirectoryName(_csvPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(_csvPath))
            {
                File.AppendAllText(_csvPath,
                    "timestamp,input_mode,source,raw_transcript,parsed_command,target_strategy,sent_packet_type,request_id,success,error_message,latency_ms\n");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[InteractionLogger] failed to create CSV header: {e.Message}");
        }
    }

    void AppendCsv(InteractionLogEntry entry)
    {
        try
        {
            EnsureCsvHeader();
            string line = string.Join(",",
                Csv(entry.timestamp),
                Csv(entry.input_mode),
                Csv(entry.source),
                Csv(entry.raw_transcript),
                Csv(entry.parsed_command),
                Csv(entry.target_strategy),
                Csv(entry.sent_packet_type),
                Csv(entry.request_id),
                entry.success ? "1" : "0",
                Csv(entry.error_message),
                entry.latency_ms.ToString("F2", CultureInfo.InvariantCulture));
            File.AppendAllText(_csvPath, line + "\n");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[InteractionLogger] failed to append CSV: {e.Message}");
        }
    }

    static string Csv(string value)
    {
        if (value == null) value = "";
        return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}

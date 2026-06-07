using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class AndroidXrAdbDiagnostics
{
    private const string MenuPath = "Tools/Android XR/Check ADB";
    private const string ConfigureMenuPath = "Tools/Android XR/Configure Direct Preview";
    private const string SelectedRuntimeVariable = "XR_SELECTED_RUNTIME_JSON";
    private const string StreamingRuntimeConfig =
        @"C:\Program Files\Google\AndroidXRStreaming\config.json";
    private const string SystemAdbDirectory =
        @"C:\Android\platform-tools";
    private const string EngineHubAdbDirectory =
        @"C:\Program Files\Google\AndroidXREngineHub";
    private const string UnityAdbDirectory =
        @"C:\Program Files\Unity\Hub\Editor\6000.4.0f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools";

    static AndroidXrAdbDiagnostics()
    {
        ConfigureCurrentUnityProcess(false);
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
        EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
    }

    [MenuItem(ConfigureMenuPath)]
    public static void ConfigureDirectPreview()
    {
        bool configured = ConfigureCurrentUnityProcess(true);
        if (configured)
        {
            EditorUtility.DisplayDialog(
                "Android XR Direct Preview",
                "Unity's current process is configured for Android XR Streaming.\n\n" +
                "Open Project Validation again, then enter Play Mode.",
                "OK");
        }
    }

    private static void HandlePlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            ConfigureCurrentUnityProcess(true);
        }
    }

    [MenuItem(MenuPath)]
    public static void CheckAdb()
    {
        string processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string adbPath = FindAdb(processPath);

        if (string.IsNullOrEmpty(adbPath))
        {
            string message =
                "Unity cannot find adb.exe on its PATH.\n\n" +
                "Run Tools > Android XR > Configure Direct Preview.\n\n" +
                $"Unity PATH:\n{processPath}";

            UnityEngine.Debug.LogError($"[Android XR ADB]\n{message}");
            EditorUtility.DisplayDialog("Android XR ADB: Not Found", message, "OK");
            return;
        }

        try
        {
            ProcessResult result = RunProcess(adbPath, "devices -l");
            string report =
                $"ADB path: {adbPath}\n" +
                $"Play Mode runtime: {Environment.GetEnvironmentVariable(SelectedRuntimeVariable)}\n" +
                $"Exit code: {result.ExitCode}\n\n" +
                $"{result.StandardOutput.Trim()}\n" +
                $"{result.StandardError.Trim()}";

            if (result.ExitCode == 0 &&
                result.StandardOutput.IndexOf(" device ", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                UnityEngine.Debug.Log($"[Android XR ADB] Connection OK\n{report}");
                EditorUtility.DisplayDialog("Android XR ADB: Connected", report, "OK");
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[Android XR ADB] ADB found, but no authorized device was detected.\n{report}");
                EditorUtility.DisplayDialog("Android XR ADB: Check Device", report, "OK");
            }
        }
        catch (Exception exception)
        {
            string message = $"Failed to run:\n{adbPath}\n\n{exception}";
            UnityEngine.Debug.LogError($"[Android XR ADB] {message}");
            EditorUtility.DisplayDialog("Android XR ADB: Error", message, "OK");
        }
    }

    private static bool ConfigureCurrentUnityProcess(bool logResult)
    {
        string processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string adbPath = null;

        if (File.Exists(Path.Combine(SystemAdbDirectory, "adb.exe")))
        {
            processPath = PrependPathEntry(processPath, SystemAdbDirectory);
            Environment.SetEnvironmentVariable("PATH", processPath);
            adbPath = Path.Combine(SystemAdbDirectory, "adb.exe");
        }
        else
        {
            adbPath = FindAdb(processPath);
        }

        if (string.IsNullOrEmpty(adbPath))
        {
            string adbDirectory = Directory.Exists(EngineHubAdbDirectory)
                ? EngineHubAdbDirectory
                : UnityAdbDirectory;

            if (File.Exists(Path.Combine(adbDirectory, "adb.exe")))
            {
                processPath = string.IsNullOrEmpty(processPath)
                    ? adbDirectory
                    : processPath.TrimEnd(Path.PathSeparator) + Path.PathSeparator + adbDirectory;
                Environment.SetEnvironmentVariable("PATH", processPath);
                adbPath = Path.Combine(adbDirectory, "adb.exe");
            }
        }

        bool runtimeAvailable = File.Exists(StreamingRuntimeConfig);
        if (runtimeAvailable)
        {
            Environment.SetEnvironmentVariable(SelectedRuntimeVariable, StreamingRuntimeConfig);
        }

        bool configured = !string.IsNullOrEmpty(adbPath) && runtimeAvailable;
        if (logResult || !configured)
        {
            string message =
                $"ADB: {(string.IsNullOrEmpty(adbPath) ? "not found" : adbPath)}\n" +
                $"Play Mode runtime: {(runtimeAvailable ? StreamingRuntimeConfig : "not found")}";

            if (configured)
                UnityEngine.Debug.Log($"[Android XR Direct Preview] Configured\n{message}");
            else
                UnityEngine.Debug.LogWarning($"[Android XR Direct Preview] Configuration incomplete\n{message}");
        }

        return configured;
    }

    private static string PrependPathEntry(string pathValue, string directory)
    {
        string[] entries = pathValue.Split(Path.PathSeparator);
        foreach (string entry in entries)
        {
            if (string.Equals(entry.Trim().Trim('"'), directory, StringComparison.OrdinalIgnoreCase))
                return pathValue;
        }

        return directory + Path.PathSeparator + pathValue;
    }

    private static string FindAdb(string pathValue)
    {
        string systemAdb = Path.Combine(SystemAdbDirectory, "adb.exe");
        if (File.Exists(systemAdb))
            return systemAdb;

        string adbPath = FindExecutableOnPath("adb.exe", pathValue);
        if (!string.IsNullOrEmpty(adbPath))
            return adbPath;

        string engineHubAdb = Path.Combine(EngineHubAdbDirectory, "adb.exe");
        if (File.Exists(engineHubAdb))
            return engineHubAdb;

        string unityAdb = Path.Combine(UnityAdbDirectory, "adb.exe");
        return File.Exists(unityAdb) ? unityAdb : null;
    }

    private static string FindExecutableOnPath(string executableName, string pathValue)
    {
        foreach (string rawDirectory in pathValue.Split(Path.PathSeparator))
        {
            string directory = rawDirectory.Trim().Trim('"');
            if (string.IsNullOrEmpty(directory))
                continue;

            try
            {
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception)
            {
                // Ignore malformed PATH entries and continue checking the rest.
            }
        }

        return null;
    }

    private static ProcessResult RunProcess(string executablePath, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Windows could not start adb.exe: {exception.Message}", exception);
        }

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(10000))
        {
            process.Kill();
            throw new TimeoutException("adb devices did not finish within 10 seconds.");
        }

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private readonly struct ProcessResult
    {
        public ProcessResult(int exitCode, string standardOutput, string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        public int ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }
    }
}

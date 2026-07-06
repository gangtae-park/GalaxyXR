using UnityEngine;

/*
NetworkSettings

Single source of truth for the MacProgram host + port config, shared across
every scene in the project. Keep the asset at

  Assets/Resources/NetworkSettings.asset

so the static Instance getter can Resources.Load it from any scene without
requiring an inspector reference (though components still expose a
serialized `networkSettings` field for explicit overrides / test scenes).

Editing the IP once in that asset propagates to every script that reads
through this object — MsgSender's UDP address, AskVoiceInputController's
HTTP endpoint, ObjectUiRequestManager's endpoint, VoiceInputManager's
snapshot endpoint, and any future consumer.

Create the asset via `Create → GalaxyXR → Network Settings` when this class
is first added; or use the one shipped alongside this file.
*/

[CreateAssetMenu(menuName = "GalaxyXR/Network Settings", fileName = "NetworkSettings")]
public class NetworkSettings : ScriptableObject
{
    [Header("Server address")]
    [Tooltip("IPv4 address of the MacProgram host. Change this ONE value and every consumer picks it up.")]
    public string serverIP = "192.168.0.8";

    [Header("Ports")]
    [Tooltip("UDP port Python listens on for GESTURE_EVENT / ASK_QUESTION / VOICE_COMMAND / OBJECT_ACTION.")]
    public int commandUdpPort = 5005;
    [Tooltip("HTTP port for the voice server (ask_voice, voice_command, object_ui endpoints).")]
    public int voiceHttpPort = 5007;

    [Header("HTTP paths")]
    public string askVoicePath = "/ask_voice";
    public string voiceCommandPath = "/voice_command";
    public string objectUiPath = "/object_ui";

    // -------- Convenience URL builders --------
    public string AskVoiceUrl     => BuildHttpUrl(askVoicePath);
    public string VoiceCommandUrl => BuildHttpUrl(voiceCommandPath);
    public string ObjectUiUrl     => BuildHttpUrl(objectUiPath);

    public string BuildHttpUrl(string path)
    {
        string p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith("/") ? path : "/" + path);
        return $"http://{serverIP}:{voiceHttpPort}{p}";
    }

    // -------- Runtime singleton --------
    // Loaded lazily from Assets/Resources/NetworkSettings.asset. Cached to
    // avoid a Resources hit per access. Returns null if the asset is missing.
    static NetworkSettings _cached;
    static bool _resourceProbeFailed;

    public static NetworkSettings Instance
    {
        get
        {
            if (_cached != null) return _cached;
            if (_resourceProbeFailed) return null;
            _cached = Resources.Load<NetworkSettings>("NetworkSettings");
            if (_cached == null)
            {
                _resourceProbeFailed = true;
                Debug.LogWarning("[NetworkSettings] No asset at Resources/NetworkSettings. Create one via Create > GalaxyXR > Network Settings and drop it under Assets/Resources/. Consumers will fall back to their per-component fields.");
            }
            return _cached;
        }
    }
}

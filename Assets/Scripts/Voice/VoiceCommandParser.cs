using System;
using UnityEngine;

public enum SemanticCommandType
{
    SearchFindInfo,
    Ask,
    Compare,
    Unknown
}

[Serializable]
public class ParsedVoiceCommand
{
    public SemanticCommandType type;
    public string rawTranscript;
    public string targetText;
    public string targetTextA;
    public string targetTextB;
    public string source;
    public string targetStrategy;
    public string requestId;
}

public class VoiceCommandParser : MonoBehaviour
{
    public bool verboseLogging = true;

    public ParsedVoiceCommand Parse(string transcript, InputMode inputMode)
    {
        string raw = transcript == null ? "" : transcript.Trim();
        string normalized = Normalize(raw);

        ParsedVoiceCommand command = new ParsedVoiceCommand
        {
            rawTranscript = raw,
            targetText = raw,
            source = GetSource(inputMode),
            targetStrategy = GetTargetStrategy(inputMode),
            type = SemanticCommandType.Unknown
        };

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return command;
        }

        if (LooksLikeCompare(normalized))
        {
            command.type = SemanticCommandType.Compare;
            ExtractCompareTargets(raw, command);
        }
        else if (LooksLikeAsk(normalized))
        {
            command.type = SemanticCommandType.Ask;
        }
        else if (LooksLikeSearch(normalized))
        {
            command.type = SemanticCommandType.SearchFindInfo;
        }

        if (verboseLogging)
        {
            Debug.Log($"[VoiceCommandParser] '{raw}' -> {command.type} source={command.source} target={command.targetStrategy}");
        }

        return command;
    }

    static string Normalize(string text)
    {
        return (text ?? "").Trim().ToLowerInvariant().Replace(" ", "");
    }

    static bool LooksLikeSearch(string text)
    {
        return ContainsAny(text,
            "이거뭐야",
            "이물건뭐야",
            "이물체뭐야",
            "뭐야",
            "알려줘",
            "찾아줘",
            "정보",
            "aboutthis",
            "whatisthis",
            "what'sthis",
            "tellmeabout");
    }

    static bool LooksLikeAsk(string text)
    {
        return ContainsAny(text,
            "어떻게써",
            "어떻게사용",
            "사용법",
            "사용하는",
            "무슨용도",
            "용도",
            "설명해줘",
            "설명",
            "질문",
            "howtouse",
            "whatfor",
            "explain");
    }

    static bool LooksLikeCompare(string text)
    {
        return ContainsAny(text,
            "비교",
            "차이",
            "다른점",
            "랑",
            "와",
            "과",
            "compare",
            "difference");
    }

    static bool ContainsAny(string text, params string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
        {
            if (!string.IsNullOrEmpty(needles[i]) && text.Contains(needles[i])) return true;
        }
        return false;
    }

    static string GetSource(InputMode inputMode)
    {
        return inputMode == InputMode.GazeVoice ? "gaze_voice" : "voice";
    }

    static string GetTargetStrategy(InputMode inputMode)
    {
        if (inputMode == InputMode.GazeVoice) return "gaze";
        if (inputMode == InputMode.VoiceOnly) return "screen_center_or_server_context";
        return "gesture_area";
    }

    static void ExtractCompareTargets(string raw, ParsedVoiceCommand command)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        string[] separators = { "랑", "와", "과", "하고", "vs", "VS", "비교", "차이" };
        for (int i = 0; i < separators.Length; i++)
        {
            string[] parts = raw.Split(new[] { separators[i] }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                command.targetTextA = parts[0].Trim();
                command.targetTextB = parts[1].Trim();
                return;
            }
        }
    }
}

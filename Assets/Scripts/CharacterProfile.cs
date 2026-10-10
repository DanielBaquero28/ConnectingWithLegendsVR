using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "CharacterProfile", menuName = "HistorySpeaks/Character Profile")]
public class CharacterProfile : ScriptableObject
{
    [Header("Identity")]
    public string characterName = "Albert Einstein";

    [TextArea(6, 20)]
    public string systemPersona;          // Character-specific voice & personality (see section 7)

    [TextArea(2, 5)]
    public string knowledgeCutoffNote;     // e.g. lifespan + how to handle post-era questions

    [Header("Voice (Google TTS)")]
    public string voiceLanguageCode = "en-GB";
    public string voiceName = "en-GB-Neural2-B";
    public float speakingRate = 0.72f;
    public float pitch = -8.0f;

    [Header("Response")]
    public int maxWords = 40;

    [System.Serializable]
    public class FewShot { [TextArea] public string studentSays; [TextArea] public string characterReplies; }

    [Header("Few-shot examples (injected every request)")]
    public List<FewShot> fewShotExamples = new List<FewShot>();
}

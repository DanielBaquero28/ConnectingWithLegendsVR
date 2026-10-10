using System;
using UnityEngine;

// Carries the hub selection across the HubScene -> character-scene load
// (Spec 7). Static on purpose: it survives a single (non-additive)
// SceneManager.LoadScene without needing any persistent GameObject or
// DontDestroyOnLoad, and there are no SerializeFields to lose on Quest.
public static class CharacterSession
{
    // Used when a character scene is launched directly (Editor testing) and
    // no hub selection exists. Matches the Firestore characters/ doc ID.
    public const string FallbackCharacterId = "einstein";

    public static CharacterDefinition Current { get; private set; }

    // Minted at selection time; the character scene's AIResponseControllerV2
    // adopts it in Start. Server-side history is keyed on this, so every
    // selection starts a clean conversation (no cross-character bleed).
    public static string SessionId { get; private set; }

    // Called by the hub when a card is selected. Always mints a fresh session
    // ID — re-selecting the same character is still a new conversation.
    public static void Select(CharacterDefinition character)
    {
        Current = character;
        NewSessionId();
    }

    public static string NewSessionId()
    {
        SessionId = Guid.NewGuid().ToString();
        return SessionId;
    }

    // What ProxyClient sends as X-Character-Id / the JSON characterId field.
    public static string CharacterId
    {
        get
        {
            if (Current != null && !string.IsNullOrEmpty(Current.characterId))
                return Current.characterId;

            Debug.LogWarning("[CharacterSession] No character selected (scene " +
                             "launched directly?) — falling back to \"" +
                             FallbackCharacterId + "\".");
            return FallbackCharacterId;
        }
    }
}

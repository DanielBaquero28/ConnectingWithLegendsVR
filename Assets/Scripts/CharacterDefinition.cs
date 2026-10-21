using UnityEngine;

// One entry in the character registry (Spec 7). This is SELECTION data only —
// persona, few-shots, and voice config live server-side in Firestore
// (characters/<characterId>); nothing here is sent to a provider.
[CreateAssetMenu(fileName = "CharacterDefinition", menuName = "HistorySpeaks/Character Definition")]
public class CharacterDefinition : ScriptableObject
{
    [Tooltip("Must match the Firestore characters/ doc ID exactly, e.g. \"einstein\".")]
    public string characterId;

    [Tooltip("Name shown on the hub card, e.g. \"Albert Einstein\".")]
    public string displayName;

    [Tooltip("Short era line for the hub card, e.g. \"1879-1955\".")]
    public string era;

    [Tooltip("Card portrait. Locked placeholders can share a silhouette sprite.")]
    public Sprite portrait;

    [Tooltip("Hub card background color. Locked cards render a muted version " +
             "automatically; keep tints dark so the white card text stays readable.")]
    public Color cardTint = new Color(0.16470589f, 0.16470589f, 0.18039216f, 1f); // #2A2A2E

    [Tooltip("Scene loaded when this character is selected. Must be added to " +
             "Build Settings. Empty for locked placeholders.")]
    public string sceneName;

    [Tooltip("False renders the card locked (\"Coming soon\", non-interactable).")]
    public bool available;

    // Spec 12 Part Q: pre-rendered fallback lines in this character's voice,
    // played by AIResponseControllerV2 when it shows the matching text. They
    // live next to the crisis clip in Assets/Characters/Voice Clips/<name>/.
    // Empty = the line is shown as text only.
    [Header("Fallback lines (pre-rendered)")]
    [Tooltip("\"Forgive me — my thoughts seem to wander just now. Would you ask me once more?\" " +
             "Played when a request fails (network error or timeout).")]
    public AudioClip networkErrorClip;

    [Tooltip("\"I didn't quite catch that — please try again.\" Played when the " +
             "server heard nothing in the recording.")]
    public AudioClip didntCatchClip;
}

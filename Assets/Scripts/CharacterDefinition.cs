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
}

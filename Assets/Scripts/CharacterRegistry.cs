using System.Collections.Generic;
using UnityEngine;

// Ordered list of every character the hub can show (Spec 7). The hub builds
// its cards straight from this asset — adding a character to the app is a
// registry entry + a scene, no hub code changes.
[CreateAssetMenu(fileName = "CharacterRegistry", menuName = "HistorySpeaks/Character Registry")]
public class CharacterRegistry : ScriptableObject
{
    [Tooltip("Hub card order, top-left first.")]
    public List<CharacterDefinition> characters = new List<CharacterDefinition>();
}

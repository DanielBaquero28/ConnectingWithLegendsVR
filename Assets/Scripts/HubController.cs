using System.Collections.Generic;
using UnityEngine;
using UnityEngine.U2D;

// Builds the hub's character grid from the registry and handles selection
// (Spec 7). Scene component in HubScene — both references below are
// Inspector-set, so this must never be added at runtime.
public class HubController : MonoBehaviour
{
    [Tooltip("Assets/Characters/CharacterRegistry.asset — hub card order.")]
    [SerializeField] private CharacterRegistry registry;

    [Tooltip("The disabled CharacterCard template inside the hub canvas. " +
             "Cards are cloned next to it under the same layout container.")]
    [SerializeField] private CharacterCard cardTemplate;

    [Tooltip("Assets/Characters/CharacterPortraits. Sprite names match " +
             "characterIds, plus a \"placeholder\" sprite for missing ones. " +
             "The atlas has Include in Build ON, so this direct reference is " +
             "the whole setup — no SpriteAtlasManager late binding.")]
    [SerializeField] private SpriteAtlas portraitAtlas;

    // One-shot latch: a scene load is irreversible, so ignore further card
    // clicks (double-trigger from ray + click) once one is underway.
    private bool loadingScene = false;

    private void Start()
    {
        // Spec 12 Part P: ask for the microphone here, before any early return,
        // so the dialog never interrupts a conversation scene.
        MicrophonePermission.RequestIfNeeded();

        if (registry == null || registry.characters == null || registry.characters.Count == 0)
        {
            Debug.LogError("[Hub] CharacterRegistry not assigned or empty on HubController.");
            return;
        }
        if (cardTemplate == null)
        {
            Debug.LogError("[Hub] Card template not assigned on HubController.");
            return;
        }

        if (portraitAtlas == null)
            Debug.LogWarning("[Hub] portraitAtlas not assigned — cards fall back to " +
                             "definition portraits / the template's default sprite.");

        // The template stays disabled; clones go under the same parent so the
        // canvas' GridLayoutGroup lays them out in registry order.
        cardTemplate.gameObject.SetActive(false);

        // GetSprite clones the sprite on every call, so resolve each name once
        // per population. Misses are cached too (as null) to keep the lookup
        // and its warning single-shot per name.
        var portraitCache = new Dictionary<string, Sprite>();

        foreach (CharacterDefinition character in registry.characters)
        {
            if (character == null) continue;

            CharacterCard card = Instantiate(cardTemplate, cardTemplate.transform.parent);
            card.name = "Card_" + (string.IsNullOrEmpty(character.characterId)
                ? character.name : character.characterId);
            CharacterDefinition captured = character; // closure safety
            card.Bind(captured, ResolvePortrait(captured, portraitCache),
                () => OnCharacterSelected(captured));
            card.gameObject.SetActive(true);
        }
    }

    // Priority: definition's portrait field (manual override) > atlas sprite
    // named exactly characterId > the atlas "placeholder" sprite. A null result
    // (no atlas / nothing matched) keeps the card template's default sprite.
    private Sprite ResolvePortrait(CharacterDefinition character, Dictionary<string, Sprite> cache)
    {
        if (character.portrait != null) return character.portrait;
        if (portraitAtlas == null) return null;

        Sprite sprite = GetAtlasSpriteCached(character.characterId, cache);
        if (sprite == null)
        {
            Debug.LogWarning($"[Hub] No portrait sprite named '{character.characterId}' " +
                             "in the atlas — using 'placeholder'.");
            sprite = GetAtlasSpriteCached("placeholder", cache);
        }
        return sprite;
    }

    private Sprite GetAtlasSpriteCached(string spriteName, Dictionary<string, Sprite> cache)
    {
        if (string.IsNullOrEmpty(spriteName)) return null;

        Sprite sprite;
        if (!cache.TryGetValue(spriteName, out sprite))
        {
            sprite = portraitAtlas.GetSprite(spriteName);
            cache[spriteName] = sprite;
        }
        return sprite;
    }

    private void OnCharacterSelected(CharacterDefinition character)
    {
        if (loadingScene) return;
        if (character == null || !character.available) return;

        if (string.IsNullOrEmpty(character.sceneName))
        {
            Debug.LogError($"[Hub] '{character.characterId}' is available but has no sceneName in its registry entry.");
            return;
        }

        loadingScene = true;

        // Fresh session per selection (server history is per-session), then a
        // single non-additive load — simplest and most memory-safe on Quest.
        // Spec 9, item 2: routed through SceneTransition rather than
        // SceneManager.LoadScene. The load itself is unchanged in kind (still
        // single, still non-additive); it is now async behind a fade, because the
        // blocking version froze the headset for the whole character-scene load.
        CharacterSession.Select(character);
        Debug.Log($"[Hub] Selected '{character.characterId}' -> scene '{character.sceneName}', session {CharacterSession.SessionId}");

        // Spec 12 Part J: the AI disclosure sits between selection and the scene
        // load, so the conversation cannot start until Continue is pressed. The
        // panel is built inside this canvas (same raycasters and ISDK surface
        // the cards use). If it cannot be shown at all (no canvas / no TMP font)
        // the scene is NOT loaded: a conversation must never start undisclosed.
        // The latch is released so the student can try another card.
        Canvas hubCanvas = cardTemplate != null ? cardTemplate.GetComponentInParent<Canvas>() : null;
        string sceneName = character.sceneName;
        bool shown = AiDisclosurePanel.Show(hubCanvas, character.displayName,
            () => SceneTransition.Load(sceneName));
        if (!shown)
        {
            Debug.LogError("[Hub] AI disclosure panel could not be shown; not loading the character scene.");
            loadingScene = false;
        }
    }
}

# Character scenes & the hub (Spec 7) — Unity cookbook

How the multi-character flow works, how to build HubScene once, and the
checklist for adding a new character. The server half of a character is a
Firestore `characters/<characterId>` doc (Spec 3) — nothing character-specific
lives in Unity source.

## How the pieces fit

```
HubScene (entry, Build Settings index 0)
  HubController  ── reads ──>  Assets/Characters/CharacterRegistry.asset
      │ clones one CharacterCard per registry entry
      │ on Select: CharacterSession.Select(def)   <- mints a fresh session ID
      └ SceneManager.LoadScene(def.sceneName)     <- single, non-additive

Character scene (e.g. MainScene for Einstein)
  ProxyClient            sends CharacterSession.CharacterId (header + JSON)
  AIResponseControllerV2 adopts CharacterSession.SessionId in Start()
  BackToHubButton        waits for the request lock, then loads HubScene
```

Launching a character scene directly in the Editor still works: the character
ID falls back to `einstein` and a local session ID is minted (both log a
warning).

## One-time: building HubScene

1. Duplicate `Assets/Scenes/MainScene.unity` (Ctrl+D), rename to `HubScene`.
2. Delete everything Einstein/conversation-specific: the `Albert_Einstein`
   model (and the inactive legacy models), `InfluentialFigureSpotlight`,
   `CharacterAnimationDriver` host, `Prompt Canvas v2` + Response Panel,
   `AI Response Controller`, `VoiceRecognitionController`,
   `TextToSpeechController`, `PromptEnablerController`, `NonNativeKeyboard`.
   **Keep:** `OVRCameraRigInteraction`, `EventSystem`, Directional Light, and
   (optionally) the University Theatre environment or a simpler backdrop.
3. Add a world-space Canvas at comfortable eye level (~1.5 m high, ~2 m from
   spawn) set up the same way as the existing prompt canvas so the ray
   interactors + EventSystem work on it (easiest: duplicate the existing
   canvas object and strip its children instead of creating a bare one).
4. Under the canvas add a panel with a **GridLayoutGroup** (e.g. 5 columns ×
   2 rows) — this is the card container.
5. Inside the container build ONE card and disable it — the template:
   - Portrait `Image` (assign the shared silhouette sprite as the default),
     `TMP_Text` for name, `TMP_Text` for era, a `Button` with a `TMP_Text`
     label, and optionally a semi-transparent "locked" overlay object.
   - Add the `CharacterCard` component to the card root and wire all of its
     fields in the Inspector.
6. Add an empty GameObject `HubController` with the `HubController` component;
   assign `registry` = `Assets/Characters/CharacterRegistry.asset` and
   `cardTemplate` = the disabled template card.
7. File > Build Settings: add HubScene and move it to **index 0** (MainScene
   stays in the list). Remove the stale disabled `SampleScene` entry — the
   file no longer exists.

## One-time: MainScene additions

1. Add a world-space **"Back to Hub"** button near the existing UI (duplicate
   an existing button so it inherits the ray-interaction setup).
2. Add the `BackToHubButton` component (e.g. on the button itself); assign
   `aiController` = the `AI Response Controller` object, `backButton` = the
   Button, leave `hubSceneName` = `HubScene`.
3. Wire the Button's OnClick to `BackToHubButton.OnBackToHub()`.

## Checklist: adding a new character

1. **Server:** create the Firestore `characters/<characterId>` doc (persona,
   voice, few-shots) per Spec 3.
2. **Scene:** duplicate MainScene, rename (e.g. `CurieScene`), then swap the
   character-specific parts: model (with SALSA configured the same way),
   animator (reuse the Spec 6 animation template/controller pattern), eye/head
   targets, and the environment. Leave the shared rig/UI/controller objects
   untouched — they read everything from `CharacterSession`.
3. **Registry:** create a `CharacterDefinition` asset in `Assets/Characters/`
   (right-click > Create > HistorySpeaks > Character Definition):
   `characterId` = the Firestore doc ID, `displayName`, `era`, portrait
   sprite, `sceneName` = the new scene's name, `available` = true. For a
   character that already has a placeholder entry, just fill in `sceneName`,
   the portrait, and flip `available`.
4. Make sure the definition is in `CharacterRegistry.asset`'s list (the 10
   launch entries already are).
5. File > Build Settings: add the new scene to the list.
6. Test: Editor play from HubScene → select the character → typed conversation
   works → Back to Hub → re-select starts a fresh session. Then Quest.

## Registry IDs (placeholders)

The 9 locked placeholders ship with provisional `characterId`s (`da-vinci`,
`mlk-jr`, `lincoln`, `cleopatra`, `marie-curie`, `mandela`, `socrates`,
`julius-caesar`, `harriet-tubman`). When a character goes live, the Firestore
doc ID is the source of truth — update the asset's `characterId` to match if
they differ.

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Spec 12 Part J — the AI disclosure shown to the student before every
// conversation.
//
// ----- Where it sits in the flow -----
//   hub card "Select" -> CharacterSession.Select -> [THIS PANEL] -> Continue
//   -> SceneTransition.Load(character scene) -> board button -> intro -> talk
// The character scene cannot be reached from the hub without pressing
// Continue, so "the conversation starts only after it's pressed" holds for
// every Quest path (HubScene is the first scene in Build Settings). A direct
// Editor launch of a character scene bypasses the hub and therefore the panel.
//
// ----- Why it is built in code, inside the hub canvas -----
// The hub canvas ("Prompt Canvas v2") already carries everything XR pointing
// needs: the TrackedDeviceGraphicRaycaster, the GraphicRaycaster and the Meta
// ISDK ray surface. A panel parented into that canvas is hit exactly like the
// cards are, with no new raycaster, surface or EventSystem setup. Building it
// from code means there are no SerializeFields to wire in five scenes and
// nothing to forget when a character is added (same reasoning as SceneFader).
// It is the last sibling of the canvas and its backdrop is a raycast target,
// so while it is up the cards underneath cannot be clicked.
public static class AiDisclosurePanel
{
    private const string TITLE = "Before you begin";
    private const string BODY_FORMAT =
        "You're about to talk with an AI recreation of {0}. It's a computer program, " +
        "not the real person, and it can make mistakes.";
    private const string CONTINUE_LABEL = "Continue";

    private static readonly Color BACKDROP = new Color(0.04f, 0.04f, 0.05f, 0.92f);
    private static readonly Color CARD = new Color(0.16f, 0.16f, 0.18f, 1f);     // #2A2A2E, the card tint
    private static readonly Color BUTTON = new Color(0.22f, 0.47f, 0.84f, 1f);
    private static readonly Color BUTTON_HOVER = new Color(0.30f, 0.56f, 0.92f, 1f);
    private static readonly Color BUTTON_PRESSED = new Color(0.16f, 0.36f, 0.68f, 1f);

    private static GameObject current;

    // Shows the panel over `canvas` for `displayName`. `onContinue` runs once,
    // after the panel has removed itself. Returns false (and runs nothing) if
    // there is no canvas to attach to, so the caller can decide what to do.
    public static bool Show(Canvas canvas, string displayName, Action onContinue)
    {
        if (canvas == null) return false;
        if (current != null) Destroy();

        Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
        RectTransform canvasRect = root.GetComponent<RectTransform>();
        if (canvasRect == null) return false;

        // --- backdrop: fills the canvas, swallows pointer events ---
        current = new GameObject("~AiDisclosure", typeof(RectTransform));
        RectTransform rect = (RectTransform)current.transform;
        rect.SetParent(canvasRect, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.SetAsLastSibling();

        Image backdrop = current.AddComponent<Image>();
        backdrop.color = BACKDROP;
        backdrop.raycastTarget = true;

        // --- card ---
        float w = Mathf.Min(canvasRect.rect.width * 0.72f, 820f);
        float h = Mathf.Min(canvasRect.rect.height * 0.62f, 380f);
        RectTransform card = MakeRect("Card", rect, new Vector2(w, h), Vector2.zero);
        Image cardImage = card.gameObject.AddComponent<Image>();
        cardImage.color = CARD;
        cardImage.raycastTarget = true;

        // --- title + body ---
        TMP_Text title = MakeText("Title", card, new Vector2(w - 60f, 60f), new Vector2(0f, h * 0.5f - 60f),
            TITLE, 34f, FontStyles.Bold);
        TMP_Text body = MakeText("Body", card, new Vector2(w - 80f, h - 200f), new Vector2(0f, 10f),
            string.Format(BODY_FORMAT, string.IsNullOrEmpty(displayName) ? "this historical figure" : displayName),
            26f, FontStyles.Normal);
        if (title == null || body == null)
        {
            // No TMP default font: we cannot show the words, so do not pretend
            // to have disclosed anything — tear down and report failure.
            Debug.LogWarning("[Disclosure] No TMP default font — disclosure panel cannot be shown.");
            Destroy();
            return false;
        }

        // --- Continue button ---
        RectTransform buttonRect = MakeRect("Continue", card, new Vector2(260f, 64f), new Vector2(0f, -h * 0.5f + 60f));
        Image buttonImage = buttonRect.gameObject.AddComponent<Image>();
        buttonImage.color = BUTTON;
        buttonImage.raycastTarget = true;

        Button button = buttonRect.gameObject.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        ColorBlock colors = button.colors;
        colors.normalColor = BUTTON;
        colors.highlightedColor = BUTTON_HOVER;
        colors.pressedColor = BUTTON_PRESSED;
        colors.selectedColor = BUTTON;
        button.colors = colors;

        MakeText("Label", buttonRect, new Vector2(240f, 56f), Vector2.zero, CONTINUE_LABEL, 28f, FontStyles.Bold);

        bool fired = false; // ray + click double-trigger guard, same as the hub cards
        button.onClick.AddListener(() =>
        {
            if (fired) return;
            fired = true;
            Destroy();
            onContinue?.Invoke();
        });

        return true;
    }

    public static bool IsShowing
    {
        get { return current != null; }
    }

    private static void Destroy()
    {
        if (current == null) return;
        UnityEngine.Object.Destroy(current);
        current = null;
    }

    private static RectTransform MakeRect(string name, RectTransform parent, Vector2 size, Vector2 position)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform r = (RectTransform)go.transform;
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.sizeDelta = size;
        r.anchoredPosition = position;
        return r;
    }

    // Returns null when TMP has no default font (the text would be invisible).
    private static TMP_Text MakeText(string name, RectTransform parent, Vector2 size, Vector2 position,
        string text, float fontSize, FontStyles style)
    {
        RectTransform r = MakeRect(name, parent, size, position);
        TextMeshProUGUI t = r.gameObject.AddComponent<TextMeshProUGUI>();
        if (t.font == null)
        {
            UnityEngine.Object.Destroy(r.gameObject);
            return null;
        }
        t.text = text;
        t.fontSize = fontSize;
        t.fontStyle = style;
        t.color = Color.white;
        t.alignment = TextAlignmentOptions.Center;
        t.enableWordWrapping = true;
        t.raycastTarget = false;
        return t;
    }
}

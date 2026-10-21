using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Spec 12 Part M — the "Report this answer" button in the conversation UI.
//
// ----- Where it sits -----
// Every character scene shows the reply in a "Response Panel" RectTransform
// inside the world-space "Prompt Canvas v2" (the same canvas that carries the
// Record / Submit buttons, so it already has the XR raycaster and ISDK ray
// surface). The button is built in code and hung off the response panel's
// bottom-right corner, just below it, so no scene needs Inspector wiring and
// a new character scene gets it for free. The panel has no layout group, so
// the extra child does not reflow anything.
//
// ----- Behaviour (owned by AIResponseControllerV2) -----
// Starts disabled. Enabled only after a character reply has finished
// playing; disabled again as soon as it is pressed (one report per reply) or
// a new request goes out. Pressing it posts { sessionId, characterId } to
// /report via ProxyClient.PostReport — fire-and-forget, no text, never
// touches the request lock.
//
// Spec 12 Part Q: the disabled state is visibly greyed (darker, translucent
// tint and a dimmed label). A Button's ColorBlock only tints its target
// graphic, so the label is dimmed by SetInteractable — always change
// interactable through it. BackToHubButton is built by the same code.
public static class ReportAnswerButton
{
    private const string LABEL = "Report this answer";

    private static readonly Color NORMAL = new Color(0.30f, 0.30f, 0.34f, 1f);
    private static readonly Color HOVER = new Color(0.42f, 0.42f, 0.47f, 1f);
    private static readonly Color PRESSED = new Color(0.20f, 0.20f, 0.23f, 1f);
    private static readonly Color DISABLED = new Color(0.16f, 0.16f, 0.17f, 0.45f);
    private static readonly Color LABEL_NORMAL = Color.white;
    private static readonly Color LABEL_DISABLED = new Color(0.62f, 0.62f, 0.64f, 0.45f);

    // Builds the button as the last child of `anchor` (the response panel),
    // hanging below its bottom-right corner. Returns null (and builds nothing)
    // when there is no RectTransform or no TMP default font, so the caller can
    // simply run without the button.
    public static Button Build(Transform anchor, Action onClick)
    {
        Button button = BuildPanelButton(anchor, "~ReportAnswer", LABEL, 1f, onClick);
        if (button != null) SetInteractable(button, false);
        return button;
    }

    // Enables/disables the button and dims its label to match.
    public static void SetInteractable(Button button, bool interactable)
    {
        if (button == null) return;
        button.interactable = interactable;
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.color = interactable ? LABEL_NORMAL : LABEL_DISABLED;
    }

    // Shared by the Report and Back to Hub buttons: a 150x30 button hung just
    // below `anchor`'s bottom edge, at its left (cornerX 0) or right (cornerX 1)
    // corner. Starts interactable.
    internal static Button BuildPanelButton(Transform anchor, string name, string labelText,
        float cornerX, Action onClick)
    {
        RectTransform parent = anchor as RectTransform;
        if (parent == null) return null;

        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(cornerX, 0f);
        rect.pivot = new Vector2(cornerX, 1f);
        rect.sizeDelta = new Vector2(150f, 30f);
        rect.anchoredPosition = new Vector2(0f, -6f);
        rect.SetAsLastSibling();

        Image image = go.AddComponent<Image>();
        image.color = NORMAL;
        image.raycastTarget = true;

        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.normalColor = NORMAL;
        colors.highlightedColor = HOVER;
        colors.pressedColor = PRESSED;
        colors.selectedColor = NORMAL;
        colors.disabledColor = DISABLED;
        button.colors = colors;

        GameObject labelGo = new GameObject("Label", typeof(RectTransform));
        RectTransform labelRect = (RectTransform)labelGo.transform;
        labelRect.SetParent(rect, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
        TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
        if (label.font == null)
        {
            UnityEngine.Object.Destroy(go);
            return null;
        }
        label.text = labelText;
        label.fontSize = 14f;
        label.fontStyle = FontStyles.Normal;
        label.color = LABEL_NORMAL;
        label.alignment = TextAlignmentOptions.Center;
        label.enableWordWrapping = false;
        label.raycastTarget = false;

        button.onClick.AddListener(() =>
        {
            if (!button.interactable) return; // ray + click double-trigger guard
            onClick?.Invoke();
        });
        return button;
    }
}

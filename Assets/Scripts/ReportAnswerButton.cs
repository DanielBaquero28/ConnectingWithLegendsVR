using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Spec 12 Part M — the "Report this answer" button in the conversation UI.
//
// ----- Where it sits -----
// Every character scene shows the reply in a response panel inside the
// world-space "Prompt Canvas v2" (the same canvas that carries the Record /
// Submit buttons, so it already has the XR raycaster and ISDK ray surface).
// The button is built in code and hung off that panel's bottom-right corner,
// just below it, so no scene needs Inspector wiring and a new character scene
// gets it for free. The panel has no layout group, so the extra child does
// not reflow anything.
//
// Spec 12 Part R: the panel and font both come from the controller's
// serialized ResponseText, never from object names: the panel is its parent
// RectTransform, the font is its font (else TMP_Settings.defaultFontAsset).
// The font MUST be set explicitly. Prompt Canvas v2 is saved inactive in
// every scene (the board button's On Click () reveals it), so a TMP label
// added at Start has not run Awake, which is where TMP assigns its default
// font. Before Part R the label's font read as null there and the build gave
// up, so neither button was ever built on the Quest.
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
// interactable through it. BackToHubButton uses the same look (MakeButton)
// but lives on its own canvas (Spec 12 Part S).
public static class ReportAnswerButton
{
    private const string LABEL = "Report this answer";

    private static readonly Color NORMAL = new Color(0.30f, 0.30f, 0.34f, 1f);
    private static readonly Color HOVER = new Color(0.42f, 0.42f, 0.47f, 1f);
    private static readonly Color PRESSED = new Color(0.20f, 0.20f, 0.23f, 1f);
    private static readonly Color DISABLED = new Color(0.16f, 0.16f, 0.17f, 0.45f);
    private static readonly Color LABEL_NORMAL = Color.white;
    private static readonly Color LABEL_DISABLED = new Color(0.62f, 0.62f, 0.64f, 0.45f);

    // Builds the button as the last child of the response panel (the parent
    // of `responseText`), hanging below its bottom-right corner. Returns null
    // and builds nothing when CannotBuildReason is non-null.
    public static Button Build(TMP_Text responseText, Action onClick)
    {
        Button button = BuildPanelButton(responseText, "~ReportAnswer", LABEL, 1f, onClick);
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

    // Why the Report button cannot be built for this response text, or null
    // when it can. Checked before anything is created.
    public static string CannotBuildReason(TMP_Text responseText)
    {
        if (responseText == null) return "ResponseText is not assigned";
        if (!(responseText.transform.parent is RectTransform)) return "ResponseText has no parent RectTransform";
        if (LabelFont(responseText) == null) return "no TMP font (ResponseText has none and TMP_Settings has no default)";
        return null;
    }

    // The label font: the given text's font, else the TMP default (null when
    // there is neither).
    internal static TMP_FontAsset LabelFont(TMP_Text fontSource)
    {
        return fontSource != null && fontSource.font != null ? fontSource.font : TMP_Settings.defaultFontAsset;
    }

    // The Report button: hung just below the response panel's bottom-right
    // corner. Starts interactable (Build disables it).
    private static Button BuildPanelButton(TMP_Text responseText, string name, string labelText,
        float cornerX, Action onClick)
    {
        if (CannotBuildReason(responseText) != null) return null;
        Button button = MakeButton((RectTransform)responseText.transform.parent,
            LabelFont(responseText), name, labelText, onClick);

        RectTransform rect = (RectTransform)button.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(cornerX, 0f);
        rect.pivot = new Vector2(cornerX, 1f);
        rect.anchoredPosition = new Vector2(0f, -6f);
        rect.SetAsLastSibling();
        return button;
    }

    // Shared by the Report and Back to Hub buttons: a 150x30 button with this
    // look, centred in `parent` (callers move it). `font` must not be null.
    // Starts interactable.
    internal static Button MakeButton(RectTransform parent, TMP_FontAsset font, string name,
        string labelText, Action onClick)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.sizeDelta = new Vector2(150f, 30f);

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
        // Font first and explicitly: on an inactive canvas this label has not
        // run Awake, so TMP has not given it a default font (see the header).
        TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = 14f;
        label.text = labelText;
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

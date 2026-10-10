using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// One hub card (Spec 7). This component lives on the DISABLED card template
// inside HubScene's canvas; HubController clones the template per registry
// entry. Clones inherit the Inspector-set references below — this is why the
// card is scene-authored rather than built with AddComponent at runtime
// (runtime-added components have empty SerializeFields on Quest).
//
// Hover: the ISDK RayInteractable lives on the whole canvas, and
// PointableCanvasModule turns ray hover into standard EventSystem pointer
// enter/exit per UI element — so these handlers ARE the ray-hover signal at
// card granularity. The background Image is the raycast target that makes
// this card part of the pointer hierarchy.
public class CharacterCard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private Image portraitImage;
    [SerializeField] private TMP_Text nameText;
    [SerializeField] private TMP_Text eraText;
    [SerializeField] private Button selectButton;
    [SerializeField] private TMP_Text selectButtonLabel;

    [Tooltip("Card root background. Tinted per character from " +
             "CharacterDefinition.cardTint; muted when locked.")]
    [SerializeField] private Image backgroundImage;

    [Tooltip("Optional dim/lock visual shown on unavailable cards. Leave empty " +
             "to rely on the button state + label alone.")]
    [SerializeField] private GameObject lockedOverlay;

    private const string SELECT_LABEL = "Select";
    private const string LOCKED_LABEL = "Coming soon";

    private const float HOVER_SCALE = 1.05f;
    private const float HOVER_TWEEN_SECONDS = 0.1f;

    private bool hoverEnabled;
    private Coroutine scaleTween;

    // `portrait` arrives already resolved by HubController (definition override
    // > atlas by characterId > atlas placeholder). Null keeps the template's
    // default sprite, so a missing atlas still shows the shared silhouette.
    public void Bind(CharacterDefinition character, Sprite portrait, Action onSelect)
    {
        if (nameText != null) nameText.text = character.displayName;
        if (eraText != null) eraText.text = character.era;

        if (portraitImage != null && portrait != null)
            portraitImage.sprite = portrait;

        bool available = character.available;

        if (backgroundImage != null)
            backgroundImage.color = available ? character.cardTint : MuteTint(character.cardTint);

        // Locked cards keep their normal size — no hover reaction.
        hoverEnabled = available;

        if (selectButton != null)
        {
            selectButton.interactable = available;
            selectButton.onClick.RemoveAllListeners();
            if (available && onSelect != null)
                selectButton.onClick.AddListener(() => onSelect());
        }

        if (selectButtonLabel != null)
            selectButtonLabel.text = available ? SELECT_LABEL : LOCKED_LABEL;

        if (lockedOverlay != null)
            lockedOverlay.SetActive(!available);
    }

    // Locked cards pull toward neutral grey and darken slightly so they read
    // as muted next to available cards. Darkening only ever raises contrast
    // with the white card text.
    private static Color MuteTint(Color tint)
    {
        float grey = (tint.r + tint.g + tint.b) / 3f;
        Color muted = Color.Lerp(tint, new Color(grey, grey, grey), 0.6f) * 0.8f;
        muted.a = tint.a;
        return muted;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (hoverEnabled) StartScaleTween(HOVER_SCALE);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (hoverEnabled) StartScaleTween(1f);
    }

    private void StartScaleTween(float target)
    {
        if (scaleTween != null) StopCoroutine(scaleTween);
        scaleTween = StartCoroutine(ScaleTo(Vector3.one * target));
    }

    private IEnumerator ScaleTo(Vector3 target)
    {
        // Scale doesn't participate in GridLayoutGroup's layout pass, so the
        // grid never reflows — the hovered card just overlaps its neighbors'
        // 20px gutters slightly.
        Vector3 start = transform.localScale;
        for (float t = 0f; t < 1f; t += Time.deltaTime / HOVER_TWEEN_SECONDS)
        {
            transform.localScale = Vector3.Lerp(start, target, t);
            yield return null;
        }
        transform.localScale = target;
        scaleTween = null;
    }

    private void OnDisable()
    {
        // Pointer-exit never arrives on a deactivated card (scene load mid-
        // hover), so snap back rather than staying enlarged on re-enable.
        if (scaleTween != null) StopCoroutine(scaleTween);
        scaleTween = null;
        transform.localScale = Vector3.one;
    }
}

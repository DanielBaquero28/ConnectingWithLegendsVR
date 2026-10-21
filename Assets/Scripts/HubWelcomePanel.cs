using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Spec 12 Part Q — the welcome panel shown over the hub once per app launch.
//
// Built in code inside the hub canvas exactly like AiDisclosurePanel (and
// with its helpers): a full-canvas backdrop that is a raycast target and the
// canvas's last sibling, so the character cards underneath cannot be clicked
// until Start is pressed.
//
// "Once per launch" is a static flag: statics survive the scene loads between
// the hub and the character scenes but start fresh when the app process
// starts, so a return to the hub never shows it again while a cold start does.
public static class HubWelcomePanel
{
    private const string TITLE = "Welcome to History Speaks.";
    private const string BODY =
        "Pick a figure from history and talk with them. Point at Record, speak your question, " +
        "and wait for the answer — or type it. These are AI recreations for learning.";
    private const string START_LABEL = "Start";

    private static bool shownThisLaunch = false;
    private static GameObject current;

    // Shows the panel over `canvas` the first time it is called in this app
    // launch; later calls do nothing. Returns true if it is now showing.
    public static bool ShowOncePerLaunch(Canvas canvas)
    {
        if (shownThisLaunch || canvas == null) return false;

        RectTransform card;
        current = AiDisclosurePanel.BuildBackdropAndCard(canvas, "~HubWelcome", out card);
        if (current == null) return false;
        float w = card.sizeDelta.x;
        float h = card.sizeDelta.y;

        TMP_Text title = AiDisclosurePanel.MakeText("Title", card, new Vector2(w - 40f, 40f),
            new Vector2(0f, h * 0.5f - 36f), TITLE, 24f, FontStyles.Bold);
        TMP_Text body = AiDisclosurePanel.MakeText("Body", card, new Vector2(w - 56f, h - 130f),
            new Vector2(0f, 4f), BODY, 19f, FontStyles.Normal);
        if (title == null || body == null)
        {
            // No TMP default font: an empty card would only block the hub.
            Debug.LogWarning("[Welcome] No TMP default font — welcome panel not shown.");
            Destroy();
            return false;
        }

        Button start = AiDisclosurePanel.MakePrimaryButton(card, START_LABEL, new Vector2(0f, -h * 0.5f + 36f));
        bool fired = false; // ray + click double-trigger guard
        start.onClick.AddListener(() =>
        {
            if (fired) return;
            fired = true;
            Destroy();
        });

        shownThisLaunch = true;
        return true;
    }

    private static void Destroy()
    {
        if (current == null) return;
        UnityEngine.Object.Destroy(current);
        current = null;
    }
}

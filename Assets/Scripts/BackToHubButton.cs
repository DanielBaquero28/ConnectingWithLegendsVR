using System;
using TMPro;
using UnityEngine.UI;

// Spec 12 Part Q — the "Back to Hub" button in the conversation UI.
//
// Built in code exactly like the Report button (same size and look, see
// ReportAnswerButton.BuildPanelButton), hung below the response panel's
// bottom-LEFT corner so the two sit at opposite ends of the same edge. Every
// character scene gets it from AIResponseControllerV2.Start with no
// Inspector wiring.
//
// This replaces the Spec 7 scene component of the same name, which was never
// placed in any scene. That version waited up to 60 s for an in-flight
// request to finish; this one leaves at once and cancels the request instead
// (AIResponseControllerV2.OnBackToHub owns that sequence).
public static class BackToHubButton
{
    private const string LABEL = "Back to Hub";

    // Hung off the parent of `responseText`, like the Report button. Returns
    // null and builds nothing when ReportAnswerButton.CannotBuildReason is
    // non-null. Always interactable until pressed.
    public static Button Build(TMP_Text responseText, Action onClick)
    {
        return ReportAnswerButton.BuildPanelButton(responseText, "~BackToHub", LABEL, 0f, onClick);
    }
}

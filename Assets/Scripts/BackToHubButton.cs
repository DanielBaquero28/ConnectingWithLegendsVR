using System;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Spec 12 Part Q/S — the "Back to Hub" button in every character scene.
//
// Part S: it lives on its OWN small world-space canvas, a scene root that is
// active from the moment the scene loads, so it can be used before the board
// button is pressed, during the intro and during replies. (Part Q hung it off
// the response panel inside Prompt Canvas v2, which is saved inactive and
// only revealed by the board button.) The Report button stays on the panel.
//
// ----- Where it sits -----
// The student's view moves a lot: the board is head-locked, and in four of
// the five scenes the conversation canvas is behind the spawn facing. So the
// canvas is not fixed in the world; TagAlong keeps it 0.9 m from the head,
// 32 degrees left and 22 degrees down, and only swings round after the
// student turns more than 40 degrees. That is clear of the board's left edge
// (the board spans about +/-25 degrees at 1.1 m) and an easy point-down-left
// with either controller.
//
// ----- Ray input -----
// Built like Prompt Canvas v2's Inspector setup, in code: a GraphicRaycaster
// (PointableCanvasModule raycasts through it), a PointableCanvas, and a
// RayInteractable on a clipped plane surface the size of the canvas. Every
// ISDK reference is injected while the canvas is still inactive, so their
// Awake/Start see a complete setup.
//
// Pressing it runs AIResponseControllerV2.OnBackToHub (the Part Q safe exit).
public static class BackToHubButton
{
    private const string LABEL = "Back to Hub";

    // Canvas units -> metres. The 150x30 button is 0.18 m x 0.036 m and the
    // 14 pt label about 17 mm tall: legible at 0.9 m on the Quest 3.
    public const float CANVAS_SCALE = 0.0012f;
    public static readonly Vector2 CANVAS_SIZE = new Vector2(160f, 40f);

    // Why the button cannot be built in this scene, or null when it can.
    public static string CannotBuildReason(TMP_Text fontSource)
    {
        if (ReportAnswerButton.LabelFont(fontSource) == null)
            return "no TMP font (ResponseText has none and TMP_Settings has no default)";
        if (FindHead() == null) return "no OVRCameraRig CenterEyeAnchor or main camera to follow";
        return null;
    }

    // Builds the canvas and button. `fontSource` only supplies the font
    // (ResponseText, else the TMP default). Returns null and builds nothing
    // when CannotBuildReason is non-null. Always interactable until pressed.
    public static Button Build(TMP_Text fontSource, Action onClick)
    {
        if (CannotBuildReason(fontSource) != null) return null;

        GameObject root = new GameObject("~BackToHubCanvas", typeof(RectTransform));
        root.SetActive(false); // ISDK references are injected before Awake/Start
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace; // before sizing: screen space drives the rect
        root.AddComponent<GraphicRaycaster>();
        RectTransform rootRect = (RectTransform)root.transform;
        rootRect.sizeDelta = CANVAS_SIZE;
        rootRect.localScale = Vector3.one * CANVAS_SCALE;

        Button button = ReportAnswerButton.MakeButton(rootRect, ReportAnswerButton.LabelFont(fontSource),
            "~BackToHub", LABEL, onClick);

        PointableCanvas pointableCanvas = root.AddComponent<PointableCanvas>();
        pointableCanvas.InjectAllPointableCanvas(canvas);

        GameObject surfaceGo = new GameObject("Surface");
        surfaceGo.transform.SetParent(root.transform, false);
        PlaneSurface plane = surfaceGo.AddComponent<PlaneSurface>();
        plane.InjectAllPlaneSurface(PlaneSurface.NormalFacing.Backward, false);
        BoundsClipper clipper = surfaceGo.AddComponent<BoundsClipper>();
        clipper.Size = new Vector3(CANVAS_SIZE.x, CANVAS_SIZE.y, 1f);
        ClippedPlaneSurface surface = surfaceGo.AddComponent<ClippedPlaneSurface>();
        surface.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { clipper });

        RayInteractable ray = root.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);
        ray.InjectOptionalPointableElement(pointableCanvas);

        TagAlong follow = root.AddComponent<TagAlong>();
        follow.Head = FindHead();
        follow.Snap();

        root.SetActive(true);
        return button;
    }

    // The head to follow: the rig's CenterEyeAnchor (its property is set in
    // the rig's Awake, so the child is looked up by name as a fallback, e.g.
    // in the Editor check), else the main camera.
    public static Transform FindHead()
    {
        OVRCameraRig rig = UnityEngine.Object.FindObjectOfType<OVRCameraRig>(true);
        if (rig != null)
        {
            if (rig.centerEyeAnchor != null) return rig.centerEyeAnchor;
            Transform eye = rig.transform.Find("TrackingSpace/CenterEyeAnchor");
            if (eye != null) return eye;
        }
        return Camera.main != null ? Camera.main.transform : null;
    }
}

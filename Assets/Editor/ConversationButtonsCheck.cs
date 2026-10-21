using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Spec 12 Part R — checks that the Report and Back to Hub buttons can be built
// in every character scene.
//
// Opens each available character's scene from the registry, finds its
// AIResponseControllerV2 and builds both buttons from its ResponseText,
// the same calls the controller makes at Start. The scenes are left as saved,
// so the prompt canvas is still inactive, which is exactly the condition that
// broke the build on the Quest before Part R. The built buttons are destroyed
// and nothing is saved.
//
// Part S: Report must be under the response panel. Back to Hub must be on its
// own always-active world-space root canvas with ISDK ray input, 0.5–1.2 m
// from the head, within 45 degrees of its facing, below eye level and clear
// of the head-locked board, as placed from the head's saved pose.
//
// Menu: HistorySpeaks > Check Conversation Buttons.
// Batch (Editor closed):
//   Unity.exe -batchmode -quit -buildTarget Android -projectPath <repo>
//     -executeMethod ConversationButtonsCheck.RunBatch -logFile check.log
// Pass = "[ButtonsCheck] PASSED" and "Exiting batchmode successfully"; a
// failure throws, which makes Unity exit with an error.
public static class ConversationButtonsCheck
{
    private const string REGISTRY_PATH = "Assets/Characters/CharacterRegistry.asset";

    [MenuItem("HistorySpeaks/Check Conversation Buttons")]
    public static void RunFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string previous = SceneManager.GetActiveScene().path;

        List<string> failures = Run(out int checkedScenes);

        if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
        EditorUtility.DisplayDialog("Conversation buttons",
            failures.Count == 0
                ? $"PASSED: both buttons build in all {checkedScenes} character scenes."
                : "FAILED:\n" + string.Join("\n", failures),
            "OK");
    }

    public static void RunBatch()
    {
        List<string> failures = Run(out _);
        if (failures.Count > 0)
            throw new System.Exception("[ButtonsCheck] FAILED: " + string.Join("; ", failures));
    }

    private static List<string> Run(out int checkedScenes)
    {
        var failures = new List<string>();
        checkedScenes = 0;

        var registry = AssetDatabase.LoadAssetAtPath<CharacterRegistry>(REGISTRY_PATH);
        if (registry == null)
        {
            failures.Add("no CharacterRegistry at " + REGISTRY_PATH);
            return failures;
        }

        // Copied out first: opening a scene unloads unused assets, after which
        // the registry's CharacterDefinition entries read as null.
        var targets = registry.characters
            .Where(c => c != null && c.available)
            .Select(c => (id: c.characterId, sceneName: c.sceneName))
            .ToList();

        foreach (var (id, sceneName) in targets)
        {
            string scenePath = EditorBuildSettings.scenes
                .Select(s => s.path)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == sceneName);
            if (scenePath == null)
            {
                failures.Add($"{id}: scene '{sceneName}' is not in Build Settings");
                continue;
            }

            checkedScenes++;
            string failure = CheckScene(scenePath);
            if (failure == null)
                Debug.Log($"[ButtonsCheck] ok - {id} ({scenePath})");
            else
                failures.Add($"{id} ({scenePath}): {failure}");
        }

        if (checkedScenes == 0) failures.Add("the registry lists no available character scenes");

        if (failures.Count == 0)
            Debug.Log($"[ButtonsCheck] PASSED: both buttons build in all {checkedScenes} character scenes.");
        else
            foreach (string f in failures) Debug.LogError("[ButtonsCheck] FAIL - " + f);
        return failures;
    }

    // Returns null when both buttons build correctly, else what went wrong.
    private static string CheckScene(string scenePath)
    {
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        AIResponseControllerV2[] controllers = scene.GetRootGameObjects()
            .SelectMany(go => go.GetComponentsInChildren<AIResponseControllerV2>(true))
            .ToArray();
        if (controllers.Length != 1)
            return $"expected one AIResponseControllerV2, found {controllers.Length}";

        TMP_Text responseText = controllers[0].ResponseText;
        string reason = ReportAnswerButton.CannotBuildReason(responseText)
            ?? BackToHubButton.CannotBuildReason(responseText);
        if (reason != null) return reason;
        Debug.Log($"[ButtonsCheck]   {scene.name}: response panel active in hierarchy as saved = " +
                  responseText.transform.parent.gameObject.activeInHierarchy);

        Button report = ReportAnswerButton.Build(responseText, null);
        Button back = BackToHubButton.Build(responseText, null);
        try
        {
            return CheckButton(report, "Report", false)
                ?? (report.transform.parent != responseText.transform.parent
                    ? "Report button is not under the response panel" : null)
                ?? CheckButton(back, "Back to Hub", true)
                ?? CheckBackToHubCanvas(back, responseText, scene.name);
        }
        finally
        {
            if (report != null) Object.DestroyImmediate(report.gameObject);
            if (back != null) Object.DestroyImmediate(back.GetComponentInParent<Canvas>(true).gameObject);
        }
    }

    private static string CheckButton(Button button, string what, bool interactable)
    {
        if (button == null) return what + " button was not built";
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label == null || label.font == null) return what + " button label has no font";
        if (string.IsNullOrEmpty(label.text)) return what + " button label is empty";
        if (button.interactable != interactable)
            return $"{what} button should start {(interactable ? "enabled" : "disabled")}";
        return null;
    }

    // Spec 12 Part S: Back to Hub is on its own always-active world-space
    // canvas with ISDK ray input, within comfortable reach of the head, and
    // clear of the head-locked board.
    private static string CheckBackToHubCanvas(Button back, TMP_Text responseText, string sceneName)
    {
        Canvas canvas = back.GetComponentInParent<Canvas>(true);
        Transform root = canvas.transform;
        Canvas promptCanvas = responseText.GetComponentsInParent<Canvas>(true).Last();

        if (root.parent != null) return "Back to Hub canvas is not a scene root";
        if (root.IsChildOf(promptCanvas.transform)) return "Back to Hub is still inside the prompt canvas";
        if (canvas.renderMode != RenderMode.WorldSpace) return "Back to Hub canvas is not world-space";
        if (!root.gameObject.activeInHierarchy) return "Back to Hub canvas is not active at scene load";
        if (root.GetComponent<GraphicRaycaster>() == null) return "Back to Hub canvas has no GraphicRaycaster";

        var pointable = root.GetComponent<Oculus.Interaction.PointableCanvas>();
        var ray = root.GetComponent<Oculus.Interaction.RayInteractable>();
        if (pointable == null || pointable.Canvas != canvas) return "Back to Hub canvas has no PointableCanvas for itself";
        if (ray == null || ray.Surface == null) return "Back to Hub canvas has no RayInteractable surface";
        if (!ReferenceEquals(ray.PointableElement, pointable)) return "Back to Hub RayInteractable is not linked to its PointableCanvas";

        Transform head = BackToHubButton.FindHead();
        Vector3 offset = root.position - head.position;
        Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        float yaw = Vector3.Angle(flatForward, Vector3.ProjectOnPlane(offset, Vector3.up));
        float down = Vector3.Angle(Vector3.ProjectOnPlane(offset, Vector3.up), offset);
        if (offset.magnitude < 0.5f || offset.magnitude > 1.2f) return $"Back to Hub is {offset.magnitude:F2} m from the head";
        if (yaw > 45f) return $"Back to Hub is {yaw:F0} degrees off the facing direction";
        if (offset.y >= 0f || down > 35f) return $"Back to Hub is not comfortably below eye level ({down:F0} degrees down)";
        if (Vector3.Angle(root.forward, offset) > 5f) return "Back to Hub canvas does not face the head";

        // The board (the button that starts the intro) must not cover it.
        Button board = Object.FindObjectsOfType<Button>(true).FirstOrDefault(b =>
            Enumerable.Range(0, b.onClick.GetPersistentEventCount())
                .Any(i => b.onClick.GetPersistentMethodName(i) == "PlayIntro"));
        string boardNote = "no board found";
        if (board != null)
        {
            RectTransform boardRect = (RectTransform)board.GetComponentsInParent<Canvas>(true).Last().transform;
            Plane boardPlane = new Plane(boardRect.forward, boardRect.position);
            Ray sight = new Ray(head.position, offset);
            if (boardPlane.Raycast(sight, out float hit))
            {
                Vector2 local = boardRect.InverseTransformPoint(sight.GetPoint(hit));
                Rect inner = boardRect.rect;
                if (hit < offset.magnitude) return "the board is in front of Back to Hub";
                // Margin: half the Back to Hub width, in board units.
                float margin = 0.5f * BackToHubButton.CANVAS_SIZE.x * BackToHubButton.CANVAS_SCALE * hit
                               / offset.magnitude / boardRect.lossyScale.x;
                inner.xMin += -margin; inner.xMax += margin; inner.yMin += -margin; inner.yMax += margin;
                if (inner.Contains(local)) return "Back to Hub sits over the board";
                boardNote = "clear of the board";
            }
            else boardNote = "board not in the line of sight";
        }

        Debug.Log($"[ButtonsCheck]   {sceneName}: Back to Hub {offset.magnitude:F2} m from the head, " +
                  $"{yaw:F0} deg to the side, {down:F0} deg down, {boardNote}");
        return null;
    }
}

using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Question field + response panel setup (2026-10-28), for every registry
// character scene:
//   - caret: 3 px wide, blinking (0.85), custom colour chosen for contrast
//     against the field's background image (dark on a light field, light on
//     a dark one), no select-all on focus, and a PromptCaret component that
//     keeps the caret at the end of the text;
//   - MLK layout only (field and panel side by side): the response panel's
//     top edge is moved to the question field's top edge, exactly. The four
//     old-layout scenes (panel below the field) are left as they are.
// Scenes are saved; nothing else is edited. ConversationButtonsCheck asserts
// the result.
//
// Menu: HistorySpeaks > Set Up Prompt Fields.
// Batch (Editor closed):
//   Unity.exe -batchmode -quit -buildTarget Android -projectPath <repo>
//     -executeMethod PromptFieldSetup.ApplyBatch -logFile prompt.log
public static class PromptFieldSetup
{
    public const float CARET_WIDTH = 3f;
    public const float CARET_BLINK_RATE = 0.85f;
    public static readonly Color CARET_ON_LIGHT = new Color(0.2f, 0.2f, 0.2f, 1f);
    public static readonly Color CARET_ON_DARK = new Color(0.93f, 0.93f, 0.93f, 1f);
    // Scenes whose field and panel share a row get the top edges aligned.
    public const string SIDE_BY_SIDE_SCENE = "MLKScene";

    [MenuItem("HistorySpeaks/Set Up Prompt Fields")]
    public static void ApplyFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string previous = SceneManager.GetActiveScene().path;
        List<string> failures = Apply();
        if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
        EditorUtility.DisplayDialog("Prompt fields",
            failures.Count == 0 ? "Done." : "FAILED:\n" + string.Join("\n", failures), "OK");
    }

    public static void ApplyBatch()
    {
        List<string> failures = Apply();
        if (failures.Count > 0) throw new System.Exception("[PromptSetup] FAILED: " + string.Join("; ", failures));
    }

    private static List<string> Apply()
    {
        var failures = new List<string>();
        foreach (string scenePath in ResponsePanelFit.CharacterScenePaths())
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            string failure = SetUpScene(scene);
            if (failure != null) { failures.Add($"{scene.name}: {failure}"); continue; }
            EditorSceneManager.SaveScene(scene);
        }
        if (failures.Count == 0) Debug.Log("[PromptSetup] PASSED");
        else foreach (string f in failures) Debug.LogError("[PromptSetup] FAIL - " + f);
        return failures;
    }

    private static string SetUpScene(Scene scene)
    {
        var controller = scene.GetRootGameObjects()
            .SelectMany(go => go.GetComponentsInChildren<AIResponseControllerV2>(true)).SingleOrDefault();
        if (controller == null) return "no AIResponseControllerV2";
        TMP_InputField field = controller.PromptField;
        if (field == null) return "AIResponseControllerV2.PromptField is not set";

        Undo.RecordObject(field, "Prompt caret");
        field.caretWidth = (int)CARET_WIDTH;
        field.caretBlinkRate = CARET_BLINK_RATE;
        field.customCaretColor = true;
        field.caretColor = CaretColorFor(field);
        field.onFocusSelectAll = false;
        EditorUtility.SetDirty(field);
        if (field.GetComponent<PromptCaret>() == null)
            Undo.AddComponent<PromptCaret>(field.gameObject);

        string note = "";
        if (scene.name == SIDE_BY_SIDE_SCENE)
        {
            var panel = (RectTransform)controller.ResponseText.transform.parent;
            float delta = TopEdge((RectTransform)field.transform, panel.parent) - TopEdge(panel, panel.parent);
            if (Mathf.Abs(delta) > 0.01f)
            {
                Undo.RecordObject(panel, "Align response panel");
                // Both edges are measured in the panel's parent space, which is
                // also the space anchoredPosition is expressed in.
                panel.anchoredPosition += new Vector2(0f, delta);
                EditorUtility.SetDirty(panel);
            }
            float after = TopEdge((RectTransform)field.transform, panel.parent) - TopEdge(panel, panel.parent);
            if (Mathf.Abs(after) > 0.01f) return $"panel top still {after:F2} from the field top";
            note = $", panel moved {delta:F2} so its top matches the field's";
        }

        Debug.Log($"[PromptSetup] {scene.name}: caret {field.caretWidth}px {ColorUtility.ToHtmlStringRGB(field.caretColor)} " +
                  $"blink {field.caretBlinkRate}, PromptCaret on, no select-all{note}");
        return null;
    }

    // Light background (the field's own Image, if any) -> dark caret; else light.
    internal static Color CaretColorFor(TMP_InputField field)
    {
        var image = field.GetComponent<Image>();
        bool light = image != null && image.color.a > 0.5f && image.color.grayscale > 0.5f;
        return light ? CARET_ON_LIGHT : CARET_ON_DARK;
    }

    // Top edge of `rt` in `space`'s local units.
    internal static float TopEdge(RectTransform rt, Transform space)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        return Mathf.Max(space.InverseTransformPoint(c[1]).y, space.InverseTransformPoint(c[2]).y);
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Response panel overflow fix (2026-10-28). The crisis message, the longest
// fixed text the response panel shows, overflowed it at the scene's font size
// (by ~7 px in the four old-layout scenes, ~80 px in MLK's rebuilt panel).
//
// For every registry character scene (locked ones too) this:
//   1. renders SafetyFilter.CrisisMessage in the ResponseText the controller
//      uses, at the scene's font size, and grows the response panel DOWNWARD
//      (top edge fixed) until it fits, plus PAD. A text box with a fixed height
//      grows with it; one that stretches with the panel follows on its own.
//      The code-built Report button hangs off the panel's bottom, so it moves
//      down with it.
//   2. turns on TMP auto-size, max = that font size, min = AUTO_SIZE_MIN_RATIO
//      of it, as the fallback for anything longer.
//   3. fails if the panel, Report, Record, Submit or the prompt field overlap
//      afterwards. Record and Submit sit above the panel (old layout) or beside
//      it (MLK), so nothing needs to move today; if that changes this stops
//      instead of guessing a new spot.
// Idempotent: a scene whose panel already fits is only re-checked. The crisis
// text is measured, never changed. The scenes are saved; nothing else in them
// is edited (device tokens included).
//
// Menu: HistorySpeaks > Fit Response Panels.
// Batch (Editor closed):
//   Unity.exe -batchmode -quit -buildTarget Android -projectPath <repo>
//     -executeMethod ResponsePanelFit.ApplyBatch -logFile fit.log
// ConversationButtonsCheck re-verifies the result.
public static class ResponsePanelFit
{
    private const string REGISTRY_PATH = "Assets/Characters/CharacterRegistry.asset";

    internal const float AUTO_SIZE_MIN_RATIO = 0.75f;
    private const float PAD = 4f;

    [MenuItem("HistorySpeaks/Fit Response Panels")]
    public static void ApplyFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string previous = SceneManager.GetActiveScene().path;
        List<string> failures = Apply();
        if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
        EditorUtility.DisplayDialog("Fit response panels",
            failures.Count == 0 ? "Done: the crisis message fits in every character scene." : "FAILED:\n" + string.Join("\n", failures),
            "OK");
    }

    public static void ApplyBatch()
    {
        List<string> failures = Apply();
        if (failures.Count > 0)
            throw new System.Exception("[Fit] FAILED: " + string.Join("; ", failures));
    }

    private static List<string> Apply()
    {
        var failures = new List<string>();
        foreach (string scenePath in CharacterScenePaths())
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            string failure = FitScene(scene);
            if (failure != null) { failures.Add($"{scene.name}: {failure}"); continue; }
            EditorSceneManager.SaveScene(scene);
        }
        if (failures.Count == 0) Debug.Log("[Fit] PASSED");
        else foreach (string f in failures) Debug.LogError("[Fit] FAIL - " + f);
        return failures;
    }

    private static string FitScene(Scene scene)
    {
        AIResponseControllerV2[] controllers = scene.GetRootGameObjects()
            .SelectMany(go => go.GetComponentsInChildren<AIResponseControllerV2>(true)).ToArray();
        if (controllers.Length != 1) return $"expected one AIResponseControllerV2, found {controllers.Length}";
        TMP_Text text = controllers[0].ResponseText;
        if (text == null) return "AIResponseControllerV2.ResponseText is not set";
        var panel = (RectTransform)text.transform.parent;
        RectTransform textRect = text.rectTransform;
        if (panel.anchorMin.y != panel.anchorMax.y) return "response panel stretches vertically; cannot grow it";

        float baseSize = text.enableAutoSizing ? text.fontSizeMax : text.fontSize;
        bool textFollowsPanel = textRect.anchorMin.y == 0f && textRect.anchorMax.y == 1f;
        if (!textFollowsPanel && textRect.anchorMin.y != textRect.anchorMax.y)
            return "response text has partial vertical anchors; cannot grow it";

        float before = panel.rect.height;
        float overflow = CrisisOverflow(text, baseSize);
        if (overflow > 0f)
        {
            float grow = Mathf.Ceil(overflow + PAD);
            Undo.RecordObjects(new Object[] { panel, textRect }, "Fit response panel");
            GrowDown(panel, grow);
            if (!textFollowsPanel) GrowDown(textRect, grow);
            overflow = CrisisOverflow(text, baseSize);
            if (overflow > 0f) return $"still {overflow:F1} px short after growing by {grow}";
        }

        Undo.RecordObject(text, "Fit response panel");
        text.fontSize = baseSize;
        text.fontSizeMax = baseSize;
        text.fontSizeMin = Mathf.Round(baseSize * AUTO_SIZE_MIN_RATIO);
        text.enableAutoSizing = true;
        EditorUtility.SetDirty(text);

        string overlap = FindOverlap(controllers[0], text);
        if (overlap != null) return overlap;

        // Cross-check against TMP's own layout estimate (wrap width = box minus margins).
        bool auto = text.enableAutoSizing;
        text.enableAutoSizing = false;
        float estimate = text.GetPreferredValues(SafetyFilter.CrisisMessage,
            textRect.rect.width - text.margin.x - text.margin.z, 0f).y;
        text.enableAutoSizing = auto;
        Debug.Log($"[Fit] {scene.name}: panel {before:F1} -> {panel.rect.height:F1} px, text box {textRect.rect.height:F1} px, " +
                  $"crisis fits at {baseSize} with {-overflow:F1} px to spare (TMP estimate {estimate:F1} px), " +
                  $"auto-size {text.fontSizeMin}-{text.fontSizeMax}");
        return null;
    }

    // Grows a rect by `amount` with its top edge where it was.
    private static void GrowDown(RectTransform rt, float amount)
    {
        rt.sizeDelta += new Vector2(0f, amount);
        rt.anchoredPosition -= new Vector2(0f, amount * (1f - rt.pivot.y));
        EditorUtility.SetDirty(rt);
    }

    // How far (text-local units) the crisis message runs below the bottom of
    // the text box, margins included, at `fontSize` with auto-size off; <= 0
    // means it fits. TMP renders nothing under an inactive hierarchy (the
    // prompt canvas is saved inactive), so inactive ancestors are switched on
    // for the measurement and back off after; the text's own content and
    // settings are restored too. Throws if nothing rendered, so an empty
    // measurement can never read as "fits".
    internal static float CrisisOverflow(TMP_Text text, float fontSize)
    {
        var inactive = new List<GameObject>();
        for (Transform t = text.transform; t != null; t = t.parent)
            if (!t.gameObject.activeSelf) inactive.Add(t.gameObject);

        string oldText = text.text;
        bool oldAuto = text.enableAutoSizing;
        float oldSize = text.fontSize;
        try
        {
            foreach (GameObject go in inactive) go.SetActive(true);
            text.enableAutoSizing = false;
            text.fontSize = fontSize;
            text.text = SafetyFilter.CrisisMessage;
            text.ForceMeshUpdate(true, true);
            if (text.textInfo.characterCount == 0 || text.textBounds.size.y <= 0f)
                throw new System.Exception($"[Fit] the crisis message did not render in '{text.name}'");
            float boxBottom = text.rectTransform.rect.yMin + text.margin.w;
            return boxBottom - text.textBounds.min.y;
        }
        finally
        {
            text.text = oldText;
            text.enableAutoSizing = oldAuto;
            text.fontSize = oldSize;
            text.ForceMeshUpdate(true, true);
            for (int i = inactive.Count - 1; i >= 0; i--) inactive[i].SetActive(false);
        }
    }

    // First overlapping pair among the response panel, the code-built Report
    // button, Record, Submit and the prompt field (active ones only), in the
    // prompt canvas' plane; null when all are clear.
    internal static string FindOverlap(AIResponseControllerV2 controller, TMP_Text text)
    {
        var panel = (RectTransform)text.transform.parent;
        Button report = ReportAnswerButton.Build(text, null);
        try
        {
            var voice = Object.FindObjectsOfType<VoiceRecognitionController>(true).FirstOrDefault();
            var items = new List<(string name, RectTransform rt)>
            {
                ("response panel", panel),
                ("Report", (RectTransform)report.transform),
            };
            if (voice != null && voice.RecordButton != null) items.Add(("Record", (RectTransform)voice.RecordButton.transform));
            if (voice != null && voice.SubmitButton != null) items.Add(("Submit", (RectTransform)voice.SubmitButton.transform));
            if (controller.PromptField != null) items.Add(("prompt field", (RectTransform)controller.PromptField.transform));

            Transform space = panel.GetComponentsInParent<Canvas>(true).Last().transform;
            var rects = items.Where(i => i.rt.gameObject.activeSelf)
                .Select(i => (i.name, rect: CanvasRect(i.rt, space))).ToList();
            for (int a = 0; a < rects.Count; a++)
                for (int b = a + 1; b < rects.Count; b++)
                    if (rects[a].rect.Overlaps(rects[b].rect))
                        return $"{rects[a].name} overlaps {rects[b].name}";
            return null;
        }
        finally
        {
            Object.DestroyImmediate(report.gameObject);
        }
    }

    // A rect's world corners in `space` (the root prompt canvas), as a 2D rect.
    private static Rect CanvasRect(RectTransform rt, Transform space)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        Vector3 min = space.InverseTransformPoint(c[0]), max = space.InverseTransformPoint(c[2]);
        return Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
    }

    // Every registry character with a scene in Build Settings, locked or not.
    internal static List<string> CharacterScenePaths()
    {
        var registry = AssetDatabase.LoadAssetAtPath<CharacterRegistry>(REGISTRY_PATH);
        // Copied out first: opening a scene unloads the registry's entries.
        List<string> names = registry.characters
            .Where(c => c != null && !string.IsNullOrEmpty(c.sceneName)).Select(c => c.sceneName).ToList();
        return names
            .Select(n => EditorBuildSettings.scenes.Select(s => s.path)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == n))
            .Where(p => p != null).ToList();
    }
}

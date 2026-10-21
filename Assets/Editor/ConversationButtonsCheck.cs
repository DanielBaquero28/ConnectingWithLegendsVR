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
        string reason = ReportAnswerButton.CannotBuildReason(responseText);
        if (reason != null) return reason;
        Debug.Log($"[ButtonsCheck]   {scene.name}: response panel active in hierarchy as saved = " +
                  responseText.transform.parent.gameObject.activeInHierarchy);

        Button report = ReportAnswerButton.Build(responseText, null);
        Button back = BackToHubButton.Build(responseText, null);
        try
        {
            return CheckButton(report, "Report", responseText, false)
                ?? CheckButton(back, "Back to Hub", responseText, true);
        }
        finally
        {
            if (report != null) Object.DestroyImmediate(report.gameObject);
            if (back != null) Object.DestroyImmediate(back.gameObject);
        }
    }

    private static string CheckButton(Button button, string what, TMP_Text responseText, bool interactable)
    {
        if (button == null) return what + " button was not built";
        if (button.transform.parent != responseText.transform.parent)
            return what + " button is not under the response panel";
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label == null || label.font == null) return what + " button label has no font";
        if (string.IsNullOrEmpty(label.text)) return what + " button label is empty";
        if (button.interactable != interactable)
            return $"{what} button should start {(interactable ? "enabled" : "disabled")}";
        return null;
    }
}

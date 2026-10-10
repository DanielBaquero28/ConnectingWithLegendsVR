using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Spec 9, item 2 — the full-FOV fade overlay that hides scene-load hitches.
//
// ----- Why an overlay at all -----
// SceneManager.LoadScene blocks the main thread for the whole load. On a monitor
// that is an ugly stutter; in a headset it is a FROZEN frame while the world stops
// but your head keeps moving, which is actively nauseating rather than merely
// unpolished. The cure is to load asynchronously behind something opaque.
//
// ----- Why it is world-space, not a Screen Space - Overlay canvas -----
// Screen Space - Overlay canvases do not render into the HMD eye buffers under
// single-pass instanced XR — the fade would be invisible on-device while looking
// perfect in the Editor Game view, which is the worst possible failure mode. So
// this is a WORLD-SPACE canvas held a fixed short distance in front of the active
// camera and re-posed every LateUpdate. Depth does the compositing for us: at
// ~0.3 m it is nearer than anything else in the scene, and transparent geometry
// sorts near-last, so it draws over everything without any sorting tricks.
//
// ----- Why it is built in code instead of authored in a scene -----
// It has to SURVIVE the very load it is covering, so it is DontDestroyOnLoad and
// therefore cannot belong to either scene. Building it from code (rather than from
// a prefab with SerializeFields) also sidesteps the Quest rule that runtime-added
// components have empty SerializeFields: there is nothing to wire, so there is
// nothing to forget. Every future character scene inherits the transition for free
// — no per-scene setup, no loading scene in Build Settings. See SceneTransition.
[DisallowMultipleComponent]
public class SceneFader : MonoBehaviour
{
    // Distance in front of the eyes. Comfortably outside any sane near-clip plane
    // and nearer than the scene's world-space UI, but still re-checked against the
    // live camera below in case a scene ships an unusual near clip.
    private const float DEFAULT_DISTANCE = 0.3f;

    // Canvas units per metre of distance. The black panel is RECT_SIZE units square,
    // so at the default distance it spans ~3 m — roughly 155 degrees of view, far
    // wider than the Quest 3's FOV even with the per-eye IPD offset. Deliberately
    // generous: a fade with a visible edge is worse than no fade.
    private const float SCALE_PER_METRE = 0.005f;
    private const int RECT_SIZE = 2000;

    private static SceneFader instance;

    // Created on first use rather than by a bootstrap scene, so nothing has to
    // remember to spawn it.
    public static SceneFader Instance
    {
        get
        {
            if (instance == null) instance = Create();
            return instance;
        }
    }

    private Transform cam;
    private Image screen;
    private TMP_Text label;
    private GameObject labelRoot;
    private float alpha;
    private float distance = DEFAULT_DISTANCE;

    private static SceneFader Create()
    {
        GameObject root = new GameObject("~SceneFader");
        DontDestroyOnLoad(root);

        SceneFader fader = root.AddComponent<SceneFader>();
        fader.Build();
        return fader;
    }

    private void Build()
    {
        // --- black panel ---
        GameObject panel = new GameObject("Fade");
        panel.transform.SetParent(transform, false);

        Canvas canvas = panel.AddComponent<Canvas>();   // also converts Transform -> RectTransform
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 32000;

        // No GraphicRaycaster on purpose: the overlay must never swallow the
        // interaction ray, or a click landing during a fade would hit nothing.
        screen = panel.AddComponent<Image>();
        screen.raycastTarget = false;

        RectTransform rect = (RectTransform)panel.transform;
        rect.sizeDelta = new Vector2(RECT_SIZE, RECT_SIZE);

        BuildLabel();
        ApplyGeometry();
        SetAlpha(0f);
    }

    // Optional status line ("Entering…"). A load can take several seconds on
    // Quest, and an unexplained black void for that long reads as a crash — the
    // label is what makes the wait feel intentional. Skipped silently if TMP has
    // no default font, so a font-less project degrades to fade-only rather than
    // throwing during a scene transition.
    private void BuildLabel()
    {
        labelRoot = new GameObject("Status");
        labelRoot.transform.SetParent(transform, false);

        Canvas canvas = labelRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 32001;

        label = labelRoot.AddComponent<TextMeshProUGUI>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 40f;
        label.color = Color.white;
        label.raycastTarget = false;

        if (label.font == null)
        {
            Debug.LogWarning("[SceneFader] No TMP default font — transition will fade without a status label.");
            Destroy(labelRoot);
            labelRoot = null;
            label = null;
            return;
        }

        RectTransform rect = (RectTransform)labelRoot.transform;
        rect.sizeDelta = new Vector2(600f, 150f);

        labelRoot.SetActive(false);
    }

    // Places both canvases in front of the eyes at the current distance. Called
    // whenever the camera (and therefore its near clip) changes.
    private void ApplyGeometry()
    {
        float scale = distance * SCALE_PER_METRE;

        if (screen != null)
        {
            screen.transform.localPosition = new Vector3(0f, 0f, distance);
            screen.transform.localScale = Vector3.one * scale;
        }

        if (labelRoot != null)
        {
            // A hair nearer than the black panel so it sorts in front of it.
            labelRoot.transform.localPosition = new Vector3(0f, 0f, distance * 0.98f);
            labelRoot.transform.localScale = Vector3.one * (scale * 0.35f);
        }
    }

    // Re-posed every frame in LateUpdate — after any camera/rig movement for the
    // frame, so the overlay can never lag a fast head turn by a frame and leak the
    // world in at the edge.
    private void LateUpdate()
    {
        if (cam == null && !AcquireCamera()) return;

        transform.SetPositionAndRotation(cam.position, cam.rotation);
    }

    // The camera is re-resolved rather than cached across loads: the previous
    // scene's rig is destroyed by a single-mode load, so the reference we held is
    // dead by the time the new scene appears.
    private bool AcquireCamera()
    {
        Camera found = Camera.main;
        if (found == null) found = FindObjectOfType<Camera>();
        if (found == null) return false;

        cam = found.transform;

        // Honour an unusual near clip rather than silently rendering the fade
        // inside it (where it would be clipped away and hide nothing).
        float minimum = found.nearClipPlane * 2f;
        distance = Mathf.Max(DEFAULT_DISTANCE, minimum);
        ApplyGeometry();
        return true;
    }

    // Unscaled time throughout: a transition must run at the same speed whatever
    // Time.timeScale happens to be.
    public IEnumerator FadeTo(float target, float seconds)
    {
        target = Mathf.Clamp01(target);

        if (seconds <= 0f)
        {
            SetAlpha(target);
            yield break;
        }

        float start = alpha;
        for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / seconds)
        {
            SetAlpha(Mathf.Lerp(start, target, t));
            yield return null;
        }

        SetAlpha(target);
    }

    public void SetStatus(string text)
    {
        if (labelRoot == null) return;

        bool show = !string.IsNullOrEmpty(text);
        if (show) label.text = text;
        labelRoot.SetActive(show);
    }

    private void SetAlpha(float value)
    {
        alpha = Mathf.Clamp01(value);
        if (screen == null) return;

        screen.color = new Color(0f, 0f, 0f, alpha);

        // Fully clear = fully out of the way. Deactivating drops the draw call and,
        // more importantly, guarantees the overlay cannot affect the scene it is
        // no longer covering.
        bool visible = alpha > 0.001f;
        if (screen.gameObject.activeSelf != visible)
            screen.gameObject.SetActive(visible);
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Editor-only restyler for the VR conversation canvas (Prompt Canvas v2 and friends):
// turns the stock white-ish Unity UI into a dark, near-opaque panel in the Meta Horizon
// store idiom. Menu: Tools/History Speaks/Apply Dark UI Style, operating on the
// GameObject selected in the Hierarchy.
//
// Design constraints this tool deliberately respects:
//   * It only ever writes COLOURS, SPRITES (+ the slicing values that make a sprite
//     render at the right corner radius) and PADDING. It never touches anchors,
//     anchoredPosition, sizeDelta, object names, component references or hierarchy.
//     That is why the "24px vertical gaps" part of the brief is reported as SKIPPED
//     on this canvas: nothing here uses a LayoutGroup, so real gaps would mean moving
//     RectTransforms. Layout groups ARE handled if any are added later - re-run then.
//   * Panel padding is delivered through TMP_Text.margin, which is genuine internal
//     padding and leaves the RectTransform alone.
//   * Idempotent: every write is compared first, so a second run reports 0 changes.
//     Safe to run repeatedly while tuning the constants below.
//
// Corner radii are expressed in canvas units and converted to Image.pixelsPerUnit-
// Multiplier, so ONE generated 9-sliced sprite serves every radius (panel 28, input 16,
// pill buttons = height/2). See MultiplierForRadius for the arithmetic.
public static class DarkUIStyler
{
    // =====================================================================
    //  TWEAKABLE CONSTANTS - edit these and re-run the menu item.
    // =====================================================================

    // -- Surfaces --------------------------------------------------------
    const string PanelBgHex = "#262626";   // main panel fill
    const float PanelBgAlpha = 0.96f;      // near-opaque, still a hint of the room behind
    const string InputBgHex = "#1A1A1A";   // input field / dropdown well

    // -- Text (never pure white; see SubmitTextHex for the one sanctioned exception) --
    const string PrimaryTextHex = "#EDEDED";
    const string SecondaryTextHex = "#A0A0A0";
    const string PlaceholderTextHex = "#808080";

    // -- Buttons ---------------------------------------------------------
    const string RecordFillHex = "#3A3A3A";
    const string RecordTextHex = "#EDEDED";
    const string SubmitFillHex = "#4A6FA5";
    const string SubmitTextHex = "#FFFFFF";   // brief specifies pure white HERE only
    const float DisabledAlpha = 0.40f;        // ColorBlock disabled state
    const float HoverTint = 0.88f;            // multiplicative, so hover darkens slightly
    const float PressedTint = 0.75f;

    // -- Geometry (canvas units) ------------------------------------------
    const float PanelCornerRadius = 28f;
    const float InputCornerRadius = 16f;
    const bool PillButtons = true;            // true => radius is button height / 2
    const float ButtonCornerRadius = 16f;     // used only when PillButtons is false

    // -- Padding / spacing (canvas units) ---------------------------------
    const float PanelPadding = 36f;           // applied as TMP margin on panel text children
    const float InputHorizontalPadding = 24f; // applied as TMP margin on input text + placeholder
    const int SectionSpacing = 24;            // applied to LayoutGroups only (see header note)

    // -- Classification tokens (lower-case, substring match on object name) --
    static readonly string[] SubmitButtonTokens = { "submit" };
    static readonly string[] RecordButtonTokens = { "record" };
    static readonly string[] PanelTokens = { "panel", "background", "backdrop", "template", "board" };
    static readonly string[] GlyphTokens = { "arrow", "checkmark", "handle", "icon" };
    // Matched against the text object's own name AND its parent's name.
    static readonly string[] SecondaryTextTokens =
        { "placeholder", "hint", "caption", "countdown", "instruction", "subtitle", "secondary", "helper" };

    // -- Generated sprite --------------------------------------------------
    const string GeneratedFolder = "Assets/UI/Generated";
    const string RoundedSpritePath = GeneratedFolder + "/UIRoundedRect.png";
    const int SpriteTextureSize = 128;   // px
    const int SpriteCornerRadiusPx = 48; // px; also the 9-slice border on all four sides
    const int SpriteSupersample = 4;     // NxN coverage samples per pixel (antialiasing)
    const float SpritePixelsPerUnit = 100f;

    // =====================================================================

    static readonly Color PanelBg = Hex(PanelBgHex, PanelBgAlpha);
    static readonly Color InputBg = Hex(InputBgHex);
    static readonly Color PrimaryText = Hex(PrimaryTextHex);
    static readonly Color SecondaryText = Hex(SecondaryTextHex);
    static readonly Color PlaceholderText = Hex(PlaceholderTextHex);
    static readonly Color RecordFill = Hex(RecordFillHex);
    static readonly Color RecordText = Hex(RecordTextHex);
    static readonly Color SubmitFill = Hex(SubmitFillHex);
    static readonly Color SubmitText = Hex(SubmitTextHex);

    const string MenuRoot = "Tools/History Speaks/";
    const string UndoName = "Apply Dark UI Style";

    // ----------------------------------------------------------------- menu items

    [MenuItem(MenuRoot + "Apply Dark UI Style", false, 200)]
    static void Apply() => Run(dryRun: false);

    [MenuItem(MenuRoot + "Apply Dark UI Style", true)]
    static bool ApplyValidate() => Selection.activeGameObject != null;

    [MenuItem(MenuRoot + "Apply Dark UI Style [Dry Run]", false, 201)]
    static void ApplyDryRun() => Run(dryRun: true);

    [MenuItem(MenuRoot + "Apply Dark UI Style [Dry Run]", true)]
    static bool ApplyDryRunValidate() => Selection.activeGameObject != null;

    // ----------------------------------------------------------------- entry point

    static void Run(bool dryRun)
    {
        GameObject root = Selection.activeGameObject;
        if (root == null || root.GetComponent<RectTransform>() == null)
        {
            EditorUtility.DisplayDialog(UndoName,
                "Select the canvas root (a GameObject with a RectTransform, e.g. \"Prompt Canvas v2\") " +
                "in the Hierarchy first.", "OK");
            return;
        }

        Canvas canvas = root.GetComponentInParent<Canvas>();
        if (canvas == null)
            Debug.LogWarning($"[DarkUI] No Canvas found on or above \"{root.name}\"; " +
                             "assuming referencePixelsPerUnit = 100 for corner-radius maths.");

        // The sprite is an asset edit, so it happens outside the undo group.
        Sprite rounded = dryRun ? FindRoundedSprite() : EnsureRoundedSprite();
        if (!dryRun && rounded == null)
        {
            Debug.LogError("[DarkUI] Could not create or load the rounded-corner sprite; aborting.");
            return;
        }

        int undoGroup = -1;
        if (!dryRun)
        {
            Undo.SetCurrentGroupName(UndoName);
            undoGroup = Undo.GetCurrentGroup();
        }

        var pass = new Pass(root, canvas, rounded, dryRun);
        pass.Execute();

        if (!dryRun)
        {
            pass.FinalisePrefabModifications();
            Undo.CollapseUndoOperations(undoGroup);
            if (root.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(root.scene);
        }

        Debug.Log(pass.BuildSummary(rounded));
    }

    // ----------------------------------------------------------------- the styling pass

    class Pass
    {
        readonly GameObject root;
        readonly Canvas canvas;
        readonly Sprite rounded;
        readonly bool dryRun;

        // Graphics claimed by a control (input background, button fill, labels...) so the
        // generic surface/text sweeps at the end don't restyle them a second time.
        readonly HashSet<Graphic> owned = new HashSet<Graphic>();
        readonly HashSet<Object> touched = new HashSet<Object>();
        readonly List<string> changes = new List<string>();
        readonly List<string> warnings = new List<string>();
        readonly List<string> skipped = new List<string>();
        readonly List<string> roles = new List<string>();

        public Pass(GameObject root, Canvas canvas, Sprite rounded, bool dryRun)
        {
            this.root = root;
            this.canvas = canvas;
            this.rounded = rounded;
            this.dryRun = dryRun;
        }

        public int ChangeCount => changes.Count;

        public void Execute()
        {
            // Order matters: controls claim their graphics before the generic sweeps run.
            StyleInputFields();
            StyleButtons();
            StyleOtherSelectables();
            StyleSurfaces();
            StyleTexts();
            StyleLayoutGroups();
        }

        // ---- 1. input fields ------------------------------------------------

        void StyleInputFields()
        {
            foreach (TMP_InputField field in root.GetComponentsInChildren<TMP_InputField>(true))
            {
                Role(field, "input field");

                Image bg = field.GetComponent<Image>() ?? field.targetGraphic as Image;
                if (bg != null)
                {
                    owned.Add(bg);
                    SetColor(bg, InputBg, "input background");
                    ApplyRounded(bg, InputCornerRadius, "input");
                }

                var placeholder = field.placeholder as TMP_Text;
                if (placeholder != null)
                {
                    owned.Add(placeholder);
                    SetColor(placeholder, PlaceholderText, "placeholder");
                    SetHorizontalMargin(placeholder, InputHorizontalPadding, "placeholder padding");
                }

                if (field.textComponent != null)
                {
                    owned.Add(field.textComponent);
                    SetColor(field.textComponent, PrimaryText, "input text");
                    SetHorizontalMargin(field.textComponent, InputHorizontalPadding, "input text padding");
                }

                // A light caret/selection on a #1A1A1A well; both are pure colour values.
                if (!field.customCaretColor)
                {
                    RecordUndo(field);
                    if (!dryRun) field.customCaretColor = true;
                    Change(field, "customCaretColor false -> true");
                }
                if (!Same(field.caretColor, PrimaryText))
                {
                    RecordUndo(field);
                    string before = ToHex(field.caretColor);
                    if (!dryRun) field.caretColor = PrimaryText;
                    Change(field, $"caret colour {before} -> {ToHex(PrimaryText)}");
                }
                Color selection = SubmitFill;
                selection.a = 0.45f;
                if (!Same(field.selectionColor, selection))
                {
                    RecordUndo(field);
                    string before = ToHex(field.selectionColor);
                    if (!dryRun) field.selectionColor = selection;
                    Change(field, $"selection colour {before} -> {ToHex(selection)}");
                }

                ApplyColorBlock(field, "input field");
            }
        }

        // ---- 2. buttons -----------------------------------------------------

        void StyleButtons()
        {
            foreach (Button button in root.GetComponentsInChildren<Button>(true))
            {
                string name = button.name.ToLowerInvariant();
                bool isSubmit = SubmitButtonTokens.Any(name.Contains);
                bool isRecord = RecordButtonTokens.Any(name.Contains);
                Color fill = isSubmit ? SubmitFill : RecordFill;
                Color label = isSubmit ? SubmitText : RecordText;
                string kind = isSubmit ? "submit button" : isRecord ? "record button" : "button (neutral)";
                Role(button, kind);

                Image bg = button.GetComponent<Image>() ?? button.targetGraphic as Image;
                if (bg != null)
                {
                    owned.Add(bg);
                    SetColor(bg, fill, kind + " fill");
                    float radius = PillButtons ? bg.rectTransform.rect.height * 0.5f : ButtonCornerRadius;
                    ApplyRounded(bg, radius, PillButtons ? "pill" : "button");
                }

                // Labels are the button's own text children only - never a nested control's.
                foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (text.GetComponentInParent<Button>() != button) continue;
                    owned.Add(text);
                    SetColor(text, label, kind + " label");
                }
                foreach (Text text in button.GetComponentsInChildren<Text>(true))
                {
                    if (text.GetComponentInParent<Button>() != button) continue;
                    owned.Add(text);
                    SetColor(text, label, kind + " label (legacy)");
                }

                ApplyColorBlock(button, kind);
            }
        }

        // ---- 3. dropdowns, scrollbars, toggles ------------------------------

        void StyleOtherSelectables()
        {
            foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>(true))
            {
                if (selectable is Button || selectable is TMP_InputField || selectable is InputField)
                    continue;

                Image bg = selectable.targetGraphic as Image;
                bool isDropdown = selectable is TMP_Dropdown || selectable is Dropdown;

                if (bg != null && !owned.Contains(bg))
                {
                    owned.Add(bg);
                    SetColor(bg, InputBg, isDropdown ? "dropdown well" : "control surface");
                    // Only dropdowns get re-cornered; scrollbar/toggle art is left as authored
                    // so we don't reshape parts whose sprite carries meaning.
                    if (isDropdown) ApplyRounded(bg, InputCornerRadius, "dropdown");
                }

                Role(selectable, isDropdown ? "dropdown" : selectable.GetType().Name.ToLowerInvariant());
                ApplyColorBlock(selectable, isDropdown ? "dropdown" : "control");
            }
        }

        // ---- 4. remaining images: panels, glyphs, generic surfaces ----------

        void StyleSurfaces()
        {
            foreach (Image img in root.GetComponentsInChildren<Image>(true))
            {
                if (owned.Contains(img)) continue;
                owned.Add(img);

                string name = img.name.ToLowerInvariant();

                if (GlyphTokens.Any(name.Contains))
                {
                    Role(img, "glyph");
                    SetColor(img, SecondaryText, "glyph tint"); // sprite left alone
                    continue;
                }

                if (PanelTokens.Any(name.Contains) || img.transform.parent == root.transform)
                {
                    Role(img, "panel");
                    SetColor(img, PanelBg, "panel background");
                    ApplyRounded(img, PanelCornerRadius, "panel");
                    ApplyPanelPadding(img);
                    continue;
                }

                // Unclassified: darken it so nothing stays white, but keep its sprite.
                Role(img, "generic surface");
                SetColor(img, PanelBg, "generic surface tint");
            }
        }

        // Internal panel padding without touching any RectTransform: TMP margin on the
        // panel's own text children.
        void ApplyPanelPadding(Image panel)
        {
            foreach (Transform child in panel.transform)
            {
                var text = child.GetComponent<TMP_Text>();
                if (text == null || owned.Contains(text)) continue;
                Vector4 margin = text.margin;
                var wanted = new Vector4(PanelPadding, PanelPadding, PanelPadding, PanelPadding);
                if (Same(margin, wanted)) continue;
                RecordUndo(text);
                if (!dryRun) text.margin = wanted;
                Change(text, $"panel padding {Fmt(margin)} -> {Fmt(wanted)}");
            }
        }

        // ---- 5. every remaining text ----------------------------------------

        void StyleTexts()
        {
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (owned.Contains(text)) continue;
                owned.Add(text);
                bool secondary = IsSecondary(text);
                Role(text, secondary ? "secondary text" : "primary text");
                SetColor(text, secondary ? SecondaryText : PrimaryText,
                    secondary ? "secondary text" : "primary text");
            }

            foreach (Text text in root.GetComponentsInChildren<Text>(true))
            {
                if (owned.Contains(text)) continue;
                owned.Add(text);
                bool secondary = IsSecondary(text);
                Role(text, (secondary ? "secondary text" : "primary text") + " (legacy)");
                SetColor(text, secondary ? SecondaryText : PrimaryText, "legacy text");
            }
        }

        static bool IsSecondary(Graphic text)
        {
            string self = text.name.ToLowerInvariant();
            string parent = text.transform.parent != null
                ? text.transform.parent.name.ToLowerInvariant() : string.Empty;
            return SecondaryTextTokens.Any(t => self.Contains(t) || parent.Contains(t));
        }

        // ---- 6. layout groups (spacing/padding) ------------------------------

        void StyleLayoutGroups()
        {
            var groups = root.GetComponentsInChildren<HorizontalOrVerticalLayoutGroup>(true);
            if (groups.Length == 0)
            {
                skipped.Add(
                    $"Vertical gaps ({SectionSpacing}px between header / input / button row) NOT applied: " +
                    "this canvas positions every element by anchoredPosition and has no LayoutGroup. " +
                    "Applying real gaps would move RectTransforms, which this tool is required to preserve. " +
                    "Add a VerticalLayoutGroup and re-run, or move them by hand in the Inspector.");
                return;
            }

            foreach (HorizontalOrVerticalLayoutGroup group in groups)
            {
                Role(group, "layout group");
                int pad = Mathf.RoundToInt(PanelPadding);
                RectOffset padding = group.padding;
                if (padding.left != pad || padding.right != pad || padding.top != pad || padding.bottom != pad)
                {
                    RecordUndo(group);
                    string before = $"({padding.left},{padding.top},{padding.right},{padding.bottom})";
                    if (!dryRun) group.padding = new RectOffset(pad, pad, pad, pad);
                    Change(group, $"padding {before} -> ({pad},{pad},{pad},{pad})");
                }
                if (!Mathf.Approximately(group.spacing, SectionSpacing))
                {
                    RecordUndo(group);
                    float before = group.spacing;
                    if (!dryRun) group.spacing = SectionSpacing;
                    Change(group, $"spacing {before} -> {SectionSpacing}");
                }
            }
        }

        // ----------------------------------------------------------------- writers

        void SetColor(Graphic graphic, Color color, string label)
        {
            if (graphic == null || Same(graphic.color, color)) return;
            RecordUndo(graphic);
            string before = ToHex(graphic.color);
            if (!dryRun) graphic.color = color;
            Change(graphic, $"{label} {before} -> {ToHex(color)}");
        }

        void SetHorizontalMargin(TMP_Text text, float padding, string label)
        {
            Vector4 margin = text.margin;
            var wanted = new Vector4(padding, margin.y, padding, margin.w);
            if (Same(margin, wanted)) return;
            RecordUndo(text);
            if (!dryRun) text.margin = wanted;
            Change(text, $"{label} {Fmt(margin)} -> {Fmt(wanted)}");
        }

        // Sprite + slicing so the rendered corner radius lands on `desiredRadius`
        // canvas units. Radius is clamped to half the shorter side, otherwise Unity
        // overlaps the 9-slice corners and the rect renders squashed.
        void ApplyRounded(Image img, float desiredRadius, string what)
        {
            if (rounded == null) return;

            if (img.GetComponent<Mask>() != null)
            {
                warnings.Add($"{PathOf(img)} : has a Mask - sprite left as authored " +
                             "(swapping it would change the mask shape). Colour still applied.");
                return;
            }

            Rect rect = img.rectTransform.rect;
            float radius = desiredRadius;
            float maxRadius = Mathf.Min(Mathf.Abs(rect.width), Mathf.Abs(rect.height)) * 0.5f;
            if (maxRadius > 0.01f && radius > maxRadius)
            {
                warnings.Add($"{PathOf(img)} : {what} radius clamped {desiredRadius:0.#} -> {maxRadius:0.#} " +
                             $"(rect is {rect.width:0.#}x{rect.height:0.#}).");
                radius = maxRadius;
            }

            WarnOnNonUniformScale(img, radius);

            if (img.sprite != rounded)
            {
                RecordUndo(img);
                string before = img.sprite != null ? img.sprite.name : "none";
                if (!dryRun) img.sprite = rounded;
                Change(img, $"{what} sprite {before} -> {rounded.name}");
            }
            if (img.type != Image.Type.Sliced)
            {
                RecordUndo(img);
                Image.Type before = img.type;
                if (!dryRun) img.type = Image.Type.Sliced;
                Change(img, $"{what} image type {before} -> Sliced");
            }
            if (!img.fillCenter)
            {
                RecordUndo(img);
                if (!dryRun) img.fillCenter = true;
                Change(img, $"{what} fillCenter false -> true");
            }

            float multiplier = MultiplierForRadius(radius);
            if (!Mathf.Approximately(img.pixelsPerUnitMultiplier, multiplier))
            {
                RecordUndo(img);
                float before = img.pixelsPerUnitMultiplier;
                if (!dryRun) img.pixelsPerUnitMultiplier = multiplier;
                Change(img, $"{what} corner radius {radius:0.#}px " +
                            $"(pixelsPerUnitMultiplier {before:0.###} -> {multiplier:0.###})");
            }
        }

        // Image.pixelsPerUnit = sprite.pixelsPerUnit / canvas.referencePixelsPerUnit, and the
        // sliced border is divided by (pixelsPerUnit * pixelsPerUnitMultiplier). So:
        //     renderedRadius = borderPx / (ppu * multiplier)
        //  => multiplier      = borderPx / (ppu * desiredRadius)
        float MultiplierForRadius(float radiusUnits)
        {
            float reference = canvas != null ? canvas.referencePixelsPerUnit : 100f;
            float ppu = rounded.pixelsPerUnit / Mathf.Max(0.0001f, reference);
            return Mathf.Max(0.01f, SpriteCornerRadiusPx / Mathf.Max(0.01f, ppu * radiusUnits));
        }

        // A 9-sliced corner is scaled by the transform like everything else, so a
        // non-uniform lossyScale renders the "round" corner as an ellipse. Worth saying
        // out loud - several panels in this canvas are squashed on one axis.
        void WarnOnNonUniformScale(Image img, float radius)
        {
            Vector3 scale = img.transform.lossyScale;
            if (Mathf.Abs(scale.x) < 0.0001f || Mathf.Abs(scale.y) < 0.0001f) return;
            float ratio = Mathf.Abs(scale.x / scale.y);
            if (ratio > 1.05f || ratio < 0.95f)
                warnings.Add($"{PathOf(img)} : non-uniform scale ({scale.x:0.###}, {scale.y:0.###}) " +
                             $"renders the {radius:0.#}px corner as an ellipse " +
                             $"(~{radius * scale.x / scale.y:0.#}px horizontal vs {radius:0.#}px vertical). " +
                             "Colour/sprite applied anyway; fix the scale if the corners look wrong.");
        }

        // Normal keeps the authored fill; disabled is the requested flat 40% alpha.
        // colorMultiplier stays at 1 on purpose - it multiplies alpha too, so anything
        // else would stop disabled from landing on exactly 40%.
        void ApplyColorBlock(Selectable selectable, string what)
        {
            ColorBlock block = selectable.colors;
            ColorBlock wanted = block;
            wanted.normalColor = Color.white;
            wanted.highlightedColor = new Color(HoverTint, HoverTint, HoverTint, 1f);
            wanted.pressedColor = new Color(PressedTint, PressedTint, PressedTint, 1f);
            wanted.selectedColor = Color.white;
            wanted.disabledColor = new Color(1f, 1f, 1f, DisabledAlpha);
            wanted.colorMultiplier = 1f;

            if (Same(block.normalColor, wanted.normalColor) &&
                Same(block.highlightedColor, wanted.highlightedColor) &&
                Same(block.pressedColor, wanted.pressedColor) &&
                Same(block.selectedColor, wanted.selectedColor) &&
                Same(block.disabledColor, wanted.disabledColor) &&
                Mathf.Approximately(block.colorMultiplier, wanted.colorMultiplier))
                return;

            RecordUndo(selectable);
            string before = ToHex(block.disabledColor);
            if (!dryRun) selectable.colors = wanted;
            Change(selectable, $"{what} tint states (disabled {before} -> {ToHex(wanted.disabledColor)})");
        }

        // ----------------------------------------------------------------- bookkeeping

        void RecordUndo(Object target)
        {
            if (dryRun) return;
            Undo.RecordObject(target, UndoName);
            touched.Add(target);
        }

        public void FinalisePrefabModifications()
        {
            foreach (Object target in touched)
            {
                EditorUtility.SetDirty(target);
                if (PrefabUtility.IsPartOfPrefabInstance(target))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
        }

        void Change(Object target, string what) => changes.Add($"{PathOf(target)} : {what}");

        void Role(Object target, string role) => roles.Add($"{PathOf(target)} -> {role}");

        public string BuildSummary(Sprite sprite)
        {
            var sb = new StringBuilder();
            sb.AppendLine(dryRun
                ? "=== Dark UI Style - DRY RUN (nothing was changed) ==="
                : "=== Dark UI Style - APPLIED ===");
            sb.AppendLine($"Canvas root: {root.name}");
            sb.AppendLine($"Rounded sprite: {(sprite != null ? AssetDatabase.GetAssetPath(sprite) : "(none)")}");
            sb.AppendLine($"Elements classified: {roles.Count}    " +
                          $"{(dryRun ? "Would change" : "Changed")}: {changes.Count}");

            sb.AppendLine();
            sb.AppendLine($"-- {(dryRun ? "Planned changes" : "Changes")} ({changes.Count}) --");
            if (changes.Count == 0) sb.AppendLine("   (none - already styled)");
            else foreach (string line in changes) sb.AppendLine("   " + line);

            sb.AppendLine();
            sb.AppendLine($"-- Warnings ({warnings.Count}) --");
            if (warnings.Count == 0) sb.AppendLine("   (none)");
            else foreach (string line in warnings) sb.AppendLine("   " + line);

            sb.AppendLine();
            sb.AppendLine($"-- Not applied ({skipped.Count}) --");
            if (skipped.Count == 0) sb.AppendLine("   (none)");
            else foreach (string line in skipped) sb.AppendLine("   " + line);

            sb.AppendLine();
            sb.AppendLine($"-- Classification ({roles.Count}) --");
            foreach (string line in roles) sb.AppendLine("   " + line);

            return sb.ToString();
        }
    }

    // ----------------------------------------------------------------- sprite asset

    static Sprite FindRoundedSprite() => AssetDatabase.LoadAssetAtPath<Sprite>(RoundedSpritePath);

    // The only rounded 9-sliced sprites already in this project live under Assets/Samples
    // (XR Hands / XRI starter assets) with the wrong radii, and they get wiped whenever a
    // sample is reimported - so the tool owns its own asset instead of adopting one.
    static Sprite EnsureRoundedSprite()
    {
        Sprite existing = FindRoundedSprite();
        if (existing != null) return existing;

        foreach (string folder in new[] { "Assets/UI", GeneratedFolder })
        {
            if (AssetDatabase.IsValidFolder(folder)) continue;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        byte[] png = RenderRoundedRectPng();
        File.WriteAllBytes(Path.GetFullPath(RoundedSpritePath), png);
        AssetDatabase.ImportAsset(RoundedSpritePath, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(RoundedSpritePath) as TextureImporter;
        if (importer == null)
        {
            Debug.LogError($"[DarkUI] No TextureImporter at {RoundedSpritePath}.");
            return null;
        }
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spriteBorder = new Vector4(
            SpriteCornerRadiusPx, SpriteCornerRadiusPx, SpriteCornerRadiusPx, SpriteCornerRadiusPx);
        importer.spritePixelsPerUnit = SpritePixelsPerUnit;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.isReadable = false;
        // Uncompressed: it is a 128x128 alpha ramp, and block compression puts visible
        // blotches on the corner arcs. Costs ~64KB.
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();

        Debug.Log($"[DarkUI] Generated 9-sliced rounded sprite at {RoundedSpritePath} " +
                  $"({SpriteTextureSize}x{SpriteTextureSize}, {SpriteCornerRadiusPx}px border, " +
                  $"{SpritePixelsPerUnit} PPU).");
        return FindRoundedSprite();
    }

    // White rounded rect with coverage-based alpha; the Image tints it. Supersampled so
    // the arcs stay smooth when a panel is scaled up on the world-space canvas.
    static byte[] RenderRoundedRectPng()
    {
        int size = SpriteTextureSize;
        float radius = SpriteCornerRadiusPx;
        float half = size * 0.5f;
        var pixels = new Color32[size * size];
        float step = 1f / SpriteSupersample;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < SpriteSupersample; sy++)
                {
                    for (int sx = 0; sx < SpriteSupersample; sx++)
                    {
                        float px = x + (sx + 0.5f) * step;
                        float py = y + (sy + 0.5f) * step;
                        if (RoundedRectDistance(px - half, py - half, half, half, radius) <= 0f)
                            inside++;
                    }
                }
                byte alpha = (byte)Mathf.RoundToInt(
                    255f * inside / (SpriteSupersample * SpriteSupersample));
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels);
        texture.Apply();
        byte[] png = texture.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(texture);
        return png;
    }

    // Signed distance to a rounded rectangle centred on the origin (negative = inside).
    static float RoundedRectDistance(float x, float y, float halfWidth, float halfHeight, float radius)
    {
        float qx = Mathf.Abs(x) - (halfWidth - radius);
        float qy = Mathf.Abs(y) - (halfHeight - radius);
        float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) +
                                   Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
        return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
    }

    // ----------------------------------------------------------------- small helpers

    static Color Hex(string hex, float alpha = 1f)
    {
        Color color;
        if (!ColorUtility.TryParseHtmlString(hex, out color))
        {
            Debug.LogError($"[DarkUI] Constant \"{hex}\" is not a valid HTML colour; using magenta.");
            color = Color.magenta;
        }
        color.a = alpha;
        return color;
    }

    static bool Same(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f &&
        Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;

    static bool Same(Vector4 a, Vector4 b) => (a - b).sqrMagnitude < 0.0001f;

    static string ToHex(Color color) => "#" + ColorUtility.ToHtmlStringRGBA(color);

    static string Fmt(Vector4 v) => $"({v.x:0.#},{v.y:0.#},{v.z:0.#},{v.w:0.#})";

    static string PathOf(Object target)
    {
        Transform transform = target is Component component ? component.transform
            : target is GameObject go ? go.transform : null;
        if (transform == null) return target != null ? target.name : "(null)";

        var parts = new List<string>();
        for (Transform t = transform; t != null; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        string path = string.Join("/", parts);
        string type = target is Component c2 ? c2.GetType().Name : "GameObject";
        return $"{path} [{type}]";
    }
}

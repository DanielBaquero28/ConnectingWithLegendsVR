using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Editor-only batch texture optimizer for the digital-human character assets
// (Reallusion / Character Creator exports living under Assets/Characters,
// Assets/Custom Models, Assets/Reallusion). Those exporters drop dozens of 2K-4K
// maps per figure with sRGB left ON for every map and no Android override, which
// wrecks both the Quest 3 VRAM budget and colour correctness. This tool applies one
// consistent, reviewable ruleset across a whole folder in a single pass.
//
// Why these rules:
//   * ASTC 6x6 - the Adreno GPU on Quest 3 samples ASTC natively; 6x6 is the sweet
//     spot between memory and quality for character skin/cloth seen at arm's length.
//   * sRGB ON only for colour maps - Diffuse/BaseColor/Albedo carry authored colour
//     and MUST be gamma-decoded; every data map (Normal, Roughness, Metallic, AO,
//     Mask, Flow, SSS, ...) must stay linear or lighting goes subtly wrong.
//   * Size tiers - skin/head/body colour deserves 2048; eyes keep the exporter's
//     size (small but detail-critical); tiny detail maps (teeth, tongue, lashes,
//     tear line, occlusion, wrinkle) drop to 512; everything else 1024.
//
// Editor-only: it lives in an Editor folder, references UnityEditor, and ships
// nothing to the runtime build. Two menu items - a real pass and a dry run that only
// logs what it WOULD do. Nothing here has any effect on device behaviour.
public static class CharacterTextureOptimizer
{
    const string MenuRoot = "Tools/History Speaks/";
    const string AndroidPlatform = "Android";
    const TextureImporterFormat AndroidFormat = TextureImporterFormat.ASTC_6x6;

    // Filename tokens that mark a map as authored COLOUR (sRGB ON). Anything without
    // one of these is treated as linear data (sRGB OFF).
    static readonly string[] ColorTokens = { "diffuse", "basecolor", "base_color", "albedo" };

    // A resolved max-size decision plus the rule that produced it (for the log).
    // MaxSize == null means "leave the importer's current size alone" (the Eye case).
    struct SizeRule
    {
        public int? MaxSize;
        public string Rule;
    }

    // ----------------------------------------------------------------- menu items

    [MenuItem(MenuRoot + "Optimize Character Textures (Selected Folder)", false, 100)]
    static void Optimize() => Run(dryRun: false);

    [MenuItem(MenuRoot + "Optimize Character Textures (Selected Folder)", true)]
    static bool OptimizeValidate() => GetSelectedFolders().Count > 0;

    [MenuItem(MenuRoot + "Optimize Character Textures (Selected Folder) [Dry Run]", false, 101)]
    static void OptimizeDryRun() => Run(dryRun: true);

    [MenuItem(MenuRoot + "Optimize Character Textures (Selected Folder) [Dry Run]", true)]
    static bool OptimizeDryRunValidate() => GetSelectedFolders().Count > 0;

    // ----------------------------------------------------------------- main pass

    static void Run(bool dryRun)
    {
        List<string> folders = GetSelectedFolders();
        if (folders.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Optimize Character Textures",
                "Select one or more folders in the Project window first.",
                "OK");
            return;
        }

        // Recursive by folder. "t:Texture" (not "t:Texture2D") deliberately also
        // returns Texture2DArray / Cubemap / RenderTexture so we can SKIP-and-log
        // them below rather than silently ignoring them.
        List<string> paths = AssetDatabase.FindAssets("t:Texture", folders.ToArray())
            .Select(AssetDatabase.GUIDToAssetPath)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        int changed = 0;
        var skipped = new List<string>();    // "path - reason"
        var srgbFlips = new List<string>();  // "path : old -> new"
        var plans = new List<string>();      // dry-run per-file detail

        // Real pass batches reimports; the dry run touches nothing so it needn't.
        bool batching = !dryRun;
        if (batching) AssetDatabase.StartAssetEditing();
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (EditorUtility.DisplayCancelableProgressBar(
                        dryRun ? "Analyzing textures" : "Optimizing textures",
                        path, (float)i / Mathf.Max(1, paths.Count)))
                    break;

                string lowerName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                string lowerPath = path.ToLowerInvariant();

                // ---- Rule 4: skips (name-based first, then type/importer) ----
                if (lowerName.Contains("_info"))
                { skipped.Add($"{path} - name contains \"_info\""); continue; }
                if (lowerName.Contains("diffusearray") || lowerName.Contains("normalarray"))
                { skipped.Add($"{path} - texture-array source (DiffuseArray/NormalArray)"); continue; }

                System.Type mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (mainType != typeof(Texture2D))
                { skipped.Add($"{path} - not a Texture2D ({mainType?.Name ?? "unknown"})"); continue; }

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                { skipped.Add($"{path} - no TextureImporter"); continue; }

                // ---- Rules 1-3: desired state ----
                bool desiredSrgb = IsColorMap(lowerName);
                SizeRule sizeRule = DetermineMaxSize(lowerName, lowerPath);

                TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings(AndroidPlatform);
                bool formatChange = !android.overridden || android.format != AndroidFormat;
                bool sizeChange = sizeRule.MaxSize.HasValue && android.maxTextureSize != sizeRule.MaxSize.Value;
                bool srgbChange = importer.sRGBTexture != desiredSrgb;

                if (!formatChange && !sizeChange && !srgbChange)
                    continue; // already compliant - not counted as changed

                changed++;

                if (srgbChange)
                    srgbFlips.Add($"{path} : sRGB {importer.sRGBTexture} -> {desiredSrgb}");

                if (dryRun)
                {
                    string sizeText = sizeRule.MaxSize.HasValue
                        ? sizeRule.MaxSize.Value.ToString()
                        : $"keep {android.maxTextureSize}";
                    plans.Add($"{path}\n      format: ASTC_6x6 | maxSize: {sizeText} ({sizeRule.Rule}) | sRGB: {desiredSrgb}");
                    continue;
                }

                android.overridden = true;
                android.format = AndroidFormat;
                if (sizeRule.MaxSize.HasValue)
                    android.maxTextureSize = sizeRule.MaxSize.Value;
                importer.SetPlatformTextureSettings(android);
                importer.sRGBTexture = desiredSrgb;
                importer.SaveAndReimport();
            }
        }
        finally
        {
            if (batching) AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
            if (!dryRun) AssetDatabase.Refresh();
        }

        LogSummary(dryRun, folders, paths.Count, changed, skipped, srgbFlips, plans);
    }

    // ----------------------------------------------------------------- rules

    static bool IsColorMap(string lowerName) => ColorTokens.Any(lowerName.Contains);

    // First match wins - the ordering here IS the spec.
    static SizeRule DetermineMaxSize(string lowerName, string lowerPath)
    {
        if (lowerName.Contains("wrinkle"))
            return new SizeRule { MaxSize = 512, Rule = "Wrinkle" };

        string[] detail = { "teeth", "tongue", "lash", "tearline", "occlusion" };
        if (detail.Any(lowerPath.Contains))
            return new SizeRule { MaxSize = 512, Rule = "Teeth/Tongue/Lash/TearLine/Occlusion" };

        // Eyes are small but detail-critical; leave whatever size the exporter chose.
        // "cornea" is included because some eye maps are named for the cornea and
        // never carry the word "eye". Occlusion already matched above, so this
        // reaches only real eye colour/data.
        if ((lowerPath.Contains("eye") || lowerPath.Contains("cornea")) && !lowerPath.Contains("occlusion"))
            return new SizeRule { MaxSize = null, Rule = "Eye/Cornea (keep size)" };

        if (IsColorMap(lowerName) &&
            (lowerPath.Contains("body") || lowerPath.Contains("head") || lowerPath.Contains("skin")))
            return new SizeRule { MaxSize = 2048, Rule = "Skin/Head/Body colour" };

        return new SizeRule { MaxSize = 1024, Rule = "Default" };
    }

    // ----------------------------------------------------------------- helpers

    static List<string> GetSelectedFolders()
    {
        var folders = new List<string>();
        foreach (UnityEngine.Object obj in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path) && !folders.Contains(path))
                folders.Add(path);
        }
        return folders;
    }

    static void LogSummary(bool dryRun, List<string> folders, int scanned, int changed,
        List<string> skipped, List<string> srgbFlips, List<string> plans)
    {
        var sb = new StringBuilder();
        sb.AppendLine(dryRun
            ? "=== Character Texture Optimizer - DRY RUN (nothing was changed) ==="
            : "=== Character Texture Optimizer - APPLIED ===");
        sb.AppendLine($"Folders: {string.Join(", ", folders)}");
        sb.AppendLine($"Textures scanned: {scanned}");
        sb.AppendLine($"{(dryRun ? "Would change" : "Changed")}: {changed}    Skipped: {skipped.Count}");

        sb.AppendLine();
        sb.AppendLine($"-- sRGB flips ({srgbFlips.Count}) --");
        if (srgbFlips.Count == 0) sb.AppendLine("   (none)");
        else foreach (string f in srgbFlips) sb.AppendLine("   " + f);

        sb.AppendLine();
        sb.AppendLine($"-- Skipped ({skipped.Count}) --");
        if (skipped.Count == 0) sb.AppendLine("   (none)");
        else foreach (string s in skipped) sb.AppendLine("   " + s);

        if (dryRun)
        {
            sb.AppendLine();
            sb.AppendLine($"-- Planned changes ({plans.Count}) --");
            if (plans.Count == 0) sb.AppendLine("   (none)");
            else foreach (string p in plans) sb.AppendLine("   " + p);
        }

        Debug.Log(sb.ToString());
    }
}

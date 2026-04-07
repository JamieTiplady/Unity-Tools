
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class SetStatic : EditorWindow
{
    [MenuItem("Tools/Set Static")]
    public static void ShowWindow() => GetWindow<SetStatic>("Set Static");

    private string matchString = "_ST_";
    private bool affectChildren = true;
    private bool skipAnimated = true;

    private bool includeUI, includeVFX, includeLights, includeAudio;
    private bool include3D = true;
    private bool include2D = true;

    private Dictionary<string, bool> tagToggles = new Dictionary<string, bool>();
    private LayerMask excludeLayers;

    // We store the paths to the prefabs that contain matches
    private List<string> previewPaths = new List<string>();
    private Vector2 previewScroll;
    private bool showFilters = true, showTags = true;

    private void OnGUI()
    {
        GUILayout.Label("Set Static Based On Name", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck(); // Start checking for UI changes

        matchString = EditorGUILayout.TextField("Match String", matchString);
        affectChildren = EditorGUILayout.Toggle("Include Children", affectChildren);

        showFilters = EditorGUILayout.Foldout(showFilters, "Filters", true);
        if (showFilters)
        {
            EditorGUI.indentLevel++;
            includeUI = EditorGUILayout.Toggle("Include UI", includeUI);
            includeVFX = EditorGUILayout.Toggle("Include VFX", includeVFX);
            includeLights = EditorGUILayout.Toggle("Include Lights", includeLights);
            includeAudio = EditorGUILayout.Toggle("Include Audio", includeAudio);
            include3D = EditorGUILayout.Toggle("Include 3D Objects", include3D);
            include2D = EditorGUILayout.Toggle("Include 2D Objects", include2D);
            EditorGUI.indentLevel--;
        }

        showTags = EditorGUILayout.Foldout(showTags, "Tags", true);
        if (showTags)
        {
            EditorGUI.indentLevel++;
            InitTagToggles();
            var keys = new List<string>(tagToggles.Keys);
            foreach (var tag in keys)
            {
                tagToggles[tag] = EditorGUILayout.Toggle(tag, tagToggles[tag]);
            }
            EditorGUI.indentLevel--;
        }

        excludeLayers = EditorGUILayout.MaskField("Exclude Layers", excludeLayers, UnityEditorInternal.InternalEditorUtility.layers);
        skipAnimated = EditorGUILayout.Toggle("Skip Animated Objects", skipAnimated);

        // Only rebuild the list if the user changed a setting
        if (EditorGUI.EndChangeCheck())
        {
            BuildPreview();
        }

        GUILayout.Space(10);
        if (GUILayout.Button("Apply To Prefabs Listed", GUILayout.Height(30)))
        {
            if (EditorUtility.DisplayDialog("Confirm", "Modify ALL prefabs list across entire project?", "Yes", "Cancel"))
                ApplyToProjectPrefabs();
        }

        GUILayout.Space(10);
        GUILayout.Label($"Preview ({previewPaths.Count} Prefabs Affected)", EditorStyles.boldLabel);
        
        previewScroll = EditorGUILayout.BeginScrollView(previewScroll, GUILayout.Height(250));
        foreach (var path in previewPaths)
        {
            EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    private void BuildPreview()
    {
        previewPaths.Clear();
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);

            if (prefabRoot != null)
            {
                if (ContainsAnyMatches(prefabRoot))
                {
                    previewPaths.Add(path);
                }
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }
    }

    private bool ContainsAnyMatches(GameObject root)
    {
        // Simple recursive check to see if THIS prefab needs work
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (ShouldProcess(t.gameObject) && t.gameObject.name.Contains(matchString))
                return true;
        }
        return false;
    }

    private void ApplyToProjectPrefabs()
    {
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                EditorUtility.DisplayProgressBar("Processing", path, (float)i / guids.Length);

                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
                ProcessObjectRecursive(prefabRoot, false);
                
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
            Debug.Log("Static flags updated.");
        }
    }

    private void ProcessObjectRecursive(GameObject obj, bool parentIsStatic)
    {
        if (skipAnimated && (obj.GetComponent<Animator>() != null || HasAnimatorInParents(obj)))
            return;

        bool matches = obj.name.Contains(matchString);
        bool shouldBeStatic = (matches || parentIsStatic) && ShouldProcess(obj);

        if (obj.isStatic != shouldBeStatic)
        {
            obj.isStatic = shouldBeStatic;
            EditorUtility.SetDirty(obj);
        }

        if (affectChildren)
        {
            foreach (Transform child in obj.transform)
                ProcessObjectRecursive(child.gameObject, shouldBeStatic);
        }
    }

    private bool ShouldProcess(GameObject obj)
    {
        if (IsLayerExcluded(obj)) return false;
        if (!IsTagIncluded(obj)) return false;

        // Filter checks
        if (!includeUI && obj.GetComponent<RectTransform>()) return false;
        if (!includeVFX && (obj.GetComponent<ParticleSystem>() || obj.GetComponent<UnityEngine.VFX.VisualEffect>())) return false;
        if (!includeLights && obj.GetComponent<Light>()) return false;
        if (!includeAudio && obj.GetComponent<AudioSource>()) return false;
        if (!include3D && obj.GetComponent<MeshRenderer>()) return false;
        if (!include2D && obj.GetComponent<SpriteRenderer>()) return false;

        return true;
    }

    private bool IsTagIncluded(GameObject obj)
    {
        bool anySelected = false;
        foreach (var kvp in tagToggles)
        {
            if (kvp.Value) {
                anySelected = true;
                if (obj.CompareTag(kvp.Key)) return true;
            }
        }
        return !anySelected; 
    }

    private bool IsLayerExcluded(GameObject obj) => (excludeLayers.value & (1 << obj.layer)) != 0;

    private bool HasAnimatorInParents(GameObject obj)
    {
        Transform p = obj.transform.parent;
        while (p != null) {
            if (p.GetComponent<Animator>()) return true;
            p = p.parent;
        }
        return false;
    }

    private void InitTagToggles()
    {
        if (tagToggles.Count > 0) return;
        foreach (string tag in UnityEditorInternal.InternalEditorUtility.tags)
            tagToggles[tag] = false;
    }
}
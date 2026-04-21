using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Profiling;

public class ShaderComplexityTool : EditorWindow
{
    private Vector2 scrollPosition;
    private List<MaterialComplexityData> scannedMaterials = new List<MaterialComplexityData>();
    private GUIStyle richTextStyle;
    
    //track which tab is open
    private int selectedTab = 0; 
    private readonly string[] tabLabels = { "Scan Active Scene", "Scan All Project Assets" };

    //sorting State
    private enum SortType { Name, Passes, Textures, Memory, Transparent, Shader }
    private SortType currentSort = SortType.Passes;
    private bool sortDescending = true;

    private class MaterialComplexityData
    {
        public Material Mat;
        public string ShaderName;
        public int PassCount;
        public int TextureCount;
        public float TextureMemoryMB; 
        public bool IsTransparent;
    }

    [MenuItem("Tools/Shader Complexity Tool")]
    public static void ShowWindow() => GetWindow<ShaderComplexityTool>("Shader Complexity");

    private void OnGUI()
    {
        if (richTextStyle == null)
        {
            richTextStyle = new GUIStyle(EditorStyles.label) { richText = true };
        }

        GUILayout.Label("Shader Pass Scanner", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Ranking materials by Render Pass count. More passes generally mean more draw-call overhead per object.", MessageType.Info);

        EditorGUILayout.Space();

        //Toggle between modes - scene/project
        selectedTab = GUILayout.Toolbar(selectedTab, tabLabels);
        EditorGUILayout.Space();

        //change button text based on the selected tab
        string buttonText = selectedTab == 0 ? "Scan Active Scene" : "Scan Project Assets";

        if (GUILayout.Button(buttonText, GUILayout.Height(40)))
        {
            if (selectedTab == 0)
            {
                ScanScene();
            }
            else
            {
                ScanProjectAssets();
            }
        }

        EditorGUILayout.Space();

        if (scannedMaterials.Count > 0)
        {
            DrawHeader();
            DrawMaterialList();
        }
    }

    private void ScanScene()
    {
        scannedMaterials.Clear();
        
        Renderer[] sceneRenderers = FindObjectsOfType<Renderer>();
        HashSet<Material> uniqueMaterials = new HashSet<Material>();

        foreach (Renderer r in sceneRenderers)
        {
            foreach (Material m in r.sharedMaterials)
            {
                if (m != null) uniqueMaterials.Add(m);
            }
        }

        foreach (Material mat in uniqueMaterials)
        {
            scannedMaterials.Add(AnalyzeMaterial(mat));
        }

        //dynamic sorting instead of hardcoded sorting
        SortData();
    }

    private void ScanProjectAssets()
    {
        scannedMaterials.Clear();

        string[] searchFolders = new string[] { "Assets" };
        string[] guids = AssetDatabase.FindAssets("t:Material", searchFolders);
        
        foreach (string guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            
            if (mat != null)
            {
                scannedMaterials.Add(AnalyzeMaterial(mat));
            }
        }

        //dynamic sorting instead of hardcoded sorting
        SortData();
    }

    private MaterialComplexityData AnalyzeMaterial(Material mat)
    {
        Shader shader = mat.shader;
        if (shader == null) return new MaterialComplexityData { Mat = mat, ShaderName = "Hidden/Error" };

        int activeTexCount = 0;
        long totalTextureMemoryBytes = 0; 
        int propertyCount = ShaderUtil.GetPropertyCount(shader);

        for (int i = 0; i < propertyCount; i++)
        {
            if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
            {
                string propertyName = ShaderUtil.GetPropertyName(shader, i);
                Texture tex = mat.GetTexture(propertyName);
                
                if (tex != null)
                {
                    activeTexCount++;
                    totalTextureMemoryBytes += Profiler.GetRuntimeMemorySizeLong(tex); 
                }
            }
        }

        return new MaterialComplexityData
        {
            Mat = mat,
            ShaderName = shader.name,
            PassCount = mat.passCount,
            TextureCount = activeTexCount,
            TextureMemoryMB = totalTextureMemoryBytes / 1048576f, 
            IsTransparent = mat.renderQueue >= 3000
        };
    }

    //handle sorting logic for ordering the materials found
    private void SortData()
    {
        if (scannedMaterials == null || scannedMaterials.Count == 0) return;

        switch (currentSort)
        {
            case SortType.Name:
                scannedMaterials = sortDescending ? scannedMaterials.OrderByDescending(m => m.Mat.name).ToList() : scannedMaterials.OrderBy(m => m.Mat.name).ToList();
                break;
            case SortType.Passes:
                scannedMaterials = sortDescending ? scannedMaterials.OrderByDescending(m => m.PassCount).ToList() : scannedMaterials.OrderBy(m => m.PassCount).ToList();
                break;
            case SortType.Textures:
                scannedMaterials = sortDescending ? scannedMaterials.OrderByDescending(m => m.TextureCount).ToList() : scannedMaterials.OrderBy(m => m.TextureCount).ToList();
                break;
            case SortType.Memory:
                scannedMaterials = sortDescending ? scannedMaterials.OrderByDescending(m => m.TextureMemoryMB).ToList() : scannedMaterials.OrderBy(m => m.TextureMemoryMB).ToList();
                break;
            case SortType.Transparent:
                scannedMaterials = sortDescending ? scannedMaterials.OrderByDescending(m => m.IsTransparent).ToList() : scannedMaterials.OrderBy(m => m.IsTransparent).ToList();
                break;
            case SortType.Shader:
                scannedMaterials = sortDescending ? scannedMaterials.OrderByDescending(m => m.ShaderName).ToList() : scannedMaterials.OrderBy(m => m.ShaderName).ToList();
                break;
        }
    }

    //function to create clickable header buttons
    private void DrawSortableHeader(string label, float width, SortType sortType)
    {
        string displayLabel = label;
        
        //arrow to show which column is currently sorting the data
        if (currentSort == sortType)
        {
            displayLabel += sortDescending ? " ▼" : " ▲";
        }

        //EditorStyles.toolbarButton makes it look like a header but act like a button
        if (GUILayout.Button(displayLabel, EditorStyles.toolbarButton, GUILayout.Width(width)))
        {
            if (currentSort == sortType)
            {
                sortDescending = !sortDescending; //toggle direction if clicking the same column
            }
            else
            {
                currentSort = sortType;
                sortDescending = true; //default to descending when clicking a new column
            }
            SortData();
        }
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        
        //sortable clickable headers
        DrawSortableHeader("Material Name", 200, SortType.Name);
        DrawSortableHeader("Passes", 50, SortType.Passes);
        DrawSortableHeader("Textures", 60, SortType.Textures);
        DrawSortableHeader("Mem (MB)", 65, SortType.Memory);
        DrawSortableHeader("Transparent", 80, SortType.Transparent);
        DrawSortableHeader("Shader", 150, SortType.Shader);
        
        EditorGUILayout.EndHorizontal();
    }

    private void DrawMaterialList()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        foreach (var data in scannedMaterials)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            if (GUILayout.Button(data.Mat.name, EditorStyles.label, GUILayout.Width(200)))
            {
                EditorGUIUtility.PingObject(data.Mat);
                Selection.activeObject = data.Mat;
            }

            GUILayout.Label(data.PassCount.ToString(), GUILayout.Width(50));
            GUILayout.Label(data.TextureCount.ToString(), GUILayout.Width(60));
            GUILayout.Label(data.TextureMemoryMB.ToString("F2"), GUILayout.Width(65)); 
            GUILayout.Label(data.IsTransparent ? "Yes" : "No", GUILayout.Width(80));
            GUILayout.Label(data.ShaderName, EditorStyles.miniLabel, GUILayout.Width(150));

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }
}
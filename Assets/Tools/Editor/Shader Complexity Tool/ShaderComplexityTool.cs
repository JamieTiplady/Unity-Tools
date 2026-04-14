using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Profiling; // Added for memory profiling

public class ShaderComplexityTool : EditorWindow
{
    private Vector2 scrollPosition;
    private List<MaterialComplexityData> scannedMaterials = new List<MaterialComplexityData>();
    private GUIStyle richTextStyle;

    private class MaterialComplexityData
    {
        public Material Mat;
        public string ShaderName;
        public int PassCount;
        public int KeywordCount;
        public int TextureCount;
        public float TextureMemoryMB; // <-- NEW: Stores total texture memory
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

        GUILayout.Label("Scene Shader Pass Scanner", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Ranking materials by Render Pass count. More passes generally mean more draw-call overhead per object.", MessageType.Info);

        EditorGUILayout.Space();

        if (GUILayout.Button("Scan Active Scene", GUILayout.Height(40)))
        {
            ScanScene();
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
        
        // Find all renderers in the active scene
        Renderer[] sceneRenderers = FindObjectsOfType<Renderer>();
        HashSet<Material> uniqueMaterials = new HashSet<Material>();

        foreach (Renderer r in sceneRenderers)
        {
            foreach (Material m in r.sharedMaterials)
            {
                if (m != null) uniqueMaterials.Add(m);
            }
        }

        // Analyze each unique material
        foreach (Material mat in uniqueMaterials)
        {
            scannedMaterials.Add(AnalyzeMaterial(mat));
        }

        // Sort by highest Pass Count first
        scannedMaterials = scannedMaterials.OrderByDescending(m => m.PassCount).ToList();
    }

    private MaterialComplexityData AnalyzeMaterial(Material mat)
    {
        Shader shader = mat.shader;

        int activeTexCount = 0;
        long totalTextureMemoryBytes = 0; // <-- NEW: Track memory in bytes
        int propertyCount = ShaderUtil.GetPropertyCount(shader);

        for (int i = 0; i < propertyCount; i++)
        {
            // Check if the property is a texture
            if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
            {
                string propertyName = ShaderUtil.GetPropertyName(shader, i);
                Texture tex = mat.GetTexture(propertyName);
                
                // Check if there is actually a texture assigned to this slot
                if (tex != null)
                {
                    activeTexCount++;
                    // <-- NEW: Get the true runtime memory size of the texture (accounts for compression/mipmaps)
                    totalTextureMemoryBytes += Profiler.GetRuntimeMemorySizeLong(tex); 
                }
            }
        }

        return new MaterialComplexityData
        {
            Mat = mat,
            ShaderName = shader.name,
            PassCount = mat.passCount,
            KeywordCount = mat.shaderKeywords.Length,
            TextureCount = activeTexCount,
            TextureMemoryMB = totalTextureMemoryBytes / 1048576f, // <-- NEW: Convert Bytes to MB (1024 * 1024)
            IsTransparent = mat.renderQueue >= 3000
        };
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("Material Name", GUILayout.Width(200));
        GUILayout.Label("Passes", GUILayout.Width(50));
        GUILayout.Label("Keywords", GUILayout.Width(65));
        GUILayout.Label("Textures", GUILayout.Width(60));
        GUILayout.Label("Mem (MB)", GUILayout.Width(65)); // <-- NEW Column
        GUILayout.Label("Transparent", GUILayout.Width(80));
        GUILayout.Label("Shader", GUILayout.Width(150));
        EditorGUILayout.EndHorizontal();
    }

    private void DrawMaterialList()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        foreach (var data in scannedMaterials)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Using simple bolding for the primary ranking factor (Passes)
            if (GUILayout.Button(data.Mat.name, EditorStyles.label, GUILayout.Width(200)))
            {
                EditorGUIUtility.PingObject(data.Mat);
                Selection.activeObject = data.Mat;
            }

            // Highlight high pass counts in red/yellow for quick scanning
            string passColor = "white";
            if (data.PassCount > 9) passColor = "#FF4444"; // Red for 3+ passes
            else if (data.PassCount > 8) passColor = "#FFFF44"; // Yellow for 2 passes

            GUILayout.Label($"<color={passColor}><b>{data.PassCount}</b></color>", richTextStyle, GUILayout.Width(50));
            GUILayout.Label(data.KeywordCount.ToString(), GUILayout.Width(65));
            GUILayout.Label(data.TextureCount.ToString(), GUILayout.Width(60));
            
            // <-- NEW: Display the memory footprint to 2 decimal places
            GUILayout.Label(data.TextureMemoryMB.ToString("F2"), GUILayout.Width(65)); 
            
            GUILayout.Label(data.IsTransparent ? "Yes" : "No", GUILayout.Width(80));
            GUILayout.Label(data.ShaderName, EditorStyles.miniLabel, GUILayout.Width(150));

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }
}
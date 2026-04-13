using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Reflection; // Added for faster reflection

public class ProjectTextureQualityChecker : EditorWindow
{
    private List<TextureData> textureList = new List<TextureData>();
    private Vector2 scrollPosition;
    private GUIStyle redLabelStyle;
    
    
    // Store the reflection method here so we only find it once
    private MethodInfo getDimensionsMethod;

    private class TextureData
    {
        public string Name;
        public string AssetPath;
        public string Usage;
        public float FileSizeMB;
        public string Dimensions;
        public string Format;
        public bool Mipmaps;
        public bool IsPoT;
    }

    [MenuItem("Tools/Project Texture Quality Checker")]
    public static void ShowWindow() => GetWindow<ProjectTextureQualityChecker>("Texture Checker");

    private void OnGUI()
    {
        GUILayout.Label("Project Texture Quality Dashboard", EditorStyles.boldLabel);

        if (GUILayout.Button("Scan Project Textures", GUILayout.Height(30)))
        {
            ScanTextures();
        }

        EditorGUILayout.Space();
        DrawHeader();

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        
        //UI culling for speed
        //
        //This way all textures are loaded into memory but not drawn in unity. more efficient and 
        //reduces additional load on editor and PC.
        //
        
        float rowHeight = 20f; //height of one row
        //firstIndex looks at how far user scrolls and stores it in scrollPosition.y. This is then 
        //divided by the height of the row (rowHeight). This says draw number X instead of from 1
        int firstIndex = (int)(scrollPosition.y / rowHeight);
        //visibleCount works out the height of the window with position.height and says to draw
        //the number of rows that would fit plus 2 (for rounding errors)
        int visibleCount = (int)(position.height / rowHeight) + 2; 

        // Add padding at the top so the scrollbar works correctly
        GUILayout.Space(firstIndex * rowHeight);

        for (int i = firstIndex; i < Mathf.Min(firstIndex + visibleCount, textureList.Count); i++)
        {
            DrawRow(textureList[i]);
        }

        // Add padding at the bottom to maintain the total scroll height
        GUILayout.Space(Mathf.Max(0, (textureList.Count - (firstIndex + visibleCount)) * rowHeight));
        
        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("Name", GUILayout.Width(150));
        GUILayout.Label("Usage", GUILayout.Width(60));
        GUILayout.Label("Size (MB)", GUILayout.Width(70));
        GUILayout.Label("File Dimensions", GUILayout.Width(100));
        GUILayout.Label("Format", GUILayout.Width(80));
        GUILayout.Label("Mips", GUILayout.Width(50));
        GUILayout.Label("NPoT", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();
    }

    private void DrawRow(TextureData data)
    {
        if (redLabelStyle == null)
        {
            redLabelStyle = new GUIStyle(EditorStyles.label);
            redLabelStyle.normal.textColor = Color.red;
        }

        Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(20));
        
        // Minimalist hover check
        if (Event.current.type == EventType.Repaint && rowRect.Contains(Event.current.mousePosition))
            GUI.Box(rowRect, ""); 

        if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && rowRect.Contains(Event.current.mousePosition))
        {
            Object obj = AssetDatabase.LoadMainAssetAtPath(data.AssetPath);
            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
            Event.current.Use();
        }

        GUILayout.Label(data.Name, GUILayout.Width(150));
        GUILayout.Label(data.Usage, GUILayout.Width(60));
        GUILayout.Label(data.FileSizeMB.ToString("F2"), GUILayout.Width(70));
        GUILayout.Label(data.Dimensions, GUILayout.Width(100));
        GUILayout.Label(data.Format, GUILayout.Width(80));
        GUILayout.Label(data.Mipmaps ? "Yes" : "No", GUILayout.Width(50));
        GUILayout.Label(data.IsPoT ? "Yes" : "No", data.IsPoT ? EditorStyles.label : redLabelStyle, GUILayout.Width(50));
        
        EditorGUILayout.EndHorizontal();
    }

    private void ScanTextures()
    {
        textureList.Clear();
        string[] guids = AssetDatabase.FindAssets("t:Texture");
        
        // Cache reflection method once
        if (getDimensionsMethod == null)
            getDimensionsMethod = typeof(TextureImporter).GetMethod("GetWidthAndHeight", BindingFlags.NonPublic | BindingFlags.Instance);

        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);

            if (!path.StartsWith("Assets/") || path.Contains("/Editor/") || path.Contains("/Plugins/") || path.StartsWith("Packages/")) 
                continue;

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;

            int width = 0, height = 0;
            object[] args = new object[] { 0, 0 };
            getDimensionsMethod.Invoke(importer, args);
            width = (int)args[0];
            height = (int)args[1];

            FileInfo fileInfo = new FileInfo(path);

            textureList.Add(new TextureData
            {
                Name = Path.GetFileNameWithoutExtension(path),
                AssetPath = path,
                Usage = DetermineUsage(importer, path),
                FileSizeMB = (float)fileInfo.Length / 1048576f, // Faster than (1024*1024)
                Dimensions = $"{width}x{height}",
                Format = Path.GetExtension(path).Replace(".", "").ToUpper(),
                Mipmaps = importer.mipmapEnabled,
                IsPoT = (width != 0 && (width & (width - 1)) == 0) && (height != 0 && (height & (height - 1)) == 0)
            });
            
            // Only update progress bar every 20 items to save UI cycles
            if (i % 20 == 0)
                EditorUtility.DisplayProgressBar("Scanning", "Checking Texture Quality...", (float)i / guids.Length);
        }

        textureList.Sort((a, b) => b.FileSizeMB.CompareTo(a.FileSizeMB));
        EditorUtility.ClearProgressBar();
        UnityEngine.Debug.Log($"<color=green>Scan Complete!</color> Found {textureList.Count} textures.");
        this.Repaint();
    }

    private string DetermineUsage(TextureImporter importer, string path)
    {
        string lowerPath = path.ToLower();
        if (lowerPath.Contains("vfx") || lowerPath.Contains("particle") || lowerPath.Contains("effect")) return "VFX";
        if (importer.textureType == TextureImporterType.Sprite || importer.textureType == TextureImporterType.GUI) return "2D";
        return (importer.textureShape == TextureImporterShape.TextureCube) ? "3D (Sky)" : "3D";
    }
}

/*

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

public class ProjectTextureQualityChecker : EditorWindow
{
    private List<TextureData> textureList = new List<TextureData>();
    private Vector2 scrollPosition;
    private GUIStyle redLabelStyle;

    // Helper class to store data for the table
    private class TextureData
    {
        public string Name;
        public string AssetPath;
        public string Usage;
        public float FileSizeMB;
        public string Dimensions;
        public string Format;
        public bool Mipmaps;
        public bool IsPoT;
    }

    [MenuItem("Tools/Project Texture Quality Checker")]
    public static void ShowWindow()
    {
        GetWindow<ProjectTextureQualityChecker>("Texture Checker");
    }

    private void OnGUI()
    {
        GUILayout.Label("Project Texture Quality Dashboard", EditorStyles.boldLabel);

        if (GUILayout.Button("Scan Project Textures", GUILayout.Height(30)))
        {
            ScanTextures();
        }

        EditorGUILayout.Space();

        // Table Header
        DrawHeader();

        // Scrollable Table Content
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        foreach (var data in textureList)
        {
            DrawRow(data);
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("Name", GUILayout.Width(150));
        GUILayout.Label("Usage", GUILayout.Width(60));
        GUILayout.Label("Size (MB)", GUILayout.Width(70));
        GUILayout.Label("Dimension", GUILayout.Width(100));
        GUILayout.Label("Format", GUILayout.Width(80));
        GUILayout.Label("Mips", GUILayout.Width(50));
        GUILayout.Label("NPoT", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();
    }

    private void DrawRow(TextureData data)
    {
        // Initialize style only once
        if (redLabelStyle == null)
        {
            redLabelStyle = new GUIStyle(EditorStyles.label);
            redLabelStyle.normal.textColor = Color.red;
        }

        Rect rowRect = EditorGUILayout.BeginHorizontal();
        
        // Background highlight only on repaint to save CPU
        if (Event.current.type == EventType.Repaint && rowRect.Contains(Event.current.mousePosition))
        {
            GUI.Box(rowRect, ""); 
        }

        // Handle Double Click
        if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && rowRect.Contains(Event.current.mousePosition))
        {
            Object obj = AssetDatabase.LoadMainAssetAtPath(data.AssetPath);
            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
            Event.current.Use();
        }

        GUILayout.Label(data.Name, GUILayout.Width(150));
        GUILayout.Label(data.Usage, GUILayout.Width(60));
        GUILayout.Label(data.FileSizeMB.ToString("F2"), GUILayout.Width(70));
        GUILayout.Label(data.Dimensions, GUILayout.Width(100));
        GUILayout.Label(data.Format, GUILayout.Width(80));
        GUILayout.Label(data.Mipmaps ? "Yes" : "No", GUILayout.Width(50));

        // Use the cached style instead of creating a new one
        GUILayout.Label(data.IsPoT ? "Yes" : "No", data.IsPoT ? EditorStyles.label : redLabelStyle, GUILayout.Width(50));
        
        EditorGUILayout.EndHorizontal();
    }

    private void ScanTextures()
    {
        textureList.Clear();
        string[] guids = AssetDatabase.FindAssets("t:Texture");

        // Show a progress bar so the UI doesn't look "frozen"
        int count = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);

            if (!path.StartsWith("Assets/") || path.Contains("/Editor/") || path.Contains("/Plugins/") || path.StartsWith("Packages/")) 
                continue;

            // OPTIMIZATION: Get the importer WITHOUT loading the full texture into RAM
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;

            // Get dimensions from the importer/metadata rather than the Texture2D object
            int width = 0;
            int height = 0;
            GetTextureDimensions(importer, out width, out height);

            FileInfo fileInfo = new FileInfo(path);

            TextureData data = new TextureData
            {
                Name = Path.GetFileNameWithoutExtension(path),
                AssetPath = path,
                Usage = DetermineUsage(importer, path),
                FileSizeMB = (float)fileInfo.Length / (1024 * 1024),
                Dimensions = $"{width}x{height}",
                Format = Path.GetExtension(path).Replace(".", "").ToUpper(),
                Mipmaps = importer.mipmapEnabled,
                IsPoT = IsPowerOfTwo(width) && IsPowerOfTwo(height)
            };

            textureList.Add(data);
            
            // Progress Bar
            count++;
            EditorUtility.DisplayProgressBar("Scanning Textures", $"Processing {data.Name}", (float)count / guids.Length);
        }

        textureList.Sort((a, b) => b.FileSizeMB.CompareTo(a.FileSizeMB));
        EditorUtility.ClearProgressBar();
        UnityEngine.Debug.Log($"<color=green>Scan Complete!</color> Found {textureList.Count} textures.");
        this.Repaint(); // Force the window to refresh immediately
    }

    private string DetermineUsage(TextureImporter importer, string path)
    {
        // 1. Check Path Keywords
        string lowerPath = path.ToLower();
        if (lowerPath.Contains("vfx") || lowerPath.Contains("particle") || lowerPath.Contains("effect")) return "VFX";
        
        // 2. Check Importer Settings
        if (importer.textureType == TextureImporterType.Sprite || importer.textureType == TextureImporterType.GUI) return "2D";
        if (importer.textureShape == TextureImporterShape.TextureCube) return "3D (Sky)";
        
        return "3D";
    }

    // Helper to get dimensions without loading the texture
    private void GetTextureDimensions(TextureImporter importer, out int width, out int height)
    {
        var method = typeof(TextureImporter).GetMethod("GetWidthAndHeight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        object[] args = new object[] { 0, 0 };
        method.Invoke(importer, args);
        width = (int)args[0];
        height = (int)args[1];
    }

    private bool IsPowerOfTwo(int value)
    {
        return (value != 0) && (value & (value - 1)) == 0;
    }
}
*/
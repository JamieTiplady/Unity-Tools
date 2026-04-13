using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public class ProjectTextureQualityChecker : EditorWindow
{
    private List<TextureData> textureList = new List<TextureData>();
    private Vector2 scrollPosition;
    private GUIStyle redLabelStyle;
    private GUIStyle centredLabelStyle;
    private GUIStyle centredRedLabelStyle;

    private bool highlightSizeMismatch = false;

    private MethodInfo getDimensionsMethod;

    // --- Column layout ---
    // Each column has a label and a width. X offsets are computed at runtime from these.
    private static readonly string[] ColHeaders = { "Name", "Usage", "Size (MB)", "Dimensions", "Max Size", "Format", "Mips", "NPoT" };
    private static readonly float[]  ColWidths  = {  160f,    55f,      65f,          90f,          65f,       55f,    40f,    40f  };

    // Precomputed X positions — filled once in EnsureStyles
    private float[] colX;

    private class TextureData
    {
        public string Name;
        public string AssetPath;
        public string Usage;
        public float  FileSizeMB;
        public string Dimensions;
        public int    SourceWidth;
        public int    SourceHeight;
        public int    MaxSize;
        public string Format;
        public bool   Mipmaps;
        public bool   IsPoT;
    }

    [MenuItem("Tools/Project Texture Quality Checker")]
    public static void ShowWindow() => GetWindow<ProjectTextureQualityChecker>("Texture Checker");

    private void EnsureStyles()
    {
        if (redLabelStyle == null)
        {
            redLabelStyle = new GUIStyle(EditorStyles.label);
            redLabelStyle.normal.textColor = Color.red;
        }

        if (centredLabelStyle == null)
        {
            centredLabelStyle = new GUIStyle(EditorStyles.label);
            centredLabelStyle.alignment = TextAnchor.MiddleCenter;
        }

        if (centredRedLabelStyle == null)
        {
            centredRedLabelStyle = new GUIStyle(EditorStyles.label);
            centredRedLabelStyle.alignment = TextAnchor.MiddleCenter;
            centredRedLabelStyle.normal.textColor = Color.red;
        }

        // Build cumulative X offsets from widths
        if (colX == null)
        {
            colX = new float[ColWidths.Length];
            float x = 0f;
            for (int i = 0; i < ColWidths.Length; i++)
            {
                colX[i] = x;
                x += ColWidths[i];
            }
        }
    }

    private void OnGUI()
    {
        EnsureStyles();

        GUILayout.Label("Project Texture Quality Dashboard", EditorStyles.boldLabel);

        if (GUILayout.Button("Scan Project Textures", GUILayout.Height(30)))
            ScanTextures();

        EditorGUILayout.Space();

        highlightSizeMismatch = EditorGUILayout.ToggleLeft(
            "Highlight source dimensions exceeding import Max Size",
            highlightSizeMismatch
        );

        EditorGUILayout.Space();
        DrawHeader();

        const float rowHeight = 20f;

        // Reserve scroll space for the full list
        float totalHeight = textureList.Count * rowHeight;
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        Rect scrollContent = GUILayoutUtility.GetRect(0, totalHeight);

        if (Event.current.type == EventType.Repaint || Event.current.type == EventType.MouseDown)
        {
            int firstIndex   = Mathf.Max(0, (int)(scrollPosition.y / rowHeight));
            int visibleCount = Mathf.CeilToInt(position.height / rowHeight) + 2;
            int lastIndex    = Mathf.Min(firstIndex + visibleCount, textureList.Count);

            for (int i = firstIndex; i < lastIndex; i++)
            {
                Rect rowRect = new Rect(scrollContent.x, scrollContent.y + i * rowHeight, scrollContent.width, rowHeight);
                DrawRow(rowRect, textureList[i]);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        // Manually draw header cells using the same colX/ColWidths so they're pixel-perfect
        Rect headerRect = GUILayoutUtility.GetRect(0, 20f, GUILayout.ExpandWidth(true));

        // Toolbar background
        if (Event.current.type == EventType.Repaint)
            EditorStyles.toolbar.Draw(headerRect, false, false, false, false);

        for (int i = 0; i < ColHeaders.Length; i++)
        {
            Rect cell = new Rect(headerRect.x + colX[i], headerRect.y, ColWidths[i], headerRect.height);
            // Name header left-aligned; all others centred
            GUIStyle style = (i == 0) ? EditorStyles.boldLabel : centredLabelStyle;
            GUI.Label(cell, ColHeaders[i], style);
        }
    }

    private void DrawRow(Rect rowRect, TextureData data)
    {
        if (Event.current.type == EventType.Repaint)
        {
            // Faint separator line at the top of the row
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.08f);
            GUI.DrawTexture(new Rect(rowRect.x, rowRect.y, rowRect.width, 1f), EditorGUIUtility.whiteTexture);
            GUI.color = prev;

            // Hover highlight
            if (rowRect.Contains(Event.current.mousePosition))
                GUI.Box(rowRect, GUIContent.none);
        }

        if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && rowRect.Contains(Event.current.mousePosition))
        {
            Object obj = AssetDatabase.LoadMainAssetAtPath(data.AssetPath);
            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
            Event.current.Use();
        }

        // Helper to get a cell Rect for column index i
        Rect Cell(int i) => new Rect(rowRect.x + colX[i], rowRect.y, ColWidths[i], rowRect.height);

        // Col 0 — Name, left-aligned
        GUI.Label(Cell(0), data.Name, EditorStyles.label);

        // Col 1 — Usage
        GUI.Label(Cell(1), data.Usage, centredLabelStyle);

        // Col 2 — Size (MB)
        GUI.Label(Cell(2), data.FileSizeMB.ToString("F2"), centredLabelStyle);

        // Col 3 — Dimensions
        GUI.Label(Cell(3), data.Dimensions, centredLabelStyle);

        // Col 4 — Max Size, conditionally red
        bool isMismatch = data.SourceWidth > data.MaxSize || data.SourceHeight > data.MaxSize;
        GUIStyle maxSizeStyle = (highlightSizeMismatch && isMismatch) ? centredRedLabelStyle : centredLabelStyle;
        GUI.Label(Cell(4), data.MaxSize.ToString(), maxSizeStyle);

        // Col 5 — Format
        GUI.Label(Cell(5), data.Format, centredLabelStyle);

        // Col 6 — Mips
        GUI.Label(Cell(6), data.Mipmaps ? "Yes" : "No", centredLabelStyle);

        // Col 7 — NPoT, independent red logic
        GUI.Label(Cell(7), data.IsPoT ? "Yes" : "No", data.IsPoT ? centredLabelStyle : centredRedLabelStyle);
    }

    private void ScanTextures()
    {
        textureList.Clear();
        string[] guids = AssetDatabase.FindAssets("t:Texture");

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
            width  = (int)args[0];
            height = (int)args[1];

            FileInfo fileInfo = new FileInfo(path);

            textureList.Add(new TextureData
            {
                Name         = Path.GetFileNameWithoutExtension(path),
                AssetPath    = path,
                Usage        = DetermineUsage(importer, path),
                FileSizeMB   = (float)fileInfo.Length / 1048576f,
                Dimensions   = $"{width}x{height}",
                SourceWidth  = width,
                SourceHeight = height,
                MaxSize      = importer.maxTextureSize,
                Format       = Path.GetExtension(path).Replace(".", "").ToUpper(),
                Mipmaps      = importer.mipmapEnabled,
                IsPoT        = (width != 0 && (width & (width - 1)) == 0) && (height != 0 && (height & (height - 1)) == 0)
            });

            if (i % 20 == 0)
                EditorUtility.DisplayProgressBar("Scanning", "Checking Texture Quality...", (float)i / guids.Length);
        }

        textureList.Sort((a, b) => b.FileSizeMB.CompareTo(a.FileSizeMB));
        EditorUtility.ClearProgressBar();
        UnityEngine.Debug.Log($"<color=green>Scan Complete!</color> Found {textureList.Count} textures.");
        Repaint();
    }

    private string DetermineUsage(TextureImporter importer, string path)
    {
        string lowerPath = path.ToLower();
        if (lowerPath.Contains("vfx") || lowerPath.Contains("particle") || lowerPath.Contains("effect")) return "VFX";
        if (importer.textureType == TextureImporterType.Sprite || importer.textureType == TextureImporterType.GUI) return "2D";
        return (importer.textureShape == TextureImporterShape.TextureCube) ? "3D (Sky)" : "3D";
    }
}





///////////////////////////////////////////////////////////////



/*

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public class ProjectTextureQualityChecker : EditorWindow
{
    private List<TextureData> textureList = new List<TextureData>();
    private Vector2 scrollPosition;
    private GUIStyle redLabelStyle;

    // Toggle: when true, highlights Max Size column red if source exceeds import max size
    private bool highlightSizeMismatch = false;

    // Store the reflection method here so we only find it once
    private MethodInfo getDimensionsMethod;

    private class TextureData
    {
        public string Name;
        public string AssetPath;
        public string Usage;
        public float FileSizeMB;
        public string Dimensions;
        public int SourceWidth;  // Stored separately for clean comparison
        public int SourceHeight;
        public int MaxSize;
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

        // --- Toggle above the table ---
        highlightSizeMismatch = EditorGUILayout.ToggleLeft(
            "Highlight source dimensions exceeding import Max Size",
            highlightSizeMismatch
        );

        EditorGUILayout.Space();
        DrawHeader();

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        // UI culling for speed
        float rowHeight = 20f;
        int firstIndex = (int)(scrollPosition.y / rowHeight);
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
        GUILayout.Label("Size (MB)", GUILayout.Width(60));
        GUILayout.Label("Dimensions", GUILayout.Width(80));
        GUILayout.Label("Max Size", GUILayout.Width(65));
        GUILayout.Label("Format", GUILayout.Width(80));
        GUILayout.Label("Mips", GUILayout.Width(40));
        GUILayout.Label("NPoT", GUILayout.Width(40));
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
        GUILayout.Label(data.FileSizeMB.ToString("F2"), GUILayout.Width(60));
        GUILayout.Label(data.Dimensions, GUILayout.Width(80));

        // Max Size column: only highlight red when the toggle is on AND source exceeds the import max size.
        // This means Unity is being forced to downsample the texture, so the source file has wasted resolution.
        bool isSizeMismatch = data.SourceWidth > data.MaxSize || data.SourceHeight > data.MaxSize;
        GUIStyle maxSizeStyle = (highlightSizeMismatch && isSizeMismatch) ? redLabelStyle : EditorStyles.label;
        GUILayout.Label(data.MaxSize.ToString(), maxSizeStyle, GUILayout.Width(65));

        GUILayout.Label(data.Format, GUILayout.Width(80));
        GUILayout.Label(data.Mipmaps ? "Yes" : "No", GUILayout.Width(40));

        // NPoT column: always uses its own independent red logic, unaffected by the toggle above
        GUILayout.Label(data.IsPoT ? "Yes" : "No", data.IsPoT ? EditorStyles.label : redLabelStyle, GUILayout.Width(40));

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
                FileSizeMB = (float)fileInfo.Length / 1048576f,
                Dimensions = $"{width}x{height}",
                SourceWidth = width,   // Store raw values for comparison
                SourceHeight = height,
                MaxSize = importer.maxTextureSize,
                Format = Path.GetExtension(path).Replace(".", "").ToUpper(),
                Mipmaps = importer.mipmapEnabled,
                IsPoT = (width != 0 && (width & (width - 1)) == 0) && (height != 0 && (height & (height - 1)) == 0)
            });

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

*/


///////////////////////////////////////////////////////////////////////////////////


/*
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

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
        public int MaxSize; // <-- NEW: Stores the import max size
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
        
        // UI culling for speed
        float rowHeight = 20f; 
        int firstIndex = (int)(scrollPosition.y / rowHeight);
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
        GUILayout.Label("Size (MB)", GUILayout.Width(60));
        GUILayout.Label("Dimensions", GUILayout.Width(80));
        GUILayout.Label("Max Size", GUILayout.Width(65)); // <-- NEW Column
        GUILayout.Label("Format", GUILayout.Width(80));
        GUILayout.Label("Mips", GUILayout.Width(40));
        GUILayout.Label("NPoT", GUILayout.Width(40));
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
        GUILayout.Label(data.FileSizeMB.ToString("F2"), GUILayout.Width(60));
        GUILayout.Label(data.Dimensions, GUILayout.Width(80));
        
        // Highlight in red if the file dimensions are larger than the max allowed size!
        GUIStyle maxSizeStyle = EditorStyles.label;
        int actualMaxWidth = int.Parse(data.Dimensions.Split('x')[0]); 
        if (actualMaxWidth > data.MaxSize) maxSizeStyle = redLabelStyle;
        
        GUILayout.Label(data.MaxSize.ToString(), maxSizeStyle, GUILayout.Width(65)); // <-- NEW Data
        
        GUILayout.Label(data.Format, GUILayout.Width(80));
        GUILayout.Label(data.Mipmaps ? "Yes" : "No", GUILayout.Width(40));
        GUILayout.Label(data.IsPoT ? "Yes" : "No", data.IsPoT ? EditorStyles.label : redLabelStyle, GUILayout.Width(40));
        
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
                FileSizeMB = (float)fileInfo.Length / 1048576f,
                Dimensions = $"{width}x{height}",
                MaxSize = importer.maxTextureSize, // <-- NEW: Grabbing the Max Size
                Format = Path.GetExtension(path).Replace(".", "").ToUpper(),
                Mipmaps = importer.mipmapEnabled,
                IsPoT = (width != 0 && (width & (width - 1)) == 0) && (height != 0 && (height & (height - 1)) == 0)
            });
            
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

*/





////////////////////////////////////////////////


/*


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

*/
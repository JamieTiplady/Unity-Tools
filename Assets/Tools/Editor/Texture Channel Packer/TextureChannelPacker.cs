using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

public class TextureChannelPacker : EditorWindow
{
    private int tab = 0;

    // Manual Mode Variables
    private Texture2D texR, texG, texB, texA;
    private bool invR, invG, invB, invA;
    private bool useMetallicAlpha = false; // <--- NEW TOGGLE
    private string fileName = "T_PackedTexture_Mask";

    // Batch Mode Variables
    private DefaultAsset targetFolder;
    
    private readonly string[] suffixR = { "_Metallic", "_Metal", "_M" };
    private readonly string[] suffixG = { "_Smoothness", "_Smooth", "_S", "_Roughness", "_Rough", "_R" };
    private readonly string[] suffixB = { "_AO", "_AmbientOcclusion", "_Occlusion" };
    private readonly string[] suffixA = { "_Emission", "_Emissive", "_Mask", "_E" };

    [MenuItem("Tools/Texture Channel Packer")]
    public static void ShowWindow() => GetWindow<TextureChannelPacker>("Channel Packer");

    private void OnGUI()
    {
        GUILayout.Label("Texture Channel Packer (Color32)", EditorStyles.boldLabel);
        
        tab = GUILayout.Toolbar(tab, new string[] { "Manual Mode", "Batch Folder Mode" });
        EditorGUILayout.Space();

        if (tab == 0) DrawManualMode();
        else DrawBatchMode();
    }

    private void DrawManualMode()
    {
        EditorGUILayout.HelpBox("Pack grayscale textures manually. Smaller textures will be automatically upscaled to match the largest input.", MessageType.Info);
        EditorGUILayout.Space();

        DrawChannelSlot("Red Channel - Metallic", ref texR, ref invR);

        // --- CUSTOM GREEN CHANNEL UI ---
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        
        // Disable the input field if the toggle is true
        EditorGUI.BeginDisabledGroup(useMetallicAlpha);
        texG = (Texture2D)EditorGUILayout.ObjectField("Green Channel - Smoothness", texG, typeof(Texture2D), false);
        EditorGUI.EndDisabledGroup();

        invertGLogic: 
        invG = EditorGUILayout.ToggleLeft("Invert", invG, GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();

        // The Toggle for Metallic Alpha
        bool lastToggle = useMetallicAlpha;
        useMetallicAlpha = EditorGUILayout.ToggleLeft("Use Metallic Alpha as Smoothness", useMetallicAlpha);
        
        // If they just turned it on, clear the green texture slot so it's not confusing
        if (useMetallicAlpha && !lastToggle) texG = null;

        if (texG != null) DrawSRGBWarning(texG);
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
        // ---------------------------------

        DrawChannelSlot("Blue Channel - AO", ref texB, ref invB);
        DrawChannelSlot("Alpha Channel - Emission Mask", ref texA, ref invA);

        EditorGUILayout.Space();
        fileName = EditorGUILayout.TextField("Output Name", fileName);

        if (GUILayout.Button("Pack and Save Texture", GUILayout.Height(40)))
        {
            Pack(texR, texG, texB, texA, fileName);
        }
    }

    private void DrawBatchMode()
    {
        EditorGUILayout.HelpBox("Batch mode will now also respect the 'Metallic Alpha' toggle if checked.", MessageType.Info);
        EditorGUILayout.Space();

        targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("Target Folder", targetFolder, typeof(DefaultAsset), false);

        EditorGUILayout.Space();
        GUILayout.Label("Batch Settings", EditorStyles.boldLabel);
        useMetallicAlpha = EditorGUILayout.ToggleLeft("Use Metallic Alpha as Smoothness", useMetallicAlpha);
        EditorGUILayout.Space();
        
        invR = EditorGUILayout.Toggle("Invert Red (Metallic)", invR);
        invG = EditorGUILayout.Toggle("Invert Green (Smoothness)", invG);
        invB = EditorGUILayout.Toggle("Invert Blue (AO)", invB);
        invA = EditorGUILayout.Toggle("Invert Alpha (Emission)", invA);
        EditorGUILayout.Space();

        if (GUILayout.Button("Batch Pack Folder", GUILayout.Height(40)))
        {
            RunBatchPack();
        }
    }

    private void RunBatchPack()
    {
        if (targetFolder == null) return;

        string folderPath = AssetDatabase.GetAssetPath(targetFolder);
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath });
        List<Texture2D> folderTextures = new List<Texture2D>();

        foreach (string guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            folderTextures.Add(AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath));
        }

        Texture2D bR = folderTextures.FirstOrDefault(t => suffixR.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));
        // Only look for a green texture if we aren't using the Red Alpha
        Texture2D bG = useMetallicAlpha ? null : folderTextures.FirstOrDefault(t => suffixG.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));
        Texture2D bB = folderTextures.FirstOrDefault(t => suffixB.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));
        Texture2D bA = folderTextures.FirstOrDefault(t => suffixA.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));

        string autoName = "T_PackedTexture_" + targetFolder.name;
        Pack(bR, bG, bB, bA, autoName);
    }

    private void DrawChannelSlot(string label, ref Texture2D tex, ref bool invert)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        tex = (Texture2D)EditorGUILayout.ObjectField(label, tex, typeof(Texture2D), false);
        invert = EditorGUILayout.ToggleLeft("Invert", invert, GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();

        if (tex != null) DrawSRGBWarning(tex);
        
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
    }

    private void DrawSRGBWarning(Texture2D tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;

        if (importer != null && importer.sRGBTexture)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(20));
            GUI.color = Color.yellow;
            GUILayout.Label("sRGB Enabled: Data will be inaccurate!", EditorStyles.miniLabel);
            GUI.color = Color.white;

            if (GUILayout.Button("Fix", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                importer.sRGBTexture = false;
                importer.SaveAndReimport();
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    private void Pack(Texture2D rTex, Texture2D gTex, Texture2D bTex, Texture2D aTex, string outputName)
    {
        int targetW = 0, targetH = 0;
        Texture2D[] inputs = { rTex, gTex, bTex, aTex };

        foreach (var t in inputs)
        {
            if (t != null) { targetW = Mathf.Max(targetW, t.width); targetH = Mathf.Max(targetH, t.height); }
        }

        if (targetW == 0) return;

        Texture2D packedTex = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
        Color32[] packedPixels = new Color32[targetW * targetH];

        Color32[] rPixels = GetPixelsResized(rTex, targetW, targetH);
        Color32[] gPixels = GetPixelsResized(gTex, targetW, targetH);
        Color32[] bPixels = GetPixelsResized(bTex, targetW, targetH);
        Color32[] aPixels = GetPixelsResized(aTex, targetW, targetH);

        for (int i = 0; i < packedPixels.Length; i++)
        {
            byte r = GetByte(rPixels, i, 0, invR);
            byte b = GetByte(bPixels, i, 0, invB);
            byte a = GetByte(aPixels, i, 255, invA);

            // --- SMOOTHNESS LOGIC ---
            byte g;
            if (useMetallicAlpha && rPixels != null)
            {
                // Grab the ALPHA from the Red (Metallic) pixels
                g = rPixels[i].a;
                if (invG) g = (byte)(255 - g);
            }
            else
            {
                g = GetByte(gPixels, i, 0, invG);
            }

            packedPixels[i] = new Color32(r, g, b, a);
        }

        packedTex.SetPixels32(packedPixels);
        packedTex.Apply();
        File.WriteAllBytes("Assets/" + outputName + ".png", packedTex.EncodeToPNG());
        AssetDatabase.Refresh();

        DestroyImmediate(packedTex);
        EditorUtility.DisplayDialog("Success", "Texture saved to Assets folder.", "OK");
    }

    private Color32[] GetPixelsResized(Texture2D tex, int width, int height)
    {
        if (tex == null) return null;
        EnsureReadable(tex);
        if (tex.width == width && tex.height == height) return tex.GetPixels32();

        RenderTexture rt = RenderTexture.GetTemporary(width, height);
        Graphics.Blit(tex, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tempTex = new Texture2D(width, height);
        tempTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tempTex.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] pixels = tempTex.GetPixels32();
        DestroyImmediate(tempTex);
        return pixels;
    }

    private byte GetByte(Color32[] pixels, int index, byte defaultValue, bool invert)
    {
        if (pixels == null) return defaultValue;
        byte val = pixels[index].r;
        return invert ? (byte)(255 - val) : val;
    }

    private void EnsureReadable(Texture2D tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
    }
}
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

public class TextureChannelPacker : EditorWindow
{
    // Mode Switching
    private int tab = 0;

    // Manual Mode Variables
    private Texture2D texR, texG, texB, texA;
    private bool invR, invG, invB, invA;
    private string fileName = "T_PackedTexture_Mask";

    // Batch Mode Variables
    private DefaultAsset targetFolder;
    
    //Suffixes - Customize these if your naming conventions change
    private readonly string[] suffixR = { "_Metallic", "_Metal", "_M" };
    private readonly string[] suffixG = { "_Smoothness", "_Smooth", "_S", "_Roughness", "_Rough", "_R" };
    private readonly string[] suffixB = { "_AO", "_AmbientOcclusion", "_Occlusion" };
    private readonly string[] suffixA = { "_Emission", "_Emissive", "_Mask", "_E" };

    [MenuItem("Tools/Texture Channel Packer")]
    public static void ShowWindow() => GetWindow<TextureChannelPacker>("Channel Packer");

    private void OnGUI()
    {
        GUILayout.Label("Texture Channel Packer (Color32)", EditorStyles.boldLabel);
        
        // Tab system for switching modes
        tab = GUILayout.Toolbar(tab, new string[] { "Manual Mode", "Batch Folder Mode" });
        EditorGUILayout.Space();

        if (tab == 0)
        {
            DrawManualMode();
        }
        else
        {
            DrawBatchMode();
        }
    }

    private void DrawManualMode()
    {
        EditorGUILayout.HelpBox("Pack grayscale textures manually. Smaller textures will be automatically upscaled to match the largest input.", MessageType.Info);
        EditorGUILayout.Space();

        //Change titles dependant on project packing preferences
        DrawChannelSlot("Red Channel - Metallic", ref texR, ref invR);
        DrawChannelSlot("Green Channel - Smoothness", ref texG, ref invG);
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
        EditorGUILayout.HelpBox("Select a folder. The script will find textures by their suffixes (e.g. _Metallic, _AO) and pack them automatically.", MessageType.Info);
        EditorGUILayout.Space();

        targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("Target Folder", targetFolder, typeof(DefaultAsset), false);

        if (targetFolder != null)
        {
            string path = AssetDatabase.GetAssetPath(targetFolder);
            if (!AssetDatabase.IsValidFolder(path))
            {
                EditorGUILayout.HelpBox("Please select a valid folder, not a file.", MessageType.Error);
                return;
            }
        }

        EditorGUILayout.Space();
        GUILayout.Label("Batch Inversion Overrides", EditorStyles.boldLabel);
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
        if (targetFolder == null)
        {
            EditorUtility.DisplayDialog("Error", "Please assign a Target Folder first.", "OK");
            return;
        }

        string folderPath = AssetDatabase.GetAssetPath(targetFolder);
        
        // Find all texture GUIDs in the target folder
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath });
        List<Texture2D> folderTextures = new List<Texture2D>();

        foreach (string guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            folderTextures.Add(AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath));
        }

        // Match textures based on suffixes using LINQ
        Texture2D bR = folderTextures.FirstOrDefault(t => suffixR.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));
        Texture2D bG = folderTextures.FirstOrDefault(t => suffixG.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));
        Texture2D bB = folderTextures.FirstOrDefault(t => suffixB.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));
        Texture2D bA = folderTextures.FirstOrDefault(t => suffixA.Any(s => t.name.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)));

        if (bR == null && bG == null && bB == null && bA == null)
        {
            EditorUtility.DisplayDialog("Batch Error", "No textures with matching suffixes (_Metallic, _AO, etc.) found in the selected folder.", "OK");
            return;
        }

        // Auto-generate name based on folder
        string autoName = "T_PackedTexture_" + targetFolder.name;
        
        // Run the packing logic
        Pack(bR, bG, bB, bA, autoName);
    }

    private void DrawChannelSlot(string label, ref Texture2D tex, ref bool invert)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        tex = (Texture2D)EditorGUILayout.ObjectField(label, tex, typeof(Texture2D), false);
        invert = EditorGUILayout.ToggleLeft("Invert", invert, GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();

        // --- sRGB Safety Warning ---
        if (tex != null)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;

            if (importer != null && importer.sRGBTexture)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(20));
                
                GUI.color = Color.yellow;
                GUILayout.Label("sRGB Enabled: Mask math will be inaccurate!", EditorStyles.miniLabel);
                GUI.color = Color.white;

                if (GUILayout.Button("Fix Import Settings", EditorStyles.miniButton, GUILayout.Width(120)))
                {
                    importer.sRGBTexture = false;
                    importer.SaveAndReimport();
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
    }

    // Refactored to accept parameters so both modes can use it
    private void Pack(Texture2D rTex, Texture2D gTex, Texture2D bTex, Texture2D aTex, string outputName)
    {
        // 1. Determine the largest dimensions among all provided textures
        int targetW = 0;
        int targetH = 0;
        Texture2D[] inputs = { rTex, gTex, bTex, aTex };

        foreach (var t in inputs)
        {
            if (t != null)
            {
                targetW = Mathf.Max(targetW, t.width);
                targetH = Mathf.Max(targetH, t.height);
            }
        }

        if (targetW == 0)
        {
            EditorUtility.DisplayDialog("Error", "Please provide at least one texture.", "OK");
            return;
        }

        // 2. Prepare the new texture
        Texture2D packedTex = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
        Color32[] packedPixels = new Color32[targetW * targetH];

        // 3. Get Pixel Arrays (Resized automatically if they don't match targetW/H)
        Color32[] rPixels = GetPixelsResized(rTex, targetW, targetH);
        Color32[] gPixels = GetPixelsResized(gTex, targetW, targetH);
        Color32[] bPixels = GetPixelsResized(bTex, targetW, targetH);
        Color32[] aPixels = GetPixelsResized(aTex, targetW, targetH);

        // 4. The Packing Loop
        for (int i = 0; i < packedPixels.Length; i++)
        {
            byte r = GetByte(rPixels, i, 0, invR);
            byte g = GetByte(gPixels, i, 0, invG);
            byte b = GetByte(bPixels, i, 0, invB);
            byte a = GetByte(aPixels, i, 255, invA);

            packedPixels[i] = new Color32(r, g, b, a);
        }

        packedTex.SetPixels32(packedPixels);
        packedTex.Apply();

        byte[] bytes = packedTex.EncodeToPNG();

        //////
        /// 
        /// Set export path here, edit "Assets/" to desired folder
        /// 
        //////
        
        string path = "Assets/" + outputName + ".png";
        File.WriteAllBytes(path, bytes);
        AssetDatabase.ImportAsset(path);

        DestroyImmediate(packedTex);
        EditorUtility.DisplayDialog("Success", $"Texture saved at {targetW}x{targetH} to: {path}", "OK");
    }

    // Handles resizing via GPU (Blit) to avoid IndexOutOfRange errors
    private Color32[] GetPixelsResized(Texture2D tex, int width, int height)
    {
        if (tex == null) return null;

        // If it's already the right size, just grab the pixels normally
        if (tex.width == width && tex.height == height)
        {
            EnsureReadable(tex);
            return tex.GetPixels32();
        }

        // If mismatched, use RenderTexture to upscale/downscale smoothly
        //this scales up smaller textures to larger sizes to keep consistency in packed texture
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
        if (tex == null) return;
        string path = AssetDatabase.GetAssetPath(tex);
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
    }
}
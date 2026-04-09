/// <summary>
/// Goes through all textures to find any that aren't power of two resolution, adds NPOT_ to the beginning of the name.
/// </summary>

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;

[ExecuteInEditMode]
public class NonPowerOfTwoDetector : MonoBehaviour
{
    private const string NPOT_PREFIX = "NPOT_";

    [SerializeField]
    private bool Run = false;

    private void Update()
    {
        if (true == Run)
        {
            Run = false;
            ScanForNPoTTextures();
        }
    }

    public static bool IsPowerOfTwo(int value)
    {
       return (value != 0) && (value & (value - 1)) == 0; 
    }

    [MenuItem("Tools/Scan For NPoT Textures")]
    public static void ScanForNPoTTextures()
    {
        string[] guids = AssetDatabase.FindAssets("t:Texture");
        Texture TextureHolder;
        string pathHolder;
        int NPoTCounter = 0;

        foreach (string textureID in guids)
        {
            TextureHolder = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(textureID));

            if (IsPowerOfTwo(TextureHolder.width) == true && IsPowerOfTwo(TextureHolder.height) == true)
            {
                if (TextureHolder.name.StartsWith(NPOT_PREFIX) == true)
                {
                    pathHolder = AssetDatabase.GUIDToAssetPath(textureID);
                    AssetDatabase.RenameAsset(pathHolder, TextureHolder.name.Remove(0, NPOT_PREFIX.Length));
                }
            }
            else
            {
                if (TextureHolder.name.StartsWith(NPOT_PREFIX) == false)
                {
                    pathHolder = AssetDatabase.GUIDToAssetPath(textureID);
                    AssetDatabase.RenameAsset(pathHolder, NPOT_PREFIX + TextureHolder.name);
                }

                NPoTCounter++;
            }
        }

        AssetDatabase.SaveAssets();

        Debug.LogError($"NonPowerOfTwoDetector found {NPoTCounter} non power of two textures from a total of {guids.Length + 1}");
    }
}
#endif
using UnityEngine;
using UnityEditor;

public class ImportAutoStatic : AssetPostprocessor
{
    void OnPostprocessModel(GameObject g)
    {
        //checks if 3D model (fbx, obj, etc), if it is, do the function, if not, just go back
        if (g == null)
            return;

        ProcessRecursive(g);
    }

    private void ProcessRecursive(GameObject obj)
    {
        //object has to contain string 
        string matchString = "_ST_";

        bool shouldBeStatic = obj.name.Contains(matchString);

        if (obj.isStatic != shouldBeStatic)
        {
            obj.isStatic = shouldBeStatic;
        }

        //check through all children
        foreach (Transform child in obj.transform)
        {
            ProcessRecursive(child.gameObject);
        }
    }
}
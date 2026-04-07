using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using System.Collections.Generic;

[ExecuteAlways]
public class SplineChainGenerator : MonoBehaviour
{
    public SplineContainer splineContainer;
    public GameObject linkPrefab;

    [Header("Settings")]
    public float spacing = 1.0f;
    public Vector3 rotationOffset;
    public Vector3 scaleOffset = Vector3.one;
    public bool alternateRotation = true;
    
    [Header("Variation")]
    [Range(0, 180)]
    public float rotationVariation = 0f; // How much random wobble to add
    public int seed = 1234;              // Change this to get a new "look"

    // Use a list to track our specific links so we don't touch other objects
    [SerializeField, HideInInspector] 
    private List<GameObject> spawnedLinks = new List<GameObject>();

    private void OnEnable()
    {
        Spline.Changed += OnSplineChanged;
        Generate();
    }

    private void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;
    }

    private void OnValidate()
    {
        // Delay the call slightly to ensure Unity's UI has finished updating
        UnityEditor.EditorApplication.delayCall += () => {
            if (this != null) Generate();
        };
    }

    private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
    {
        if (splineContainer != null && spline == splineContainer.Spline)
        {
            Generate();
        }
    }

    public void Generate()
    {
        if (splineContainer == null || linkPrefab == null) return;

        var spline = splineContainer.Spline;
        float totalLength = spline.GetLength();
        if (spacing <= 0.1f) spacing = 0.1f;

        int requiredCount = Mathf.FloorToInt(totalLength / spacing);

        // 1. Cleanup: Remove nulls from the list (if user deleted them manually)
        spawnedLinks.RemoveAll(item => item == null);

        // 2. Adjust Pool Size: Only create what is missing
        while (spawnedLinks.Count < requiredCount)
        {
            GameObject newLink = Instantiate(linkPrefab, transform);
            newLink.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            spawnedLinks.Add(newLink);
        }

        // 3. Position and Show/Hide
        for (int i = 0; i < spawnedLinks.Count; i++)
        {
            if (i < requiredCount)
            {
                spawnedLinks[i].SetActive(true);
                
                float distance = i * spacing;
                float t = distance / totalLength;
                spline.Evaluate(t, out float3 pos, out float3 forward, out float3 up);

                spawnedLinks[i].transform.localPosition = (Vector3)pos;
                Quaternion splineRotation = Quaternion.LookRotation((Vector3)forward, (Vector3)up);
                spawnedLinks[i].transform.rotation = splineRotation * Quaternion.Euler(rotationOffset);
                spawnedLinks[i].transform.localScale = scaleOffset;

                if (alternateRotation && i % 2 == 0)
                {
                    spawnedLinks[i].transform.Rotate(Vector3.forward, 90f);
                }

                // 1. Initialize the random state with the seed + the index 
                UnityEngine.Random.InitState(seed + i);

                // 2. Calculate a random offset for each axis
                float randomX = UnityEngine.Random.Range(-rotationVariation, rotationVariation);
                float randomY = UnityEngine.Random.Range(-rotationVariation, rotationVariation);
                float randomZ = UnityEngine.Random.Range(-rotationVariation, rotationVariation);

                // 3. Apply it to the existing rotation on Z only
                spawnedLinks[i].transform.Rotate(new Vector3(0, 0, randomZ));
            }

            else
            {
                // Disable extra links instead of destroying them (pooling)
                spawnedLinks[i].SetActive(false);
            }
        }
    }

    // Optional: Context menu to really wipe it clean if things get weird
    [ContextMenu("Force Clear All")]
    private void ForceClear()
    {
        foreach (var link in spawnedLinks)
        {
            if (link != null) DestroyImmediate(link);
        }
        spawnedLinks.Clear();
        // Also kill any lost children
        var children = new List<GameObject>();
        foreach (Transform child in transform) children.Add(child.gameObject);
        children.ForEach(c => DestroyImmediate(c));
    }
}
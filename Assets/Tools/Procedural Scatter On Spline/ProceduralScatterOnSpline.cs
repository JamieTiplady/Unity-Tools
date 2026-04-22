using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;

[ExecuteInEditMode]
public class ProceduralScatterOnSpline : MonoBehaviour
{
    public enum RotationMode { FollowSpline, WorldSpace, RandomFull }

    [ContextMenu("Force Scatter")]
    public void ManualScatter() => Scatter();

    [System.Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        public bool markAsStatic = false;
        [Range(1, 1000)] public int count = 20;
        
        [Header("Lateral Distribution")]
        public float startDistance = 0.5f; // New Variable
        public float lateralRange = 5.0f;
        public bool scatterLeft = true;
        public bool scatterRight = true;
        
        [Space(5)]
        public bool useLateralCurve = false;
        [Tooltip("X (Time) = Distance from startDistance to lateralRange (0 to 1). Y (Value) = Probability of spawning there (0 to 1)")]
        public AnimationCurve lateralCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        [Header("Collision Handling")]
        public bool checkOverlap = false;
        public float detectionRadius = 0.1f; 
        public LayerMask overlapLayer = ~0; 

        [Header("Rotation")]
        public RotationMode rotationMode = RotationMode.FollowSpline;
        public Vector3 minRotationOffset;
        public Vector3 maxRotationOffset = new Vector3(0, 360, 0);

        [Header("Scale")]
        public Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        public int seedOffset = 0;
    }

    public SplineContainer splineContainer;
    public List<ScatterSettings> scatterGroups = new List<ScatterSettings>();
    public int globalSeed = 12345;

    private Transform _internalHolder;

    private void OnValidate()
    {
        if (splineContainer == null || scatterGroups.Count == 0) return;
        UnityEditor.EditorApplication.delayCall -= Scatter;
        UnityEditor.EditorApplication.delayCall += Scatter;
    }

    public void Scatter()
    {
        if (this == null || splineContainer == null) return;

        EnsureHolderExists();
        ClearGeneratedObjects();

        var spline = splineContainer.Spline;
        List<(GameObject instance, ScatterSettings settings)> spawnedItems = new List<(GameObject, ScatterSettings)>();

        foreach (var group in scatterGroups)
        {
            if (group.prefab == null) continue;
            Random.InitState(globalSeed + group.seedOffset);

            for (int i = 0; i < group.count; i++)
            {
                float t = Random.value;
                spline.Evaluate(t, out float3 localPos, out float3 forward, out float3 up);
                
                float3 right = math.cross(forward, up);
                right = math.normalize(right);

                float sideDirection = 0;
                float magnitude = 0;

                // 1. Determine Direction
                if (group.scatterLeft && group.scatterRight) 
                    sideDirection = Random.value > 0.5f ? 1f : -1f; 
                else if (group.scatterLeft) 
                    sideDirection = -1f;
                else if (group.scatterRight) 
                    sideDirection = 1f;

                // 2. Determine Magnitude (0 to 1)
                if (group.useLateralCurve)
                {
                    int safetyNet = 0;
                    while (safetyNet < 100)
                    {
                        float randomDist = Random.value; 
                        float randomChance = Random.value; 
                        
                        if (randomChance <= group.lateralCurve.Evaluate(randomDist))
                        {
                            magnitude = randomDist;
                            break;
                        }
                        safetyNet++;
                    }
                }
                else
                {
                    magnitude = Random.value;
                }

                // 3. Calculate Final Position using startDistance as the floor
                float finalOffsetDist = group.startDistance + (magnitude * (group.lateralRange - group.startDistance));
                float3 localOffsetPos = localPos + (right * sideDirection * finalOffsetDist);
                
                GameObject newInstance = PlaceObject(group, localOffsetPos, forward, up);
                if (newInstance != null) spawnedItems.Add((newInstance, group));
            }
        }

        Physics.SyncTransforms();

        // --- PASS 2: SELECTIVE CULLING ---
        HashSet<GameObject> toDestroy = new HashSet<GameObject>();

        foreach (var item in spawnedItems)
        {
            if (item.instance == null || toDestroy.Contains(item.instance)) continue;

            if (item.settings.checkOverlap)
            {
                Collider[] hits = Physics.OverlapSphere(item.instance.transform.position, item.settings.detectionRadius, item.settings.overlapLayer);

                foreach (Collider hit in hits)
                {
                    if (hit.transform != item.instance.transform && !hit.transform.IsChildOf(item.instance.transform))
                    {
                        if (toDestroy.Contains(hit.gameObject)) continue;
                        
                        bool hitIsMarked = false;
                        foreach(var marked in toDestroy) {
                            if (hit.transform.IsChildOf(marked.transform)) { hitIsMarked = true; break; }
                        }
                        if (hitIsMarked) continue;

                        toDestroy.Add(item.instance);
                        break; 
                    }
                }
            }
        }

        foreach (GameObject deadObj in toDestroy)
        {
            if (deadObj != null) DestroyImmediate(deadObj);
        }
    }

    private GameObject PlaceObject(ScatterSettings settings, float3 localPos, float3 forward, float3 up)
    {
        GameObject instance = Instantiate(settings.prefab, _internalHolder);
        instance.transform.localPosition = localPos;
        instance.isStatic = settings.markAsStatic;

        Quaternion baseRot = settings.rotationMode switch
        {
            RotationMode.FollowSpline => Quaternion.LookRotation(forward, up),
            RotationMode.RandomFull => Random.rotation,
            _ => Quaternion.identity
        };

        Vector3 randomEuler = new Vector3(
            Random.Range(settings.minRotationOffset.x, settings.maxRotationOffset.x),
            Random.Range(settings.minRotationOffset.y, settings.maxRotationOffset.y),
            Random.Range(settings.minRotationOffset.z, settings.maxRotationOffset.z)
        );

        instance.transform.localRotation = baseRot * Quaternion.Euler(randomEuler);
        float s = Random.Range(settings.scaleRange.x, settings.scaleRange.y);
        instance.transform.localScale = new Vector3(s, s, s);

        return instance; 
    }

    private void EnsureHolderExists()
    {
        Transform existing = transform.Find("Generated_Scatter");
        if (existing == null)
        {
            GameObject holder = new GameObject("Generated_Scatter");
            holder.transform.SetParent(this.transform);
            holder.transform.localPosition = Vector3.zero;
            _internalHolder = holder.transform;
        }
        else { _internalHolder = existing; }
    }

    private void ClearGeneratedObjects()
    {
        if (_internalHolder == null) return;
        for (int i = _internalHolder.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(_internalHolder.GetChild(i).gameObject);
        }
    }
}
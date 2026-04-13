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
        public float lateralRange = 5.0f;
        public bool scatterLeft = true;
        public bool scatterRight = true;
        
        [Space(5)]
        public bool useLateralCurve = false;
        [Tooltip("X (Time) = Distance from Spline (0 to 1). Y (Value) = Probability of spawning there (0 to 1)")]
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

        // --- PASS 1: SPAWN EVERYTHING ---
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

                float sideMultiplier = 0;

                if (group.useLateralCurve)
                {
                    float magnitude = 0f;
                    
                    // --- REJECTION SAMPLING FOR PROBABILITY CURVE ---
                    int safetyNet = 0;
                    while (safetyNet < 100) // Prevent infinite loops if curve is flat at 0
                    {
                        float randomDist = Random.value; // Pick a random distance (0 to 1)
                        float randomChance = Random.value; // Roll a die
                        
                        // If our die roll is lower than the curve's weight at this distance, we keep it
                        if (randomChance <= group.lateralCurve.Evaluate(randomDist))
                        {
                            magnitude = randomDist;
                            break;
                        }
                        safetyNet++;
                    }
                    
                    // Decide which side to apply this magnitude to
                    float sideDirection = 0f;
                    if (group.scatterLeft && group.scatterRight) 
                        sideDirection = Random.value > 0.5f ? 1f : -1f; 
                    else if (group.scatterLeft) 
                        sideDirection = -1f;
                    else if (group.scatterRight) 
                        sideDirection = 1f;

                    sideMultiplier = sideDirection * magnitude;
                }
                else
                {
                    // Standard linear random
                    if (group.scatterLeft && group.scatterRight) sideMultiplier = Random.Range(-1f, 1f);
                    else if (group.scatterLeft) sideMultiplier = Random.Range(-1f, 0f);
                    else if (group.scatterRight) sideMultiplier = Random.Range(0f, 1f);
                }

                float3 localOffsetPos = localPos + (right * sideMultiplier * group.lateralRange);
                
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

        //Mark the objects as static - only works if bool is true
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



/*

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;

[ExecuteInEditMode]
public class ProceduralScatterOnSpline : MonoBehaviour
{
    public enum RotationMode { FollowSpline, WorldSpace, RandomFull }

    [System.Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        [Range(1, 1000)] public int count = 20;
        public float lateralRange = 5f;
        public bool scatterLeft = true;
        public bool scatterRight = true;

        [Header("Collision Handling")]
        public bool checkOverlap = false;
        public float detectionRadius = 0.1f; //set to 0.1f as it seems to mess up with a bigger number, need to look into
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

        // --- PASS 1: SPAWN EVERYTHING ---
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

                float sideMultiplier = 0;
                if (group.scatterLeft && group.scatterRight) sideMultiplier = Random.Range(-1f, 1f);
                else if (group.scatterLeft) sideMultiplier = Random.Range(-1f, 0f);
                else if (group.scatterRight) sideMultiplier = Random.Range(0f, 1f);

                float3 localOffsetPos = localPos + (right * sideMultiplier * group.lateralRange);
                
                GameObject newInstance = PlaceObject(group, localOffsetPos, forward, up);
                if (newInstance != null) spawnedItems.Add((newInstance, group));
            }
        }

        // Essential for Editor physics
        Physics.SyncTransforms();

        // --- PASS 2: SELECTIVE CULLING ---
        HashSet<GameObject> toDestroy = new HashSet<GameObject>();

        foreach (var item in spawnedItems)
        {
            if (item.instance == null || toDestroy.Contains(item.instance)) continue;

            if (item.settings.checkOverlap)
            {
                // Check for colliders
                Collider[] hits = Physics.OverlapSphere(item.instance.transform.position, item.settings.detectionRadius, item.settings.overlapLayer);

                foreach (Collider hit in hits)
                {
                    // DIRECT CHECK: If the hit transform is NOT part of this item's hierarchy
                    if (hit.transform != item.instance.transform && !hit.transform.IsChildOf(item.instance.transform))
                    {
                        // Avoid "Double-Kill": If we hit another spawned item that is ALREADY marked for death, we stay.
                        if (toDestroy.Contains(hit.gameObject)) continue;
                        
                        // Additionally check if the hit is a child of another spawned item already marked
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
*/





/*
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;

[ExecuteInEditMode]
public class ProceduralScatterOnSpline : MonoBehaviour
{
    public enum RotationMode { FollowSpline, WorldSpace, RandomFull }

    [System.Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        [Range(1, 1000)] public int count = 20;
        public float lateralRange = 5f;
        public bool scatterLeft = true;
        public bool scatterRight = true;

        [Header("Collision Handling")]
        public bool checkOverlap = false;
        public float detectionRadius = 1.0f;
        public LayerMask overlapLayer = ~0; // Restored the dropdown

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

        // --- PASS 1: SPAWN EVERYTHING (STABLE POSITIONS) ---
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

                float sideMultiplier = 0;
                if (group.scatterLeft && group.scatterRight) sideMultiplier = Random.Range(-1f, 1f);
                else if (group.scatterLeft) sideMultiplier = Random.Range(-1f, 0f);
                else if (group.scatterRight) sideMultiplier = Random.Range(0f, 1f);

                float3 localOffsetPos = localPos + (right * sideMultiplier * group.lateralRange);
                
                GameObject newInstance = PlaceObject(group, localOffsetPos, forward, up);
                if (newInstance != null) spawnedItems.Add((newInstance, group));
            }
        }

        // Sync transforms so Physics knows where the new colliders are
        Physics.SyncTransforms();

        // --- PASS 2: SELECTIVE CULLING ---
        HashSet<GameObject> toDestroy = new HashSet<GameObject>();

        foreach (var item in spawnedItems)
        {
            if (item.instance == null || toDestroy.Contains(item.instance)) continue;

            if (item.settings.checkOverlap)
            {
                // Find everything in the radius based on the LayerMask dropdown
                Collider[] hits = Physics.OverlapSphere(item.instance.transform.position, item.settings.detectionRadius, item.settings.overlapLayer);

                foreach (Collider hit in hits)
                {
                    // Check if the hit is NOT ourself or any of our children
                    if (!hit.transform.IsChildOf(item.instance.transform))
                    {
                        // To avoid deleting BOTH objects that collide, we only delete the current one.
                        toDestroy.Add(item.instance);
                        break; 
                    }
                }
            }
        }

        // --- EXECUTE DELETIONS ---
        foreach (GameObject deadObj in toDestroy)
        {
            if (deadObj != null) DestroyImmediate(deadObj);
        }
    }

    private GameObject PlaceObject(ScatterSettings settings, float3 localPos, float3 forward, float3 up)
    {
        GameObject instance = Instantiate(settings.prefab, _internalHolder);
        instance.transform.localPosition = localPos;

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
        List<GameObject> children = new List<GameObject>();
        foreach (Transform child in _internalHolder) children.Add(child.gameObject);
        foreach (GameObject obj in children) DestroyImmediate(obj);
    }
}
*/






/*
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;

[ExecuteInEditMode]
public class ProceduralScatterOnSpline : MonoBehaviour
{
    public enum RotationMode { FollowSpline, WorldSpace, RandomFull }

    [System.Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        [Range(1, 500)] public int count = 20;
        public float lateralRange = 5f;
        public bool scatterLeft = true;
        public bool scatterRight = true;

        [Header("Collision Handling")]
        public bool checkOverlap = false;
        public float detectionRadius = 1.0f;

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

        // --- PASS 1: SPAWN EVERYTHING ---
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

                float sideMultiplier = 0;
                if (group.scatterLeft && group.scatterRight) sideMultiplier = Random.Range(-1f, 1f);
                else if (group.scatterLeft) sideMultiplier = Random.Range(-1f, 0f);
                else if (group.scatterRight) sideMultiplier = Random.Range(0f, 1f);

                float3 localOffsetPos = localPos + (right * sideMultiplier * group.lateralRange);
                
                GameObject newInstance = PlaceObject(group, localOffsetPos, forward, up);
                if (newInstance != null) spawnedItems.Add((newInstance, group));
            }
        }

        Physics.SyncTransforms();

        // --- PASS 2: CULL SAME-LAYER COLLISIONS ---
        List<GameObject> toDestroy = new List<GameObject>();

        foreach (var item in spawnedItems)
        {
            if (item.instance == null || toDestroy.Contains(item.instance)) continue;

            if (item.settings.checkOverlap)
            {
                // We create a bitmask specifically for the layer this prefab is on
                int myLayerMask = 1 << item.instance.layer;

                Collider[] hits = Physics.OverlapSphere(item.instance.transform.position, item.settings.detectionRadius, myLayerMask);

                foreach (Collider hit in hits)
                {
                    // Ignore if the hit is part of this object's hierarchy
                    if (hit.transform.IsChildOf(item.instance.transform)) continue;

                    // If we reach here, we hit another object on the SAME layer
                    toDestroy.Add(item.instance);
                    break; 
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
        List<GameObject> children = new List<GameObject>();
        foreach (Transform child in _internalHolder) children.Add(child.gameObject);
        foreach (GameObject obj in children) DestroyImmediate(obj);
    }
}
*/

/*

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;

[ExecuteInEditMode]
public class ProceduralScatterOnSpline : MonoBehaviour
{
    public enum RotationMode { FollowSpline, WorldSpace, RandomFull }

    [System.Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        [Range(1, 500)] public int count = 20;
        public float lateralRange = 5f;
        public bool scatterLeft = true;
        public bool scatterRight = true;

        [Header("Collision Handling")]
        public bool checkOverlap = false;
        public float detectionRadius = 1.0f;
        public LayerMask overlapLayer = ~0; // Suggestion: Change this from "Everything" to a specific layer!

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

        // --- PASS 1: SPAWN EVERYTHING ---
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

                float sideMultiplier = 0;
                if (group.scatterLeft && group.scatterRight) sideMultiplier = Random.Range(-1f, 1f);
                else if (group.scatterLeft) sideMultiplier = Random.Range(-1f, 0f);
                else if (group.scatterRight) sideMultiplier = Random.Range(0f, 1f);

                float3 localOffsetPos = localPos + (right * sideMultiplier * group.lateralRange);
                
                GameObject newInstance = PlaceObject(group, localOffsetPos, forward, up);
                if (newInstance != null)
                {
                    spawnedItems.Add((newInstance, group));
                }
            }
        }

        // --- PREPARE PHYSICS ---
        Physics.SyncTransforms();

        // --- PASS 2: CULL COLLISIONS ---
        List<GameObject> toDestroy = new List<GameObject>();

        foreach (var item in spawnedItems)
        {
            if (item.instance == null || toDestroy.Contains(item.instance)) continue;

            if (item.settings.checkOverlap)
            {
                Collider[] hits = Physics.OverlapSphere(item.instance.transform.position, item.settings.detectionRadius, item.settings.overlapLayer);

                foreach (Collider hit in hits)
                {
                    // FIX: IsChildOf ensures we don't accidentally delete ourselves if the collider is on a nested child mesh
                    if (!hit.transform.IsChildOf(item.instance.transform))
                    {
                        // Make sure the object we hit isn't already marked for death
                        // (We check IsChildOf again on the marked objects to be safe)
                        bool hitAlreadyDead = false;
                        foreach (GameObject deadObj in toDestroy)
                        {
                            if (hit.transform.IsChildOf(deadObj.transform))
                            {
                                hitAlreadyDead = true;
                                break;
                            }
                        }

                        if (!hitAlreadyDead)
                        {
                            toDestroy.Add(item.instance);
                            break; 
                        }
                    }
                }
            }
        }

        // --- EXECUTE DELETIONS ---
        foreach (GameObject deadObj in toDestroy)
        {
            if (deadObj != null)
            {
                DestroyImmediate(deadObj);
            }
        }
    }

    private GameObject PlaceObject(ScatterSettings settings, float3 localPos, float3 forward, float3 up)
    {
        GameObject instance = Instantiate(settings.prefab, _internalHolder);
        instance.transform.localPosition = localPos;

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
        List<GameObject> toDestroy = new List<GameObject>();
        foreach (Transform child in _internalHolder) toDestroy.Add(child.gameObject);
        foreach (GameObject obj in toDestroy) DestroyImmediate(obj);
    }
}
*/


/*
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Random = UnityEngine.Random;

[ExecuteInEditMode]
public class ProceduralScatterOnSpline : MonoBehaviour
{
    public enum RotationMode { FollowSpline, WorldSpace, RandomFull }

    [System.Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        [Range(1, 500)] public int count = 20;
        public float lateralRange = 5f;
        public bool scatterLeft = true;
        public bool scatterRight = true;

        [Header("Collision Handling")]
        public bool checkOverlap = false;
        public float detectionRadius = 1.0f;
        public LayerMask overlapLayer = ~0; // Default to "Everything"

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

        foreach (var group in scatterGroups)
        {
            if (group.prefab == null) continue;

            Random.InitState(globalSeed + group.seedOffset);

            int placedCount = 0;
            int attempts = 0;
            int maxAttempts = group.count * 5; // Prevent infinite loops if space is full

            while (placedCount < group.count && attempts < maxAttempts)
            {
                attempts++;

                float t = Random.value;
                spline.Evaluate(t, out float3 localPos, out float3 forward, out float3 up);
                
                float3 right = math.cross(forward, up);
                right = math.normalize(right);

                float sideMultiplier = 0;
                if (group.scatterLeft && group.scatterRight) sideMultiplier = Random.Range(-1f, 1f);
                else if (group.scatterLeft) sideMultiplier = Random.Range(-1f, 0f);
                else if (group.scatterRight) sideMultiplier = Random.Range(0f, 1f);

                float3 localOffsetPos = localPos + (right * sideMultiplier * group.lateralRange);
                
                // Convert local position to world position for physics check
                Vector3 worldPos = transform.TransformPoint(localOffsetPos);

                // Overlap Check
                if (group.checkOverlap)
                {
                    // Checks if any colliders are within the radius at worldPos
                    if (Physics.CheckSphere(worldPos, group.detectionRadius, group.overlapLayer))
                    {
                        continue; // Skip this placement and try again
                    }
                }

                PlaceObject(group, localOffsetPos, forward, up);
                placedCount++;
            }
        }
    }

    private void PlaceObject(ScatterSettings settings, float3 localPos, float3 forward, float3 up)
    {
        GameObject instance = Instantiate(settings.prefab, _internalHolder);
        instance.transform.localPosition = localPos;

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
        
        // Ensure the new object has a collider for the NEXT object to detect it
        // If your prefab doesn't have one, this check won't work!
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
        List<GameObject> toDestroy = new List<GameObject>();
        foreach (Transform child in _internalHolder) toDestroy.Add(child.gameObject);
        foreach (GameObject obj in toDestroy) DestroyImmediate(obj);
    }
}
*/
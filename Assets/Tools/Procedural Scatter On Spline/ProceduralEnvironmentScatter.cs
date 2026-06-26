using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class ProceduralEnvironmentScatter : MonoBehaviour
{
    public enum RotationMode
    {
        FollowSpline,
        WorldSpace,
        RandomFull
    }

    private const string CombinedHolderName = "Combined_EnvironmentBake";
    private const int MaxCurveSampleAttempts = 100;

    [Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        public bool markAsStatic = false;
        
        [Tooltip("If true, all meshes in this group will be combined into an optimized batch. If false, they remain independent child assets.")]
        public bool mergeMeshes = true;

        [Range(1, 1000)]
        public int count = 20;

        [Header("Lateral Distribution")]
        [Min(0f)]
        public float startDistance = 0.5f;

        [Min(0f)]
        public float lateralRange = 5.0f;

        public bool scatterLeft = true;
        public bool scatterRight = true;

        [Space(5)]
        public bool useLateralCurve = false;

        [Tooltip("X is the normalized distance from startDistance to lateralRange. Y is the chance of accepting that distance.")]
        public AnimationCurve lateralCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        [Header("Collision Handling")]
        public bool checkOverlap = false;

        [Min(0f)]
        public float detectionRadius = 0.1f;

        [Tooltip("Objects touching colliders on these layers can be removed during the overlap check.")]
        public LayerMask overlapLayer = ~0;

        [Header("Rotation")]
        [Tooltip("Every mode aligns the object up direction to the hit surface. This setting controls which way the object faces around that surface normal.")]
        public RotationMode rotationMode = RotationMode.FollowSpline;

        public Vector3 minRotationOffset;
        public Vector3 maxRotationOffset = new Vector3(0f, 360f, 0f);

        [Header("Scale")]
        public Vector2 scaleRange = new Vector2(0.8f, 1.2f);
        public int seedOffset = 0;
    }

    private struct SpawnedItem
    {
        public GameObject instance;
        public ScatterSettings settings;
        public Collider surfaceCollider;
    }

    private struct MeshPart
    {
        public Mesh mesh;
        public int subMeshIndex;
        public Matrix4x4 localToWorldMatrix;
    }

    private readonly struct BakeKey : IEquatable<BakeKey>
    {
        public readonly Material material;
        public readonly bool markAsStatic;

        public BakeKey(Material material, bool markAsStatic)
        {
            this.material = material;
            this.markAsStatic = markAsStatic;
        }

        public bool Equals(BakeKey other)
        {
            return material == other.material && markAsStatic == other.markAsStatic;
        }

        public override bool Equals(object obj)
        {
            return obj is BakeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = material != null ? material.GetHashCode() : 0;
                return (hash * 397) ^ markAsStatic.GetHashCode();
            }
        }
    }

    private struct LocalRandom
    {
        private uint _state;

        public LocalRandom(int seed)
        {
            _state = unchecked((uint)seed);

            // Xorshift needs a non-zero state. This keeps a seed of 0 deterministic.
            if (_state == 0u)
                _state = 0x6D2B79F5u;
        }

        public float Value()
        {
            return (NextUInt() & 0x00FFFFFFu) / 16777216f;
        }

        public float Range(float min, float max)
        {
            return Mathf.Lerp(min, max, Value());
        }

        public bool Bool()
        {
            return (NextUInt() & 1u) == 1u;
        }

        public Vector3 HorizontalDirection()
        {
            float angle = Range(0f, Mathf.PI * 2f);
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }

        private uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }
    }

    public SplineContainer splineContainer;
    public List<ScatterSettings> scatterGroups = new List<ScatterSettings>();
    public int globalSeed = 12345;

    [Header("Surface Raycast")]
    [Tooltip("The ray starts this far above the sampled spline position.")]
    [Min(0f)]
    public float raycastStartHeight = 10f;

    [Tooltip("How far below the sampled spline position the ray is allowed to search.")]
    [Min(0.01f)]
    public float raycastDistance = 100f;

    [Tooltip("Only colliders on these layers can receive scattered objects.")]
    public LayerMask surfaceLayer = ~0;

    [Tooltip("Moves the object slightly along the surface normal after the ray hits.")]
    public float surfaceOffset = 0f;

    public QueryTriggerInteraction surfaceTriggerInteraction = QueryTriggerInteraction.Ignore;

    [Header("Debug")]
    [Tooltip("Logs a short report after each scatter so you can see why objects did or did not spawn.")]
    public bool logScatterReport = true;

    [Tooltip("Leaves the temporary prefab instances visible instead of baking them into combined meshes.")]
    public bool keepGeneratedInstances = false;

    [TextArea(3, 8)]
    public string lastScatterReport = "Scatter has not run yet.";

    private readonly List<SpawnedItem> _spawnedItems = new List<SpawnedItem>();
    private readonly HashSet<GameObject> _spawnedRoots = new HashSet<GameObject>();
    private readonly HashSet<GameObject> _objectsToDestroy = new HashSet<GameObject>();
    private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
    private readonly List<CombineInstance> _combineInstances = new List<CombineInstance>();
    private readonly Dictionary<BakeKey, List<MeshPart>> _meshPartsByBakeKey = new Dictionary<BakeKey, List<MeshPart>>();

    private Collider[] _overlapResults = new Collider[64];
    private int _lastSampleCount;
    private int _lastSurfaceHitCount;
    private int _lastSurfaceMissCount;
    private int _lastSpawnedCount;
    private int _lastOverlapRemovedCount;
    private int _lastRemainingAfterOverlap;
    private int _lastCombinedMeshCount;

    [ContextMenu("Force Environment Scatter")]
    public void ManualScatter()
    {
        Scatter();
    }

    private void OnValidate()
    {
#if UNITY_EDITOR
        if (!isActiveAndEnabled)
            return;

        // Unity can call OnValidate while it is still applying Inspector changes.
        // Delaying the rebuild keeps hierarchy edits out of that sensitive moment.
        EditorApplication.delayCall -= DelayedScatter;
        EditorApplication.delayCall += DelayedScatter;
#endif
    }

#if UNITY_EDITOR
    private void DelayedScatter()
    {
        EditorApplication.delayCall -= DelayedScatter;

        if (this == null || !isActiveAndEnabled)
            return;

        Scatter();
    }
#endif

    public void Scatter()
    {
        ResetScatterReport();

        if (!HasSplineContainer())
            return;

        if (scatterGroups == null || raycastDistance <= 0f)
            return;

        ClearAllGenerated();
        _spawnedItems.Clear();
        _spawnedRoots.Clear();

        // Raycasts use the physics world, so make sure edited or moved colliders are up to date first.
        Physics.SyncTransforms();

        Spline spline = splineContainer.Spline;
        Transform splineTransform = splineContainer.transform;

        // Establish the primary bake holder immediately
        GameObject combinedHolderObj = new GameObject(CombinedHolderName);
        Transform combinedHolder = combinedHolderObj.transform;
        combinedHolder.SetParent(transform, false);
        ResetLocalTransform(combinedHolder);

        // Track unmerged folders by name so we reuse them if multiple groups share a name
        Dictionary<string, Transform> unmergedFolders = new Dictionary<string, Transform>();

        for (int groupIndex = 0; groupIndex < scatterGroups.Count; groupIndex++)
        {
            ScatterSettings group = scatterGroups[groupIndex];

            if (!CanScatterGroup(group))
                continue;

            LocalRandom random = new LocalRandom(unchecked(globalSeed + group.seedOffset));

            // Determine parent up-front based on merge setting
            Transform itemParent = combinedHolder;
            if (!group.mergeMeshes)
            {
                string groupName = string.IsNullOrEmpty(group.name) ? "Unnamed Group" : group.name;
                
                if (!unmergedFolders.TryGetValue(groupName, out itemParent))
                {
                    GameObject folderObj = new GameObject(groupName);
                    itemParent = folderObj.transform;
                    // Put it right alongside the combined holder (sibling)
                    itemParent.SetParent(transform, false); 
                    ResetLocalTransform(itemParent);
                    unmergedFolders.Add(groupName, itemParent);
                }
            }

            for (int i = 0; i < group.count; i++)
            {
                _lastSampleCount++;
                float t = random.Value();
                spline.Evaluate(t, out float3 localPosition, out float3 localForward, out float3 localUp);

                Vector3 worldPosition = splineTransform.TransformPoint(localPosition);
                Vector3 worldForward = SafeNormalize(splineTransform.TransformDirection(localForward), Vector3.forward);
                Vector3 worldUp = SafeNormalize(splineTransform.TransformDirection(localUp), Vector3.up);

                // This gives us a sideways direction from the spline before we raycast down.
                Vector3 worldRight = SafeNormalize(Vector3.Cross(worldForward, worldUp), Vector3.right);
                float sideDirection = GetSideDirection(group, ref random);
                float lateralPercent = GetLateralPercent(group, ref random);
                float lateralDistance = Mathf.Lerp(group.startDistance, group.lateralRange, lateralPercent);
                Vector3 rayBasePosition = worldPosition + (worldRight * sideDirection * lateralDistance);

                if (!TryFindSurface(rayBasePosition, out RaycastHit surfaceHit))
                {
                    _lastSurfaceMissCount++;
                    continue;
                }

                _lastSurfaceHitCount++;

                // Instantiate directly into the target parent
                GameObject instance = PlaceObject(group, surfaceHit, worldForward, itemParent, ref random);

                if (instance != null)
                {
                    _lastSpawnedCount++;
                    _spawnedRoots.Add(instance);
                    _spawnedItems.Add(new SpawnedItem
                    {
                        instance = instance,
                        settings = group,
                        surfaceCollider = surfaceHit.collider
                    });
                }
            }
        }

        if (_spawnedItems.Count == 0)
        {
            WriteScatterReport();
            return;
        }

        Physics.SyncTransforms();
        RemoveOverlappingObjects();
        _lastRemainingAfterOverlap = _spawnedItems.Count - _lastOverlapRemovedCount;

        if (!keepGeneratedInstances)
        {
            CombineGeneratedMeshes(combinedHolder);
        }

        WriteScatterReport();
    }

    private bool TryFindSurface(Vector3 rayBasePosition, out RaycastHit hit)
    {
        Vector3 rayOrigin = rayBasePosition + (Vector3.up * raycastStartHeight);
        float totalRayDistance = raycastStartHeight + raycastDistance;

        // The tool always casts in world-space down, so it can find uneven terrain below the spline.
        return Physics.Raycast(
            rayOrigin,
            Vector3.down,
            out hit,
            totalRayDistance,
            surfaceLayer,
            surfaceTriggerInteraction);
    }

    private void ResetScatterReport()
    {
        _lastSampleCount = 0;
        _lastSurfaceHitCount = 0;
        _lastSurfaceMissCount = 0;
        _lastSpawnedCount = 0;
        _lastOverlapRemovedCount = 0;
        _lastRemainingAfterOverlap = 0;
        _lastCombinedMeshCount = 0;
    }

    private void WriteScatterReport()
    {
        lastScatterReport =
            "Samples tried: " + _lastSampleCount +
            "\nSurface hits: " + _lastSurfaceHitCount +
            "\nSurface misses: " + _lastSurfaceMissCount +
            "\nObjects spawned: " + _lastSpawnedCount +
            "\nRemoved by overlap: " + _lastOverlapRemovedCount +
            "\nRemaining before bake: " + _lastRemainingAfterOverlap +
            "\nCombined meshes made: " + _lastCombinedMeshCount;

        if (logScatterReport)
            Debug.Log(lastScatterReport, this);
    }

    private bool HasSplineContainer()
    {
        if (splineContainer != null)
            return true;

        string message = gameObject.name + " is missing an object in the spline container";
        Debug.LogError(message, this);
        return false;
    }

    private static bool CanScatterGroup(ScatterSettings group)
    {
        return group != null && group.prefab != null && group.count > 0;
    }

    private static float3 SafeNormalize(float3 value, float3 fallback)
    {
        return math.lengthsq(value) > 0.0001f ? math.normalize(value) : fallback;
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }

    private static float GetSideDirection(ScatterSettings settings, ref LocalRandom random)
    {
        if (settings.scatterLeft && settings.scatterRight)
            return random.Bool() ? 1f : -1f;

        if (settings.scatterLeft)
            return -1f;

        if (settings.scatterRight)
            return 1f;

        // If neither side is enabled, the ray starts directly above the spline line.
        return 0f;
    }

    private static float GetLateralPercent(ScatterSettings settings, ref LocalRandom random)
    {
        if (!settings.useLateralCurve || settings.lateralCurve == null)
            return random.Value();

        // Rejection sampling lets the curve act like a probability mask.
        // If nothing is accepted after a few tries, fall back to the start distance.
        for (int attempt = 0; attempt < MaxCurveSampleAttempts; attempt++)
        {
            float distancePercent = random.Value();
            float chance = random.Value();

            if (chance <= settings.lateralCurve.Evaluate(distancePercent))
                return distancePercent;
        }

        return 0f;
    }

    private GameObject PlaceObject(
        ScatterSettings settings,
        RaycastHit surfaceHit,
        Vector3 splineForward,
        Transform parent,
        ref LocalRandom random)
    {
        GameObject instance = InstantiatePrefab(settings.prefab, parent);

        if (instance == null)
            return null;

        Vector3 surfaceNormal = SafeNormalize(surfaceHit.normal, Vector3.up);
        instance.transform.position = surfaceHit.point + (surfaceNormal * surfaceOffset);
        instance.transform.rotation = GetSurfaceRotation(settings, splineForward, surfaceNormal, ref random);
        instance.transform.localScale = GetScale(settings, ref random);

        SetStaticRecursive(instance, settings.markAsStatic);
        return instance;
    }

    private static GameObject InstantiatePrefab(GameObject prefab, Transform parent)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            GameObject prefabInstance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;

            if (prefabInstance != null)
                return prefabInstance;
        }
#endif

        return Instantiate(prefab, parent);
    }

    private static Quaternion GetSurfaceRotation(
        ScatterSettings settings,
        Vector3 splineForward,
        Vector3 surfaceNormal,
        ref LocalRandom random)
    {
        Vector3 forward;

        switch (settings.rotationMode)
        {
            case RotationMode.FollowSpline:
                forward = ProjectDirectionOnSurface(splineForward, surfaceNormal, Vector3.forward);
                break;

            case RotationMode.RandomFull:
                forward = ProjectDirectionOnSurface(random.HorizontalDirection(), surfaceNormal, Vector3.forward);
                break;

            default:
                forward = ProjectDirectionOnSurface(Vector3.forward, surfaceNormal, Vector3.right);
                break;
        }

        // LookRotation makes the object stand on the surface by using the hit normal as its up direction.
        Quaternion surfaceRotation = Quaternion.LookRotation(forward, surfaceNormal);

        Vector3 rotationOffset = new Vector3(
            random.Range(settings.minRotationOffset.x, settings.maxRotationOffset.x),
            random.Range(settings.minRotationOffset.y, settings.maxRotationOffset.y),
            random.Range(settings.minRotationOffset.z, settings.maxRotationOffset.z));

        return surfaceRotation * Quaternion.Euler(rotationOffset);
    }

    private static Vector3 ProjectDirectionOnSurface(Vector3 direction, Vector3 surfaceNormal, Vector3 fallback)
    {
        Vector3 projected = Vector3.ProjectOnPlane(direction, surfaceNormal);

        if (projected.sqrMagnitude > 0.0001f)
            return projected.normalized;

        projected = Vector3.ProjectOnPlane(fallback, surfaceNormal);

        if (projected.sqrMagnitude > 0.0001f)
            return projected.normalized;

        // This final fallback handles rare cases where the requested forward direction points straight into the surface.
        Vector3 helperAxis = Mathf.Abs(Vector3.Dot(surfaceNormal, Vector3.up)) < 0.99f ? Vector3.up : Vector3.right;
        return Vector3.Cross(helperAxis, surfaceNormal).normalized;
    }

    private static Vector3 GetScale(ScatterSettings settings, ref LocalRandom random)
    {
        float minScale = Mathf.Min(settings.scaleRange.x, settings.scaleRange.y);
        float maxScale = Mathf.Max(settings.scaleRange.x, settings.scaleRange.y);
        float scale = random.Range(minScale, maxScale);

        return new Vector3(scale, scale, scale);
    }

    private static void SetStaticRecursive(GameObject root, bool value)
    {
        Transform[] children = root.GetComponentsInChildren<Transform>(true);

        for (int i = 0; i < children.Length; i++)
            children[i].gameObject.isStatic = value;
    }

    private void RemoveOverlappingObjects()
    {
        _objectsToDestroy.Clear();

        for (int i = 0; i < _spawnedItems.Count; i++)
        {
            SpawnedItem item = _spawnedItems[i];

            if (item.instance == null || _objectsToDestroy.Contains(item.instance))
                continue;

            if (!item.settings.checkOverlap || item.settings.detectionRadius <= 0f)
                continue;

            int hitCount = OverlapSphereNonAllocResizing(
                item.instance.transform.position,
                item.settings.detectionRadius,
                item.settings.overlapLayer);

            for (int hitIndex = 0; hitIndex < hitCount; hitIndex++)
            {
                Collider hit = _overlapResults[hitIndex];

                if (hit == null)
                    continue;

                // The surface we landed on should not count as an overlap failure.
                if (hit == item.surfaceCollider)
                    continue;

                GameObject hitRoot = GetGeneratedRoot(hit.transform);

                if (hitRoot == item.instance)
                    continue;

                if (hitRoot != null && _objectsToDestroy.Contains(hitRoot))
                    continue;

                _objectsToDestroy.Add(item.instance);
                break;
            }
        }

        _lastOverlapRemovedCount = _objectsToDestroy.Count;

        foreach (GameObject objectToDestroy in _objectsToDestroy)
            DestroyObject(objectToDestroy);
    }

    private int OverlapSphereNonAllocResizing(Vector3 position, float radius, LayerMask layerMask)
    {
        while (true)
        {
            int count = Physics.OverlapSphereNonAlloc(position, radius, _overlapResults, layerMask);

            if (count < _overlapResults.Length)
                return count;

            Array.Resize(ref _overlapResults, _overlapResults.Length * 2);
        }
    }

    // Safely finds the root of our generated objects using the _spawnedRoots HashSet.
    // This allows objects to be parented anywhere (e.g. side-by-side folders).
    private GameObject GetGeneratedRoot(Transform child)
    {
        if (child == null)
            return null;

        Transform current = child;
        while (current != null)
        {
            if (_spawnedRoots.Contains(current.gameObject))
                return current.gameObject;
            current = current.parent;
        }

        return null;
    }

    private void ClearAllGenerated()
    {
        // 1. Clear the combined bake holder
        Transform oldBake = transform.Find(CombinedHolderName);
        if (oldBake != null)
        {
            DestroyGeneratedMeshes(oldBake);
            DestroyObject(oldBake.gameObject);
        }

        // 2. Clear out any unmerged group folders that we previously created
        if (scatterGroups != null)
        {
            for (int i = 0; i < scatterGroups.Count; i++)
            {
                if (scatterGroups[i] != null && !string.IsNullOrEmpty(scatterGroups[i].name))
                {
                    Transform oldGroup = transform.Find(scatterGroups[i].name);
                    
                    // Safety check to ensure we don't accidentally delete the combined holder if they share a name
                    if (oldGroup != null && oldGroup.name != CombinedHolderName)
                    {
                        DestroyObject(oldGroup.gameObject);
                    }
                }
            }
        }
    }

    public void CombineGeneratedMeshes(Transform combinedHolder)
    {
        if (combinedHolder == null)
            return;

        // Collect mesh parts explicitly from the items flagged for merging.
        CollectMeshParts();

        if (_meshPartsByBakeKey.Count > 0)
        {
            foreach (KeyValuePair<BakeKey, List<MeshPart>> pair in _meshPartsByBakeKey)
            {
                BakeKey key = pair.Key;
                List<MeshPart> meshParts = pair.Value;

                if (key.material == null || meshParts.Count == 0)
                    continue;

                GameObject combinedObject = new GameObject("Combined_" + key.material.name);
                combinedObject.transform.SetParent(combinedHolder, false);
                combinedObject.isStatic = key.markAsStatic;
                ResetLocalTransform(combinedObject.transform);

                Matrix4x4 worldToLocal = combinedObject.transform.worldToLocalMatrix;
                _combineInstances.Clear();

                for (int i = 0; i < meshParts.Count; i++)
                {
                    MeshPart part = meshParts[i];

                    _combineInstances.Add(new CombineInstance
                    {
                        mesh = part.mesh,
                        subMeshIndex = part.subMeshIndex,
                        transform = worldToLocal * part.localToWorldMatrix
                    });
                }

                Mesh combinedMesh = new Mesh
                {
                    name = "Mesh_" + key.material.name,
                    indexFormat = IndexFormat.UInt32
                };

                combinedMesh.CombineMeshes(_combineInstances.ToArray(), true, true);
                combinedMesh.RecalculateBounds();

                MeshFilter meshFilter = combinedObject.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = combinedObject.AddComponent<MeshRenderer>();

                meshFilter.sharedMesh = combinedMesh;
                meshRenderer.sharedMaterial = key.material;
                _lastCombinedMeshCount++;
            }
        }

        // Destroy the individual prefab instances that were successfully merged
        for (int i = 0; i < _spawnedItems.Count; i++)
        {
            SpawnedItem item = _spawnedItems[i];
            
            if (item.settings.mergeMeshes && item.instance != null)
            {
                DestroyObject(item.instance);
            }
        }

        // Garbage collection cleanup if absolutely nothing was placed inside the combined holder
        if (_meshPartsByBakeKey.Count == 0 && combinedHolder.childCount == 0)
        {
            DestroyObject(combinedHolder.gameObject);
        }
    }

    private void CollectMeshParts()
    {
        _meshPartsByBakeKey.Clear();
        _renderers.Clear();
        
        // Exclusively fetch renderers from objects in groups marked for merging
        for (int i = 0; i < _spawnedItems.Count; i++)
        {
            SpawnedItem item = _spawnedItems[i];
            
            if (item.instance == null || !item.settings.mergeMeshes)
                continue;

            MeshRenderer[] childRenderers = item.instance.GetComponentsInChildren<MeshRenderer>(true);
            _renderers.AddRange(childRenderers);
        }

        for (int rendererIndex = 0; rendererIndex < _renderers.Count; rendererIndex++)
        {
            MeshRenderer meshRenderer = _renderers[rendererIndex];
            MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();

            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            Mesh sourceMesh = meshFilter.sharedMesh;
            Material[] materials = meshRenderer.sharedMaterials;

            if (materials == null || materials.Length == 0)
                continue;

            for (int subMeshIndex = 0; subMeshIndex < sourceMesh.subMeshCount; subMeshIndex++)
            {
                Material material = materials[Mathf.Min(subMeshIndex, materials.Length - 1)];

                if (material == null)
                    continue;

                BakeKey key = new BakeKey(material, meshRenderer.gameObject.isStatic);

                if (!_meshPartsByBakeKey.TryGetValue(key, out List<MeshPart> parts))
                {
                    parts = new List<MeshPart>();
                    _meshPartsByBakeKey.Add(key, parts);
                }

                parts.Add(new MeshPart
                {
                    mesh = sourceMesh,
                    subMeshIndex = subMeshIndex,
                    localToWorldMatrix = meshRenderer.transform.localToWorldMatrix
                });
            }
        }
    }

    private static void DestroyGeneratedMeshes(Transform root)
    {
        MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);

        for (int i = 0; i < meshFilters.Length; i++)
        {
            Mesh mesh = meshFilters[i].sharedMesh;

            if (mesh == null)
                continue;

#if UNITY_EDITOR
            if (AssetDatabase.Contains(mesh))
                continue;
#endif

            meshFilters[i].sharedMesh = null;
            DestroyObject(mesh);
        }
    }

    private static void ResetLocalTransform(Transform target)
    {
        target.localPosition = Vector3.zero;
        target.localRotation = Quaternion.identity;
        target.localScale = Vector3.one;
    }

    private static void DestroyObject(UnityEngine.Object target)
    {
        if (target == null)
            return;

        // The tool rebuilds immediately, so immediate destruction prevents old results from being baked again.
        DestroyImmediate(target);
    }
}
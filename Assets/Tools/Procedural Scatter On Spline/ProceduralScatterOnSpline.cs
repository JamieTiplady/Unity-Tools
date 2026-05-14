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
public class ProceduralScatterOnSpline : MonoBehaviour
{
    public enum RotationMode
    {
        FollowSpline,
        WorldSpace,
        RandomFull
    }

    private const string GeneratedHolderName = "Generated_Scatter";
    private const string CombinedHolderName = "Combined_Bake";
    private const int MaxCurveSampleAttempts = 100;

    [Serializable]
    public class ScatterSettings
    {
        public string name = "New Group";
        public GameObject prefab;
        public bool markAsStatic = false;

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

        public LayerMask overlapLayer = ~0;

        [Header("Rotation")]
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

        public Quaternion Rotation()
        {
            // This creates an even random rotation, similar in spirit to UnityEngine.Random.rotation.
            float u1 = Value();
            float u2 = Value();
            float u3 = Value();

            float rootOneMinusU1 = Mathf.Sqrt(1f - u1);
            float rootU1 = Mathf.Sqrt(u1);
            float theta1 = 2f * Mathf.PI * u2;
            float theta2 = 2f * Mathf.PI * u3;

            return new Quaternion(
                rootOneMinusU1 * Mathf.Sin(theta1),
                rootOneMinusU1 * Mathf.Cos(theta1),
                rootU1 * Mathf.Sin(theta2),
                rootU1 * Mathf.Cos(theta2));
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

    private readonly List<SpawnedItem> _spawnedItems = new List<SpawnedItem>();
    private readonly HashSet<GameObject> _objectsToDestroy = new HashSet<GameObject>();
    private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
    private readonly List<CombineInstance> _combineInstances = new List<CombineInstance>();
    private readonly Dictionary<BakeKey, List<MeshPart>> _meshPartsByBakeKey = new Dictionary<BakeKey, List<MeshPart>>();

    private Collider[] _overlapResults = new Collider[64];
    private Transform _internalHolder;

    [ContextMenu("Force Scatter")]
    public void ManualScatter()
    {
        Scatter();
    }

    private void OnValidate()
    {
#if UNITY_EDITOR
        if (!isActiveAndEnabled || splineContainer == null || scatterGroups == null || scatterGroups.Count == 0)
            return;

        // OnValidate can run while Unity is still updating serialized fields.
        // Delaying the rebuild avoids editing the hierarchy in the middle of validation.
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
        if (splineContainer == null || scatterGroups == null)
            return;

        EnsureHolderExists();
        ClearAllGenerated();
        _spawnedItems.Clear();

        Spline spline = splineContainer.Spline;

        for (int groupIndex = 0; groupIndex < scatterGroups.Count; groupIndex++)
        {
            ScatterSettings group = scatterGroups[groupIndex];

            if (!CanScatterGroup(group))
                continue;

            LocalRandom random = new LocalRandom(unchecked(globalSeed + group.seedOffset));

            for (int i = 0; i < group.count; i++)
            {
                float t = random.Value();
                spline.Evaluate(t, out float3 localPosition, out float3 forward, out float3 up);

                forward = SafeNormalize(forward, new float3(0f, 0f, 1f));
                up = SafeNormalize(up, new float3(0f, 1f, 0f));

                // The right vector is what lets us move objects sideways from the spline.
                float3 right = SafeNormalize(math.cross(forward, up), new float3(1f, 0f, 0f));
                float sideDirection = GetSideDirection(group, ref random);
                float lateralPercent = GetLateralPercent(group, ref random);
                float lateralDistance = Mathf.Lerp(group.startDistance, group.lateralRange, lateralPercent);
                float3 finalLocalPosition = localPosition + (right * sideDirection * lateralDistance);

                GameObject instance = PlaceObject(group, finalLocalPosition, forward, up, ref random);

                if (instance != null)
                {
                    _spawnedItems.Add(new SpawnedItem
                    {
                        instance = instance,
                        settings = group
                    });
                }
            }
        }

        if (_spawnedItems.Count == 0)
            return;

        Physics.SyncTransforms();
        RemoveOverlappingObjects();
        CombineGeneratedMeshes();
    }

    private static bool CanScatterGroup(ScatterSettings group)
    {
        return group != null && group.prefab != null && group.count > 0;
    }

    private static float3 SafeNormalize(float3 value, float3 fallback)
    {
        return math.lengthsq(value) > 0.0001f ? math.normalize(value) : fallback;
    }

    private static float GetSideDirection(ScatterSettings settings, ref LocalRandom random)
    {
        if (settings.scatterLeft && settings.scatterRight)
            return random.Bool() ? 1f : -1f;

        if (settings.scatterLeft)
            return -1f;

        if (settings.scatterRight)
            return 1f;

        // If neither side is enabled, the object stays on the spline line.
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
        float3 localPosition,
        float3 forward,
        float3 up,
        ref LocalRandom random)
    {
        GameObject instance = InstantiatePrefab(settings.prefab, _internalHolder);

        if (instance == null)
            return null;

        instance.transform.localPosition = localPosition;
        instance.transform.localRotation = GetRotation(settings, forward, up, ref random);
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

    private static Quaternion GetRotation(
        ScatterSettings settings,
        float3 forward,
        float3 up,
        ref LocalRandom random)
    {
        Quaternion baseRotation;

        switch (settings.rotationMode)
        {
            case RotationMode.FollowSpline:
                baseRotation = Quaternion.LookRotation(forward, up);
                break;

            case RotationMode.RandomFull:
                baseRotation = random.Rotation();
                break;

            default:
                baseRotation = Quaternion.identity;
                break;
        }

        Vector3 rotationOffset = new Vector3(
            random.Range(settings.minRotationOffset.x, settings.maxRotationOffset.x),
            random.Range(settings.minRotationOffset.y, settings.maxRotationOffset.y),
            random.Range(settings.minRotationOffset.z, settings.maxRotationOffset.z));

        return baseRotation * Quaternion.Euler(rotationOffset);
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

                GameObject hitRoot = GetGeneratedRoot(hit.transform);

                if (hitRoot == item.instance)
                    continue;

                if (hitRoot != null && _objectsToDestroy.Contains(hitRoot))
                    continue;

                _objectsToDestroy.Add(item.instance);
                break;
            }
        }

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

    private GameObject GetGeneratedRoot(Transform child)
    {
        if (child == null || _internalHolder == null)
            return null;

        Transform current = child;

        while (current != null && current.parent != _internalHolder)
            current = current.parent;

        return current != null && current.parent == _internalHolder ? current.gameObject : null;
    }

    private void EnsureHolderExists()
    {
        _internalHolder = transform.Find(GeneratedHolderName);

        if (_internalHolder == null)
        {
            GameObject holder = new GameObject(GeneratedHolderName);
            _internalHolder = holder.transform;
            _internalHolder.SetParent(transform, false);
        }

        ResetLocalTransform(_internalHolder);
    }

    private void ClearAllGenerated()
    {
        if (_internalHolder != null)
        {
            for (int i = _internalHolder.childCount - 1; i >= 0; i--)
                DestroyObject(_internalHolder.GetChild(i).gameObject);
        }

        Transform oldBake = transform.Find(CombinedHolderName);

        if (oldBake != null)
        {
            DestroyGeneratedMeshes(oldBake);
            DestroyObject(oldBake.gameObject);
        }
    }

    public void CombineGeneratedMeshes()
    {
        if (_internalHolder == null || _internalHolder.childCount == 0)
            return;

        CollectMeshParts();

        if (_meshPartsByBakeKey.Count == 0)
            return;

        GameObject combinedHolder = new GameObject(CombinedHolderName);
        combinedHolder.transform.SetParent(transform, false);
        ResetLocalTransform(combinedHolder.transform);

        foreach (KeyValuePair<BakeKey, List<MeshPart>> pair in _meshPartsByBakeKey)
        {
            BakeKey key = pair.Key;
            List<MeshPart> meshParts = pair.Value;

            if (key.material == null || meshParts.Count == 0)
                continue;

            GameObject combinedObject = new GameObject("Combined_" + key.material.name);
            combinedObject.transform.SetParent(combinedHolder.transform, false);
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
        }

        //Once the combined meshes exist, the individual prefab instances are no longer needed.
        for (int i = _internalHolder.childCount - 1; i >= 0; i--)
            DestroyObject(_internalHolder.GetChild(i).gameObject);
    }

    private void CollectMeshParts()
    {
        _meshPartsByBakeKey.Clear();
        _renderers.Clear();
        _internalHolder.GetComponentsInChildren(true, _renderers);

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

        //The tool rebuilds immediately, so immediate destruction prevents old results from being baked again.
        DestroyImmediate(target);
    }
}




//////////////////////////////////////////////////


/*


using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using System.Linq;
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
        public float startDistance = 0.5f;
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
        ClearAllGenerated(); // Now clears both scatter instances AND old bakes

        var spline = splineContainer.Spline;
        List<(GameObject instance, ScatterSettings settings)> spawnedItems = new();

        foreach (var group in scatterGroups)
        {
            if (group.prefab == null) continue;
            Random.InitState(globalSeed + group.seedOffset);

            for (int i = 0; i < group.count; i++)
            {
                float t = Random.value;
                spline.Evaluate(t, out float3 localPos, out float3 forward, out float3 up);

                float3 right = math.normalize(math.cross(forward, up));

                float sideDirection = 0;
                float magnitude    = 0;

                if (group.scatterLeft && group.scatterRight)
                    sideDirection = Random.value > 0.5f ? 1f : -1f;
                else if (group.scatterLeft)
                    sideDirection = -1f;
                else if (group.scatterRight)
                    sideDirection = 1f;

                if (group.useLateralCurve)
                {
                    int safetyNet = 0;
                    while (safetyNet < 100)
                    {
                        float randomDist   = Random.value;
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

                float finalOffsetDist = group.startDistance + (magnitude * (group.lateralRange - group.startDistance));
                float3 localOffsetPos = localPos + (right * sideDirection * finalOffsetDist);

                GameObject newInstance = PlaceObject(group, localOffsetPos, forward, up);
                if (newInstance != null) spawnedItems.Add((newInstance, group));
            }
        }

        Physics.SyncTransforms();

        // Pass 2: cull overlapping objects
        HashSet<GameObject> toDestroy = new();

        foreach (var item in spawnedItems)
        {
            if (item.instance == null || toDestroy.Contains(item.instance)) continue;

            if (item.settings.checkOverlap)
            {
                Collider[] hits = Physics.OverlapSphere(
                    item.instance.transform.position,
                    item.settings.detectionRadius,
                    item.settings.overlapLayer);

                foreach (Collider hit in hits)
                {
                    if (hit.transform == item.instance.transform) continue;
                    if (hit.transform.IsChildOf(item.instance.transform)) continue;
                    if (toDestroy.Contains(hit.gameObject)) continue;

                    bool hitAlreadyMarked = toDestroy.Any(m => hit.transform.IsChildOf(m.transform));
                    if (hitAlreadyMarked) continue;

                    toDestroy.Add(item.instance);
                    break;
                }
            }
        }

        foreach (GameObject dead in toDestroy)
            if (dead != null) DestroyImmediate(dead);

        CombineGeneratedMeshes();
    }

    private GameObject PlaceObject(ScatterSettings settings, float3 localPos, float3 forward, float3 up)
    {
        GameObject instance = Instantiate(settings.prefab, _internalHolder);
        instance.transform.localPosition = localPos;
        instance.isStatic = settings.markAsStatic;

        Quaternion baseRot = settings.rotationMode switch
        {
            RotationMode.FollowSpline => Quaternion.LookRotation(forward, up),
            RotationMode.RandomFull   => Random.rotation,
            _                         => Quaternion.identity
        };

        Vector3 randomEuler = new Vector3(
            Random.Range(settings.minRotationOffset.x, settings.maxRotationOffset.x),
            Random.Range(settings.minRotationOffset.y, settings.maxRotationOffset.y),
            Random.Range(settings.minRotationOffset.z, settings.maxRotationOffset.z));

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
        else
        {
            _internalHolder = existing;
        }
    }

    // Clears both the scatter instances AND any old combined bake, so runs don't stack up
    private void ClearAllGenerated()
    {
        if (_internalHolder != null)
        {
            for (int i = _internalHolder.childCount - 1; i >= 0; i--)
                DestroyImmediate(_internalHolder.GetChild(i).gameObject);
        }

        // Destroy any previous bake — without this, every Scatter() call adds another Combined_Bake child
        Transform oldBake = transform.Find("Combined_Bake");
        if (oldBake != null) DestroyImmediate(oldBake.gameObject);
    }

    public void CombineGeneratedMeshes()
    {
        if (_internalHolder == null || _internalHolder.childCount == 0) return;

        GameObject combinedHolder = new GameObject("Combined_Bake");
        combinedHolder.transform.SetParent(this.transform);
        combinedHolder.transform.localPosition = Vector3.zero;

        var renderers     = _internalHolder.GetComponentsInChildren<MeshRenderer>();
        var materialGroups = renderers.GroupBy(r => r.sharedMaterial);

        foreach (var group in materialGroups)
        {
            List<CombineInstance> combineList = new();

            // Create the combined object first so we can compute matrices relative to it
            GameObject combinedObj = new GameObject($"Combined_{group.Key.name}");
            combinedObj.transform.SetParent(combinedHolder.transform);
            combinedObj.transform.localPosition = Vector3.zero;

            // worldToLocalMatrix converts each renderer's world-space transform into the
            // combined object's local space, so vertices land in the right place after
            // the combined object's own transform is applied at render time
            Matrix4x4 worldToLocal = combinedObj.transform.worldToLocalMatrix;

            foreach (var renderer in group)
            {
                MeshFilter mf = renderer.GetComponent<MeshFilter>();

                // sharedMesh — never .mesh in edit mode; .mesh would create a leaked copy
                if (mf == null || mf.sharedMesh == null) continue;

                combineList.Add(new CombineInstance
                {
                    mesh      = mf.sharedMesh,
                    transform = worldToLocal * renderer.transform.localToWorldMatrix
                });
            }

            MeshFilter   meshFilter   = combinedObj.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = combinedObj.AddComponent<MeshRenderer>();

            Mesh combinedMesh = new Mesh
            {
                name        = $"Mesh_{group.Key.name}",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
            };
            combinedMesh.CombineMeshes(combineList.ToArray(), true, true);

            // sharedMesh — assign the asset reference, not a per-instance copy
            meshFilter.sharedMesh      = combinedMesh;
            meshRenderer.sharedMaterial = group.Key;
        }

        // The individual instances are no longer needed now that we have the combined mesh —
        // destroy them entirely instead of just disabling, which would still use memory
        for (int i = _internalHolder.childCount - 1; i >= 0; i--)
            DestroyImmediate(_internalHolder.GetChild(i).gameObject);
    }
}

*/
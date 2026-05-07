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
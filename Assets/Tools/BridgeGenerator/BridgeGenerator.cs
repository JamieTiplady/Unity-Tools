using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class BridgeGenerator : MonoBehaviour
{
    private const float MinDistance = 0.01f;
    private const float MinLoopStep = 0.1f;
    private const float PillarEndTolerance = 0.05f;
    private const float SqrEpsilon = 0.000001f;

    [System.Serializable]
    public class ReferenceSettings
    {
        public SplineContainer splineContainer;
        public List<GameObject> plankPrefabs = new List<GameObject>();
        public GameObject stringerPrefab;
        public GameObject pillarPrefab;
    }

    [System.Serializable]
    public class PlankSettings
    {
        public int seed = 12345;
        [Min(0.01f)] public float width = 0.2f;
        [Min(0f)] public float spacing = 0.05f;
        [Min(0.1f)] public float bridgeWidth = 2.0f;
        [Range(0f, 5f)] public float randomRotation = 2.0f;
    }

    [System.Serializable]
    public class StringerSettings
    {
        [Min(0.1f)] public float length = 2.0f;
        public float yOffset = 0.1f;
        [Min(0f)] public float inset = 0.2f;
        [Min(0f)] public float centerBeamThreshold = 2.5f;
    }

    public enum PivotLocation
    {
        Top,
        Center,
        Bottom
    }

    [System.Serializable]
    public class PillarSettings
    {
        [Min(0.1f)] public float spacing = 5.0f;
        [Min(1.0f)] public float maxHeight = 50.0f;
        [Min(0f)] public float inset = 0.3f;
        [Min(0f)] public float raycastOffset = 0.5f;
        public LayerMask groundMask = ~0;
        public bool onEdges = true;
        public PivotLocation pivot = PivotLocation.Center;
    }

    private readonly struct SplineFrame
    {
        public readonly Vector3 Position;
        public readonly Vector3 Tangent;
        public readonly Vector3 Up;
        public readonly Vector3 Right;
        public readonly Quaternion Rotation;

        public SplineFrame(Vector3 position, Vector3 tangent, Vector3 up, Vector3 right, Quaternion rotation)
        {
            Position = position;
            Tangent = tangent;
            Up = up;
            Right = right;
            Rotation = rotation;
        }
    }

    [Header("Orientation Settings")]
    public bool lockWorldUp = true;

    [Header("Optimisation")]
    public bool staticBatchInPlayMode = true;

    public ReferenceSettings refs = new ReferenceSettings();
    public PlankSettings planks = new PlankSettings();
    public StringerSettings stringers = new StringerSettings();
    public PillarSettings pillars = new PillarSettings();

    private bool _rebuildPending;

    private SplineContainer SplineContainer => refs != null ? refs.splineContainer : null;

    private void OnValidate()
    {
        EnsureSettings();
        RequestRebuild();
    }

    private void OnEnable()
    {
        EnsureSettings();
        Spline.Changed += OnSplineChanged;
    }

    private void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;
    }

    private void OnSplineChanged(Spline changedSpline, int knotIndex, SplineModification modification)
    {
        SplineContainer container = SplineContainer;
        if (container != null && changedSpline == container.Spline)
        {
            RequestRebuild();
        }
    }

    private void RequestRebuild()
    {
#if UNITY_EDITOR
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        EditorApplication.delayCall -= DelayedRebuild;
        EditorApplication.delayCall += DelayedRebuild;
#else
        RebuildBridge();
#endif
    }

#if UNITY_EDITOR
    private void DelayedRebuild()
    {
        EditorApplication.delayCall -= DelayedRebuild;
        _rebuildPending = false;

        if (this != null)
        {
            RebuildBridge();
        }
    }
#endif

    public void RebuildBridge()
    {
        EnsureSettings();
        ClearBridge();

        SplineContainer container = SplineContainer;
        if (container == null)
        {
            return;
        }

        Spline spline = container.Spline;
        float totalLength = spline.GetLength();
        if (totalLength <= MinDistance)
        {
            return;
        }

        Transform splineTransform = container.transform;
        float plankStep = Mathf.Max(MinDistance, planks.width + planks.spacing);
        int plankCount = Mathf.FloorToInt(totalLength / plankStep);
        float bridgeEnd = plankCount > 0 ? Mathf.Min(totalLength, (plankCount - 1) * plankStep) : totalLength;

        GenerateStringers(spline, splineTransform, totalLength, bridgeEnd);
        GeneratePillars(spline, splineTransform, totalLength, bridgeEnd);
        GeneratePlanks(spline, splineTransform, totalLength, plankCount, plankStep);

        if (Application.isPlaying && staticBatchInPlayMode)
        {
            StaticBatchingUtility.Combine(gameObject);
        }
    }

    private void GenerateStringers(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.stringerPrefab == null || maxDistance <= 0f)
        {
            return;
        }

        bool useCenterBeam = planks.bridgeWidth >= stringers.centerBeamThreshold;
        float segmentLength = Mathf.Max(MinLoopStep, stringers.length);
        float sideOffset = GetSideOffset(stringers.inset);

        for (float distance = 0f; distance <= maxDistance - segmentLength * 0.2f; distance += segmentLength)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 verticalOffset = -frame.Up * stringers.yOffset;
            Spawn(refs.stringerPrefab, frame.Position - frame.Right * sideOffset + verticalOffset, frame.Rotation);
            Spawn(refs.stringerPrefab, frame.Position + frame.Right * sideOffset + verticalOffset, frame.Rotation);

            if (useCenterBeam)
            {
                Spawn(refs.stringerPrefab, frame.Position + verticalOffset, frame.Rotation);
            }
        }
    }

    private void GeneratePlanks(Spline spline, Transform splineTransform, float totalLength, int count, float stepSize)
    {
        if (refs.plankPrefabs == null || refs.plankPrefabs.Count == 0 || count <= 0)
        {
            return;
        }

        var random = new Unity.Mathematics.Random(GetRandomSeed());
        float randomRotation = Mathf.Max(0f, planks.randomRotation);

        for (int i = 0; i < count; i++)
        {
            if (!TryGetFrame(spline, splineTransform, i * stepSize, totalLength, out SplineFrame frame))
            {
                continue;
            }

            GameObject prefab = refs.plankPrefabs[random.NextInt(0, refs.plankPrefabs.Count)];
            if (prefab == null)
            {
                continue;
            }

            GameObject plank = Spawn(prefab, frame.Position, frame.Rotation);
            if (randomRotation > 0f)
            {
                plank.transform.Rotate(frame.Up, random.NextFloat(-randomRotation, randomRotation), Space.World);
            }
        }
    }

    private void GeneratePillars(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.pillarPrefab == null || maxDistance < 0f)
        {
            return;
        }

        float spacing = Mathf.Max(MinLoopStep, pillars.spacing);
        float sideOffset = GetSideOffset(pillars.inset);

        for (float distance = 0f; distance <= maxDistance + PillarEndTolerance; distance += spacing)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 rayOrigin = frame.Position - Vector3.up * pillars.raycastOffset;
            Vector3 right = GetRight(Vector3.up, frame.Tangent, frame.Right);

            if (pillars.onEdges)
            {
                SpawnPillar(frame.Position + right * sideOffset, rayOrigin + right * sideOffset);
                SpawnPillar(frame.Position - right * sideOffset, rayOrigin - right * sideOffset);
            }
            else
            {
                SpawnPillar(frame.Position, rayOrigin);
            }
        }
    }

    private void SpawnPillar(Vector3 bridgePosition, Vector3 rayOrigin)
    {
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, pillars.maxHeight, pillars.groundMask, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        float distanceToGround = Vector3.Distance(bridgePosition, hit.point);
        if (distanceToGround <= MinDistance || distanceToGround >= pillars.maxHeight)
        {
            return;
        }

        Spawn(refs.pillarPrefab, GetPillarSpawnPosition(bridgePosition, hit.point), refs.pillarPrefab.transform.rotation);
    }

    private Vector3 GetPillarSpawnPosition(Vector3 bridgePosition, Vector3 groundPosition)
    {
        switch (pillars.pivot)
        {
            case PivotLocation.Top:
                return bridgePosition;
            case PivotLocation.Center:
                return Vector3.Lerp(bridgePosition, groundPosition, 0.5f);
            case PivotLocation.Bottom:
                return groundPosition;
            default:
                return bridgePosition;
        }
    }

    private bool TryGetFrame(Spline spline, Transform splineTransform, float distance, float totalLength, out SplineFrame frame)
    {
        frame = default;

        if (spline == null || splineTransform == null || totalLength <= MinDistance)
        {
            return false;
        }

        float t = Mathf.Clamp01(distance / totalLength);
        spline.Evaluate(t, out float3 localPosition, out float3 localTangent, out float3 localUp);

        Vector3 position = splineTransform.TransformPoint(localPosition);
        Vector3 tangent = SafeNormalize(splineTransform.TransformDirection(localTangent), splineTransform.forward);
        Vector3 up = lockWorldUp ? Vector3.up : SafeNormalize(splineTransform.TransformDirection(localUp), Vector3.up);
        Vector3 right = GetRight(up, tangent, splineTransform.right);
        Vector3 rotationUp = SafeNormalize(Vector3.Cross(tangent, right), up);
        Quaternion rotation = Quaternion.LookRotation(tangent, rotationUp);

        frame = new SplineFrame(position, tangent, up, right, rotation);
        return true;
    }

    private GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        return Instantiate(prefab, position, rotation, transform);
    }

    private float GetSideOffset(float inset)
    {
        return Mathf.Max(0f, planks.bridgeWidth * 0.5f - inset);
    }

    private uint GetRandomSeed()
    {
        Vector3 position = transform.position;
        uint seed = math.hash(new int4(
            planks.seed,
            Mathf.RoundToInt(position.x * 1000f),
            Mathf.RoundToInt(position.y * 1000f),
            Mathf.RoundToInt(position.z * 1000f)));

        return seed == 0u ? 1u : seed;
    }

    private static Vector3 GetRight(Vector3 up, Vector3 tangent, Vector3 fallback)
    {
        Vector3 right = Vector3.Cross(up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.forward, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        return SafeNormalize(fallback, Vector3.right);
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        if (value.sqrMagnitude > SqrEpsilon)
        {
            return value.normalized;
        }

        if (fallback.sqrMagnitude > SqrEpsilon)
        {
            return fallback.normalized;
        }

        return Vector3.forward;
    }

    private void EnsureSettings()
    {
        if (refs == null)
        {
            refs = new ReferenceSettings();
        }

        if (refs.plankPrefabs == null)
        {
            refs.plankPrefabs = new List<GameObject>();
        }

        if (planks == null)
        {
            planks = new PlankSettings();
        }

        if (stringers == null)
        {
            stringers = new StringerSettings();
        }

        if (pillars == null)
        {
            pillars = new PillarSettings();
        }
    }

    private void ClearBridge()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;

            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }
    }
}





/*

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class BridgeGenerator : MonoBehaviour
{
    private const float MinDistance = 0.01f;
    private const float MinLoopStep = 0.1f;
    private const float PillarEndTolerance = 0.05f;
    private const float SqrEpsilon = 0.000001f;

    [System.Serializable]
    public class ReferenceSettings
    {
        public SplineContainer splineContainer;
        public List<GameObject> plankPrefabs = new List<GameObject>();
        public GameObject stringerPrefab;
        public GameObject pillarPrefab;
    }

    [System.Serializable]
    public class PlankSettings
    {
        public int seed = 12345;
        [Min(0.01f)] public float width = 0.2f;
        [Min(0f)] public float spacing = 0.05f;
        [Min(0.1f)] public float bridgeWidth = 2.0f;
        [Range(0f, 5f)] public float randomRotation = 2.0f;
    }

    [System.Serializable]
    public class StringerSettings
    {
        [Min(0.1f)] public float length = 2.0f;
        public float yOffset = 0.1f;
        [Min(0f)] public float inset = 0.2f;
        [Min(0f)] public float centerBeamThreshold = 2.5f;
    }

    public enum PivotLocation
    {
        Top,
        Center,
        Bottom
    }

    [System.Serializable]
    public class PillarSettings
    {
        [Min(0.1f)] public float spacing = 5.0f;
        [Min(1.0f)] public float maxHeight = 50.0f;
        [Min(0f)] public float inset = 0.3f;
        [Min(0f)] public float raycastOffset = 0.5f;
        [Min(0.01f)] public float meshNativeHeight = 1.0f;
        public LayerMask groundMask = ~0;
        public bool onEdges = true;
        public PivotLocation pivot = PivotLocation.Center;
    }

    private readonly struct SplineFrame
    {
        public readonly Vector3 Position;
        public readonly Vector3 Tangent;
        public readonly Vector3 Up;
        public readonly Vector3 Right;
        public readonly Quaternion Rotation;

        public SplineFrame(Vector3 position, Vector3 tangent, Vector3 up, Vector3 right, Quaternion rotation)
        {
            Position = position;
            Tangent = tangent;
            Up = up;
            Right = right;
            Rotation = rotation;
        }
    }

    [Header("Orientation Settings")]
    public bool lockWorldUp = true;

    [Header("Optimisation")]
    public bool staticBatchInPlayMode = true;

    public ReferenceSettings refs = new ReferenceSettings();
    public PlankSettings planks = new PlankSettings();
    public StringerSettings stringers = new StringerSettings();
    public PillarSettings pillars = new PillarSettings();

    private bool _rebuildPending;

    private SplineContainer SplineContainer => refs != null ? refs.splineContainer : null;

    private void OnValidate()
    {
        EnsureSettings();
        RequestRebuild();
    }

    private void OnEnable()
    {
        EnsureSettings();
        Spline.Changed += OnSplineChanged;
    }

    private void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;
    }

    private void OnSplineChanged(Spline changedSpline, int knotIndex, SplineModification modification)
    {
        SplineContainer container = SplineContainer;
        if (container != null && changedSpline == container.Spline)
        {
            RequestRebuild();
        }
    }

    private void RequestRebuild()
    {
#if UNITY_EDITOR
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        EditorApplication.delayCall -= DelayedRebuild;
        EditorApplication.delayCall += DelayedRebuild;
#else
        RebuildBridge();
#endif
    }

#if UNITY_EDITOR
    private void DelayedRebuild()
    {
        EditorApplication.delayCall -= DelayedRebuild;
        _rebuildPending = false;

        if (this != null)
        {
            RebuildBridge();
        }
    }
#endif

    public void RebuildBridge()
    {
        EnsureSettings();
        ClearBridge();

        SplineContainer container = SplineContainer;
        if (container == null)
        {
            return;
        }

        Spline spline = container.Spline;
        float totalLength = spline.GetLength();
        if (totalLength <= MinDistance)
        {
            return;
        }

        Transform splineTransform = container.transform;
        float plankStep = Mathf.Max(MinDistance, planks.width + planks.spacing);
        int plankCount = Mathf.FloorToInt(totalLength / plankStep);
        float bridgeEnd = plankCount > 0 ? Mathf.Min(totalLength, (plankCount - 1) * plankStep) : totalLength;

        GenerateStringers(spline, splineTransform, totalLength, bridgeEnd);
        GeneratePillars(spline, splineTransform, totalLength, bridgeEnd);
        GeneratePlanks(spline, splineTransform, totalLength, plankCount, plankStep);

        if (Application.isPlaying && staticBatchInPlayMode)
        {
            StaticBatchingUtility.Combine(gameObject);
        }
    }

    private void GenerateStringers(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.stringerPrefab == null || maxDistance <= 0f)
        {
            return;
        }

        bool useCenterBeam = planks.bridgeWidth >= stringers.centerBeamThreshold;
        float segmentLength = Mathf.Max(MinLoopStep, stringers.length);
        float sideOffset = GetSideOffset(stringers.inset);

        for (float distance = 0f; distance <= maxDistance - segmentLength * 0.2f; distance += segmentLength)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 verticalOffset = -frame.Up * stringers.yOffset;
            Spawn(refs.stringerPrefab, frame.Position - frame.Right * sideOffset + verticalOffset, frame.Rotation);
            Spawn(refs.stringerPrefab, frame.Position + frame.Right * sideOffset + verticalOffset, frame.Rotation);

            if (useCenterBeam)
            {
                Spawn(refs.stringerPrefab, frame.Position + verticalOffset, frame.Rotation);
            }
        }
    }

    private void GeneratePlanks(Spline spline, Transform splineTransform, float totalLength, int count, float stepSize)
    {
        if (refs.plankPrefabs == null || refs.plankPrefabs.Count == 0 || count <= 0)
        {
            return;
        }

        var random = new Unity.Mathematics.Random(GetRandomSeed());
        float randomRotation = Mathf.Max(0f, planks.randomRotation);

        for (int i = 0; i < count; i++)
        {
            if (!TryGetFrame(spline, splineTransform, i * stepSize, totalLength, out SplineFrame frame))
            {
                continue;
            }

            GameObject prefab = refs.plankPrefabs[random.NextInt(0, refs.plankPrefabs.Count)];
            if (prefab == null)
            {
                continue;
            }

            GameObject plank = Spawn(prefab, frame.Position, frame.Rotation);
            if (randomRotation > 0f)
            {
                plank.transform.Rotate(frame.Up, random.NextFloat(-randomRotation, randomRotation), Space.World);
            }
        }
    }

    private void GeneratePillars(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.pillarPrefab == null || maxDistance < 0f)
        {
            return;
        }

        float spacing = Mathf.Max(MinLoopStep, pillars.spacing);
        float sideOffset = GetSideOffset(pillars.inset);

        for (float distance = 0f; distance <= maxDistance + PillarEndTolerance; distance += spacing)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 rayOrigin = frame.Position - Vector3.up * pillars.raycastOffset;
            Vector3 right = GetRight(Vector3.up, frame.Tangent, frame.Right);

            if (pillars.onEdges)
            {
                SpawnPillar(frame.Position + right * sideOffset, rayOrigin + right * sideOffset);
                SpawnPillar(frame.Position - right * sideOffset, rayOrigin - right * sideOffset);
            }
            else
            {
                SpawnPillar(frame.Position, rayOrigin);
            }
        }
    }

    private void SpawnPillar(Vector3 bridgePosition, Vector3 rayOrigin)
    {
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, pillars.maxHeight, pillars.groundMask, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        float height = Vector3.Distance(bridgePosition, hit.point);
        if (height <= MinDistance)
        {
            return;
        }

        float nativeHeight = Mathf.Max(MinDistance, pillars.meshNativeHeight);
        GameObject pillar = Spawn(refs.pillarPrefab, bridgePosition, Quaternion.identity);
        Vector3 prefabScale = refs.pillarPrefab.transform.localScale;
        pillar.transform.localScale = new Vector3(prefabScale.x, height / nativeHeight, prefabScale.z);

        switch (pillars.pivot)
        {
            case PivotLocation.Top:
                pillar.transform.position = bridgePosition;
                break;
            case PivotLocation.Center:
                pillar.transform.position = Vector3.Lerp(bridgePosition, hit.point, 0.5f);
                break;
            case PivotLocation.Bottom:
                pillar.transform.position = hit.point;
                break;
        }
    }

    private bool TryGetFrame(Spline spline, Transform splineTransform, float distance, float totalLength, out SplineFrame frame)
    {
        frame = default;

        if (spline == null || splineTransform == null || totalLength <= MinDistance)
        {
            return false;
        }

        float t = Mathf.Clamp01(distance / totalLength);
        spline.Evaluate(t, out float3 localPosition, out float3 localTangent, out float3 localUp);

        Vector3 position = splineTransform.TransformPoint(localPosition);
        Vector3 tangent = SafeNormalize(splineTransform.TransformDirection(localTangent), splineTransform.forward);
        Vector3 up = lockWorldUp ? Vector3.up : SafeNormalize(splineTransform.TransformDirection(localUp), Vector3.up);
        Vector3 right = GetRight(up, tangent, splineTransform.right);
        Vector3 rotationUp = SafeNormalize(Vector3.Cross(tangent, right), up);
        Quaternion rotation = Quaternion.LookRotation(tangent, rotationUp);

        frame = new SplineFrame(position, tangent, up, right, rotation);
        return true;
    }

    private GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        return Instantiate(prefab, position, rotation, transform);
    }

    private float GetSideOffset(float inset)
    {
        return Mathf.Max(0f, planks.bridgeWidth * 0.5f - inset);
    }

    private uint GetRandomSeed()
    {
        Vector3 position = transform.position;
        uint seed = math.hash(new int4(
            planks.seed,
            Mathf.RoundToInt(position.x * 1000f),
            Mathf.RoundToInt(position.y * 1000f),
            Mathf.RoundToInt(position.z * 1000f)));

        return seed == 0u ? 1u : seed;
    }

    private static Vector3 GetRight(Vector3 up, Vector3 tangent, Vector3 fallback)
    {
        Vector3 right = Vector3.Cross(up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.forward, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        return SafeNormalize(fallback, Vector3.right);
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        if (value.sqrMagnitude > SqrEpsilon)
        {
            return value.normalized;
        }

        if (fallback.sqrMagnitude > SqrEpsilon)
        {
            return fallback.normalized;
        }

        return Vector3.forward;
    }

    private void EnsureSettings()
    {
        if (refs == null)
        {
            refs = new ReferenceSettings();
        }

        if (refs.plankPrefabs == null)
        {
            refs.plankPrefabs = new List<GameObject>();
        }

        if (planks == null)
        {
            planks = new PlankSettings();
        }

        if (stringers == null)
        {
            stringers = new StringerSettings();
        }

        if (pillars == null)
        {
            pillars = new PillarSettings();
        }
    }

    private void ClearBridge()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;

            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }
    }
}


*/



///////////////////////////////////////////////////////


/*


using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class BridgeGenerator : MonoBehaviour
{
    private const float MinDistance = 0.01f;
    private const float MinLoopStep = 0.1f;
    private const float PillarEndTolerance = 0.05f;
    private const float SqrEpsilon = 0.000001f;

    [System.Serializable]
    public class ReferenceSettings
    {
        public SplineContainer splineContainer;
        public List<GameObject> plankPrefabs = new List<GameObject>();
        public GameObject railingPrefab;
        public GameObject lightPolePrefab;
        public GameObject stringerPrefab;
        public GameObject pillarPrefab;
    }

    [System.Serializable]
    public class PlankSettings
    {
        public int seed = 12345;
        [Min(0.01f)] public float width = 0.2f;
        [Min(0f)] public float spacing = 0.05f;
        [Min(0.1f)] public float bridgeWidth = 2.0f;
        [Range(0f, 5f)] public float randomRotation = 2.0f;
    }

    [System.Serializable]
    public class StringerSettings
    {
        [Min(0.1f)] public float length = 2.0f;
        public float yOffset = 0.1f;
        [Min(0f)] public float inset = 0.2f;
        [Min(0f)] public float centerBeamThreshold = 2.5f;
    }

    [System.Serializable]
    public class RailingSettings
    {
        [Min(0.1f)] public float length = 1.0f;
        [Min(0f)] public float spacing = 0.1f;
        [Min(0.01f)] public float lightPoleWidth = 0.2f;
        [Min(1)] public int lightEveryNth = 3;
        [Min(0f)] public float edgeOffset = 0.1f;
    }

    public enum PivotLocation
    {
        Top,
        Center,
        Bottom
    }

    [System.Serializable]
    public class PillarSettings
    {
        [Min(0.1f)] public float spacing = 5.0f;
        [Min(1.0f)] public float maxHeight = 50.0f;
        [Min(0f)] public float inset = 0.3f;
        [Min(0f)] public float raycastOffset = 0.5f;
        [Min(0.01f)] public float meshNativeHeight = 1.0f;
        public LayerMask groundMask = ~0;
        public bool onEdges = true;
        public PivotLocation pivot = PivotLocation.Center;
    }

    private readonly struct SplineFrame
    {
        public readonly Vector3 Position;
        public readonly Vector3 Tangent;
        public readonly Vector3 Up;
        public readonly Vector3 Right;
        public readonly Quaternion Rotation;

        public SplineFrame(Vector3 position, Vector3 tangent, Vector3 up, Vector3 right, Quaternion rotation)
        {
            Position = position;
            Tangent = tangent;
            Up = up;
            Right = right;
            Rotation = rotation;
        }
    }

    [Header("Orientation Settings")]
    public bool lockWorldUp = true;

    [Header("Optimisation")]
    public bool staticBatchInPlayMode = true;

    public ReferenceSettings refs = new ReferenceSettings();
    public PlankSettings planks = new PlankSettings();
    public StringerSettings stringers = new StringerSettings();
    public RailingSettings railings = new RailingSettings();
    public PillarSettings pillars = new PillarSettings();

    private bool _rebuildPending;

    private SplineContainer SplineContainer => refs != null ? refs.splineContainer : null;

    private void OnValidate()
    {
        EnsureSettings();
        RequestRebuild();
    }

    private void OnEnable()
    {
        EnsureSettings();
        Spline.Changed += OnSplineChanged;
    }

    private void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;
    }

    private void OnSplineChanged(Spline changedSpline, int knotIndex, SplineModification modification)
    {
        SplineContainer container = SplineContainer;
        if (container != null && changedSpline == container.Spline)
        {
            RequestRebuild();
        }
    }

    private void RequestRebuild()
    {
#if UNITY_EDITOR
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        EditorApplication.delayCall -= DelayedRebuild;
        EditorApplication.delayCall += DelayedRebuild;
#else
        RebuildBridge();
#endif
    }

#if UNITY_EDITOR
    private void DelayedRebuild()
    {
        EditorApplication.delayCall -= DelayedRebuild;
        _rebuildPending = false;

        if (this != null)
        {
            RebuildBridge();
        }
    }
#endif

    public void RebuildBridge()
    {
        EnsureSettings();
        ClearBridge();

        SplineContainer container = SplineContainer;
        if (container == null)
        {
            return;
        }

        Spline spline = container.Spline;
        float totalLength = spline.GetLength();
        if (totalLength <= MinDistance)
        {
            return;
        }

        Transform splineTransform = container.transform;
        float plankStep = Mathf.Max(MinDistance, planks.width + planks.spacing);
        int plankCount = Mathf.FloorToInt(totalLength / plankStep);
        float bridgeEnd = plankCount > 0 ? Mathf.Min(totalLength, (plankCount - 1) * plankStep) : totalLength;

        GenerateStringers(spline, splineTransform, totalLength, bridgeEnd);
        GeneratePillars(spline, splineTransform, totalLength, bridgeEnd);
        GeneratePlanks(spline, splineTransform, totalLength, plankCount, plankStep);
        GenerateRailingsAndLights(spline, splineTransform, totalLength, bridgeEnd);

        if (Application.isPlaying && staticBatchInPlayMode)
        {
            StaticBatchingUtility.Combine(gameObject);
        }
    }

    private void GenerateStringers(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.stringerPrefab == null || maxDistance <= 0f)
        {
            return;
        }

        bool useCenterBeam = planks.bridgeWidth >= stringers.centerBeamThreshold;
        float segmentLength = Mathf.Max(MinLoopStep, stringers.length);
        float sideOffset = GetSideOffset(stringers.inset);

        for (float distance = 0f; distance <= maxDistance - segmentLength * 0.2f; distance += segmentLength)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 verticalOffset = -frame.Up * stringers.yOffset;
            Spawn(refs.stringerPrefab, frame.Position - frame.Right * sideOffset + verticalOffset, frame.Rotation);
            Spawn(refs.stringerPrefab, frame.Position + frame.Right * sideOffset + verticalOffset, frame.Rotation);

            if (useCenterBeam)
            {
                Spawn(refs.stringerPrefab, frame.Position + verticalOffset, frame.Rotation);
            }
        }
    }

    private void GeneratePlanks(Spline spline, Transform splineTransform, float totalLength, int count, float stepSize)
    {
        if (refs.plankPrefabs == null || refs.plankPrefabs.Count == 0 || count <= 0)
        {
            return;
        }

        var random = new Unity.Mathematics.Random(GetRandomSeed());
        float randomRotation = Mathf.Max(0f, planks.randomRotation);

        for (int i = 0; i < count; i++)
        {
            if (!TryGetFrame(spline, splineTransform, i * stepSize, totalLength, out SplineFrame frame))
            {
                continue;
            }

            GameObject prefab = refs.plankPrefabs[random.NextInt(0, refs.plankPrefabs.Count)];
            if (prefab == null)
            {
                continue;
            }

            GameObject plank = Spawn(prefab, frame.Position, frame.Rotation);
            if (randomRotation > 0f)
            {
                plank.transform.Rotate(frame.Up, random.NextFloat(-randomRotation, randomRotation), Space.World);
            }
        }
    }

    private void GenerateRailingsAndLights(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.railingPrefab == null || maxDistance <= 0f)
        {
            return;
        }

        float railingLength = Mathf.Max(MinLoopStep, railings.length);
        float spacing = Mathf.Max(MinDistance, railings.spacing);
        float lightPoleWidth = Mathf.Max(MinDistance, railings.lightPoleWidth);
        float sideOffset = GetSideOffset(railings.edgeOffset);
        int lightEveryNth = Mathf.Max(1, railings.lightEveryNth);
        int railingCounter = 0;

        for (float distance = 0f; distance <= maxDistance - railingLength;)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                distance += railingLength + spacing;
                continue;
            }

            Spawn(refs.railingPrefab, frame.Position + frame.Right * sideOffset, frame.Rotation);
            Spawn(refs.railingPrefab, frame.Position - frame.Right * sideOffset, frame.Rotation);

            distance += railingLength;
            railingCounter++;

            bool shouldSpawnLight = refs.lightPolePrefab != null && railingCounter % lightEveryNth == 0;
            if (!shouldSpawnLight)
            {
                distance += spacing;
                continue;
            }

            float lightDistance = distance + spacing;
            if (lightDistance <= maxDistance && TryGetFrame(spline, splineTransform, lightDistance, totalLength, out SplineFrame lightFrame))
            {
                Spawn(refs.lightPolePrefab, lightFrame.Position + lightFrame.Right * sideOffset, lightFrame.Rotation);
                Spawn(refs.lightPolePrefab, lightFrame.Position - lightFrame.Right * sideOffset, lightFrame.Rotation);
            }

            distance = lightDistance + lightPoleWidth + spacing;
        }
    }

    private void GeneratePillars(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.pillarPrefab == null || maxDistance < 0f)
        {
            return;
        }

        float spacing = Mathf.Max(MinLoopStep, pillars.spacing);
        float sideOffset = GetSideOffset(pillars.inset);

        for (float distance = 0f; distance <= maxDistance + PillarEndTolerance; distance += spacing)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 rayOrigin = frame.Position - Vector3.up * pillars.raycastOffset;
            Vector3 right = GetRight(Vector3.up, frame.Tangent, frame.Right);

            if (pillars.onEdges)
            {
                SpawnPillar(frame.Position + right * sideOffset, rayOrigin + right * sideOffset);
                SpawnPillar(frame.Position - right * sideOffset, rayOrigin - right * sideOffset);
            }
            else
            {
                SpawnPillar(frame.Position, rayOrigin);
            }
        }
    }

    private void SpawnPillar(Vector3 bridgePosition, Vector3 rayOrigin)
    {
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, pillars.maxHeight, pillars.groundMask, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        float height = Vector3.Distance(bridgePosition, hit.point);
        if (height <= MinDistance)
        {
            return;
        }

        float nativeHeight = Mathf.Max(MinDistance, pillars.meshNativeHeight);
        GameObject pillar = Spawn(refs.pillarPrefab, bridgePosition, Quaternion.identity);
        Vector3 prefabScale = refs.pillarPrefab.transform.localScale;
        pillar.transform.localScale = new Vector3(prefabScale.x, height / nativeHeight, prefabScale.z);

        switch (pillars.pivot)
        {
            case PivotLocation.Top:
                pillar.transform.position = bridgePosition;
                break;
            case PivotLocation.Center:
                pillar.transform.position = Vector3.Lerp(bridgePosition, hit.point, 0.5f);
                break;
            case PivotLocation.Bottom:
                pillar.transform.position = hit.point;
                break;
        }
    }

    private bool TryGetFrame(Spline spline, Transform splineTransform, float distance, float totalLength, out SplineFrame frame)
    {
        frame = default;

        if (spline == null || splineTransform == null || totalLength <= MinDistance)
        {
            return false;
        }

        float t = Mathf.Clamp01(distance / totalLength);
        spline.Evaluate(t, out float3 localPosition, out float3 localTangent, out float3 localUp);

        Vector3 position = splineTransform.TransformPoint(localPosition);
        Vector3 tangent = SafeNormalize(splineTransform.TransformDirection(localTangent), splineTransform.forward);
        Vector3 up = lockWorldUp ? Vector3.up : SafeNormalize(splineTransform.TransformDirection(localUp), Vector3.up);
        Vector3 right = GetRight(up, tangent, splineTransform.right);
        Vector3 rotationUp = SafeNormalize(Vector3.Cross(tangent, right), up);
        Quaternion rotation = Quaternion.LookRotation(tangent, rotationUp);

        frame = new SplineFrame(position, tangent, up, right, rotation);
        return true;
    }

    private GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        return Instantiate(prefab, position, rotation, transform);
    }

    private float GetSideOffset(float inset)
    {
        return Mathf.Max(0f, planks.bridgeWidth * 0.5f - inset);
    }

    private uint GetRandomSeed()
    {
        Vector3 position = transform.position;
        uint seed = math.hash(new int4(
            planks.seed,
            Mathf.RoundToInt(position.x * 1000f),
            Mathf.RoundToInt(position.y * 1000f),
            Mathf.RoundToInt(position.z * 1000f)));

        return seed == 0u ? 1u : seed;
    }

    private static Vector3 GetRight(Vector3 up, Vector3 tangent, Vector3 fallback)
    {
        Vector3 right = Vector3.Cross(up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.forward, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        return SafeNormalize(fallback, Vector3.right);
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        if (value.sqrMagnitude > SqrEpsilon)
        {
            return value.normalized;
        }

        if (fallback.sqrMagnitude > SqrEpsilon)
        {
            return fallback.normalized;
        }

        return Vector3.forward;
    }

    private void EnsureSettings()
    {
        if (refs == null)
        {
            refs = new ReferenceSettings();
        }

        if (refs.plankPrefabs == null)
        {
            refs.plankPrefabs = new List<GameObject>();
        }

        if (planks == null)
        {
            planks = new PlankSettings();
        }

        if (stringers == null)
        {
            stringers = new StringerSettings();
        }

        if (railings == null)
        {
            railings = new RailingSettings();
        }

        if (pillars == null)
        {
            pillars = new PillarSettings();
        }
    }

    private void ClearBridge()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;

            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }
    }
}


*/











//////////////////////////////
/// 
/// OG keeping as backup
///
///////////////////////////// 

/*
using UnityEngine;
using UnityEngine.Splines;
using System.Collections.Generic;
using Unity.Mathematics;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class BridgeGenerator : MonoBehaviour
{
    [System.Serializable]
    public class ReferenceSettings
    {
        public SplineContainer splineContainer;
        public List<GameObject> plankPrefabs;
        public GameObject railingPrefab;
        public GameObject lightPolePrefab;
        public GameObject stringerPrefab;
        public GameObject pillarPrefab;
    }

    [System.Serializable]
    public class PlankSettings
    {
        public int seed = 12345;
        [Min(0.01f)] public float width = 0.2f;
        public float spacing = 0.05f;
        public float bridgeWidth = 2.0f;
        [Range(0, 5f)] public float randomRotation = 2.0f;
    }

    [System.Serializable]
    public class StringerSettings
    {
        [Min(0.1f)] public float length = 2.0f;
        public float yOffset = 0.1f;
        public float inset = 0.2f;
        public float centerBeamThreshold = 2.5f;
    }

    [System.Serializable]
    public class RailingSettings
    {
        [Min(0.1f)] public float length = 1.0f;
        public float spacing = 0.1f;
        [Min(0.01f)] public float lightPoleWidth = 0.2f;
        [Min(1)] public int lightEveryNth = 3;
        public float edgeOffset = 0.1f;
    }

    

    public enum PivotLocation { Top, Center, Bottom }

    [System.Serializable]
    public class PillarSettings
    {
        [Min(0.1f)] public float spacing = 5.0f;
        [Min(1.0f)] public float maxHeight = 50.0f;
        public float inset = 0.3f;
        public float raycastOffset = 0.5f;
        public float meshNativeHeight = 1.0f;
        public LayerMask groundMask = ~0;
        public bool onEdges = true;
        public PivotLocation pivot = PivotLocation.Center;
    }

    [Header("Orientation Settings")]
    public bool lockWorldUp = true;

    public ReferenceSettings refs;
    public PlankSettings planks;
    public StringerSettings stringers;
    public RailingSettings railings;
    public PillarSettings pillars;

    private bool _rebuildPending = false;

    private void OnValidate() => RequestRebuild();
    private void OnEnable() => Spline.Changed += OnSplineChanged;
    private void OnDisable() => Spline.Changed -= OnSplineChanged;

    private void OnSplineChanged(Spline s, int k, SplineModification m)
    {
        if (refs.splineContainer != null && s == refs.splineContainer.Spline) RequestRebuild();
    }

    private void RequestRebuild()
    {
        if (_rebuildPending) return;
        #if UNITY_EDITOR
        _rebuildPending = true;
        EditorApplication.delayCall += () =>
        {
            _rebuildPending = false;
            if (this != null) RebuildBridge();
        };
        #else
        RebuildBridge();
        #endif
    }

    public void RebuildBridge()
    {
        if (this == null) return;
        ClearBridge();
        if (refs.splineContainer == null) return;

        var spline = refs.splineContainer.Spline;
        float totalLength = spline.GetLength();

        float plankStepSize = planks.width + planks.spacing;
        int plankCount = Mathf.FloorToInt(totalLength / plankStepSize);
        float trueBridgeEnd = (plankCount > 0) ? (plankCount - 1) * plankStepSize : totalLength;

        GenerateStringers(spline, trueBridgeEnd);
        GeneratePillars(spline, trueBridgeEnd);
        GeneratePlanks(spline, totalLength);
        GenerateRailingsAndLights(spline, trueBridgeEnd);

        StaticBatchingUtility.Combine(gameObject);
    }

    private void GenerateStringers(Spline spline, float maxAllowedLength)
    {
        if (refs.stringerPrefab == null) return;

        float currentDist = 0;
        bool useCenterBeam = planks.bridgeWidth >= stringers.centerBeamThreshold;
        float safeLength = Mathf.Max(0.1f, stringers.length);

        while (currentDist <= maxAllowedLength - (safeLength * 0.2f))
        {
            float t = currentDist / spline.GetLength();
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);

            Vector3 worldPos = refs.splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = refs.splineContainer.transform.TransformDirection(localTan);
            
            //uses a stable up, world up rather than local spline up
            Vector3 upDir = lockWorldUp ? Vector3.up : (Vector3)refs.splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, upDir);
            
            Vector3 right = Vector3.Cross(upDir, worldTan).normalized;
            float sideOffset = (planks.bridgeWidth / 2f) - stringers.inset;
            Vector3 verticalOffset = -upDir * stringers.yOffset;

            Instantiate(refs.stringerPrefab, worldPos - (right * sideOffset) + verticalOffset, rot, transform);
            Instantiate(refs.stringerPrefab, worldPos + (right * sideOffset) + verticalOffset, rot, transform);

            if (useCenterBeam) Instantiate(refs.stringerPrefab, worldPos + verticalOffset, rot, transform);

            currentDist += safeLength;
        }
    }

    private void GeneratePlanks(Spline spline, float totalLength)
    {
        if (refs.plankPrefabs == null || refs.plankPrefabs.Count == 0) return;

        uint internalSeed = (uint)planks.seed + (uint)transform.position.GetHashCode();
        var rnd = new Unity.Mathematics.Random(internalSeed == 0 ? 1 : internalSeed);

        float stepSize = planks.width + planks.spacing;
        int count = Mathf.FloorToInt(totalLength / stepSize);

        for (int i = 0; i < count; i++)
        {
            float dist = i * stepSize;
            float t = dist / totalLength;
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);

            Vector3 worldPos = refs.splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = refs.splineContainer.transform.TransformDirection(localTan);
            
            //uses a stable up, world up rather than local spline up
            Vector3 upDir = lockWorldUp ? Vector3.up : (Vector3)refs.splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, upDir);

            GameObject prefab = refs.plankPrefabs[rnd.NextInt(0, refs.plankPrefabs.Count)];
            GameObject plank = Instantiate(prefab, worldPos, rot, transform);
            //GameObjectUtility.SetStaticEditorFlags(plank, 0); // Editor only
            
            float randomRot = rnd.NextFloat(-planks.randomRotation, planks.randomRotation);
            plank.transform.Rotate(Vector3.up, randomRot);
        }
    }

    private void GenerateRailingsAndLights(Spline spline, float maxAllowedLength)
    {
        if (refs.railingPrefab == null) return;
        float currentDist = 0;
        int railingCounter = 0;
        float totalLength = spline.GetLength();

        while (currentDist <= maxAllowedLength - railings.length)
        {
            float t = currentDist / totalLength;
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);
            Vector3 worldPos = refs.splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = refs.splineContainer.transform.TransformDirection(localTan);
            
            //uses a stable up, world up rather than local spline up
            Vector3 upDir = lockWorldUp ? Vector3.up : (Vector3)refs.splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, upDir);
            Vector3 right = Vector3.Cross(upDir, worldTan).normalized;

            float sideOffset = (planks.bridgeWidth / 2f) - railings.edgeOffset;
            Instantiate(refs.railingPrefab, worldPos + (right * sideOffset), rot, transform);
            Instantiate(refs.railingPrefab, worldPos - (right * sideOffset), rot, transform);

            currentDist += railings.length;
            railingCounter++;

            if (refs.lightPolePrefab != null && railingCounter % railings.lightEveryNth == 0)
            {
                currentDist += railings.spacing;
                float pt = currentDist / totalLength;
                spline.Evaluate(pt, out float3 pLocalPos, out float3 pLocalTan, out float3 pLocalUp);
                Vector3 pWorldPos = refs.splineContainer.transform.TransformPoint(pLocalPos);
                Vector3 pWorldTan = refs.splineContainer.transform.TransformDirection(pLocalTan);
                
                Vector3 pUpDir = lockWorldUp ? Vector3.up : (Vector3)refs.splineContainer.transform.TransformDirection(pLocalUp);
                Quaternion pRot = Quaternion.LookRotation(pWorldTan, pUpDir);
                
                Instantiate(refs.lightPolePrefab, pWorldPos + (right * sideOffset), pRot, transform);
                Instantiate(refs.lightPolePrefab, pWorldPos - (right * sideOffset), pRot, transform);
                currentDist += railings.lightPoleWidth + railings.spacing;
            }
            else
            {
                currentDist += Mathf.Max(0.01f, railings.spacing);
            }
        }
    }

    private void GeneratePillars(Spline spline, float maxAllowedLength)
    {
        if (refs.pillarPrefab == null) return;

        float currentDist = 0;
        float safeSpacing = Mathf.Max(0.1f, pillars.spacing); 

        while (currentDist <= maxAllowedLength + 0.05f)
        {
            float t = currentDist / spline.GetLength();
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);

            Vector3 worldPos = refs.splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = refs.splineContainer.transform.TransformDirection(localTan);
            Vector3 trueRight = Vector3.Cross(Vector3.up, worldTan).normalized;

            Vector3 rayStartOffset = worldPos + (Vector3.down * pillars.raycastOffset);

            if (pillars.onEdges)
            {
                float sideOffset = (planks.bridgeWidth / 2f) - pillars.inset;
                SpawnPillar(worldPos + (trueRight * sideOffset), rayStartOffset + (trueRight * sideOffset));
                SpawnPillar(worldPos - (trueRight * sideOffset), rayStartOffset - (trueRight * sideOffset));
            }
            else
            {
                SpawnPillar(worldPos, rayStartOffset);
            }

            currentDist += safeSpacing;
        }
    }

    private void SpawnPillar(Vector3 bridgePos, Vector3 rayOrigin)
    {
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, pillars.maxHeight, pillars.groundMask))
        {
            float totalDistance = Vector3.Distance(bridgePos, hit.point);
            
            //Ensure can't divide by zero
            float nativeHeight = Mathf.Max(0.01f, pillars.meshNativeHeight);
            
            GameObject pillar = Instantiate(refs.pillarPrefab, transform);
            pillar.transform.rotation = Quaternion.identity;

            Vector3 originalScale = refs.pillarPrefab.transform.localScale;
            
            //Calculate using the safe height
            float finalYScale = totalDistance / nativeHeight;
            
            pillar.transform.localScale = new Vector3(originalScale.x, finalYScale, originalScale.z);

            switch (pillars.pivot)
            {
                case PivotLocation.Top: pillar.transform.position = bridgePos; break;
                case PivotLocation.Center: pillar.transform.position = bridgePos + (Vector3.down * (totalDistance / 2f)); break;
                case PivotLocation.Bottom: pillar.transform.position = hit.point; break;
            }
        }
    }

    private void ClearBridge()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child);
            else DestroyImmediate(child);
        }
    }
}

*/
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
    }

    public bool lockWorldUp = true;
    public ReferenceSettings refs;
    public PlankSettings planks;
    public StringerSettings stringers;
    public RailingSettings railings;
    public PillarSettings pillars;

    private void OnValidate() => RebuildBridge();
    private void OnEnable() => Spline.Changed += OnSplineChanged;
    private void OnDisable() => Spline.Changed -= OnSplineChanged;

    private void OnSplineChanged(Spline s, int k, SplineModification m)
    {
        if (refs.splineContainer != null && s == refs.splineContainer.Spline) RebuildBridge();
    }

    public void RebuildBridge()
    {
        if (this == null || refs.splineContainer == null) return;
        
        ClearBridge();

        var spline = refs.splineContainer.Spline;
        float totalLength = spline.GetLength();
        float trueBridgeEnd = totalLength;

        GenerateStringers(spline, trueBridgeEnd);
        GeneratePillars(spline, trueBridgeEnd);
        GeneratePlanks(spline, totalLength);
        GenerateRailingsAndLights(spline, trueBridgeEnd);

        // THE BATCH FIX: Force Unity to treat these as a batch
        #if UNITY_EDITOR
        StaticBatchingUtility.Combine(gameObject);
        #endif
    }

    private void GenerateStringers(Spline spline, float maxDist)
    {
        if (refs.stringerPrefab == null) return;
        float dist = 0;
        bool center = planks.bridgeWidth >= stringers.centerBeamThreshold;
        while (dist <= maxDist)
        {
            GetSplineData(spline, dist, out Vector3 pos, out Quaternion rot, out Vector3 right, out Vector3 up);
            float side = (planks.bridgeWidth / 2f) - stringers.inset;
            Vector3 vOff = -up * stringers.yOffset;

            Spawn(refs.stringerPrefab, pos - (right * side) + vOff, rot);
            Spawn(refs.stringerPrefab, pos + (right * side) + vOff, rot);
            if (center) Spawn(refs.stringerPrefab, pos + vOff, rot);
            dist += stringers.length;
        }
    }

    private void GeneratePlanks(Spline spline, float totalLength)
    {
        if (refs.plankPrefabs == null || refs.plankPrefabs.Count == 0) return;
        var rnd = new Unity.Mathematics.Random((uint)planks.seed);
        float step = planks.width + planks.spacing;
        for (float d = 0; d < totalLength; d += step)
        {
            GetSplineData(spline, d, out Vector3 pos, out Quaternion rot, out Vector3 right, out Vector3 up);
            GameObject p = Spawn(refs.plankPrefabs[rnd.NextInt(0, refs.plankPrefabs.Count)], pos, rot);
            p.transform.Rotate(Vector3.up, rnd.NextFloat(-planks.randomRotation, planks.randomRotation));
        }
    }

    private void GeneratePillars(Spline spline, float maxDist)
    {
        if (refs.pillarPrefab == null) return;
        for (float d = 0; d <= maxDist + 0.1f; d += pillars.spacing)
        {
            GetSplineData(spline, d, out Vector3 pos, out Quaternion rot, out Vector3 right, out Vector3 up);
            Vector3 ray = pos + (Vector3.down * pillars.raycastOffset);
            float side = (planks.bridgeWidth / 2f) - pillars.inset;

            if (pillars.onEdges) {
                TryPillar(pos + (right * side), ray + (right * side));
                TryPillar(pos - (right * side), ray - (right * side));
            } else TryPillar(pos, ray);
        }
    }

    private void TryPillar(Vector3 bridgePos, Vector3 rayOrigin)
    {
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, pillars.maxHeight, pillars.groundMask))
        {
            float dist = Vector3.Distance(bridgePos, hit.point);
            GameObject p = Spawn(refs.pillarPrefab, bridgePos + (Vector3.down * (dist / 2f)), Quaternion.identity);
            p.transform.localScale = new Vector3(p.transform.localScale.x, dist / pillars.meshNativeHeight, p.transform.localScale.z);
        }
    }

    private void GenerateRailingsAndLights(Spline spline, float maxDist)
    {
        if (refs.railingPrefab == null) return;
        float d = 0; int i = 0;
        while (d <= maxDist - railings.length)
        {
            GetSplineData(spline, d, out Vector3 pos, out Quaternion rot, out Vector3 right, out Vector3 up);
            float side = (planks.bridgeWidth / 2f) - railings.edgeOffset;

            Spawn(refs.railingPrefab, pos + (right * side), rot);
            Spawn(refs.railingPrefab, pos - (right * side), rot);

            d += railings.length + railings.spacing;
            if (refs.lightPolePrefab != null && ++i % railings.lightEveryNth == 0)
            {
                GetSplineData(spline, d, out Vector3 lp, out Quaternion lr, out Vector3 lri, out Vector3 lu);
                Spawn(refs.lightPolePrefab, lp + (lri * side), lr);
                Spawn(refs.lightPolePrefab, lp - (lri * side), lr);
                d += railings.lightPoleWidth + railings.spacing;
            }
        }
    }

    private void GetSplineData(Spline s, float d, out Vector3 p, out Quaternion r, out Vector3 ri, out Vector3 u)
    {
        float t = d / s.GetLength();
        s.Evaluate(t, out float3 lp, out float3 lt, out float3 lu);
        p = refs.splineContainer.transform.TransformPoint(lp);
        u = lockWorldUp ? Vector3.up : (Vector3)refs.splineContainer.transform.TransformDirection(lu);
        r = Quaternion.LookRotation(refs.splineContainer.transform.TransformDirection(lt), u);
        ri = Vector3.Cross(u, (Vector3)refs.splineContainer.transform.TransformDirection(lt)).normalized;
    }

    private GameObject Spawn(GameObject prefab, Vector3 pos, Quaternion rot)
    {
        GameObject obj = Instantiate(prefab, pos, rot, transform);
        obj.isStatic = true; // Essential for batching
        return obj;
    }

    private void ClearBridge()
    {
        while (transform.childCount > 0) DestroyImmediate(transform.GetChild(0).gameObject);
    }
}

*/







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
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
            Vector3 worldUp = refs.splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, worldUp);
            Vector3 right = Vector3.Cross(worldUp, worldTan).normalized;

            float sideOffset = (planks.bridgeWidth / 2f) - stringers.inset;
            Vector3 verticalOffset = -worldUp * stringers.yOffset;

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
            Vector3 worldUp = refs.splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, worldUp);

            GameObject prefab = refs.plankPrefabs[rnd.NextInt(0, refs.plankPrefabs.Count)];
            GameObject plank = Instantiate(prefab, worldPos, rot, transform);
            
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
            Vector3 worldUp = refs.splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, worldUp);
            Vector3 right = Vector3.Cross(worldUp, worldTan).normalized;

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
                Vector3 pWorldUp = refs.splineContainer.transform.TransformDirection(pLocalUp);
                Quaternion pRot = Quaternion.LookRotation(pWorldTan, pWorldUp);
                
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
            GameObject pillar = Instantiate(refs.pillarPrefab, transform);
            pillar.transform.rotation = Quaternion.identity;

            Vector3 originalScale = refs.pillarPrefab.transform.localScale;
            float finalYScale = totalDistance / pillars.meshNativeHeight;
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
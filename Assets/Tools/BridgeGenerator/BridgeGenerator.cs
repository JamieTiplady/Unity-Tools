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
    [Header("References")]
    public SplineContainer splineContainer;
    public List<GameObject> plankPrefabs;
    public GameObject railingPrefab;
    public GameObject lightPolePrefab;
    public GameObject stringerPrefab;
    public GameObject pillarPrefab; 

    [Header("Plank Settings")]
    [Min(0.01f)] public float plankWidth = 0.2f;
    public float plankSpacing = 0.05f;
    public float bridgeWidth = 2.0f;
    [Range(0, 5f)] public float randomRotation = 2.0f;

    [Header("Stringer (Under-Beam) Settings")]
    [Min(0.1f)] public float stringerLength = 2.0f; 
    public float stringerYOffset = 0.1f; 
    public float stringerInset = 0.2f; 
    public float centerBeamThreshold = 2.5f; 

    [Header("Railing & Light Settings")]
    [Min(0.1f)] public float railingLength = 1.0f;
    public float railingSpacing = 0.1f;
    [Min(0.01f)] public float lightPoleWidth = 0.2f;
    [Min(1)] public int lightEveryNthRailing = 3;
    public float railingEdgeOffset = 0.1f;

    public enum PivotLocation { Top, Center, Bottom }

    [Header("Support Pillar Settings")]
    [Min(0.1f)] public float pillarSpacing = 5.0f;
    [Min(1.0f)] public float maxPillarHeight = 50.0f;
    public float pillarInset = 0.3f; 
    public float raycastOffset = 0.5f; 
    public float meshNativeHeight = 1.0f; 
    public LayerMask groundMask = ~0; 
    public bool pillarsOnEdges = true;
    public PivotLocation pillarPivot = PivotLocation.Center;

    private bool _rebuildPending = false;

    private void OnValidate() => RequestRebuild();
    private void OnEnable() => Spline.Changed += (s, k, m) => RequestRebuild();
    private void OnDisable() => Spline.Changed -= (s, k, m) => RequestRebuild();

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
        if (splineContainer == null) return;

        var spline = splineContainer.Spline;
        float totalLength = spline.GetLength();

        // FIX 1: Calculate the exact position of the final plank.
        float plankStepSize = plankWidth + plankSpacing;
        int plankCount = Mathf.FloorToInt(totalLength / plankStepSize);
        float trueBridgeEnd = (plankCount > 0) ? (plankCount - 1) * plankStepSize : totalLength;

        // FIX 2: Pass trueBridgeEnd to the supports so they never overhang the planks
        GenerateStringers(spline, trueBridgeEnd);
        GeneratePillars(spline, trueBridgeEnd);
        GeneratePlanks(spline, totalLength); // Planks still use totalLength to calculate their own count
        GenerateRailingsAndLights(spline, trueBridgeEnd);
    }

    private void GenerateStringers(Spline spline, float maxAllowedLength)
    {
        if (stringerPrefab == null) return;

        float currentDist = 0;
        bool useCenterBeam = bridgeWidth >= centerBeamThreshold;
        float safeLength = Mathf.Max(0.1f, stringerLength);

        // FIX 3: Stop spawning if the next beam would stick out past the planks
        while (currentDist <= maxAllowedLength - (safeLength * 0.2f))
        {
            float t = currentDist / spline.GetLength();
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);

            Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = splineContainer.transform.TransformDirection(localTan);
            Vector3 worldUp = splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, worldUp);
            Vector3 right = Vector3.Cross(worldUp, worldTan).normalized;

            float sideOffset = (bridgeWidth / 2f) - stringerInset;
            Vector3 verticalOffset = -worldUp * stringerYOffset;

            Instantiate(stringerPrefab, worldPos - (right * sideOffset) + verticalOffset, rot, transform);
            Instantiate(stringerPrefab, worldPos + (right * sideOffset) + verticalOffset, rot, transform);

            if (useCenterBeam)
            {
                Instantiate(stringerPrefab, worldPos + verticalOffset, rot, transform);
            }

            currentDist += safeLength;
        }
    }

    private void GeneratePlanks(Spline spline, float totalLength)
    {
        if (plankPrefabs == null || plankPrefabs.Count == 0) return;
        float stepSize = plankWidth + plankSpacing;
        int count = Mathf.FloorToInt(totalLength / stepSize);

        for (int i = 0; i < count; i++)
        {
            float dist = i * stepSize;
            float t = dist / totalLength;
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);

            Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = splineContainer.transform.TransformDirection(localTan);
            Vector3 worldUp = splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, worldUp);

            GameObject prefab = plankPrefabs[UnityEngine.Random.Range(0, plankPrefabs.Count)];
            GameObject plank = Instantiate(prefab, worldPos, rot, transform);
            plank.transform.Rotate(Vector3.up, UnityEngine.Random.Range(-randomRotation, randomRotation));
        }
    }

    private void GenerateRailingsAndLights(Spline spline, float maxAllowedLength)
    {
        if (railingPrefab == null) return;
        float currentDist = 0;
        int railingCounter = 0;
        float totalLength = spline.GetLength();

        // FIX 4: Railings also respect the maxAllowedLength now
        while (currentDist <= maxAllowedLength - railingLength)
        {
            float t = currentDist / totalLength;
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);
            Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = splineContainer.transform.TransformDirection(localTan);
            Vector3 worldUp = splineContainer.transform.TransformDirection(localUp);
            Quaternion rot = Quaternion.LookRotation(worldTan, worldUp);
            Vector3 right = Vector3.Cross(worldUp, worldTan).normalized;

            float sideOffset = (bridgeWidth / 2f) - railingEdgeOffset;
            Instantiate(railingPrefab, worldPos + (right * sideOffset), rot, transform);
            Instantiate(railingPrefab, worldPos - (right * sideOffset), rot, transform);

            currentDist += railingLength;
            railingCounter++;

            if (lightPolePrefab != null && railingCounter % lightEveryNthRailing == 0)
            {
                currentDist += railingSpacing;
                float pt = currentDist / totalLength;
                spline.Evaluate(pt, out float3 pLocalPos, out float3 pLocalTan, out float3 pLocalUp);
                Vector3 pWorldPos = splineContainer.transform.TransformPoint(pLocalPos);
                Vector3 pWorldTan = splineContainer.transform.TransformDirection(pLocalTan);
                Vector3 pWorldUp = splineContainer.transform.TransformDirection(pLocalUp);
                Quaternion pRot = Quaternion.LookRotation(pWorldTan, pWorldUp);
                
                Instantiate(lightPolePrefab, pWorldPos + (right * sideOffset), pRot, transform);
                Instantiate(lightPolePrefab, pWorldPos - (right * sideOffset), pRot, transform);
                currentDist += lightPoleWidth + railingSpacing;
            }
            else
            {
                float safeSpacing = Mathf.Max(0.01f, railingSpacing);
                currentDist += safeSpacing;
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

    private void GeneratePillars(Spline spline, float maxAllowedLength)
    {
        if (pillarPrefab == null) return;

        float currentDist = 0;
        float safeSpacing = Mathf.Max(0.1f, pillarSpacing); 

        // FIX 5: Pillars stop exactly when the planks stop (with a tiny math buffer)
        while (currentDist <= maxAllowedLength + 0.05f)
        {
            float t = currentDist / spline.GetLength();
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);

            Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = splineContainer.transform.TransformDirection(localTan);
            Vector3 trueRight = Vector3.Cross(Vector3.up, worldTan).normalized;

            Vector3 rayStartOffset = worldPos + (Vector3.down * raycastOffset);

            if (pillarsOnEdges)
            {
                float sideOffset = (bridgeWidth / 2f) - pillarInset;
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
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, maxPillarHeight, groundMask))
        {
            float totalDistance = Vector3.Distance(bridgePos, hit.point);

            GameObject pillar = Instantiate(pillarPrefab, transform);
            pillar.transform.rotation = Quaternion.identity;

            Vector3 originalScale = pillarPrefab.transform.localScale;
            float finalYScale = totalDistance / meshNativeHeight;
            pillar.transform.localScale = new Vector3(originalScale.x, finalYScale, originalScale.z);

            switch (pillarPivot)
            {
                case PivotLocation.Top:
                    pillar.transform.position = bridgePos;
                    break;
                case PivotLocation.Center:
                    pillar.transform.position = bridgePos + (Vector3.down * (totalDistance / 2f));
                    break;
                case PivotLocation.Bottom:
                    pillar.transform.position = hit.point;
                    break;
            }
        }
    }
}
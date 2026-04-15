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

    [Header("Plank Settings")]
    [Min(0.01f)] public float plankWidth = 0.2f;
    public float plankSpacing = 0.05f; // Spacing can be 0, but width + spacing shouldn't be
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
    public GameObject pillarPrefab;
    [Min(0.1f)] public float pillarSpacing = 5.0f; // <-- THE CULPRIT!
    [Min(1.0f)] public float maxPillarHeight = 50.0f;
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

        GenerateStringers(spline, totalLength);
        GeneratePillars(spline, totalLength); // <-- NEW: Add this here
        GeneratePlanks(spline, totalLength);
        GenerateRailingsAndLights(spline, totalLength);
    }

    private void GenerateStringers(Spline spline, float totalLength)
    {
        if (stringerPrefab == null) return;

        float currentDist = 0;
        bool useCenterBeam = bridgeWidth >= centerBeamThreshold;

        while (currentDist < totalLength)
        {
            float t = currentDist / totalLength;
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

            float safeLength = Mathf.Max(0.1f, stringerLength);
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

            // FIXED: Explicitly use UnityEngine.Random
            GameObject prefab = plankPrefabs[UnityEngine.Random.Range(0, plankPrefabs.Count)];
            GameObject plank = Instantiate(prefab, worldPos, rot, transform);
            
            // FIXED: Explicitly use UnityEngine.Random
            plank.transform.Rotate(Vector3.up, UnityEngine.Random.Range(-randomRotation, randomRotation));
        }
    }

    private void GenerateRailingsAndLights(Spline spline, float totalLength)
    {
        if (railingPrefab == null) return;
        float currentDist = 0;
        int railingCounter = 0;

        while (currentDist < totalLength - railingLength)
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
                // Failsafe applied to spacing
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

    private void GeneratePillars(Spline spline, float totalLength)
    {
        if (pillarPrefab == null) return;

        float currentDist = 0;

        while (currentDist <= totalLength)
        {
            float t = currentDist / totalLength;
            spline.Evaluate(t, out float3 localPos, out float3 localTan, out float3 localUp);

            Vector3 worldPos = splineContainer.transform.TransformPoint(localPos);
            Vector3 worldTan = splineContainer.transform.TransformDirection(localTan);
            
            // FIX 1: Cross with Vector3.up to guarantee a perfectly horizontal left/right offset
            Vector3 trueRight = Vector3.Cross(Vector3.up, worldTan).normalized;

            // FIX 2: Push the raycast start point down slightly so it doesn't hit the bridge's own colliders
            Vector3 rayStartOffset = worldPos + (Vector3.down * 0.5f);

            if (pillarsOnEdges)
            {
                float sideOffset = (bridgeWidth / 2f) - stringerInset;
                SpawnPillar(rayStartOffset + (trueRight * sideOffset));
                SpawnPillar(rayStartOffset - (trueRight * sideOffset));
            }
            else
            {
                SpawnPillar(rayStartOffset);
            }

            // Failsafe: Ensure spacing is NEVER 0 to prevent an infinite loop
            float safeSpacing = Mathf.Max(0.1f, pillarSpacing); 
            currentDist += safeSpacing;
        }
    }

    private void SpawnPillar(Vector3 rayOrigin)
    {
        // Shoot ray straight down
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, maxPillarHeight, groundMask))
        {
            float distanceToGround = hit.distance;

            GameObject pillar = Instantiate(pillarPrefab, transform);
            
            // FIX 3: Force rotation to identity so pillars are always perfectly vertical
            pillar.transform.rotation = Quaternion.identity;

            Vector3 originalScale = pillarPrefab.transform.localScale;
            
            // Note: This math assumes your mesh is exactly 1 unit tall. 
            pillar.transform.localScale = new Vector3(originalScale.x, distanceToGround, originalScale.z);

            // FIX 4: Position the pillar based on where its pivot is located
            switch (pillarPivot)
            {
                case PivotLocation.Top:
                    // If pivot is at the top, just snap the top to the ray origin
                    pillar.transform.position = rayOrigin;
                    break;
                case PivotLocation.Center:
                    // If pivot is centered, move it exactly halfway down the raycast
                    pillar.transform.position = rayOrigin + (Vector3.down * (distanceToGround / 2f));
                    break;
                case PivotLocation.Bottom:
                    // If pivot is at the bottom, snap it to the ground hit point
                    pillar.transform.position = hit.point;
                    break;
            }
        }
    }
}
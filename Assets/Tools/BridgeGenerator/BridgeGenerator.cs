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
    public float plankWidth = 0.2f;
    public float plankSpacing = 0.05f;
    public float bridgeWidth = 2.0f;
    [Range(0, 5f)] public float randomRotation = 2.0f;

    [Header("Stringer (Under-Beam) Settings")]
    public float stringerLength = 2.0f; 
    public float stringerYOffset = 0.1f; 
    public float stringerInset = 0.2f; 
    public float centerBeamThreshold = 2.5f; 

    [Header("Railing & Light Settings")]
    public float railingLength = 1.0f;
    public float railingSpacing = 0.1f;
    public float lightPoleWidth = 0.2f;
    public int lightEveryNthRailing = 3;
    public float railingEdgeOffset = 0.1f;

    private bool _rebuildPending = false;

    private void OnValidate() => RequestRebuild();
    private void OnEnable() => Spline.Changed += (s, k, m) => RequestRebuild();
    private void OnDisable() => Spline.Changed -= (s, k, m) => RequestRebuild();

    private void RequestRebuild()
    {
        if (_rebuildPending) return;
#if UNITY_EDITOR
        _rebuildPending = true;
        EditorApplication.delayCall += () => {
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

            currentDist += stringerLength;
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
                currentDist += railingSpacing;
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
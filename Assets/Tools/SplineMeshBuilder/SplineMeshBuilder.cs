using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SplineMeshBuilder : MonoBehaviour
{
    [Header("Dependencies")]
    public SplineContainer splineContainer;
    public Mesh sourceMesh;
    public Material material;

    [Header("Bridge Settings")]
    public bool autoUpdate = true;
    public bool fitToEnd = true;

    [Header("Pillar Settings")]
    public bool generatePillars = true;
    public Mesh pillarMesh;
    public Material pillarMaterial;
    [Min(0.1f)] public float pillarSpacing = 10f;
    public bool pillarsOnEdges = true;
    public float edgeOffset = 2.5f;
    public float pillarVerticalOffset = 0f;

    [Header("Raycast Settings")]
    public LayerMask groundLayer;
    public float maxPillarHeight = 20f;
    public float rayStartOffset = 0.5f;
    public bool showDebugRays = false;

    // --- OPTIMIZATION CACHE ---
    private MeshFilter _filter;
    private MeshRenderer _renderer;
    private Mesh _generatedMesh;

    // Cache mesh data locally to avoid expensive ".vertices" calls
    private Vector3[] _srcVerts;
    private Vector3[] _srcNormals;
    private Vector2[] _srcUVs;
    private int[] _srcTris;

    private void OnEnable() => Spline.Changed += OnSplineChanged;
    private void OnDisable() => Spline.Changed -= OnSplineChanged;

    private void OnSplineChanged(Spline s, int k, SplineModification m)
    {
        if (autoUpdate && splineContainer != null && s == splineContainer.Spline)
            GenerateMesh();
    }

    private void CacheSourceMeshData()
    {
        if (sourceMesh == null) return;
        _srcVerts = sourceMesh.vertices;
        _srcNormals = sourceMesh.normals;
        _srcUVs = sourceMesh.uv;
        _srcTris = sourceMesh.triangles;
    }

    [ContextMenu("Generate Spline Mesh")]
    public void GenerateMesh()
    {
        if (sourceMesh == null || splineContainer == null) return;
        if (!sourceMesh.isReadable) return;

        if (_filter == null) _filter = GetComponent<MeshFilter>();
        if (_renderer == null) _renderer = GetComponent<MeshRenderer>();

        float splineLength = splineContainer.CalculateLength();
        float meshLength = sourceMesh.bounds.size.z;
        if (splineLength < 0.1f || meshLength < 0.01f) return;

        // 1. Cache the heavy data once per generation
        CacheSourceMeshData();

        int segments = Mathf.CeilToInt(splineLength / meshLength);
        float scaledMeshLength = fitToEnd ? (splineLength / segments) : meshLength;

        // Pre-calculate exact sizes to avoid List resizing
        int pillarCount = generatePillars ? (Mathf.FloorToInt(splineLength / pillarSpacing) + 1) : 0;
        int pillarsToSpawn = pillarsOnEdges ? pillarCount * 2 : pillarCount;
        
        int totalVerts = (segments * _srcVerts.Length) + (pillarsToSpawn * (pillarMesh ? pillarMesh.vertexCount : 0));
        
        // We still use Lists for triangles because we don't know exactly how many pillars will pass the raycast check
        List<Vector3> allVerts = new List<Vector3>(totalVerts);
        List<Vector3> allNormals = new List<Vector3>(totalVerts);
        List<Vector2> allUVs = new List<Vector2>(totalVerts);
        
        // Submesh tracking
        List<int> bridgeTris = new List<int>();
        List<int> pillarTris = new List<int>();

        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;
        float minZ = sourceMesh.bounds.min.z;

        // ==========================================
        // OPTIMIZED PASS 1: BRIDGE ROAD
        // ==========================================
        for (int s = 0; s < segments; s++)
        {
            int baseVertIndex = allVerts.Count;
            float segmentStartDist = s * scaledMeshLength;

            for (int i = 0; i < _srcVerts.Length; i++)
            {
                float dist = segmentStartDist + ((_srcVerts[i].z - minZ) * (scaledMeshLength / meshLength));
                float t = Mathf.Clamp01(dist / splineLength);

                // Evaluation is expensive, but we've reduced it to once per vertex
                splineContainer.Evaluate(t, out float3 wPos, out float3 wTan, out float3 wUp);

                Vector3 lPos = worldToLocal.MultiplyPoint(wPos);
                Vector3 lTan = worldToLocal.MultiplyVector(wTan).normalized;
                Vector3 lUp = worldToLocal.MultiplyVector(wUp).normalized;
                Vector3 lRight = Vector3.Cross(lUp, lTan).normalized;

                allVerts.Add(lPos + (lRight * _srcVerts[i].x) + (lUp * _srcVerts[i].y));
                allNormals.Add((lRight * _srcNormals[i].x) + (lUp * _srcNormals[i].y) + (lTan * _srcNormals[i].z));
                allUVs.Add(_srcUVs[i]);
            }

            for (int i = 0; i < _srcTris.Length; i++)
                bridgeTris.Add(_srcTris[i] + baseVertIndex);
        }

        // ==========================================
        // OPTIMIZED PASS 2: PILLARS
        // ==========================================
        if (generatePillars && pillarMesh != null)
        {
            Vector3[] pVerts = pillarMesh.vertices;
            Vector3[] pNormals = pillarMesh.normals;
            Vector2[] pUVs = pillarMesh.uv;
            int[] pTris = pillarMesh.triangles;
            float actualSpacing = splineLength / Mathf.Max(1, pillarCount - 1);

            for (int p = 0; p < pillarCount; p++)
            {
                float t = Mathf.Clamp01((p * actualSpacing) / splineLength);
                splineContainer.Evaluate(t, out float3 wPos, out float3 wTan, out float3 wUp);

                Vector3 worldForward = new Vector3(wTan.x, 0, wTan.z).normalized;
                if (worldForward.sqrMagnitude < 0.001f) worldForward = Vector3.forward;
                Vector3 worldRight = Vector3.Cross(Vector3.up, worldForward).normalized;

                // Pre-calculate orientation for the pillar
                Vector3 lTan = worldToLocal.MultiplyVector(worldForward).normalized;
                Vector3 lUp = worldToLocal.MultiplyVector(Vector3.up).normalized;
                Vector3 lRight = worldToLocal.MultiplyVector(worldRight).normalized;

                void TryPillar(Vector3 pos) {
                    Vector3 rayOrigin = pos + (Vector3.down * rayStartOffset);
                    if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, maxPillarHeight, groundLayer)) {
                        int vStart = allVerts.Count;
                        Vector3 lPivot = worldToLocal.MultiplyPoint(pos) + (lUp * pillarVerticalOffset);
                        for(int i=0; i<pVerts.Length; i++) {
                            allVerts.Add(lPivot + (lRight * pVerts[i].x) + (lUp * pVerts[i].y) + (lTan * pVerts[i].z));
                            allNormals.Add((lRight * pNormals[i].x) + (lUp * pNormals[i].y) + (lTan * pNormals[i].z));
                            allUVs.Add(pUVs[i]);
                        }
                        for(int i=0; i<pTris.Length; i++) pillarTris.Add(pTris[i] + vStart);
                    }
                }

                if (pillarsOnEdges) {
                    TryPillar((Vector3)wPos - (worldRight * edgeOffset));
                    TryPillar((Vector3)wPos + (worldRight * edgeOffset));
                } else {
                    TryPillar(wPos);
                }
            }
        }

        // ==========================================
        // FINAL APPLY
        // ==========================================
        if (_generatedMesh == null) _generatedMesh = new Mesh { name = "BridgeMesh" };
        else _generatedMesh.Clear();

        if (allVerts.Count > 65535) _generatedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        _generatedMesh.SetVertices(allVerts);
        _generatedMesh.SetNormals(allNormals);
        _generatedMesh.SetUVs(0, allUVs);
        _generatedMesh.subMeshCount = 2;
        _generatedMesh.SetTriangles(bridgeTris, 0);
        _generatedMesh.SetTriangles(pillarTris, 1);

        _renderer.sharedMaterials = new Material[] { material, pillarMaterial != null ? pillarMaterial : material };
        _filter.sharedMesh = _generatedMesh;
    }
}
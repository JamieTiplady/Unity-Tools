using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

#if UNITY_EDITOR
using UnityEditor;
#endif

[System.Serializable]
public class InlineFitting
{
    public string label = "Fitting";
    public Mesh mesh;
    public Material material; // New material slot per fitting
    [Range(0f, 1f)]
    public float spawnChance = 0.1f;
}

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SplinePipeBuilder : MonoBehaviour
{
    [Header("Dependencies")]
    public SplineContainer splineContainer;
    public Mesh sourceMesh;
    public Material material;

    [Header("Settings")]
    public bool autoUpdate = true;
    public bool fitToEnd = true;

    [Header("Inline Fittings")]
    public List<InlineFitting> inlineFittings = new List<InlineFitting>();

    private MeshFilter _filter;
    private MeshRenderer _renderer;
    private Mesh _generatedMesh;

    private struct SegmentPlan
    {
        public Mesh mesh;
        public Material mat; // Track which material this segment uses
        public float startDist;
        public float length;
    }

    private void OnEnable() => Spline.Changed += OnSplineChanged;
    private void OnDisable() => Spline.Changed -= OnSplineChanged;

    private void OnSplineChanged(Spline s, int k, SplineModification m)
    {
        if (autoUpdate && splineContainer != null && s == splineContainer.Spline)
            GenerateMesh();
    }

    private List<SegmentPlan> PlanSegments(float splineLength, float meshLength)
    {
        var plans = new List<SegmentPlan>();
        float currentDist = 0f;

        while (currentDist < splineLength - 0.001f)
        {
            float remaining = splineLength - currentDist;
            InlineFitting fitting = TryPickFitting(remaining);

            if (fitting != null && fitting.mesh != null)
            {
                float fLen = fitting.mesh.bounds.size.z;
                plans.Add(new SegmentPlan { 
                    mesh = fitting.mesh, 
                    mat = fitting.material != null ? fitting.material : material, 
                    startDist = currentDist, 
                    length = fLen 
                });
                currentDist += fLen;
            }
            else
            {
                plans.Add(new SegmentPlan { 
                    mesh = sourceMesh, 
                    mat = material, 
                    startDist = currentDist, 
                    length = meshLength 
                });
                currentDist += meshLength;
            }
        }

        if (fitToEnd)
        {
            float fittingTotal = 0f;
            int straightCount = 0;
            foreach (var p in plans) {
                if (p.mesh == sourceMesh) straightCount++;
                else fittingTotal += p.length;
            }

            if (straightCount > 0)
            {
                float scaledStraightLength = Mathf.Max(0.01f, (splineLength - fittingTotal) / straightCount);
                float dist = 0f;
                for (int i = 0; i < plans.Count; i++)
                {
                    var p = plans[i];
                    p.startDist = dist;
                    if (p.mesh == sourceMesh) p.length = scaledStraightLength;
                    plans[i] = p;
                    dist += p.length;
                }
            }
        }
        return plans;
    }

    private InlineFitting TryPickFitting(float remainingLength)
    {
        if (inlineFittings == null || inlineFittings.Count == 0) return null;

        int startIndex = UnityEngine.Random.Range(0, inlineFittings.Count);
        for (int i = 0; i < inlineFittings.Count; i++)
        {
            var fitting = inlineFittings[(startIndex + i) % inlineFittings.Count];
            if (fitting.mesh == null || !fitting.mesh.isReadable) continue;
            if (fitting.mesh.bounds.size.z > remainingLength) continue;

            if (UnityEngine.Random.value < fitting.spawnChance) return fitting;
        }
        return null;
    }

    [ContextMenu("Generate Spline Mesh")]
    public void GenerateMesh()
    {
        if (sourceMesh == null || splineContainer == null) return;
        _filter = GetComponent<MeshFilter>();
        _renderer = GetComponent<MeshRenderer>();

        float splineLength = splineContainer.CalculateLength();
        float meshLength = sourceMesh.bounds.size.z;
        if (splineLength < 0.1f || meshLength < 0.01f) return;

        List<SegmentPlan> plan = PlanSegments(splineLength, meshLength);

        // --- SUBMESH LOGIC ---
        // We need to group triangles by material
        Dictionary<Material, List<int>> submeshIndices = new Dictionary<Material, List<int>>();
        List<Vector3> allVerts = new List<Vector3>();
        List<Vector3> allNormals = new List<Vector3>();
        List<Vector2> allUVs = new List<Vector2>();

        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;

        foreach (var seg in plan)
        {
            int baseVertIndex = allVerts.Count;
            Mesh m = seg.mesh;
            float segMeshLen = m.bounds.size.z;
            float lengthScale = seg.length / segMeshLen;
            float minZ = m.bounds.min.z;

            // Vertices
            Vector3[] verts = m.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                float dist = seg.startDist + ((verts[i].z - minZ) * lengthScale);
                float t = Mathf.Clamp01(dist / splineLength);

                splineContainer.Evaluate(t, out float3 wPos, out float3 wTan, out float3 wUp);
                Vector3 lPos = worldToLocal.MultiplyPoint(wPos);
                Vector3 lTan = worldToLocal.MultiplyVector(wTan).normalized;
                Vector3 lUp = worldToLocal.MultiplyVector(wUp).normalized;
                Vector3 lRight = Vector3.Cross(lUp, lTan).normalized;

                allVerts.Add(lPos + (lRight * verts[i].x) + (lUp * verts[i].y));
                allNormals.Add((lRight * m.normals[i].x) + (lUp * m.normals[i].y) + (lTan * m.normals[i].z));
                allUVs.Add(m.uv[i]);
            }

            // Triangles (assigned to specific material group)
            if (!submeshIndices.ContainsKey(seg.mat)) submeshIndices[seg.mat] = new List<int>();
            int[] tris = m.triangles;
            for (int i = 0; i < tris.Length; i++)
            {
                submeshIndices[seg.mat].Add(tris[i] + baseVertIndex);
            }
        }

        // Apply to Mesh
        if (_generatedMesh != null) {
            if (Application.isPlaying) Destroy(_generatedMesh);
            else DestroyImmediate(_generatedMesh);
        }

        _generatedMesh = new Mesh { name = "SplinePipeMesh" };
        _generatedMesh.subMeshCount = submeshIndices.Count;
        _generatedMesh.SetVertices(allVerts);
        _generatedMesh.SetNormals(allNormals);
        _generatedMesh.SetUVs(0, allUVs);

        Material[] rendererMaterials = new Material[submeshIndices.Count];
        int mIdx = 0;
        foreach (var kvp in submeshIndices)
        {
            _generatedMesh.SetTriangles(kvp.Value, mIdx);
            rendererMaterials[mIdx] = kvp.Key;
            mIdx++;
        }

        _renderer.sharedMaterials = rendererMaterials;
        _generatedMesh.RecalculateBounds();
        _generatedMesh.RecalculateTangents();
        _filter.sharedMesh = _generatedMesh;
    }
}
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using System.Collections.Generic;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RibbonGenerator : MonoBehaviour
{
public SplineContainer splineContainer;
    
    [Header("Ribbon Settings")]
    public float width = 0.5f;
    public int resolution = 20; // How many segments along the spline

    private Mesh ribbonMesh;
    private MeshFilter meshFilter;

    [Header("Shape Settings")]
    public AnimationCurve widthCurve = AnimationCurve.Linear(0, 1, 1, 1);
    public float widthMultiplier = 0.5f;

    private void OnEnable()
    {
        meshFilter = GetComponent<MeshFilter>();
        ribbonMesh = new Mesh { name = "RibbonMesh" };
        meshFilter.mesh = ribbonMesh;

        Spline.Changed += OnSplineChanged;
        GenerateMesh();
    }

    private void OnDisable() => Spline.Changed -= OnSplineChanged;
    private void OnValidate() => GenerateMesh();
    private void OnSplineChanged(Spline s, int i, SplineModification m) => GenerateMesh();

    public void GenerateMesh()
    {
        if (splineContainer == null) return;

        var spline = splineContainer.Spline;
        // Vertices: 2 per segment (Left and Right)
        Vector3[] vertices = new Vector3[(resolution + 1) * 2];
        // UVs: 2 per segment
        Vector2[] uvs = new Vector2[vertices.Length];
        // Triangles: 6 indices per segment (2 triangles * 3 points)
        int[] tris = new int[resolution * 6];

        for (int i = 0; i <= resolution; i++)
        {
            float t = (float)i / resolution;
            spline.Evaluate(t, out float3 pos, out float3 forward, out float3 up);

            // 1. Calculate the taper based on the curve
            // If the curve is at 1.0, it's full width. If it's at 0.0, it's a point.
            float currentWidth = widthCurve.Evaluate(t) * widthMultiplier;

// 2. Calculate direction and index (ONLY ONCE)
            Vector3 right = Vector3.Cross((Vector3)forward, (Vector3)up).normalized;

            int vIndex = i * 2;
// 3. Apply the vertices using currentWidth
            vertices[vIndex] = (Vector3)pos + (right * currentWidth);    //right 
            vertices[vIndex + 1] = (Vector3)pos - (right * currentWidth);//left

            // Set UVs (x is across the width, y is along the length)
            uvs[vIndex] = new Vector2(0, t);
            uvs[vIndex + 1] = new Vector2(1, t);

            // Build Triangles (only if we aren't at the very last point)
            if (i < resolution)
            {
                int tIndex = i * 6;
                // Triangle 1
                tris[tIndex] = vIndex;
                tris[tIndex + 1] = vIndex + 2;
                tris[tIndex + 2] = vIndex + 1;
                // Triangle 2
                tris[tIndex + 3] = vIndex + 1;
                tris[tIndex + 4] = vIndex + 2;
                tris[tIndex + 5] = vIndex + 3;
            }
        }

        // Upload data to the mesh
        ribbonMesh.Clear();
        ribbonMesh.vertices = vertices;
        ribbonMesh.triangles = tris;
        ribbonMesh.uv = uvs;
        ribbonMesh.RecalculateNormals(); // Important for lighting!
    }
}
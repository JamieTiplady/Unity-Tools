using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using System.Collections.Generic;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RopeGenerator : MonoBehaviour
{
    public SplineContainer splineContainer;

    [Header("Rope Shape")]
    public float radius = 0.2f;
    [Range(3, 32)]
    public int radialResolution = 8; // Sides of the circle
    [Range(10, 200)]
    public int splineResolution = 30; // Lengthwise segments

    [Header("Taper Settings")]
    public AnimationCurve widthCurve = AnimationCurve.Linear(0, 1, 1, 1);
    public float taperMultiplier = 1.0f;

    [Header("Texturing")]
    public float uvStretch = 1.0f;

    private Mesh ropeMesh;
    private MeshFilter meshFilter;

    private void OnEnable()
    {
        meshFilter = GetComponent<MeshFilter>();
        ropeMesh = new Mesh { name = "RopeMesh" };
        meshFilter.mesh = ropeMesh;

        Spline.Changed += OnSplineChanged;
        GenerateRope();
    }

    private void OnDisable() => Spline.Changed -= OnSplineChanged;
    private void OnValidate() => GenerateRope();
    private void OnSplineChanged(Spline s, int i, SplineModification m) => GenerateRope();

    public void GenerateRope()
    {
        if (splineContainer == null) return;

        var spline = splineContainer.Spline;
        
        // Calculate counts
        int vertCount = (splineResolution + 1) * radialResolution;
        int triCount = splineResolution * radialResolution * 6;

        Vector3[] vertices = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];
        int[] tris = new int[triCount];

        for (int i = 0; i <= splineResolution; i++)
        {
            float t = (float)i / splineResolution;
            spline.Evaluate(t, out float3 pos, out float3 forward, out float3 up);

            // Create a "Frame" for the circle to sit on
            Vector3 binormal = Vector3.Cross((Vector3)forward, (Vector3)up).normalized;
            Vector3 normal = Vector3.Cross(binormal, (Vector3)forward).normalized;

            // Apply the taper
            float currentRadius = radius * widthCurve.Evaluate(t) * taperMultiplier;

            for (int r = 0; r < radialResolution; r++)
            {
                int vIndex = i * radialResolution + r;

                // Circular math
                float angle = (float)r / radialResolution * Mathf.PI * 2f;
                Vector3 offset = (binormal * Mathf.Cos(angle) + normal * Mathf.Sin(angle)) * currentRadius;
                
                vertices[vIndex] = (Vector3)pos + offset;

                // UVs: X wraps around, Y goes along length
                uvs[vIndex] = new Vector2((float)r / (radialResolution - 1), t * uvStretch);

                // Stitch the faces
                if (i < splineResolution)
                {
                    int tIndex = (i * radialResolution + r) * 6;
                    
                    int current = i * radialResolution + r;
                    int next = i * radialResolution + (r + 1) % radialResolution;
                    int nextRowCurrent = (i + 1) * radialResolution + r;
                    int nextRowNext = (i + 1) * radialResolution + (r + 1) % radialResolution;

                    // Triangle 1
                    tris[tIndex] = current;
                    tris[tIndex + 1] = nextRowCurrent;
                    tris[tIndex + 2] = next;

                    // Triangle 2
                    tris[tIndex + 3] = next;
                    tris[tIndex + 4] = nextRowCurrent;
                    tris[tIndex + 5] = nextRowNext;
                }
            }
        }

        ropeMesh.Clear();
        ropeMesh.vertices = vertices;
        ropeMesh.triangles = tris;
        ropeMesh.uv = uvs;
        ropeMesh.RecalculateNormals();
        ropeMesh.RecalculateBounds();
    }
}
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SplineMeshBuilder : MonoBehaviour
{
    [Header("Dependencies")]
    public SplineContainer splineContainer;
    public Mesh sourceMesh;
    public Material material;

    [Header("Settings")]
    public bool autoUpdate = true;
    public bool fitToEnd = true;

    // Cached component references so we never call GetComponent in a hot path
    private MeshFilter   _filter;
    private MeshRenderer _renderer;
    private Mesh         _generatedMesh;

    // Cache components once on wake rather than every GenerateMesh call
    private void Awake()
    {
        _filter   = GetComponent<MeshFilter>();
        _renderer = GetComponent<MeshRenderer>();
    }

    // Subscribe to Unity's spline-changed event so we rebuild whenever the spline moves/reshapes
    private void OnEnable()  => Spline.Changed += OnSplineChanged;
    private void OnDisable() => Spline.Changed -= OnSplineChanged;

    private void OnSplineChanged(Spline s, int k, SplineModification m)
    {
        // Only rebuild if auto-update is on AND the changed spline is ours
        if (autoUpdate && splineContainer != null && s == splineContainer.Spline)
            GenerateMesh();
    }

    [ContextMenu("Generate Spline Mesh")]
    public void GenerateMesh()
    {
        if (sourceMesh == null || splineContainer == null) return;

        // Unity requires Read/Write enabled on the mesh asset to access its vertex data in code
        if (!sourceMesh.isReadable) return;

        // Lazily grab components if Awake hasn't fired (e.g. called from editor context menu)
        if (_filter   == null) _filter   = GetComponent<MeshFilter>();
        if (_renderer == null) _renderer = GetComponent<MeshRenderer>();

        if (material != null) _renderer.sharedMaterial = material;

        // How long the spline is in world units
        float splineLength = splineContainer.CalculateLength();
        // How long a single tile of our source mesh is (measured along Z, its forward axis)
        float meshLength = sourceMesh.bounds.size.z;

        if (splineLength < 0.1f || meshLength < 0.01f) return;

        // How many times we need to repeat the mesh to cover the full spline
        int segments = Mathf.CeilToInt(splineLength / meshLength);

        // If fitToEnd is on, stretch each tile slightly so the last one ends exactly at the spline tip
        // Otherwise every tile is true-to-size and the last one may fall a little short
        float scaledMeshLength = fitToEnd ? (splineLength / segments) : meshLength;

        // Snapshot source mesh data into local arrays — avoids repeated managed->native calls inside the loop
        Vector3[] srcVerts   = sourceMesh.vertices;
        Vector3[] srcNormals = sourceMesh.normals;
        Vector2[] srcUVs     = sourceMesh.uv;
        int[]     srcTris    = sourceMesh.triangles;

        int srcVertCount = srcVerts.Length;
        int srcTriCount  = srcTris.Length;

        // Pre-allocate output arrays sized for all segments up front — no mid-loop allocations
        Vector3[] newVerts   = new Vector3[srcVertCount * segments];
        Vector3[] newNormals = new Vector3[srcVertCount * segments];
        Vector2[] newUVs     = new Vector2[srcVertCount * segments];
        int[]     newTris    = new int[srcTriCount  * segments];

        // The lowest Z value in the source mesh; used to normalise each vertex's Z to a 0-based distance
        float minZ = sourceMesh.bounds.min.z;

        // Cache the world-to-local matrix once — it's the same for every vertex and calling
        // transform.worldToLocalMatrix repeatedly re-computes it each time
        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;

        for (int s = 0; s < segments; s++)
        {
            // Where in the output arrays this segment's data starts
            int vertOffset = s * srcVertCount;
            int triOffset  = s * srcTriCount;

            // World-distance along the spline where this segment tile begins
            float segmentStartDist = s * scaledMeshLength;

            for (int i = 0; i < srcVertCount; i++)
            {
                // Distance of this vertex along the spline:
                // shift the vertex Z so it starts from 0, scale it if fitToEnd is on,
                // then add the offset for whichever tile we're on
                float dist = segmentStartDist + ((srcVerts[i].z - minZ) * (scaledMeshLength / meshLength));

                // Convert distance to a 0-1 parameter (t=0 is spline start, t=1 is spline end)
                float t = Mathf.Clamp01(dist / splineLength);

                // Ask the spline for position, forward direction, and up direction at this t
                // These come back in world space
                splineContainer.Evaluate(t, out float3 wPos, out float3 wTan, out float3 wUp);

                // Bring the spline's world-space frame into this object's local space
                Vector3 lPos   = worldToLocal.MultiplyPoint(wPos);
                Vector3 lTan   = worldToLocal.MultiplyVector(wTan).normalized; // forward along spline
                Vector3 lUp    = worldToLocal.MultiplyVector(wUp).normalized;  // up at this point
                Vector3 lRight = Vector3.Cross(lUp, lTan).normalized;          // right = up × forward

                // Place the vertex by starting at the spline position then pushing it
                // sideways (X) and upward (Y) according to its original offset in the source mesh
                newVerts[vertOffset + i] = lPos + (lRight * srcVerts[i].x) + (lUp * srcVerts[i].y);

                // Rotate the normal using the local tangent frame directly — avoids creating a
                // Quaternion just to do a basis transform, which is the same operation written cheaper
                newNormals[vertOffset + i] = (lRight * srcNormals[i].x)
                                           + (lUp    * srcNormals[i].y)
                                           + (lTan   * srcNormals[i].z);

                //UVs don't change — copy straight across
                newUVs[vertOffset + i] = srcUVs[i];
            }

            // Copy triangle indices for this segment, shifting each index by how many
            // vertices came before it so they point at the right segment's vertices
            for (int i = 0; i < srcTriCount; i++)
                newTris[triOffset + i] = srcTris[i] + vertOffset;
        }

        // Clean up the old generated mesh to avoid leaking assets in memory
        if (_generatedMesh != null)
        {
            if (Application.isPlaying) Destroy(_generatedMesh);
            else DestroyImmediate(_generatedMesh);
        }

        _generatedMesh = new Mesh { name = "SplineDeformedMesh" };

        // Unity's default index buffer only supports ~65k vertices; switch to 32-bit if need more
        if (newVerts.Length > 65535)
            _generatedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        _generatedMesh.vertices  = newVerts;
        _generatedMesh.normals   = newNormals;
        _generatedMesh.uv        = newUVs;
        _generatedMesh.triangles = newTris;

        // Recalculate the bounding box (used for culling) and tangents (used for normal maps)
        _generatedMesh.RecalculateBounds();
        _generatedMesh.RecalculateTangents();

        _filter.sharedMesh = _generatedMesh;
    }
}
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class BridgeGenerator : MonoBehaviour
{
    private const float MinDistance = 0.01f;
    private const float MinLoopStep = 0.1f;
    private const float PillarEndTolerance = 0.05f;
    private const float SqrEpsilon = 0.000001f;

    [System.Serializable]
    public class ReferenceSettings
    {
        [Tooltip("assign the spline you want the bridge to follow")]
        public SplineContainer splineContainer;
        [Tooltip("Assign all the plank prefabs here")]
        public List<GameObject> plankPrefabs = new List<GameObject>();
        [Tooltip("If using wooden bridge stringer, assign the prefab here")]
        public GameObject stringerPrefab;
        [Tooltip("If using wooden bridge pillars to support the bridge and hold it up, assign them here.")]
        public GameObject pillarPrefab;
    }

    [System.Serializable]
    public class PlankSettings
    {
        [Tooltip("Randomise the planks")]
        public int seed = 12345;
        [Tooltip("Assign the width of your planks")]
        [Min(0.01f)] public float width = 0.2f;
        [Tooltip("set the space between planks")]
        [Min(0f)] public float spacing = 0.05f;
        [Tooltip("how wide is the bridge? This will help setup pillars, stringers and balustrades.")]
        [Min(0.1f)] public float bridgeWidth = 2.0f;
        [Tooltip("procedurally rotate the brideg planks at random")]
        [Range(0f, 5f)] public float randomRotation = 2.0f;
        [Tooltip("add some damage to the bridge by removing random planks. 0 = no damage, 1 = all planks are gone.")]
        [Range(0f, 1f)] public float bridgePlankRemoval = 0f;
    }

    [System.Serializable]
    public class StringerSettings
    {
        [Tooltip("Set the length of your wooden bridge stringers. These are the parts under the bridge which connect and hold all the planks in place")]
        [Min(0.1f)] public float length = 2.0f;
        public float yOffset = 0.1f;
        [Tooltip("how far from the edge do you want the stringer?")]
        [Min(0f)] public float inset = 0.2f;
        [Tooltip("tweak this number to add a central stringer beam.")]
        [Min(0f)] public float centerBeamThreshold = 2.5f;
    }

    [System.Serializable]
    public class RopeBridgeSettings
    {
        [Tooltip("For the hand rail for a rope bridge, how smooth do you want the mesh, higher number = more polys and smoother mesh")]
        [Min(3)] public int sides = 8;
        [Tooltip("alter mesh on hand rail, lower number = higher mesh count")]
        [Min(0.05f)] public float meshSampleSpacing = 0.35f;

        [Header("Balustrade Rope")]
        [Tooltip("Set the thickness of the hand rail rope")]
        [Min(0.01f)] public float balustradeRadius = 0.05f;
        [Tooltip("Set the offset from centre of the bridge for the handrail, If using tringer rope, I would recommend setting the to match the stringer")]
        [Min(0f)] public float balustradeSideOffset = 0.9f;
        [Tooltip("Height of handrail from the bridge")]
        [Min(0f)] public float balustradeHeight = 1.0f;
        [Tooltip("Assign rope material here")]
        public Material balustradeMaterial;

        [Header("Stringer Rope")]
        [Tooltip("Control the thickness of the rope stringer")]
        [Min(0.01f)] public float stringerRadius = 0.06f;
        [Tooltip("assign rope stringer material here")]
        public Material stringerMaterial;

        [Header("Start / End Mesh")]
        [Tooltip("optional feature, assign Prefab for start and end of bridge model. E.g. a frame to signify entrance and exit of bridge")]
        public GameObject bridgeEndPrefab;

        [Header("Rope Pillars")]
        [Tooltip("Rope pillar is a poor name, but this is the prefab model which connects the stringer and the handrail together. Make sure Pillar pivot point is set to base of pillar prefab")]
        public GameObject ropePillarPrefab;
        [Tooltip("Number of rope pillar positions per side.")]
        [Min(0)] public int ropePillarCount = 8;
    }

    [System.Serializable]
    public class WoodenBridgeSettings
    {
        [Header("Wooden Pillars")]
        public GameObject woodenPillarPrefab;
        [Tooltip("Target pillar positions per side. Spline knots are always included even if this value is lower.")]
        [Min(0)] public int woodenPillarCount = 8;

        [Header("Horizontal Bars")]
        [Tooltip("Number of horizontal beams alongside the wooden bridge path, minimum 0, maximum 3.")]
        [Range(0, 3)] public int horizontalBarCount = 2;
        [Tooltip("Control the width shape of all the bars.")]
        [Min(0.01f)] public float horizontalBarWidth = 0.08f;
        [Tooltip("Control the height shape of all the bars")]
        [Min(0.01f)] public float horizontalBarHeight = 0.08f;
        [Tooltip("Tweak the amount of geometry assigned to the bars, lower = higher")]
        [Min(0.05f)] public float horizontalBarSampleSpacing = 0.35f;
        [Tooltip("Assign material for all the bars")]
        public Material horizontalBarMaterial;

        [Header("Bar Heights From Spline")]
        [Min(0f)] public float firstBarHeight = 0.45f;
        [Min(0f)] public float secondBarHeight = 0.75f;
        [Min(0f)] public float thirdBarHeight = 1.05f;
    }

    public enum PivotLocation
    {
        Top,
        Center,
        Bottom
    }

    public enum BridgeType
    {
        Wooden,
        Rope
    }

    [System.Serializable]
    public class PillarSettings
    {
        [Tooltip("Wooden bridge pillar controls. Set the spacing between pillars")]
        [Min(0.1f)] public float spacing = 5.0f;
        [Tooltip("Set the height if your pillar, if the 'ground' is further than this number, a pillar will not spawn")]
        [Min(1.0f)] public float maxHeight = 50.0f;
        [Tooltip("set the distance from the edge of the bridge you want pillars to spawn")]
        [Min(0f)] public float inset = 0.3f;
        [Tooltip("if pillars are spawning awkwardly, altering this number slightly may help. Thicker wooden planks might mean increasing this number for example")]
        [Min(0f)] public float raycastOffset = 0.5f;
        [Tooltip("Set the layer name(s) you want the bridge to consider the ground. If any ground layer is within the range set in the Max Height, a pillar will be spawned")]
        public LayerMask groundMask = ~0;
        [Tooltip("option to only have a pillar in the centre of the bridge")]
        public bool onEdges = true;
        [Tooltip("Where is the pivot of your pillar? this will position them correctly depending on pivot position. If it's custom, consider altering it to either top, centre or bottom of the model.")]
        public PivotLocation pivot = PivotLocation.Center;
    }

    private readonly struct SplineFrame
    {
        public readonly Vector3 Position;
        public readonly Vector3 Tangent;
        public readonly Vector3 Up;
        public readonly Vector3 Right;
        public readonly Quaternion Rotation;

        //this stores a snapshot of position, direction, and orientation at one point along the spline.
        public SplineFrame(Vector3 position, Vector3 tangent, Vector3 up, Vector3 right, Quaternion rotation)
        {
            Position = position;
            Tangent = tangent;
            Up = up;
            Right = right;
            Rotation = rotation;
        }
    }

    [Header("Bridge Type")]
    [Tooltip("Switch between a rope bridge or a wooden bridge")]
    public BridgeType bridgeType = BridgeType.Wooden;

    [Header("Orientation Settings")]
    [Tooltip("Splines can use Auto, Linear and Bezier on spline points. If using Linear, set to True. Can be used for other settings as well, this will force the bridge to stay flat and not bank to any sides")]
    public bool lockWorldUp = true;

    [Header("Optimisation")]
    [Tooltip("Option to set all models/prefabs to static automatically")]
    public bool staticBatchInPlayMode = true;

    [Tooltip("The lower the number the quicker Unity will build the bridge in Editor. WARNING: Low value can cause frame rate to drop in Editor when chnaging bridge settings.")]
    [Min(0f)] public float editorRebuildDelay = 0.12f;

    public ReferenceSettings refs = new ReferenceSettings();
    public PlankSettings planks = new PlankSettings();
    public StringerSettings stringers = new StringerSettings();
    public RopeBridgeSettings ropeBridge = new RopeBridgeSettings();
    public WoodenBridgeSettings woodenBridge = new WoodenBridgeSettings();
    public PillarSettings pillars = new PillarSettings();

    private bool _rebuildPending;


    #if UNITY_EDITOR
        private double _nextEditorRebuildTime;
    #endif

    //returns the spline container assigned in the inspector, or null if settings are missing
    private SplineContainer SplineContainer => refs != null ? refs.splineContainer : null;

    //called when any value changes in the Inspector. Triggers a bridge rebuild
    private void OnValidate()
    {
        EnsureSettings();
        RequestRebuild();
    }

    
    private void OnEnable()
    {
        EnsureSettings();
        Spline.Changed += OnSplineChanged;
    }

    private void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;

    #if UNITY_EDITOR
            EditorApplication.update -= ProcessEditorRebuild;
            _rebuildPending = false;
    #endif
    }

    //called when spline is edited. Triggers a rebuild only if it's the assigned spline that changed. Had a bug that rebuilt every bridge spline when changing one bridge.
    private void OnSplineChanged(Spline changedSpline, int knotIndex, SplineModification modification)
    {
        SplineContainer container = SplineContainer;
        if (container != null && changedSpline == container.Spline)
        {
            RequestRebuild();
        }
    }

    //Rebuild the bridge. In the editor it waits a short delay to avoid rebuilding on every mouse move.
    private void RequestRebuild()
    {

    #if UNITY_EDITOR
        if (Application.isPlaying)
        {
            RebuildBridge();
            return;
        }

        _nextEditorRebuildTime = EditorApplication.timeSinceStartup + Mathf.Max(0f, editorRebuildDelay);
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        EditorApplication.update -= ProcessEditorRebuild;
        EditorApplication.update += ProcessEditorRebuild;
    #else
        RebuildBridge();
    #endif
    }


    #if UNITY_EDITOR
    //Runs every editor frame. Once the delay has passed, triggers the actual bridge rebuild. Makes Unity Editor run a little smoother
    private void ProcessEditorRebuild()
    {
        if (this == null)
        {
            EditorApplication.update -= ProcessEditorRebuild;
            _rebuildPending = false;
            return;
        }

        if (EditorApplication.timeSinceStartup < _nextEditorRebuildTime)
        {
            return;
        }

        EditorApplication.update -= ProcessEditorRebuild;
        _rebuildPending = false;
        RebuildBridge();
    }
#endif

    //Main function. Wipes the old bridge and builds a fresh one from scratch
    public void RebuildBridge()
    {
        EnsureSettings();
        ClearBridge();

        SplineContainer container = SplineContainer;
        if (container == null)
        {
            return;
        }

        Spline spline = container.Spline;
        float totalLength = spline.GetLength();
        if (totalLength <= MinDistance)
        {
            return;
        }

        Transform splineTransform = container.transform;
        float plankStep = Mathf.Max(MinDistance, planks.width + planks.spacing);
        int plankCount = Mathf.FloorToInt(totalLength / plankStep);
        float bridgeEnd = plankCount > 0 ? Mathf.Min(totalLength, (plankCount - 1) * plankStep) : totalLength;

        if (bridgeType == BridgeType.Rope)
        {
            GenerateRopeBridgeMeshes(spline, splineTransform, totalLength, bridgeEnd);
        }
        else
        {
            GenerateStringers(spline, splineTransform, totalLength, bridgeEnd);
            GenerateWoodenBridgeDetails(spline, splineTransform, totalLength, totalLength);
        }

        GeneratePillars(spline, splineTransform, totalLength, bridgeEnd);
        GeneratePlanks(spline, splineTransform, totalLength, plankCount, plankStep);

        if (Application.isPlaying && staticBatchInPlayMode)
        {
            StaticBatchingUtility.Combine(gameObject);
        }
    }

    //places the wooden stringer (support beams) prefabs along the underside of the bridge.
    private void GenerateStringers(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.stringerPrefab == null || maxDistance <= 0f)
        {
            return;
        }

        bool useCenterBeam = planks.bridgeWidth >= stringers.centerBeamThreshold;
        float segmentLength = Mathf.Max(MinLoopStep, stringers.length);
        float sideOffset = GetSideOffset(stringers.inset);

        for (float distance = 0f; distance <= maxDistance - segmentLength * 0.2f; distance += segmentLength)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 verticalOffset = -frame.Up * stringers.yOffset;
            Spawn(refs.stringerPrefab, frame.Position - frame.Right * sideOffset + verticalOffset, frame.Rotation);
            Spawn(refs.stringerPrefab, frame.Position + frame.Right * sideOffset + verticalOffset, frame.Rotation);

            if (useCenterBeam)
            {
                Spawn(refs.stringerPrefab, frame.Position + verticalOffset, frame.Rotation);
            }
        }
    }

    //Rope bridge only. Builds all the rope geometry and end models
    private void GenerateRopeBridgeMeshes(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (maxDistance <= 0f)
        {
            return;
        }

        float stringerSideOffset = GetSideOffset(stringers.inset);
        float stringerHeightOffset = -stringers.yOffset;

        GenerateRopePair(
            "Rope Stringer",
            spline,
            splineTransform,
            totalLength,
            maxDistance,
            stringerSideOffset,
            stringerHeightOffset,
            ropeBridge.stringerRadius,
            ropeBridge.stringerMaterial);

        if (planks.bridgeWidth >= stringers.centerBeamThreshold)
        {
            GenerateRope(
                "Rope Center Stringer",
                spline,
                splineTransform,
                totalLength,
                maxDistance,
                0f,
                stringerHeightOffset,
                ropeBridge.stringerRadius,
                ropeBridge.stringerMaterial);
        }

        GenerateRopePair(
            "Rope Balustrade",
            spline,
            splineTransform,
            totalLength,
            maxDistance,
            ropeBridge.balustradeSideOffset,
            ropeBridge.balustradeHeight,
            ropeBridge.balustradeRadius,
            ropeBridge.balustradeMaterial);

        GenerateRopeBridgeEndPrefabs(spline, splineTransform, totalLength, maxDistance);
        GenerateRopePillars(spline, splineTransform, totalLength, maxDistance);
    }

    //positions the end prefabs at the very start and finish of the bridge to frame entrance and exit/add realism. Saves time in editor trying to position assets to match a bridge
    private void GenerateRopeBridgeEndPrefabs(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (ropeBridge.bridgeEndPrefab == null)
        {
            return;
        }

        if (TryGetFrame(spline, splineTransform, 0f, totalLength, out SplineFrame startFrame))
        {
            Spawn(ropeBridge.bridgeEndPrefab, startFrame.Position, Quaternion.identity);
        }

        if (TryGetFrame(spline, splineTransform, maxDistance, totalLength, out SplineFrame endFrame))
        {
            Spawn(ropeBridge.bridgeEndPrefab, endFrame.Position, Quaternion.Euler(0f, 180f, 0f));
        }
    }

    //sets the postions of rope pillar prefabs at evenly spaced points along both sides of the bridge
    private void GenerateRopePillars(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (ropeBridge.ropePillarPrefab == null || ropeBridge.ropePillarCount <= 0)
        {
            return;
        }

        int count = Mathf.Max(1, ropeBridge.ropePillarCount);
        float stringerSideOffset = GetSideOffset(stringers.inset);
        float stringerHeightOffset = -stringers.yOffset;

        for (int i = 0; i < count; i++)
        {
            float normalizedDistance = count == 1 ? 0.5f : (float)i / (count - 1);
            float distance = maxDistance * normalizedDistance;

            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            SpawnRopePillar(frame, -1f, stringerSideOffset, stringerHeightOffset);
            SpawnRopePillar(frame, 1f, stringerSideOffset, stringerHeightOffset);
        }
    }

    //function to control spawning of rope pillars
    private void SpawnRopePillar(SplineFrame frame, float side, float stringerSideOffset, float stringerHeightOffset)
    {
        Vector3 stringerPosition = frame.Position + frame.Right * (stringerSideOffset * side) + frame.Up * stringerHeightOffset;

        Spawn(ropeBridge.ropePillarPrefab, stringerPosition, frame.Rotation);
    }

    //generates the mesh for a matching rope on both the left and right sides of the bridge.
    private void GenerateRopePair(
        string meshName,
        Spline spline,
        Transform splineTransform,
        float totalLength,
        float maxDistance,
        float sideOffset,
        float heightOffset,
        float radius,
        Material material)
    {
        GenerateRope(meshName + " Left", spline, splineTransform, totalLength, maxDistance, -sideOffset, heightOffset, radius, material);
        GenerateRope(meshName + " Right", spline, splineTransform, totalLength, maxDistance, sideOffset, heightOffset, radius, material);
    }

    //generates the mesh for a single rope and adds it to the scene as a new child object. This is for the central stringer position as well
    private void GenerateRope(
        string meshName,
        Spline spline,
        Transform splineTransform,
        float totalLength,
        float maxDistance,
        float sideOffset,
        float heightOffset,
        float radius,
        Material material)
    {
        Mesh mesh = BuildRopeMesh(meshName, spline, splineTransform, totalLength, maxDistance, sideOffset, heightOffset, radius);
        if (mesh == null)
        {
            return;
        }

        CreateGeneratedMeshObject(meshName, mesh, material);
    }

    //This does the maths to create the mesh. Creates the actual cylindrical rope mesh by sampling positions along the spline and building rings of verts
    private Mesh BuildRopeMesh(
        string meshName,
        Spline spline,
        Transform splineTransform,
        float totalLength,
        float maxDistance,
        float sideOffset,
        float heightOffset,
        float radius)
    {
        int sides = Mathf.Max(3, ropeBridge.sides);
        float sampleSpacing = Mathf.Max(MinDistance, ropeBridge.meshSampleSpacing);
        int ringCount = Mathf.Max(2, Mathf.CeilToInt(maxDistance / sampleSpacing) + 1);
        int vertexCount = ringCount * sides;

        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[(ringCount - 1) * sides * 6];
        float[] ringCos = new float[sides];
        float[] ringSin = new float[sides];

        for (int side = 0; side < sides; side++)
        {
            float angle = (Mathf.PI * 2f * side) / sides;
            ringCos[side] = Mathf.Cos(angle);
            ringSin[side] = Mathf.Sin(angle);
        }

        for (int ring = 0; ring < ringCount; ring++)
        {
            float distance = ring == ringCount - 1 ? maxDistance : Mathf.Min(maxDistance, ring * sampleSpacing);
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 center = frame.Position + frame.Right * sideOffset + frame.Up * heightOffset;
            Vector3 ringRight = frame.Right;
            Vector3 ringUp = SafeNormalize(Vector3.Cross(frame.Tangent, ringRight), frame.Up);

            for (int side = 0; side < sides; side++)
            {
                Vector3 worldNormal = ringRight * ringCos[side] + ringUp * ringSin[side];
                int vertexIndex = ring * sides + side;

                vertices[vertexIndex] = transform.InverseTransformPoint(center + worldNormal * Mathf.Max(MinDistance, radius));
                normals[vertexIndex] = transform.InverseTransformDirection(worldNormal).normalized;
                uvs[vertexIndex] = new Vector2(distance / Mathf.Max(MinDistance, maxDistance), (float)side / sides);
            }
        }

        int triangleIndex = 0;
        for (int ring = 0; ring < ringCount - 1; ring++)
        {
            for (int side = 0; side < sides; side++)
            {
                int current = ring * sides + side;
                int next = ring * sides + (side + 1) % sides;
                int currentNextRing = (ring + 1) * sides + side;
                int nextNextRing = (ring + 1) * sides + (side + 1) % sides;

                triangles[triangleIndex++] = current;
                triangles[triangleIndex++] = next;
                triangles[triangleIndex++] = currentNextRing;
                triangles[triangleIndex++] = next;
                triangles[triangleIndex++] = nextNextRing;
                triangles[triangleIndex++] = currentNextRing;
            }
        }

        Mesh mesh = new Mesh
        {
            name = meshName,
            hideFlags = HideFlags.DontSave
        };

        if (vertexCount > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        return mesh;
    }

    //sets up building the vertical pillars and horizontal bars for a wooden-style bridge
    private void GenerateWoodenBridgeDetails(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        List<float> pillarDistances = GetWoodenPillarDistances(spline, splineTransform, totalLength, maxDistance);
        if (pillarDistances.Count == 0)
        {
            return;
        }

        GenerateWoodenPillars(spline, splineTransform, totalLength, pillarDistances);
        GenerateWoodenHorizontalBars(spline, splineTransform, totalLength, pillarDistances);
    }

    //works out where along the bridge each wooden pillar should be placed
    private List<float> GetWoodenPillarDistances(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        var distances = new List<float>();
        AddSplineKnotDistances(spline, splineTransform, totalLength, maxDistance, distances);

        int targetCount = Mathf.Max(distances.Count, woodenBridge.woodenPillarCount);
        while (distances.Count < targetCount)
        {
            if (!AddDistanceInLargestGap(distances, maxDistance))
            {
                break;
            }
        }

        distances.Sort();
        return distances;
    }

    //finds the biggest empty gap between existing pillar positions and inserts a new one in the middle.
    private bool AddDistanceInLargestGap(List<float> distances, float maxDistance)
    {
        distances.Sort();

        float bestStart = 0f;
        float bestEnd = maxDistance;
        float bestGap = distances.Count == 0 ? maxDistance : 0f;

        if (distances.Count > 0)
        {
            float startGap = distances[0];
            if (startGap > bestGap)
            {
                bestGap = startGap;
                bestStart = 0f;
                bestEnd = distances[0];
            }

            for (int i = 0; i < distances.Count - 1; i++)
            {
                float gap = distances[i + 1] - distances[i];
                if (gap > bestGap)
                {
                    bestGap = gap;
                    bestStart = distances[i];
                    bestEnd = distances[i + 1];
                }
            }

            float endGap = maxDistance - distances[distances.Count - 1];
            if (endGap > bestGap)
            {
                bestGap = endGap;
                bestStart = distances[distances.Count - 1];
                bestEnd = maxDistance;
            }
        }

        if (bestGap <= MinDistance)
        {
            return false;
        }

        int beforeCount = distances.Count;
        AddUniqueDistance(distances, Mathf.Lerp(bestStart, bestEnd, 0.5f), maxDistance);
        return distances.Count > beforeCount;
    }

    //adds a pillar position at each spline knot so pillars always appear at the bridge's control points. Without one the hand rails would look bad and the structure of the bridge is no longer believable
    private void AddSplineKnotDistances(Spline spline, Transform splineTransform, float totalLength, float maxDistance, List<float> distances)
    {
        int knotCount = spline.Count;
        int sampleCount = Mathf.Max(32, Mathf.CeilToInt(totalLength / Mathf.Max(0.1f, woodenBridge.horizontalBarSampleSpacing)) * 2);
        Vector3[] samplePositions = new Vector3[sampleCount + 1];
        float[] sampleDistances = new float[sampleCount + 1];
        int validSampleCount = 0;

        for (int sample = 0; sample <= sampleCount; sample++)
        {
            float distance = maxDistance * sample / sampleCount;
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            samplePositions[validSampleCount] = frame.Position;
            sampleDistances[validSampleCount] = distance;
            validSampleCount++;
        }

        if (validSampleCount == 0)
        {
            return;
        }

        for (int knotIndex = 0; knotIndex < knotCount; knotIndex++)
        {
            Vector3 knotPosition = splineTransform.TransformPoint(spline[knotIndex].Position);
            float bestDistance = 0f;
            float bestSqrDistance = float.MaxValue;

            for (int sample = 0; sample < validSampleCount; sample++)
            {
                float sqrDistance = (samplePositions[sample] - knotPosition).sqrMagnitude;
                if (sqrDistance < bestSqrDistance)
                {
                    bestSqrDistance = sqrDistance;
                    bestDistance = sampleDistances[sample];
                }
            }

            AddUniqueDistance(distances, bestDistance, maxDistance);
        }
    }

    //checks that the position is good to add a pillar, makes sure it's not too crowded. If it's good, it adds it
    private void AddUniqueDistance(List<float> distances, float distance, float maxDistance)
    {
        float clampedDistance = Mathf.Clamp(distance, 0f, maxDistance);
        float tolerance = Mathf.Max(MinDistance, woodenBridge.horizontalBarSampleSpacing * 0.25f);
        for (int i = 0; i < distances.Count; i++)
        {
            if (Mathf.Abs(distances[i] - clampedDistance) <= tolerance)
            {
                return;
            }
        }

        distances.Add(clampedDistance);
    }

    //spawns the wooden pillar prefab on both sides at each calculated pillar position
    private void GenerateWoodenPillars(Spline spline, Transform splineTransform, float totalLength, List<float> pillarDistances)
    {
        if (woodenBridge.woodenPillarPrefab == null)
        {
            return;
        }

        float sideOffset = GetSideOffset(stringers.inset);
        float heightOffset = -stringers.yOffset;

        for (int i = 0; i < pillarDistances.Count; i++)
        {
            if (!TryGetFrame(spline, splineTransform, pillarDistances[i], totalLength, out SplineFrame frame))
            {
                continue;
            }

            SpawnWoodenPillar(frame, -1f, sideOffset, heightOffset);
            SpawnWoodenPillar(frame, 1f, sideOffset, heightOffset);
        }
    }

    //spawns a single wooden pillar on one side at a given point along the bridge
    private void SpawnWoodenPillar(SplineFrame frame, float side, float sideOffset, float heightOffset)
    {
        Vector3 position = frame.Position + frame.Right * (sideOffset * side) + frame.Up * heightOffset;
        Spawn(woodenBridge.woodenPillarPrefab, position, GetWorldVerticalSplineRotation(frame));
    }

    //creates the horizontal bar meshes that run between pillars along both sides of the bridge
    private void GenerateWoodenHorizontalBars(Spline spline, Transform splineTransform, float totalLength, List<float> pillarDistances)
    {
        int barCount = Mathf.Clamp(woodenBridge.horizontalBarCount, 0, 3);
        if (barCount == 0 || pillarDistances.Count < 2)
        {
            return;
        }

        float sideOffset = GetSideOffset(stringers.inset);

        for (int barIndex = 0; barIndex < barCount; barIndex++)
        {
            float heightOffset = GetWoodenBarHeight(barIndex);

            GenerateWoodenHorizontalBarSide(
                "Wooden Bar " + (barIndex + 1) + " Left",
                spline,
                splineTransform,
                totalLength,
                pillarDistances,
                -sideOffset,
                heightOffset);

            GenerateWoodenHorizontalBarSide(
                "Wooden Bar " + (barIndex + 1) + " Right",
                spline,
                splineTransform,
                totalLength,
                pillarDistances,
                sideOffset,
                heightOffset);
        }
    }

    //returns the configured height for a given bar index (first, second, or third row)
    private float GetWoodenBarHeight(int barIndex)
    {
        switch (barIndex)
        {
            case 0:
                return woodenBridge.firstBarHeight;
            case 1:
                return woodenBridge.secondBarHeight;
            case 2:
                return woodenBridge.thirdBarHeight;
            default:
                return woodenBridge.firstBarHeight;
        }
    }

    //builds and places the horizontal bar mesh for one side (left or right) of the bridge
    private void GenerateWoodenHorizontalBarSide(
        string meshName,
        Spline spline,
        Transform splineTransform,
        float totalLength,
        List<float> pillarDistances,
        float sideOffset,
        float heightOffset)
    {
        Mesh mesh = BuildWoodenHorizontalBarMesh(meshName, spline, splineTransform, totalLength, pillarDistances, sideOffset, heightOffset);
        if (mesh == null)
        {
            return;
        }

        CreateGeneratedMeshObject(meshName, mesh, woodenBridge.horizontalBarMaterial);
    }

    //creates the rectangular bar mesh that connects pillars along the side of the bridge. This does all the maths along the spline and pillar points to make the shapes
    private Mesh BuildWoodenHorizontalBarMesh(
        string meshName,
        Spline spline,
        Transform splineTransform,
        float totalLength,
        List<float> pillarDistances,
        float sideOffset,
        float heightOffset)
    {
        float sampleSpacing = Mathf.Max(MinDistance, woodenBridge.horizontalBarSampleSpacing);
        int verticesPerRing = 4;
        int estimatedRings = Mathf.Max(2, Mathf.CeilToInt((pillarDistances[pillarDistances.Count - 1] - pillarDistances[0]) / sampleSpacing) + pillarDistances.Count);
        var vertices = new List<Vector3>(estimatedRings * verticesPerRing);
        var uvs = new List<Vector2>(estimatedRings * verticesPerRing);
        var triangles = new List<int>(estimatedRings * 24);

        float halfWidth = Mathf.Max(MinDistance, woodenBridge.horizontalBarWidth) * 0.5f;
        float halfHeight = Mathf.Max(MinDistance, woodenBridge.horizontalBarHeight) * 0.5f;

        for (int segment = 0; segment < pillarDistances.Count - 1; segment++)
        {
            float startDistance = pillarDistances[segment];
            float endDistance = pillarDistances[segment + 1];
            float segmentLength = endDistance - startDistance;
            if (segmentLength <= MinDistance)
            {
                continue;
            }

            int firstVertex = vertices.Count;
            int ringCount = Mathf.Max(2, Mathf.CeilToInt(segmentLength / sampleSpacing) + 1);

            for (int ring = 0; ring < ringCount; ring++)
            {
                float t = (float)ring / (ringCount - 1);
                float distance = Mathf.Lerp(startDistance, endDistance, t);
                if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
                {
                    continue;
                }

                AddWoodenBarRing(vertices, uvs, frame, sideOffset, heightOffset, halfWidth, halfHeight, t);
            }

            int segmentRingCount = (vertices.Count - firstVertex) / verticesPerRing;
            if (segmentRingCount < 2)
            {
                continue;
            }

            for (int ring = 0; ring < segmentRingCount - 1; ring++)
            {
                int current = firstVertex + ring * verticesPerRing;
                int next = firstVertex + (ring + 1) * verticesPerRing;

                AddQuad(triangles, current, current + 1, next, next + 1);
                AddQuad(triangles, current + 1, current + 2, next + 1, next + 2);
                AddQuad(triangles, current + 2, current + 3, next + 2, next + 3);
                AddQuad(triangles, current + 3, current, next + 3, next);
            }

            AddQuad(triangles, firstVertex, firstVertex + 3, firstVertex + 1, firstVertex + 2);

            int last = firstVertex + (segmentRingCount - 1) * verticesPerRing;
            AddQuad(triangles, last, last + 1, last + 3, last + 2);
        }

        if (vertices.Count == 0 || triangles.Count == 0)
        {
            return null;
        }

        Mesh mesh = new Mesh
        {
            name = meshName,
            hideFlags = HideFlags.DontSave
        };

        if (vertices.Count > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    //adds one rectangular cross-section (four corner vertices) of the bar mesh at a point along the bridge
    private void AddWoodenBarRing(
        List<Vector3> vertices,
        List<Vector2> uvs,
        SplineFrame frame,
        float sideOffset,
        float heightOffset,
        float halfWidth,
        float halfHeight,
        float uvX)
    {
        Vector3 center = frame.Position + frame.Right * sideOffset + frame.Up * heightOffset;
        Vector3 widthDirection = SafeNormalize(frame.Right, Vector3.right);
        Vector3 heightDirection = Vector3.up;

        vertices.Add(transform.InverseTransformPoint(center - widthDirection * halfWidth - heightDirection * halfHeight));
        vertices.Add(transform.InverseTransformPoint(center + widthDirection * halfWidth - heightDirection * halfHeight));
        vertices.Add(transform.InverseTransformPoint(center + widthDirection * halfWidth + heightDirection * halfHeight));
        vertices.Add(transform.InverseTransformPoint(center - widthDirection * halfWidth + heightDirection * halfHeight));

        uvs.Add(new Vector2(uvX, 0f));
        uvs.Add(new Vector2(uvX, 0.33f));
        uvs.Add(new Vector2(uvX, 0.66f));
        uvs.Add(new Vector2(uvX, 1f));
    }

    //creates a new child GameObject, attaches the given mesh to it, and assigns its material
    private void CreateGeneratedMeshObject(string meshName, Mesh mesh, Material material)
    {
        GameObject meshObject = new GameObject(meshName);
        meshObject.transform.SetParent(transform, false);

        MeshFilter meshFilter = meshObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = meshObject.AddComponent<MeshRenderer>();
        meshFilter.sharedMesh = mesh;

        if (material != null)
        {
            meshRenderer.sharedMaterial = material;
        }
    }

    //adds two triangles to form a flat four-sided face on a mesh
    private static void AddQuad(List<int> triangles, int a, int b, int c, int d)
    {
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
        triangles.Add(b);
        triangles.Add(d);
        triangles.Add(c);
    }

    //apawns the walkable plank prefabs along the bridge. Also does the maths to remove planks at random
    private void GeneratePlanks(Spline spline, Transform splineTransform, float totalLength, int count, float stepSize)
    {
        if (refs.plankPrefabs == null || refs.plankPrefabs.Count == 0 || count <= 0)
        {
            return;
        }

        var random = new Unity.Mathematics.Random(GetRandomSeed());
        float randomRotation = Mathf.Max(0f, planks.randomRotation);
        float removalChance = Mathf.Clamp01(planks.bridgePlankRemoval);

        for (int i = 0; i < count; i++)
        {
            //as the slider changes the same planks are always "marked" for removal
            bool shouldRemove = removalChance > 0f && random.NextFloat() < removalChance;

            if (!TryGetFrame(spline, splineTransform, i * stepSize, totalLength, out SplineFrame frame) || shouldRemove)
            {
                continue;
            }

            GameObject prefab = refs.plankPrefabs[random.NextInt(0, refs.plankPrefabs.Count)];
            if (prefab == null)
            {
                continue;
            }

            GameObject plank = Spawn(prefab, frame.Position, frame.Rotation);
            if (randomRotation > 0f)
            {
                plank.transform.Rotate(frame.Up, random.NextFloat(-randomRotation, randomRotation), Space.World);
            }
        }
    }

    //raycasts downward at intervals along the bridge and spawns support pillars wherever 'ground' is found below.
    private void GeneratePillars(Spline spline, Transform splineTransform, float totalLength, float maxDistance)
    {
        if (refs.pillarPrefab == null || maxDistance < 0f)
        {
            return;
        }

        float spacing = Mathf.Max(MinLoopStep, pillars.spacing);
        float sideOffset = GetSideOffset(pillars.inset);

        for (float distance = 0f; distance <= maxDistance + PillarEndTolerance; distance += spacing)
        {
            if (!TryGetFrame(spline, splineTransform, distance, totalLength, out SplineFrame frame))
            {
                continue;
            }

            Vector3 rayOrigin = frame.Position - Vector3.up * pillars.raycastOffset;
            Vector3 right = GetRight(Vector3.up, frame.Tangent, frame.Right);

            if (pillars.onEdges)
            {
                SpawnPillar(frame.Position + right * sideOffset, rayOrigin + right * sideOffset);
                SpawnPillar(frame.Position - right * sideOffset, rayOrigin - right * sideOffset);
            }
            else
            {
                SpawnPillar(frame.Position, rayOrigin);
            }
        }
    }

    //fires a single downward raycast and spawns a pillar if the ground is close enough
    private void SpawnPillar(Vector3 bridgePosition, Vector3 rayOrigin)
    {
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, pillars.maxHeight, pillars.groundMask, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        float distanceToGround = Vector3.Distance(bridgePosition, hit.point);
        if (distanceToGround <= MinDistance || distanceToGround >= pillars.maxHeight)
        {
            return;
        }

        Spawn(refs.pillarPrefab, GetPillarSpawnPosition(bridgePosition, hit.point), refs.pillarPrefab.transform.rotation);
    }

    //returns the spawn position for a pillar based on the pivot setting (top, centre, or bottom). Helpful if pillar objects are being used for other purposes in game. Saves user having to do any maths
    private Vector3 GetPillarSpawnPosition(Vector3 bridgePosition, Vector3 groundPosition)
    {
        switch (pillars.pivot)
        {
            case PivotLocation.Top:
                return bridgePosition;
            case PivotLocation.Center:
                return Vector3.Lerp(bridgePosition, groundPosition, 0.5f);
            case PivotLocation.Bottom:
                return groundPosition;
            default:
                return bridgePosition;
        }
    }

    //samples the spline at a given distance and returns the position, direction, and orientation at that point
    private bool TryGetFrame(Spline spline, Transform splineTransform, float distance, float totalLength, out SplineFrame frame)
    {
        frame = default;

        if (spline == null || splineTransform == null || totalLength <= MinDistance)
        {
            return false;
        }

        float t = Mathf.Clamp01(distance / totalLength);
        spline.Evaluate(t, out float3 localPosition, out float3 localTangent, out float3 localUp);

        Vector3 position = splineTransform.TransformPoint(localPosition);
        Vector3 tangent = SafeNormalize(splineTransform.TransformDirection(localTangent), splineTransform.forward);
        Vector3 up = lockWorldUp ? Vector3.up : SafeNormalize(splineTransform.TransformDirection(localUp), Vector3.up);
        Vector3 right = GetRight(up, tangent, splineTransform.right);
        Vector3 rotationUp = SafeNormalize(Vector3.Cross(tangent, right), up);
        Quaternion rotation = Quaternion.LookRotation(tangent, rotationUp);

        frame = new SplineFrame(position, tangent, up, right, rotation);
        return true;
    }

    //instantiates a prefab at a world position and rotation, parented to this bridge object
    private GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        return Instantiate(prefab, position, rotation, transform);
    }

    //calculates how far left or right from the centre a rail or pillar should be placed
    private float GetSideOffset(float inset)
    {
        return Mathf.Max(0f, planks.bridgeWidth * 0.5f - inset);
    }

    //produces a stable random seed based on the seed setting and the bridge's world position
    private uint GetRandomSeed()
    {
        Vector3 position = transform.position;
        uint seed = math.hash(new int4(
            planks.seed,
            Mathf.RoundToInt(position.x * 1000f),
            Mathf.RoundToInt(position.y * 1000f),
            Mathf.RoundToInt(position.z * 1000f)));

        return seed == 0u ? 1u : seed;
    }

    //works out which direction is 'right' along the bridge at a given point, with fallbacks for awkward areas
    private static Vector3 GetRight(Vector3 up, Vector3 tangent, Vector3 fallback)
    {
        Vector3 right = Vector3.Cross(up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.up, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        right = Vector3.Cross(Vector3.forward, tangent);
        if (right.sqrMagnitude > SqrEpsilon)
        {
            return right.normalized;
        }

        return SafeNormalize(fallback, Vector3.right);
    }

    //returns a rotation that faces along the bridge but always stays upright in world space. Needed for linear positions on splines. 
    //Will help if you want to animate objects along the bridge, can follow the bridge rotation and position along spline
    private static Quaternion GetWorldVerticalSplineRotation(SplineFrame frame)
    {
        Vector3 forward = Vector3.ProjectOnPlane(frame.Tangent, Vector3.up);
        return Quaternion.LookRotation(SafeNormalize(forward, Vector3.forward), Vector3.up);
    }

    //normalises a vector safely, returning a fallback direction if the vector is too short to normalise
    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        if (value.sqrMagnitude > SqrEpsilon)
        {
            return value.normalized;
        }

        if (fallback.sqrMagnitude > SqrEpsilon)
        {
            return fallback.normalized;
        }

        return Vector3.forward;
    }

    //makes sure none of the settings objects are null, creating them with defaults if needed
    private void EnsureSettings()
    {
        if (refs == null)
        {
            refs = new ReferenceSettings();
        }

        if (refs.plankPrefabs == null)
        {
            refs.plankPrefabs = new List<GameObject>();
        }

        if (planks == null)
        {
            planks = new PlankSettings();
        }

        if (stringers == null)
        {
            stringers = new StringerSettings();
        }

        if (ropeBridge == null)
        {
            ropeBridge = new RopeBridgeSettings();
        }

        if (woodenBridge == null)
        {
            woodenBridge = new WoodenBridgeSettings();
        }

        if (pillars == null)
        {
            pillars = new PillarSettings();
        }
    }

    //destroys all child objects and the meshes so the bridge can be rebuilt cleanly. Tried storing and altering with new data but got buggy and slow
    private void ClearBridge()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;
            DestroyGeneratedMesh(child);

            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }
    }

    //destroys the procedural mesh on a child object to prevent memory leaks when rebuilding
    private void DestroyGeneratedMesh(GameObject child)
    {
        MeshFilter meshFilter = child.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null || meshFilter.sharedMesh.hideFlags != HideFlags.DontSave)
        {
            return;
        }

        Mesh mesh = meshFilter.sharedMesh;
        meshFilter.sharedMesh = null;

        if (Application.isPlaying)
        {
            Destroy(mesh);
        }
        else
        {
            DestroyImmediate(mesh);
        }
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(BridgeGenerator))]
public class BridgeGeneratorEditor : Editor
{
    private SerializedProperty _bridgeType;
    private SerializedProperty _lockWorldUp;
    private SerializedProperty _staticBatchInPlayMode;
    private SerializedProperty _editorRebuildDelay;
    private SerializedProperty _refs;
    private SerializedProperty _planks;
    private SerializedProperty _stringers;
    private SerializedProperty _ropeBridge;
    private SerializedProperty _woodenBridge;
    private SerializedProperty _pillars;

    //looks up all the serialized properties so they can be drawn in the inspector.
    private void OnEnable()
    {
        _bridgeType = serializedObject.FindProperty("bridgeType");
        _lockWorldUp = serializedObject.FindProperty("lockWorldUp");
        _staticBatchInPlayMode = serializedObject.FindProperty("staticBatchInPlayMode");
        _editorRebuildDelay = serializedObject.FindProperty("editorRebuildDelay");
        _refs = serializedObject.FindProperty("refs");
        _planks = serializedObject.FindProperty("planks");
        _stringers = serializedObject.FindProperty("stringers");
        _ropeBridge = serializedObject.FindProperty("ropeBridge");
        _woodenBridge = serializedObject.FindProperty("woodenBridge");
        _pillars = serializedObject.FindProperty("pillars");
    }

    //draws the custom inspector layout, showing only the settings relevant to the chosen bridge type.
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("Bridge Type", EditorStyles.boldLabel);
        _bridgeType.enumValueIndex = GUILayout.Toolbar(_bridgeType.enumValueIndex, _bridgeType.enumDisplayNames);
        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(_lockWorldUp);
        EditorGUILayout.PropertyField(_staticBatchInPlayMode);
        EditorGUILayout.PropertyField(_editorRebuildDelay);
        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(_refs, true);
        EditorGUILayout.PropertyField(_planks, true);
        EditorGUILayout.PropertyField(_stringers, true);

        BridgeGenerator.BridgeType bridgeType = (BridgeGenerator.BridgeType)_bridgeType.enumValueIndex;
        if (bridgeType == BridgeGenerator.BridgeType.Rope)
        {
            EditorGUILayout.PropertyField(_ropeBridge, true);
        }
        else
        {
            EditorGUILayout.PropertyField(_woodenBridge, true);
        }

        EditorGUILayout.PropertyField(_pillars, true);

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
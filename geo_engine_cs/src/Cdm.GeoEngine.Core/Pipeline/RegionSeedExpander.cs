using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Expand sparse region seeds into face sets via flood-fill + nearest-seed fallback.</summary>
public static class RegionSeedExpander
{
    private static readonly GeoRegionKind[] ExpansionPriority =
    {
        GeoRegionKind.Gable,
        GeoRegionKind.Soffit,
        GeoRegionKind.Plinth,
        GeoRegionKind.Roof,
        GeoRegionKind.Floor,
        GeoRegionKind.WallInner,
        GeoRegionKind.WallOuter,
    };

    public static RegionGuidedFacePlan BuildPlan(
        MeshData mesh,
        IReadOnlyList<GeoRegionSeed> seeds,
        BuildingMeshProfile profile,
        bool blindFallbackForUnassigned = true)
    {
        if (seeds.Count == 0)
            return new RegionGuidedFacePlan { BlindFallbackForUnassigned = blindFallbackForUnassigned };

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();
        var neighbors = BuildFaceNeighbors(mesh);
        var assigned = new Dictionary<int, GeoRegionKind>();
        var facesByKind = ExpansionPriority.ToDictionary(k => k, _ => new HashSet<int>());

        foreach (var kind in ExpansionPriority)
        {
            foreach (var seed in seeds.Where(s => s.Kind == kind))
            {
                var seedFi = ResolveSeedFace(mesh, seed);
                if (seedFi < 0 || seedFi >= mesh.Faces.Count)
                    continue;

                var seedNormal = seed.Normal.Length() > 1e-6
                    ? seed.Normal.Normalized()
                    : faceNormals[seedFi];
                var seedCz = FaceCentroid(mesh, mesh.Faces[seedFi]).Dot(profile.AxisZ.Normalized());

                FloodFill(
                    mesh, seedFi, kind, seedNormal, seedCz, faceNormals, neighbors,
                    assigned, facesByKind[kind], profile);
            }
        }

        AssignUnclaimedByNearestSeed(mesh, seeds, faceNormals, assigned, facesByKind, profile);

        var allGuided = new HashSet<int>(assigned.Keys);
        var unassigned = mesh.Faces.Count - allGuided.Count;

        return new RegionGuidedFacePlan
        {
            FacesByKind = facesByKind,
            AllGuidedFaces = allGuided,
            BlindFallbackForUnassigned = blindFallbackForUnassigned,
            UnassignedFaceCount = unassigned,
        };
    }

    private static void FloodFill(
        MeshData mesh,
        int seedFi,
        GeoRegionKind kind,
        Vec3 seedNormal,
        double seedCz,
        Vec3[] faceNormals,
        List<int>[] neighbors,
        Dictionary<int, GeoRegionKind> assigned,
        HashSet<int> bucket,
        BuildingMeshProfile profile)
    {
        if (assigned.ContainsKey(seedFi))
            return;

        var queue = new Queue<int>();
        queue.Enqueue(seedFi);
        assigned[seedFi] = kind;
        bucket.Add(seedFi);

        while (queue.Count > 0)
        {
            var fi = queue.Dequeue();
            var fn = faceNormals[fi];
            var cz = FaceCentroid(mesh, mesh.Faces[fi]).Dot(profile.AxisZ.Normalized());

            foreach (var nb in neighbors[fi])
            {
                if (assigned.ContainsKey(nb))
                    continue;
                if (!CanExpand(kind, fn, faceNormals[nb], seedNormal, cz, seedCz, profile))
                    continue;

                assigned[nb] = kind;
                bucket.Add(nb);
                queue.Enqueue(nb);
            }
        }
    }

    private static void AssignUnclaimedByNearestSeed(
        MeshData mesh,
        IReadOnlyList<GeoRegionSeed> seeds,
        Vec3[] faceNormals,
        Dictionary<int, GeoRegionKind> assigned,
        Dictionary<GeoRegionKind, HashSet<int>> facesByKind,
        BuildingMeshProfile profile)
    {
        if (seeds.Count == 0)
            return;

        var seedFaces = seeds
            .Select(s => (Seed: s, Fi: ResolveSeedFace(mesh, s)))
            .Where(x => x.Fi >= 0 && x.Fi < mesh.Faces.Count)
            .ToList();
        if (seedFaces.Count == 0)
            return;

        var az = profile.AxisZ.Normalized();

        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (assigned.ContainsKey(fi))
                continue;

            var c = FaceCentroid(mesh, mesh.Faces[fi]);
            var fn = faceNormals[fi];
            var best = seedFaces[0];
            var bestCost = double.MaxValue;

            foreach (var entry in seedFaces)
            {
                var sc = FaceCentroid(mesh, mesh.Faces[entry.Fi]);
                var sn = entry.Seed.Normal.Length() > 1e-6
                    ? entry.Seed.Normal.Normalized()
                    : faceNormals[entry.Fi];
                var align = System.Math.Max(0, fn.Dot(sn));
                if (!MatchesKindLoose(entry.Seed.Kind, fn, profile))
                    align *= 0.35;

                var dist = Vec3.Distance(c, sc);
                var cost = dist - align * 0.8;

                // BUGFIX (Region-Marking-Workflow Session 2): Floor and Plinth are
                // narrow HEIGHT BANDS (ground floor slab, foundation strip) — a face
                // that is merely close in XY but at a very different height (e.g. a
                // mid-wall face, or a mezzanine-level face several meters above the
                // ground floor) must not silently inherit that band's kind just
                // because no better XY-adjacent seed exists. Without this, faces
                // like that get merged later into one absurd box spanning the whole
                // building height (see PROGRESS.md Session 2). Mirror the same
                // height-window used by CanExpand() during flood-fill.
                if (entry.Seed.Kind is GeoRegionKind.Floor or GeoRegionKind.Plinth)
                {
                    var heightWindow = entry.Seed.Kind == GeoRegionKind.Floor
                        ? System.Math.Clamp(profile.SizeM.Z * 0.35, 0.6, 1.8)
                        : System.Math.Clamp(profile.SizeM.Z * 0.28, 0.5, 1.2);
                    var heightGap = System.Math.Abs(c.Dot(az) - sc.Dot(az));
                    if (heightGap > heightWindow)
                        cost += (heightGap - heightWindow) * 3.0;
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = entry;
                }
            }

            var footprintLong = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);
            if (bestCost > footprintLong * 2.5)
                continue;

            assigned[fi] = best.Seed.Kind;
            facesByKind[best.Seed.Kind].Add(fi);
        }
    }

    private static bool CanExpand(
        GeoRegionKind kind,
        Vec3 faceNormal,
        Vec3 neighborNormal,
        Vec3 seedNormal,
        double cz,
        double seedCz,
        BuildingMeshProfile profile)
    {
        var az = profile.AxisZ.Normalized();
        var absZ = System.Math.Abs(faceNormal.Dot(az));
        var nAlign = faceNormal.Dot(seedNormal);
        var nbAlign = neighborNormal.Dot(seedNormal);

        if (nAlign < 0.45 || nbAlign < 0.45)
            return false;

        return kind switch
        {
            GeoRegionKind.WallOuter or GeoRegionKind.WallInner =>
                absZ < 0.58 && nAlign >= 0.62,
            GeoRegionKind.Floor =>
                faceNormal.Dot(az) < -0.55 && System.Math.Abs(cz - seedCz) < System.Math.Clamp(profile.SizeM.Z * 0.35, 0.6, 1.8),
            GeoRegionKind.Roof =>
                absZ > 0.35 || (nAlign >= 0.7 && absZ > 0.15),
            GeoRegionKind.Gable =>
                absZ < 0.88 && nAlign >= 0.55,
            GeoRegionKind.Plinth =>
                System.Math.Abs(cz - seedCz) < System.Math.Clamp(profile.SizeM.Z * 0.28, 0.5, 1.2),
            GeoRegionKind.Soffit =>
                absZ > 0.65,
            _ => nAlign >= 0.5,
        };
    }

    private static bool MatchesKindLoose(GeoRegionKind kind, Vec3 faceNormal, BuildingMeshProfile profile)
    {
        var az = profile.AxisZ.Normalized();
        var absZ = System.Math.Abs(faceNormal.Dot(az));
        return kind switch
        {
            GeoRegionKind.WallOuter or GeoRegionKind.WallInner => absZ < 0.65,
            GeoRegionKind.Floor => faceNormal.Dot(az) < -0.4,
            GeoRegionKind.Roof => absZ > 0.25,
            GeoRegionKind.Gable => absZ < 0.9,
            GeoRegionKind.Plinth => true,
            GeoRegionKind.Soffit => absZ > 0.5,
            _ => true,
        };
    }

    private static int ResolveSeedFace(MeshData mesh, GeoRegionSeed seed)
        => ResolveSeedFacePublic(mesh, seed);

    /// <summary>Nearest-face resolve for picker seeds (face_index optional).</summary>
    public static int ResolveSeedFacePublic(MeshData mesh, GeoRegionSeed seed)
    {
        if (seed.FaceIndex >= 0 && seed.FaceIndex < mesh.Faces.Count)
            return seed.FaceIndex;

        if (seed.Position.Length() < 1e-6)
            return -1;

        var wantNormal = seed.Normal.Length() > 1e-6 ? seed.Normal.Normalized() : (Vec3?)null;
        var best = -1;
        var bestD = double.MaxValue;
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (wantNormal is { } wn)
            {
                var fn = MeshTopology.FaceNormal(mesh, mesh.Faces[fi]).Normalized();
                if (fn.Dot(wn) < 0.15)
                    continue;
            }

            var c = FaceCentroid(mesh, mesh.Faces[fi]);
            var d = Vec3.Distance(c, seed.Position);
            if (d < bestD)
            {
                bestD = d;
                best = fi;
            }
        }
        return best;
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }

    private static List<int>[] BuildFaceNeighbors(MeshData mesh)
    {
        var neighbors = new List<int>[mesh.Faces.Count];
        for (var i = 0; i < neighbors.Length; i++)
            neighbors[i] = new List<int>();

        var edgeMap = new Dictionary<(int, int), List<int>>();
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var face = mesh.Faces[fi];
            for (var i = 0; i < face.Length; i++)
            {
                var a = face[i];
                var b = face[(i + 1) % face.Length];
                var key = a < b ? (a, b) : (b, a);
                if (!edgeMap.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    edgeMap[key] = list;
                }
                list.Add(fi);
            }
        }

        foreach (var faces in edgeMap.Values)
        {
            for (var i = 0; i < faces.Count; i++)
            {
                for (var j = i + 1; j < faces.Count; j++)
                {
                    neighbors[faces[i]].Add(faces[j]);
                    neighbors[faces[j]].Add(faces[i]);
                }
            }
        }

        return neighbors;
    }
}

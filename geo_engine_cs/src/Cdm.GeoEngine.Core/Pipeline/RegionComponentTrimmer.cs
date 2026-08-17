using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Merge generated OBB components down to corpus / reference target count (picker-guided sheds).
/// </summary>
public static class RegionComponentTrimmer
{
    public static List<MeshComponent> TrimToTarget(
        MeshData resolutionLod,
        IReadOnlyList<MeshComponent> components,
        int targetCount,
        BuildingMeshProfile profile)
    {
        if (targetCount <= 0 || components.Count <= targetCount)
            return components.ToList();

        var list = components.ToList();
        while (list.Count > targetCount)
        {
            var (bestI, bestJ, _) = FindBestMergePair(list, profile);
            list[bestI] = MergeTwo(list[bestI], list[bestJ], resolutionLod, profile);
            list.RemoveAt(bestJ);
        }

        for (var i = 0; i < list.Count; i++)
        {
            var name = $"Component{i + 1:D2}";
            var mesh = list[i].Mesh;
            mesh.Name = name;
            list[i] = new MeshComponent { Name = name, Mesh = mesh };
        }

        return BlindComponentRefiner.Refit(resolutionLod, list, profile);
    }

    private static (int I, int J, double Cost) FindBestMergePair(
        List<MeshComponent> list,
        BuildingMeshProfile profile)
    {
        var bestI = 0;
        var bestJ = 1;
        var bestCost = double.MaxValue;
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var cost = MergeCost(list[i], list[j], profile);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestI = i;
                    bestJ = j;
                }
            }
        }
        return (bestI, bestJ, bestCost);
    }

    private static double MergeCost(MeshComponent a, MeshComponent b, BuildingMeshProfile profile)
    {
        var ca = Vec3.Centroid(a.Mesh.Vertices);
        var cb = Vec3.Centroid(b.Mesh.Vertices);
        var dist = Vec3.Distance(ca, cb);

        var na = ComponentAxis(a, profile);
        var nb = ComponentAxis(b, profile);
        var align = System.Math.Abs(na.Dot(nb));
        var misalign = 1.0 - System.Math.Clamp(align, 0.0, 1.0);

        // BUGFIX (Region-Marking-Workflow Session 2): Abs(dot) treats ANTI-parallel
        // normals (e.g. left wall normal -X vs right wall normal +X) as "perfectly
        // aligned" (misalign=0), so the greedy trimmer previously loved to merge
        // two OPPOSITE walls into one nonsensical diagonal box spanning the whole
        // building footprint. Two components are only truly "coplanar" (safe to
        // merge into one flat OBB) when they are close together along the shared
        // normal axis, not merely parallel/anti-parallel in direction. Penalize
        // large offsets along that axis heavily so opposite-facing patches are
        // never chosen as merge partners ahead of spatially-adjacent ones.
        var axisRef = na.Length() > 1e-6 ? na : nb;
        var offsetPenalty = 0.0;
        if (axisRef.Length() > 1e-6)
        {
            var offsetGap = System.Math.Abs(ca.Dot(axisRef) - cb.Dot(axisRef));
            var wallThick = profile.WallThicknessM > 0 ? profile.WallThicknessM : 0.15;
            var maxCoplanarGapM = System.Math.Max(wallThick * 3.0, 0.4);
            if (offsetGap > maxCoplanarGapM)
            {
                var over = (offsetGap - maxCoplanarGapM) / System.Math.Max(maxCoplanarGapM, 0.1);
                offsetPenalty = System.Math.Min(over, 4.0) * 3.0;
            }
        }

        var sa = ComponentSpan(a.Mesh.Vertices);
        var sb = ComponentSpan(b.Mesh.Vertices);
        var sizeRatio = System.Math.Min(sa, sb) / System.Math.Max(System.Math.Max(sa, sb), 1e-6);

        return dist + misalign * 2.5 + offsetPenalty + (1.0 - sizeRatio) * 0.35;
    }

    private static MeshComponent MergeTwo(
        MeshComponent a,
        MeshComponent b,
        MeshData resolutionLod,
        BuildingMeshProfile profile)
    {
        var samples = new List<Vec3>(a.Mesh.Vertices.Count + b.Mesh.Vertices.Count + 64);
        samples.AddRange(a.Mesh.Vertices);
        samples.AddRange(b.Mesh.Vertices);

        // BUGFIX (Region-Marking-Workflow Session 2): the previous selection used
        // `da <= db*1.15 || db <= da*1.15` (OR of two near-equal-distance checks),
        // which is a tautology satisfied by almost EVERY vertex in the whole mesh
        // (whichever centroid a point is closer to, the other inequality holds
        // trivially). That pulled the entire building's resolution mesh into
        // `samples`, so the OBB fitter produced a box roughly the size of the
        // whole building footprint instead of a tight merge of just the two
        // adjacent patches. Restrict candidate points to a small margin around
        // the two components' combined bounding box instead (predictable and
        // independent of component shape/rotation).
        var unionMin = new Vec3(
            System.Math.Min(a.Mesh.Vertices.Min(v => v.X), b.Mesh.Vertices.Min(v => v.X)),
            System.Math.Min(a.Mesh.Vertices.Min(v => v.Y), b.Mesh.Vertices.Min(v => v.Y)),
            System.Math.Min(a.Mesh.Vertices.Min(v => v.Z), b.Mesh.Vertices.Min(v => v.Z)));
        var unionMax = new Vec3(
            System.Math.Max(a.Mesh.Vertices.Max(v => v.X), b.Mesh.Vertices.Max(v => v.X)),
            System.Math.Max(a.Mesh.Vertices.Max(v => v.Y), b.Mesh.Vertices.Max(v => v.Y)),
            System.Math.Max(a.Mesh.Vertices.Max(v => v.Z), b.Mesh.Vertices.Max(v => v.Z)));
        var marginM = System.Math.Max(profile.WallThicknessM * 2.0, 0.25);

        foreach (var v in resolutionLod.Vertices)
        {
            if (v.X < unionMin.X - marginM || v.X > unionMax.X + marginM)
                continue;
            if (v.Y < unionMin.Y - marginM || v.Y > unionMax.Y + marginM)
                continue;
            if (v.Z < unionMin.Z - marginM || v.Z > unionMax.Z + marginM)
                continue;
            samples.Add(v);
        }

        var hint = ComponentAxis(a, profile);
        var nb = ComponentAxis(b, profile);
        if (hint.Dot(nb) < 0)
            hint = hint.Scale(-1);
        if (hint.Length() < 1e-6)
            hint = profile.AxisZ;

        var mesh = FaceBoundsObbFitter.BuildPatchMesh(samples, hint, profile, PatchSurfaceKind.Wall)
            ?? ObbFitter.BuildPatchMeshTight(samples, hint, profile)
            ?? ObbFitter.BuildPatchMesh(samples, hint, profile);

        if (mesh == null)
            mesh = a.Mesh;

        mesh.Name = a.Name;
        return new MeshComponent { Name = a.Name, Mesh = mesh };
    }

    private static Vec3 ComponentAxis(MeshComponent comp, BuildingMeshProfile profile)
    {
        if (comp.Mesh.Vertices.Count == 8)
        {
            var parsed = BoxMeshParser.TryParse(comp.Mesh.Vertices);
            if (parsed != null)
                return parsed.AxisN.Normalized();
        }
        return profile.AxisZ.Normalized();
    }

    private static double ComponentSpan(IReadOnlyList<Vec3> verts)
    {
        if (verts.Count == 0)
            return 0;
        var xs = verts.Select(v => v.X).ToList();
        var ys = verts.Select(v => v.Y).ToList();
        var zs = verts.Select(v => v.Z).ToList();
        return System.Math.Max(
            xs.Max() - xs.Min(),
            System.Math.Max(ys.Max() - ys.Min(), zs.Max() - zs.Min()));
    }
}

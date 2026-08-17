using Cdm.GeoEngine.Core.IO;

namespace Cdm.GeoEngine.Core.Validation;

public sealed class GeometryValidationReport
{
    public string ModelId { get; init; } = "";
    public bool HasReference { get; init; }
    public bool Passed { get; init; }
    public int ReferenceComponents { get; init; }
    public int GeneratedComponents { get; init; }
    public int ReferenceVertices { get; init; }
    public int GeneratedVertices { get; init; }
    public double ComponentScore { get; init; }
    public double VertexScore { get; init; }
    public double OverallScore { get; init; }
    public double MinAreaUsed { get; init; }
    public double AngleUsed { get; init; }
    public int SkippedPatches { get; init; }
    public List<string> Messages { get; init; } = new();

    public static GeometryValidationReport Compare(
        string modelId,
        CorpusReference? reference,
        int generatedComponents,
        int generatedVertices,
        int skippedPatches,
        double minArea,
        double angle)
    {
        if (reference == null || reference.GeometryComponentCount <= 0)
        {
            return new GeometryValidationReport
            {
                ModelId = modelId,
                HasReference = false,
                Passed = generatedComponents > 0,
                GeneratedComponents = generatedComponents,
                GeneratedVertices = generatedVertices,
                SkippedPatches = skippedPatches,
                MinAreaUsed = minArea,
                AngleUsed = angle,
                OverallScore = generatedComponents > 0 ? 0.5 : 0,
                Messages = { "Keine Corpus-Referenz — nur Plausibilität geprüft." },
            };
        }

        var compDiff = Math.Abs(generatedComponents - reference.GeometryComponentCount);
        var compDenom = Math.Max(1, reference.GeometryComponentCount);
        var componentScore = Math.Clamp(1.0 - compDiff / (double)compDenom, 0, 1);

        var vertRatio = generatedVertices / (double)Math.Max(1, reference.GeometryVertices);
        var vertDiff = Math.Abs(1.0 - vertRatio);
        var vertexScore = Math.Clamp(1.0 - vertDiff / 0.5, 0, 1);

        var overall = componentScore * 0.65 + vertexScore * 0.35;
        var passed = overall >= 0.72
                     && compDiff <= Math.Max(3, reference.GeometryComponentCount * 0.2);

        var messages = new List<string>
        {
            $"Referenz: {reference.GeometryComponentCount} Components, {reference.GeometryVertices} Vertices",
            $"Generiert: {generatedComponents} Components, {generatedVertices} Vertices",
            $"Parameter: min_area={minArea:F3} angle={angle:F1}",
            $"Score: overall={overall:P0} components={componentScore:P0} vertices={vertexScore:P0}",
        };

        if (!passed)
            messages.Add("WARN: Abweichung zur Referenz-Geometry über Toleranz.");
        else
            messages.Add("OK: Innerhalb Corpus-Toleranz.");

        return new GeometryValidationReport
        {
            ModelId = modelId,
            HasReference = true,
            Passed = passed,
            ReferenceComponents = reference.GeometryComponentCount,
            GeneratedComponents = generatedComponents,
            ReferenceVertices = reference.GeometryVertices,
            GeneratedVertices = generatedVertices,
            ComponentScore = componentScore,
            VertexScore = vertexScore,
            OverallScore = overall,
            MinAreaUsed = minArea,
            AngleUsed = angle,
            SkippedPatches = skippedPatches,
            Messages = messages,
        };
    }
}

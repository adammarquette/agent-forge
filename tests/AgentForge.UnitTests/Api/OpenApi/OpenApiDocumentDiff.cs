using System.Text.Json.Nodes;

namespace AgentForge.UnitTests.Api.OpenApi;

/// <summary>
/// Says <i>what</i> differs between the committed OpenAPI document and the one the implementation renders,
/// one line per difference, so a red contract test names the drift instead of reporting two unequal files.
/// Routes are compared first and in their own words - a route the code serves that the spec does not
/// describe, and a route the spec describes that the code no longer serves - because that is the drift a
/// reader most needs to see; everything else is a structural walk that names the JSON path of each change.
/// </summary>
/// <remarks>
/// Written for the test alone and sharing nothing with the generator, so it cannot agree with it by
/// construction. A separate change
/// </remarks>
internal static class OpenApiDocumentDiff
{
    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    private const int ValuePreviewLength = 120;

    /// <summary>Every difference between <paramref name="committed"/> and <paramref name="generated"/>.</summary>
    /// <param name="committed">The document in the repository.</param>
    /// <param name="generated">The document the implementation renders now.</param>
    /// <returns>One human-readable line per difference; empty when the two describe the same surface.</returns>
    public static IReadOnlyList<string> Compare(JsonNode? committed, JsonNode? generated)
    {
        var differences = new List<string>();

        var committedPaths = committed?["paths"] as JsonObject;
        var generatedPaths = generated?["paths"] as JsonObject;
        CompareRoutes(committedPaths, generatedPaths, differences);

        CompareNode(Without(committed, "paths"), Without(generated, "paths"), "$", differences);
        foreach (var path in SharedKeys(committedPaths, generatedPaths))
        {
            // An operation on one side only was already reported as a route; walk everything else.
            var committedItem = committedPaths![path];
            var generatedItem = generatedPaths![path];
            var unshared = OperationKeys(committedItem).Union(OperationKeys(generatedItem), StringComparer.Ordinal)
                .Except(OperationKeys(committedItem).Intersect(OperationKeys(generatedItem), StringComparer.Ordinal), StringComparer.Ordinal)
                .ToArray();
            CompareNode(Without(committedItem, unshared), Without(generatedItem, unshared), $"$.paths['{path}']", differences);
        }

        return differences;
    }

    private static List<string> OperationKeys(JsonNode? pathItem) =>
        pathItem is JsonObject item
            ? item.Select(p => p.Key).Where(key => HttpMethods.Contains(key, StringComparer.Ordinal)).ToList()
            : [];

    private static void CompareRoutes(JsonObject? committed, JsonObject? generated, List<string> differences)
    {
        var committedRoutes = Routes(committed);
        var generatedRoutes = Routes(generated);

        differences.AddRange(generatedRoutes.Except(committedRoutes, StringComparer.Ordinal)
            .Select(route => $"route served by the implementation but missing from the committed spec: {route}"));
        differences.AddRange(committedRoutes.Except(generatedRoutes, StringComparer.Ordinal)
            .Select(route => $"route in the committed spec that the implementation no longer serves: {route}"));
    }

    private static List<string> Routes(JsonObject? paths)
    {
        if (paths is null)
        {
            return [];
        }

        return [.. paths
            .SelectMany(path => OperationKeys(path.Value)
                .Select(method => $"{method.ToUpperInvariant()} {path.Key}"))
            .Order(StringComparer.Ordinal)];
    }

    private static void CompareNode(JsonNode? committed, JsonNode? generated, string path, List<string> differences)
    {
        switch (committed, generated)
        {
            case (JsonObject committedObject, JsonObject generatedObject):
                foreach (var key in generatedObject.Select(p => p.Key).Except(committedObject.Select(p => p.Key), StringComparer.Ordinal))
                {
                    differences.Add($"{Child(path, key)}: in the implementation, missing from the committed spec (implementation: {Preview(generatedObject[key])})");
                }

                foreach (var key in committedObject.Select(p => p.Key).Except(generatedObject.Select(p => p.Key), StringComparer.Ordinal))
                {
                    differences.Add($"{Child(path, key)}: in the committed spec, no longer in the implementation (committed: {Preview(committedObject[key])})");
                }

                foreach (var key in SharedKeys(committedObject, generatedObject))
                {
                    CompareNode(committedObject[key], generatedObject[key], Child(path, key), differences);
                }

                break;

            case (JsonArray committedArray, JsonArray generatedArray) when committedArray.Count == generatedArray.Count:
                for (var index = 0; index < committedArray.Count; index++)
                {
                    CompareNode(committedArray[index], generatedArray[index], $"{path}[{index}]", differences);
                }

                break;

            default:
                if (!JsonNode.DeepEquals(committed, generated))
                {
                    differences.Add($"{path}: committed spec has {Preview(committed)}, implementation has {Preview(generated)}");
                }

                break;
        }
    }

    private static List<string> SharedKeys(JsonObject? left, JsonObject? right) =>
        left is null || right is null
            ? []
            : left.Select(p => p.Key).Intersect(right.Select(p => p.Key), StringComparer.Ordinal).ToList();

    private static JsonNode? Without(JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject source)
        {
            return node?.DeepClone();
        }

        var copy = (JsonObject)source.DeepClone();
        foreach (var key in keys)
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static string Child(string path, string key) =>
        key.All(c => char.IsLetterOrDigit(c) || c == '_') ? $"{path}.{key}" : $"{path}['{key}']";

    private static string Preview(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length <= ValuePreviewLength ? text : text[..ValuePreviewLength] + "...";
    }
}

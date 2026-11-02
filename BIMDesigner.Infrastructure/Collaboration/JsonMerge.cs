using System.Text.Json.Nodes;

namespace BIMDesigner.Infrastructure.Collaboration;

/// <summary>
/// A three-way merge of saved projects: the issue both started from, one person's copy and the other's.
/// Where only one changed something, theirs is taken; where both did the same, it is kept; where both
/// changed it differently, the central's is kept and the place said. Objects are merged property by
/// property; lists of things with ids - every element, type and level - item by item, an item made by
/// either kept, one taken away by either gone unless the other changed it. Ownership keeps two people
/// from changing the same element, so what clashes is what nobody owns: a schedule, a sheet's settings.
/// </summary>
public static class JsonMerge
{
    public sealed record Result(JsonNode? Merged, IReadOnlyList<string> Conflicts);

    public static Result Merge(JsonNode? common, JsonNode? mine, JsonNode? theirs)
    {
        var conflicts = new List<string>();
        var merged = Merge(common, mine, theirs, "$", conflicts);
        return new Result(merged, conflicts);
    }

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static JsonNode? Merge(JsonNode? common, JsonNode? mine, JsonNode? theirs, string path, List<string> conflicts)
    {
        if (Same(mine, common)) return theirs?.DeepClone();
        if (Same(theirs, common)) return mine?.DeepClone();
        if (Same(mine, theirs)) return mine?.DeepClone();

        if (mine is JsonObject mineObject && theirs is JsonObject theirObject && common is null or JsonObject)
        {
            var commonObject = common as JsonObject;
            var result = new JsonObject();
            var keys = theirObject.Select(pair => pair.Key).Concat(mineObject.Select(pair => pair.Key)).Distinct().ToList();
            foreach (var key in keys)
            {
                var value = Merge(commonObject?[key], mineObject[key], theirObject[key], $"{path}.{key}", conflicts);
                if (value is not null || theirObject.ContainsKey(key) && theirObject[key] is null) result[key] = value;
            }

            return result;
        }

        if (mine is JsonArray mineArray && theirs is JsonArray theirArray && common is null or JsonArray
            && Keyed(mineArray) is { } mineItems && Keyed(theirArray) is { } theirItems && Keyed(common as JsonArray ?? new JsonArray()) is { } commonItems)
        {
            // Theirs in their order, then what only mine has, in mine.
            var result = new JsonArray();
            var keys = theirItems.Keys.Concat(mineItems.Keys).Distinct().ToList();
            foreach (var key in keys)
            {
                var value = Merge(commonItems.GetValueOrDefault(key), mineItems.GetValueOrDefault(key), theirItems.GetValueOrDefault(key), $"{path}[{key}]", conflicts);
                if (value is not null) result.Add(value);
            }

            return result;
        }

        conflicts.Add(path);
        return theirs?.DeepClone();
    }

    /// <summary>
    /// A list's items by their key - an id, an element's id, or the ids it is made of together - in
    /// order; null where its items are not all objects with a key each of their own.
    /// </summary>
    private static Dictionary<string, JsonNode>? Keyed(JsonArray array)
    {
        var items = new Dictionary<string, JsonNode>();
        foreach (var item in array)
        {
            if (item is not JsonObject entry) return null;
            string? key;
            if (entry["id"] is JsonValue id) key = id.ToJsonString();
            else
            {
                var parts = entry.Where(pair => pair.Key.EndsWith("Id", StringComparison.Ordinal) && pair.Value is JsonValue)
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value!.ToJsonString()}").ToList();
                key = parts.Count == 0 ? null : string.Join("|", parts);
            }

            if (key is null || !items.TryAdd(key, entry)) return null;
        }

        return items;
    }
}

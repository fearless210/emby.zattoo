using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    /// <summary>
    /// Walks a provider response and records which fields exist, how often, and
    /// with which JSON kinds. It never records a value, so the result can be
    /// pasted into an issue without leaking account data.
    /// </summary>
    public static class ZattooFieldSurvey
    {
        private const int MaximumDepth = 6;

        /// <summary>
        /// Above this many properties sharing one value kind, an object is read as
        /// a map keyed by identifiers rather than as a record. The guide indexes
        /// its programs by channel ID, which would otherwise produce one distinct
        /// path per channel and bury the field names.
        /// </summary>
        private const int DynamicKeyThreshold = 8;

        /// <summary>
        /// Above this many distinct values, a field is not a vocabulary and its
        /// values stop being collected. This keeps a mistaken entry in the
        /// vocabulary list from dumping content such as titles.
        /// </summary>
        private const int VocabularySampleCap = 60;

        /// <summary>
        /// Leaf names whose values are catalogue vocabularies rather than account
        /// data: categories, genres, quality levels and availability.
        /// </summary>
        private static readonly HashSet<string> VocabularyFields =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "c", "c_ids", "g", "level", "availability", "stream_types",
                "content_type", "yp_r",
            };

        public static ZattooFieldSection Analyze(string name, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                throw new ArgumentException(
                    "A response body is required.",
                    nameof(content));
            }

            var fields = new Dictionary<string, FieldAccumulator>(StringComparer.Ordinal);
            try
            {
                using (var document = JsonDocument.Parse(content))
                {
                    Walk(document.RootElement, string.Empty, 0, fields);
                }
            }
            catch (JsonException)
            {
                throw new ZattooProtocolException(
                    "The response could not be parsed for a field survey.");
            }

            return new ZattooFieldSection
            {
                Name = name,
                DocumentBytes = Encoding.UTF8.GetByteCount(content),
                Fields = fields.Values
                    .Select(accumulator => accumulator.ToUsage())
                    .OrderBy(usage => usage.Path, StringComparer.Ordinal)
                    .ToArray(),
                Vocabularies = fields.Values
                    .Where(accumulator => accumulator.HasVocabulary)
                    .Select(accumulator => accumulator.ToVocabulary())
                    .OrderBy(vocabulary => vocabulary.Path, StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        private static void Walk(
            JsonElement element,
            string path,
            int depth,
            IDictionary<string, FieldAccumulator> fields)
        {
            if (depth > MaximumDepth)
            {
                return;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var keyedByIdentifier = IsDynamicKeyMap(element);
                    foreach (var property in element.EnumerateObject())
                    {
                        var name = keyedByIdentifier ? "*" : property.Name;
                        var childPath = path.Length == 0 ? name : path + "." + name;
                        Record(fields, childPath, property.Value);
                        Walk(property.Value, childPath, depth + 1, fields);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, path + "[]", depth + 1, fields);
                    }

                    break;
            }
        }

        private static void Record(
            IDictionary<string, FieldAccumulator> fields,
            string path,
            JsonElement value)
        {
            if (!fields.TryGetValue(path, out var accumulator))
            {
                var leaf = path.Split('.').Last().Replace("[]", string.Empty);
                accumulator = new FieldAccumulator(
                    path,
                    VocabularyFields.Contains(leaf));
                fields[path] = accumulator;
            }

            accumulator.Add(value);
        }

        private static bool IsDynamicKeyMap(JsonElement element)
        {
            var count = 0;
            JsonValueKind? sharedKind = null;
            foreach (var property in element.EnumerateObject())
            {
                if (sharedKind == null)
                {
                    sharedKind = property.Value.ValueKind;
                }
                else if (sharedKind != property.Value.ValueKind)
                {
                    return false;
                }

                count++;
            }

            return count >= DynamicKeyThreshold
                && (sharedKind == JsonValueKind.Array
                    || sharedKind == JsonValueKind.Object);
        }

        private static bool IsPopulated(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return false;
                case JsonValueKind.String:
                    return !string.IsNullOrWhiteSpace(value.GetString());
                case JsonValueKind.Array:
                    return value.GetArrayLength() > 0;
                case JsonValueKind.Object:
                    return value.EnumerateObject().Any();
                default:
                    return true;
            }
        }

        private static string DescribeKind(JsonValueKind kind)
        {
            switch (kind)
            {
                case JsonValueKind.String:
                    return "string";
                case JsonValueKind.Number:
                    return "number";
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return "bool";
                case JsonValueKind.Array:
                    return "array";
                case JsonValueKind.Object:
                    return "object";
                case JsonValueKind.Null:
                    return "null";
                default:
                    return "unknown";
            }
        }

        private sealed class FieldAccumulator
        {
            private readonly SortedSet<string> kinds =
                new SortedSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> vocabulary =
                new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly string path;
            private readonly bool collectsVocabulary;
            private int occurrences;
            private int populatedOccurrences;
            private bool truncated;

            public FieldAccumulator(string path, bool collectsVocabulary)
            {
                this.path = path;
                this.collectsVocabulary = collectsVocabulary;
            }

            public bool HasVocabulary =>
                collectsVocabulary && (vocabulary.Count > 0 || truncated);

            public void Add(JsonElement value)
            {
                occurrences++;
                if (IsPopulated(value))
                {
                    populatedOccurrences++;
                }

                kinds.Add(DescribeKind(value.ValueKind));
                if (collectsVocabulary)
                {
                    CollectVocabulary(value);
                }
            }

            public ZattooFieldVocabulary ToVocabulary()
            {
                return new ZattooFieldVocabulary
                {
                    Path = path,
                    Truncated = truncated,
                    Values = vocabulary
                        .OrderByDescending(entry => entry.Value)
                        .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => new ZattooVocabularyValue
                        {
                            Value = entry.Key,
                            Occurrences = entry.Value,
                        })
                        .ToArray(),
                };
            }

            private void CollectVocabulary(JsonElement value)
            {
                if (truncated)
                {
                    // Once a field proved not to be a vocabulary, nothing more of
                    // it is collected for the rest of the document.
                    return;
                }

                if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        CollectVocabulary(item);
                    }

                    return;
                }

                string? text = null;
                if (value.ValueKind == JsonValueKind.String)
                {
                    text = value.GetString();
                }
                else if (value.ValueKind == JsonValueKind.Number)
                {
                    text = value.ToString();
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                if (vocabulary.TryGetValue(text!, out var count))
                {
                    vocabulary[text!] = count + 1;
                    return;
                }

                if (vocabulary.Count >= VocabularySampleCap)
                {
                    // Too many distinct values to be a vocabulary; stop collecting
                    // rather than risk exposing content.
                    truncated = true;
                    vocabulary.Clear();
                    return;
                }

                vocabulary[text!] = 1;
            }

            public ZattooFieldUsage ToUsage()
            {
                return new ZattooFieldUsage
                {
                    Path = path,
                    Occurrences = occurrences,
                    PopulatedOccurrences = populatedOccurrences,
                    ValueKinds = string.Join("|", kinds),
                };
            }
        }
    }
}

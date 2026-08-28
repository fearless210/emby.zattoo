using System;
using System.Collections.Generic;

namespace Emby.Zattoo.Models
{
    /// <summary>
    /// Names and shapes of the fields a provider account actually publishes.
    /// Values are never collected, only counted, so the result can be shared.
    /// </summary>
    public sealed class ZattooFieldInventory
    {
        public IReadOnlyList<ZattooFieldSection> Sections { get; set; } =
            Array.Empty<ZattooFieldSection>();
    }

    public sealed class ZattooFieldSection
    {
        public string Name { get; set; } = string.Empty;

        public int DocumentBytes { get; set; }

        public IReadOnlyList<ZattooFieldUsage> Fields { get; set; } =
            Array.Empty<ZattooFieldUsage>();

        /// <summary>Gets the distinct values of the fields known to be vocabularies.</summary>
        public IReadOnlyList<ZattooFieldVocabulary> Vocabularies { get; set; } =
            Array.Empty<ZattooFieldVocabulary>();
    }

    public sealed class ZattooFieldVocabulary
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets whether the field held more values than the sample cap.</summary>
        public bool Truncated { get; set; }

        public IReadOnlyList<ZattooVocabularyValue> Values { get; set; } =
            Array.Empty<ZattooVocabularyValue>();
    }

    public sealed class ZattooVocabularyValue
    {
        public string Value { get; set; } = string.Empty;

        public int Occurrences { get; set; }
    }

    public sealed class ZattooFieldUsage
    {
        /// <summary>Gets the dotted path of the field, arrays shown as [].</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets how many times the field appeared.</summary>
        public int Occurrences { get; set; }

        /// <summary>Gets how often it carried something other than null or empty.</summary>
        public int PopulatedOccurrences { get; set; }

        /// <summary>Gets the JSON kinds observed, such as "string" or "number".</summary>
        public string ValueKinds { get; set; } = string.Empty;
    }
}

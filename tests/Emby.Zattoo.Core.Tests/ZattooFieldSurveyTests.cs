using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooFieldSurveyTests
{
    private const string Document = """
        {
          "success": true,
          "channels": [
            {
              "cid": "ch-secret-one",
              "group_index": 0,
              "qualities": [
                { "level": "hd", "title": "Secret Channel", "drm_required": false }
              ]
            },
            {
              "cid": "ch-secret-two",
              "group_index": 1,
              "qualities": [
                { "level": "sd", "title": "", "drm_required": true }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Analyze_ReportsPathsOccurrencesAndKinds()
    {
        var section = ZattooFieldSurvey.Analyze("channels", Document);

        Assert.Equal("channels", section.Name);
        var cid = Assert.Single(
            section.Fields,
            field => field.Path == "channels[].cid");
        Assert.Equal(2, cid.Occurrences);
        Assert.Equal(2, cid.PopulatedOccurrences);
        Assert.Equal("string", cid.ValueKinds);

        var drm = Assert.Single(
            section.Fields,
            field => field.Path == "channels[].qualities[].drm_required");
        Assert.Equal(2, drm.Occurrences);
        Assert.Equal("bool", drm.ValueKinds);
    }

    [Fact]
    public void Analyze_CountsAnEmptyStringAsUnpopulated()
    {
        var section = ZattooFieldSurvey.Analyze("channels", Document);

        var title = Assert.Single(
            section.Fields,
            field => field.Path == "channels[].qualities[].title");
        Assert.Equal(2, title.Occurrences);
        Assert.Equal(1, title.PopulatedOccurrences);
    }

    [Fact]
    public void Analyze_NeverExposesAValue()
    {
        var section = ZattooFieldSurvey.Analyze("channels", Document);

        var rendered = string.Join(
            "\n",
            section.Fields.Select(field =>
                field.Path + field.ValueKinds + field.Occurrences));

        Assert.DoesNotContain("secret", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret Channel", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_SortsFieldsByPath()
    {
        var section = ZattooFieldSurvey.Analyze("channels", Document);

        var paths = section.Fields.Select(field => field.Path).ToArray();
        Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), paths);
    }

    [Fact]
    public void Analyze_CollapsesAnObjectKeyedByIdentifiers()
    {
        var programsByChannel = string.Join(
            ",",
            Enumerable.Range(0, 9).Select(index =>
                $"\"ch-{index}\": [{{ \"id\": \"p{index}\", \"tms_id\": \"EP00{index}\" }}]"));
        var guide = "{\"channels\": {" + programsByChannel + "}}";

        var section = ZattooFieldSurvey.Analyze("guide", guide);

        // One path for every channel, not one path per channel identifier.
        var programs = Assert.Single(
            section.Fields,
            field => field.Path == "channels.*");
        Assert.Equal(9, programs.Occurrences);
        var tmsId = Assert.Single(
            section.Fields,
            field => field.Path == "channels.*[].tms_id");
        Assert.Equal(9, tmsId.Occurrences);
        Assert.DoesNotContain(section.Fields, field => field.Path.Contains("ch-0"));
    }

    [Fact]
    public void Analyze_KeepsTheNamesOfARecordWithFewProperties()
    {
        var section = ZattooFieldSurvey.Analyze(
            "session",
            "{\"active\": true, \"account\": {\"service_country\": \"ch\"}}");

        Assert.Contains(section.Fields, field => field.Path == "account.service_country");
        Assert.DoesNotContain(section.Fields, field => field.Path.Contains("*"));
    }

    [Fact]
    public void Analyze_CollectsTheValuesOfVocabularyFields()
    {
        var section = ZattooFieldSurvey.Analyze(
            "guide",
            "{\"programs\":["
                + "{\"t\":\"Secret title\",\"g\":[\"Sport\",\"Football\"]},"
                + "{\"t\":\"Another secret\",\"g\":[\"Sport\"]}"
                + "]}");

        var genres = Assert.Single(section.Vocabularies);
        Assert.Equal("programs[].g", genres.Path);
        Assert.False(genres.Truncated);
        Assert.Collection(
            genres.Values,
            first =>
            {
                Assert.Equal("Sport", first.Value);
                Assert.Equal(2, first.Occurrences);
            },
            second => Assert.Equal("Football", second.Value));

        // A title is content, never a vocabulary.
        Assert.DoesNotContain(section.Vocabularies, entry => entry.Path.EndsWith(".t"));
    }

    [Fact]
    public void Analyze_StopsCollectingAFieldThatIsNotAVocabulary()
    {
        var values = string.Join(
            ",",
            Enumerable.Range(0, 80).Select(index => $"{{\"g\":[\"value-{index}\"]}}"));

        var section = ZattooFieldSurvey.Analyze("guide", "{\"programs\":[" + values + "]}");

        var genres = Assert.Single(section.Vocabularies);
        Assert.True(genres.Truncated);
        Assert.Empty(genres.Values);
    }

    [Fact]
    public void Analyze_RejectsMalformedJson()
    {
        Assert.Throws<ZattooProtocolException>(
            () => ZattooFieldSurvey.Analyze("channels", "{ not json"));
    }
}

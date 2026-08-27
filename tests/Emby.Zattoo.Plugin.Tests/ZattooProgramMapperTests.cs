using Emby.Zattoo.Models;
using Emby.Zattoo.Plugin.LiveTv;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooProgramMapperTests
{
    [Fact]
    public void Map_UsesStableShowIdAndMapsGuideMetadata()
    {
        var program = new ZattooProgram
        {
            Id = "fixture-program",
            ChannelId = "tsr1",
            Name = "Magazine fictif",
            EpisodeTitle = "Épisode pilote",
            Overview = "Description fictive.",
            StartDate = DateTimeOffset.FromUnixTimeSeconds(1800000900),
            EndDate = DateTimeOffset.FromUnixTimeSeconds(1800002700),
            Genres = new[] { "Magazine", "Culture" },
            SeasonNumber = 2,
            EpisodeNumber = 3,
            ImageUrl = "https://images.invalid/program.jpg",
        };

        var result = ZattooProgramMapper.Map(program);

        Assert.Equal("fixture-program", result.ShowId);
        Assert.Equal(program.Name, result.Name);
        Assert.Equal(program.EpisodeTitle, result.EpisodeTitle);
        Assert.Equal(program.Overview, result.Overview);
        Assert.Equal(program.StartDate, result.StartDate);
        Assert.Equal(program.EndDate, result.EndDate);
        Assert.Equal(program.Genres, result.Genres);
        Assert.Equal(2, result.SeasonNumber);
        Assert.Equal(3, result.EpisodeNumber);
        Assert.Equal(program.ImageUrl, result.ImageUrl);
        Assert.True(result.IsSeries);
        Assert.Null(result.Id);
        Assert.Null(result.ChannelId);
    }

    [Fact]
    public void Map_DoesNotMarkPlainBroadcastAsSeries()
    {
        var result = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "fixture-program",
            Name = "Journal fictif",
        });

        Assert.False(result.IsSeries);
    }

    [Fact]
    public void Map_DoesNotInferSeriesOnlyFromEpisodeTitle()
    {
        var result = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "fixture-program",
            Name = "Magazine fictif",
            EpisodeTitle = "Édition spéciale",
        });

        Assert.False(result.IsSeries);
    }

    [Fact]
    public void Map_RejectsNullProgram()
    {
        Assert.Throws<ArgumentNullException>(() => ZattooProgramMapper.Map(null!));
    }
}

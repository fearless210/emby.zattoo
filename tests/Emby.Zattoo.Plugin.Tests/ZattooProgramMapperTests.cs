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

    [Fact]
    public void Map_UsesTheContentIdentifierAsShowId()
    {
        var info = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "broadcast-1",
            ContentId = "EP0123456700010",
            Name = "Episode",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow.AddHours(1),
        });

        // Emby matches repeats through ShowId, so it has to identify the content
        // rather than one airing of it.
        Assert.Equal("EP0123456700010", info.ShowId);
    }

    [Fact]
    public void Map_FallsBackToTheBroadcastIdWithoutAContentIdentifier()
    {
        var info = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "broadcast-1",
            Name = "Episode",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow.AddHours(1),
        });

        Assert.Equal("broadcast-1", info.ShowId);
    }

    [Fact]
    public void Map_ReportsSeriesYearAndRating()
    {
        var info = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "broadcast-1",
            Name = "Episode",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow.AddHours(1),
            IsSeries = true,
            ProductionYear = 1998,
            AgeRating = "12",
        });

        Assert.True(info.IsSeries);
        Assert.Equal(1998, info.ProductionYear);
        Assert.Equal("12", info.OfficialRating);
    }

    [Fact]
    public void Map_TreatsAnEpisodeNumberAsASeriesEvenWithoutTheProviderFlag()
    {
        var info = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "broadcast-1",
            Name = "Episode",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow.AddHours(1),
            EpisodeNumber = 4,
        });

        Assert.True(info.IsSeries);
    }

    [Theory]
    [InlineData(5, true, false, false, false)]
    [InlineData(4, false, true, false, false)]
    [InlineData(3, false, false, true, false)]
    [InlineData(2, false, false, false, true)]
    [InlineData(6, false, false, false, false)]
    [InlineData(7, false, false, false, false)]
    public void Map_DerivesCategoryFlagsFromTheProviderIdentifiers(
        int categoryId,
        bool isMovie,
        bool isSports,
        bool isNews,
        bool isKids)
    {
        var info = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "broadcast-1",
            Name = "Program",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow.AddHours(1),
            CategoryIds = new[] { categoryId },
        });

        Assert.Equal(isMovie, info.IsMovie);
        Assert.Equal(isSports, info.IsSports);
        Assert.Equal(isNews, info.IsNews);
        Assert.Equal(isKids, info.IsKids);
    }

    [Fact]
    public void Map_IgnoresAnUnknownCategory()
    {
        var info = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "broadcast-1",
            Name = "Program",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow.AddHours(1),
            CategoryIds = new[] { 42 },
        });

        // An identifier the provider adds later must not be guessed at.
        Assert.False(info.IsMovie);
        Assert.False(info.IsSports);
        Assert.False(info.IsNews);
        Assert.False(info.IsKids);
        Assert.False(info.IsSeries);
    }

    [Fact]
    public void Map_TreatsTheSeriesCategoryAsASeries()
    {
        var info = ZattooProgramMapper.Map(new ZattooProgram
        {
            Id = "broadcast-1",
            Name = "Program",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow.AddHours(1),
            CategoryIds = new[] { 1 },
        });

        Assert.True(info.IsSeries);
    }
}

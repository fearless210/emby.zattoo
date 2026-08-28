using System;
using System.Linq;
using Emby.Zattoo.Models;
using MediaBrowser.Controller.LiveTv;

namespace Emby.Zattoo.Plugin.LiveTv
{
    public static class ZattooProgramMapper
    {
        // Category identifiers observed in the provider guide. They are matched on
        // the number rather than on the name, which is localised and would break
        // the mapping as soon as the account language changes.
        private const int SeriesCategory = 1;
        private const int ChildrenCategory = 2;
        private const int NewsCategory = 3;
        private const int SportsCategory = 4;
        private const int MoviesCategory = 5;

        public static ProgramInfo Map(ZattooProgram program)
        {
            if (program == null)
            {
                throw new ArgumentNullException(nameof(program));
            }

            return new ProgramInfo
            {
                // Emby documents ShowId as an identifier of the content that stays
                // the same whatever the air time and channel, which is what the
                // provider content identifier is. The broadcast identifier only
                // serves as a fallback, and Emby still derives a unique program
                // entry from the start time and the channel.
                ShowId = string.IsNullOrWhiteSpace(program.ContentId)
                    ? program.Id
                    : program.ContentId,
                Name = program.Name,
                EpisodeTitle = program.EpisodeTitle,
                Overview = program.Overview,
                StartDate = program.StartDate,
                EndDate = program.EndDate,
                Genres = program.Genres.ToList(),
                SeasonNumber = program.SeasonNumber,
                EpisodeNumber = program.EpisodeNumber,
                ImageUrl = program.ImageUrl,
                ProductionYear = program.ProductionYear,
                OfficialRating = program.AgeRating,
                IsMovie = HasCategory(program, MoviesCategory),
                IsSports = HasCategory(program, SportsCategory),
                IsNews = HasCategory(program, NewsCategory),
                IsKids = HasCategory(program, ChildrenCategory),
                IsSeries = program.IsSeries
                    || HasCategory(program, SeriesCategory)
                    || program.SeasonNumber.HasValue
                    || program.EpisodeNumber.HasValue,
            };
        }

        private static bool HasCategory(ZattooProgram program, int categoryId)
        {
            return program.CategoryIds.Contains(categoryId);
        }
    }
}

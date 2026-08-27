using System;
using System.Linq;
using Emby.Zattoo.Models;
using MediaBrowser.Controller.LiveTv;

namespace Emby.Zattoo.Plugin.LiveTv
{
    public static class ZattooProgramMapper
    {
        public static ProgramInfo Map(ZattooProgram program)
        {
            if (program == null)
            {
                throw new ArgumentNullException(nameof(program));
            }

            return new ProgramInfo
            {
                ShowId = program.Id,
                Name = program.Name,
                EpisodeTitle = program.EpisodeTitle,
                Overview = program.Overview,
                StartDate = program.StartDate,
                EndDate = program.EndDate,
                Genres = program.Genres.ToList(),
                SeasonNumber = program.SeasonNumber,
                EpisodeNumber = program.EpisodeNumber,
                ImageUrl = program.ImageUrl,
                IsSeries = program.SeasonNumber.HasValue
                    || program.EpisodeNumber.HasValue,
            };
        }
    }
}

using System;
using System.Globalization;
using System.Linq;
using Emby.Zattoo.Models;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Emby.Zattoo.Plugin.LiveTv
{
    public static class ZattooChannelMapper
    {
        public static ChannelInfo Map(ZattooChannel channel)
        {
            if (channel == null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            return new ChannelInfo
            {
                Id = channel.Id,
                TunerChannelId = channel.Id,
                Name = channel.Name,
                Number = channel.Number.ToString(CultureInfo.InvariantCulture),
                SortIndexNumber = channel.Number,
                ImageUrl = channel.LogoUrl,
                IsFavorite = channel.IsFavorite,
                IsHD = channel.Qualities.Any(
                    quality => quality.IsAvailable && quality.Height >= 720),
                ChannelType = ChannelType.TV,
            };
        }
    }
}

using System;
using System.Collections.Generic;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>Tells whether a stream that ended on its own is worth reopening.</summary>
    /// <remarks>
    /// A signed Zattoo URL was measured serving three hours of continuous
    /// playback, so a stream never dies on schedule. When it dies anyway the
    /// useful question is not what expired but whether a fresh URL has a chance:
    /// reopening a stream that cannot work only burns the concurrent stream
    /// capacity of the account and hides the real cause.
    /// </remarks>
    public static class ZattooStreamFailureClassifier
    {
        private static readonly string[] LocalFailureMarkers =
        {
            "Unrecognized option",
            "Option not found",
            "Error splitting the argument list",
            "Protocol not found",
            "Unknown encoder",
            "Unknown decoder",
            "No such file or directory",
            "Permission denied",
        };

        private static readonly string[] MediaRequestMarkers =
        {
            "Server returned 4",
            "Server returned 5",
            "HTTP error",
            "Failed to open segment",
            "Failed to reload playlist",
            "403 Forbidden",
            "401 Unauthorized",
        };

        private static readonly string[] ConnectionMarkers =
        {
            "Connection reset by peer",
            "Connection timed out",
            "Connection refused",
            "Error in the pull function",
            "Input/output error",
            "Broken pipe",
            "Network is unreachable",
            "Name or service not known",
            "Temporary failure in name resolution",
            "Operation timed out",
            "End of file",
        };

        /// <summary>
        /// Classifies an FFmpeg process that ended while the stream was still
        /// wanted. Only the recent standard error lines are read, and no provider
        /// text ever reaches the result: the reason comes from a fixed vocabulary,
        /// so a signed URL printed by FFmpeg cannot travel with it.
        /// </summary>
        public static ZattooStreamFailure Classify(
            int exitCode,
            IEnumerable<string> recentStandardError)
        {
            if (recentStandardError == null)
            {
                throw new ArgumentNullException(nameof(recentStandardError));
            }

            var sawMediaRequestFailure = false;
            var sawConnectionFailure = false;
            foreach (var line in recentStandardError)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (ContainsAny(line, LocalFailureMarkers))
                {
                    // FFmpeg could not run the command at all. A new URL changes
                    // nothing, and retrying would loop on the same argument.
                    return new ZattooStreamFailure(
                        ZattooStreamFailureKind.Permanent,
                        ZattooStreamFailureReason.LocalFfmpegFailure,
                        exitCode);
                }

                if (ContainsAny(line, MediaRequestMarkers))
                {
                    sawMediaRequestFailure = true;
                    continue;
                }

                if (ContainsAny(line, ConnectionMarkers))
                {
                    sawConnectionFailure = true;
                }
            }

            if (sawMediaRequestFailure)
            {
                return Recoverable(ZattooStreamFailureReason.MediaRequestFailed, exitCode);
            }

            if (sawConnectionFailure)
            {
                return Recoverable(ZattooStreamFailureReason.ConnectionLost, exitCode);
            }

            if (exitCode == 0)
            {
                // FFmpeg reporting success in the middle of live television means
                // the provider stopped handing out segments. Left unreported it
                // reaches Emby as a normal end of stream, and a recording is
                // silently cut short.
                return Recoverable(ZattooStreamFailureReason.NoDataFromProvider, exitCode);
            }

            return Recoverable(ZattooStreamFailureReason.Unknown, exitCode);
        }

        private static ZattooStreamFailure Recoverable(
            ZattooStreamFailureReason reason,
            int exitCode)
        {
            return new ZattooStreamFailure(
                ZattooStreamFailureKind.Recoverable,
                reason,
                exitCode);
        }

        private static bool ContainsAny(string line, string[] markers)
        {
            foreach (var marker in markers)
            {
                if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether reopening the stream with a fresh URL has a chance.</summary>
    public enum ZattooStreamFailureKind
    {
        /// <summary>A new stream request may restore playback.</summary>
        Recoverable,

        /// <summary>Nothing a new stream request can repair.</summary>
        Permanent,
    }

    /// <summary>What ended the stream, in a vocabulary the plugin controls.</summary>
    public enum ZattooStreamFailureReason
    {
        /// <summary>FFmpeg failed without saying anything recognisable.</summary>
        Unknown,

        /// <summary>FFmpeg reported success while live television was still expected.</summary>
        NoDataFromProvider,

        /// <summary>A playlist or a segment was refused or could not be loaded.</summary>
        MediaRequestFailed,

        /// <summary>The connection to the provider was lost.</summary>
        ConnectionLost,

        /// <summary>FFmpeg could not run the requested command.</summary>
        LocalFfmpegFailure,
    }

    /// <summary>One classified end of stream.</summary>
    public sealed class ZattooStreamFailure
    {
        public ZattooStreamFailure(
            ZattooStreamFailureKind kind,
            ZattooStreamFailureReason reason,
            int exitCode)
        {
            Kind = kind;
            Reason = reason;
            ExitCode = exitCode;
        }

        public ZattooStreamFailureKind Kind { get; }

        public ZattooStreamFailureReason Reason { get; }

        public int ExitCode { get; }

        /// <summary>Gets a log sentence built only from the plugin vocabulary.</summary>
        public string Description
        {
            get
            {
                switch (Reason)
                {
                    case ZattooStreamFailureReason.NoDataFromProvider:
                        return "the provider stopped sending data";
                    case ZattooStreamFailureReason.MediaRequestFailed:
                        return "a playlist or segment request was refused";
                    case ZattooStreamFailureReason.ConnectionLost:
                        return "the connection to the provider was lost";
                    case ZattooStreamFailureReason.LocalFfmpegFailure:
                        return "FFmpeg could not run the requested command";
                    default:
                        return "FFmpeg stopped without a recognised reason";
                }
            }
        }
    }
}

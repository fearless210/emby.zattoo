using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    internal sealed class ZattooGuideDetailsCacheStore
    {
        private const int FormatVersion = 1;
        private const int CompactionSlack = 5000;

        private readonly object syncRoot = new object();
        private readonly string path;
        private readonly string scope;
        private long journalEntries;

        public ZattooGuideDetailsCacheStore(string path, string scope)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A cache path is required.", nameof(path));
            }

            if (string.IsNullOrWhiteSpace(scope))
            {
                throw new ArgumentException("A cache scope is required.", nameof(scope));
            }

            this.path = path;
            this.scope = scope;
        }

        public ZattooGuideDetailsCacheLoadResult Load()
        {
            lock (syncRoot)
            {
                if (!File.Exists(path))
                {
                    return new ZattooGuideDetailsCacheLoadResult(
                        Array.Empty<ZattooGuideDetailsCacheRecord>(),
                        false);
                }

                var entries = new Dictionary<
                    string,
                    ZattooGuideDetailsCacheRecord>(StringComparer.Ordinal);
                var ignored = false;
                journalEntries = 0;
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    ZattooGuideDetailsCacheEnvelope? envelope;
                    try
                    {
                        envelope = JsonSerializer.Deserialize<
                            ZattooGuideDetailsCacheEnvelope>(line);
                    }
                    catch (JsonException)
                    {
                        ignored = true;
                        continue;
                    }

                    if (envelope == null
                        || envelope.Version != FormatVersion
                        || !string.Equals(envelope.Scope, scope, StringComparison.Ordinal)
                        || envelope.Entries == null)
                    {
                        ignored = true;
                        continue;
                    }

                    foreach (var entry in envelope.Entries)
                    {
                        journalEntries++;
                        if (!entry.IsValid())
                        {
                            ignored = true;
                            continue;
                        }

                        entries[entry.Id] = entry;
                    }
                }

                return new ZattooGuideDetailsCacheLoadResult(
                    entries.Values.ToArray(),
                    ignored || ShouldCompactLocked(entries.Count));
            }
        }

        public bool Append(IReadOnlyCollection<ZattooGuideDetailsCacheRecord> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (entries.Count == 0)
            {
                return true;
            }

            lock (syncRoot)
            {
                try
                {
                    EnsureDirectory();
                    var envelope = new ZattooGuideDetailsCacheEnvelope
                    {
                        Version = FormatVersion,
                        Scope = scope,
                        Entries = entries.ToArray(),
                    };
                    var line = JsonSerializer.Serialize(envelope);
                    using (var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    using (var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
                    {
                        writer.WriteLine(line);
                        writer.Flush();
                    }

                    journalEntries += entries.Count;
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        public bool ShouldCompact(int liveEntries)
        {
            lock (syncRoot)
            {
                return ShouldCompactLocked(liveEntries);
            }
        }

        public bool Replace(IReadOnlyCollection<ZattooGuideDetailsCacheRecord> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            lock (syncRoot)
            {
                var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    EnsureDirectory();
                    var envelope = new ZattooGuideDetailsCacheEnvelope
                    {
                        Version = FormatVersion,
                        Scope = scope,
                        Entries = entries.ToArray(),
                    };
                    using (var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    using (var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
                    {
                        writer.WriteLine(JsonSerializer.Serialize(envelope));
                        writer.Flush();
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(temporaryPath, path, null);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            File.Delete(path);
                            File.Move(temporaryPath, path);
                        }
                    }
                    else
                    {
                        File.Move(temporaryPath, path);
                    }

                    journalEntries = entries.Count;
                    return true;
                }
                catch (IOException)
                {
                    TryDeleteTemporaryFile(temporaryPath);
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    TryDeleteTemporaryFile(temporaryPath);
                    return false;
                }
            }
        }

        private bool ShouldCompactLocked(int liveEntries)
        {
            return journalEntries > checked((long)liveEntries * 2 + CompactionSlack);
        }

        private void EnsureDirectory()
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void TryDeleteTemporaryFile(string temporaryPath)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class ZattooGuideDetailsCacheEnvelope
        {
            public int Version { get; set; }

            public string Scope { get; set; } = string.Empty;

            public ZattooGuideDetailsCacheRecord[] Entries { get; set; }
                = Array.Empty<ZattooGuideDetailsCacheRecord>();
        }
    }

    internal sealed class ZattooGuideDetailsCacheLoadResult
    {
        public ZattooGuideDetailsCacheLoadResult(
            IReadOnlyList<ZattooGuideDetailsCacheRecord> entries,
            bool needsCompaction)
        {
            Entries = entries;
            NeedsCompaction = needsCompaction;
        }

        public IReadOnlyList<ZattooGuideDetailsCacheRecord> Entries { get; }

        public bool NeedsCompaction { get; }
    }

    internal sealed class ZattooGuideDetailsCacheRecord
    {
        public string Id { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;

        public long ExpiresAt { get; set; }

        public long RefreshAfter { get; set; }

        public bool HasDetails { get; set; }

        public string? EpisodeTitle { get; set; }

        public string? Overview { get; set; }

        public string[] Genres { get; set; } = Array.Empty<string>();

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(Id)
                && !string.IsNullOrWhiteSpace(Fingerprint)
                && ExpiresAt > 0
                && RefreshAfter > 0;
        }

        public ZattooProgramDetails? ToDetails()
        {
            if (!HasDetails)
            {
                return null;
            }

            return new ZattooProgramDetails
            {
                Id = Id,
                EpisodeTitle = EpisodeTitle,
                Overview = Overview,
                Genres = Genres ?? Array.Empty<string>(),
                SeasonNumber = SeasonNumber,
                EpisodeNumber = EpisodeNumber,
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Zattoo.Infrastructure
{
    /// <summary>HTTP boundary kept injectable so the Zattoo protocol can be tested offline.</summary>
    public interface IZattooTransport : IDisposable
    {
        Task<ZattooTransportResponse> GetAsync(
            string relativePath,
            CancellationToken cancellationToken);

        Task<ZattooTransportResponse> PostFormAsync(
            string relativePath,
            IReadOnlyDictionary<string, string> fields,
            CancellationToken cancellationToken);

        /// <summary>Expires provider cookies and installs a fresh server-side device cookie.</summary>
        void ResetSession(string deviceId);
    }
}

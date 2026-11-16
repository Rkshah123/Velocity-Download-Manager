using Velocity.Core.Models;

namespace Velocity.Core.Interfaces;

public interface IDownloadEngine
{
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

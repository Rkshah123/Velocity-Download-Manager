using Velocity.Core.Models;

namespace Velocity.Core.Interfaces;

public interface IDownloadRepository
{
    Task<IReadOnlyList<DownloadRecord>> GetRecentAsync(CancellationToken cancellationToken = default);
}

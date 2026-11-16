using Velocity.Core.Models;

namespace Velocity.Core.Tests.Support;

/// <summary>Synchronous progress sink so a test can react to the engine's reports inline.</summary>
internal sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => report(value);
}

/// <summary>A throw-away destination folder for engine tests.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("velocity-resume-");
    public string Path => _directory.FullName;

    public void Dispose()
    {
        try { _directory.Delete(true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

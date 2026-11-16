namespace Velocity.Core.Models;

/// <summary>Shared byte-size formatting so every surface reports sizes the same way.</summary>
public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.00} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };
}

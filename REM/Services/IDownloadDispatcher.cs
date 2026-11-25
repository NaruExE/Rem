namespace REM.Services;

public interface IDownloadDispatcher
{
    Task EnqueueAsync(string url, string? title, string formatPreset, CancellationToken cancellationToken = default);
}

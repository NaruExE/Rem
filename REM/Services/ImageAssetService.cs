using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace REM.Services;

public sealed class ImageAssetService : IDisposable
{
    private readonly Dictionary<string, Bitmap?> _brandCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    public ImageAssetService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rootDir = Path.Combine(appData, "REM");
        CustomImagesDirectory = Path.Combine(rootDir, "Images");
        AvatarsDirectory = Path.Combine(rootDir, "Avatars");

        Directory.CreateDirectory(CustomImagesDirectory);
        Directory.CreateDirectory(AvatarsDirectory);
    }

    public string CustomImagesDirectory { get; }
    public string AvatarsDirectory { get; }

    public string ActiveImagesDirectory
    {
        get
        {
            var bundledDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Images");
            if (Directory.Exists(bundledDir) &&
                Directory.EnumerateFiles(bundledDir, "*.png").Any())
            {
                return bundledDir;
            }

            return CustomImagesDirectory;
        }
    }

    public Bitmap? LoadScaledBitmap(string? filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Bitmap.DecodeToWidth(stream, decodeWidth);
        }
        catch
        {
            return null;
        }
    }

    public Bitmap? GetBrandImage(string fileName, int decodeWidth)
    {
        if (_disposed)
        {
            return null;
        }

        var key = $"{fileName}@{decodeWidth}";
        lock (_sync)
        {
            if (_brandCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var loaded = LoadBrandImageUncached(fileName, decodeWidth);
            _brandCache[key] = loaded;
            return loaded;
        }
    }

    public Bitmap? GetPopupImage(string? kindSuffix = null, int decodeWidth = 140)
    {
        if (!string.IsNullOrWhiteSpace(kindSuffix))
        {
            var specific = GetBrandImage($"rem-popup-{kindSuffix}.png", decodeWidth);
            if (specific is not null)
            {
                return specific;
            }
        }

        return GetBrandImage("rem-popup.png", decodeWidth)
            ?? GetBrandImage("rem-popup-info.png", decodeWidth);
    }

    public void ReloadBrandImages()
    {
        lock (_sync)
        {
            foreach (var bmp in _brandCache.Values)
            {
                bmp?.Dispose();
            }

            _brandCache.Clear();
        }
    }

    public WindowIcon? TryLoadWindowIcon()
    {
        using var bitmap = LoadBrandImageUncached("rem-icon.png", 64);
        if (bitmap is null)
        {
            return null;
        }

        try
        {
            return new WindowIcon(bitmap);
        }
        catch
        {
            return null;
        }
    }

    public string CopyProfileAvatar(string sourceFilePath)
    {
        var ext = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".png";
        }

        var destPath = Path.Combine(AvatarsDirectory, $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}");
        File.Copy(sourceFilePath, destPath, overwrite: true);
        return destPath;
    }

    public void TryDeleteProfileAvatar(string? avatarPath)
    {
        if (string.IsNullOrWhiteSpace(avatarPath))
        {
            return;
        }

        try
        {
            var fullTarget = Path.GetFullPath(avatarPath);
            var fullAvatarsDir = Path.GetFullPath(AvatarsDirectory);
            if (fullTarget.StartsWith(fullAvatarsDir, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullTarget))
            {
                File.Delete(fullTarget);
            }
        }
        catch
        {
            // Ignore cleanup failure for locked or missing files.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReloadBrandImages();
    }

    private Bitmap? LoadBrandImageUncached(string fileName, int decodeWidth)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", fileName);
        if (File.Exists(bundledPath))
        {
            var bundled = LoadScaledBitmap(bundledPath, decodeWidth);
            if (bundled is not null)
            {
                return bundled;
            }
        }

        var customPath = Path.Combine(CustomImagesDirectory, fileName);
        return LoadScaledBitmap(customPath, decodeWidth);
    }
}

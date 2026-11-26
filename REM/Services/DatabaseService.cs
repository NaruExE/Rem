using System.Globalization;
using Microsoft.Data.Sqlite;
using REM.Models;

namespace REM.Services;

public sealed class DatabaseService
{
    public const string KeySkipIntro = "SkipIntro";
    public const string KeyThemeName = "ThemeName";
    public const string KeyWelcomeAudioPath = "WelcomeAudioPath";
    public const string KeyShowAppHeaderBar = "ShowAppHeaderBar";
    public const string KeyMinimizeToTray = "MinimizeToTray";
    public const string KeyWallpaperPath = "WallpaperPath";
    public const string KeyWallpaperDim = "WallpaperDim";
    public const string KeyWallpaperSoftBlur = "WallpaperSoftBlur";
    public const string KeyModuleOpacity = "ModuleOpacity";
    public const string KeyTwitchClientId = "TwitchClientId";
    public const string KeyTwitchClientSecret = "TwitchClientSecret";
    public const string KeyTwitchUserAccessToken = "TwitchUserAccessToken";
    public const string KeyTwitchUserId = "TwitchUserId";
    public const string KeyTwitchUserLogin = "TwitchUserLogin";
    public const string KeyTwitchUserIrcLogin = "TwitchUserIrcLogin";
    public const string KeyTwitchHasChatScopes = "TwitchHasChatScopes";
    public const string KeyAudioShowThumbnails = "AudioShowThumbnails";
    public const string KeyAudioVolume = "AudioVolume";
    public const string KeyAudioYtDlpFallback = "AudioYtDlpFallback";
    public const string KeyAudioActivePlatform = "AudioActivePlatform";
    public const string KeyAudioShowYouTubeThumbnails = "AudioShowYouTubeThumbnails";
    public const string KeyAudioYouTubeCookieBrowser = "AudioYouTubeCookieBrowser";
    public const string KeyAudioYouTubeCookieProfile = "AudioYouTubeCookieProfile";
    public const string KeyAudioQualityPreset = "AudioQualityPreset";
    public const string KeyWatchVolume = "WatchVolume";
    public const string KeyWatchQuality = "WatchQuality";
    public const string KeyWatchActivePlatform = "WatchActivePlatform";
    public const string KeyWatchChatEnabled = "WatchChatEnabled";
    public const string KeyWatchTwitchEmotesEnabled = "WatchTwitchEmotesEnabled";
    public const string KeyWatchSevenTvEmotesEnabled = "WatchSevenTvEmotesEnabled";
    public const string KeyWatchShowThumbnails = "WatchShowThumbnails";
    public const string KeySpotifyClientId = "SpotifyClientId";
    public const string KeySpotifySoloistApiKey = "SpotifySoloistApiKey";
    public const string KeySpotifyAccessToken = "SpotifyAccessToken";
    public const string KeySpotifyRefreshToken = "SpotifyRefreshToken";
    public const string KeySpotifyTokenExpiresAt = "SpotifyTokenExpiresAt";
    public const string KeySpotifyUserDisplayName = "SpotifyUserDisplayName";
    public const string KeySpotifyLastFmApiKey = "SpotifyLastFmApiKey";
    public const string KeySpotifyBitrate = "SpotifyBitrate";
    public const string KeySpotifyCacheSizeMb = "SpotifyCacheSizeMb";
    public const string KeySpotifyVolume = "SpotifyVolume";
    public const string KeySpotifyShowPlaylistIcons = "SpotifyShowPlaylistIcons";
    public const string KeySpotifyShowTrackCovers = "SpotifyShowTrackCovers";
    public const string KeySpotifyShowAmbientGlow = "SpotifyShowAmbientGlow";
    public const string KeySpotifyShowRemArt = "SpotifyShowRemArt";
    public const string KeySpotifyRemArtFile = "SpotifyRemArtFile";
    public const string KeySpotifyLyricsBgMode = "SpotifyLyricsBgMode";
    public const string KeySpotifyLyricsSolidColor = "SpotifyLyricsSolidColor";
    public const string KeySpotifyLyricsShowTimestamps = "SpotifyLyricsShowTimestamps";
    public const string KeySpotifyLyricsAnimate = "SpotifyLyricsAnimate";
    public const string KeySpotifyLyricsFontSize = "SpotifyLyricsFontSize";
    public const string KeySpotifyLyricsCentered = "SpotifyLyricsCentered";
    public const string KeySpotifyPreferDeepCuts = "SpotifyPreferDeepCuts";
    public const string KeyVideoVolume = "VideoVolume";
    public const string KeyVideoQuality = "VideoQuality";
    public const string KeyTmdbBearerToken = "TmdbBearerToken";
    public const string KeyMovieEmbedTemplate = "MovieEmbedTemplate";
    public const string KeyTvEmbedTemplate = "TvEmbedTemplate";
    public const string KeyMoviesBlockedAdDomains = "MoviesBlockedAdDomains";
    public const string KeyAutoCheckUpdates = "AutoCheckUpdates";
    public const string KeySkippedUpdateVersion = "SkippedUpdateVersion";

    private static readonly HashSet<string> SecretKeys = new(StringComparer.Ordinal)
    {
        KeyTwitchClientSecret,
        KeyTwitchUserAccessToken,
        KeySpotifyAccessToken,
        KeySpotifyRefreshToken,
        KeySpotifySoloistApiKey,
        KeySpotifyLastFmApiKey,
        KeyTmdbBearerToken
    };

    private readonly string _connectionString;

    public DatabaseService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(appData, "REM");
        Directory.CreateDirectory(dataDir);
        DatabasePath = Path.Combine(dataDir, "rem.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS AppSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Profiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE,
                    AvatarColor TEXT NOT NULL,
                    AvatarPath TEXT NULL,
                    PasswordSalt TEXT NULL,
                    PasswordHash TEXT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ProfileModules (
                    ProfileId INTEGER NOT NULL,
                    ModuleId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    PRIMARY KEY (ProfileId, ModuleId),
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS TwitchCategories (
                    ProfileId INTEGER NOT NULL,
                    CategoryName TEXT NOT NULL,
                    GameId TEXT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    PRIMARY KEY (ProfileId, CategoryName),
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS TwitchGameIdCache (
                    CategoryName TEXT PRIMARY KEY COLLATE NOCASE,
                    GameId TEXT NOT NULL,
                    CachedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS YouTubeTopics (
                    ProfileId INTEGER NOT NULL,
                    TopicName TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    PRIMARY KEY (ProfileId, TopicName),
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS SpotifyLyricsCache (
                    TrackUri TEXT PRIMARY KEY,
                    IsSynced INTEGER NOT NULL,
                    SyncedLrc TEXT NULL,
                    PlainLyrics TEXT NULL,
                    OffsetMs INTEGER NOT NULL DEFAULT 0,
                    SourceLabel TEXT NULL,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SpotifyPinnedPlaylists (
                    PlaylistId TEXT PRIMARY KEY,
                    PinnedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS VideoPlaylists (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS VideoPlaylistItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PlaylistId INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    SourceUrl TEXT NOT NULL,
                    Referer TEXT NULL,
                    LastPositionMs INTEGER NOT NULL DEFAULT 0,
                    DurationMs INTEGER NOT NULL DEFAULT 0,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (PlaylistId) REFERENCES VideoPlaylists(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS MovieWatchlist (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileId INTEGER NOT NULL,
                    TmdbId INTEGER NOT NULL,
                    ImdbId TEXT NULL,
                    MediaType TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    PosterPath TEXT NULL,
                    Year TEXT NULL,
                    Rating REAL NOT NULL DEFAULT 0,
                    AddedAt TEXT NOT NULL,
                    UNIQUE (ProfileId, TmdbId, MediaType),
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS MovieHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileId INTEGER NOT NULL,
                    TmdbId INTEGER NOT NULL,
                    ImdbId TEXT NULL,
                    MediaType TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    PosterPath TEXT NULL,
                    LastSeason INTEGER NOT NULL DEFAULT 0,
                    LastEpisode INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL,
                    UNIQUE (ProfileId, TmdbId, MediaType),
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS MediaGrabberSettings (
                    ProfileId INTEGER PRIMARY KEY,
                    DefaultDownloadFolder TEXT NOT NULL,
                    YtDlpPath TEXT NOT NULL,
                    Aria2cPath TEXT NOT NULL,
                    FfmpegPath TEXT NOT NULL,
                    MaxConcurrentDownloads INTEGER NOT NULL DEFAULT 3,
                    Aria2Connections INTEGER NOT NULL DEFAULT 16,
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS DownloadHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileId INTEGER NOT NULL,
                    Url TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Engine TEXT NOT NULL,
                    FormatPreset TEXT NOT NULL,
                    FilePath TEXT NULL,
                    FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                    Status TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CompletedAt TEXT NULL,
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS SpotifyPlayHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TrackUri TEXT NOT NULL,
                    TrackName TEXT NOT NULL,
                    ArtistName TEXT NOT NULL,
                    HourOfDay INTEGER NOT NULL,
                    PlayedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SpotifyMixCache (
                    MixKey TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    Subtitle TEXT NOT NULL,
                    Badge TEXT NOT NULL,
                    AccentHex TEXT NOT NULL,
                    TracksJson TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SpotifyTrackResolveCache (
                    LookupKey TEXT PRIMARY KEY,
                    TrackJson TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        EnsureProfilesAvatarPathColumn(connection);
        MigratePlaintextSecretsToDpapi(connection);
    }

    public AppSettings LoadSettings()
    {
        var settings = new AppSettings();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM AppSettings;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);
            switch (key)
            {
                case KeySkipIntro:
                    settings.SkipIntro = bool.TryParse(value, out var skip) && skip;
                    break;
                case KeyThemeName:
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.ThemeName = value;
                    }
                    break;
                case KeyWelcomeAudioPath:
                    settings.WelcomeAudioPath = value;
                    break;
                case KeyShowAppHeaderBar:
                    settings.ShowAppHeaderBar = bool.TryParse(value, out var showHeader) && showHeader;
                    break;
                case KeyMinimizeToTray:
                    settings.MinimizeToTray = !bool.TryParse(value, out var minTray) || minTray;
                    break;
                case KeyAutoCheckUpdates:
                    settings.AutoCheckUpdates = !bool.TryParse(value, out var autoUpdate) || autoUpdate;
                    break;
                case KeyWallpaperPath:
                    settings.WallpaperPath = value;
                    break;
                case KeyWallpaperDim:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dim))
                    {
                        settings.WallpaperDim = Math.Clamp(dim, 0.0, 0.85);
                    }
                    break;
                case KeyWallpaperSoftBlur:
                    settings.WallpaperSoftBlur = bool.TryParse(value, out var blur) && blur;
                    break;
                case KeyModuleOpacity:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var op))
                    {
                        settings.ModuleOpacity = Math.Clamp(op, 0.25, 1.0);
                    }
                    break;
            }
        }

        return settings;
    }

    public string? GetSetting(string key)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        if (result is DBNull or null)
        {
            return null;
        }

        var str = Convert.ToString(result, CultureInfo.InvariantCulture);
        return SecretKeys.Contains(key) && !string.IsNullOrEmpty(str)
            ? SecretProtectionService.Unprotect(str)
            : str;
    }

    public List<TwitchCategoryItem> GetProfileTwitchCategories(long profileId)
    {
        var list = new List<TwitchCategoryItem>();
        using var connection = OpenConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CategoryName, GameId
                FROM TwitchCategories
                WHERE ProfileId = $profileId
                ORDER BY DisplayOrder ASC, CategoryName ASC;
                """;
            cmd.Parameters.AddWithValue("$profileId", profileId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TwitchCategoryItem(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        return list;
    }

    public void AddProfileTwitchCategory(long profileId, string categoryName, string? gameId)
    {
        if (profileId <= 0 || string.IsNullOrWhiteSpace(categoryName))
        {
            return;
        }

        var trimmed = categoryName.Trim();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TwitchCategories (ProfileId, CategoryName, GameId, DisplayOrder)
            VALUES (
                $profileId,
                $name,
                $gameId,
                COALESCE((SELECT MAX(DisplayOrder) + 1 FROM TwitchCategories WHERE ProfileId = $profileId), 0)
            )
            ON CONFLICT(ProfileId, CategoryName) DO UPDATE SET
                GameId = COALESCE(excluded.GameId, TwitchCategories.GameId);
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$name", trimmed);
        cmd.Parameters.AddWithValue("$gameId", (object?)gameId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void RemoveProfileTwitchCategory(long profileId, string categoryName)
    {
        if (profileId <= 0 || string.IsNullOrWhiteSpace(categoryName))
        {
            return;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM TwitchCategories
            WHERE ProfileId = $profileId AND CategoryName = $name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$name", categoryName.Trim());
        cmd.ExecuteNonQuery();
    }

    public string? GetCachedTwitchGameId(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return null;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GameId FROM TwitchGameIdCache WHERE CategoryName = $name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$name", categoryName.Trim());
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    public void CacheTwitchGameId(string categoryName, string gameId)
    {
        if (string.IsNullOrWhiteSpace(categoryName) || string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TwitchGameIdCache (CategoryName, GameId, CachedAt)
            VALUES ($name, $gameId, $cachedAt)
            ON CONFLICT(CategoryName) DO UPDATE SET
                GameId = excluded.GameId,
                CachedAt = excluded.CachedAt;
            """;
        cmd.Parameters.AddWithValue("$name", categoryName.Trim());
        cmd.Parameters.AddWithValue("$gameId", gameId.Trim());
        cmd.Parameters.AddWithValue("$cachedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public List<string> GetProfileYouTubeTopics(long profileId)
    {
        var list = new List<string>();
        using var connection = OpenConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TopicName
                FROM YouTubeTopics
                WHERE ProfileId = $profileId
                ORDER BY DisplayOrder ASC, TopicName ASC;
                """;
            cmd.Parameters.AddWithValue("$profileId", profileId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }
        }

        if (list.Count == 0 && profileId > 0)
        {
            string[] defaults = ["Music", "Lo-Fi", "Gaming", "Podcasts"];
            var order = 0;
            foreach (var topic in defaults)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = """
                    INSERT OR IGNORE INTO YouTubeTopics (ProfileId, TopicName, DisplayOrder)
                    VALUES ($profileId, $name, $order);
                    """;
                insertCmd.Parameters.AddWithValue("$profileId", profileId);
                insertCmd.Parameters.AddWithValue("$name", topic);
                insertCmd.Parameters.AddWithValue("$order", order++);
                insertCmd.ExecuteNonQuery();
                list.Add(topic);
            }
        }

        return list;
    }

    public void AddProfileYouTubeTopic(long profileId, string topicName)
    {
        if (profileId <= 0 || string.IsNullOrWhiteSpace(topicName))
        {
            return;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO YouTubeTopics (ProfileId, TopicName, DisplayOrder)
            VALUES (
                $profileId,
                $name,
                COALESCE((SELECT MAX(DisplayOrder) + 1 FROM YouTubeTopics WHERE ProfileId = $profileId), 0)
            );
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$name", topicName.Trim());
        cmd.ExecuteNonQuery();
    }

    public void RemoveProfileYouTubeTopic(long profileId, string topicName)
    {
        if (profileId <= 0 || string.IsNullOrWhiteSpace(topicName))
        {
            return;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM YouTubeTopics
            WHERE ProfileId = $profileId AND TopicName = $name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$name", topicName.Trim());
        cmd.ExecuteNonQuery();
    }

    public void SaveSetting(string key, string value)
    {
        var stored = SecretKeys.Contains(key) && !string.IsNullOrEmpty(value)
            ? SecretProtectionService.Protect(value)
            : value;

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppSettings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", stored);
        cmd.ExecuteNonQuery();
    }

    public CachedSpotifyLyrics? GetCachedSpotifyLyrics(string trackUri)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
        {
            return null;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TrackUri, IsSynced, SyncedLrc, PlainLyrics, OffsetMs, SourceLabel
            FROM SpotifyLyricsCache
            WHERE TrackUri = $uri;
            """;
        cmd.Parameters.AddWithValue("$uri", trackUri.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CachedSpotifyLyrics(
            TrackUri: reader.GetString(0),
            IsSynced: reader.GetInt32(1) != 0,
            SyncedLrc: reader.IsDBNull(2) ? null : reader.GetString(2),
            PlainLyrics: reader.IsDBNull(3) ? null : reader.GetString(3),
            OffsetMs: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            SourceLabel: reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public void SaveCachedSpotifyLyrics(
        string trackUri,
        bool isSynced,
        string? syncedLrc,
        string? plainLyrics,
        int offsetMs,
        string? sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
        {
            return;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SpotifyLyricsCache (TrackUri, IsSynced, SyncedLrc, PlainLyrics, OffsetMs, SourceLabel, UpdatedAt)
            VALUES ($uri, $synced, $lrc, $plain, $offset, $src, $updated)
            ON CONFLICT(TrackUri) DO UPDATE SET
                IsSynced = excluded.IsSynced,
                SyncedLrc = excluded.SyncedLrc,
                PlainLyrics = excluded.PlainLyrics,
                OffsetMs = excluded.OffsetMs,
                SourceLabel = excluded.SourceLabel,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$uri", trackUri.Trim());
        cmd.Parameters.AddWithValue("$synced", isSynced ? 1 : 0);
        cmd.Parameters.AddWithValue("$lrc", (object?)syncedLrc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plain", (object?)plainLyrics ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$offset", offsetMs);
        cmd.Parameters.AddWithValue("$src", (object?)sourceLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void SaveSpotifyLyricsOffset(string trackUri, int offsetMs)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
        {
            return;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SpotifyLyricsCache (TrackUri, IsSynced, SyncedLrc, PlainLyrics, OffsetMs, SourceLabel, UpdatedAt)
            VALUES ($uri, 0, NULL, NULL, $offset, NULL, $updated)
            ON CONFLICT(TrackUri) DO UPDATE SET
                OffsetMs = excluded.OffsetMs,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$uri", trackUri.Trim());
        cmd.Parameters.AddWithValue("$offset", offsetMs);
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public HashSet<string> GetPinnedSpotifyPlaylistIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PlaylistId FROM SpotifyPinnedPlaylists;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public void SetSpotifyPlaylistPinned(string playlistId, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return;
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        if (pinned)
        {
            cmd.CommandText = """
                INSERT INTO SpotifyPinnedPlaylists (PlaylistId, PinnedAt)
                VALUES ($id, $at)
                ON CONFLICT(PlaylistId) DO UPDATE SET PinnedAt = excluded.PinnedAt;
                """;
            cmd.Parameters.AddWithValue("$id", playlistId.Trim());
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            cmd.CommandText = "DELETE FROM SpotifyPinnedPlaylists WHERE PlaylistId = $id;";
            cmd.Parameters.AddWithValue("$id", playlistId.Trim());
        }

        cmd.ExecuteNonQuery();
    }

    public List<UserProfile> GetProfiles()
    {
        var profiles = new List<UserProfile>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, AvatarColor, AvatarPath, PasswordSalt, PasswordHash, CreatedAt
            FROM Profiles
            ORDER BY Id ASC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            profiles.Add(new UserProfile
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                AvatarColor = reader.GetString(2),
                AvatarPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                PasswordSalt = reader.IsDBNull(4) ? null : reader.GetString(4),
                PasswordHash = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = DateTime.TryParse(
                    reader.GetString(6),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var dt)
                    ? dt
                    : DateTime.UtcNow
            });
        }

        return profiles;
    }

    public UserProfile CreateProfile(
        string name,
        string? password,
        string avatarColor,
        string? avatarPath = null)
    {
        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(name));
        }

        string? salt = null;
        string? hash = null;
        if (!string.IsNullOrEmpty(password))
        {
            (salt, hash) = PasswordHasher.HashPassword(password);
        }

        var createdAt = DateTime.UtcNow;

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Profiles (Name, AvatarColor, AvatarPath, PasswordSalt, PasswordHash, CreatedAt)
            VALUES ($name, $color, $avatarPath, $salt, $hash, $createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", trimmedName);
        cmd.Parameters.AddWithValue("$color", avatarColor);
        cmd.Parameters.AddWithValue("$avatarPath", (object?)avatarPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$salt", (object?)salt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));

        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        return new UserProfile
        {
            Id = id,
            Name = trimmedName,
            AvatarColor = avatarColor,
            AvatarPath = avatarPath,
            PasswordSalt = salt,
            PasswordHash = hash,
            CreatedAt = createdAt
        };
    }

    public UserProfile? GetProfileById(long profileId)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, AvatarColor, AvatarPath, PasswordSalt, PasswordHash, CreatedAt
            FROM Profiles
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", profileId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new UserProfile
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            AvatarColor = reader.GetString(2),
            AvatarPath = reader.IsDBNull(3) ? null : reader.GetString(3),
            PasswordSalt = reader.IsDBNull(4) ? null : reader.GetString(4),
            PasswordHash = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = DateTime.TryParse(
                reader.GetString(6),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dt)
                ? dt
                : DateTime.UtcNow
        };
    }

    public void UpdateProfileAvatar(long profileId, string? avatarPath)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Profiles
            SET AvatarPath = $avatarPath
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$avatarPath", (object?)avatarPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", profileId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateProfileName(long profileId, string newName)
    {
        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(newName));
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Profiles
            SET Name = $name
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$name", trimmed);
        cmd.Parameters.AddWithValue("$id", profileId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateProfilePassword(long profileId, string? newPassword)
    {
        string? salt = null;
        string? hash = null;
        if (!string.IsNullOrEmpty(newPassword))
        {
            (salt, hash) = PasswordHasher.HashPassword(newPassword);
        }

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Profiles
            SET PasswordSalt = $salt, PasswordHash = $hash
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$salt", (object?)salt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", profileId);
        cmd.ExecuteNonQuery();
    }

    public bool VerifyProfilePassword(long profileId, string password)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT PasswordSalt, PasswordHash
            FROM Profiles
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", profileId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var salt = reader.IsDBNull(0) ? null : reader.GetString(0);
        var hash = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(hash))
        {
            return true;
        }

        return PasswordHasher.VerifyPassword(password, salt, hash);
    }

    public void DeleteProfile(long profileId)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Profiles WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", profileId);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetProfileModuleIds(long profileId)
    {
        var ids = new List<string>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ModuleId
            FROM ProfileModules
            WHERE ProfileId = $profileId
            ORDER BY DisplayOrder ASC, ModuleId ASC;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public void SetProfileModuleIds(long profileId, IEnumerable<string> moduleIds)
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();

        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM ProfileModules WHERE ProfileId = $profileId;";
            deleteCmd.Parameters.AddWithValue("$profileId", profileId);
            deleteCmd.ExecuteNonQuery();
        }

        var order = 0;
        foreach (var moduleId in moduleIds.Distinct(StringComparer.Ordinal))
        {
            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO ProfileModules (ProfileId, ModuleId, DisplayOrder)
                VALUES ($profileId, $moduleId, $order);
                """;
            insertCmd.Parameters.AddWithValue("$profileId", profileId);
            insertCmd.Parameters.AddWithValue("$moduleId", moduleId);
            insertCmd.Parameters.AddWithValue("$order", order++);
            insertCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void AddProfileModule(long profileId, string moduleId)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO ProfileModules (ProfileId, ModuleId, DisplayOrder)
            VALUES (
                $profileId,
                $moduleId,
                COALESCE((SELECT MAX(DisplayOrder) + 1 FROM ProfileModules WHERE ProfileId = $profileId), 0)
            );
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$moduleId", moduleId);
        cmd.ExecuteNonQuery();
    }

    public void RemoveProfileModule(long profileId, string moduleId)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM ProfileModules
            WHERE ProfileId = $profileId AND ModuleId = $moduleId;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$moduleId", moduleId);
        cmd.ExecuteNonQuery();
    }

    public async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var stored = SecretKeys.Contains(key) && !string.IsNullOrEmpty(value)
            ? SecretProtectionService.Protect(value)
            : value;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppSettings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", stored);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is DBNull or null)
        {
            return null;
        }

        var str = Convert.ToString(result, CultureInfo.InvariantCulture);
        return SecretKeys.Contains(key) && !string.IsNullOrEmpty(str)
            ? SecretProtectionService.Unprotect(str)
            : str;
    }

    public async Task<MoviesTvSettings> GetMoviesTvSettingsAsync(CancellationToken cancellationToken = default)
    {
        var token = (await GetSettingAsync(KeyTmdbBearerToken, cancellationToken).ConfigureAwait(false)) ?? string.Empty;
        var movieTpl = (await GetSettingAsync(KeyMovieEmbedTemplate, cancellationToken).ConfigureAwait(false)) ?? string.Empty;
        var tvTpl = (await GetSettingAsync(KeyTvEmbedTemplate, cancellationToken).ConfigureAwait(false)) ?? string.Empty;
        var rawBlocked = await GetSettingAsync(KeyMoviesBlockedAdDomains, cancellationToken).ConfigureAwait(false);

        var extraBlocked = string.IsNullOrWhiteSpace(rawBlocked)
            ? Array.Empty<string>()
            : rawBlocked
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return new MoviesTvSettings(
            TmdbBearerToken: token.Trim(),
            MovieEmbedTemplate: movieTpl.Trim(),
            TvEmbedTemplate: tvTpl.Trim(),
            ExtraBlockedDomains: extraBlocked);
    }

    public async Task SaveMoviesTvSettingsAsync(
        string tmdbBearerToken,
        string movieEmbedTemplate,
        string tvEmbedTemplate,
        string? extraBlockedDomains = null,
        CancellationToken cancellationToken = default)
    {
        await SaveSettingAsync(KeyTmdbBearerToken, tmdbBearerToken.Trim(), cancellationToken).ConfigureAwait(false);
        await SaveSettingAsync(KeyMovieEmbedTemplate, movieEmbedTemplate.Trim(), cancellationToken).ConfigureAwait(false);
        await SaveSettingAsync(KeyTvEmbedTemplate, tvEmbedTemplate.Trim(), cancellationToken).ConfigureAwait(false);
        if (extraBlockedDomains is not null)
        {
            await SaveSettingAsync(KeyMoviesBlockedAdDomains, extraBlockedDomains.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<List<MovieWatchlistItem>> GetMovieWatchlistAsync(
        long profileId,
        CancellationToken cancellationToken = default)
    {
        var list = new List<MovieWatchlistItem>();
        if (profileId <= 0)
        {
            return list;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProfileId, TmdbId, ImdbId, MediaType, Title, PosterPath, Year, Rating, AddedAt
            FROM MovieWatchlist
            WHERE ProfileId = $profileId
            ORDER BY AddedAt DESC, Id DESC;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var addedAt = DateTime.TryParse(
                reader.GetString(9),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dt)
                ? dt
                : DateTime.UtcNow;

            list.Add(new MovieWatchlistItem(
                Id: reader.GetInt64(0),
                ProfileId: reader.GetInt64(1),
                TmdbId: reader.GetInt32(2),
                ImdbId: reader.IsDBNull(3) ? null : reader.GetString(3),
                MediaType: reader.GetString(4),
                Title: reader.GetString(5),
                PosterPath: reader.IsDBNull(6) ? null : reader.GetString(6),
                Year: reader.IsDBNull(7) ? null : reader.GetString(7),
                Rating: reader.GetDouble(8),
                AddedAt: addedAt));
        }

        return list;
    }

    public async Task<bool> IsInMovieWatchlistAsync(
        long profileId,
        int tmdbId,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        if (profileId <= 0 || tmdbId <= 0 || string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM MovieWatchlist
            WHERE ProfileId = $profileId AND TmdbId = $tmdbId AND MediaType = $mediaType
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$tmdbId", tmdbId);
        cmd.Parameters.AddWithValue("$mediaType", mediaType.Trim().ToLowerInvariant());

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null && result != DBNull.Value;
    }

    public async Task AddToMovieWatchlistAsync(
        long profileId,
        int tmdbId,
        string? imdbId,
        string mediaType,
        string title,
        string? posterPath,
        string? year,
        double rating,
        CancellationToken cancellationToken = default)
    {
        if (profileId <= 0 || tmdbId <= 0 || string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("Valid profile ID, TMDB ID, and media type are required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MovieWatchlist (ProfileId, TmdbId, ImdbId, MediaType, Title, PosterPath, Year, Rating, AddedAt)
            VALUES ($profileId, $tmdbId, $imdbId, $mediaType, $title, $posterPath, $year, $rating, $addedAt)
            ON CONFLICT(ProfileId, TmdbId, MediaType) DO UPDATE SET
                ImdbId = COALESCE(excluded.ImdbId, MovieWatchlist.ImdbId),
                Title = excluded.Title,
                PosterPath = COALESCE(excluded.PosterPath, MovieWatchlist.PosterPath),
                Year = COALESCE(excluded.Year, MovieWatchlist.Year),
                Rating = excluded.Rating,
                AddedAt = excluded.AddedAt;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$tmdbId", tmdbId);
        cmd.Parameters.AddWithValue("$imdbId", string.IsNullOrWhiteSpace(imdbId) ? DBNull.Value : imdbId.Trim());
        cmd.Parameters.AddWithValue("$mediaType", mediaType.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? $"TMDB #{tmdbId}" : title.Trim());
        cmd.Parameters.AddWithValue("$posterPath", string.IsNullOrWhiteSpace(posterPath) ? DBNull.Value : posterPath.Trim());
        cmd.Parameters.AddWithValue("$year", string.IsNullOrWhiteSpace(year) ? DBNull.Value : year.Trim());
        cmd.Parameters.AddWithValue("$rating", rating);
        cmd.Parameters.AddWithValue("$addedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveFromMovieWatchlistAsync(
        long profileId,
        int tmdbId,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        if (profileId <= 0 || tmdbId <= 0 || string.IsNullOrWhiteSpace(mediaType))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM MovieWatchlist
            WHERE ProfileId = $profileId AND TmdbId = $tmdbId AND MediaType = $mediaType;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$tmdbId", tmdbId);
        cmd.Parameters.AddWithValue("$mediaType", mediaType.Trim().ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<MovieHistoryItem>> GetMovieHistoryAsync(
        long profileId,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var list = new List<MovieHistoryItem>();
        if (profileId <= 0)
        {
            return list;
        }

        var clampedLimit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProfileId, TmdbId, ImdbId, MediaType, Title, PosterPath, LastSeason, LastEpisode, UpdatedAt
            FROM MovieHistory
            WHERE ProfileId = $profileId
            ORDER BY UpdatedAt DESC, Id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$limit", clampedLimit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var updatedAt = DateTime.TryParse(
                reader.GetString(9),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dt)
                ? dt
                : DateTime.UtcNow;

            list.Add(new MovieHistoryItem(
                Id: reader.GetInt64(0),
                ProfileId: reader.GetInt64(1),
                TmdbId: reader.GetInt32(2),
                ImdbId: reader.IsDBNull(3) ? null : reader.GetString(3),
                MediaType: reader.GetString(4),
                Title: reader.GetString(5),
                PosterPath: reader.IsDBNull(6) ? null : reader.GetString(6),
                LastSeason: reader.GetInt32(7),
                LastEpisode: reader.GetInt32(8),
                UpdatedAt: updatedAt));
        }

        return list;
    }

    public async Task UpsertMovieHistoryAsync(
        long profileId,
        int tmdbId,
        string? imdbId,
        string mediaType,
        string title,
        string? posterPath,
        int lastSeason = 0,
        int lastEpisode = 0,
        CancellationToken cancellationToken = default)
    {
        if (profileId <= 0 || tmdbId <= 0 || string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("Valid profile ID, TMDB ID, and media type are required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MovieHistory (ProfileId, TmdbId, ImdbId, MediaType, Title, PosterPath, LastSeason, LastEpisode, UpdatedAt)
            VALUES ($profileId, $tmdbId, $imdbId, $mediaType, $title, $posterPath, $lastSeason, $lastEpisode, $updatedAt)
            ON CONFLICT(ProfileId, TmdbId, MediaType) DO UPDATE SET
                ImdbId = COALESCE(excluded.ImdbId, MovieHistory.ImdbId),
                Title = excluded.Title,
                PosterPath = COALESCE(excluded.PosterPath, MovieHistory.PosterPath),
                LastSeason = excluded.LastSeason,
                LastEpisode = excluded.LastEpisode,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$tmdbId", tmdbId);
        cmd.Parameters.AddWithValue("$imdbId", string.IsNullOrWhiteSpace(imdbId) ? DBNull.Value : imdbId.Trim());
        cmd.Parameters.AddWithValue("$mediaType", mediaType.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? $"TMDB #{tmdbId}" : title.Trim());
        cmd.Parameters.AddWithValue("$posterPath", string.IsNullOrWhiteSpace(posterPath) ? DBNull.Value : posterPath.Trim());
        cmd.Parameters.AddWithValue("$lastSeason", Math.Max(0, lastSeason));
        cmd.Parameters.AddWithValue("$lastEpisode", Math.Max(0, lastEpisode));
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveMovieHistoryAsync(
        long profileId,
        int tmdbId,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        if (profileId <= 0 || tmdbId <= 0 || string.IsNullOrWhiteSpace(mediaType))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM MovieHistory
            WHERE ProfileId = $profileId AND TmdbId = $tmdbId AND MediaType = $mediaType;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$tmdbId", tmdbId);
        cmd.Parameters.AddWithValue("$mediaType", mediaType.Trim().ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<VideoPlaylist>> GetVideoPlaylistsAsync(
        long profileId,
        CancellationToken cancellationToken = default)
    {
        var list = new List<VideoPlaylist>();
        if (profileId <= 0)
        {
            return list;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProfileId, Name, CreatedAt
            FROM VideoPlaylists
            WHERE ProfileId = $profileId
            ORDER BY Id ASC;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var createdAt = DateTime.TryParse(
                reader.GetString(3),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dt)
                ? dt
                : DateTime.UtcNow;
            list.Add(new VideoPlaylist(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), createdAt));
        }

        return list;
    }

    public async Task<VideoPlaylist> CreateVideoPlaylistAsync(
        long profileId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (profileId <= 0 || string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Valid profile ID and playlist name are required.", nameof(name));
        }

        var createdAt = DateTime.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VideoPlaylists (ProfileId, Name, CreatedAt)
            VALUES ($profileId, $name, $createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$name", trimmed);
        cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));

        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        return new VideoPlaylist(id, profileId, trimmed, createdAt);
    }

    public async Task RenameVideoPlaylistAsync(
        long playlistId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var trimmed = newName.Trim();
        if (playlistId <= 0 || string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE VideoPlaylists SET Name = $name WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$name", trimmed);
        cmd.Parameters.AddWithValue("$id", playlistId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVideoPlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        if (playlistId <= 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM VideoPlaylists WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<VideoPlaylistItem>> GetVideoPlaylistItemsAsync(
        long playlistId,
        CancellationToken cancellationToken = default)
    {
        var list = new List<VideoPlaylistItem>();
        if (playlistId <= 0)
        {
            return list;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, PlaylistId, Title, SourceUrl, Referer, LastPositionMs, DurationMs, SortOrder
            FROM VideoPlaylistItems
            WHERE PlaylistId = $playlistId
            ORDER BY SortOrder ASC, Id ASC;
            """;
        cmd.Parameters.AddWithValue("$playlistId", playlistId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new VideoPlaylistItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7)));
        }

        return list;
    }

    public async Task<VideoPlaylistItem?> GetVideoPlaylistItemByIdAsync(
        long itemId,
        CancellationToken cancellationToken = default)
    {
        if (itemId <= 0)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, PlaylistId, Title, SourceUrl, Referer, LastPositionMs, DurationMs, SortOrder
            FROM VideoPlaylistItems
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", itemId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new VideoPlaylistItem(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7));
    }

    public async Task<VideoPlaylistItem> AddVideoPlaylistItemAsync(
        long playlistId,
        string title,
        string sourceUrl,
        string? referer = null,
        long durationMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (playlistId <= 0 || string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new ArgumentException("Valid playlist ID and source URL are required.", nameof(sourceUrl));
        }

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? sourceUrl.Trim() : title.Trim();
        var cleanUrl = sourceUrl.Trim();
        var cleanReferer = string.IsNullOrWhiteSpace(referer) ? null : referer.Trim();
        var clampedDuration = Math.Max(0, durationMs);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int sortOrder;
        await using (var sortCmd = connection.CreateCommand())
        {
            sortCmd.CommandText = "SELECT COALESCE(MAX(SortOrder) + 1, 0) FROM VideoPlaylistItems WHERE PlaylistId = $playlistId;";
            sortCmd.Parameters.AddWithValue("$playlistId", playlistId);
            sortOrder = Convert.ToInt32(await sortCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VideoPlaylistItems (PlaylistId, Title, SourceUrl, Referer, LastPositionMs, DurationMs, SortOrder)
            VALUES ($playlistId, $title, $url, $referer, 0, $durationMs, $sortOrder);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$playlistId", playlistId);
        cmd.Parameters.AddWithValue("$title", cleanTitle);
        cmd.Parameters.AddWithValue("$url", cleanUrl);
        cmd.Parameters.AddWithValue("$referer", (object?)cleanReferer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$durationMs", clampedDuration);
        cmd.Parameters.AddWithValue("$sortOrder", sortOrder);

        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        return new VideoPlaylistItem(id, playlistId, cleanTitle, cleanUrl, cleanReferer, 0, clampedDuration, sortOrder);
    }

    public async Task RemoveVideoPlaylistItemAsync(long itemId, CancellationToken cancellationToken = default)
    {
        if (itemId <= 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM VideoPlaylistItems WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", itemId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderVideoPlaylistItemsAsync(
        long playlistId,
        IReadOnlyList<long> orderedItemIds,
        CancellationToken cancellationToken = default)
    {
        if (playlistId <= 0 || orderedItemIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < orderedItemIds.Count; i++)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "UPDATE VideoPlaylistItems SET SortOrder = $order WHERE Id = $id AND PlaylistId = $playlistId;";
            cmd.Parameters.AddWithValue("$order", i);
            cmd.Parameters.AddWithValue("$id", orderedItemIds[i]);
            cmd.Parameters.AddWithValue("$playlistId", playlistId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateVideoItemProgressAsync(
        long itemId,
        long lastPositionMs,
        long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        if (itemId <= 0)
        {
            return;
        }

        var clampedPos = Math.Max(0, lastPositionMs);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        if (durationMs is > 0)
        {
            cmd.CommandText = """
                UPDATE VideoPlaylistItems
                SET LastPositionMs = $pos, DurationMs = $dur
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$dur", durationMs.Value);
        }
        else
        {
            cmd.CommandText = "UPDATE VideoPlaylistItems SET LastPositionMs = $pos WHERE Id = $id;";
        }

        cmd.Parameters.AddWithValue("$pos", clampedPos);
        cmd.Parameters.AddWithValue("$id", itemId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddVideoPlaylistItemsBulkAsync(
        long playlistId,
        IReadOnlyList<(string Title, string SourceUrl)> items,
        CancellationToken cancellationToken = default)
    {
        if (playlistId <= 0 || items.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long nextOrder = 0;
        await using (var maxCmd = connection.CreateCommand())
        {
            maxCmd.Transaction = (SqliteTransaction)tx;
            maxCmd.CommandText = "SELECT COALESCE(MAX(SortOrder) + 1, 0) FROM VideoPlaylistItems WHERE PlaylistId = $playlistId;";
            maxCmd.Parameters.AddWithValue("$playlistId", playlistId);
            var scalar = await maxCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is long l) nextOrder = l;
            else if (scalar is int n) nextOrder = n;
        }

        foreach (var (title, sourceUrl) in items)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                continue;
            }

            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = (SqliteTransaction)tx;
            insertCmd.CommandText = """
                INSERT INTO VideoPlaylistItems (PlaylistId, Title, SourceUrl, Referer, LastPositionMs, DurationMs, SortOrder)
                VALUES ($playlistId, $title, $url, NULL, 0, 0, $sortOrder);
                """;
            insertCmd.Parameters.AddWithValue("$playlistId", playlistId);
            insertCmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? sourceUrl : title.Trim());
            insertCmd.Parameters.AddWithValue("$url", sourceUrl.Trim());
            insertCmd.Parameters.AddWithValue("$sortOrder", nextOrder++);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaGrabberSettings> GetMediaGrabberSettingsAsync(
        long profileId,
        CancellationToken cancellationToken = default)
    {
        var defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "REM");

        if (profileId <= 0)
        {
            return new MediaGrabberSettings(defaultFolder, string.Empty, string.Empty, string.Empty, 3, 16);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DefaultDownloadFolder, YtDlpPath, Aria2cPath, FfmpegPath, MaxConcurrentDownloads, Aria2Connections
            FROM MediaGrabberSettings
            WHERE ProfileId = $profileId;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var folder = reader.GetString(0);
            return new MediaGrabberSettings(
                DefaultDownloadFolder: string.IsNullOrWhiteSpace(folder) ? defaultFolder : folder,
                YtDlpPath: reader.GetString(1),
                Aria2cPath: reader.GetString(2),
                FfmpegPath: reader.GetString(3),
                MaxConcurrentDownloads: Math.Clamp(reader.GetInt32(4), 1, 8),
                Aria2Connections: Math.Clamp(reader.GetInt32(5), 1, 16));
        }

        return new MediaGrabberSettings(defaultFolder, string.Empty, string.Empty, string.Empty, 3, 16);
    }

    public async Task SaveMediaGrabberSettingsAsync(
        long profileId,
        MediaGrabberSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (profileId <= 0)
        {
            return;
        }

        var folder = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "REM")
            : settings.DefaultDownloadFolder.Trim();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MediaGrabberSettings (
                ProfileId, DefaultDownloadFolder, YtDlpPath, Aria2cPath, FfmpegPath, MaxConcurrentDownloads, Aria2Connections
            )
            VALUES ($profileId, $folder, $ytDlp, $aria2, $ffmpeg, $concurrent, $conns)
            ON CONFLICT(ProfileId) DO UPDATE SET
                DefaultDownloadFolder = excluded.DefaultDownloadFolder,
                YtDlpPath = excluded.YtDlpPath,
                Aria2cPath = excluded.Aria2cPath,
                FfmpegPath = excluded.FfmpegPath,
                MaxConcurrentDownloads = excluded.MaxConcurrentDownloads,
                Aria2Connections = excluded.Aria2Connections;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$folder", folder);
        cmd.Parameters.AddWithValue("$ytDlp", settings.YtDlpPath.Trim());
        cmd.Parameters.AddWithValue("$aria2", settings.Aria2cPath.Trim());
        cmd.Parameters.AddWithValue("$ffmpeg", settings.FfmpegPath.Trim());
        cmd.Parameters.AddWithValue("$concurrent", Math.Clamp(settings.MaxConcurrentDownloads, 1, 8));
        cmd.Parameters.AddWithValue("$conns", Math.Clamp(settings.Aria2Connections, 1, 16));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DownloadHistoryItem>> GetDownloadHistoryAsync(
        long profileId,
        CancellationToken cancellationToken = default)
    {
        var list = new List<DownloadHistoryItem>();
        if (profileId <= 0)
        {
            return list;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ProfileId, Url, Title, Engine, FormatPreset, FilePath, FileSizeBytes, Status, CreatedAt, CompletedAt
            FROM DownloadHistory
            WHERE ProfileId = $profileId
            ORDER BY CreatedAt DESC, Id DESC;
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var createdAt = DateTime.TryParse(
                reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cDt)
                ? cDt
                : DateTime.UtcNow;

            DateTime? completedAt = !reader.IsDBNull(10) && DateTime.TryParse(
                reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var doneDt)
                ? doneDt
                : null;

            list.Add(new DownloadHistoryItem(
                Id: reader.GetInt64(0),
                ProfileId: reader.GetInt64(1),
                Url: reader.GetString(2),
                Title: reader.GetString(3),
                Engine: reader.GetString(4),
                FormatPreset: reader.GetString(5),
                FilePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                FileSizeBytes: reader.GetInt64(7),
                Status: reader.GetString(8),
                CreatedAt: createdAt,
                CompletedAt: completedAt));
        }

        return list;
    }

    public async Task<long> InsertDownloadHistoryAsync(
        long profileId,
        string url,
        string title,
        string engine,
        string formatPreset,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DownloadHistory (ProfileId, Url, Title, Engine, FormatPreset, FilePath, FileSizeBytes, Status, CreatedAt, CompletedAt)
            VALUES ($profileId, $url, $title, $engine, $preset, NULL, 0, $status, $createdAt, NULL);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$profileId", profileId);
        cmd.Parameters.AddWithValue("$url", url.Trim());
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? url.Trim() : title.Trim());
        cmd.Parameters.AddWithValue("$engine", engine);
        cmd.Parameters.AddWithValue("$preset", formatPreset);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task UpdateDownloadHistoryAsync(
        long id,
        string? title,
        string? filePath,
        long? fileSizeBytes,
        string status,
        DateTime? completedAt,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE DownloadHistory
            SET Title = COALESCE($title, Title),
                FilePath = COALESCE($filePath, FilePath),
                FileSizeBytes = COALESCE($size, FileSizeBytes),
                Status = $status,
                CompletedAt = COALESCE($completedAt, CompletedAt)
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? DBNull.Value : title.Trim());
        cmd.Parameters.AddWithValue("$filePath", string.IsNullOrWhiteSpace(filePath) ? DBNull.Value : filePath.Trim());
        cmd.Parameters.AddWithValue("$size", fileSizeBytes.HasValue && fileSizeBytes.Value > 0 ? fileSizeBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$completedAt", completedAt.HasValue ? completedAt.Value.ToString("O") : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearCompletedDownloadHistoryAsync(long profileId, CancellationToken cancellationToken = default)
    {
        if (profileId <= 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM DownloadHistory WHERE ProfileId = $profileId AND Status = 'Completed';";
        cmd.Parameters.AddWithValue("$profileId", profileId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDownloadHistoryItemAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM DownloadHistory WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSpotifyPlayAsync(
        string trackUri,
        string trackName,
        string artistName,
        int hourOfDay,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackUri) || string.IsNullOrWhiteSpace(trackName))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.CommandText = """
                INSERT INTO SpotifyPlayHistory (TrackUri, TrackName, ArtistName, HourOfDay, PlayedAt)
                VALUES ($uri, $name, $artist, $hour, $playedAt);
                """;
            insertCmd.Parameters.AddWithValue("$uri", trackUri.Trim());
            insertCmd.Parameters.AddWithValue("$name", trackName.Trim());
            insertCmd.Parameters.AddWithValue("$artist", string.IsNullOrWhiteSpace(artistName) ? "Unknown Artist" : artistName.Trim());
            insertCmd.Parameters.AddWithValue("$hour", Math.Clamp(hourOfDay, 0, 23));
            insertCmd.Parameters.AddWithValue("$playedAt", DateTime.UtcNow.ToString("O"));
            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Keep history bounded so SQLite stays tiny.
        await using var pruneCmd = connection.CreateCommand();
        pruneCmd.CommandText = """
            DELETE FROM SpotifyPlayHistory
            WHERE Id NOT IN (
                SELECT Id FROM SpotifyPlayHistory ORDER BY Id DESC LIMIT 1200
            );
            """;
        await pruneCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<(string TrackUri, string TrackName, string ArtistName, int HourOfDay, DateTime PlayedAt)>> GetSpotifyRecentPlayHistoryAsync(
        int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        var list = new List<(string TrackUri, string TrackName, string ArtistName, int HourOfDay, DateTime PlayedAt)>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TrackUri, TrackName, ArtistName, HourOfDay, PlayedAt
            FROM SpotifyPlayHistory
            ORDER BY Id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(maxRows, 1, 1200));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var playedAt = DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTime.UtcNow;
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), playedAt));
        }

        return list;
    }

    public async Task<RemMixDescriptor?> GetSpotifyCachedMixAsync(
        string mixKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mixKey))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Title, Subtitle, Badge, AccentHex, TracksJson, UpdatedAt
            FROM SpotifyMixCache
            WHERE MixKey = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", mixKey.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var tracks = System.Text.Json.JsonSerializer.Deserialize<List<SpotifyTrackItem>>(reader.GetString(4)) ?? [];
            var updatedAt = DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTime.MinValue;
            return new RemMixDescriptor(
                mixKey.Trim(),
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                updatedAt,
                tracks);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveSpotifyCachedMixAsync(
        RemMixDescriptor mix,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mix.Key))
        {
            return;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(mix.Tracks);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SpotifyMixCache (MixKey, Title, Subtitle, Badge, AccentHex, TracksJson, UpdatedAt)
            VALUES ($key, $title, $subtitle, $badge, $accent, $json, $updated)
            ON CONFLICT(MixKey) DO UPDATE SET
                Title = excluded.Title,
                Subtitle = excluded.Subtitle,
                Badge = excluded.Badge,
                AccentHex = excluded.AccentHex,
                TracksJson = excluded.TracksJson,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$key", mix.Key);
        cmd.Parameters.AddWithValue("$title", mix.Title);
        cmd.Parameters.AddWithValue("$subtitle", mix.Subtitle);
        cmd.Parameters.AddWithValue("$badge", mix.Badge);
        cmd.Parameters.AddWithValue("$accent", mix.AccentHex);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$updated", mix.GeneratedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SpotifyTrackItem?> GetResolvedSpotifyTrackAsync(
        string lookupKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lookupKey))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TrackJson
            FROM SpotifyTrackResolveCache
            WHERE LookupKey = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", lookupKey.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<SpotifyTrackItem>(reader.GetString(0));
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveResolvedSpotifyTrackAsync(
        string lookupKey,
        SpotifyTrackItem track,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lookupKey) || string.IsNullOrWhiteSpace(track.Uri))
        {
            return;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(track);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO SpotifyTrackResolveCache (LookupKey, TrackJson, UpdatedAt)
                VALUES ($key, $json, $updated)
                ON CONFLICT(LookupKey) DO UPDATE SET
                    TrackJson = excluded.TrackJson,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            cmd.Parameters.AddWithValue("$key", lookupKey.Trim());
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var pruneCmd = connection.CreateCommand();
        pruneCmd.CommandText = """
            DELETE FROM SpotifyTrackResolveCache
            WHERE LookupKey NOT IN (
                SELECT LookupKey FROM SpotifyTrackResolveCache ORDER BY UpdatedAt DESC LIMIT 1500
            );
            """;
        await pruneCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureProfilesAvatarPathColumn(SqliteConnection connection)
    {
        var hasAvatarPath = false;
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info(Profiles);";
            using var reader = pragmaCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "AvatarPath", StringComparison.OrdinalIgnoreCase))
                {
                    hasAvatarPath = true;
                    break;
                }
            }
        }

        if (!hasAvatarPath)
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE Profiles ADD COLUMN AvatarPath TEXT NULL;";
            alterCmd.ExecuteNonQuery();
        }
    }

    private static void MigratePlaintextSecretsToDpapi(SqliteConnection connection)
    {
        var updates = new List<(string Key, string ProtectedValue)>();
        using (var readCmd = connection.CreateCommand())
        {
            readCmd.CommandText = "SELECT Key, Value FROM AppSettings WHERE Value <> '';";
            using var reader = readCmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var val = reader.GetString(1);
                if (SecretKeys.Contains(key) && !SecretProtectionService.IsProtected(val))
                {
                    updates.Add((key, SecretProtectionService.Protect(val)));
                }
            }
        }

        foreach (var (key, protectedVal) in updates)
        {
            using var upd = connection.CreateCommand();
            upd.CommandText = "UPDATE AppSettings SET Value = $val WHERE Key = $key;";
            upd.Parameters.AddWithValue("$key", key);
            upd.Parameters.AddWithValue("$val", protectedVal);
            upd.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA cache_size = -2000; PRAGMA temp_store = MEMORY;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA cache_size = -2000; PRAGMA temp_store = MEMORY;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

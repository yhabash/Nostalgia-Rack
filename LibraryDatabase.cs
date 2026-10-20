using System.IO;
using Microsoft.Data.Sqlite;

namespace NostalgiaRack;

/// <summary>A saved playlist as shown in the drop-down list.</summary>
public sealed record PlaylistInfo(long Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The music library, stored in one SQLite database file:
///   %LocalAppData%\NostalgiaRack\library.db
/// (open it with "DB Browser for SQLite" if you are curious).
///
/// Tables:
///   tracks           every song we know about: where it is, its tags, size and date
///   library_folders  the music folders you asked us to scan
///   playlists        saved playlists, plus two hidden ones that remember the
///                    current playlist and queue between runs
///   playlist_tracks  which songs are in which playlist, in order
///   settings         small remembered values (settings, which playlist was open)
///   radio_favorites  your favourite radio stations
///   podcasts         podcasts you subscribed to, and podcast_episodes, their episodes
///
/// Safety: WAL mode means a crash in the middle of saving can't damage the file,
/// and a backup copy (library.backup.db) is made every time the app starts.
/// </summary>
public sealed class LibraryDatabase
{
    private const string SessionKind = "session"; // the playlist you are working on
    private const string QueueKind = "queue";     // the queue ("up next")
    private const string UserKind = "user";       // playlists you saved by name

    public string FilePath { get; }
    public string BackupPath { get; }

    public LibraryDatabase()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(appData, "NostalgiaRack");
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, "library.db");
        BackupPath = Path.Combine(folder, "library.backup.db");

        CopyFromOldName(Path.Combine(appData, "AmpPlayer"), folder);
    }

    /// <summary>
    /// The player used to be called "Amp Player" and kept its library in %LocalAppData%\AmpPlayer.
    /// The first time Nostalgia Rack starts, it copies that library across (songs, playlists,
    /// queue, radio, podcasts and settings). The old folder is left untouched, as a safety copy;
    /// you can delete it yourself once everything looks right.
    /// </summary>
    private void CopyFromOldName(string oldFolder, string newFolder)
    {
        string oldDb = Path.Combine(oldFolder, "library.db");
        if (File.Exists(FilePath) || !File.Exists(oldDb)) return; // already moved, or nothing to move

        try
        {
            // The database plus its two working files (WAL mode), and the start-up backup.
            foreach (string name in new[] { "library.db", "library.db-wal", "library.db-shm", "library.backup.db" })
            {
                string from = Path.Combine(oldFolder, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(newFolder, name), overwrite: false);
            }
        }
        catch (Exception)
        {
            // If copying fails half-way, start clean rather than with a damaged copy.
            foreach (string name in new[] { "library.db", "library.db-wal", "library.db-shm" })
                try { File.Delete(Path.Combine(newFolder, name)); } catch (Exception) { }
        }
    }

    // Each job opens its own connection (cheap; SQLite reuses them behind the scenes).
    // That keeps background scans and the screen from tripping over each other.
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={FilePath}");
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    /// <summary>Creates the tables the first time, turns on WAL, and makes the start-up backup.</summary>
    public void Initialize()
    {
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS tracks (
                id             INTEGER PRIMARY KEY,
                path           TEXT NOT NULL UNIQUE COLLATE NOCASE,
                title          TEXT NOT NULL,
                artist         TEXT NOT NULL DEFAULT '',
                album          TEXT NOT NULL DEFAULT '',
                duration_ms    INTEGER NOT NULL DEFAULT 0,
                file_size      INTEGER NOT NULL DEFAULT 0,
                modified_ticks INTEGER NOT NULL DEFAULT 0,
                added_ticks    INTEGER NOT NULL DEFAULT 0,
                missing        INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS library_folders (
                path TEXT PRIMARY KEY COLLATE NOCASE
            );

            CREATE TABLE IF NOT EXISTS playlists (
                id            INTEGER PRIMARY KEY,
                name          TEXT NOT NULL,
                kind          TEXT NOT NULL DEFAULT 'user',
                updated_ticks INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS playlists_name ON playlists(kind, name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS playlist_tracks (
                playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                position    INTEGER NOT NULL,
                track_id    INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                PRIMARY KEY (playlist_id, position)
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            """);

        // Milestone 4–6 additions. Older databases get the new columns added; nothing is lost.
        AddColumnIfMissing(connection, "tracks", "kind", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "tracks", "format", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "tracks", "chapters_url", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "tracks", "transcript_url", "TEXT NOT NULL DEFAULT ''");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS radio_favorites (
                track_id    INTEGER PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE,
                added_ticks INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS podcasts (
                feed_url      TEXT PRIMARY KEY COLLATE NOCASE,
                title         TEXT NOT NULL,
                author        TEXT NOT NULL DEFAULT '',
                image_url     TEXT NOT NULL DEFAULT '',
                updated_ticks INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS podcast_episodes (
                feed_url TEXT NOT NULL REFERENCES podcasts(feed_url) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                PRIMARY KEY (feed_url, position)
            );
            """);

        // A fresh backup copy at every start.
        using var backup = new SqliteConnection($"Data Source={BackupPath}");
        backup.Open();
        connection.BackupDatabase(backup);
    }

    // ===================== Songs =====================

    /// <summary>
    /// Saves songs into the library (new ones are added, known ones updated) and fills in
    /// each song's TrackId. Runs as one transaction, so it's fast even for thousands of songs.
    /// </summary>
    public void UpsertTracks(IList<PlaylistItem> items)
    {
        if (items.Count == 0) return;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracks (path, title, artist, album, duration_ms, file_size, modified_ticks, added_ticks, missing,
                                kind, format, chapters_url, transcript_url)
            VALUES ($path, $title, $artist, $album, $duration, $size, $modified, $added, 0,
                    $kind, $format, $chapters, $transcript)
            ON CONFLICT(path) DO UPDATE SET
                title = excluded.title, artist = excluded.artist, album = excluded.album,
                duration_ms = excluded.duration_ms, file_size = excluded.file_size,
                modified_ticks = excluded.modified_ticks, missing = 0,
                kind = excluded.kind, format = excluded.format,
                chapters_url = excluded.chapters_url, transcript_url = excluded.transcript_url
            RETURNING id;
            """;

        var path = command.Parameters.Add("$path", SqliteType.Text);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var artist = command.Parameters.Add("$artist", SqliteType.Text);
        var album = command.Parameters.Add("$album", SqliteType.Text);
        var duration = command.Parameters.Add("$duration", SqliteType.Integer);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var modified = command.Parameters.Add("$modified", SqliteType.Integer);
        var added = command.Parameters.Add("$added", SqliteType.Integer);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var format = command.Parameters.Add("$format", SqliteType.Text);
        var chapters = command.Parameters.Add("$chapters", SqliteType.Text);
        var transcript = command.Parameters.Add("$transcript", SqliteType.Text);

        long now = DateTime.UtcNow.Ticks;
        foreach (var item in items)
        {
            path.Value = item.Path;
            title.Value = item.Title;
            artist.Value = item.Artist;
            album.Value = item.Album;
            duration.Value = (long)item.Duration.TotalMilliseconds;
            size.Value = item.FileSize;
            modified.Value = item.ModifiedTicks;
            added.Value = now;
            kind.Value = (int)item.Kind;
            format.Value = item.Format;
            chapters.Value = item.ChaptersUrl;
            transcript.Value = item.TranscriptUrl;
            item.TrackId = Convert.ToInt64(command.ExecuteScalar());
        }

        transaction.Commit();
    }

    /// <summary>Every song in the library, sorted by artist, album and file path.</summary>
    public List<PlaylistItem> GetAllTracks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackColumns} FROM tracks
            WHERE kind = 0
            ORDER BY artist COLLATE NOCASE, album COLLATE NOCASE, path COLLATE NOCASE;
            """;
        return ReadTracks(command);
    }

    public int CountTracks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tracks WHERE missing = 0 AND kind = 0;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private const string TrackColumns =
        "id, path, title, artist, album, duration_ms, file_size, modified_ticks, missing, kind, format, chapters_url, transcript_url";

    private static List<PlaylistItem> ReadTracks(SqliteCommand command)
    {
        var result = new List<PlaylistItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PlaylistItem
            {
                TrackId = reader.GetInt64(0),
                Path = reader.GetString(1),
                Title = reader.GetString(2),
                Artist = reader.GetString(3),
                Album = reader.GetString(4),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                FileSize = reader.GetInt64(6),
                ModifiedTicks = reader.GetInt64(7),
                HasError = reader.GetInt64(8) != 0,
                Kind = (TrackKind)reader.GetInt64(9),
                Format = reader.GetString(10),
                ChaptersUrl = reader.GetString(11),
                TranscriptUrl = reader.GetString(12)
            });
        }
        return result;
    }

    // ===================== Library folders and scanning =====================

    public List<string> GetLibraryFolders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM library_folders ORDER BY path COLLATE NOCASE;";
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void AddLibraryFolder(string folder)
    {
        using var connection = Open();
        Execute(connection, "INSERT OR IGNORE INTO library_folders (path) VALUES ($p);", ("$p", folder));
    }

    public void RemoveLibraryFolder(string folder)
    {
        using var connection = Open();
        Execute(connection, "DELETE FROM library_folders WHERE path = $p;", ("$p", folder));
    }

    /// <summary>
    /// Scans all library folders. Only new or changed files have their tags read (that's the
    /// slow part), so a rescan of a big library is quick. Songs that disappeared from a library
    /// folder are marked missing (shown greyed out), not deleted, in case a drive is unplugged.
    /// "progress" receives short messages for the status line.
    /// </summary>
    public (int added, int updated, int missing) ScanLibrary(IProgress<string> progress)
    {
        var folders = GetLibraryFolders().Where(Directory.Exists).ToList();
        if (folders.Count == 0) return (0, 0, 0);

        // What do we already know? path → (size, date, missing)
        var known = new Dictionary<string, (long size, long ticks, bool missing)>(StringComparer.OrdinalIgnoreCase);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path, file_size, modified_ticks, missing FROM tracks WHERE kind = 0;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                known[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3) != 0);
        }

        progress.Report("Looking for songs in your library folders…");
        var files = PlaylistItem.ExpandToAudioFiles(folders);
        var onDisk = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        // Pick out the files that are new, changed, or were missing and are back.
        var toRead = new List<string>();
        foreach (var file in files)
        {
            if (!known.TryGetValue(file, out var info) || info.missing) { toRead.Add(file); continue; }
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length != info.size || fi.LastWriteTimeUtc.Ticks != info.ticks) toRead.Add(file);
            }
            catch (Exception) { /* can't read it now; leave as it is */ }
        }

        // Read tags and save, in batches so the status line can count up.
        int added = toRead.Count(f => !known.ContainsKey(f));
        int updated = toRead.Count - added;
        const int batchSize = 200;
        for (int i = 0; i < toRead.Count; i += batchSize)
        {
            progress.Report($"Reading tags… {i:N0} of {toRead.Count:N0}");
            var batch = toRead.Skip(i).Take(batchSize).Select(PlaylistItem.FromFile).ToList();
            UpsertTracks(batch);
        }

        // Mark songs that were inside a library folder but aren't there any more.
        var gone = known
            .Where(k => !k.Value.missing && !onDisk.Contains(k.Key) &&
                        folders.Any(f => k.Key.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            .Select(k => k.Key)
            .ToList();
        if (gone.Count > 0)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE tracks SET missing = 1 WHERE path = $p;";
            var p = command.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in gone) { p.Value = path; command.ExecuteNonQuery(); }
            transaction.Commit();
        }

        return (added, updated, gone.Count);
    }

    // ===================== Playlists =====================

    /// <summary>The playlists you saved by name, A to Z.</summary>
    public List<PlaylistInfo> GetPlaylists()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM playlists WHERE kind = $k ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$k", UserKind);
        var result = new List<PlaylistInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new PlaylistInfo(reader.GetInt64(0), reader.GetString(1)));
        return result;
    }

    /// <summary>Finds a saved playlist by name, or creates it. Returns its id.</summary>
    public long GetOrCreatePlaylist(string name) => GetOrCreate(name, UserKind);

    public void DeletePlaylist(long id)
    {
        using var connection = Open();
        Execute(connection, "DELETE FROM playlists WHERE id = $id;", ("$id", id));
    }

    /// <summary>Replaces the songs of a playlist with these, in this order.</summary>
    public void SavePlaylistTracks(long playlistId, IEnumerable<PlaylistItem> items)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = $id;";
            clear.Parameters.AddWithValue("$id", playlistId);
            clear.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO playlist_tracks (playlist_id, position, track_id) VALUES ($id, $pos, $track);";
            insert.Parameters.AddWithValue("$id", playlistId);
            var pos = insert.Parameters.Add("$pos", SqliteType.Integer);
            var track = insert.Parameters.Add("$track", SqliteType.Integer);

            int position = 0;
            foreach (var item in items)
            {
                if (item.TrackId == 0) continue; // not in the library (shouldn't happen)
                pos.Value = position++;
                track.Value = item.TrackId;
                insert.ExecuteNonQuery();
            }
        }

        using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE playlists SET updated_ticks = $now WHERE id = $id;";
            touch.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            touch.Parameters.AddWithValue("$id", playlistId);
            touch.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The songs of a playlist, in order. Files that no longer exist come back greyed out.</summary>
    public List<PlaylistItem> LoadPlaylistTracks(long playlistId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.path, t.title, t.artist, t.album, t.duration_ms, t.file_size, t.modified_ticks, t.missing,
                   t.kind, t.format, t.chapters_url, t.transcript_url
            FROM playlist_tracks pt JOIN tracks t ON t.id = pt.track_id
            WHERE pt.playlist_id = $id
            ORDER BY pt.position;
            """;
        command.Parameters.AddWithValue("$id", playlistId);
        var items = ReadTracks(command);
        foreach (var item in items)
            if (!item.IsOnline && !File.Exists(item.Path)) item.HasError = true;
        return items;
    }

    // ===================== Remembering the playlist and queue between runs =====================

    public void SaveSession(IEnumerable<PlaylistItem> playlist, IEnumerable<PlaylistItem> queue)
    {
        SavePlaylistTracks(GetOrCreate("Current playlist", SessionKind), playlist);
        SavePlaylistTracks(GetOrCreate("Queue", QueueKind), queue);
    }

    public (List<PlaylistItem> playlist, List<PlaylistItem> queue) LoadSession()
        => (LoadPlaylistTracks(GetOrCreate("Current playlist", SessionKind)),
            LoadPlaylistTracks(GetOrCreate("Queue", QueueKind)));

    // ===================== Settings =====================

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        using var connection = Open();
        Execute(connection,
            "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            ("$k", key), ("$v", (object?)value ?? DBNull.Value));
    }

    // ===================== Radio favourites =====================

    public List<PlaylistItem> GetRadioFavorites()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.path, t.title, t.artist, t.album, t.duration_ms, t.file_size, t.modified_ticks, t.missing,
                   t.kind, t.format, t.chapters_url, t.transcript_url
            FROM radio_favorites f JOIN tracks t ON t.id = f.track_id
            ORDER BY t.title COLLATE NOCASE;
            """;
        return ReadTracks(command);
    }

    public void AddRadioFavorite(PlaylistItem station)
    {
        if (station.TrackId == 0) UpsertTracks(new[] { station });
        using var connection = Open();
        Execute(connection, "INSERT OR IGNORE INTO radio_favorites (track_id, added_ticks) VALUES ($id, $now);",
                ("$id", station.TrackId), ("$now", DateTime.UtcNow.Ticks));
    }

    public void RemoveRadioFavorite(PlaylistItem station)
    {
        using var connection = Open();
        Execute(connection, "DELETE FROM radio_favorites WHERE track_id = (SELECT id FROM tracks WHERE path = $p);",
                ("$p", station.Path));
    }

    // ===================== Podcasts =====================

    /// <summary>Your subscriptions, shown as list entries (double-click one to see its episodes).</summary>
    public List<PlaylistItem> GetPodcasts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.feed_url, p.title, p.author, (SELECT COUNT(*) FROM podcast_episodes e WHERE e.feed_url = p.feed_url)
            FROM podcasts p ORDER BY p.title COLLATE NOCASE;
            """;
        var result = new List<PlaylistItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new PlaylistItem
            {
                Path = reader.GetString(0),
                Title = reader.GetString(1),
                Artist = reader.GetString(2),
                Album = $"{reader.GetInt64(3)} episodes",
                Kind = TrackKind.Podcast,
                Format = "FEED"
            });
        return result;
    }

    public bool IsSubscribed(string feedUrl)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM podcasts WHERE feed_url = $u;";
        command.Parameters.AddWithValue("$u", feedUrl);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>Subscribes (or refreshes) a podcast and stores its episodes for offline browsing.</summary>
    public void SavePodcast(PodcastFeed feed)
    {
        UpsertTracks(feed.Episodes);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO podcasts (feed_url, title, author, image_url, updated_ticks) VALUES ($u, $t, $a, $i, $now)
                ON CONFLICT(feed_url) DO UPDATE SET title = excluded.title, author = excluded.author,
                    image_url = excluded.image_url, updated_ticks = excluded.updated_ticks;
                DELETE FROM podcast_episodes WHERE feed_url = $u;
                """;
            upsert.Parameters.AddWithValue("$u", feed.FeedUrl);
            upsert.Parameters.AddWithValue("$t", feed.Title);
            upsert.Parameters.AddWithValue("$a", feed.Author);
            upsert.Parameters.AddWithValue("$i", feed.ImageUrl);
            upsert.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            upsert.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO podcast_episodes (feed_url, position, track_id) VALUES ($u, $pos, $id);";
            insert.Parameters.AddWithValue("$u", feed.FeedUrl);
            var pos = insert.Parameters.Add("$pos", SqliteType.Integer);
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            for (int i = 0; i < feed.Episodes.Count; i++)
            {
                pos.Value = i;
                id.Value = feed.Episodes[i].TrackId;
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    /// <summary>The stored episodes of a podcast, newest first (works offline).</summary>
    public List<PlaylistItem> GetEpisodes(string feedUrl)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.path, t.title, t.artist, t.album, t.duration_ms, t.file_size, t.modified_ticks, t.missing,
                   t.kind, t.format, t.chapters_url, t.transcript_url
            FROM podcast_episodes e JOIN tracks t ON t.id = e.track_id
            WHERE e.feed_url = $u ORDER BY e.position;
            """;
        command.Parameters.AddWithValue("$u", feedUrl);
        return ReadTracks(command);
    }

    public void Unsubscribe(string feedUrl)
    {
        using var connection = Open();
        Execute(connection, "DELETE FROM podcasts WHERE feed_url = $u;", ("$u", feedUrl));
    }

    // ===================== Helpers =====================

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
        check.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private long GetOrCreate(string name, string kind)
    {
        using var connection = Open();
        using var find = connection.CreateCommand();
        find.CommandText = "SELECT id FROM playlists WHERE kind = $k AND name = $n COLLATE NOCASE;";
        find.Parameters.AddWithValue("$k", kind);
        find.Parameters.AddWithValue("$n", name);
        if (find.ExecuteScalar() is long existing) return existing;

        using var create = connection.CreateCommand();
        create.CommandText = "INSERT INTO playlists (name, kind, updated_ticks) VALUES ($n, $k, $now) RETURNING id;";
        create.Parameters.AddWithValue("$n", name);
        create.Parameters.AddWithValue("$k", kind);
        create.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        return Convert.ToInt64(create.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql, params (string name, object value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}

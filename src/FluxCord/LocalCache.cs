using Microsoft.Data.Sqlite;

namespace FluxCord;

/// <summary>
/// Instant local SQLite WAL cache (%LOCALAPPDATA%\FluxCord\cache.db).
/// Stores recent messages per channel with proper numeric Snowflake ordering & live edit/delete sync.
/// </summary>
public static class LocalCache
{
    private static readonly string DbPath = Path.Combine(SettingsStore.DataDir, "cache.db");
    private static readonly object _lock = new();
    private static SqliteConnection? _conn;

    public static void Init()
    {
        lock (_lock)
        {
            if (_conn != null) return;
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDir);
                _conn = new SqliteConnection($"Data Source={DbPath}");
                _conn.Open();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    CREATE TABLE IF NOT EXISTS messages (
                        id TEXT PRIMARY KEY,
                        channel_id TEXT NOT NULL,
                        author_id TEXT NOT NULL,
                        author_name TEXT NOT NULL,
                        author_avatar TEXT NOT NULL,
                        author_color INTEGER NOT NULL,
                        content TEXT NOT NULL,
                        timestamp TEXT NOT NULL,
                        reply_preview TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_msg_ch ON messages(channel_id, id);
                    """;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { SettingsStore.Log("SQLite init: " + ex.Message); }
        }
    }

    public static void SaveMessages(string channelId, IReadOnlyList<DiscordMessage> msgs)
    {
        if (msgs.Count == 0) return;
        lock (_lock)
        {
            if (_conn == null) Init();
            if (_conn == null) return;
            try
            {
                using var tx = _conn.BeginTransaction();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO messages
                    (id, channel_id, author_id, author_name, author_avatar, author_color, content, timestamp, reply_preview)
                    VALUES ($id, $ch, $aid, $an, $av, $ac, $cnt, $ts, $rp);
                    """;
                var pId = cmd.Parameters.Add("$id", SqliteType.Text);
                var pCh = cmd.Parameters.Add("$ch", SqliteType.Text);
                var pAid = cmd.Parameters.Add("$aid", SqliteType.Text);
                var pAn = cmd.Parameters.Add("$an", SqliteType.Text);
                var pAv = cmd.Parameters.Add("$av", SqliteType.Text);
                var pAc = cmd.Parameters.Add("$ac", SqliteType.Integer);
                var pCnt = cmd.Parameters.Add("$cnt", SqliteType.Text);
                var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
                var pRp = cmd.Parameters.Add("$rp", SqliteType.Text);

                int start = Math.Max(0, msgs.Count - 80);
                for (int i = start; i < msgs.Count; i++)
                {
                    var m = msgs[i];
                    if (string.IsNullOrEmpty(m.Id)) continue;
                    pId.Value = m.Id;
                    pCh.Value = channelId;
                    pAid.Value = m.AuthorId;
                    pAn.Value = m.AuthorName;
                    pAv.Value = m.AuthorAvatar;
                    pAc.Value = (long)m.AuthorColor;
                    pCnt.Value = m.Content;
                    pTs.Value = m.Timestamp;
                    pRp.Value = m.ReplyPreview;
                    cmd.ExecuteNonQuery();
                }

                // Prune older cached messages per channel (>120 rows) so cache.db stays tiny & fast
                using var prune = _conn.CreateCommand();
                prune.Transaction = tx;
                prune.CommandText = """
                    DELETE FROM messages
                    WHERE channel_id = $ch AND id NOT IN (
                        SELECT id FROM messages WHERE channel_id = $ch ORDER BY CAST(id AS INTEGER) DESC LIMIT 120
                    );
                    """;
                prune.Parameters.AddWithValue("$ch", channelId);
                prune.ExecuteNonQuery();

                tx.Commit();
            }
            catch { }
        }
    }

    public static void UpdateMessage(string messageId, string newContent)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        lock (_lock)
        {
            if (_conn == null) return;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE messages SET content=$cnt WHERE id=$id;";
                cmd.Parameters.AddWithValue("$cnt", newContent);
                cmd.Parameters.AddWithValue("$id", messageId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public static void DeleteMessage(string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        lock (_lock)
        {
            if (_conn == null) return;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM messages WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", messageId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public static void DeleteMessagesBulk(IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0) return;
        lock (_lock)
        {
            if (_conn == null) return;
            try
            {
                using var tx = _conn.BeginTransaction();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM messages WHERE id=$id;";
                var pId = cmd.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in messageIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    pId.Value = id;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { }
        }
    }

    public static void DeleteChannelMessages(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return;
        lock (_lock)
        {
            if (_conn == null) return;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM messages WHERE channel_id=$ch;";
                cmd.Parameters.AddWithValue("$ch", channelId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    /// <summary>
    /// Reconciles offline-deleted messages in the fetched Snowflake window and persists fresh messages atomically.
    /// </summary>
    public static void ReconcileRecentChannelMessages(string channelId, IReadOnlyList<DiscordMessage> latestMsgs)
    {
        if (string.IsNullOrEmpty(channelId)) return;
        if (latestMsgs.Count == 0)
        {
            DeleteChannelMessages(channelId);
            return;
        }
        ulong minId = ulong.MaxValue;
        var liveIds = new HashSet<string>(latestMsgs.Count);
        foreach (var m in latestMsgs)
        {
            if (string.IsNullOrEmpty(m.Id)) continue;
            liveIds.Add(m.Id);
            if (ulong.TryParse(m.Id, out var u) && u < minId) minId = u;
        }
        if (minId != ulong.MaxValue)
        {
            lock (_lock)
            {
                if (_conn == null) Init();
                if (_conn != null)
                {
                    try
                    {
                        var stale = new List<string>();
                        using (var q = _conn.CreateCommand())
                        {
                            q.CommandText = "SELECT id FROM messages WHERE channel_id=$ch AND CAST(id AS INTEGER) >= $minId;";
                            q.Parameters.AddWithValue("$ch", channelId);
                            q.Parameters.AddWithValue("$minId", (long)minId);
                            using var r = q.ExecuteReader();
                            while (r.Read())
                            {
                                string id = r.GetString(0);
                                if (!liveIds.Contains(id)) stale.Add(id);
                            }
                        }
                        if (stale.Count > 0)
                        {
                            using var tx = _conn.BeginTransaction();
                            using var del = _conn.CreateCommand();
                            del.Transaction = tx;
                            del.CommandText = "DELETE FROM messages WHERE id=$id;";
                            var pId = del.Parameters.Add("$id", SqliteType.Text);
                            foreach (var sid in stale) { pId.Value = sid; del.ExecuteNonQuery(); }
                            tx.Commit();
                        }
                    }
                    catch { }
                }
            }
        }
        SaveMessages(channelId, latestMsgs);
    }

    public static List<DiscordMessage> LoadMessages(string channelId, int limit = 50)
    {
        var res = new List<DiscordMessage>();
        lock (_lock)
        {
            if (_conn == null) Init();
            if (_conn == null) return res;
            try
            {
                using var cmd = _conn.CreateCommand();
                // Numeric Snowflake ordering (handles 18-digit vs 19-digit IDs accurately!)
                cmd.CommandText = "SELECT id, author_id, author_name, author_avatar, author_color, content, timestamp, reply_preview FROM messages WHERE channel_id=$ch ORDER BY CAST(id AS INTEGER) DESC LIMIT $lim;";
                cmd.Parameters.AddWithValue("$ch", channelId);
                cmd.Parameters.AddWithValue("$lim", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    res.Add(new DiscordMessage
                    {
                        Id = r.GetString(0),
                        ChannelId = channelId,
                        AuthorId = r.GetString(1),
                        AuthorName = r.GetString(2),
                        AuthorAvatar = r.GetString(3),
                        AuthorColor = (uint)r.GetInt64(4),
                        Content = r.GetString(5),
                        Timestamp = r.GetString(6),
                        ReplyPreview = r.GetString(7),
                    });
                }
                res.Reverse();
            }
            catch { }
        }
        return res;
    }

    public static void Close()
    {
        lock (_lock)
        {
            try { _conn?.Close(); _conn?.Dispose(); } catch { }
            _conn = null;
        }
    }
}

using Microsoft.Data.Sqlite;

namespace FluxCord;

/// <summary>
/// Instant local SQLite WAL cache (%LOCALAPPDATA%\FluxCord\cache.db).
/// Stores recent messages per channel so switching channels is <2ms with zero wait.
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

                int start = Math.Max(0, msgs.Count - 60);
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
                tx.Commit();
            }
            catch { }
        }
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
                cmd.CommandText = "SELECT id, author_id, author_name, author_avatar, author_color, content, timestamp, reply_preview FROM messages WHERE channel_id=$ch ORDER BY id DESC LIMIT $lim;";
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

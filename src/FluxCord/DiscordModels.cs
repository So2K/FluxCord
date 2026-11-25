namespace FluxCord;

public sealed class RtcRegionInfo
{
    public string Id = "";
    public string Name = "";
    public int PingMs = -1; // -1 = measuring / unknown
    public bool Optimal;
}

public sealed class DiscordUser
{
    public string Id = "";
    public string Username = "";
    public string DisplayName = "";
    public string Avatar = "";
    public string Status = "offline"; // online, idle, dnd, offline

    public string? AvatarUrl => string.IsNullOrEmpty(Avatar) || string.IsNullOrEmpty(Id)
        ? null
        : $"https://cdn.discordapp.com/avatars/{Id}/{Avatar}.png?size=64";
}

public sealed class DiscordFriend
{
    public string UserId = "";
    public string Username = "";
    public string DisplayName = "";
    public string Avatar = "";
    public string Status = "offline";
    public int Type; // 1 = Friend, 3 = Incoming, 4 = Outgoing

    public string? AvatarUrl => string.IsNullOrEmpty(Avatar) || string.IsNullOrEmpty(UserId)
        ? null
        : $"https://cdn.discordapp.com/avatars/{UserId}/{Avatar}.png?size=64";
}

public sealed class DiscordGuild
{
    public string Id = "";
    public string Name = "";
    public string Icon = "";
    public int UnreadCount;
    public List<DiscordChannel> Channels = [];
    public Dictionary<string, VoiceMember> VoiceStates = new();

    public string? IconUrl => string.IsNullOrEmpty(Icon)
        ? null
        : $"https://cdn.discordapp.com/icons/{Id}/{Icon}.png?size=96";
}

public sealed class DiscordChannel
{
    public string Id = "";
    public string GuildId = "";
    public string Name = "";
    public string Topic = "";
    public string RecipientId = "";
    public string RecipientAvatar = "";
    public int Type; // 0=Text, 1=DM, 2=Voice, 3=GroupDM, 4=Category, 5=Announcement, 13=Stage, 15=Forum
    public int Position;
    public string ParentId = "";
    public string LastMessageId = "";
    public bool IsVoice => Type == 2 || Type == 13;
    public bool IsCategory => Type == 4;
    public string? DmAvatarUrl => string.IsNullOrEmpty(RecipientAvatar) || string.IsNullOrEmpty(RecipientId)
        ? null
        : $"https://cdn.discordapp.com/avatars/{RecipientId}/{RecipientAvatar}.png?size=64";
}

public sealed class VoiceMember
{
    public string UserId = "";
    public string ChannelId = "";
    public string Username = "";
    public string Avatar = "";
    public bool SelfMute;
    public bool SelfDeaf;
    public bool SelfStream;
    public bool Speaking;
    public long SpeakingUntil;

    public string? AvatarUrl => string.IsNullOrEmpty(Avatar) || string.IsNullOrEmpty(UserId)
        ? null
        : $"https://cdn.discordapp.com/avatars/{UserId}/{Avatar}.png?size=48";
}

public sealed class DiscordAttachment
{
    public string Url = "";
    public string Filename = "";
    public long Size;
    public bool IsImage;
}

public sealed class DiscordEmbed
{
    public string Title = "";
    public string Description = "";
    public string Url = "";
    public string Author = "";
    public string Footer = "";
    public string ImageUrl = "";
    public uint Color = 0xFF5865F2;
}

public sealed class DiscordMessage
{
    public string Id = "";
    public string ChannelId = "";
    public string AuthorId = "";
    public string AuthorName = "";
    public string AuthorAvatar = "";
    public uint AuthorColor = 0xFF8AB4F8;
    public string Content = "";
    public string Timestamp = "";
    public string ReplyPreview = "";
    public List<string> AttachmentUrls = [];
    public List<DiscordAttachment> Attachments = [];
    public List<DiscordEmbed> Embeds = [];
    public List<string> Reactions = [];
    public bool Pending;

    public string? AvatarUrl => string.IsNullOrEmpty(AuthorAvatar)
        ? null
        : $"https://cdn.discordapp.com/avatars/{AuthorId}/{AuthorAvatar}.png?size=64";
}

using System;
using System.Collections.Generic;

namespace TToDo
{
    public class TaskItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public ulong ChannelId { get; set; }
        public ulong UserId { get; set; }
        public string GuildName { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public string UserName { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string Assignee { get; set; } = "";
        public string Content { get; set; } = "";
        public int Priority { get; set; } = -1;
        public int Difficulty { get; set; } = -1;
        public List<string> Tags { get; set; } = new List<string>();
        public bool IsSnoozed { get; set; }
        public bool IsForgotten { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class UserConfig
    {
        public ulong UserId { get; set; }
        public string ReportTime { get; set; } = "00:00";
    }

    public class BatchChannelRequest
    {
        public ulong UserId { get; set; }
        public string GuildName { get; set; } = "";
        public string ChannelName { get; set; } = "";
    }

    public class CleanupRequest
    {
        public List<string> TargetUserNames { get; set; } = new List<string>();
    }

    // ★追加: 日報送信リクエスト
    public class ReportRequest
    {
        public string UserName { get; set; } = "";
        public string GuildName { get; set; } = "";
        public string ChannelName { get; set; } = "";
    }
}
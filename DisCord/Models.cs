using System;
using System.Collections.Generic;

namespace TToDo
{
    public class TaskItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        // Discord情報
        public ulong ChannelId { get; set; }
        public ulong UserId { get; set; }
        public string GuildName { get; set; } = "";
        public string ChannelName { get; set; } = "";

        // ユーザー情報
        public string UserName { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string Assignee { get; set; } = "";

        // タスク内容
        public string Content { get; set; } = "";
        public int Priority { get; set; } = -1;
        public int Difficulty { get; set; } = -1;
        public List<string> Tags { get; set; } = new List<string>();

        // 状態
        public bool IsSnoozed { get; set; }
        public bool IsForgotten { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class UserConfig
    {
        public ulong UserId { get; set; }
        public string ReportTime { get; set; } = "00:00";
    }

    // AssigneeGroup は削除しました

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
}
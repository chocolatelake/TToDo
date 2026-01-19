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

        // アーカイブ済みフラグ
        public bool IsForgotten { get; set; }

        // ★追加: アーカイブされた日時（自動削除の判定に使用）
        public DateTime? ArchivedAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        public DateTime CreatedAt { get; set; } = Globals.GetJstNow();

        // 日報で報告済みかどうかのフラグ
        public bool IsReported { get; set; }

        // 開始日と期日
        public DateTime? StartDate { get; set; }
        public DateTime? DueDate { get; set; }

        // 時間/工数 (0:不明, 1:短い, 2:長い)
        public int TimeMode { get; set; }
    }

    public class UserConfig
    {
        public ulong UserId { get; set; }
        public string UserName { get; set; } = "";
        public string ReportTime { get; set; } = "00:00";
        public string TargetGuild { get; set; } = "";
        public string TargetChannel { get; set; } = "";
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

    public class ReportRequest
    {
        public string TargetUser { get; set; } = "";
        public string TargetGuild { get; set; } = "";
        public string TargetChannel { get; set; } = "";

        public string SourceGuild { get; set; } = "";
        public string SourceChannel { get; set; } = "";

        public string TargetRange { get; set; } = "today";
    }
}
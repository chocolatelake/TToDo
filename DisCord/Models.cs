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
        public string GuildName { get; set; } = "";   // サーバー名
        public string ChannelName { get; set; } = ""; // チャンネル名

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

    public class AssigneeGroup
    {
        public string MainName { get; set; } = "";
        public List<string> Aliases { get; set; } = new List<string>();
    }

    // ★追加: 一括変更用のリクエスト型
    public class BatchChannelRequest
    {
        public ulong UserId { get; set; }
        public string GuildName { get; set; } = "";
        public string ChannelName { get; set; } = "";
    }
}
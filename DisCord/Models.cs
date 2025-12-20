using System;
using System.Collections.Generic;

namespace TToDo
{
    public class TaskItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public ulong ChannelId { get; set; }
        public ulong UserId { get; set; }
        public string UserName { get; set; } = "";
        public string AvatarUrl { get; set; } = ""; // ★追加: アイコンURL
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
}
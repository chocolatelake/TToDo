using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TToDo
{
    public static class Globals
    {
        public static string BotToken { get; set; } = "";
        public static string WebUrl { get; set; } = "http://*:5000";

        private const string DbFileName = "tasks.json";
        private const string ConfigFileName = "config.json";
        private const string AssigneesFileName = "assignees.json"; // ★追加

        public static List<TaskItem> AllTasks = new List<TaskItem>();
        public static List<UserConfig> Configs = new List<UserConfig>();
        public static List<AssigneeGroup> AssigneeGroups = new List<AssigneeGroup>(); // ★追加

        public static readonly object Lock = new object();
        public static readonly TimeZoneInfo JstZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

        public static DateTime GetJstNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, JstZone);

        public static void LoadConfiguration(IConfiguration config)
        {
            BotToken = config["Discord:Token"] ?? "";
            var port = config["Web:Port"] ?? "5000";
            WebUrl = $"http://*:{port}";
        }

        public static void SaveData()
        {
            lock (Lock)
            {
                try { File.WriteAllText(DbFileName, JsonSerializer.Serialize(AllTasks)); } catch { }
                try { File.WriteAllText(ConfigFileName, JsonSerializer.Serialize(Configs)); } catch { }
                try { File.WriteAllText(AssigneesFileName, JsonSerializer.Serialize(AssigneeGroups)); } catch { } // ★追加
            }
        }

        public static void LoadData()
        {
            lock (Lock)
            {
                if (File.Exists(DbFileName)) try { AllTasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(DbFileName)) ?? new(); } catch { }
                if (File.Exists(ConfigFileName)) try { Configs = JsonSerializer.Deserialize<List<UserConfig>>(File.ReadAllText(ConfigFileName)) ?? new(); } catch { }
                if (File.Exists(AssigneesFileName)) try { AssigneeGroups = JsonSerializer.Deserialize<List<AssigneeGroup>>(File.ReadAllText(AssigneesFileName)) ?? new(); } catch { } // ★追加
            }
        }
    }
}
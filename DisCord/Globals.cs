using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TToDo
{
    public static class Globals
    {
        // 設定
        public static string BotToken { get; set; } = "";
        public static string WebUrl { get; set; } = "http://*:5000";

        // ファイル名
        private const string DbFileName = "tasks.json";
        private const string ConfigFileName = "config.json";

        // 共有データ
        public static List<TaskItem> AllTasks = new List<TaskItem>();
        public static List<UserConfig> Configs = new List<UserConfig>();
        public static readonly object Lock = new object();
        public static readonly TimeZoneInfo JstZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

        public static DateTime GetJstNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, JstZone);

        // 設定ロード
        public static void LoadConfiguration(IConfiguration config)
        {
            BotToken = config["Discord:Token"] ?? "";
            var port = config["Web:Port"] ?? "5000";
            WebUrl = $"http://*:{port}";
        }

        // 保存
        public static void SaveData()
        {
            lock (Lock)
            {
                try { File.WriteAllText(DbFileName, JsonSerializer.Serialize(AllTasks)); } catch { }
                try { File.WriteAllText(ConfigFileName, JsonSerializer.Serialize(Configs)); } catch { }
            }
        }

        // 読み込み
        public static void LoadData()
        {
            lock (Lock)
            {
                if (File.Exists(DbFileName)) try { AllTasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(DbFileName)) ?? new(); } catch { }
                if (File.Exists(ConfigFileName)) try { Configs = JsonSerializer.Deserialize<List<UserConfig>>(File.ReadAllText(ConfigFileName)) ?? new(); } catch { }
            }
        }
    }
}
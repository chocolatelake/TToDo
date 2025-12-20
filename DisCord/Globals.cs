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

        // 実際にアプリが動くポート (localhost:5000)
        public static string BindUrl { get; set; } = "http://*:5000";

        // ★追加: Discordで案内する用の公開URL (ngrokなどのURL)
        public static string PublicUrl { get; set; } = "http://localhost:5000";

        private const string DbFileName = "tasks.json";
        private const string ConfigFileName = "config.json";

        public static List<TaskItem> AllTasks = new List<TaskItem>();
        public static List<UserConfig> Configs = new List<UserConfig>();

        public static readonly object Lock = new object();
        public static readonly TimeZoneInfo JstZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

        public static DateTime GetJstNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, JstZone);

        public static void LoadConfiguration(IConfiguration config)
        {
            BotToken = config["Discord:Token"] ?? "";

            // ポート設定があれば読み込む
            var port = config["Web:Port"] ?? "5000";
            BindUrl = $"http://*:{port}";

            // ★追加: 公開URLがあれば読み込む (なければローカルURLをデフォルトに)
            PublicUrl = config["Web:PublicUrl"] ?? $"http://localhost:{port}";
            // 末尾の/を削除
            if (PublicUrl.EndsWith("/")) PublicUrl = PublicUrl.Substring(0, PublicUrl.Length - 1);
        }

        public static void SaveData()
        {
            lock (Lock)
            {
                try { File.WriteAllText(DbFileName, JsonSerializer.Serialize(AllTasks)); } catch { }
                try { File.WriteAllText(ConfigFileName, JsonSerializer.Serialize(Configs)); } catch { }
            }
        }

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
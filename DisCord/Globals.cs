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
        public static string BindUrl { get; set; } = "http://*:5000";
        public static string PublicUrl { get; set; } = "http://localhost:5000";

        // ★変更: const ではなく static プロパティに変更 (初期値は相対パス)
        public static string DataDirectory { get; private set; } = "TToDoData";

        private static string DbPath => Path.Combine(DataDirectory, "tasks.json");
        private static string ConfigPath => Path.Combine(DataDirectory, "config.json");

        public static List<TaskItem> AllTasks = new List<TaskItem>();
        public static List<UserConfig> Configs = new List<UserConfig>();

        public static readonly object Lock = new object();
        public static readonly TimeZoneInfo JstZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

        public static DateTime GetJstNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, JstZone);

        public static void LoadConfiguration(IConfiguration config)
        {
            BotToken = config["Discord:Token"] ?? "";

            var port = config["Web:Port"] ?? "5000";
            BindUrl = $"http://*:{port}";

            PublicUrl = config["Web:PublicUrl"] ?? $"http://localhost:{port}";
            if (PublicUrl.EndsWith("/")) PublicUrl = PublicUrl.Substring(0, PublicUrl.Length - 1);

            // ★追加: 設定ファイルからDataPathを読み込む
            var configuredPath = config["DataPath"];
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                DataDirectory = configuredPath;
            }
        }

        public static void SaveData()
        {
            lock (Lock)
            {
                // フォルダが存在しない場合は作成
                if (!Directory.Exists(DataDirectory))
                {
                    Directory.CreateDirectory(DataDirectory);
                }

                try { File.WriteAllText(DbPath, JsonSerializer.Serialize(AllTasks)); } catch { }
                try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Configs)); } catch { }
            }
        }

        public static void LoadData()
        {
            lock (Lock)
            {
                if (File.Exists(DbPath)) try { AllTasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(DbPath)) ?? new(); } catch { }
                if (File.Exists(ConfigPath)) try { Configs = JsonSerializer.Deserialize<List<UserConfig>>(File.ReadAllText(ConfigPath)) ?? new(); } catch { }
            }
        }
    }
}
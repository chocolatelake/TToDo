using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TToDo
{
    public class BotBackgroundService : BackgroundService
    {
        private readonly DiscordBot _bot;

        // システムメンテナンス（掃除・バックアップ）を最後に実行した日
        private DateTime _lastSystemMaintenanceDate = DateTime.MinValue;

        // ユーザーごとに「最後に日報を出した日」を覚えておく辞書
        // キー: UserId, 値: 最後に実行した日付
        private Dictionary<ulong, DateTime> _userLastReportDates = new Dictionary<ulong, DateTime>();

        public BotBackgroundService(DiscordBot bot)
        {
            _bot = bot;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Bot起動
            await _bot.StartAsync();

            // 起動直後の暴発を防ぐため、システムメンテは「昨日済ませた」ことにする
            _lastSystemMaintenanceDate = DateTime.Now.Date.AddDays(-1);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = Globals.GetJstNow(); // 日本時間
                    string currentTimeStr = now.ToString("HH:mm");

                    // ---------------------------------------------------------
                    // 1. システムメンテ（掃除・バックアップ）: 日付が変わっていたら実行
                    // ---------------------------------------------------------
                    if (_lastSystemMaintenanceDate.Date < now.Date)
                    {
                        Console.WriteLine($"[System] Starting Daily Maintenance: {now}");
                        CleanupOldArchives();
                        BackupData();

                        // 「今日は終わった」と記録
                        _lastSystemMaintenanceDate = now.Date;
                        Console.WriteLine($"[System] Maintenance Done. Next run will be tomorrow.");
                    }

                    // ---------------------------------------------------------
                    // 2. ユーザーごとの日報チェック: 設定時刻になったら実行
                    // ---------------------------------------------------------
                    List<UserConfig> currentConfigs;
                    lock (Globals.Lock)
                    {
                        // 処理中にコレクションが変わらないようにコピーを取得
                        currentConfigs = Globals.Configs.ToList();
                    }

                    foreach (var config in currentConfigs)
                    {
                        // Webで設定された ReportTime ("HH:mm") と現在時刻が一致するか？
                        if (config.ReportTime == currentTimeStr)
                        {
                            // 辞書に未登録なら初期化
                            if (!_userLastReportDates.ContainsKey(config.UserId))
                            {
                                _userLastReportDates[config.UserId] = DateTime.MinValue;
                            }

                            // 「今日まだ送っていない」なら実行
                            if (_userLastReportDates[config.UserId].Date < now.Date)
                            {
                                Console.WriteLine($"[Report] Sending daily report for {config.UserName} ({config.UserId}) at {currentTimeStr}");

                                // Bot側の日報処理を呼び出す
                                await _bot.RunDailyClose(config.UserId);

                                // 「今日は送った」と記録
                                _userLastReportDates[config.UserId] = now.Date;
                            }
                        }
                    }

                    // 1分間待機して、またチェックする
                    // 秒単位のズレがあっても、必ず「その分のどこか」でヒットします
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Loop Error] {ex.Message}");
                    // エラーが出ても止まらず、1分後に再トライ
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }

        private void CleanupOldArchives()
        {
            try
            {
                lock (Globals.Lock)
                {
                    var now = Globals.GetJstNow();
                    int removedCount = Globals.AllTasks.RemoveAll(t =>
                        t.IsForgotten &&
                        t.ArchivedAt.HasValue &&
                        (now - t.ArchivedAt.Value).TotalDays >= 90
                    );

                    if (removedCount > 0)
                    {
                        Globals.SaveData();
                        Console.WriteLine($"[Cleanup] {removedCount} items were auto-deleted from archive.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cleanup Error] {ex.Message}");
            }
        }

        private void BackupData()
        {
            try
            {
                // Globalsからパスを取得（環境に合わせて柔軟にするため）
                string sourceDir = Globals.DataDirectory;
                string backupDir = Path.Combine(sourceDir, "Backup");

                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string[] files = { "tasks.json", "config.json" };

                foreach (var file in files)
                {
                    string sourcePath = Path.Combine(sourceDir, file);
                    if (File.Exists(sourcePath))
                    {
                        string destPath = Path.Combine(backupDir, $"{Path.GetFileNameWithoutExtension(file)}_{timestamp}{Path.GetExtension(file)}");
                        File.Copy(sourcePath, destPath, true);
                        Console.WriteLine($"[Backup] Saved: {destPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Backup Error] {ex.Message}");
            }
        }
    }
}
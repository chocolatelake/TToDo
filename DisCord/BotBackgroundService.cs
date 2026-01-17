using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TToDo
{
    public class BotBackgroundService : BackgroundService
    {
        private readonly DiscordBot _bot;

        public BotBackgroundService(DiscordBot bot)
        {
            _bot = bot;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _bot.StartAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var nextRun = now.Date.AddDays(1);
                var delay = nextRun - now;

                try
                {
                    await Task.Delay(delay, stoppingToken);
                    BackupData();
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Backup Loop Error] {ex.Message}");
                }
            }
        }

        private void BackupData()
        {
            try
            {
                string sourceDir = "/Users/chocolatecakelake/Repositories/TToDo/TToDoData";
                string backupDir = Path.Combine(sourceDir, "Backup");

                if (!Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                }

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
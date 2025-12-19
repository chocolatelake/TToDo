using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace TToDo
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 1. 設定 & データ読み込み
            var builder = WebApplication.CreateBuilder(args);
            Globals.LoadConfiguration(builder.Configuration);
            Globals.LoadData();

            // 2. Webサーバー構築
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(Globals.WebUrl);
            var app = builder.Build();

            // 静的ファイル(wwwroot内のindex.htmlなど)を使用
            app.UseFileServer();

            // --- Web API Endpoints ---

            // タスク取得 (チームモード対応)
            app.MapGet("/api/tasks", (HttpContext ctx) => {
                lock (Globals.Lock)
                {
                    string mode = ctx.Request.Query["mode"];

                    // チームモード: アーカイブ以外、全員分返す
                    if (mode == "team")
                    {
                        return Results.Json(Globals.AllTasks.Where(t => !t.IsForgotten));
                    }

                    // 個人モード: 指定IDのみ
                    if (ulong.TryParse(ctx.Request.Query["uid"], out ulong userId))
                    {
                        return Results.Json(Globals.AllTasks.Where(t => t.UserId == userId));
                    }

                    return Results.Json(new List<TaskItem>());
                }
            });

            app.MapPost("/api/update", async (TaskItem item) => {
                lock (Globals.Lock)
                {
                    var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id);
                    if (t != null)
                    {
                        t.Content = item.Content;
                        t.Priority = item.Priority;
                        t.Difficulty = item.Difficulty;
                        t.Tags = item.Tags;
                        Globals.SaveData();
                    }
                }
                return Results.Ok();
            });

            app.MapPost("/api/done", async (TaskItem item) => {
                lock (Globals.Lock)
                {
                    var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id);
                    if (t != null)
                    {
                        t.CompletedAt = t.CompletedAt == null ? Globals.GetJstNow() : null;
                        t.IsSnoozed = false;
                        Globals.SaveData();
                    }
                }
                return Results.Ok();
            });

            app.MapPost("/api/archive", async (TaskItem item) => {
                lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = true; Globals.SaveData(); } }
                return Results.Ok();
            });

            app.MapPost("/api/restore", async (TaskItem item) => {
                lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = false; Globals.SaveData(); } }
                return Results.Ok();
            });

            app.MapPost("/api/delete", async (TaskItem item) => {
                lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { Globals.AllTasks.Remove(t); Globals.SaveData(); } }
                return Results.Ok();
            });

            // 3. Discord Bot起動
            var bot = new DiscordBot();
            await bot.StartAsync();

            System.Console.WriteLine($"\n🚀 Dashboard is running at: {Globals.WebUrl.Replace("*", "localhost")}\n");
            await app.RunAsync();
        }
    }
}
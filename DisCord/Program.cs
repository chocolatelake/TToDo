using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration; // 追加
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;

namespace TToDo
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 1. Webサーバー構築 & 設定読み込み
            var builder = WebApplication.CreateBuilder(args);

            // appsettings.json を読み込む
            Globals.LoadConfiguration(builder.Configuration);
            Globals.LoadData(); // データもロード

            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(Globals.WebUrl);

            var app = builder.Build();

            // ★重要: 静的ファイル(wwwroot)を使う設定
            app.UseFileServer();

            // --- Web API Endpoints ---

            // ★変更: モードによって返すデータを変える
            app.MapGet("/api/tasks", (HttpContext ctx) => {
                lock (Globals.Lock)
                {
                    string mode = ctx.Request.Query["mode"];

                    // チームモードなら全員分 (アーカイブ以外)
                    if (mode == "team")
                    {
                        return Results.Json(Globals.AllTasks.Where(t => !t.IsForgotten));
                    }

                    // 個人モードならその人だけ
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

            Console.WriteLine($"\n🚀 Dashboard is running at: {Globals.WebUrl.Replace("*", "localhost")}\n");
            await app.RunAsync();
        }
    }
}
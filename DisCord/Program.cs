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
            var builder = WebApplication.CreateBuilder(args);
            Globals.LoadConfiguration(builder.Configuration);
            Globals.LoadData();

            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(Globals.WebUrl);
            var app = builder.Build();

            app.UseFileServer();

            // --- Web API Endpoints ---

            app.MapGet("/api/tasks", (HttpContext ctx) => {
                lock (Globals.Lock)
                {
                    return Results.Json(Globals.AllTasks);
                }
            });

            app.MapPost("/api/update", async (TaskItem item) => {
                lock (Globals.Lock)
                {
                    var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id);
                    if (t != null)
                    {
                        if ((t.ChannelName != item.ChannelName || t.GuildName != item.GuildName) && DiscordBot.Instance != null)
                        {
                            var newId = DiscordBot.Instance.ResolveChannelId(item.GuildName, item.ChannelName);
                            if (newId.HasValue) t.ChannelId = newId.Value;
                        }

                        t.Content = item.Content;
                        t.Priority = item.Priority;
                        t.Difficulty = item.Difficulty;
                        t.Tags = item.Tags;
                        t.Assignee = item.Assignee;
                        t.GuildName = item.GuildName;
                        t.ChannelName = item.ChannelName;
                        Globals.SaveData();
                    }
                }
                return Results.Ok();
            });

            app.MapPost("/api/batch/channel", async (BatchChannelRequest req) => {
                lock (Globals.Lock)
                {
                    ulong? newChannelId = DiscordBot.Instance?.ResolveChannelId(req.GuildName, req.ChannelName);

                    if (newChannelId == null)
                    {
                        return Results.BadRequest(new { message = "指定されたサーバーまたはチャンネルが見つかりません。Botが参加しているか確認してください。" });
                    }

                    var targets = Globals.AllTasks.Where(t => t.UserId == req.UserId && !t.IsForgotten);
                    foreach (var t in targets)
                    {
                        t.GuildName = req.GuildName;
                        t.ChannelName = req.ChannelName;
                        t.ChannelId = newChannelId.Value;
                    }
                    Globals.SaveData();
                }
                return Results.Ok();
            });

            // Assignee API は削除しました

            app.MapPost("/api/archive/cleanup", async (CleanupRequest req) => {
                lock (Globals.Lock)
                {
                    if (req.TargetUserNames != null && req.TargetUserNames.Count > 0)
                    {
                        Globals.AllTasks.RemoveAll(t => t.IsForgotten && req.TargetUserNames.Contains(t.UserName));
                    }
                    else
                    {
                        Globals.AllTasks.RemoveAll(t => t.IsForgotten);
                    }
                    Globals.SaveData();
                }
                return Results.Ok();
            });

            app.MapPost("/api/done", async (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.CompletedAt = t.CompletedAt == null ? Globals.GetJstNow() : null; t.IsSnoozed = false; Globals.SaveData(); } } return Results.Ok(); });
            app.MapPost("/api/archive", async (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = true; Globals.SaveData(); } } return Results.Ok(); });
            app.MapPost("/api/restore", async (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = false; Globals.SaveData(); } } return Results.Ok(); });
            app.MapPost("/api/delete", async (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { Globals.AllTasks.Remove(t); Globals.SaveData(); } } return Results.Ok(); });

            var bot = new DiscordBot();
            await bot.StartAsync();

            System.Console.WriteLine($"\n🚀 Dashboard is running at: {Globals.WebUrl.Replace("*", "localhost")}\n");
            await app.RunAsync();
        }
    }
}
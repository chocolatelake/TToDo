using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

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
            builder.WebHost.UseUrls(Globals.BindUrl);

            var app = builder.Build();
            app.UseFileServer();

            // --- Web API Endpoints ---

            app.MapGet("/api/tasks", (HttpContext ctx) => { lock (Globals.Lock) { return Results.Json(Globals.AllTasks); } });
            app.MapGet("/api/config", () => { lock (Globals.Lock) { return Results.Json(Globals.Configs); } });

            app.MapPost("/api/config", (UserConfig req) => {
                lock (Globals.Lock)
                {
                    var c = Globals.Configs.FirstOrDefault(x => x.UserId == req.UserId);
                    if (c == null) { c = new UserConfig { UserId = req.UserId }; Globals.Configs.Add(c); }
                    c.UserName = req.UserName;
                    c.ReportTime = req.ReportTime;
                    c.TargetGuild = req.TargetGuild;
                    c.TargetChannel = req.TargetChannel;
                    Globals.SaveData();
                }
                return Results.Ok();
            });

            app.MapPost("/api/report/send", async (ReportRequest req) => {
                if (DiscordBot.Instance == null) return Results.BadRequest();
                bool success = await DiscordBot.Instance.SendManualReport(req);
                return success ? Results.Ok() : Results.BadRequest();
            });

            app.MapPost("/api/create", (TaskItem item) => {
                lock (Globals.Lock)
                {
                    if (string.IsNullOrWhiteSpace(item.Content)) return Results.BadRequest();
                    item.Id = Guid.NewGuid().ToString("N");
                    item.UserName = "Webで作成";

                    if (!string.IsNullOrEmpty(item.GuildName) && !string.IsNullOrEmpty(item.ChannelName) && DiscordBot.Instance != null)
                    {
                        var newId = DiscordBot.Instance.ResolveChannelId(item.GuildName, item.ChannelName);
                        if (newId.HasValue) item.ChannelId = newId.Value;
                    }

                    // ★修正: 新規作成時のアイコン取得 (DBキャッシュ対応)
                    if (!string.IsNullOrEmpty(item.Assignee))
                    {
                        string newUrl = "";
                        // 1. Discordから取得を試みる
                        if (DiscordBot.Instance != null) newUrl = DiscordBot.Instance.ResolveAvatarUrl(item.Assignee);

                        // 2. 失敗したら、既存タスクから同じ名前の人のアイコンを探す
                        if (string.IsNullOrEmpty(newUrl))
                        {
                            var existing = Globals.AllTasks.FirstOrDefault(x =>
                                (x.Assignee == item.Assignee || x.UserName == item.Assignee)
                                && !string.IsNullOrEmpty(x.AvatarUrl));
                            if (existing != null) newUrl = existing.AvatarUrl;
                        }

                        if (!string.IsNullOrEmpty(newUrl)) item.AvatarUrl = newUrl;
                    }

                    Globals.AllTasks.Add(item);
                    Globals.SaveData();
                }
                return Results.Ok();
            });

            app.MapPost("/api/update", (TaskItem item) => {
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

                        // ★修正: 更新時のアイコン取得 (DBキャッシュ対応)
                        bool assigneeChanged = t.Assignee != item.Assignee;

                        // ターゲットとなる名前を決定 (変更があった場合は新しい担当者 or 外れたら作成者)
                        string targetUser = assigneeChanged
                            ? (!string.IsNullOrEmpty(item.Assignee) ? item.Assignee : t.UserName)
                            : t.Assignee;

                        // 担当者が変わった、または 現在アイコンがなくて担当者がいる場合
                        if (assigneeChanged || (!string.IsNullOrEmpty(targetUser) && string.IsNullOrEmpty(t.AvatarUrl)))
                        {
                            string newUrl = "";

                            // 1. Discordから取得を試みる
                            if (DiscordBot.Instance != null)
                            {
                                newUrl = DiscordBot.Instance.ResolveAvatarUrl(targetUser);
                            }

                            // 2. 失敗したら、既存のタスクDBから同じ名前の人のアイコンを探す (DBキャッシュ利用)
                            if (string.IsNullOrEmpty(newUrl))
                            {
                                var existing = Globals.AllTasks.FirstOrDefault(x =>
                                    (x.Assignee == targetUser || x.UserName == targetUser)
                                    && !string.IsNullOrEmpty(x.AvatarUrl));
                                if (existing != null) newUrl = existing.AvatarUrl;
                            }

                            // 変更があった場合、または有効なURLが見つかった場合にセット
                            // (担当者を外してWeb作成者に戻った場合などは newUrl が空になるが、それで正解なのでセットする)
                            if (assigneeChanged || !string.IsNullOrEmpty(newUrl))
                            {
                                t.AvatarUrl = newUrl;
                            }
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

            app.MapPost("/api/batch/channel", (BatchChannelRequest req) => {
                lock (Globals.Lock)
                {
                    ulong? newChannelId = DiscordBot.Instance?.ResolveChannelId(req.GuildName, req.ChannelName);
                    if (newChannelId == null) return Results.BadRequest(new { message = "指定されたサーバーまたはチャンネルが見つかりません。" });
                    var targets = Globals.AllTasks.Where(t => t.UserId == req.UserId && !t.IsForgotten);
                    foreach (var t in targets) { t.GuildName = req.GuildName; t.ChannelName = req.ChannelName; t.ChannelId = newChannelId.Value; }
                    Globals.SaveData();
                }
                return Results.Ok();
            });

            app.MapPost("/api/archive/cleanup", (CleanupRequest req) => { lock (Globals.Lock) { if (req.TargetUserNames != null && req.TargetUserNames.Count > 0) Globals.AllTasks.RemoveAll(t => t.IsForgotten && req.TargetUserNames.Contains(t.UserName)); else Globals.AllTasks.RemoveAll(t => t.IsForgotten); Globals.SaveData(); } return Results.Ok(); });
            app.MapPost("/api/done", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.CompletedAt = t.CompletedAt == null ? Globals.GetJstNow() : null; t.IsSnoozed = false; Globals.SaveData(); } } return Results.Ok(); });
            app.MapPost("/api/archive", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = true; Globals.SaveData(); } } return Results.Ok(); });
            app.MapPost("/api/restore", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = false; Globals.SaveData(); } } return Results.Ok(); });
            app.MapPost("/api/delete", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { Globals.AllTasks.Remove(t); Globals.SaveData(); } } return Results.Ok(); });

            var bot = new DiscordBot();
            await bot.StartAsync();

            System.Console.WriteLine($"\n🚀 Dashboard is running at: {Globals.PublicUrl}\n");
            await app.RunAsync();
        }
    }
}
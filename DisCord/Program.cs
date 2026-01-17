using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TToDo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<DiscordBot>();
builder.Services.AddHostedService<BotBackgroundService>();

// Token.json のパス設定 (警告 CS8602 の修正)
var parentDir = Directory.GetParent(builder.Environment.ContentRootPath);
if (parentDir != null)
{
    var tokenPath = Path.Combine(parentDir.FullName, "Token.json");
    if (File.Exists(tokenPath))
    {
        builder.Configuration.AddJsonFile(tokenPath, optional: true, reloadOnChange: true);
    }
}

var app = builder.Build();

// Globalsの設定とデータ読み込み
Globals.LoadConfiguration(app.Configuration);
Globals.LoadData();

// 環境に応じたトークンの上書き
string? tokenFromFile = null;
if (app.Environment.IsDevelopment())
{
    tokenFromFile = app.Configuration["TToDo_Dev"];
}
else
{
    tokenFromFile = app.Configuration["TToDo"];
}

if (!string.IsNullOrEmpty(tokenFromFile))
{
    Globals.BotToken = tokenFromFile;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// デフォルトファイル(index.html)を有効化
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

// --- Web API Endpoints ---

// 1. タスク一覧取得
app.MapGet("/api/tasks", () =>
{
    lock (Globals.Lock)
    {
        return Globals.AllTasks.OrderByDescending(t => t.Priority).ToList();
    }
});

// 2. 設定一覧取得
app.MapGet("/api/config", () =>
{
    lock (Globals.Lock)
    {
        return Globals.Configs;
    }
});

// 3. タスク新規作成
app.MapPost("/api/create", (TaskItem task) =>
{
    lock (Globals.Lock)
    {
        task.Id = Guid.NewGuid().ToString();
        // Models.cs で定義した CreatedAt を使用
        task.CreatedAt = Globals.GetJstNow();
        Globals.AllTasks.Add(task);
        Globals.SaveData();
    }
    return Results.Ok(task);
});

// 4. タスク更新
app.MapPost("/api/update", (TaskItem task) =>
{
    lock (Globals.Lock)
    {
        var existing = Globals.AllTasks.FirstOrDefault(t => t.Id == task.Id);
        if (existing != null)
        {
            existing.Content = task.Content;
            existing.Priority = task.Priority;
            existing.Difficulty = task.Difficulty;
            existing.Tags = task.Tags;
            existing.Assignee = task.Assignee;
            existing.GuildName = task.GuildName;
            existing.ChannelName = task.ChannelName;
            existing.StartDate = task.StartDate;
            existing.DueDate = task.DueDate;
            existing.TimeMode = task.TimeMode;
            Globals.SaveData();
        }
    }
    return Results.Ok();
});

// 5. タスク完了
app.MapPost("/api/done", (TaskItem task) =>
{
    lock (Globals.Lock)
    {
        var existing = Globals.AllTasks.FirstOrDefault(t => t.Id == task.Id);
        if (existing != null)
        {
            existing.CompletedAt = task.CompletedAt ?? Globals.GetJstNow();
            existing.IsSnoozed = false;
            Globals.SaveData();
        }
    }
    return Results.Ok();
});

// 6. タスク復帰
app.MapPost("/api/restore", (TaskItem task) =>
{
    lock (Globals.Lock)
    {
        var existing = Globals.AllTasks.FirstOrDefault(t => t.Id == task.Id);
        if (existing != null)
        {
            existing.CompletedAt = null;
            existing.IsForgotten = false;
            Globals.SaveData();
        }
    }
    return Results.Ok();
});

// 7. アーカイブ
app.MapPost("/api/archive", (TaskItem task) =>
{
    lock (Globals.Lock)
    {
        var existing = Globals.AllTasks.FirstOrDefault(t => t.Id == task.Id);
        if (existing != null)
        {
            existing.IsForgotten = true;
            Globals.SaveData();
        }
    }
    return Results.Ok();
});

// 8. 削除
app.MapPost("/api/delete", (TaskItem task) =>
{
    lock (Globals.Lock)
    {
        var existing = Globals.AllTasks.FirstOrDefault(t => t.Id == task.Id);
        if (existing != null)
        {
            Globals.AllTasks.Remove(existing);
            Globals.SaveData();
        }
    }
    return Results.Ok();
});

// 9. 設定保存
app.MapPost("/api/config", (UserConfig cfg) =>
{
    lock (Globals.Lock)
    {
        var existing = Globals.Configs.FirstOrDefault(c => c.UserId == cfg.UserId);
        if (existing != null)
        {
            existing.UserName = cfg.UserName;
            existing.ReportTime = cfg.ReportTime;
            existing.TargetGuild = cfg.TargetGuild;
            existing.TargetChannel = cfg.TargetChannel;
        }
        else
        {
            Globals.Configs.Add(cfg);
        }
        Globals.SaveData();
    }
    return Results.Ok();
});

// 10. 送信先一括変更 (Models.csのクラスを使用)
app.MapPost("/api/batch/channel", (BatchChannelRequest req) =>
{
    if (req == null) return Results.BadRequest();

    lock (Globals.Lock)
    {
        var targets = Globals.AllTasks.Where(t => t.UserId == req.UserId && !t.IsForgotten).ToList();
        foreach (var t in targets)
        {
            t.GuildName = req.GuildName;
            t.ChannelName = req.ChannelName;
        }
        Globals.SaveData();
    }
    return Results.Ok();
});

// 11. アーカイブ掃除
app.MapPost("/api/archive/cleanup", (CleanupRequest req) =>
{
    lock (Globals.Lock)
    {
        if (req.TargetUserNames != null && req.TargetUserNames.Count > 0)
        {
            Globals.AllTasks.RemoveAll(t => t.IsForgotten && req.TargetUserNames.Contains(t.Assignee));
        }
        else
        {
            Globals.AllTasks.RemoveAll(t => t.IsForgotten);
        }
        Globals.SaveData();
    }
    return Results.Ok();
});

// 12. 日報手動送信
app.MapPost("/api/report/send", async (ReportRequest req) =>
{
    if (DiscordBot.Instance == null) return Results.StatusCode(500);

    bool success = await DiscordBot.Instance.SendManualReport(req);
    return success ? Results.Ok() : Results.BadRequest();
});

app.Run();
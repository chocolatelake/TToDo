using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TToDo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<DiscordBot>();
builder.Services.AddHostedService<BotBackgroundService>();

// --- 設定ファイルの読み込み ---
var tokenPathSetting = builder.Configuration["Paths:TokenPath"];
if (!string.IsNullOrEmpty(tokenPathSetting) && File.Exists(tokenPathSetting))
{
    builder.Configuration.AddJsonFile(tokenPathSetting, optional: true, reloadOnChange: true);
}

var app = builder.Build();

Globals.LoadConfiguration(app.Configuration);
Globals.LoadData();

// --- トークンの設定 ---
string? tokenFromFile = app.Environment.IsDevelopment() ? app.Configuration["TToDo_Dev"] : app.Configuration["TToDo"];
if (!string.IsNullOrEmpty(tokenFromFile) && tokenFromFile != "あなたのトークン")
{
    Globals.BotToken = tokenFromFile;
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

// ==========================================
//        完全版APIリスト
// ==========================================

// 1. タスク一覧取得
app.MapGet("/api/tasks", () =>
{
    lock (Globals.Lock)
    {
        return Globals.AllTasks.Where(t => t.CompletedAt == null && !t.IsForgotten).OrderByDescending(t => t.Priority).ToList();
    }
});

// 2. 設定取得（★これがLoading解除に必須！）
app.MapGet("/api/config", () =>
{
    lock (Globals.Lock)
    {
        return Globals.Configs;
    }
});

// 3. タスク作成
app.MapPost("/api/create", (TaskItem newItem) =>
{
    lock (Globals.Lock)
    {
        newItem.Id = Guid.NewGuid().ToString("N");
        newItem.CreatedAt = Globals.GetJstNow();
        Globals.AllTasks.Add(newItem);
        Globals.SaveData();
    }
    return Results.Ok();
});

// 4. 更新
app.MapPost("/api/update", (TaskItem updated) =>
{
    lock (Globals.Lock)
    {
        var t = Globals.AllTasks.FirstOrDefault(x => x.Id == updated.Id);
        if (t != null)
        {
            t.Content = updated.Content; t.Assignee = updated.Assignee;
            t.GuildName = updated.GuildName; t.ChannelName = updated.ChannelName;
            t.Priority = updated.Priority; t.Tags = updated.Tags;
            t.StartDate = updated.StartDate; t.DueDate = updated.DueDate; t.TimeMode = updated.TimeMode;
            Globals.SaveData();
        }
    }
    return Results.Ok();
});

// 5. 完了・復元・削除などの必須API
app.MapPost("/api/done", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.CompletedAt = item.CompletedAt; Globals.SaveData(); } } return Results.Ok(); });
app.MapPost("/api/restore", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.CompletedAt = null; t.IsForgotten = false; Globals.SaveData(); } } return Results.Ok(); });
app.MapPost("/api/archive", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = true; Globals.SaveData(); } } return Results.Ok(); });
app.MapPost("/api/delete", (TaskItem item) => { lock (Globals.Lock) { var t = Globals.AllTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { Globals.AllTasks.Remove(t); Globals.SaveData(); } } return Results.Ok(); });

app.Run();
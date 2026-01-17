using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Linq;
using TToDo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<DiscordBot>();
builder.Services.AddHostedService<BotBackgroundService>();

// --- ★修正: 設定ファイルから絶対パスを読み込んで Token.json を追加 ---
var tokenPathSetting = builder.Configuration["Paths:TokenPath"];
if (!string.IsNullOrEmpty(tokenPathSetting))
{
    // 設定ファイルに書かれたパス（絶対パス）をそのまま使用
    if (File.Exists(tokenPathSetting))
    {
        Console.WriteLine($"[Setup] Loading Token from: {tokenPathSetting}");
        builder.Configuration.AddJsonFile(tokenPathSetting, optional: true, reloadOnChange: true);
    }
    else
    {
        Console.WriteLine($"[Setup] WARNING: Token file not found at: {tokenPathSetting}");
    }
}
else
{
    Console.WriteLine("[Setup] WARNING: 'Paths:TokenPath' is not set in appsettings.json");
}
// -------------------------------------------------------------------

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

if (!string.IsNullOrEmpty(tokenFromFile) && tokenFromFile != "あなたのトークン")
{
    Globals.BotToken = tokenFromFile;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

// --- Web API Endpoints ---
app.MapGet("/api/tasks", () =>
{
    lock (Globals.Lock)
    {
        var tasks = Globals.AllTasks
            .Where(t => t.CompletedAt == null && !t.IsForgotten)
            .OrderByDescending(t => t.Priority)
            .ToList();
        return tasks;
    }
});

// 必要なAPIがあればここに追加...

app.Run();
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Linq; // 追加
using TToDo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<DiscordBot>();
builder.Services.AddHostedService<BotBackgroundService>();

// Token.json のパス設定
var tokenPath = Path.Combine(Directory.GetParent(builder.Environment.ContentRootPath).FullName, "Token.json");
if (File.Exists(tokenPath))
{
    builder.Configuration.AddJsonFile(tokenPath, optional: true, reloadOnChange: true);
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
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

// --- ★ここから追加: Web APIのエンドポイント ---

// タスク一覧取得 (GET /api/tasks)
app.MapGet("/api/tasks", () =>
{
    lock (Globals.Lock)
    {
        // 完了していない、かつアーカイブされていないタスクを返す
        var tasks = Globals.AllTasks
            .Where(t => t.CompletedAt == null && !t.IsForgotten)
            .OrderByDescending(t => t.Priority)
            .ToList();
        return tasks;
    }
});

// タスク更新などのAPIが必要であればここに追加...
// -------------------------------------------

app.Run();

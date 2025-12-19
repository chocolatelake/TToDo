using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

// --- Data Models ---
public class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ulong ChannelId { get; set; }
    public ulong UserId { get; set; }
    public string Content { get; set; } = "";
    public int Priority { get; set; } = -1;
    public int Difficulty { get; set; } = -1;
    public List<string> Tags { get; set; } = new List<string>();
    public bool IsSnoozed { get; set; }
    public bool IsForgotten { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class UserConfig
{
    public ulong UserId { get; set; }
    public string ReportTime { get; set; } = "00:00";
}

class Program
{
    // ★★★ ここにトークンを設定してください ★★★
#if DEBUG
    private const string BotToken = "MTQ1MTYwNTE3NDI0OTMyNDU4NA.GXbNFG.4WuFDWUy2gmBA9b3P50sYAMS4WYQsS7yrxoyVk";
#else
    private const string BotToken = "ここに_いつものTToDo_のトークン";
#endif

    // 設定
    private const string DbFileName = "tasks.json";
    private const string ConfigFileName = "config.json";
    private const string WebUrl = "http://*:5000";

    // グローバル変数
    private static DiscordSocketClient _client = null!;
    private static List<TaskItem> _allTasks = new List<TaskItem>();
    private static List<UserConfig> _configs = new List<UserConfig>();
    private static readonly object _lock = new object();
    private static readonly TimeZoneInfo JstZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private static readonly List<string> DefaultPrefixes = new List<string> { " ", "　", "\t", "→", "：", ":", "・", "※", ">", "-", "+", "*", "■", "□", "●", "○" };
    private static readonly List<string> TagPrefixes = new List<string> { "#", "＃" };

    static async Task Main(string[] args)
    {
        LoadData();

        // 1. Webサーバー構築
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(WebUrl);
        var app = builder.Build();

        // --- Web API ---
        app.MapGet("/", () => Results.Content(HtmlSource, "text/html"));
        app.MapGet("/api/tasks", () => { lock (_lock) { return Results.Json(_allTasks.Where(t => !t.IsForgotten)); } });
        app.MapPost("/api/update", async (TaskItem item) => {
            lock (_lock)
            {
                var t = _allTasks.FirstOrDefault(x => x.Id == item.Id);
                if (t != null) { t.Content = item.Content; t.Priority = item.Priority; t.Difficulty = item.Difficulty; t.Tags = item.Tags; SaveData(); }
            }
            return Results.Ok();
        });
        app.MapPost("/api/done", async (TaskItem item) => {
            lock (_lock)
            {
                var t = _allTasks.FirstOrDefault(x => x.Id == item.Id);
                if (t != null) { t.CompletedAt = t.CompletedAt == null ? GetJstNow() : null; t.IsSnoozed = false; SaveData(); }
            }
            return Results.Ok();
        });
        app.MapPost("/api/archive", async (TaskItem item) => {
            lock (_lock) { var t = _allTasks.FirstOrDefault(x => x.Id == item.Id); if (t != null) { t.IsForgotten = true; SaveData(); } }
            return Results.Ok();
        });

        // 2. Discord Bot起動
        var socketConfig = new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent };
        _client = new DiscordSocketClient(socketConfig);
        _client.Log += msg => { Console.WriteLine($"[Bot] {msg}"); return Task.CompletedTask; };
        _client.MessageReceived += MessageReceivedAsync;
        _client.SelectMenuExecuted += SelectMenuHandler; // ドロップダウン操作用

        await _client.LoginAsync(TokenType.Bot, BotToken);
        await _client.StartAsync();

        _ = Task.Run(() => AutoReportLoop());

        Console.WriteLine($"\n🚀 Dashboard is running at: {WebUrl.Replace("*", "localhost")}\n");
        await app.RunAsync();
    }

    // --- HTML Source ---
    private const string HtmlSource = @"
<!DOCTYPE html>
<html lang='ja'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>TToDo Web Controller</title>
    <script src='https://unpkg.com/vue@3/dist/vue.global.js'></script>
    <link href='https://fonts.googleapis.com/css2?family=Inter:wght@400;600;800&display=swap' rel='stylesheet'>
    <style>
        :root { --bg: #f2f3f5; --card: #ffffff; --primary: #5865F2; --text: #2c2f33; --danger: #ED4245; --success: #57F287; }
        body { font-family: 'Inter', sans-serif; background: var(--bg); color: var(--text); margin: 0; padding: 20px; }
        .container { max-width: 900px; margin: 0 auto; }
        h1 { text-align: center; color: var(--primary); margin-bottom: 20px; font-weight: 800; }
        .controls { display: flex; gap: 10px; margin-bottom: 20px; justify-content: flex-end; }
        .btn { border: none; padding: 8px 16px; border-radius: 6px; cursor: pointer; font-weight: 600; transition: 0.2s; }
        .btn-outline { background: transparent; border: 1px solid #ccc; color: #666; }
        .btn-primary { background: var(--primary); color: white; }
        .card { background: var(--card); border-radius: 8px; padding: 16px; margin-bottom: 12px; box-shadow: 0 2px 5px rgba(0,0,0,0.05); display: flex; align-items: flex-start; gap: 12px; transition: 0.2s; border-left: 5px solid transparent; }
        .card:hover { transform: translateY(-2px); box-shadow: 0 5px 15px rgba(0,0,0,0.1); }
        .card.done { opacity: 0.6; background: #fafafa; }
        .p-1 { border-left-color: #ED4245; } .p-2 { border-left-color: #E67E22; } .p-3 { border-left-color: #F1C40F; } .p-4 { border-left-color: #95a5a6; } .p-0 { border-left-color: #bdc3c7; }
        .content-area { flex-grow: 1; cursor: pointer; }
        .task-text { font-size: 1.1em; font-weight: 600; white-space: pre-wrap; margin-bottom: 6px; }
        .tags { display: flex; gap: 6px; flex-wrap: wrap; }
        .tag { background: #e3e5e8; font-size: 0.75em; padding: 2px 8px; border-radius: 12px; color: #4f545c; }
        .check-btn { width: 40px; height: 40px; border-radius: 50%; border: 2px solid #ddd; background: transparent; cursor: pointer; font-size: 1.2em; display: flex; align-items: center; justify-content: center; }
        .check-btn.checked { background: var(--success); border-color: var(--success); color: white; }
        .archive-btn { background: transparent; border: none; color: #999; cursor: pointer; padding: 5px; }
        .archive-btn:hover { color: var(--danger); }
        .modal-overlay { position: fixed; top: 0; left: 0; width: 100%; height: 100%; background: rgba(0,0,0,0.5); display: flex; align-items: center; justify-content: center; z-index: 1000; }
        .modal { background: white; padding: 25px; border-radius: 12px; width: 90%; max-width: 500px; box-shadow: 0 10px 30px rgba(0,0,0,0.2); }
        .form-group { margin-bottom: 15px; }
        .form-control { width: 100%; padding: 10px; border: 1px solid #ddd; border-radius: 6px; font-family: inherit; box-sizing: border-box; }
        .row { display: flex; gap: 10px; }
        .prio-select { flex: 1; padding: 10px; border: 1px solid #ddd; border-radius: 6px; cursor: pointer; text-align: center; }
        .prio-select.selected { background: var(--primary); color: white; border-color: var(--primary); }
    </style>
</head>
<body>
    <div id='app' class='container'>
        <h1>TToDo Controller</h1>
        <div class='controls'><button class='btn btn-outline' @click='toggleShowDone'>{{ showDone ? '完了を隠す' : '完了を表示' }}</button></div>
        <div v-if='loading'>Loading...</div>
        <div v-else>
            <div v-if='filteredTasks.length === 0' style='text-align:center; margin-top:50px; color:#aaa;'>タスクはありません。Discordで入力してください。</div>
            <div v-for='task in filteredTasks' :key='task.id' class='card' :class='[getPrioClass(task), {done: task.completedAt}]'>
                <button class='check-btn' :class='{checked: task.completedAt}' @click.stop='toggleDone(task)'>{{ task.completedAt ? '✔' : '' }}</button>
                <div class='content-area' @click='openEdit(task)'>
                    <div class='task-text'>{{ task.content }}</div>
                    <div class='tags'><span v-for='tag in task.tags' class='tag'>#{{ tag }}</span><span v-if='task.priority > -1' class='tag' style='background:#f0f0f0'>{{ getPrioLabel(task) }}</span></div>
                </div>
                <button class='archive-btn' title='アーカイブ' @click.stop='archiveTask(task)'>🗑️</button>
            </div>
        </div>
        <div v-if='editingTask' class='modal-overlay' @click.self='editingTask = null'>
            <div class='modal'>
                <h2 style='margin-top:0'>タスク編集</h2>
                <div class='form-group'><label>内容</label><textarea class='form-control' rows='3' v-model='editForm.content'></textarea></div>
                <div class='form-group'><label>タグ (カンマ区切り)</label><input type='text' class='form-control' v-model='editForm.tagsStr'></div>
                <div class='form-group'><div class='row'>
                    <div class='prio-select' :class='{selected: editForm.priority===1}' @click='editForm.priority=1'>高</div>
                    <div class='prio-select' :class='{selected: editForm.priority===0}' @click='editForm.priority=0'>低</div>
                    <div class='prio-select' :class='{selected: editForm.priority===-1}' @click='editForm.priority=-1'>なし</div>
                </div></div>
                <div class='row' style='margin-top:20px'><button class='btn btn-outline' style='flex:1' @click='editingTask = null'>キャンセル</button><button class='btn btn-primary' style='flex:1' @click='saveEdit'>保存</button></div>
            </div>
        </div>
    </div>
    <script>
        const { createApp } = Vue
        createApp({
            data() { return { tasks: [], loading: true, showDone: false, editingTask: null, editForm: {} } },
            computed: {
                filteredTasks() {
                    let list = this.tasks;
                    if (!this.showDone) list = list.filter(t => !t.completedAt);
                    return list.sort((a, b) => { if(a.completedAt && !b.completedAt) return 1; if(!a.completedAt && b.completedAt) return -1; return this.getScore(b) - this.getScore(a); });
                }
            },
            mounted() { this.fetchTasks(); setInterval(this.fetchTasks, 3000); },
            methods: {
                async fetchTasks() { if(this.editingTask) return; try { const res = await fetch('/api/tasks'); this.tasks = await res.json(); this.loading = false; } catch(e) {} },
                async toggleDone(task) { task.completedAt = task.completedAt ? null : new Date().toISOString(); await fetch('/api/done', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(task) }); this.fetchTasks(); },
                async archiveTask(task) { if(!confirm('アーカイブしますか？')) return; await fetch('/api/archive', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(task) }); this.fetchTasks(); },
                openEdit(task) { this.editingTask = task; this.editForm = { ...task, tagsStr: task.tags.join(', ') }; },
                async saveEdit() { const payload = { ...this.editForm, tags: this.editForm.tagsStr.split(',').map(s=>s.trim()).filter(s=>s) }; await fetch('/api/update', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(payload) }); this.editingTask = null; this.fetchTasks(); },
                getScore(t) { if (t.priority === 1 && t.difficulty === 1) return 10; if (t.priority === 1) return 9; if (t.priority === -1) return 5; if (t.priority === 0 && t.difficulty === 1) return 2; return 1; },
                getPrioLabel(t) { return t.priority === 1 ? '重要' : (t.priority === 0 ? '低' : ''); },
                getPrioClass(t) { return t.priority === 1 ? (t.difficulty===1?'p-1':'p-2') : (t.priority===0?(t.difficulty===1?'p-3':'p-4'):'p-0'); }
            }
        }).mount('#app')
    </script>
</body>
</html>
";

    // --- Helper Methods ---
    private static DateTime GetJstNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, JstZone);
    private static void SaveData() { lock (_lock) { File.WriteAllText(DbFileName, JsonSerializer.Serialize(_allTasks)); File.WriteAllText(ConfigFileName, JsonSerializer.Serialize(_configs)); } }
    private static void LoadData()
    {
        lock (_lock)
        {
            if (File.Exists(DbFileName)) try { _allTasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(DbFileName)) ?? new(); } catch { }
            if (File.Exists(ConfigFileName)) try { _configs = JsonSerializer.Deserialize<List<UserConfig>>(File.ReadAllText(ConfigFileName)) ?? new(); } catch { }
        }
    }

    // --- Discord Handlers ---
    private static async Task MessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot || string.IsNullOrWhiteSpace(message.Content)) return;
        string content = message.Content.Trim();
        if (!content.StartsWith("!ttodo")) return;

        string arg1 = content.Replace("!ttodo", "").Trim();
        if (arg1.StartsWith("list", StringComparison.OrdinalIgnoreCase)) await ShowCompactList(message.Channel, message.Author, arg1);
        else if (arg1.StartsWith("report", StringComparison.OrdinalIgnoreCase)) await ShowReport(message.Channel, message.Author, arg1);
        else if (arg1.StartsWith("close", StringComparison.OrdinalIgnoreCase)) await RunDailyClose(message.Author.Id, message.Channel);
        else if (arg1.StartsWith("trash", StringComparison.OrdinalIgnoreCase)) await ShowTrashList(message.Channel, message.Author);
        else if (!string.IsNullOrWhiteSpace(arg1)) await AddNewTasks(message.Channel, message.Author, arg1);
    }

    private static async Task AddNewTasks(ISocketMessageChannel channel, SocketUser user, string rawText)
    {
        var lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var addedTasks = new List<TaskItem>();
        TaskItem? pendingTask = null;
        List<string> currentTags = new List<string>();

        foreach (var line in lines)
        {
            string trimLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimLine))
            {
                if (pendingTask != null) { lock (_lock) _allTasks.Add(pendingTask); addedTasks.Add(pendingTask); pendingTask = null; }
                currentTags.Clear(); continue;
            }
            if (trimLine.StartsWith("#") || trimLine.StartsWith("＃")) { currentTags = new List<string> { trimLine.Substring(1).Trim() }; continue; }
            if ((line.StartsWith(" ") || line.StartsWith("　")) && pendingTask != null) { pendingTask.Content += "\n" + trimLine; }
            else
            {
                if (pendingTask != null) { lock (_lock) _allTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
                pendingTask = new TaskItem { ChannelId = channel.Id, UserId = user.Id, Content = trimLine, Tags = new List<string>(currentTags) };
            }
        }
        if (pendingTask != null) { lock (_lock) _allTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
        SaveData();
        if (addedTasks.Count > 0) await ShowCompactList(channel, user, "");
    }

    // ★ 修正点: 未完了タスクのみを数えてテキスト化する
    private static string BuildListText(List<TaskItem> tasks)
    {
        // 完了していないタスクのみ抽出
        var incompleteTasks = tasks.Where(t => t.CompletedAt == null).ToList();

        if (incompleteTasks.Count == 0) return "🎉 タスクはありません！";

        var sb = new StringBuilder();
        // 件数を「未完了のみ」に修正
        sb.AppendLine($"📂 **タスク一覧 ({incompleteTasks.Count}件):**");
        foreach (var task in incompleteTasks)
        {
            sb.AppendLine($"・{task.Content}");
        }
        return sb.ToString();
    }

    private static async Task RefreshListAsync(ISocketMessageChannel channel, ulong userId, SocketMessageComponent component = null)
    {
        var today = GetJstNow().Date;
        List<TaskItem> visibleTasks;
        // リストには今日完了したものも含む（プルダウンでundoするため）
        lock (_lock) visibleTasks = _allTasks.Where(t => !t.IsForgotten && t.UserId == userId && (t.CompletedAt == null || t.CompletedAt >= today)).ToList();

        // メッセージ生成（未完了のみ）
        string content = BuildListText(visibleTasks);
        // コンポーネント生成（完了含む全件）
        var components = BuildComponents(visibleTasks);

        if (component != null)
        {
            await component.UpdateAsync(x => { x.Content = content; x.Components = components; });
        }
        else
        {
            await channel.SendMessageAsync(content, components: components);
        }
    }

    private static MessageComponent BuildComponents(List<TaskItem> tasks)
    {
        if (tasks.Count == 0) return null;

        var doneMenu = new SelectMenuBuilder().WithCustomId("sel_done").WithPlaceholder("▼ 完了にする (Complete)").WithMinValues(1).WithMaxValues(1);
        var archiveMenu = new SelectMenuBuilder().WithCustomId("sel_archive").WithPlaceholder("▼ アーカイブする (Archive)").WithMinValues(1).WithMaxValues(1);

        bool hasItems = false;
        foreach (var task in tasks.Take(25))
        {
            hasItems = true;
            string label = task.Content.Replace("\n", " ");
            if (label.Length > 50) label = label.Substring(0, 45) + "...";
            string state = task.CompletedAt != null ? "✅ " : "";

            doneMenu.AddOption($"{state}{label}", task.Id);
            archiveMenu.AddOption($"{state}{label}", task.Id);
        }

        if (!hasItems) return null;

        return new ComponentBuilder().WithSelectMenu(doneMenu, row: 0).WithSelectMenu(archiveMenu, row: 1).Build();
    }

    private static async Task ShowCompactList(ISocketMessageChannel channel, SocketUser user, string args)
    {
        await RefreshListAsync(channel, user.Id);
    }

    private static async Task SelectMenuHandler(SocketMessageComponent component)
    {
        try
        {
            string id = component.Data.CustomId;
            string val = component.Data.Values.First();
            TaskItem? task; lock (_lock) task = _allTasks.FirstOrDefault(t => t.Id == val);

            if (task != null)
            {
                if (id == "sel_done")
                {
                    lock (_lock)
                    {
                        if (task.CompletedAt == null) task.CompletedAt = GetJstNow();
                        else task.CompletedAt = null;
                        SaveData();
                    }
                }
                else if (id == "sel_archive")
                {
                    lock (_lock) { task.IsForgotten = true; SaveData(); }
                }
            }
            await RefreshListAsync(component.Channel, component.User.Id, component);
        }
        catch { }
    }

    private static async Task ShowTrashList(ISocketMessageChannel c, SocketUser u)
    {
        List<TaskItem> l; lock (_lock) l = _allTasks.Where(t => t.UserId == u.Id && t.IsForgotten).ToList();
        if (l.Count == 0) { await c.SendMessageAsync("🗑️ 空です"); return; }
        var menu = new SelectMenuBuilder().WithCustomId("sel_restore").WithPlaceholder("復活させる...").WithMinValues(1).WithMaxValues(1);
        foreach (var t in l.Take(25)) menu.AddOption(t.Content.Length > 50 ? t.Content.Substring(0, 47) + "..." : t.Content, t.Id);
        await c.SendMessageAsync($"🗑️ **ゴミ箱 ({l.Count}件)**", components: new ComponentBuilder().WithSelectMenu(menu).Build());
    }

    private static async Task RunDailyClose(ulong userId, ISocketMessageChannel feedbackChannel = null)
    {
        List<TaskItem> doneTasks; lock (_lock) doneTasks = _allTasks.Where(t => t.UserId == userId && t.CompletedAt != null).ToList();
        if (doneTasks.Count == 0) { if (feedbackChannel != null) await feedbackChannel.SendMessageAsync("本日の完了タスクはありません。"); return; }
        var user = _client.GetUser(userId); string userName = user?.Username ?? "User";
        var tasksToRemove = new List<TaskItem>();
        var channelGroups = doneTasks.GroupBy(t => t.ChannelId);
        foreach (var group in channelGroups)
        {
            ulong chId = group.Key; var targetChannel = _client.GetChannel(chId) as ISocketMessageChannel;
            if (targetChannel == null) try { targetChannel = await _client.GetChannelAsync(chId) as ISocketMessageChannel; } catch { }
            if (targetChannel == null) continue;
            var sb = new StringBuilder(); sb.AppendLine($"🌅 **Daily Report: {userName}** ({GetJstNow():yyyy/MM/dd})");
            sb.AppendLine($"**{group.Count()}件** 完了！"); sb.AppendLine("```");
            foreach (var tGroup in group.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "その他")) { sb.AppendLine($"【{tGroup.Key}】"); foreach (var t in tGroup) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}"); sb.AppendLine(); }
            sb.AppendLine("```"); try { await targetChannel.SendMessageAsync(sb.ToString()); tasksToRemove.AddRange(group); } catch { }
        }
        lock (_lock) { foreach (var t in tasksToRemove) _allTasks.Remove(t); SaveData(); }
    }
    private static async Task ShowReport(ISocketMessageChannel c, SocketUser u, string a) { var d = GetJstNow().Date; List<TaskItem> l; lock (_lock) l = _allTasks.Where(x => x.UserId == u.Id && x.CompletedAt >= d).ToList(); if (l.Count == 0) { await c.SendMessageAsync($"📭 Todayの完了タスクなし"); return; } await c.SendMessageAsync($"📊 **Report:** {l.Count}件完了"); }
    private static async Task AutoReportLoop() { while (true) { try { var now = GetJstNow(); if (now.Second == 0) { string curTime = now.ToString("HH:mm"); List<ulong> users; lock (_lock) users = _allTasks.Select(t => t.UserId).Distinct().ToList(); foreach (var uid in users) { string target = "00:00"; lock (_lock) { var c = _configs.FirstOrDefault(x => x.UserId == uid); if (c != null) target = c.ReportTime; } if (target == curTime) await RunDailyClose(uid); } await Task.Delay(60000); } else await Task.Delay(500); } catch { await Task.Delay(1000); } } }
}
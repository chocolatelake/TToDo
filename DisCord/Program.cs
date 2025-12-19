using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
    public string ReportTime { get; set; } = "00:00"; // HH:mm
}

class Program
{
    private static readonly List<string> DefaultPrefixes = new List<string> { " ", "　", "\t", "→", "：", ":", "・", "※", ">", "-", "+", "*", "■", "□", "●", "○" };
    private static readonly List<string> TagPrefixes = new List<string> { "#", "＃" };

    private DiscordSocketClient _client = null!;
    private static List<TaskItem> _allTasks = new List<TaskItem>();
    private static List<UserConfig> _configs = new List<UserConfig>();
    private const string DbFileName = "tasks.json";
    private const string ConfigFileName = "config.json";

    static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();

    public async Task MainAsync()
    {
        var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent };
        _client = new DiscordSocketClient(config);
        _client.Log += Log;
        _client.MessageReceived += MessageReceivedAsync;
        _client.ButtonExecuted += ButtonHandler;
        _client.ModalSubmitted += ModalHandler;
        _client.SelectMenuExecuted += SelectMenuHandler;

        string token = "MTQ1MTE5Mzg0MDczNTIyNDAxMg.GJzP10.TsRAcmITV_oyZQZajFHpV0361O17t5HV_Ej_aU"; // ★トークンを設定

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        LoadData();

        _ = Task.Run(() => AutoReportLoop());

        await Task.Delay(-1);
    }

    // --- Background Loop ---
    private async Task AutoReportLoop()
    {
        while (true)
        {
            try
            {
                var now = DateTime.Now;
                if (now.Second == 0)
                {
                    string currentTimeStr = now.ToString("HH:mm");
                    var activeUsers = _allTasks.Select(t => t.UserId).Distinct().ToList(); // ToListでコピー

                    foreach (var uid in activeUsers)
                    {
                        var conf = _configs.FirstOrDefault(c => c.UserId == uid);
                        string targetTime = conf?.ReportTime ?? "00:00";

                        if (targetTime == currentTimeStr)
                        {
                            await RunDailyClose(uid);
                        }
                    }
                    await Task.Delay(60000);
                }
                else await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Timer Error: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }

    private async Task MessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot || string.IsNullOrWhiteSpace(message.Content)) return;
        string content = message.Content.Trim();
        if (!content.StartsWith("!ttodo")) return;

        var parts = content.Split(new[] { ' ', '　', '\t', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
        string arg1 = parts.Length > 1 ? parts[1].Trim() : "";

        if (string.Equals(arg1, "list", StringComparison.OrdinalIgnoreCase) || arg1.StartsWith("list ", StringComparison.OrdinalIgnoreCase))
        {
            await ShowCompactList(message.Channel, message.Author, arg1);
            return;
        }
        if (string.Equals(arg1, "report", StringComparison.OrdinalIgnoreCase) || string.Equals(arg1, "today", StringComparison.OrdinalIgnoreCase))
        {
            await ShowReport(message.Channel, message.Author, arg1);
            return;
        }
        if (string.Equals(arg1, "close", StringComparison.OrdinalIgnoreCase))
        {
            await RunDailyClose(message.Author.Id, message.Channel);
            return;
        }
        if (string.Equals(arg1, "trash", StringComparison.OrdinalIgnoreCase))
        {
            await ShowTrashList(message.Channel, message.Author);
            return;
        }
        if (arg1.StartsWith("config"))
        {
            await HandleConfig(message.Channel, message.Author, arg1);
            return;
        }
        if (string.Equals(arg1, "export", StringComparison.OrdinalIgnoreCase))
        {
            await ExportData(message.Channel, message.Author);
            return;
        }
        if (arg1.StartsWith("import"))
        {
            await ImportData(message.Channel, content.Substring(content.IndexOf("import") + 6).Trim());
            return;
        }
        if (!string.IsNullOrWhiteSpace(arg1))
        {
            await AddNewTasks(message.Channel, message.Author, arg1);
        }
    }

    // ---------------------------------------------------------
    // 1. Input Logic
    // ---------------------------------------------------------
    private async Task AddNewTasks(ISocketMessageChannel channel, SocketUser user, string rawText)
    {
        var lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var addedTasks = new List<TaskItem>();
        List<string> currentTags = new List<string>();
        TaskItem? pendingTask = null;

        foreach (var line in lines)
        {
            string trimLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimLine))
            {
                if (pendingTask != null) { _allTasks.Add(pendingTask); addedTasks.Add(pendingTask); pendingTask = null; }
                currentTags.Clear();
                continue;
            }
            bool isTagLine = false;
            foreach (var tp in TagPrefixes)
            {
                if (trimLine.StartsWith(tp))
                {
                    if (pendingTask != null) { _allTasks.Add(pendingTask); addedTasks.Add(pendingTask); pendingTask = null; }
                    string tagName = trimLine.Substring(tp.Length).Trim();
                    if (!string.IsNullOrEmpty(tagName)) currentTags = new List<string> { tagName };
                    isTagLine = true; break;
                }
            }
            if (isTagLine) continue;

            bool isContinuation = false;
            foreach (var p in DefaultPrefixes) { if (line.StartsWith(p)) { isContinuation = true; break; } }

            if (isContinuation && pendingTask != null) pendingTask.Content += "\n" + trimLine;
            else
            {
                if (pendingTask != null) { _allTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
                int prio = -1; int diff = -1; string content = trimLine;
                if (content.StartsWith("!!") || content.StartsWith("！！")) { prio = 1; diff = 1; content = content.Substring(2); }
                else if (content.StartsWith("??") || content.StartsWith("？？")) { prio = 0; diff = 0; content = content.Substring(2); }
                else if (content.StartsWith("!") || content.StartsWith("！")) { prio = 1; diff = 0; content = content.Substring(1); }
                else if (content.StartsWith("?") || content.StartsWith("？")) { prio = 0; diff = 1; content = content.Substring(1); }
                content = content.Trim();
                var lineTags = new List<string>(currentTags);
                if (content.StartsWith("[") || content.StartsWith("【"))
                {
                    int endIdx = -1;
                    if (content.Contains("]")) endIdx = content.IndexOf("]"); else if (content.Contains("】")) endIdx = content.IndexOf("】");
                    if (endIdx > 0) { lineTags.Add(content.Substring(1, endIdx - 1)); content = content.Substring(endIdx + 1).Trim(); }
                }
                if (!string.IsNullOrWhiteSpace(content))
                {
                    pendingTask = new TaskItem { ChannelId = channel.Id, UserId = user.Id, Content = content, Priority = prio, Difficulty = diff, Tags = lineTags.Distinct().ToList() };
                }
            }
        }
        if (pendingTask != null) { _allTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
        SaveData();

        if (addedTasks.Count > 0) await ShowCompactList(channel, user, "");
    }

    // ---------------------------------------------------------
    // 2. Compact List
    // ---------------------------------------------------------
    private async Task ShowCompactList(ISocketMessageChannel channel, SocketUser user, string args)
    {
        var today = DateTime.Today;
        IEnumerable<TaskItem> query = _allTasks.Where(t => !t.IsForgotten);
        query = query.Where(t => t.CompletedAt == null || t.CompletedAt >= today);

        bool isMine = args.Contains("mine");
        bool isChannel = args.Contains("channel");

        if (isMine) query = query.Where(t => t.UserId == user.Id);
        else if (isChannel) query = query.Where(t => t.ChannelId == channel.Id);
        else query = query.Where(t => t.UserId == user.Id && t.ChannelId == channel.Id);

        var visibleTasks = query.ToList();
        if (visibleTasks.Count == 0) { await channel.SendMessageAsync("🎉 タスクはありません！"); return; }

        var sb = new StringBuilder();
        sb.AppendLine($"📂 **タスク一覧 ({visibleTasks.Count}件):**");

        var grouped = visibleTasks.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "📂 未分類").OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            sb.AppendLine($"\n**{group.Key}**");
            foreach (var task in group.OrderByDescending(t => GetSortScore(t)))
            {
                string label = GetPriorityLabel(task.Priority, task.Difficulty);
                string state = "";
                if (task.CompletedAt != null) state = "✅ ";
                else if (task.IsSnoozed) state = "💤 ";

                string display = task.Content.Split('\n')[0];
                if (display.Length > 20) display = display.Substring(0, 20) + "...";
                sb.AppendLine($"`[{label}]` {state}{display}");
            }
        }

        var menuBuilder = new SelectMenuBuilder()
            .WithCustomId("main_task_selector")
            .WithPlaceholder("▼ 操作するタスクを選択...")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var task in visibleTasks.Take(25))
        {
            string label = GetPriorityLabel(task.Priority, task.Difficulty);
            string contentLabel = task.Content.Replace("\n", " ");
            if (contentLabel.Length > 45) contentLabel = contentLabel.Substring(0, 42) + "...";
            string stateIcon = "";
            if (task.CompletedAt != null) stateIcon = "✅ ";
            else if (task.IsSnoozed) stateIcon = "💤 ";

            string fullLabel = $"[{label}] {stateIcon}{contentLabel}";
            string desc = task.Tags.Count > 0 ? $"[{task.Tags[0]}]" : "未分類";

            menuBuilder.AddOption(fullLabel, task.Id, desc);
        }

        var builder = new ComponentBuilder()
            .WithSelectMenu(menuBuilder)
            .WithButton("優先度", "start_sort_p", ButtonStyle.Secondary, null, row: 1)
            .WithButton("タグ", "start_sort_t", ButtonStyle.Secondary, null, row: 1);

        await channel.SendMessageAsync(sb.ToString(), components: builder.Build());
    }

    // ---------------------------------------------------------
    // 3. Trash List
    // ---------------------------------------------------------
    private async Task ShowTrashList(ISocketMessageChannel channel, SocketUser user)
    {
        var trashTasks = _allTasks.Where(t => t.UserId == user.Id && t.IsForgotten).ToList();

        if (trashTasks.Count == 0)
        {
            await channel.SendMessageAsync("🗑️ ゴミ箱は空です。");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"🗑️ **ゴミ箱 (忘却リスト): {trashTasks.Count}件**");
        sb.AppendLine("※ここから「復活」させるか、完全に「抹消」できます。");

        var menuBuilder = new SelectMenuBuilder()
            .WithCustomId("trash_task_selector")
            .WithPlaceholder("▼ 操作するタスクを選択...")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var task in trashTasks.Take(25))
        {
            string contentLabel = task.Content.Replace("\n", " ");
            if (contentLabel.Length > 50) contentLabel = contentLabel.Substring(0, 47) + "...";
            menuBuilder.AddOption(contentLabel, task.Id, "忘却中");
            sb.AppendLine($"・{contentLabel}");
        }

        var builder = new ComponentBuilder().WithSelectMenu(menuBuilder);
        await channel.SendMessageAsync(sb.ToString(), components: builder.Build());
    }

    // ---------------------------------------------------------
    // 4. Handlers
    // ---------------------------------------------------------
    private async Task SelectMenuHandler(SocketMessageComponent component)
    {
        try
        {
            string customId = component.Data.CustomId;
            string value = component.Data.Values.First();
            var task = _allTasks.FirstOrDefault(t => t.Id == value);

            if (customId == "main_task_selector")
            {
                if (task == null) { await component.RespondAsync("エラー: タスクが見つかりません。", ephemeral: true); return; }
                await ShowTaskActionPanel(component, task);
            }
            else if (customId == "trash_task_selector")
            {
                if (task == null) { await component.RespondAsync("既に削除されています。", ephemeral: true); return; }
                string txt = $"🗑️ **ゴミ箱:** {task.Content}";
                var builder = new ComponentBuilder()
                    .WithButton("↩️ 復活", $"recall_{task.Id}", ButtonStyle.Primary)
                    .WithButton("💥 抹消", $"obliterate_{task.Id}", ButtonStyle.Danger);
                await component.RespondAsync(txt, components: builder.Build(), ephemeral: true);
            }
            else if (customId.StartsWith("tag_sel_"))
            {
                string rawArgs = customId.Substring("tag_sel_".Length);
                var parts = rawArgs.Split('_');
                var t = _allTasks.FirstOrDefault(x => x.Id == parts[0]);
                if (t != null)
                {
                    string tag = component.Data.Values.First();
                    t.Tags.Clear(); if (!string.IsNullOrEmpty(tag)) t.Tags.Add(tag);
                    SaveData();
                    string mode = parts.Length > 1 ? parts[1] : "normal";
                    if (mode == "normal") await component.RespondAsync($"✅ タグ: **[{tag}]**", ephemeral: true);
                    else
                    {
                        var next = FindNextSortTarget(component.User.Id, mode);
                        if (next != null) await ShowSortCard(component, next, mode, false);
                        else await component.RespondAsync("🎉 仕分け完了！", ephemeral: true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Menu Error: {ex.Message}");
            // 既に応答済みかもしれないので、可能ならEphemeralで返す
            try { await component.RespondAsync("⚠️ エラーが発生しました。", ephemeral: true); } catch { }
        }
    }

    private async Task ShowTaskActionPanel(SocketMessageComponent component, TaskItem task)
    {
        string label = GetPriorityLabel(task.Priority, task.Difficulty);
        string currentTag = task.Tags.Count > 0 ? task.Tags[0] : "(なし)";
        string status = task.CompletedAt != null ? "(完了済み・日報待ち)" : "";
        string text = $"🛠️ **タスク操作:** {status}\n[{label}] **{task.Content}**\nタグ: {currentTag}";

        var builder = new ComponentBuilder()
            .WithButton(null, $"done_{task.Id}", ButtonStyle.Secondary, new Emoji("✅"))
            .WithButton(null, $"snooze_{task.Id}", ButtonStyle.Secondary, new Emoji("💤"))
            .WithButton(null, $"forget_{task.Id}", ButtonStyle.Secondary, new Emoji("🗑️"))
            .WithButton(null, $"edit_{task.Id}", ButtonStyle.Secondary, new Emoji("⚙️"));

        await component.RespondAsync(text, components: builder.Build(), ephemeral: true);
    }

    private async Task ButtonHandler(SocketMessageComponent component)
    {
        try
        {
            string id = component.Data.CustomId;
            if (id == "start_sort_p") { await StartSortingSession(component, "p"); return; }
            if (id == "start_sort_t") { await StartSortingSession(component, "t"); return; }
            if (id.StartsWith("set_")) { await HandleAttributeSet(component); return; }
            if (id.StartsWith("tag_edit_")) { await ShowTagSelector(component); return; }
            if (id.StartsWith("tag_create_")) { await ShowTagModal(component); return; }

            string tid = id.Substring(id.IndexOf('_') + 1);
            var task = _allTasks.FirstOrDefault(x => x.Id == tid);
            if (task == null) { await component.RespondAsync("⚠️ なし", ephemeral: true); return; }

            if (id.StartsWith("done_"))
            {
                task.CompletedAt = DateTime.Now; task.IsSnoozed = false; SaveData();
                await component.UpdateAsync(x => { x.Content = $"✅ ~~{task.Content}~~ (完了・日報待ち)"; x.Components = null; });
            }
            else if (id.StartsWith("snooze_"))
            {
                task.IsSnoozed = !task.IsSnoozed; SaveData();
                await component.UpdateAsync(x => { x.Content = $"💤 ~~{task.Content}~~ (後回し)"; x.Components = null; });
            }
            else if (id.StartsWith("forget_"))
            {
                task.IsForgotten = true; task.IsSnoozed = false; SaveData();
                await component.UpdateAsync(x => { x.Content = $"🗑️ ~~{task.Content}~~ (ゴミ箱へ移動)"; x.Components = null; });
            }
            else if (id.StartsWith("recall_"))
            {
                task.IsForgotten = false; SaveData();
                await component.UpdateAsync(x => { x.Content = $"↩️ **復活:** {task.Content}"; x.Components = null; });
            }
            else if (id.StartsWith("obliterate_"))
            {
                _allTasks.Remove(task); SaveData();
                await component.UpdateAsync(x => { x.Content = $"💥 **抹消:** {task.Content}"; x.Components = null; });
            }
            else if (id.StartsWith("edit_")) { await ShowEditPanel(component, task); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Button Error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------
    // 5. Daily Close & Config
    // ---------------------------------------------------------
    private async Task RunDailyClose(ulong userId, ISocketMessageChannel feedbackChannel = null)
    {
        var doneTasks = _allTasks.Where(t => t.UserId == userId && t.CompletedAt != null).ToList();

        if (doneTasks.Count == 0)
        {
            if (feedbackChannel != null) await feedbackChannel.SendMessageAsync("本日の完了タスクはありません。");
            return;
        }

        var user = _client.GetUser(userId);
        string userName = user?.Username ?? $"User";

        var channelGroups = doneTasks.GroupBy(t => t.ChannelId);

        foreach (var group in channelGroups)
        {
            ulong chId = group.Key;
            var targetChannel = _client.GetChannel(chId) as ISocketMessageChannel;

            if (targetChannel == null && feedbackChannel != null) targetChannel = feedbackChannel;
            if (targetChannel == null) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"🌅 **Daily Report: {userName}** ({DateTime.Now:yyyy/MM/dd})");
            sb.AppendLine($"**{group.Count()}件** 完了！");
            sb.AppendLine("```");
            foreach (var tGroup in group.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "その他"))
            {
                sb.AppendLine($"【{tGroup.Key}】");
                foreach (var t in tGroup) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}");
                sb.AppendLine();
            }
            sb.AppendLine("```");

            await targetChannel.SendMessageAsync(sb.ToString());
        }

        foreach (var t in doneTasks) _allTasks.Remove(t);
        SaveData();
    }

    private async Task HandleConfig(ISocketMessageChannel channel, SocketUser user, string args)
    {
        if (args.Contains("time"))
        {
            var parts = args.Split(' ');
            if (parts.Length < 3) return;
            string time = parts[2];

            var conf = _configs.FirstOrDefault(c => c.UserId == user.Id);
            if (conf == null) { conf = new UserConfig { UserId = user.Id }; _configs.Add(conf); }
            conf.ReportTime = time;
            SaveData();
            await channel.SendMessageAsync($"⏰ 日報時間を **{time}** に設定しました。");
        }
    }

    // --- Helper Logic ---
    private int GetSortScore(TaskItem t)
    {
        if (t.CompletedAt != null) return -10;
        if (t.IsSnoozed) return -1;
        if (t.Priority == 1 && t.Difficulty == 1) return 10;
        if (t.Priority == 1 && t.Difficulty == 0) return 9;
        if (t.Priority == -1) return 5;
        if (t.Priority == 0 && t.Difficulty == 1) return 2;
        return 1;
    }
    private string GetPriorityLabel(int p, int d)
    {
        if (p == 1 && d == 1) return "1"; if (p == 1 && d == 0) return "2";
        if (p == 0 && d == 1) return "3"; if (p == 0 && d == 0) return "4"; return "-";
    }
    private async Task StartSortingSession(SocketMessageComponent c, string m)
    {
        var t = FindNextSortTarget(c.User.Id, m);
        if (t == null) { await c.RespondAsync("🎉 完了！", ephemeral: true); return; }
        await ShowSortCard(c, t, m, false);
    }
    private async Task HandleAttributeSet(SocketMessageComponent c)
    {
        var p = c.Data.CustomId.Split('_');
        var t = _allTasks.FirstOrDefault(x => x.Id == p[3]);
        if (t != null) { t.Priority = int.Parse(p[1]); t.Difficulty = int.Parse(p[2]); SaveData(); }
        var next = FindNextSortTarget(c.User.Id, p.Length > 4 ? p[4] : "p");
        if (next != null) await ShowSortCard(c, next, p[4], true);
        else await c.UpdateAsync(x => { x.Content = "🎉 完了！"; x.Components = null; });
    }
    private TaskItem? FindNextSortTarget(ulong u, string m) => _allTasks.FirstOrDefault(t => t.UserId == u && !t.IsForgotten && t.CompletedAt == null && (m == "p" ? t.Priority == -1 : t.Tags.Count == 0));

    private ComponentBuilder CreateSortComp(TaskItem t, string m) => new ComponentBuilder()
        .WithButton("1", $"set_1_1_{t.Id}_{m}", ButtonStyle.Secondary).WithButton("2", $"set_1_0_{t.Id}_{m}", ButtonStyle.Secondary)
        .WithButton("3", $"set_0_1_{t.Id}_{m}", ButtonStyle.Secondary).WithButton("4", $"set_0_0_{t.Id}_{m}", ButtonStyle.Secondary)
        .WithButton(null, $"tag_edit_{t.Id}_{m}", ButtonStyle.Secondary, new Emoji("🏷️"));

    private async Task ShowSortCard(SocketInteraction interaction, TaskItem t, string m, bool up)
    {
        string txt = $"**仕分け:** {t.Content}"; var b = CreateSortComp(t, m);
        if (up && interaction is SocketMessageComponent c) await c.UpdateAsync(x => { x.Content = txt; x.Components = b.Build(); });
        else await interaction.RespondAsync(txt, components: b.Build(), ephemeral: true);
    }
    private async Task ShowEditPanel(SocketMessageComponent c, TaskItem t)
    {
        string txt = $"🔧 **編集:** {t.Content}\nタグ: {string.Join(",", t.Tags)}";
        await c.RespondAsync(txt, components: CreateSortComp(t, "normal").Build(), ephemeral: true);
    }
    private async Task ShowTagSelector(SocketMessageComponent c)
    {
        string rid = c.Data.CustomId.Substring("tag_edit_".Length); var ps = rid.Split('_'); var t = _allTasks.FirstOrDefault(x => x.Id == ps[0]); if (t == null) return;
        var tags = _allTasks.Where(x => x.UserId == c.User.Id || x.ChannelId == c.Channel.Id).SelectMany(x => x.Tags).Distinct().Take(25).ToList();
        if (tags.Count == 0) { await ShowTagModal(c); return; }
        var menu = new SelectMenuBuilder().WithCustomId($"tag_sel_{ps[0]}_{ps[1]}").WithPlaceholder("タグ選択");
        foreach (var tag in tags) menu.AddOption(tag, tag);
        var b = new ComponentBuilder().WithSelectMenu(menu).WithButton(null, $"tag_create_{ps[0]}_{ps[1]}", ButtonStyle.Primary, new Emoji("➕"));
        await c.RespondAsync($"🏷️ {t.Content}", components: b.Build(), ephemeral: true);
    }
    private async Task ShowTagModal(SocketMessageComponent c)
    {
        string rid = c.Data.CustomId.Substring("tag_create_".Length);
        await c.RespondWithModalAsync(new ModalBuilder().WithTitle("新規タグ").WithCustomId($"modal_tag_{rid}").AddTextInput("タグ名", "n", value: "", required: true).Build());
    }
    private async Task ModalHandler(SocketModal m)
    {
        try
        {
            if (!m.Data.CustomId.StartsWith("modal_tag_")) return;
            string rid = m.Data.CustomId.Substring("modal_tag_".Length); var ps = rid.Split('_'); var t = _allTasks.FirstOrDefault(x => x.Id == ps[0]);
            if (t != null) { string v = m.Data.Components.First().Value.Trim(); t.Tags.Clear(); if (v != "") t.Tags.Add(v); SaveData(); }
            string mode = ps.Length > 1 ? ps[1] : "normal";
            if (mode == "normal") await m.RespondAsync($"✅ 更新: {t?.Content}", ephemeral: true);
            else { var next = FindNextSortTarget(m.User.Id, mode); if (next != null) await ShowSortCard(m, next, mode, false); else await m.RespondAsync("🎉 完了", ephemeral: true); }
        }
        catch (Exception ex) { Console.WriteLine(ex); }
    }
    // ★変更: ヘッダーに名前を表示
    private async Task ShowReport(ISocketMessageChannel c, SocketUser u, string a)
    {
        var d = DateTime.Today;
        string period = "Today";
        if (a.Contains("yesterday")) { d = d.AddDays(-1); period = "Yesterday"; }
        else if (a.Contains("week")) { d = d.AddDays(-7); period = "Last Week"; }
        var l = _allTasks.Where(x => x.UserId == u.Id && x.CompletedAt >= d).OrderBy(x => x.CompletedAt).ToList();
        if (l.Count == 0) { await c.SendMessageAsync($"📭 {period}の完了タスクはありません。"); return; }
        var sb = new StringBuilder();
        sb.AppendLine($"📊 **Task Report: {u.Username}** ({period})");
        sb.AppendLine($"**{l.Count}件** 完了！");
        sb.AppendLine("```text"); foreach (var t in l) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}"); sb.AppendLine("```");
        await c.SendMessageAsync(sb.ToString());
    }

    private async Task ExportData(ISocketMessageChannel c, SocketUser u)
    {
        string j = JsonSerializer.Serialize(_allTasks.Where(x => x.UserId == u.Id), new JsonSerializerOptions { WriteIndented = true });
        if (j.Length > 1900) { File.WriteAllText("e.json", j); await c.SendFileAsync("e.json"); } else await c.SendMessageAsync($"```json\n{j}\n```");
    }
    private async Task ImportData(ISocketMessageChannel c, string j)
    {
        try { var l = JsonSerializer.Deserialize<List<TaskItem>>(j); if (l != null) { foreach (var i in l) { _allTasks.RemoveAll(x => x.Id == i.Id); _allTasks.Add(i); } SaveData(); await c.SendMessageAsync($"📥 {l.Count}件"); } } catch { }
    }
    private void SaveData()
    {
        try { File.WriteAllText(DbFileName, JsonSerializer.Serialize(_allTasks)); File.WriteAllText(ConfigFileName, JsonSerializer.Serialize(_configs)); } catch { }
    }
    private void LoadData()
    {
        if (File.Exists(DbFileName)) try { _allTasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(DbFileName)) ?? new(); } catch { }
        if (File.Exists(ConfigFileName)) try { _configs = JsonSerializer.Deserialize<List<UserConfig>>(File.ReadAllText(ConfigFileName)) ?? new(); } catch { }
    }
    private Task Log(LogMessage msg) { Console.WriteLine(msg.ToString()); return Task.CompletedTask; }
}
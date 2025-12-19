using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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

class Program
{
    private static readonly List<string> DefaultPrefixes = new List<string>
    {
        " ", "　", "\t", "→", "：", ":", "・", "※", ">", "-", "+", "*", "■", "□", "●", "○"
    };
    private static readonly List<string> TagPrefixes = new List<string> { "#", "＃" };

    private DiscordSocketClient _client = null!;
    private static List<TaskItem> _allTasks = new List<TaskItem>();
    private const string DbFileName = "tasks.json";

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
        LoadTasksFromJson();
        await Task.Delay(-1);
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
        if (string.Equals(arg1, "report", StringComparison.OrdinalIgnoreCase))
        {
            await ShowReport(message.Channel, message.Author, arg1);
            return;
        }
        if (string.Equals(arg1, "close", StringComparison.OrdinalIgnoreCase))
        {
            await CloseDay(message.Channel, message.Author);
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
        SaveTasksToJson();

        if (addedTasks.Count > 0)
        {
            await ShowCompactList(channel, user, "");
        }
    }

    // ---------------------------------------------------------
    // 2. Compact List Logic
    // ---------------------------------------------------------
    private async Task ShowCompactList(ISocketMessageChannel channel, SocketUser user, string args)
    {
        IEnumerable<TaskItem> query = _allTasks.Where(t => !t.IsForgotten && t.CompletedAt == null);
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
                string pfx = task.IsSnoozed ? "💤 " : "";
                string display = task.Content.Split('\n')[0];
                if (display.Length > 20) display = display.Substring(0, 20) + "...";

                sb.AppendLine($"`[{label}]` {pfx}{display}");
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
            if (task.IsSnoozed) contentLabel = "💤 " + contentLabel;

            string fullLabel = $"[{label}] {contentLabel}";
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
    // 3. Handlers
    // ---------------------------------------------------------
    private async Task SelectMenuHandler(SocketMessageComponent component)
    {
        string customId = component.Data.CustomId;

        if (customId == "main_task_selector")
        {
            string taskId = component.Data.Values.First();
            var task = _allTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) { await component.RespondAsync("タスクが見つかりません", ephemeral: true); return; }
            await ShowTaskActionPanel(component, task);
            return;
        }

        if (customId.StartsWith("tag_sel_"))
        {
            string rawArgs = customId.Substring("tag_sel_".Length);
            var parts = rawArgs.Split('_');
            var task = _allTasks.FirstOrDefault(t => t.Id == parts[0]);
            if (task != null)
            {
                string tag = component.Data.Values.First();
                task.Tags.Clear(); if (!string.IsNullOrEmpty(tag)) task.Tags.Add(tag);
                SaveTasksToJson();

                string mode = parts.Length > 1 ? parts[1] : "normal";
                if (mode == "normal") await component.RespondAsync($"✅ タグを **[{tag}]** にしました。", ephemeral: true);
                else
                {
                    var next = FindNextSortTarget(component.User.Id, mode);
                    if (next != null) await ShowSortCard(component, next, mode, false);
                    else await component.RespondAsync("🎉 仕分け完了！", ephemeral: true);
                }
            }
        }
    }

    private async Task ShowTaskActionPanel(SocketMessageComponent component, TaskItem task)
    {
        string label = GetPriorityLabel(task.Priority, task.Difficulty);
        string content = task.Content;
        string currentTag = task.Tags.Count > 0 ? task.Tags[0] : "(なし)";
        string text = $"🛠️ **タスク操作:**\n[{label}] **{content}**\nタグ: {currentTag}";

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
            var t = _allTasks.FirstOrDefault(x => x.Id == tid);
            if (t == null) { await component.RespondAsync("⚠️ なし", ephemeral: true); return; }

            if (id.StartsWith("done_"))
            {
                t.CompletedAt = DateTime.Now; SaveTasksToJson();
                await component.UpdateAsync(x => { x.Content = $"✅ ~~{t.Content}~~ (完了)"; x.Components = null; });
            }
            else if (id.StartsWith("snooze_"))
            {
                t.IsSnoozed = !t.IsSnoozed; SaveTasksToJson();
                await component.UpdateAsync(x => { x.Content = $"💤 ~~{t.Content}~~ (後回し)"; x.Components = null; });
            }
            else if (id.StartsWith("forget_"))
            {
                t.IsForgotten = true; SaveTasksToJson();
                await component.UpdateAsync(x => { x.Content = $"🗑️ ~~{t.Content}~~ (忘却)"; x.Components = null; });
            }
            else if (id.StartsWith("edit_")) { await ShowEditPanel(component, t); }
        }
        catch { }
    }

    // --- Helper Logic ---
    private int GetSortScore(TaskItem t)
    {
        if (t.IsSnoozed) return 0;
        if (t.Priority == 1 && t.Difficulty == 1) return 10;
        if (t.Priority == 1 && t.Difficulty == 0) return 9;
        if (t.Priority == -1) return 5;
        if (t.Priority == 0 && t.Difficulty == 1) return 2;
        return 1;
    }

    private string GetPriorityLabel(int p, int d)
    {
        if (p == 1 && d == 1) return "1";
        if (p == 1 && d == 0) return "2";
        if (p == 0 && d == 1) return "3";
        if (p == 0 && d == 0) return "4";
        return "-";
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
        if (t != null) { t.Priority = int.Parse(p[1]); t.Difficulty = int.Parse(p[2]); SaveTasksToJson(); }
        var next = FindNextSortTarget(c.User.Id, p.Length > 4 ? p[4] : "p");
        if (next != null) await ShowSortCard(c, next, p[4], true);
        else await c.UpdateAsync(x => { x.Content = "🎉 完了！"; x.Components = null; });
    }
    private TaskItem? FindNextSortTarget(ulong u, string m) => _allTasks.FirstOrDefault(t => t.UserId == u && !t.IsForgotten && t.CompletedAt == null && (m == "p" ? t.Priority == -1 : t.Tags.Count == 0));

    // UI Components
    private ComponentBuilder CreateSortComp(TaskItem t, string m) => new ComponentBuilder()
        .WithButton("1", $"set_1_1_{t.Id}_{m}", ButtonStyle.Secondary)
        .WithButton("2", $"set_1_0_{t.Id}_{m}", ButtonStyle.Secondary)
        .WithButton("3", $"set_0_1_{t.Id}_{m}", ButtonStyle.Secondary)
        .WithButton("4", $"set_0_0_{t.Id}_{m}", ButtonStyle.Secondary)
        .WithButton(null, $"tag_edit_{t.Id}_{m}", ButtonStyle.Secondary, new Emoji("🏷️"));

    private async Task ShowSortCard(SocketInteraction interaction, TaskItem t, string m, bool up)
    {
        string txt = $"**仕分け:** {t.Content}";
        var b = CreateSortComp(t, m);
        if (up && interaction is SocketMessageComponent c)
            await c.UpdateAsync(x => { x.Content = txt; x.Components = b.Build(); });
        else
            await interaction.RespondAsync(txt, components: b.Build(), ephemeral: true);
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
        // ★変更: ここだけ Primary (青)
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
        if (!m.Data.CustomId.StartsWith("modal_tag_")) return;
        string rid = m.Data.CustomId.Substring("modal_tag_".Length); var ps = rid.Split('_'); var t = _allTasks.FirstOrDefault(x => x.Id == ps[0]);
        if (t != null) { string v = m.Data.Components.First().Value.Trim(); t.Tags.Clear(); if (v != "") t.Tags.Add(v); SaveTasksToJson(); }
        string mode = ps.Length > 1 ? ps[1] : "normal";
        if (mode == "normal") await m.RespondAsync($"✅ 更新: {t?.Content}", ephemeral: true);
        else
        {
            var next = FindNextSortTarget(m.User.Id, mode);
            if (next != null) await ShowSortCard(m, next, mode, false);
            else await m.RespondAsync("🎉 完了", ephemeral: true);
        }
    }

    // IO Logic
    private async Task ShowReport(ISocketMessageChannel c, SocketUser u, string a)
    {
        var d = DateTime.Today; if (a.Contains("yesterday")) d = d.AddDays(-1); else if (a.Contains("week")) d = d.AddDays(-7);
        var l = _allTasks.Where(x => x.UserId == u.Id && x.CompletedAt >= d).OrderBy(x => x.CompletedAt).ToList();
        if (l.Count == 0) { await c.SendMessageAsync("📭 なし"); return; }
        var sb = new StringBuilder(); sb.AppendLine($"🎉 **Report:** {l.Count}件");
        sb.AppendLine("```text"); foreach (var t in l) sb.AppendLine($"・{t.Content}"); sb.AppendLine("```");
        await c.SendMessageAsync(sb.ToString());
    }
    private async Task CloseDay(ISocketMessageChannel c, SocketUser u)
    {
        var l = _allTasks.Where(x => x.UserId == u.Id && x.CompletedAt != null).ToList(); if (l.Count == 0) { await c.SendMessageAsync("なし"); return; }
        await ShowReport(c, u, "report"); foreach (var t in l) _allTasks.Remove(t); SaveTasksToJson(); await c.SendMessageAsync("🧹 クローズ完了");
    }
    private async Task ExportData(ISocketMessageChannel c, SocketUser u)
    {
        string j = JsonSerializer.Serialize(_allTasks.Where(x => x.UserId == u.Id), new JsonSerializerOptions { WriteIndented = true });
        if (j.Length > 1900) { File.WriteAllText("e.json", j); await c.SendFileAsync("e.json"); } else await c.SendMessageAsync($"```json\n{j}\n```");
    }
    private async Task ImportData(ISocketMessageChannel c, string j)
    {
        try { var l = JsonSerializer.Deserialize<List<TaskItem>>(j); if (l != null) { foreach (var i in l) { _allTasks.RemoveAll(x => x.Id == i.Id); _allTasks.Add(i); } SaveTasksToJson(); await c.SendMessageAsync($"📥 {l.Count}件"); } } catch { }
    }
    private void SaveTasksToJson() { try { File.WriteAllText(DbFileName, JsonSerializer.Serialize(_allTasks)); } catch { } }
    private void LoadTasksFromJson() { if (File.Exists(DbFileName)) try { _allTasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(DbFileName)) ?? new(); } catch { } }
    private Task Log(LogMessage msg) { Console.WriteLine(msg.ToString()); return Task.CompletedTask; }
}
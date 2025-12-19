using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// --- Ver 3.0 Data Model ---
public class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ulong ChannelId { get; set; }
    public ulong UserId { get; set; }
    public string Content { get; set; } = "";

    public int Priority { get; set; } = -1;   // -1:Inbox, 1:High, 0:Low
    public int Difficulty { get; set; } = -1;
    public List<string> Tags { get; set; } = new List<string>();

    public bool IsSnoozed { get; set; }
    public bool IsForgotten { get; set; }
    public DateTime? CompletedAt { get; set; }
}

class Program
{
    // ★復活: メモ（行結合）とみなす記号リスト
    private static readonly List<string> DefaultPrefixes = new List<string>
    {
        " ", "　", "\t",         // 空白インデント
        "→", "：", ":", "・",    // リスト記号
        "※", ">", "-", "+", "*"  // 注釈・箇条書き
    };

    private DiscordSocketClient _client = null!;
    private static List<TaskItem> _allTasks = new List<TaskItem>();
    private const string DbFileName = "tasks.json";

    static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();

    public async Task MainAsync()
    {
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        };
        _client = new DiscordSocketClient(config);
        _client.Log += Log;
        _client.MessageReceived += MessageReceivedAsync;
        _client.ButtonExecuted += ButtonHandler;

        string token = "MTQ1MTE5Mzg0MDczNTIyNDAxMg.GJzP10.TsRAcmITV_oyZQZajFHpV0361O17t5HV_Ej_aU"; // ★トークンを設定

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        LoadTasksFromJson();

        await Task.Delay(-1);
    }

    private async Task MessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (string.IsNullOrWhiteSpace(message.Content)) return;

        string content = message.Content.Trim();
        if (!content.StartsWith("!ttodo")) return;

        ulong userId = message.Author.Id;

        var parts = content.Split(new[] { ' ', '　', '\t', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
        string arg1 = parts.Length > 1 ? parts[1].Trim() : "";

        // コマンド分岐
        if (string.Equals(arg1, "list", StringComparison.OrdinalIgnoreCase) || arg1.StartsWith("list ", StringComparison.OrdinalIgnoreCase))
        {
            await ShowList(message.Channel, message.Author, arg1);
            return;
        }
        if (string.Equals(arg1, "report", StringComparison.OrdinalIgnoreCase) || arg1.StartsWith("report ", StringComparison.OrdinalIgnoreCase))
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
            string json = content.Substring(content.IndexOf("import") + 6).Trim();
            await ImportData(message.Channel, json);
            return;
        }

        if (!string.IsNullOrWhiteSpace(arg1))
        {
            await AddNewTasks(message.Channel, message.Author, arg1);
        }
    }

    // ---------------------------------------------------------
    // 1. Input Logic (Inbox) - ★ここを大幅修正
    // ---------------------------------------------------------
    private async Task AddNewTasks(ISocketMessageChannel channel, SocketUser user, string rawText)
    {
        var lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var addedTasks = new List<TaskItem>();

        List<string> currentTags = new List<string>();

        // 一時保持用（タスク作成前のバッファ）
        TaskItem? pendingTask = null;

        foreach (var line in lines)
        {
            string trimLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimLine))
            {
                // 空行が来たら、溜まっているタスクを確定させて、タグをリセット
                if (pendingTask != null)
                {
                    _allTasks.Add(pendingTask);
                    addedTasks.Add(pendingTask);
                    pendingTask = null;
                }
                currentTags.Clear();
                continue;
            }

            // ヘッダー解析 (#タグ)
            if (trimLine.StartsWith("#") || trimLine.StartsWith("＃"))
            {
                // タグ行が来た時点で、前のタスクは終わり
                if (pendingTask != null)
                {
                    _allTasks.Add(pendingTask);
                    addedTasks.Add(pendingTask);
                    pendingTask = null;
                }

                string tagName = trimLine.Substring(1).Trim();
                if (!string.IsNullOrEmpty(tagName)) currentTags = new List<string> { tagName };
                continue;
            }

            // ★行結合判定: DefaultPrefixes で始まる場合は「前のタスクの続き」とみなす
            bool isContinuation = false;
            foreach (var p in DefaultPrefixes)
            {
                if (line.StartsWith(p))
                {
                    isContinuation = true;
                    break;
                }
            }

            // 続きの行であり、かつ親タスクが存在する場合
            if (isContinuation && pendingTask != null)
            {
                // 改行を入れて結合
                pendingTask.Content += "\n" + trimLine;
            }
            else
            {
                // 新しいタスクの開始
                // 前のタスクがあれば確定させる
                if (pendingTask != null)
                {
                    _allTasks.Add(pendingTask);
                    addedTasks.Add(pendingTask);
                }

                // --- ここから新しいタスクの解析 ---
                int prio = -1;
                int diff = -1;
                string content = trimLine;

                // 優先度判定 (!!, !, ??, ?)
                if (content.StartsWith("!!") || content.StartsWith("！！")) { prio = 1; diff = 1; content = content.Substring(2); }
                else if (content.StartsWith("??") || content.StartsWith("？？")) { prio = 0; diff = 0; content = content.Substring(2); }
                else if (content.StartsWith("!") || content.StartsWith("！")) { prio = 1; diff = 0; content = content.Substring(1); }
                else if (content.StartsWith("?") || content.StartsWith("？")) { prio = 0; diff = 1; content = content.Substring(1); }

                content = content.Trim();

                // インラインタグ解析
                var lineTags = new List<string>(currentTags);
                if (content.StartsWith("[") || content.StartsWith("【"))
                {
                    int endIdx = -1;
                    if (content.Contains("]")) endIdx = content.IndexOf("]");
                    else if (content.Contains("】")) endIdx = content.IndexOf("】");

                    if (endIdx > 0)
                    {
                        string tagPart = content.Substring(1, endIdx - 1);
                        lineTags.Add(tagPart);
                        content = content.Substring(endIdx + 1).Trim();
                    }
                }

                // 解析後のコンテンツが空でなければタスク生成（まだリストには入れない）
                if (!string.IsNullOrWhiteSpace(content))
                {
                    pendingTask = new TaskItem
                    {
                        ChannelId = channel.Id,
                        UserId = user.Id,
                        Content = content,
                        Priority = prio,
                        Difficulty = diff,
                        Tags = lineTags.Distinct().ToList(),
                        IsSnoozed = false,
                        IsForgotten = false
                    };
                }
            }
        }

        // 最後のタスクを確定
        if (pendingTask != null)
        {
            _allTasks.Add(pendingTask);
            addedTasks.Add(pendingTask);
        }

        SaveTasksToJson();

        // 結果表示 (アクティブのみ)
        if (addedTasks.Count > 0)
        {
            var allActiveTasks = _allTasks
                .Where(t => t.UserId == user.Id &&
                            t.ChannelId == channel.Id &&
                            t.CompletedAt == null &&
                            !t.IsForgotten &&
                            !t.IsSnoozed)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"📋 **現在のタスク一覧 (全{allActiveTasks.Count}件):**");

            var grouped = allActiveTasks
                .GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "📂 未分類")
                .OrderBy(g => g.Key);

            foreach (var g in grouped)
            {
                sb.AppendLine($"\n**🏷️ [{g.Key}]**");
                var sorted = g.OrderByDescending(t => GetSortScore(t));
                foreach (var t in sorted)
                {
                    string icon = GetIcon(t.Priority, t.Difficulty);
                    string isNew = addedTasks.Contains(t) ? " `(New)`" : "";

                    // 表示用: 改行が含まれている場合、2行目以降をインデントして見やすくする
                    string displayContent = t.Content;
                    if (displayContent.Contains("\n"))
                    {
                        // 1行目だけ普通に表示し、2行目以降はそのまま出す（Markdownの引用記号等は使わないほうがコピペしやすい）
                        // ただ、あまりに長いと見づらいので、1行目 + (メモ...) のように省略する手もあるが、
                        // ここでは要望通り「テキスト一覧」なのでそのまま出す
                    }

                    sb.AppendLine($"・{icon} {displayContent}{isNew}");
                }
            }

            bool hasInbox = allActiveTasks.Any(t => t.Priority == -1);
            var comp = hasInbox
                ? new ComponentBuilder().WithButton("🖊️ 仕分けを開始", "start_sort", ButtonStyle.Primary).Build()
                : null;

            await channel.SendMessageAsync(sb.ToString(), components: comp);
        }
    }

    // ---------------------------------------------------------
    // 2. List Logic
    // ---------------------------------------------------------
    private async Task ShowList(ISocketMessageChannel channel, SocketUser user, string args)
    {
        IEnumerable<TaskItem> query = _allTasks.Where(t => !t.IsForgotten && t.CompletedAt == null);

        bool isMine = args.Contains("mine");
        bool isChannel = args.Contains("channel");

        if (isMine)
        {
            query = query.Where(t => t.UserId == user.Id);
            await channel.SendMessageAsync($"🌍 **マイスコープ (全チャンネルの自分のタスク):**");
        }
        else if (isChannel)
        {
            query = query.Where(t => t.ChannelId == channel.Id);
            await channel.SendMessageAsync($"📢 **チャンネルスコープ (ここの全員のタスク):**");
        }
        else
        {
            query = query.Where(t => t.UserId == user.Id && t.ChannelId == channel.Id);
            await channel.SendMessageAsync($"📂 **ToDoリスト (標準):**");
        }

        var visibleTasks = query.ToList();

        if (visibleTasks.Count == 0)
        {
            await channel.SendMessageAsync("🎉 タスクはありません！");
            return;
        }

        var grouped = visibleTasks
            .GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "📂 未分類")
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            await channel.SendMessageAsync($"**🏷️ [{group.Key}]**");
            var sortedGroup = group.OrderByDescending(t => GetSortScore(t));
            foreach (var task in sortedGroup)
            {
                await SendTaskPanel(channel, task, isChannel, isMine);
                await Task.Delay(200);
            }
        }
    }

    private int GetSortScore(TaskItem t)
    {
        if (t.IsSnoozed) return 0;
        if (t.Priority == 1 && t.Difficulty == 1) return 10;
        if (t.Priority == 1 && t.Difficulty == 0) return 9;
        if (t.Priority == -1) return 5;
        if (t.Priority == 0 && t.Difficulty == 1) return 2;
        return 1;
    }

    private async Task SendTaskPanel(ISocketMessageChannel channel, TaskItem item, bool showUser, bool showChannel)
    {
        string icon = GetIcon(item.Priority, item.Difficulty);
        string prefix = item.IsSnoozed ? "💤 " : "";
        string info = "";
        if (showUser) info += $" (@{_client.GetUser(item.UserId)?.Username ?? "User"})";
        if (showChannel) info += $" (at <#{item.ChannelId}>)";

        var builder = new ComponentBuilder()
            .WithButton("完了！", $"done_{item.Id}", ButtonStyle.Success)
            .WithButton("後回し", $"snooze_{item.Id}", ButtonStyle.Secondary)
            .WithButton("忘れる", $"forget_{item.Id}", ButtonStyle.Danger)
            .WithButton("⚙️", $"edit_{item.Id}", ButtonStyle.Secondary);

        await channel.SendMessageAsync($"📝 {prefix}{icon} {item.Content}{info}", components: builder.Build());
    }

    private string GetIcon(int p, int d)
    {
        if (p == 1 && d == 1) return "⛰️";
        if (p == 1 && d == 0) return "🔥";
        if (p == 0 && d == 1) return "🐌";
        if (p == 0 && d == 0) return "☕";
        return "📂";
    }

    // ---------------------------------------------------------
    // 3. Button Handler
    // ---------------------------------------------------------
    private async Task ButtonHandler(SocketMessageComponent component)
    {
        try
        {
            string customId = component.Data.CustomId;
            if (customId == "start_sort") { await StartSortingSession(component); return; }
            if (customId.StartsWith("set_")) { await HandleAttributeSet(component); return; }

            string targetId = customId.Substring(customId.IndexOf('_') + 1);
            var task = _allTasks.FirstOrDefault(t => t.Id == targetId);
            if (task == null)
            {
                await component.RespondAsync("⚠️ タスクが見つかりません。", ephemeral: true);
                return;
            }

            if (customId.StartsWith("done_"))
            {
                task.CompletedAt = DateTime.Now;
                SaveTasksToJson();
                await component.UpdateAsync(x => { x.Content = $"✅ ~~{task.Content}~~ (完了)"; x.Components = null; });
            }
            else if (customId.StartsWith("snooze_"))
            {
                task.IsSnoozed = !task.IsSnoozed;
                SaveTasksToJson();
                await component.UpdateAsync(x => {
                    string prefix = task.IsSnoozed ? "💤 " : "";
                    x.Content = $"📝 {prefix}{GetIcon(task.Priority, task.Difficulty)} {task.Content} (後回し中)";
                    x.Components = new ComponentBuilder()
                        .WithButton("完了！", $"done_{task.Id}", ButtonStyle.Success)
                        .WithButton("復活", $"recall_{task.Id}", ButtonStyle.Primary)
                        .Build();
                });
            }
            else if (customId.StartsWith("forget_"))
            {
                task.IsForgotten = true;
                SaveTasksToJson();
                await component.UpdateAsync(x => { x.Content = $"🌫️ ~~{task.Content}~~ (忘却)"; x.Components = null; });
            }
            else if (customId.StartsWith("recall_"))
            {
                task.IsSnoozed = false; task.IsForgotten = false;
                SaveTasksToJson();
                await component.UpdateAsync(x => {
                    x.Content = $"📝 {GetIcon(task.Priority, task.Difficulty)} {task.Content}";
                    x.Components = new ComponentBuilder()
                        .WithButton("完了！", $"done_{task.Id}", ButtonStyle.Success)
                        .WithButton("後回し", $"snooze_{task.Id}", ButtonStyle.Secondary)
                        .WithButton("忘れる", $"forget_{task.Id}", ButtonStyle.Danger)
                        .WithButton("⚙️", $"edit_{task.Id}", ButtonStyle.Secondary).Build();
                });
            }
            else if (customId.StartsWith("edit_")) { await ShowEditPanel(component, task); }
        }
        catch (Exception ex) { Console.WriteLine($"Error in button: {ex}"); }
    }

    private async Task StartSortingSession(SocketMessageComponent component)
    {
        var inboxTask = _allTasks.FirstOrDefault(t => t.UserId == component.User.Id && t.Priority == -1 && !t.IsForgotten && t.CompletedAt == null);
        if (inboxTask == null) { await component.RespondAsync("🎉 仕分けが必要なタスクはありません！", ephemeral: true); return; }
        await ShowSortCard(component, inboxTask, isUpdate: false);
    }

    private async Task HandleAttributeSet(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split('_');
        string taskId = parts[3];
        var task = _allTasks.FirstOrDefault(t => t.Id == taskId);
        if (task != null) { task.Priority = int.Parse(parts[1]); task.Difficulty = int.Parse(parts[2]); SaveTasksToJson(); }

        var nextTask = _allTasks.FirstOrDefault(t => t.UserId == component.User.Id && t.Priority == -1 && !t.IsForgotten && t.CompletedAt == null);
        if (nextTask != null) await ShowSortCard(component, nextTask, isUpdate: true);
        else await component.UpdateAsync(x => { x.Content = "🎉 仕分け完了！ `!ttodo list` で確認しましょう。"; x.Components = null; });
    }

    private async Task ShowSortCard(SocketMessageComponent component, TaskItem task, bool isUpdate)
    {
        string text = $"**仕分け中:**\n「{task.Content}」はどのタイプ？";
        var builder = new ComponentBuilder()
            .WithButton("⛰️ 最重要", $"set_1_1_{task.Id}", ButtonStyle.Danger)
            .WithButton("🔥 即処理", $"set_1_0_{task.Id}", ButtonStyle.Success)
            .WithButton("🐌 重量級", $"set_0_1_{task.Id}", ButtonStyle.Secondary)
            .WithButton("☕ 雑用", $"set_0_0_{task.Id}", ButtonStyle.Secondary);
        if (isUpdate) await component.UpdateAsync(x => { x.Content = text; x.Components = builder.Build(); });
        else await component.RespondAsync(text, components: builder.Build(), ephemeral: true);
    }

    private async Task ShowEditPanel(SocketMessageComponent component, TaskItem task)
    {
        string text = $"🔧 **編集モード:** {task.Content}\n現在の状態に変更しますか？";
        var builder = new ComponentBuilder()
            .WithButton("⛰️", $"set_1_1_{task.Id}", ButtonStyle.Secondary)
            .WithButton("🔥", $"set_1_0_{task.Id}", ButtonStyle.Secondary)
            .WithButton("🐌", $"set_0_1_{task.Id}", ButtonStyle.Secondary)
            .WithButton("☕", $"set_0_0_{task.Id}", ButtonStyle.Secondary);
        await component.RespondAsync(text, components: builder.Build(), ephemeral: true);
    }

    // ---------------------------------------------------------
    // 4. Report / Close / Export / Import
    // ---------------------------------------------------------
    private async Task ShowReport(ISocketMessageChannel channel, SocketUser user, string args)
    {
        DateTime since = DateTime.Today;
        string title = "今日";
        if (args.Contains("yesterday")) { since = DateTime.Today.AddDays(-1); title = "昨日"; }
        else if (args.Contains("week")) { since = DateTime.Today.AddDays(-7); title = "今週"; }

        var dones = _allTasks.Where(t => t.UserId == user.Id && t.CompletedAt >= since).OrderBy(t => t.CompletedAt).ToList();
        if (dones.Count == 0) { await channel.SendMessageAsync($"📭 {title}の完了タスクはありません。"); return; }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"🎉 **Report ({title}):** {dones.Count} Tasks Completed!");
        sb.AppendLine("```text");
        var grouped = dones.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "その他");
        foreach (var g in grouped)
        {
            sb.AppendLine($"【{g.Key}】");
            foreach (var t in g) sb.AppendLine($"・[{t.CompletedAt?.ToString("HH:mm")}] {GetIcon(t.Priority, t.Difficulty)} {t.Content}");
            sb.AppendLine();
        }
        sb.AppendLine("```");
        await channel.SendMessageAsync(sb.ToString());
    }

    private async Task CloseDay(ISocketMessageChannel channel, SocketUser user)
    {
        var dones = _allTasks.Where(t => t.UserId == user.Id && t.CompletedAt != null).ToList();
        if (dones.Count == 0) { await channel.SendMessageAsync("本日はまだ完了タスクがありません。"); return; }
        await ShowReport(channel, user, "report");
        foreach (var t in dones) _allTasks.Remove(t);
        SaveTasksToJson();
        await channel.SendMessageAsync($"🧹 **Daily Closing:** {dones.Count}件のタスクをアーカイブし、リストから削除しました。");
    }

    private async Task ExportData(ISocketMessageChannel channel, SocketUser user)
    {
        var myTasks = _allTasks.Where(t => t.UserId == user.Id).ToList();
        string json = JsonSerializer.Serialize(myTasks, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > 1900) { File.WriteAllText("export.json", json); await channel.SendFileAsync("export.json", "📦 データエクスポート"); }
        else await channel.SendMessageAsync($"📦 **データエクスポート:**\n```json\n{json}\n```");
    }

    private async Task ImportData(ISocketMessageChannel channel, string json)
    {
        try
        {
            var imported = JsonSerializer.Deserialize<List<TaskItem>>(json);
            if (imported == null) return;
            int count = 0;
            foreach (var item in imported)
            {
                var existing = _allTasks.FirstOrDefault(t => t.Id == item.Id);
                if (existing != null) _allTasks.Remove(existing);
                _allTasks.Add(item);
                count++;
            }
            SaveTasksToJson();
            await channel.SendMessageAsync($"📥 **{count}件** のデータをインポートしました。");
        }
        catch (Exception ex) { await channel.SendMessageAsync($"⚠️ インポート失敗: {ex.Message}"); }
    }

    private void SaveTasksToJson()
    {
        try { File.WriteAllText(DbFileName, JsonSerializer.Serialize(_allTasks)); }
        catch (Exception ex) { Console.WriteLine($"Save Error: {ex.Message}"); }
    }

    private void LoadTasksFromJson()
    {
        if (!File.Exists(DbFileName)) return;
        try { var data = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(DbFileName)); if (data != null) _allTasks = data; }
        catch (Exception ex) { Console.WriteLine($"Load Error: {ex.Message}"); }
    }

    private Task Log(LogMessage msg) { Console.WriteLine(msg.ToString()); return Task.CompletedTask; }
}
using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TToDo
{
    public class DiscordBot
    {
        private DiscordSocketClient _client;

        public static DiscordBot? Instance { get; private set; }
        public DiscordSocketClient Client => _client;

        private static readonly List<string> DefaultPrefixes = new List<string> { " ", "　", "\t", "→", "：", ":", "・", "※", ">", "-", "+", "*", "■", "□", "●", "○" };
        private static readonly List<string> TagPrefixes = new List<string> { "#", "＃" };

        public DiscordBot()
        {
            Instance = this;
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            };
            _client = new DiscordSocketClient(config);
            _client.Log += msg => { Console.WriteLine($"[Bot] {msg}"); return Task.CompletedTask; };
            _client.MessageReceived += MessageReceivedAsync;
            _client.SelectMenuExecuted += SelectMenuHandler;
        }

        public ulong? ResolveChannelId(string guildName, string channelName)
        {
            if (_client == null || _client.ConnectionState != ConnectionState.Connected) return null;
            var guild = _client.Guilds.FirstOrDefault(g => g.Name == guildName);
            if (guild == null) return null;
            var channel = guild.TextChannels.FirstOrDefault(c => c.Name == channelName);
            return channel?.Id;
        }

        public string ResolveAvatarUrl(string userName)
        {
            if (_client == null || _client.ConnectionState != ConnectionState.Connected || string.IsNullOrEmpty(userName)) return "";
            foreach (var guild in _client.Guilds)
            {
                var user = guild.Users.FirstOrDefault(u => u.Username == userName || u.Nickname == userName || u.DisplayName == userName);
                if (user != null) return user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            }
            return "";
        }

        public async Task StartAsync()
        {
            await _client.LoginAsync(TokenType.Bot, Globals.BotToken);
            await _client.StartAsync();
            _ = Task.Run(() => AutoReportLoop());
        }

        // --- コマンド処理 ---
        private async Task MessageReceivedAsync(SocketMessage message)
        {
            if (message.Author.IsBot || string.IsNullOrWhiteSpace(message.Content)) return;
            string content = message.Content.Trim();
            if (!content.StartsWith("!ttodo")) return;

            string arg1 = "";
            var parts = content.Split(new[] { ' ', '　', '\t', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) arg1 = parts[1].Trim();

            // 1. ヘルプ
            if (arg1.StartsWith("help", StringComparison.OrdinalIgnoreCase))
            {
                await ShowHelp(message.Channel);
                return;
            }

            // 2. Webページ
            if (arg1.StartsWith("web", StringComparison.OrdinalIgnoreCase))
            {
                // ★変更: 公開用のURLを表示
                await message.Channel.SendMessageAsync($"🌍 **TToDo Board:**\n{Globals.PublicUrl}");
                return;
            }

            // 3. 日報 (report today / report yesterday のみ)
            // ★変更: "report" 単体や "today" 単体を排除
            if (arg1.Equals("report today", StringComparison.OrdinalIgnoreCase) ||
                arg1.Equals("report yesterday", StringComparison.OrdinalIgnoreCase))
            {
                await ShowReport(message.Channel, message.Author, arg1);
                return;
            }

            // 4. タスク一覧
            if (arg1.StartsWith("list", StringComparison.OrdinalIgnoreCase))
            {
                await ShowCompactList(message.Channel, message.Author);
                return;
            }

            // 5. タスク追加 (それ以外の場合)
            // ※ "report" とだけ打った場合もタスクとして登録されてしまうのを防ぐならここに追加条件が必要ですが、
            // 今回は「コマンドに合致しないものはタスク」というルール通りにします。
            if (!string.IsNullOrWhiteSpace(arg1))
            {
                await AddNewTasks(message.Channel, message.Author, arg1);
            }
        }

        // --- 機能実装: ヘルプ ---
        private async Task ShowHelp(ISocketMessageChannel c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📖 **TToDo Help**"); 
            sb.AppendLine($"Web Board: {Globals.PublicUrl}");
            sb.AppendLine("");
            sb.AppendLine("`!ttodo report today`"); // ★変更
            sb.AppendLine("今日の完了タスク(日報)を表示します。");
            sb.AppendLine("");
            sb.AppendLine("`!ttodo report yesterday`");
            sb.AppendLine("昨日の完了タスク(日報)を表示します。");
            sb.AppendLine("");
            sb.AppendLine("`!ttodo list`");
            sb.AppendLine("自分の未完了タスク一覧を表示します。");
            sb.AppendLine("");
            sb.AppendLine("`!ttodo web`");
            sb.AppendLine("Web管理画面のURLを表示します。");
            sb.AppendLine("");
            sb.AppendLine("`!ttodo [タスク内容]`");
            sb.AppendLine("タスクを追加します。複数行、`!!`重要、`#タグ` も可能です。");

            await c.SendMessageAsync(sb.ToString());
        }

        // --- 機能実装: タスク追加 ---
        private async Task AddNewTasks(ISocketMessageChannel channel, SocketUser user, string rawText)
        {
            string avatar = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            string guildName = "DM";
            string channelName = channel.Name;
            if (channel is SocketGuildChannel guildChannel) guildName = guildChannel.Guild.Name;

            var lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var addedTasks = new List<TaskItem>();
            List<string> currentTags = new List<string>();
            TaskItem? pendingTask = null;

            foreach (var line in lines)
            {
                string trimLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimLine))
                {
                    if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); pendingTask = null; }
                    currentTags.Clear(); continue;
                }

                bool isTagLine = false;
                foreach (var tp in TagPrefixes)
                {
                    if (trimLine.StartsWith(tp))
                    {
                        if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); pendingTask = null; }
                        string tagName = trimLine.Substring(tp.Length).Trim();
                        if (!string.IsNullOrEmpty(tagName)) currentTags = new List<string> { tagName };
                        isTagLine = true; break;
                    }
                }
                if (isTagLine) continue;

                bool isContinuation = false;
                foreach (var p in DefaultPrefixes) { if (line.StartsWith(p)) { isContinuation = true; break; } }

                if (isContinuation && pendingTask != null)
                {
                    pendingTask.Content += "\n" + trimLine;
                }
                else
                {
                    if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); }

                    int prio = -1; int diff = -1; string content = trimLine;
                    if (content.StartsWith("!!") || content.StartsWith("！！")) { prio = 1; diff = 1; content = content.Substring(2); }
                    else if (content.StartsWith("!")) { prio = 1; diff = 0; content = content.Substring(1); }

                    content = content.Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        pendingTask = new TaskItem
                        {
                            ChannelId = channel.Id,
                            UserId = user.Id,
                            UserName = user.Username,
                            AvatarUrl = avatar,
                            Assignee = user.Username,
                            GuildName = guildName,
                            ChannelName = channelName,
                            Content = content,
                            Priority = prio,
                            Difficulty = diff,
                            Tags = new List<string>(currentTags)
                        };
                    }
                }
            }
            if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
            Globals.SaveData();
            if (addedTasks.Count > 0) await ShowCompactList(channel, user);
        }

        // --- 機能実装: リスト表示 ---
        private (string Text, MessageComponent? Components) BuildListView(ulong userId, ulong channelId)
        {
            var today = Globals.GetJstNow().Date;
            List<TaskItem> visibleTasks;
            lock (Globals.Lock)
            {
                visibleTasks = Globals.AllTasks
                    .Where(t => t.UserId == userId && t.ChannelId == channelId && !t.IsForgotten && (t.CompletedAt == null || t.CompletedAt >= today))
                    .OrderByDescending(t => GetSortScore(t))
                    .ToList();
            }

            if (visibleTasks.Count == 0) return ("🎉 タスクはありません！", null);

            var sb = new StringBuilder();
            sb.AppendLine($"📂 **タスク一覧 ({visibleTasks.Count}件):**");

            var grouped = visibleTasks.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "📂 未分類").OrderBy(g => g.Key);
            foreach (var group in grouped)
            {
                sb.AppendLine($"\n**{group.Key}**");
                foreach (var task in group)
                {
                    string label = GetPriorityLabel(task.Priority);
                    string state = task.CompletedAt != null ? "✅ " : "";
                    string display = task.Content.Split('\n')[0];
                    if (display.Length > 25) display = display.Substring(0, 25) + "...";
                    sb.AppendLine($"`[{label}]` {state}{display}");
                }
            }

            var doneMenu = new SelectMenuBuilder().WithCustomId("menu_quick_done").WithPlaceholder("✅ 完了にする...").WithMinValues(1).WithMaxValues(1);
            var archiveMenu = new SelectMenuBuilder().WithCustomId("menu_quick_archive").WithPlaceholder("🗑️ アーカイブする...").WithMinValues(1).WithMaxValues(1);

            int count = 0;
            foreach (var task in visibleTasks)
            {
                if (count >= 25) break;
                string label = GetPriorityLabel(task.Priority);
                string contentLabel = task.Content.Replace("\n", " ");
                if (contentLabel.Length > 45) contentLabel = contentLabel.Substring(0, 42) + "...";

                var option = new SelectMenuOptionBuilder()
                    .WithLabel($"[{label}] {contentLabel}")
                    .WithValue(task.Id)
                    .WithDescription(task.Tags.Count > 0 ? task.Tags[0] : "未分類");

                doneMenu.AddOption(option);
                archiveMenu.AddOption(option);
                count++;
            }

            var components = new ComponentBuilder()
                .WithSelectMenu(doneMenu, row: 0)
                .WithSelectMenu(archiveMenu, row: 1)
                .Build();

            return (sb.ToString(), components);
        }

        private async Task ShowCompactList(ISocketMessageChannel channel, SocketUser user)
        {
            var view = BuildListView(user.Id, channel.Id);
            if (view.Components == null) await channel.SendMessageAsync(view.Text);
            else await channel.SendMessageAsync(view.Text, components: view.Components);
        }

        private async Task SelectMenuHandler(SocketMessageComponent component)
        {
            try
            {
                string id = component.Data.CustomId;
                string val = component.Data.Values.First();

                if (id == "menu_quick_done")
                {
                    lock (Globals.Lock)
                    {
                        var t = Globals.AllTasks.FirstOrDefault(x => x.Id == val);
                        if (t != null)
                        {
                            t.CompletedAt = Globals.GetJstNow();
                            t.IsSnoozed = false;
                            Globals.SaveData();
                        }
                    }
                    var view = BuildListView(component.User.Id, component.Channel.Id);
                    await component.UpdateAsync(x => { x.Content = view.Text; x.Components = view.Components; });
                }
                else if (id == "menu_quick_archive")
                {
                    lock (Globals.Lock)
                    {
                        var t = Globals.AllTasks.FirstOrDefault(x => x.Id == val);
                        if (t != null)
                        {
                            t.IsForgotten = true;
                            Globals.SaveData();
                        }
                    }
                    var view = BuildListView(component.User.Id, component.Channel.Id);
                    await component.UpdateAsync(x => { x.Content = view.Text; x.Components = view.Components; });
                }
            }
            catch { }
        }

        // --- 機能実装: 日報表示 ---
        private async Task ShowReport(ISocketMessageChannel c, SocketUser u, string args)
        {
            var now = Globals.GetJstNow();
            var today = now.Date;
            DateTime start, end;
            string label;

            if (args.Contains("yesterday"))
            {
                start = today.AddDays(-1);
                end = today;
                label = "Yesterday";
            }
            else
            {
                start = today;
                end = today.AddDays(1);
                label = "Today";
            }

            List<TaskItem> l;
            lock (Globals.Lock)
            {
                l = Globals.AllTasks
                    .Where(x => x.UserId == u.Id && x.CompletedAt != null && x.CompletedAt >= start && x.CompletedAt < end)
                    .OrderBy(x => x.CompletedAt)
                    .ToList();
            }

            if (l.Count == 0) { await c.SendMessageAsync($"📭 {label}の完了タスクはありません。"); return; }

            var sb = new StringBuilder();
            sb.AppendLine($"📊 **Task Report: {u.Username}** ({label})");
            sb.AppendLine($"**{l.Count}件** 完了！");
            sb.AppendLine("```text");
            foreach (var t in l) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}");
            sb.AppendLine("```");
            await c.SendMessageAsync(sb.ToString());
        }

        // --- 機能実装: 自動日報 & 手動日報送信 ---
        public async Task<bool> SendManualReport(ReportRequest req)
        {
            var targetChannelId = ResolveChannelId(req.TargetGuild, req.TargetChannel);
            if (targetChannelId == null) return false;

            var channel = _client.GetChannel(targetChannelId.Value) as ISocketMessageChannel;
            if (channel == null) return false;

            var now = Globals.GetJstNow();
            var today = now.Date;
            DateTime rangeStart, rangeEnd;
            string dateLabel, periodName;

            if (req.TargetRange == "yesterday")
            {
                rangeStart = today.AddDays(-1);
                rangeEnd = today;
                dateLabel = $"{rangeStart:yyyy/MM/dd} (昨日)";
                periodName = "昨日";
            }
            else
            {
                rangeStart = today;
                rangeEnd = today.AddDays(1);
                dateLabel = $"{rangeStart:yyyy/MM/dd}";
                periodName = "本日";
            }

            List<TaskItem> completedTasks;
            lock (Globals.Lock)
            {
                completedTasks = Globals.AllTasks
                    .Where(t =>
                        (t.Assignee == req.TargetUser || (string.IsNullOrEmpty(t.Assignee) && t.UserName == req.TargetUser))
                        && t.CompletedAt != null && t.CompletedAt >= rangeStart && t.CompletedAt < rangeEnd
                        && (string.IsNullOrEmpty(req.SourceGuild) || t.GuildName == req.SourceGuild)
                        && (string.IsNullOrEmpty(req.SourceChannel) || t.ChannelName == req.SourceChannel)
                    )
                    .OrderBy(t => t.CompletedAt)
                    .ToList();
            }

            string sourceInfo = "";
            if (!string.IsNullOrEmpty(req.SourceGuild)) sourceInfo += $" ({req.SourceGuild}" + (!string.IsNullOrEmpty(req.SourceChannel) ? $" / {req.SourceChannel})" : ")");

            if (completedTasks.Count == 0)
            {
                await channel.SendMessageAsync($"📭 **Daily Report: {req.TargetUser}**{sourceInfo}\n{dateLabel} の完了タスクはありません。");
                return true;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"📊 **Daily Report: {req.TargetUser}**{sourceInfo}");
            sb.AppendLine($"📅 {dateLabel}");
            sb.AppendLine($"{periodName} **{completedTasks.Count}件** のタスクを完了しました！");
            sb.AppendLine("```");
            foreach (var t in completedTasks)
            {
                string loc = (string.IsNullOrEmpty(req.SourceGuild) && !string.IsNullOrEmpty(t.GuildName)) ? $" [{t.GuildName}]" : "";
                sb.AppendLine($"・[{t.CompletedAt?.ToString("HH:mm")}{loc}] {t.Content}");
            }
            sb.AppendLine("```");
            await channel.SendMessageAsync(sb.ToString());
            return true;
        }

        private async Task RunDailyClose(ulong userId)
        {
            var now = Globals.GetJstNow();
            var deleteThreshold = now.Date.AddDays(-1);
            lock (Globals.Lock) { Globals.AllTasks.RemoveAll(t => t.CompletedAt != null && t.CompletedAt < deleteThreshold); Globals.SaveData(); }

            UserConfig? config;
            lock (Globals.Lock) { config = Globals.Configs.FirstOrDefault(x => x.UserId == userId); }

            if (config != null && !string.IsNullOrEmpty(config.TargetGuild) && !string.IsNullOrEmpty(config.TargetChannel))
            {
                var reportReq = new ReportRequest { TargetUser = config.UserName, TargetGuild = config.TargetGuild, TargetChannel = config.TargetChannel, TargetRange = "today" };
                await SendManualReport(reportReq);
                return;
            }

            // 設定がない場合は従来通り発生元へ送信
            DateTime reportStart = (now.Hour == 0 && now.Minute < 5) ? now.Date.AddDays(-1) : now.Date;
            List<TaskItem> reportTasks;
            lock (Globals.Lock) { reportTasks = Globals.AllTasks.Where(t => t.UserId == userId && t.CompletedAt != null && t.CompletedAt >= reportStart).ToList(); }
            if (reportTasks.Count == 0) return;

            var u = _client.GetUser(userId);
            string un = u?.Username ?? "User";
            foreach (var group in reportTasks.GroupBy(t => t.ChannelId))
            {
                try
                {
                    ulong chId = group.Key;
                    var targetChannel = _client.GetChannel(chId) as ISocketMessageChannel;
                    if (targetChannel == null) try { targetChannel = await _client.GetChannelAsync(chId) as ISocketMessageChannel; } catch { }
                    if (targetChannel == null) continue;
                    var sb = new StringBuilder();
                    sb.AppendLine($"🌅 **Daily Report: {un}** ({Globals.GetJstNow():yyyy/MM/dd})");
                    sb.AppendLine($"**{group.Count()}件** 完了！");
                    sb.AppendLine("```");
                    foreach (var t in group) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}");
                    sb.AppendLine("```");
                    await targetChannel.SendMessageAsync(sb.ToString());
                }
                catch { }
            }
        }

        private async Task AutoReportLoop()
        {
            while (true)
            {
                try
                {
                    var now = Globals.GetJstNow();
                    if (now.Second == 0)
                    {
                        string curTime = now.ToString("HH:mm");
                        List<ulong> users; lock (Globals.Lock) users = Globals.AllTasks.Select(t => t.UserId).Distinct().ToList();
                        foreach (var uid in users)
                        {
                            string target = "00:00";
                            lock (Globals.Lock) { var c = Globals.Configs.FirstOrDefault(x => x.UserId == uid); if (c != null) target = c.ReportTime; }
                            if (target == curTime) await RunDailyClose(uid);
                        }
                        await Task.Delay(60000);
                    }
                    else await Task.Delay(500);
                }
                catch { await Task.Delay(1000); }
            }
        }

        private int GetSortScore(TaskItem t) { if (t.CompletedAt != null) return -10; if (t.IsSnoozed) return -1; if (t.Priority == 1) return 10; return 1; }
        private string GetPriorityLabel(int p) { if (p == 1) return "重要"; return "普通"; }
    }
}
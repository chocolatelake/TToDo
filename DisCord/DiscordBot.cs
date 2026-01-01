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
                var user = guild.Users.FirstOrDefault(u =>
                    (u.Username != null && u.Username == userName) ||
                    (u.Nickname != null && u.Nickname == userName) ||
                    (u.DisplayName != null && u.DisplayName == userName));

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
            string lower = content.ToLowerInvariant();

            string arg1 = "";
            if (lower.StartsWith("!ttodolist")) arg1 = "list";
            else if (lower.StartsWith("!ttodofull")) arg1 = "list full";
            else if (lower.StartsWith("!ttododone")) arg1 = "list done";
            else if (lower.StartsWith("!ttodoarchive")) arg1 = "list archive";
            else if (lower.StartsWith("!ttodohelp")) arg1 = "help";
            else if (lower.StartsWith("!ttodoweb")) arg1 = "web";
            else if (lower.StartsWith("!ttodotoday")) arg1 = "report today";
            else if (lower.StartsWith("!ttodoyesterday")) arg1 = "report yesterday";
            else if (content.StartsWith("!ttodo"))
            {
                var parts = content.Split(new[] { ' ', '　', '\t', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) arg1 = parts[1].Trim();
            }
            else return;

            if (arg1.StartsWith("help", StringComparison.OrdinalIgnoreCase))
            {
                await ShowHelp(message.Channel);
                return;
            }

            if (arg1.StartsWith("web", StringComparison.OrdinalIgnoreCase))
            {
                await message.Channel.SendMessageAsync($"🌍 **TToDo Board:**\n{Globals.PublicUrl}");
                return;
            }

            if (arg1.Equals("report today", StringComparison.OrdinalIgnoreCase) ||
                arg1.Equals("report yesterday", StringComparison.OrdinalIgnoreCase))
            {
                await ShowReport(message.Channel, message.Author, arg1);
                return;
            }

            if (arg1.StartsWith("list", StringComparison.OrdinalIgnoreCase))
            {
                string type = "todo";
                if (arg1.Contains("full")) type = "full";
                else if (arg1.Contains("done")) type = "done";
                else if (arg1.Contains("archive")) type = "archive";

                await ShowCompactList(message.Channel, message.Author, type);
                return;
            }

            if (!string.IsNullOrWhiteSpace(arg1))
            {
                await AddNewTasks(message.Channel, message.Author, arg1, message);
            }
        }

        // --- 機能実装: ヘルプ ---
        private async Task ShowHelp(ISocketMessageChannel c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📖 **TToDo Help**");
            sb.AppendLine($"Web Board: {Globals.PublicUrl}");
            sb.AppendLine("");
            sb.AppendLine("`!ttodo [タスク内容]`");
            sb.AppendLine("タスクを追加します。");
            sb.AppendLine("");
            sb.AppendLine("`!ttodolist` : 未完了タスクのみ");
            sb.AppendLine("`!ttododone` : 完了タスクのみ");
            sb.AppendLine("`!ttodoarchive` : アーカイブのみ");
            sb.AppendLine("`!ttodofull` : 全てのタスク");
            sb.AppendLine("");
            sb.AppendLine("`!ttodotoday` : 今日の日報");
            sb.AppendLine("`!ttodo web` : Web URL");

            await c.SendMessageAsync(sb.ToString());
        }

        // --- 機能実装: タスク追加 ---
        private async Task AddNewTasks(ISocketMessageChannel channel, SocketUser user, string rawText, SocketMessage message)
        {
            string assignee = user.Username;
            string avatar = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();

            var mentionedUser = message.MentionedUsers.FirstOrDefault(u => !u.IsBot);
            if (mentionedUser != null)
            {
                assignee = mentionedUser.Username;
                avatar = mentionedUser.GetAvatarUrl() ?? mentionedUser.GetDefaultAvatarUrl();
                rawText = rawText.Replace(mentionedUser.Mention, "").Replace($"<@!{mentionedUser.Id}>", "").Trim();
            }

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

                    int prio = 0;
                    string content = trimLine;

                    if (content.StartsWith("!") || content.StartsWith("！"))
                    {
                        prio = 1;
                        content = content.Substring(1);
                        if (content.StartsWith("!") || content.StartsWith("！")) content = content.Substring(1);
                    }
                    else if (content.StartsWith("?") || content.StartsWith("？"))
                    {
                        prio = -1;
                        content = content.Substring(1);
                        if (content.StartsWith("?") || content.StartsWith("？")) content = content.Substring(1);
                    }

                    content = content.Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        pendingTask = new TaskItem
                        {
                            ChannelId = channel.Id,
                            UserId = user.Id,
                            UserName = user.Username,
                            AvatarUrl = avatar,
                            Assignee = assignee,
                            GuildName = guildName,
                            ChannelName = channelName,
                            Content = content,
                            Priority = prio,
                            Difficulty = 0,
                            Tags = new List<string>(currentTags),
                            // ★デフォルト日付: 今日 ～ 1か月後
                            StartDate = Globals.GetJstNow().Date,
                            DueDate = Globals.GetJstNow().Date.AddMonths(1),
                            TimeMode = 0 // 不明
                        };
                    }
                }
            }
            if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
            Globals.SaveData();
            if (addedTasks.Count > 0) await ShowCompactList(channel, user, "todo");
        }

        // --- 機能実装: リスト表示 ---
        private (string Text, MessageComponent? Components) BuildListView(ulong userId, ulong channelId, string type)
        {
            var user = _client.GetUser(userId);
            string username = user?.Username ?? "";

            List<TaskItem> visibleTasks;
            lock (Globals.Lock)
            {
                var query = Globals.AllTasks.AsEnumerable();
                query = query.Where(t => !string.IsNullOrEmpty(t.Assignee) && t.Assignee == username);

                if (type == "done") query = query.Where(t => t.CompletedAt != null && !t.IsForgotten);
                else if (type == "archive") query = query.Where(t => t.IsForgotten);
                else if (type == "full") { }
                else query = query.Where(t => t.CompletedAt == null && !t.IsForgotten);

                // 自動優先度スコア順にソート
                visibleTasks = query.OrderBy(t => GetAutoScore(t)).ToList();
            }

            string titleSuffix = "";
            if (type == "done") titleSuffix = " (完了済み)";
            if (type == "archive") titleSuffix = " (アーカイブ)";
            if (type == "full") titleSuffix = " (すべて)";

            if (visibleTasks.Count == 0) return ($"🎉 タスクはありません！{titleSuffix}\n🌍 {Globals.PublicUrl}", null);

            var sb = new StringBuilder();
            sb.AppendLine($"📂 **タスク一覧{titleSuffix} ({visibleTasks.Count}件):**");
            sb.AppendLine($"🌍 {Globals.PublicUrl}");

            // 自動判定ラベル > タグ > 手動優先度 > タスク
            var autoPrioGroups = visibleTasks.GroupBy(t => GetAutoPriorityLabel(t));

            foreach (var autoGroup in autoPrioGroups)
            {
                // 自動判定ラベル (見出し)
                sb.AppendLine($"\n**{autoGroup.Key}**");

                var tagGroups = autoGroup.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "📂 未分類").OrderBy(g => g.Key);
                foreach (var tagGroup in tagGroups)
                {
                    // タグ (見出し)
                    sb.AppendLine($"**{tagGroup.Key}**");

                    foreach (var task in tagGroup.OrderByDescending(t => t.Priority))
                    {
                        string pLabel = GetPriorityLabel(task.Priority);
                        string pBadge = string.IsNullOrEmpty(pLabel) ? "" : $"[{pLabel}] ";

                        string state = task.CompletedAt != null ? "✅ " : "";
                        string display = task.Content.Split('\n')[0];
                        if (display.Length > 25) display = display.Substring(0, 25) + "...";
                        if (task.CompletedAt != null && type != "done") display = $"~~{display}~~";

                        sb.AppendLine($"{pBadge}{state}{display}");
                    }
                }
            }

            var doneMenu = new SelectMenuBuilder().WithCustomId("menu_quick_done").WithPlaceholder("✅ 完了にする...").WithMinValues(1).WithMaxValues(1);
            var archiveMenu = new SelectMenuBuilder().WithCustomId("menu_quick_archive").WithPlaceholder("🗑️ アーカイブする...").WithMinValues(1).WithMaxValues(1);

            int count = 0;
            bool hasItems = false;
            foreach (var task in visibleTasks)
            {
                if (count >= 25) break;
                string autoLabel = GetAutoPriorityLabel(task).Replace("【", "").Replace("】", "").Replace("\n", " ");
                string contentLabel = task.Content.Replace("\n", " ");
                if (contentLabel.Length > 45) contentLabel = contentLabel.Substring(0, 42) + "...";

                var option = new SelectMenuOptionBuilder()
                    .WithLabel($"{contentLabel}")
                    .WithValue(task.Id)
                    .WithDescription($"{autoLabel} | {(task.Tags.Count > 0 ? task.Tags[0] : "未分類")}");

                doneMenu.AddOption(option);
                archiveMenu.AddOption(option);
                count++;
                hasItems = true;
            }

            if (!hasItems) return (sb.ToString(), null);

            var components = new ComponentBuilder()
                .WithSelectMenu(doneMenu, row: 0)
                .WithSelectMenu(archiveMenu, row: 1)
                .Build();

            return (sb.ToString(), components);
        }

        private async Task ShowCompactList(ISocketMessageChannel channel, SocketUser user, string type = "todo")
        {
            var view = BuildListView(user.Id, channel.Id, type);
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
                    var view = BuildListView(component.User.Id, component.Channel.Id, "todo");
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
                    var view = BuildListView(component.User.Id, component.Channel.Id, "todo");
                    await component.UpdateAsync(x => { x.Content = view.Text; x.Components = view.Components; });
                }
            }
            catch { }
        }

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
                label = "昨日";
            }
            else
            {
                start = today;
                end = today.AddDays(1);
                label = "本日";
            }

            List<TaskItem> l;
            lock (Globals.Lock)
            {
                l = Globals.AllTasks
                    .Where(x =>
                        (!string.IsNullOrEmpty(x.Assignee) && x.Assignee == u.Username)
                        && x.CompletedAt != null && x.CompletedAt >= start && x.CompletedAt < end)
                    .OrderBy(x => x.CompletedAt)
                    .ToList();
            }

            if (l.Count == 0) { await c.SendMessageAsync($"📭 {label}の完了タスクはありません。"); return; }

            var sb = new StringBuilder();
            sb.AppendLine($"📊 **日報: {u.Username}** ({label})");
            sb.AppendLine($"**{l.Count}件** 完了！");
            sb.AppendLine("```text");
            foreach (var t in l) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}");
            sb.AppendLine("```");
            await c.SendMessageAsync(sb.ToString());
        }

        // --- 全体日報送信 (日本語タイトル + 絵文字アイコン) ---
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
                await channel.SendMessageAsync($"📭 **全体日報: {req.TargetUser}**{sourceInfo}\n{dateLabel} の完了タスクはありません。");
                return true;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"🌅 **全体日報: {req.TargetUser}**{sourceInfo}");
            sb.AppendLine($"{dateLabel}");
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

        // --- 自動日報処理 (日本語タイトル + 絵文字アイコン) ---
        private async Task RunDailyClose(ulong userId)
        {
            UserConfig? config;
            List<string> allKnownUserNames;

            lock (Globals.Lock)
            {
                Globals.AllTasks.RemoveAll(t => t.UserId == userId && t.IsReported);
                Globals.SaveData();
                config = Globals.Configs.FirstOrDefault(x => x.UserId == userId);
                allKnownUserNames = Globals.Configs
                    .Select(c => c.UserName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList();
            }

            string targetUserName = config?.UserName ?? "";
            if (string.IsNullOrEmpty(targetUserName))
            {
                var u = _client.GetUser(userId);
                if (u != null) targetUserName = u.Username;
            }
            if (string.IsNullOrEmpty(targetUserName)) return;
            if (!allKnownUserNames.Contains(targetUserName)) allKnownUserNames.Add(targetUserName);

            List<TaskItem> myTasks = new List<TaskItem>();
            List<TaskItem> orphanTasks = new List<TaskItem>();

            lock (Globals.Lock)
            {
                var completedTasks = Globals.AllTasks
                    .Where(t => t.CompletedAt != null && !t.IsReported)
                    .ToList();

                foreach (var t in completedTasks)
                {
                    if (t.Assignee == targetUserName)
                    {
                        myTasks.Add(t);
                        continue;
                    }
                    bool isKnownUser = allKnownUserNames.Contains(t.Assignee);
                    if (!isKnownUser)
                    {
                        orphanTasks.Add(t);
                    }
                }
            }

            if (myTasks.Count > 0)
            {
                // 元のチャンネルへの報告 (チャンネル別日報)
                foreach (var group in myTasks.GroupBy(t => t.ChannelId))
                {
                    try
                    {
                        var ch = _client.GetChannel(group.Key) as ISocketMessageChannel;
                        if (ch == null) ch = await _client.GetChannelAsync(group.Key) as ISocketMessageChannel;

                        if (ch != null)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"📺 **チャンネル別日報: {targetUserName}** ({Globals.GetJstNow():yyyy/MM/dd})");
                            sb.AppendLine($"**{group.Count()}件** 完了！");
                            sb.AppendLine("```");
                            foreach (var t in group) sb.AppendLine($"・[{t.CompletedAt:MM/dd HH:mm}] {t.Content}");
                            sb.AppendLine("```");
                            await ch.SendMessageAsync(sb.ToString());
                        }
                    }
                    catch { }
                }

                // 指定チャンネルへの報告 (全体日報)
                if (config != null && !string.IsNullOrEmpty(config.TargetGuild) && !string.IsNullOrEmpty(config.TargetChannel))
                {
                    var reportReq = new ReportRequest
                    {
                        TargetUser = targetUserName,
                        TargetGuild = config.TargetGuild,
                        TargetChannel = config.TargetChannel,
                        TargetRange = "today"
                    };
                    await SendManualReport(reportReq);
                }

                lock (Globals.Lock)
                {
                    foreach (var t in myTasks) Globals.AllTasks.Remove(t);
                    Globals.SaveData();
                }
            }

            if (orphanTasks.Count > 0)
            {
                string defaultGuildName = "Discordの方が便利かも";
                string defaultChannelName = "ttodo";
                ulong? defaultChId = ResolveChannelId(defaultGuildName, defaultChannelName);

                if (defaultChId.HasValue)
                {
                    var ch = _client.GetChannel(defaultChId.Value) as ISocketMessageChannel;
                    if (ch != null)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"🧹 **担当者不明タスクの回収**");
                        sb.AppendLine($"**{orphanTasks.Count}件** のタスクを回収・削除しました。");
                        sb.AppendLine("```");
                        foreach (var t in orphanTasks)
                        {
                            string assigneeInfo = string.IsNullOrEmpty(t.Assignee) ? "None" : t.Assignee;
                            sb.AppendLine($"・[{t.CompletedAt:MM/dd}] {t.Content} (担当: {assigneeInfo})");
                        }
                        sb.AppendLine("```");
                        await ch.SendMessageAsync(sb.ToString());
                    }
                }

                lock (Globals.Lock)
                {
                    foreach (var t in orphanTasks) Globals.AllTasks.Remove(t);
                    Globals.SaveData();
                }
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
                        List<ulong> targetUserIds;
                        lock (Globals.Lock)
                        {
                            targetUserIds = Globals.Configs.Select(c => c.UserId).Distinct().ToList();
                        }
                        foreach (var uid in targetUserIds)
                        {
                            string targetTime = "00:00";
                            lock (Globals.Lock)
                            {
                                var c = Globals.Configs.FirstOrDefault(x => x.UserId == uid);
                                if (c != null) targetTime = c.ReportTime;
                            }
                            if (targetTime == curTime)
                            {
                                await RunDailyClose(uid);
                            }
                        }
                        await Task.Delay(60000);
                    }
                    else await Task.Delay(500);
                }
                catch { await Task.Delay(1000); }
            }
        }

        // 自動優先度計算
        private int GetAutoScore(TaskItem t)
        {
            int dateScore;
            if (!t.DueDate.HasValue)
            {
                dateScore = 90;
            }
            else
            {
                var today = Globals.GetJstNow().Date;
                var due = t.DueDate.Value.Date;
                var diff = (int)(due - today).TotalDays;

                if (diff < 0) dateScore = 0;
                else if (diff == 0) dateScore = 10; // 今日中(1日目)
                else if (diff == 1) dateScore = 30; // あと2日(2日目)
                else if (diff == 2) dateScore = 40; // あと3日(3日目)
                else if (diff <= 6) dateScore = 50; // 期日が近い
                else dateScore = 60;                // 期日が遠い
            }

            int timeScore;
            if (t.TimeMode == 1) timeScore = 1;
            else if (t.TimeMode == 2) timeScore = 2;
            else timeScore = 3;

            return (dateScore * 10) + timeScore;
        }

        // 自動優先度ラベル
        private string GetAutoPriorityLabel(TaskItem t)
        {
            string timeLabel = t.TimeMode switch
            {
                1 => "すぐ終わる",
                2 => "時間かかる",
                _ => "工数は不明"
            };

            string dateLabel;
            if (!t.DueDate.HasValue)
            {
                dateLabel = "いつかやる";
            }
            else
            {
                var today = Globals.GetJstNow().Date;
                var due = t.DueDate.Value.Date;
                var diff = (int)(due - today).TotalDays;

                if (diff < 0) dateLabel = "期日過ぎたよ";
                else if (diff == 0) dateLabel = "今日中";
                else if (diff == 1) dateLabel = "あと 2 日";
                else if (diff == 2) dateLabel = "あと 3 日";
                else if (diff <= 6) dateLabel = "期日が近い";
                else dateLabel = "期日が遠い";
            }

            return $"【{timeLabel}】\n【{dateLabel}】";
        }

        private int GetSortScore(TaskItem t)
        {
            if (t.CompletedAt != null) return -10;
            if (t.IsSnoozed) return -1;
            if (t.Priority == 1) return 10;
            if (t.Priority == 0) return 5;
            return 1;
        }

        private string GetPriorityLabel(int p)
        {
            if (p == 1) return "高";
            if (p == 0) return "中";
            if (p == -1) return "低";
            return "";
        }
    }
}
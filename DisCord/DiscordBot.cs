using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TToDo
{
    public class DiscordBot
    {
        private DiscordSocketClient _client;

        public static DiscordBot Instance { get; private set; }
        public DiscordSocketClient Client => _client;

        private static readonly List<string> DefaultPrefixes = new List<string> { " ", "　", "\t", "→", "：", ":", "・", "※", ">", "-", "+", "*", "■", "□", "●", "○" };
        private static readonly List<string> TagPrefixes = new List<string> { "#", "＃" };

        public DiscordBot()
        {
            Instance = this;
            var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent };
            _client = new DiscordSocketClient(config);
            _client.Log += msg => { Console.WriteLine($"[Bot] {msg}"); return Task.CompletedTask; };
            _client.MessageReceived += MessageReceivedAsync;
            _client.ButtonExecuted += ButtonHandler;
            _client.SelectMenuExecuted += SelectMenuHandler;
            _client.ModalSubmitted += ModalHandler;
        }

        public ulong? ResolveChannelId(string guildName, string channelName)
        {
            if (_client == null || _client.ConnectionState != ConnectionState.Connected) return null;
            var guild = _client.Guilds.FirstOrDefault(g => g.Name == guildName);
            if (guild == null) return null;
            var channel = guild.TextChannels.FirstOrDefault(c => c.Name == channelName);
            return channel?.Id;
        }

        public async Task StartAsync()
        {
            await _client.LoginAsync(TokenType.Bot, Globals.BotToken);
            await _client.StartAsync();
            _ = Task.Run(() => AutoReportLoop());
        }

        private async Task MessageReceivedAsync(SocketMessage message)
        {
            if (message.Author.IsBot || string.IsNullOrWhiteSpace(message.Content)) return;
            string content = message.Content.Trim();
            if (!content.StartsWith("!ttodo")) return;

            string arg1 = "";
            var parts = content.Split(new[] { ' ', '　', '\t', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) arg1 = parts[1].Trim();

            // ★変更: webコマンドは統合。誰が打っても同じURL（識別用パラメータ付き）を案内
            if (arg1.StartsWith("web", StringComparison.OrdinalIgnoreCase))
            {
                // ?uid=... は「私は誰か」をブラウザに教えるためだけに使う（表示フィルタ用）
                string url = $"{Globals.WebUrl}/?uid={message.Author.Id}";
                await message.Channel.SendMessageAsync($"🌍 **TToDo Board:**\n以下のURLからアクセスしてください:\n{url}");
                return;
            }

            if (arg1.StartsWith("list", StringComparison.OrdinalIgnoreCase)) await ShowCompactList(message.Channel, message.Author, arg1);
            else if (arg1.StartsWith("report", StringComparison.OrdinalIgnoreCase) || arg1.StartsWith("today", StringComparison.OrdinalIgnoreCase)) await ShowReport(message.Channel, message.Author, arg1);
            else if (arg1.StartsWith("close", StringComparison.OrdinalIgnoreCase)) await RunDailyClose(message.Author.Id, message.Channel);
            else if (arg1.StartsWith("trash", StringComparison.OrdinalIgnoreCase)) await ShowTrashList(message.Channel, message.Author);
            else if (arg1.StartsWith("config")) await HandleConfig(message.Channel, message.Author, arg1);
            else if (arg1.StartsWith("export", StringComparison.OrdinalIgnoreCase)) await ExportData(message.Channel, message.Author);
            else if (arg1.StartsWith("import")) await ImportData(message.Channel, content.Substring(content.IndexOf("import") + 6).Trim());
            else if (!string.IsNullOrWhiteSpace(arg1)) await AddNewTasks(message.Channel, message.Author, arg1);
        }

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

                if (isContinuation && pendingTask != null) pendingTask.Content += "\n" + trimLine;
                else
                {
                    if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
                    int prio = -1; int diff = -1; string content = trimLine;
                    if (content.StartsWith("!!") || content.StartsWith("！！")) { prio = 1; diff = 1; content = content.Substring(2); }
                    else if (content.StartsWith("??") || content.StartsWith("？？")) { prio = 0; diff = 0; content = content.Substring(2); }
                    else if (content.StartsWith("!") || content.StartsWith("！")) { prio = 1; diff = 0; content = content.Substring(1); }
                    else if (content.StartsWith("?") || content.StartsWith("？")) { prio = 0; diff = 1; content = content.Substring(1); }
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
            if (addedTasks.Count > 0) await ShowCompactList(channel, user, "");
        }

        // --- 以下変更なし (既存のメソッド群) ---
        private async Task ShowCompactList(ISocketMessageChannel channel, SocketUser user, string args) { var today = Globals.GetJstNow().Date; IEnumerable<TaskItem> query; lock (Globals.Lock) query = Globals.AllTasks.Where(t => !t.IsForgotten && (t.CompletedAt == null || t.CompletedAt >= today)).ToList(); bool isMine = args.Contains("mine"); bool isChannel = args.Contains("channel"); if (isMine) query = query.Where(t => t.UserId == user.Id); else if (isChannel) query = query.Where(t => t.ChannelId == channel.Id); else query = query.Where(t => t.UserId == user.Id && t.ChannelId == channel.Id); var visibleTasks = query.ToList(); if (visibleTasks.Count == 0) { await channel.SendMessageAsync("🎉 タスクはありません！"); return; } var sb = new StringBuilder(); sb.AppendLine($"📂 **タスク一覧 ({visibleTasks.Count}件):**"); var grouped = visibleTasks.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "📂 未分類").OrderBy(g => g.Key); foreach (var group in grouped) { sb.AppendLine($"\n**{group.Key}**"); foreach (var task in group.OrderByDescending(t => GetSortScore(t))) { string label = GetPriorityLabel(task.Priority, task.Difficulty); string state = task.CompletedAt != null ? "✅ " : (task.IsSnoozed ? "💤 " : ""); string display = task.Content.Split('\n')[0]; if (display.Length > 20) display = display.Substring(0, 20) + "..."; sb.AppendLine($"`[{label}]` {state}{display}"); } } var menuBuilder = new SelectMenuBuilder().WithCustomId("main_task_selector").WithPlaceholder("▼ 操作...").WithMinValues(1).WithMaxValues(1); foreach (var task in visibleTasks.Take(25)) { string label = GetPriorityLabel(task.Priority, task.Difficulty); string contentLabel = task.Content.Replace("\n", " "); if (contentLabel.Length > 45) contentLabel = contentLabel.Substring(0, 42) + "..."; if (task.CompletedAt != null) contentLabel = "✅ " + contentLabel; else if (task.IsSnoozed) contentLabel = "💤 " + contentLabel; menuBuilder.AddOption($"[{label}] {contentLabel}", task.Id, task.Tags.Count > 0 ? $"[{task.Tags[0]}]" : "未分類"); } var builder = new ComponentBuilder().WithSelectMenu(menuBuilder).WithButton("優先度", "start_sort_p", ButtonStyle.Secondary, null, row: 1).WithButton("タグ", "start_sort_t", ButtonStyle.Secondary, null, row: 1); await channel.SendMessageAsync(sb.ToString(), components: builder.Build()); }
        private async Task ShowTrashList(ISocketMessageChannel c, SocketUser u) { List<TaskItem> l; lock (Globals.Lock) l = Globals.AllTasks.Where(t => t.UserId == u.Id && t.IsForgotten).ToList(); if (l.Count == 0) { await c.SendMessageAsync("🗑️ ゴミ箱は空です。"); return; } var menu = new SelectMenuBuilder().WithCustomId("trash_task_selector").WithPlaceholder("▼ 操作...").WithMinValues(1).WithMaxValues(1); foreach (var t in l.Take(25)) menu.AddOption(t.Content.Length > 50 ? t.Content.Substring(0, 47) + "..." : t.Content, t.Id, "忘却中"); await c.SendMessageAsync($"🗑️ **ゴミ箱 ({l.Count}件)**", components: new ComponentBuilder().WithSelectMenu(menu).Build()); }
        private async Task SelectMenuHandler(SocketMessageComponent component) { try { string id = component.Data.CustomId; string val = component.Data.Values.First(); TaskItem? task; lock (Globals.Lock) task = Globals.AllTasks.FirstOrDefault(t => t.Id == val); if (id == "main_task_selector") { if (task == null) { await component.RespondAsync("エラー: なし", ephemeral: true); return; } string l = GetPriorityLabel(task.Priority, task.Difficulty); string status = task.CompletedAt != null ? "(完了済)" : ""; string txt = $"🛠️ **操作:** {status}\n[{l}] **{task.Content}**"; var b = new ComponentBuilder().WithButton(null, $"done_{task.Id}", ButtonStyle.Secondary, new Emoji("✅")).WithButton(null, $"snooze_{task.Id}", ButtonStyle.Secondary, new Emoji("💤")).WithButton(null, $"forget_{task.Id}", ButtonStyle.Secondary, new Emoji("🗑️")).WithButton(null, $"edit_{task.Id}", ButtonStyle.Secondary, new Emoji("⚙️")); await component.RespondAsync(txt, components: b.Build(), ephemeral: true); } else if (id == "trash_task_selector") { if (task == null) { await component.RespondAsync("エラー: なし", ephemeral: true); return; } string txt = $"🗑️ **ゴミ箱:** {task.Content}"; var b = new ComponentBuilder().WithButton("↩️ 復活", $"recall_{task.Id}", ButtonStyle.Primary).WithButton("💥 抹消", $"obliterate_{task.Id}", ButtonStyle.Danger); await component.RespondAsync(txt, components: b.Build(), ephemeral: true); } else if (id.StartsWith("tag_sel_")) { var parts = id.Substring(8).Split('_'); var t = Globals.AllTasks.FirstOrDefault(x => x.Id == parts[0]); if (t != null) { lock (Globals.Lock) { t.Tags.Clear(); if (!string.IsNullOrEmpty(val)) t.Tags.Add(val); Globals.SaveData(); } if (parts.Length > 1 && parts[1] != "normal") { var next = FindNextSortTarget(component.User.Id, parts[1]); if (next != null) await ShowSortCard(component, next, parts[1], false); else await component.RespondAsync("🎉 完了", ephemeral: true); } else await component.RespondAsync($"✅ タグ: [{val}]", ephemeral: true); } } } catch { } }
        private async Task ButtonHandler(SocketMessageComponent component) { try { string id = component.Data.CustomId; if (id == "start_sort_p") { await StartSortingSession(component, "p"); return; } if (id == "start_sort_t") { await StartSortingSession(component, "t"); return; } if (id.StartsWith("set_")) { await HandleAttributeSet(component); return; } if (id.StartsWith("tag_edit_")) { await ShowTagSelector(component); return; } if (id.StartsWith("tag_create_")) { await ShowTagModal(component); return; } string tid = id.Substring(id.IndexOf('_') + 1); TaskItem? task; lock (Globals.Lock) task = Globals.AllTasks.FirstOrDefault(x => x.Id == tid); if (task == null) { await component.RespondAsync("⚠️ なし", ephemeral: true); return; } if (id.StartsWith("done_")) { lock (Globals.Lock) { task.CompletedAt = Globals.GetJstNow(); task.IsSnoozed = false; Globals.SaveData(); } await component.UpdateAsync(x => { x.Content = $"✅ ~~{task.Content}~~ (完了)"; x.Components = null; }); } else if (id.StartsWith("snooze_")) { lock (Globals.Lock) { task.IsSnoozed = !task.IsSnoozed; Globals.SaveData(); } await component.UpdateAsync(x => { x.Content = $"💤 ~~{task.Content}~~"; x.Components = null; }); } else if (id.StartsWith("forget_")) { lock (Globals.Lock) { task.IsForgotten = true; task.IsSnoozed = false; Globals.SaveData(); } await component.UpdateAsync(x => { x.Content = $"🗑️ ~~{task.Content}~~"; x.Components = null; }); } else if (id.StartsWith("recall_")) { lock (Globals.Lock) { task.IsForgotten = false; Globals.SaveData(); } await component.UpdateAsync(x => { x.Content = $"↩️ 復活: {task.Content}"; x.Components = null; }); } else if (id.StartsWith("obliterate_")) { lock (Globals.Lock) { Globals.AllTasks.Remove(task); Globals.SaveData(); } await component.UpdateAsync(x => { x.Content = $"💥 抹消: {task.Content}"; x.Components = null; }); } else if (id.StartsWith("edit_")) { await ShowEditPanel(component, task); } } catch { } }
        private async Task ModalHandler(SocketModal m) { try { if (!m.Data.CustomId.StartsWith("modal_tag_")) return; string rid = m.Data.CustomId.Substring(10); var ps = rid.Split('_'); TaskItem? t; lock (Globals.Lock) t = Globals.AllTasks.FirstOrDefault(x => x.Id == ps[0]); if (t != null) { string v = m.Data.Components.First().Value.Trim(); lock (Globals.Lock) { t.Tags.Clear(); if (v != "") t.Tags.Add(v); Globals.SaveData(); } } string mode = ps.Length > 1 ? ps[1] : "normal"; if (mode == "normal") await m.RespondAsync($"✅ 更新: {t?.Content}", ephemeral: true); else { var next = FindNextSortTarget(m.User.Id, mode); if (next != null) await ShowSortCard(m, next, mode, false); else await m.RespondAsync("🎉 完了", ephemeral: true); } } catch { } }
        private int GetSortScore(TaskItem t) { if (t.CompletedAt != null) return -10; if (t.IsSnoozed) return -1; if (t.Priority == 1 && t.Difficulty == 1) return 10; if (t.Priority == 1) return 9; if (t.Priority == -1) return 5; if (t.Priority == 0 && t.Difficulty == 1) return 2; return 1; }
        private string GetPriorityLabel(int p, int d) { if (p == 1 && d == 1) return "1"; if (p == 1 && d == 0) return "2"; if (p == 0 && d == 1) return "3"; if (p == 0 && d == 0) return "4"; return "-"; }
        private TaskItem? FindNextSortTarget(ulong u, string m) { lock (Globals.Lock) return Globals.AllTasks.FirstOrDefault(t => t.UserId == u && !t.IsForgotten && t.CompletedAt == null && (m == "p" ? t.Priority == -1 : t.Tags.Count == 0)); }
        private async Task ShowEditPanel(SocketMessageComponent c, TaskItem t) { var b = new ComponentBuilder().WithButton("1", $"set_1_1_{t.Id}_n", ButtonStyle.Secondary).WithButton("2", $"set_1_0_{t.Id}_n", ButtonStyle.Secondary).WithButton("3", $"set_0_1_{t.Id}_n", ButtonStyle.Secondary).WithButton("4", $"set_0_0_{t.Id}_n", ButtonStyle.Secondary).WithButton(null, $"tag_edit_{t.Id}_n", ButtonStyle.Secondary, new Emoji("🏷️")); await c.RespondAsync($"🔧 **編集:** {t.Content}", components: b.Build(), ephemeral: true); }
        private async Task StartSortingSession(SocketMessageComponent c, string m) { var t = FindNextSortTarget(c.User.Id, m); if (t == null) { await c.RespondAsync("🎉 完了", ephemeral: true); return; } await ShowSortCard(c, t, m, false); }
        private async Task ShowSortCard(SocketInteraction i, TaskItem t, string m, bool up) { string txt = $"**仕分け:** {t.Content}"; var b = new ComponentBuilder().WithButton("1", $"set_1_1_{t.Id}_{m}", ButtonStyle.Secondary).WithButton("2", $"set_1_0_{t.Id}_{m}", ButtonStyle.Secondary).WithButton("3", $"set_0_1_{t.Id}_{m}", ButtonStyle.Secondary).WithButton("4", $"set_0_0_{t.Id}_{m}", ButtonStyle.Secondary).WithButton(null, $"tag_edit_{t.Id}_{m}", ButtonStyle.Secondary, new Emoji("🏷️")); if (up && i is SocketMessageComponent c) await c.UpdateAsync(x => { x.Content = txt; x.Components = b.Build(); }); else await i.RespondAsync(txt, components: b.Build(), ephemeral: true); }
        private async Task HandleAttributeSet(SocketMessageComponent c) { var p = c.Data.CustomId.Split('_'); TaskItem? t; lock (Globals.Lock) t = Globals.AllTasks.FirstOrDefault(x => x.Id == p[3]); if (t != null) { lock (Globals.Lock) { t.Priority = int.Parse(p[1]); t.Difficulty = int.Parse(p[2]); Globals.SaveData(); } } var next = FindNextSortTarget(c.User.Id, p.Length > 4 ? p[4] : "p"); if (next != null) await ShowSortCard(c, next, p[4], true); else await c.UpdateAsync(x => { x.Content = "🎉 完了"; x.Components = null; }); }
        private async Task ShowTagSelector(SocketMessageComponent c) { string rid = c.Data.CustomId.Substring(9); var ps = rid.Split('_'); TaskItem? t; lock (Globals.Lock) t = Globals.AllTasks.FirstOrDefault(x => x.Id == ps[0]); if (t == null) return; List<string> tags; lock (Globals.Lock) tags = Globals.AllTasks.Where(x => x.UserId == c.User.Id).SelectMany(x => x.Tags).Distinct().Take(25).ToList(); if (tags.Count == 0) { await ShowTagModal(c); return; } var menu = new SelectMenuBuilder().WithCustomId($"tag_sel_{ps[0]}_{ps[1]}").WithPlaceholder("タグ選択"); foreach (var tag in tags) menu.AddOption(tag, tag); var b = new ComponentBuilder().WithSelectMenu(menu).WithButton(null, $"tag_create_{ps[0]}_{ps[1]}", ButtonStyle.Primary, new Emoji("➕")); await c.RespondAsync($"🏷️ {t.Content}", components: b.Build(), ephemeral: true); }
        private async Task ShowTagModal(SocketMessageComponent c) { string rid = c.Data.CustomId.Substring(11); await c.RespondWithModalAsync(new ModalBuilder().WithTitle("新規タグ").WithCustomId($"modal_tag_{rid}").AddTextInput("タグ名", "n", value: "", required: true).Build()); }
        private async Task ShowReport(ISocketMessageChannel c, SocketUser u, string a) { var d = Globals.GetJstNow().Date; string p = "Today"; if (a.Contains("yesterday")) { d = d.AddDays(-1); p = "Yesterday"; } else if (a.Contains("week")) { d = d.AddDays(-7); p = "Last Week"; } List<TaskItem> l; lock (Globals.Lock) l = Globals.AllTasks.Where(x => x.UserId == u.Id && x.CompletedAt >= d).OrderBy(x => x.CompletedAt).ToList(); if (l.Count == 0) { await c.SendMessageAsync($"📭 {p}の完了タスクなし"); return; } var sb = new StringBuilder(); sb.AppendLine($"📊 **Task Report: {u.Username}** ({p})"); sb.AppendLine($"**{l.Count}件** 完了！"); sb.AppendLine("```text"); foreach (var t in l) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}"); sb.AppendLine("```"); await c.SendMessageAsync(sb.ToString()); }
        private async Task HandleConfig(ISocketMessageChannel c, SocketUser u, string a) { if (a.Contains("time")) { var ps = a.Split(' '); if (ps.Length < 3) return; lock (Globals.Lock) { var conf = Globals.Configs.FirstOrDefault(x => x.UserId == u.Id); if (conf == null) { conf = new UserConfig { UserId = u.Id }; Globals.Configs.Add(conf); } conf.ReportTime = ps[2]; Globals.SaveData(); } await c.SendMessageAsync($"⏰ 日報: {ps[2]}"); } }
        private async Task ExportData(ISocketMessageChannel c, SocketUser u) { string j; lock (Globals.Lock) j = JsonSerializer.Serialize(Globals.AllTasks.Where(x => x.UserId == u.Id), new JsonSerializerOptions { WriteIndented = true }); if (j.Length > 1900) { File.WriteAllText("e.json", j); await c.SendFileAsync("e.json"); } else await c.SendMessageAsync($"```json\n{j}\n```"); }
        private async Task ImportData(ISocketMessageChannel c, string j) { try { var l = JsonSerializer.Deserialize<List<TaskItem>>(j); if (l != null) { lock (Globals.Lock) { foreach (var i in l) { Globals.AllTasks.RemoveAll(x => x.Id == i.Id); Globals.AllTasks.Add(i); } Globals.SaveData(); } await c.SendMessageAsync($"📥 {l.Count}件"); } } catch { } }

        private async Task RunDailyClose(ulong userId, ISocketMessageChannel feedbackChannel = null)
        {
            var now = Globals.GetJstNow();

            // 1. 古いデータを削除
            var deleteThreshold = now.Date.AddDays(-1);
            lock (Globals.Lock)
            {
                int removedCount = Globals.AllTasks.RemoveAll(t => t.CompletedAt != null && t.CompletedAt < deleteThreshold);
                if (removedCount > 0) Globals.SaveData();
            }

            // 2. レポート対象を取得
            DateTime reportStart;
            if (now.Hour == 0 && now.Minute < 5)
            {
                reportStart = now.Date.AddDays(-1);
            }
            else
            {
                reportStart = now.Date;
            }

            List<TaskItem> reportTasks;
            lock (Globals.Lock)
            {
                reportTasks = Globals.AllTasks
                    .Where(t => t.UserId == userId && t.CompletedAt != null && t.CompletedAt >= reportStart)
                    .ToList();
            }

            if (reportTasks.Count == 0)
            {
                if (feedbackChannel != null) await feedbackChannel.SendMessageAsync("本日の完了タスクはありません。");
                return;
            }

            var user = _client.GetUser(userId);
            string userName = user?.Username ?? "User";

            // チャンネルごとにグループ化して送信
            var channelGroups = reportTasks.GroupBy(t => t.ChannelId);

            foreach (var group in channelGroups)
            {
                try
                {
                    ulong chId = group.Key;
                    var targetChannel = _client.GetChannel(chId) as ISocketMessageChannel;
                    if (targetChannel == null) try { targetChannel = await _client.GetChannelAsync(chId) as ISocketMessageChannel; } catch { }

                    if (targetChannel == null && feedbackChannel != null) targetChannel = feedbackChannel;
                    if (targetChannel == null) continue;

                    var sb = new StringBuilder();
                    sb.AppendLine($"🌅 **Daily Report: {userName}** ({Globals.GetJstNow():yyyy/MM/dd})");
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
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] Report send failed for channel {group.Key}: {ex.Message}");
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
    }
}
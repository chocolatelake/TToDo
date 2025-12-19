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
        private static readonly List<string> DefaultPrefixes = new List<string> { " ", "　", "\t", "→", "：", ":", "・", "※", ">", "-", "+", "*", "■", "□", "●", "○" };
        private static readonly List<string> TagPrefixes = new List<string> { "#", "＃" };

        public DiscordBot()
        {
            var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent };
            _client = new DiscordSocketClient(config);
            _client.Log += msg => { Console.WriteLine($"[Bot] {msg}"); return Task.CompletedTask; };
            _client.MessageReceived += MessageReceivedAsync;
            _client.SelectMenuExecuted += SelectMenuHandler;
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

            string arg1 = content.Replace("!ttodo", "").Trim();
            if (arg1.StartsWith("list", StringComparison.OrdinalIgnoreCase)) await ShowCompactList(message.Channel, message.Author, arg1);
            else if (arg1.StartsWith("report", StringComparison.OrdinalIgnoreCase)) await ShowReport(message.Channel, message.Author, arg1);
            else if (arg1.StartsWith("close", StringComparison.OrdinalIgnoreCase)) await RunDailyClose(message.Author.Id, message.Channel);
            else if (arg1.StartsWith("trash", StringComparison.OrdinalIgnoreCase)) await ShowTrashList(message.Channel, message.Author);
            else if (!string.IsNullOrWhiteSpace(arg1)) await AddNewTasks(message.Channel, message.Author, arg1);
        }

        private async Task AddNewTasks(ISocketMessageChannel channel, SocketUser user, string rawText)
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
                    if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); pendingTask = null; }
                    currentTags.Clear(); continue;
                }
                if (trimLine.StartsWith("#") || trimLine.StartsWith("＃")) { currentTags = new List<string> { trimLine.Substring(1).Trim() }; continue; }
                if ((line.StartsWith(" ") || line.StartsWith("　")) && pendingTask != null) { pendingTask.Content += "\n" + trimLine; }
                else
                {
                    if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
                    pendingTask = new TaskItem { ChannelId = channel.Id, UserId = user.Id, Content = trimLine, Tags = new List<string>(currentTags) };
                }
            }
            if (pendingTask != null) { lock (Globals.Lock) Globals.AllTasks.Add(pendingTask); addedTasks.Add(pendingTask); }
            Globals.SaveData();
            if (addedTasks.Count > 0) await ShowCompactList(channel, user, "");
        }

        private async Task ShowCompactList(ISocketMessageChannel channel, SocketUser user, string args)
        {
            await RefreshListAsync(channel, user.Id);
        }

        private async Task RefreshListAsync(ISocketMessageChannel channel, ulong userId, SocketMessageComponent component = null)
        {
            var today = Globals.GetJstNow().Date;
            List<TaskItem> visibleTasks;
            lock (Globals.Lock) visibleTasks = Globals.AllTasks.Where(t => !t.IsForgotten && t.UserId == userId && (t.CompletedAt == null || t.CompletedAt >= today)).ToList();

            string content = BuildListText(visibleTasks);
            var components = BuildComponents(visibleTasks);

            if (component != null) await component.UpdateAsync(x => { x.Content = content; x.Components = components; });
            else await channel.SendMessageAsync(content, components: components);
        }

        private string BuildListText(List<TaskItem> tasks)
        {
            var incompleteTasks = tasks.Where(t => t.CompletedAt == null).ToList();
            if (incompleteTasks.Count == 0) return "🎉 タスクはありません！";
            var sb = new StringBuilder();
            sb.AppendLine($"📂 **タスク一覧 ({incompleteTasks.Count}件):**");
            foreach (var task in incompleteTasks) { sb.AppendLine($"・{task.Content}"); }
            return sb.ToString();
        }

        private MessageComponent BuildComponents(List<TaskItem> tasks)
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

        private async Task SelectMenuHandler(SocketMessageComponent component)
        {
            try
            {
                string id = component.Data.CustomId;
                string val = component.Data.Values.First();
                TaskItem? task; lock (Globals.Lock) task = Globals.AllTasks.FirstOrDefault(t => t.Id == val);
                if (task != null)
                {
                    if (id == "sel_done") { lock (Globals.Lock) { task.CompletedAt = task.CompletedAt == null ? Globals.GetJstNow() : null; Globals.SaveData(); } }
                    else if (id == "sel_archive") { lock (Globals.Lock) { task.IsForgotten = true; Globals.SaveData(); } }
                    else if (id == "sel_restore") { lock (Globals.Lock) { task.IsForgotten = false; Globals.SaveData(); } }
                }
                if (id == "sel_restore") await ShowTrashList(component.Channel, component.User);
                else await RefreshListAsync(component.Channel, component.User.Id, component);
            }
            catch { }
        }

        private async Task ShowTrashList(ISocketMessageChannel c, SocketUser u)
        {
            List<TaskItem> l; lock (Globals.Lock) l = Globals.AllTasks.Where(t => t.UserId == u.Id && t.IsForgotten).ToList();
            if (l.Count == 0) { await c.SendMessageAsync("🗑️ 空です"); return; }
            var menu = new SelectMenuBuilder().WithCustomId("sel_restore").WithPlaceholder("復活させる...").WithMinValues(1).WithMaxValues(1);
            foreach (var t in l.Take(25)) menu.AddOption(t.Content.Length > 50 ? t.Content.Substring(0, 47) + "..." : t.Content, t.Id);
            await c.SendMessageAsync($"🗑️ **ゴミ箱 ({l.Count}件)**", components: new ComponentBuilder().WithSelectMenu(menu).Build());
        }

        private async Task ShowReport(ISocketMessageChannel c, SocketUser u, string a)
        {
            var d = Globals.GetJstNow().Date;
            List<TaskItem> l; lock (Globals.Lock) l = Globals.AllTasks.Where(x => x.UserId == u.Id && x.CompletedAt >= d).ToList();
            if (l.Count == 0) { await c.SendMessageAsync($"📭 Todayの完了タスクなし"); return; }
            await c.SendMessageAsync($"📊 **Report:** {l.Count}件完了");
        }

        private async Task RunDailyClose(ulong userId, ISocketMessageChannel feedbackChannel = null)
        {
            List<TaskItem> doneTasks; lock (Globals.Lock) doneTasks = Globals.AllTasks.Where(t => t.UserId == userId && t.CompletedAt != null).ToList();
            if (doneTasks.Count == 0) { if (feedbackChannel != null) await feedbackChannel.SendMessageAsync("本日の完了タスクはありません。"); return; }
            var user = _client.GetUser(userId); string userName = user?.Username ?? "User";
            var tasksToRemove = new List<TaskItem>();
            var channelGroups = doneTasks.GroupBy(t => t.ChannelId);
            foreach (var group in channelGroups)
            {
                ulong chId = group.Key; var targetChannel = _client.GetChannel(chId) as ISocketMessageChannel;
                if (targetChannel == null) try { targetChannel = await _client.GetChannelAsync(chId) as ISocketMessageChannel; } catch { }
                if (targetChannel == null) continue;
                var sb = new StringBuilder(); sb.AppendLine($"🌅 **Daily Report: {userName}** ({Globals.GetJstNow():yyyy/MM/dd})");
                sb.AppendLine($"**{group.Count()}件** 完了！"); sb.AppendLine("```");
                foreach (var tGroup in group.GroupBy(t => t.Tags.Count > 0 ? t.Tags[0] : "その他")) { sb.AppendLine($"【{tGroup.Key}】"); foreach (var t in tGroup) sb.AppendLine($"・[{t.CompletedAt:HH:mm}] {t.Content}"); sb.AppendLine(); }
                sb.AppendLine("```"); try { await targetChannel.SendMessageAsync(sb.ToString()); tasksToRemove.AddRange(group); } catch { }
            }
            lock (Globals.Lock) { foreach (var t in tasksToRemove) Globals.AllTasks.Remove(t); Globals.SaveData(); }
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
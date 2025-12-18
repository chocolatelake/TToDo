using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// タスク情報クラス
public class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; }
    public bool IsSnoozed { get; set; }
}

class Program
{
    // 非タスク化文字（初期設定）
    private static readonly List<string> DefaultPrefixes = new List<string>
    {
        " ", "　", "\t",
        "→", "：", ":", "・"
    };

    private DiscordSocketClient _client;

    // ユーザーごとのデータ
    private static Dictionary<ulong, List<TaskItem>> _userTasks = new Dictionary<ulong, List<TaskItem>>();
    private static Dictionary<ulong, HashSet<string>> _userPrefixes = new Dictionary<ulong, HashSet<string>>();

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

        LoadTasksFromFile();
        LoadPrefixesFromFile();

        await Task.Delay(-1);
    }

    private async Task MessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        // --- 設定コマンド (!tset) ---
        if (message.Content.StartsWith("!tset "))
        {
            await HandleConfigCommand(message);
            return;
        }

        // --- ToDoコマンド (!ttodo) ---
        if (message.Content.StartsWith("!ttodo"))
        {
            ulong userId = message.Author.Id;
            string allContent = message.Content.Substring(6);

            var prefixes = GetUserPrefixes(userId);
            string[] rawLines = allContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var newTasksToAdd = new List<string>();
            StringBuilder currentTaskBuffer = new StringBuilder();

            foreach (var line in rawLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                bool isContinuation = false;
                foreach (var prefix in prefixes)
                {
                    if (line.StartsWith(prefix))
                    {
                        isContinuation = true;
                        break;
                    }
                }

                if (currentTaskBuffer.Length == 0)
                {
                    currentTaskBuffer.Append(line.Trim());
                }
                else
                {
                    if (isContinuation)
                    {
                        currentTaskBuffer.Append("\n" + line);
                    }
                    else
                    {
                        newTasksToAdd.Add(currentTaskBuffer.ToString());
                        currentTaskBuffer.Clear();
                        currentTaskBuffer.Append(line.Trim());
                    }
                }
            }
            if (currentTaskBuffer.Length > 0) newTasksToAdd.Add(currentTaskBuffer.ToString());


            if (!_userTasks.ContainsKey(userId)) _userTasks[userId] = new List<TaskItem>();

            // 1. 新規追加
            foreach (var taskText in newTasksToAdd)
            {
                var newItem = new TaskItem { Name = taskText, IsSnoozed = false };
                _userTasks[userId].Add(newItem);

                await SendTaskPanel(message.Channel, newItem);
                await Task.Delay(500);
            }

            // 2. 後回し復活
            var snoozedTasks = _userTasks[userId].Where(t => t.IsSnoozed).ToList();
            if (snoozedTasks.Count > 0)
            {
                await message.Channel.SendMessageAsync("👀 **後回し分:**");
                foreach (var task in snoozedTasks)
                {
                    task.IsSnoozed = false;
                    await SendTaskPanel(message.Channel, task);
                    await Task.Delay(500);
                }
            }

            SaveTasksToFile();
        }
    }

    private async Task HandleConfigCommand(SocketMessage message)
    {
        ulong userId = message.Author.Id;
        // "!tset cmd arg"
        string[] parts = message.Content.Split(' ', 3);

        if (parts.Length < 2) return;
        string command = parts[1].ToLower();
        string arg = parts.Length > 2 ? parts[2] : "";

        // 設定ロード済みか確認（なければコピー）
        if (!_userPrefixes.ContainsKey(userId))
        {
            _userPrefixes[userId] = new HashSet<string>(DefaultPrefixes);
        }

        switch (command)
        {
            case "list":
                // 現在の設定を「|」区切りでコマンド形式にする
                // タブ文字(\t)は見えないので、編集しやすいよう "\\t" という文字に変換して表示する
                var displayList = _userPrefixes[userId].Select(p => p.Replace("\t", "\\t"));
                string cmdStr = "!tset setall |" + string.Join("|", displayList);

                await message.Channel.SendMessageAsync(
                    "🔧 **一括編集モード**\n" +
                    "以下のコマンドをコピーし、`|` の間に好きな文字を追加・削除して送信してください。\n" +
                    "(スペースやタブも `|` で囲むことで認識されます)\n" +
                    $"```text\n{cmdStr}\n```");
                break;

            case "setall":
                // "|a|b|c" の形式を受け取って一括置換する
                if (string.IsNullOrEmpty(arg))
                {
                    await message.Channel.SendMessageAsync("⚠️ 設定する文字がありません。");
                    return;
                }

                // パイプで分割
                var newSet = arg.Split('|', StringSplitOptions.RemoveEmptyEntries);

                _userPrefixes[userId].Clear();
                foreach (var s in newSet)
                {
                    // "\\t" という文字を、本物のタブ文字に戻す
                    string realChar = s.Replace("\\t", "\t");
                    _userPrefixes[userId].Add(realChar);
                }

                SavePrefixesToFile();
                await message.Channel.SendMessageAsync($"✅ 設定を一括更新しました！ ({_userPrefixes[userId].Count}個)");
                break;

            case "add":
                if (string.IsNullOrEmpty(arg))
                {
                    await message.Channel.SendMessageAsync("⚠️ 追加する文字を指定してください");
                    return;
                }
                _userPrefixes[userId].Add(arg);
                SavePrefixesToFile();
                await message.Channel.SendMessageAsync($"✅ `{arg}` を追加しました。");
                break;

            case "del":
                if (string.IsNullOrEmpty(arg))
                {
                    await message.Channel.SendMessageAsync("⚠️ 削除する文字を指定してください");
                    return;
                }
                if (_userPrefixes[userId].Contains(arg))
                {
                    _userPrefixes[userId].Remove(arg);
                    SavePrefixesToFile();
                    await message.Channel.SendMessageAsync($"🗑️ `{arg}` を削除しました。");
                }
                else
                {
                    await message.Channel.SendMessageAsync($"⚠️ `{arg}` は設定に含まれていません。");
                }
                break;

            case "reset":
                _userPrefixes[userId] = new HashSet<string>(DefaultPrefixes);
                SavePrefixesToFile();
                await message.Channel.SendMessageAsync("🔄 設定をリセットしました。");
                break;
        }
    }

    private HashSet<string> GetUserPrefixes(ulong userId)
    {
        if (_userPrefixes.ContainsKey(userId)) return _userPrefixes[userId];
        return new HashSet<string>(DefaultPrefixes);
    }

    private async Task SendTaskPanel(ISocketMessageChannel channel, TaskItem item)
    {
        var builder = new ComponentBuilder()
            .WithButton("完了！", $"done_{item.Id}", ButtonStyle.Success)
            .WithButton("後回し", $"snooze_{item.Id}", ButtonStyle.Secondary);

        await channel.SendMessageAsync($"📝 **ToDo:**\n{item.Name}", components: builder.Build());
    }

    private async Task ButtonHandler(SocketMessageComponent component)
    {
        string customId = component.Data.CustomId;
        string targetId = customId.Substring(customId.IndexOf('_') + 1);
        ulong userId = component.User.Id;

        if (!_userTasks.ContainsKey(userId)) return;

        var targetTask = _userTasks[userId].FirstOrDefault(t => t.Id == targetId);

        if (targetTask == null && !customId.StartsWith("snooze_"))
        {
            await component.RespondAsync("⚠️ タスクが見つかりません。", ephemeral: true);
            return;
        }

        if (customId.StartsWith("done_"))
        {
            if (targetTask != null) _userTasks[userId].Remove(targetTask);
            SaveTasksToFile();

            string taskName = targetTask?.Name ?? "完了済みタスク";
            await component.UpdateAsync(x =>
            {
                x.Content = $"✅ ~~{taskName}~~";
                x.Components = null;
            });
        }
        else if (customId.StartsWith("snooze_"))
        {
            if (targetTask != null)
            {
                targetTask.IsSnoozed = true;
                SaveTasksToFile();

                await component.UpdateAsync(x =>
                {
                    x.Content = $"💤 ~~{targetTask.Name}~~";
                    x.Components = new ComponentBuilder()
                        .WithButton("完了！", $"done_{targetTask.Id}", ButtonStyle.Success)
                        .Build();
                });
            }
        }
    }

    private void SaveTasksToFile()
    {
        var lines = new List<string>();
        foreach (var user in _userTasks)
        {
            foreach (var task in user.Value)
            {
                string safeName = task.Name.Replace("\n", "<<NL>>").Replace("\r", "");
                lines.Add($"{user.Key}|{task.IsSnoozed}|{task.Id}|{safeName}");
            }
        }
        File.WriteAllLines("tasks.txt", lines);
    }

    private void LoadTasksFromFile()
    {
        if (!File.Exists("tasks.txt")) return;
        string[] lines = File.ReadAllLines("tasks.txt");
        _userTasks.Clear();
        foreach (var line in lines)
        {
            var parts = line.Split('|', 4);
            if (parts.Length < 4) continue;
            if (ulong.TryParse(parts[0], out ulong userId))
            {
                bool isSnoozed = bool.Parse(parts[1]);
                string id = parts[2];
                string name = parts[3].Replace("<<NL>>", "\n");
                if (!_userTasks.ContainsKey(userId)) _userTasks[userId] = new List<TaskItem>();
                _userTasks[userId].Add(new TaskItem { Id = id, Name = name, IsSnoozed = isSnoozed });
            }
        }
        Console.WriteLine("Tasks loaded.");
    }

    private void SavePrefixesToFile()
    {
        var lines = new List<string>();
        foreach (var kvp in _userPrefixes)
        {
            string joined = string.Join("\t", kvp.Value);
            lines.Add($"{kvp.Key}|{joined}");
        }
        File.WriteAllLines("prefixes.txt", lines);
    }

    private void LoadPrefixesFromFile()
    {
        if (!File.Exists("prefixes.txt")) return;
        string[] lines = File.ReadAllLines("prefixes.txt");
        _userPrefixes.Clear();
        foreach (var line in lines)
        {
            var parts = line.Split('|', 2);
            if (parts.Length < 2) continue;
            if (ulong.TryParse(parts[0], out ulong userId))
            {
                var prefixes = parts[1].Split('\t');
                _userPrefixes[userId] = new HashSet<string>(prefixes);
            }
        }
        Console.WriteLine("Settings loaded.");
    }

    private Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }
}
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
    public bool IsSnoozed { get; set; }   // 後回しフラグ
    public bool IsForgotten { get; set; } // ★追加: 忘却フラグ
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
            string allContent = message.Content.Substring(6).Trim();

            // データ初期化
            if (!_userTasks.ContainsKey(userId)) _userTasks[userId] = new List<TaskItem>();

            // --- コマンド分岐 ---

            // 1. バックアップ (!ttodo backup)
            if (allContent.Equals("backup", StringComparison.OrdinalIgnoreCase))
            {
                await ShowBackup(message.Channel, userId);
                return;
            }

            // 2. 現在のリスト表示 (!ttodo list)
            if (allContent.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                await ShowActiveList(message.Channel, userId);
                return;
            }

            // 3. 忘れたリスト表示 (!ttodo forgot)
            if (allContent.Equals("forgot", StringComparison.OrdinalIgnoreCase) ||
                allContent.Equals("forgotten", StringComparison.OrdinalIgnoreCase))
            {
                await ShowForgottenList(message.Channel, userId);
                return;
            }

            // 4. 通常のタスク追加処理
            await AddNewTasks(message, userId, message.Content.Substring(6)); // Trimなしの生テキストを渡す
        }
    }

    // --- コマンド処理メソッド群 ---

    private async Task ShowBackup(ISocketMessageChannel channel, ulong userId)
    {
        if (_userTasks[userId].Count == 0)
        {
            await channel.SendMessageAsync("⚠️ タスクはありません。");
            return;
        }
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("!ttodo");
        // 忘却フラグが立っていないものだけバックアップするのが一般的だが、
        // 全データ保全のため、ここでは全て出力する（必要ならフィルタしてください）
        foreach (var task in _userTasks[userId])
        {
            sb.AppendLine(task.Name);
        }
        await channel.SendMessageAsync($"📦 **全タスク バックアップ:**\n```text\n{sb.ToString()}\n```");
    }

    private async Task ShowActiveList(ISocketMessageChannel channel, ulong userId)
    {
        // 忘れてない、かつスヌーズしてないタスク
        var activeTasks = _userTasks[userId]
            .Where(t => !t.IsForgotten && !t.IsSnoozed).ToList();

        if (activeTasks.Count == 0)
        {
            await channel.SendMessageAsync("🎉 現在、抱えているタスクはありません！");
            return;
        }

        await channel.SendMessageAsync("📂 **現在のToDo一覧:**");
        foreach (var task in activeTasks)
        {
            await SendTaskPanel(channel, task);
            await Task.Delay(500);
        }
    }

    private async Task ShowForgottenList(ISocketMessageChannel channel, ulong userId)
    {
        // 忘却フラグが立っているタスク
        var forgottenTasks = _userTasks[userId].Where(t => t.IsForgotten).ToList();

        if (forgottenTasks.Count == 0)
        {
            await channel.SendMessageAsync("📭 「忘れた」タスクはありません。");
            return;
        }

        await channel.SendMessageAsync("🌫️ **忘却の彼方にあるタスク一覧:**");
        foreach (var task in forgottenTasks)
        {
            // 忘却専用パネルを送る
            await SendForgottenPanel(channel, task);
            await Task.Delay(500);
        }
    }

    private async Task AddNewTasks(SocketMessage message, ulong userId, string rawContent)
    {
        var prefixes = GetUserPrefixes(userId);
        string[] rawLines = rawContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var newTasksToAdd = new List<string>();
        StringBuilder currentTaskBuffer = new StringBuilder();

        foreach (var line in rawLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            bool isContinuation = false;
            foreach (var prefix in prefixes)
            {
                if (line.StartsWith(prefix)) { isContinuation = true; break; }
            }

            if (currentTaskBuffer.Length == 0)
            {
                currentTaskBuffer.Append(line.Trim());
            }
            else
            {
                if (isContinuation) currentTaskBuffer.Append("\n" + line);
                else
                {
                    newTasksToAdd.Add(currentTaskBuffer.ToString());
                    currentTaskBuffer.Clear();
                    currentTaskBuffer.Append(line.Trim());
                }
            }
        }
        if (currentTaskBuffer.Length > 0) newTasksToAdd.Add(currentTaskBuffer.ToString());

        // 1. 新規追加
        foreach (var taskText in newTasksToAdd)
        {
            var newItem = new TaskItem { Name = taskText, IsSnoozed = false, IsForgotten = false };
            _userTasks[userId].Add(newItem);
            await SendTaskPanel(message.Channel, newItem);
            await Task.Delay(500);
        }

        // 2. 後回し分の復活（忘却分は復活しない）
        var snoozedTasks = _userTasks[userId].Where(t => t.IsSnoozed && !t.IsForgotten).ToList();
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

    // --- パネル送信メソッド ---

    // 通常タスク用パネル
    private async Task SendTaskPanel(ISocketMessageChannel channel, TaskItem item)
    {
        var builder = new ComponentBuilder()
            .WithButton("完了！", $"done_{item.Id}", ButtonStyle.Success)
            .WithButton("後回し", $"snooze_{item.Id}", ButtonStyle.Secondary)
            .WithButton("忘れる", $"forget_{item.Id}", ButtonStyle.Danger); // ★追加

        await channel.SendMessageAsync($"📝 **ToDo:**\n{item.Name}", components: builder.Build());
    }

    // 忘却タスク用パネル
    private async Task SendForgottenPanel(ISocketMessageChannel channel, TaskItem item)
    {
        var builder = new ComponentBuilder()
            .WithButton("思い出す", $"recall_{item.Id}", ButtonStyle.Primary) // 青
            .WithButton("抹消", $"delete_{item.Id}", ButtonStyle.Danger);    // 赤

        // 少し薄い感じで表示
        await channel.SendMessageAsync($"🌫️ **[忘却]**\n{item.Name}", components: builder.Build());
    }

    private async Task ButtonHandler(SocketMessageComponent component)
    {
        string customId = component.Data.CustomId;
        string targetId = customId.Substring(customId.IndexOf('_') + 1);
        ulong userId = component.User.Id;

        if (!_userTasks.ContainsKey(userId)) return;

        var targetTask = _userTasks[userId].FirstOrDefault(t => t.Id == targetId);

        // タスクが見つからない（既に消去された）場合
        if (targetTask == null)
        {
            if (customId.StartsWith("snooze_") || customId.StartsWith("forget_"))
            {
                // 無視
            }
            else
            {
                await component.RespondAsync("⚠️ そのタスクは既に存在しません。", ephemeral: true);
            }
            return;
        }

        // ★変更点：先にメモリ上のデータを書き換えて、画面を更新してしまう（3秒対策）
        // その後で SaveTasksToFile() を呼ぶように順序を変更しました。

        if (customId.StartsWith("done_")) // 完了
        {
            _userTasks[userId].Remove(targetTask);
            string taskName = targetTask.Name;

            // 1. 先にDiscordに反応する
            await component.UpdateAsync(x => { x.Content = $"✅ ~~{taskName}~~"; x.Components = null; });

            // 2. 後で保存する
            SaveTasksToFile();
        }
        else if (customId.StartsWith("snooze_")) // 後回し
        {
            targetTask.IsSnoozed = true;

            await component.UpdateAsync(x => {
                x.Content = $"💤 ~~{targetTask.Name}~~";
                x.Components = new ComponentBuilder().WithButton("完了！", $"done_{targetTask.Id}", ButtonStyle.Success).Build();
            });

            SaveTasksToFile();
        }
        else if (customId.StartsWith("forget_")) // 忘れる
        {
            targetTask.IsForgotten = true;

            await component.UpdateAsync(x => {
                x.Content = $"🌫️ ~~{targetTask.Name}~~ (忘却リストへ)";
                x.Components = null;
            });

            SaveTasksToFile();
        }
        else if (customId.StartsWith("recall_")) // 思い出す
        {
            targetTask.IsForgotten = false;
            targetTask.IsSnoozed = false;

            // ここはメッセージ削除と新規送信を行うため、UpdateAsyncではないが
            // 処理が重くなる前に Respondなどは不要。削除→送信の流れ。
            await component.Message.DeleteAsync();
            await component.Channel.SendMessageAsync("💡 **思い出しました！**");
            await SendTaskPanel(component.Channel, targetTask);

            SaveTasksToFile();
        }
        else if (customId.StartsWith("delete_")) // 完全に消す
        {
            _userTasks[userId].Remove(targetTask);

            await component.UpdateAsync(x => { x.Content = $"🗑️ ~~{targetTask.Name}~~ (抹消済み)"; x.Components = null; });

            SaveTasksToFile();
        }
    }

    // --- 設定・保存周り ---

    private async Task HandleConfigCommand(SocketMessage message)
    {
        // ... (設定コマンド部分は変更なし。長いので省略も可能ですが、完全版として載せます) ...
        ulong userId = message.Author.Id;
        string[] parts = message.Content.Split(' ', 3);
        if (parts.Length < 2) return;
        string command = parts[1].ToLower();
        string arg = parts.Length > 2 ? parts[2] : "";

        if (!_userPrefixes.ContainsKey(userId)) _userPrefixes[userId] = new HashSet<string>(DefaultPrefixes);

        switch (command)
        {
            case "list":
                var list = _userPrefixes[userId].Select(p => p.Replace("\t", "\\t"));
                await message.Channel.SendMessageAsync($"🔧 **一括編集モード**\n```text\n!tset setall |{string.Join("|", list)}\n```");
                break;
            case "setall":
                if (string.IsNullOrEmpty(arg)) return;
                var newSet = arg.Split('|', StringSplitOptions.RemoveEmptyEntries);
                _userPrefixes[userId].Clear();
                foreach (var s in newSet) _userPrefixes[userId].Add(s.Replace("\\t", "\t"));
                SavePrefixesToFile();
                await message.Channel.SendMessageAsync($"✅ 設定を更新しました");
                break;
            case "add":
                if (string.IsNullOrEmpty(arg)) return;
                _userPrefixes[userId].Add(arg);
                SavePrefixesToFile();
                await message.Channel.SendMessageAsync($"✅ `{arg}` 追加");
                break;
            case "del":
                if (string.IsNullOrEmpty(arg)) return;
                if (_userPrefixes[userId].Remove(arg)) { SavePrefixesToFile(); await message.Channel.SendMessageAsync($"🗑️ `{arg}` 削除"); }
                break;
            case "reset":
                _userPrefixes[userId] = new HashSet<string>(DefaultPrefixes);
                SavePrefixesToFile();
                await message.Channel.SendMessageAsync("🔄 リセット完了");
                break;
        }
    }

    // --- ファイル保存・読み込み (形式変更あり: IsForgottenを追加) ---
    // 形式: UserId|IsSnoozed|IsForgotten|Id|Content

    private void SaveTasksToFile()
    {
        var lines = new List<string>();
        foreach (var user in _userTasks)
        {
            foreach (var task in user.Value)
            {
                string safeName = task.Name.Replace("\n", "<<NL>>").Replace("\r", "");
                // 5項目で保存
                lines.Add($"{user.Key}|{task.IsSnoozed}|{task.IsForgotten}|{task.Id}|{safeName}");
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
            var parts = line.Split('|', 5); // 5分割を試みる

            // 古いデータ(4分割)への互換性対応
            if (parts.Length < 4) continue;

            if (ulong.TryParse(parts[0], out ulong userId))
            {
                bool isSnoozed = bool.Parse(parts[1]);
                bool isForgotten = false;
                string id;
                string name;

                if (parts.Length == 5)
                {
                    // 新形式
                    isForgotten = bool.Parse(parts[2]);
                    id = parts[3];
                    name = parts[4].Replace("<<NL>>", "\n");
                }
                else
                {
                    // 旧形式 (IsForgottenがない時代のデータ)
                    id = parts[2];
                    name = parts[3].Replace("<<NL>>", "\n");
                }

                if (!_userTasks.ContainsKey(userId)) _userTasks[userId] = new List<TaskItem>();
                _userTasks[userId].Add(new TaskItem { Id = id, Name = name, IsSnoozed = isSnoozed, IsForgotten = isForgotten });
            }
        }
        Console.WriteLine("Tasks loaded.");
    }

    private void SavePrefixesToFile()
    {
        var lines = new List<string>();
        foreach (var kvp in _userPrefixes) lines.Add($"{kvp.Key}|{string.Join("\t", kvp.Value)}");
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
                _userPrefixes[userId] = new HashSet<string>(parts[1].Split('\t'));
            }
        }
    }

    private HashSet<string> GetUserPrefixes(ulong userId)
    {
        if (_userPrefixes.ContainsKey(userId)) return _userPrefixes[userId];
        return new HashSet<string>(DefaultPrefixes);
    }

    private Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }
}
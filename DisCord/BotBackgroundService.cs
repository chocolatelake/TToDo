using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace TToDo
{
    public class BotBackgroundService : BackgroundService
    {
        private readonly DiscordBot _bot;

        public BotBackgroundService(DiscordBot bot)
        {
            _bot = bot;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _bot.StartAsync();
            await Task.Delay(-1, stoppingToken);
        }
    }
}

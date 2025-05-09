using Microsoft.Extensions.Hosting;

namespace InstUploader.Service;

public class TokenService(IHostApplicationLifetime lifetime, IAppHandler handler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            var now = DateTime.Now;
            foreach (var tokenData in handler.Tokens.Where(t => t.ExpiresAt >= now).ToList())
            {
                await tokenData.CancellationTokenSource.CancelAsync();
                handler.Tokens.Remove(tokenData);
            }

            await Task.Delay(100, stoppingToken);
        }
    }
}
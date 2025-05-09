using Microsoft.Extensions.Hosting;

namespace InstUploader.Service;

public class TokenService(IHostApplicationLifetime lifetime, IAppHandler handler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            var now = DateTime.Now;
            foreach (var token in handler.Tokens.ToList().Where(tokenData => tokenData.ExpiresAt <= now))
            {
                await token.CancellationTokenSource.CancelAsync();
                token.CancellationTokenSource.Dispose();
                handler.RemoveToken(token);
            }

            await Task.Delay(1000, stoppingToken);
        }
    }
}
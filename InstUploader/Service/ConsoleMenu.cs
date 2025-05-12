using Microsoft.Extensions.Hosting;

namespace InstUploader.Service;

public class ConsoleMenu(
    AppHandler appHandler,
    IHostApplicationLifetime lifetime) : IHostedService
{
    private Task? _task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _task = Worker();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_task != null)
            {
                await Task.WhenAny(_task, Task.Delay(Timeout.Infinite, cancellationToken));
            }
        }
        finally
        {
            _task?.Dispose();
            lifetime.StopApplication();
        }
    }

    private async Task Worker()
    {
        await appHandler.EnteringParameters();

        await appHandler.Connect();

        await appHandler.Process();
    }
}
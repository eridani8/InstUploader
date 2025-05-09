using System.Net.Http.Headers;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace InstUploader.Service;

public class ConsoleMenu(
    IAppHandler appHandler,
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
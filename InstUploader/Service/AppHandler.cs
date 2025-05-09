using System.Net.Http.Headers;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace InstUploader.Service;

public interface IAppHandler
{
    List<CancellationTokenData> Tokens { get; }
    void AddToken(CancellationTokenData token);
    void RemoveToken(CancellationTokenData token);
    Task EnteringParameters();
    Task Connect();
    Task Process();
    string AdbPath { get; }
    List<string> Paths { get; }
    int Timeout { get; }
    string Description { get; }
}

public class AppHandler(
    IHttpClientFactory clientFactory,
    IHostApplicationLifetime lifetime,
    Style style,
    IOptions<AppConfiguration> configuration,
    ILogger<AppHandler> logger)
    : IAppHandler
{
    public List<CancellationTokenData> Tokens { get; } = [];
    private readonly Lock _tokensLock = new();
    public string AdbPath { get; private set; } = string.Empty;
    public List<string> Paths { get; private set; } = [];
    public int Timeout { get; private set; }
    public string Description { get; private set; } = string.Empty;
    private List<DeviceData> Devices { get; set; } = [];
    private IAdbClient? AdbClient { get; set; }
    private const string AppName = "com.instagram.android";

    private async Task<bool> CheckCode(string code)
    {
        var client = clientFactory.CreateClient("API");
        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri($"{client.BaseAddress}Check"),
            Content = new StringContent($"{{\"key\":\"{code}\"}}")
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue("application/json")
                }
            }
        };
        using var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task EnteringParameters()
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            string code;
            if (string.IsNullOrEmpty(configuration.Value.Code))
            {
                code = AnsiConsole.Prompt(
                    new TextPrompt<string>($"{"Введите код".MarkupSecondaryColor()}")
                        .PromptStyle(style)
                        .ValidationErrorMessage("Неверный формат".MarkupErrorColor())
                        .Validate(c => Guid.TryParse(c, out _)));
            }
            else
            {
                code = configuration.Value.Code;
            }


            var result = await CheckCode(code);
            if (!result)
            {
                AnsiConsole.MarkupLine("Неверный код".MarkupErrorColor());
                configuration.Value.Code = string.Empty;
            }
            else
            {
                break;
            }
        }

        if (!string.IsNullOrEmpty(configuration.Value.Code))
        {
            AnsiConsole.MarkupLine($"Код загружен из конфигурации".MarkupSecondaryColor());
        }

        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(configuration.Value.AdbPath))
            {
                AdbPath = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("Введите путь к директории с adb.exe".MarkupSecondaryColor())
                        .PromptStyle(style)
                        .ValidationErrorMessage("Директория не найдена".MarkupErrorColor())
                        .Validate(Path.Exists));
            }
            else
            {
                AdbPath = configuration.Value.AdbPath;
            }

            AdbPath = Path.Combine(AdbPath, "adb.exe");

            if (!File.Exists(AdbPath))
            {
                AnsiConsole.MarkupLine("adb.exe не найден...".MarkupErrorColor());
                configuration.Value.AdbPath = string.Empty;
            }
            else break;
        }

        if (!string.IsNullOrEmpty(configuration.Value.AdbPath))
        {
            AnsiConsole.MarkupLine("Путь к adb загружен из конфигурации".MarkupSecondaryColor());
        }

        var directoriesPath = string.Empty;

        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(configuration.Value.DirectoriesPath))
            {
                directoriesPath = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("Введите путь к списку папок".MarkupSecondaryColor())
                        .PromptStyle(style)
                        .ValidationErrorMessage("Директория не найдена".MarkupErrorColor())
                        .Validate(Directory.Exists));
            }
            else
            {
                directoriesPath = configuration.Value.DirectoriesPath;
            }

            if (!Directory.Exists(directoriesPath))
            {
                configuration.Value.DirectoriesPath = string.Empty;
            }
            else break;
        }

        if (!string.IsNullOrEmpty(configuration.Value.DirectoriesPath))
        {
            AnsiConsole.MarkupLine("Путь к директориям загружен из конфигурации".MarkupSecondaryColor());
        }

        Paths = Directory.EnumerateDirectories(directoriesPath).ToList();

        AnsiConsole.MarkupLine($"Найдено {Paths.Count} директорий".MarkupSecondaryColor());

        Description = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Введите описание".MarkupSecondaryColor())
                .PromptStyle(style)
                .AllowEmpty());

        Timeout = await AnsiConsole.PromptAsync(
            new TextPrompt<int>("Введите таймаут (мин)".MarkupSecondaryColor())
                .PromptStyle(style)
                .ValidationErrorMessage("Неверный формат".MarkupErrorColor())
                .Validate(t => t > 0));
    }

    public async Task Connect()
    {
        var server = new AdbServer();
        var startServerResult = await server.StartServerAsync(AdbPath, true, lifetime.ApplicationStopping);
        AnsiConsole.MarkupLine(
            $"{"Статус adb сервера:".MarkupPrimaryColor()} {startServerResult.ToString().MarkupSecondaryColor()}");


        AdbClient = new AdbClient();
        await AdbClient.ConnectAsync("127.0.0.1:5554");

        Devices = (await AdbClient.GetDevicesAsync(lifetime.ApplicationStopping))
            .Where(d => d.State == DeviceState.Online).ToList();
        if (Devices.Count != Paths.Count)
        {
            AnsiConsole.MarkupLine("Количество эмуляторов и папок не совпадает".MarkupErrorColor());
            AnsiConsole.MarkupLine(
                $"{"Эмуляторов:".MarkupPrimaryColor()} {Devices.Count.ToString().MarkupSecondaryColor()}");
            AnsiConsole.MarkupLine($"{"Папок:".MarkupPrimaryColor()} {Paths.Count.ToString().MarkupSecondaryColor()}");
            AnsiConsole.MarkupLine("Нажмите любую клавишу, что бы продолжить...".MarkupSecondaryColor());
            Console.ReadKey(true);
        }
    }

    public async Task Process()
    {
        if (AdbClient is null)
        {
            AnsiConsole.MarkupLine("Нет запущенных эмуляторов".MarkupErrorColor());
            AnsiConsole.MarkupLine("Нажмите любую клавишу для выхода...".MarkupSecondaryColor());
            Console.ReadKey(true);
            return;
        }

        const string mediaDirectory = "storage/emulated/0/DCIM";

        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            foreach (var (i, deviceData) in Devices.Index())
            {
                try
                {
                    if (deviceData.State != DeviceState.Online) continue;

                    var device = deviceData.CreateDeviceClient(AdbClient);

                    // var dump = await device.DumpScreenAsync();
                    // dump?.Save("dump.xml");

                    var screen = await AdbClient.GetFrameBufferAsync(deviceData, lifetime.ApplicationStopping);
                    var height = (int)screen.Header.Height;
                    var width = (int)screen.Header.Width;

                    var directory = Directory.GetFiles(Paths[i]).ToList();
                    if (directory.Count == 0) continue;

                    var file = directory.FirstOrDefault()!;
                    using var sync = new SyncService(deviceData);
                    await using var stream = File.OpenRead(file);
                    await sync.PushAsync(stream,
                        $"{mediaDirectory}/{Guid.CreateVersion7()}.mp4",
                        UnixFileStatus.DefaultFileMode, DateTimeOffset.Now,
                        null,
                        lifetime.ApplicationStopping);
                    stream.Close();

                    await AdbClient!.ExecuteRemoteCommandAsync(
                        $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///{mediaDirectory}",
                        deviceData,
                        lifetime.ApplicationStopping);

                    // File.Delete(file); // TODO

                    var small = TimeSpan.FromSeconds(2);
                    var medium = TimeSpan.FromSeconds(4);
                    var @long = TimeSpan.FromSeconds(7);

                    if (await device.IsAppRunningAsync(AppName, lifetime.ApplicationStopping))
                    {
                        await device.StopAppAsync(AppName);
                        await Task.Delay(small);
                    }

                    await device.StartAppAsync(AppName);
                    await Task.Delay(@long);

                    var creationTab = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/creation_tab']",
                        lifetime.ApplicationStopping);
                    if (creationTab is not null)
                    {
                        await creationTab.ClickAsync();
                        await Task.Delay(small);

                        try
                        {
                            var ctd = new CancellationTokenData(DateTime.Now.Add(medium));
                            AddToken(ctd);
                        
                            var auxiliaryButton = await device.FindElementAsync(
                                "//node[@resource-id='com.instagram.android:id/auxiliary_button']",
                                ctd.CancellationTokenSource.Token);
                            if (auxiliaryButton is not null)
                            {
                                await auxiliaryButton.ClickAsync();
                                await Task.Delay(small);
                            }
                        }
                        catch (OperationCanceledException) {}
                        catch 
                        { 
                            // ignore
                        }
                        
                    }
                    else
                    {
                        throw new Exception("Creation Button Not Found");
                    }

                    try
                    {
                        var ctd1 = new CancellationTokenData(DateTime.Now.Add(medium));
                        AddToken(ctd1);

                        var selectReelsButton = await device.FindElementAsync(
                            "//node[@resource-id='com.instagram.android:id/cam_dest_clips']",
                            ctd1.CancellationTokenSource.Token);
                        if (selectReelsButton is not null)
                        {
                            await selectReelsButton.ClickAsync();
                            await Task.Delay(small);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // ignore
                    }

                    var firstVideoInGallery = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/gallery_recycler_view']/node[@class='android.view.ViewGroup']",
                        lifetime.ApplicationStopping);
                    if (firstVideoInGallery is not null)
                    {
                        await firstVideoInGallery.ClickAsync();
                        await Task.Delay(medium);
                    }
                    else
                    {
                        throw new Exception("FirstVideoInGallery Not Found");
                    }

                    var nextButton = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/clips_right_action_button']",
                        lifetime.ApplicationStopping);
                    if (nextButton is not null)
                    {
                        await nextButton.ClickAsync();
                        await Task.Delay(medium);
                    }
                    else
                    {
                        throw new Exception("Next Button Not Found");
                    }

                    var descriptionInput = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/caption_input_text_view']",
                        lifetime.ApplicationStopping);
                    if (descriptionInput is not null)
                    {
                        await descriptionInput.ClickAsync();
                        await Task.Delay(small);
                        await descriptionInput.SendTextAsync(Description, lifetime.ApplicationStopping);
                        await Task.Delay(small);
                        await device.ClickBackButtonAsync(lifetime.ApplicationStopping);
                        await Task.Delay(small);
                    }
                    else
                    {
                        throw new Exception("Description Input Not Found");
                    }

                    await device.SwipeAsync(
                        width / 2, Convert.ToInt32(height / 1.3),
                        width / 2, Convert.ToInt32(height / 3.3),
                        300,
                        lifetime.ApplicationStopping);

                    await Task.Delay(medium);

                    var trialPeriodCheckbox = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/title' and @text='Пробный период']",
                        lifetime.ApplicationStopping);
                    if (trialPeriodCheckbox is not null)
                    {
                        if (trialPeriodCheckbox.Attributes != null &&
                            trialPeriodCheckbox.Attributes.TryGetValue("checked", out var checkedValue))
                        {
                            if (checkedValue == "false")
                            {
                                await trialPeriodCheckbox.ClickAsync();
                                await Task.Delay(small);

                                try
                                {
                                    var ctd = new CancellationTokenData(DateTime.Now.Add(medium));
                                    AddToken(ctd);

                                    var closeButton = await device.FindElementAsync(
                                        "//node[@resource-id='com.instagram.android:id/bb_primary_action_container']",
                                        ctd.CancellationTokenSource.Token);
                                    if (closeButton is not null)
                                    {
                                        await closeButton.ClickAsync();
                                        await Task.Delay(small);
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception e)
                                {
                                    // ignore
                                }
                            }
                        }
                        else
                        {
                            throw new Exception("Checkbox Attribute Not Found");
                        }
                    }
                    else
                    {
                        throw new Exception("Checkbox Not Found");
                    }

                    var shareButton = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/share_button']",
                        lifetime.ApplicationStopping);
                    if (shareButton is not null)
                    {
                        await shareButton.ClickAsync();
                        await Task.Delay(@long);

                        try
                        {
                            var ctd = new CancellationTokenData(DateTime.Now.Add(medium));
                            AddToken(ctd);

                            var promoDialogCloseButton = await device.FindElementAsync(
                                "//node[@resource-id='com.instagram.android:id/igds_promo_dialog_action_button']",
                                ctd.CancellationTokenSource.Token);
                            if (promoDialogCloseButton is not null)
                            {
                                await promoDialogCloseButton.ClickAsync();
                                await Task.Delay(small);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception e)
                        {
                            // ignore
                        }
                    }

                    var r = "";
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Ошибка в процессе");
                }
            }
        }
    }

    public void AddToken(CancellationTokenData token)
    {
        lock (_tokensLock)
        {
            Tokens.Add(token);
        }
    }

    public void RemoveToken(CancellationTokenData token)
    {
        lock (_tokensLock)
        {
            Tokens.Remove(token);
        }
    }
}
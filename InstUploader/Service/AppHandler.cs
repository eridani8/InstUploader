using System.Net.Http.Headers;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;
using TextCopy;

namespace InstUploader.Service;

public interface IAppHandler
{
    Task EnteringParameters();
    Task Connect();
    Task Process();
    string AdbPath { get; }
    List<string> Paths { get; }
    TimeSpan Timeout { get; }
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
    public string AdbPath { get; private set; } = string.Empty;
    public List<string> Paths { get; private set; } = [];
    public TimeSpan Timeout { get; private set; }
    public string Description { get; private set; } = string.Empty;
    private List<DeviceData> Devices { get; set; } = [];
    private IAdbClient? AdbClient { get; set; }

    private const string AppName = "com.instagram.android";
    private const string MediaDirectory = "storage/emulated/0/DCIM";

    private readonly TimeSpan _smallDelay = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _mediumDelay = TimeSpan.FromSeconds(4);
    private readonly TimeSpan _longDelay = TimeSpan.FromSeconds(7);

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

        const string tagsFile = "tags.txt";
        if (!File.Exists(tagsFile))
        {
            Description = await AnsiConsole.PromptAsync(
                new TextPrompt<string>("Введите описание".MarkupSecondaryColor())
                    .PromptStyle(style)
                    .AllowEmpty());
        }
        else if (await File.ReadAllLinesAsync(tagsFile, lifetime.ApplicationStopping) is { Length: > 0 } readAllLines)
        {
            var randomNumber = Random.Shared.Next(0, readAllLines.Length);
            var randomLine = readAllLines[randomNumber];
            Description = randomLine;
            AnsiConsole.MarkupLine($"Рандомное описание из tags.txt: {randomLine}".MarkupSecondaryColor());
        }
        else
        {
            Description = await AnsiConsole.PromptAsync(
                new TextPrompt<string>("Введите описание".MarkupSecondaryColor())
                    .PromptStyle(style)
                    .AllowEmpty());
        }

        var timeoutInMinutes = await AnsiConsole.PromptAsync(
            new TextPrompt<int>("Введите таймаут (мин)".MarkupSecondaryColor())
                .PromptStyle(style)
                .ValidationErrorMessage("Неверный формат".MarkupErrorColor())
                .Validate(t => t > 0));

        Timeout = TimeSpan.FromMinutes(timeoutInMinutes);
    }

    public async Task Connect()
    {
        var server = new AdbServer();
        await server.StartServerAsync(AdbPath, true, lifetime.ApplicationStopping);
        // AnsiConsole.MarkupLine(
        //     $"{"Статус adb сервера:".MarkupPrimaryColor()} {startServerResult.ToString().MarkupSecondaryColor()}");


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

        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            foreach (var (i, deviceData) in Devices.Index())
            {
                try
                {
                    if (deviceData.State != DeviceState.Online) continue;

                    var device = deviceData.CreateDeviceClient(AdbClient);
                    
                    AnsiConsole.MarkupLine($"Начало процесса отправки {deviceData.Model}".EscapeMarkup().MarkupPrimaryColor());

                    // var dump = await device.DumpScreenAsync();
                    // dump?.Save("dump.xml");

                    var screen = await AdbClient.GetFrameBufferAsync(deviceData, lifetime.ApplicationStopping);
                    var height = (int)screen.Header.Height;
                    var width = (int)screen.Header.Width;

                    var directory = Directory.GetFiles(Paths[i]).ToList();
                    if (directory.Count == 0)
                    {
                        throw new Exception($"Нет файлов в директории: {Paths[i]}");
                    }

                    var file = directory.FirstOrDefault()!;
                    using var sync = new SyncService(deviceData);

                    var files = await sync.GetDirectoryListingAsync(MediaDirectory);
                    foreach (var fileStatistic in files)
                    {
                        await AdbClient.ExecuteRemoteCommandAsync(
                            $"rm {MediaDirectory}/{fileStatistic.Path}",
                            deviceData,
                            lifetime.ApplicationStopping);
                    }

                    await using var stream = File.OpenRead(file);
                    await sync.PushAsync(stream,
                        $"{MediaDirectory}/{Guid.CreateVersion7()}.mp4",
                        UnixFileStatus.DefaultFileMode, DateTimeOffset.Now,
                        null,
                        lifetime.ApplicationStopping);
                    stream.Close();

                    await AdbClient.ExecuteRemoteCommandAsync(
                        $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///{MediaDirectory}",
                        deviceData,
                        lifetime.ApplicationStopping);

                    File.Delete(file);

                    if (await device.IsAppRunningAsync(AppName, lifetime.ApplicationStopping))
                    {
                        await device.StopAppAsync(AppName);
                        await Task.Delay(_smallDelay);
                    }

                    await device.StartAppAsync(AppName);
                    await Task.Delay(_longDelay);

                    var creationTab = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/creation_tab']",
                        lifetime.ApplicationStopping);
                    if (creationTab is not null)
                    {
                        await creationTab.ClickAsync();
                        await Task.Delay(_smallDelay);

                        var continueVideoEditCloseButton = device.FindElement(
                            "//node[@resource-id='com.instagram.android:id/auxiliary_button']",
                            _smallDelay);
                        if (continueVideoEditCloseButton is not null)
                        {
                            await continueVideoEditCloseButton.ClickAsync();
                            await Task.Delay(_smallDelay);
                        }
                    }
                    else
                    {
                        throw new Exception("Creation Button Not Found");
                    }

                    var selectReelsButton = device.FindElement(
                        "//node[@resource-id='com.instagram.android:id/cam_dest_clips']",
                        _smallDelay);
                    if (selectReelsButton is not null)
                    {
                        await selectReelsButton.ClickAsync();
                        await Task.Delay(_smallDelay);
                    }

                    var reelsPopupCloseButton = device.FindElement(
                        "//node[@resource-id='com.instagram.android:id/dialog_container']//node[@resource-id='com.instagram.android:id/primary_button' and @text='ОК']",
                        _smallDelay);
                    if (reelsPopupCloseButton is not null)
                    {
                        await reelsPopupCloseButton.ClickAsync();
                        await Task.Delay(_smallDelay);
                    }

                    var firstVideoInGallery = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/gallery_recycler_view']/node[@class='android.view.ViewGroup']",
                        lifetime.ApplicationStopping);
                    if (firstVideoInGallery is not null)
                    {
                        await firstVideoInGallery.ClickAsync();
                        await Task.Delay(_mediumDelay);

                        var stickerDialogCloseButton = device.FindElement(
                            "//node[@resource-id='com.instagram.android:id/auxiliary_button']",
                            _smallDelay);
                        if (stickerDialogCloseButton is not null)
                        {
                            await stickerDialogCloseButton.ClickAsync();
                            await Task.Delay(_smallDelay);
                        }
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
                        await Task.Delay(_mediumDelay);
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
                        await Task.Delay(_smallDelay);
                        await ClipboardService.SetTextAsync(string.Empty);
                        await Task.Delay(_smallDelay);
                        await descriptionInput.SendTextAsync(Description);
                        await Task.Delay(_smallDelay);
                        await device.ClickBackButtonAsync(lifetime.ApplicationStopping);
                        await Task.Delay(_smallDelay);
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

                    await Task.Delay(_mediumDelay);

                    var trialPeriodCheckbox = device.FindElement(
                        "//node[@resource-id='com.instagram.android:id/title' and @text='Пробный период']",
                        _smallDelay);
                    if (trialPeriodCheckbox is not null)
                    {
                        if (trialPeriodCheckbox.Attributes != null &&
                            trialPeriodCheckbox.Attributes.TryGetValue("checked", out var checkedValue))
                        {
                            if (checkedValue == "false")
                            {
                                await trialPeriodCheckbox.ClickAsync();
                                await Task.Delay(_smallDelay);

                                var closeButton = device.FindElement(
                                    "//node[@resource-id='com.instagram.android:id/bb_primary_action_container']",
                                    _smallDelay);
                                if (closeButton is not null)
                                {
                                    await closeButton.ClickAsync();
                                    await Task.Delay(_smallDelay);
                                }
                            }
                        }
                        else
                        {
                            throw new Exception("Checkbox Attribute Not Found");
                        }
                    }

                    var shareButton = await device.FindElementAsync(
                        "//node[@resource-id='com.instagram.android:id/share_button']",
                        lifetime.ApplicationStopping);
                    if (shareButton is not null)
                    {
                        await shareButton.ClickAsync();
                        await Task.Delay(_longDelay);

                        var promoDialogCloseButton = device.FindElement(
                            "//node[@resource-id='com.instagram.android:id/igds_promo_dialog_action_button']",
                            _smallDelay);
                        if (promoDialogCloseButton is not null)
                        {
                            await promoDialogCloseButton.ClickAsync();
                            await Task.Delay(_smallDelay);
                        }

                        var sharePopupCloseButton = device.FindElement(
                            "//node[@resource-id='com.instagram.android:id/clips_nux_sheet_share_button']",
                            _smallDelay);
                        if (sharePopupCloseButton is not null)
                        {
                            await sharePopupCloseButton.ClickAsync();
                            await Task.Delay(_smallDelay);
                        }
                    }

                    await Task.Delay(_mediumDelay);

                    while (device.FindElement("//node[@resource-id='com.instagram.android:id/upload_progress_bar_container']", _mediumDelay) is not null)
                    {
                        await Task.Delay(_mediumDelay);
                    }
                    
                    AnsiConsole.MarkupLine($"Успешная публикация {deviceData.Model}".EscapeMarkup().MarkupPrimaryColor());

                    await Task.Delay(_smallDelay);
                }
                catch (Exception e)
                {
                    logger.LogError(e, e.Message);
                }
            }
            
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Balloon)
                .SpinnerStyle(style)
                .StartAsync("Таймаут...", async ctx =>
                {
                    var secondsRemaining = (int)Timeout.TotalSeconds;
                    while (secondsRemaining > 0)
                    {
                        ctx.Status($"Возобновление через {secondsRemaining} секунд(ы)...");
                        await Task.Delay(1000, lifetime.ApplicationStopping);
                        secondsRemaining--;
                    }
                });
                
            AnsiConsole.WriteLine();
        }
    }
}
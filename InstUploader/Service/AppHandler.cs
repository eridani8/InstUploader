using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using CliWrap;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace InstUploader.Service;

public class AppHandler(
    IHttpClientFactory clientFactory,
    IHostApplicationLifetime lifetime,
    Style style,
    IOptions<AppConfiguration> configuration,
    ILogger<AppHandler> logger
)
{
    public string AdbPath { get; private set; } = string.Empty;
    public string EmulatorPath { get; private set; } = string.Empty;
    public List<string> Paths { get; private set; } = [];
    public TimeSpan Timeout { get; private set; }
    public string Description { get; private set; } = string.Empty;
    private List<string> Avds { get; set; } = [];
    private IAdbClient? AdbClient { get; set; }

    private const string AdbLogsPath = "adb_logs";
    private const string AppName = "com.instagram.android";
    private const string MediaDirectory = "storage/emulated/0/Pictures";

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
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        using var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task EnteringParameters()
    {
        Directory.CreateDirectory(AdbLogsPath);

        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            string code;
            if (string.IsNullOrEmpty(configuration.Value.Code))
            {
                code = AnsiConsole.Prompt(
                    new TextPrompt<string>($"{"Введите код".MarkupSecondaryColor()}")
                        .PromptStyle(style)
                        .ValidationErrorMessage("Неверный формат".MarkupErrorColor())
                        .Validate(c => Guid.TryParse(c, out _))
                );
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
                    new TextPrompt<string>(
                        "Введите путь к директории с adb.exe".MarkupSecondaryColor()
                    )
                        .PromptStyle(style)
                        .ValidationErrorMessage("Директория не найдена".MarkupErrorColor())
                        .Validate(Path.Exists)
                );
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
            else
                break;
        }

        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(configuration.Value.EmulatorPath))
            {
                EmulatorPath = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>(
                        "Введите путь к директории эмулятора".MarkupSecondaryColor()
                    )
                        .PromptStyle(style)
                        .ValidationErrorMessage("Директория не найдена".MarkupErrorColor())
                        .Validate(Path.Exists)
                );
            }
            else
            {
                EmulatorPath = configuration.Value.EmulatorPath;
            }

            EmulatorPath = Path.Combine(EmulatorPath, "emulator.exe");

            if (!File.Exists(EmulatorPath))
            {
                AnsiConsole.MarkupLine("emulator.exe не найден...".MarkupErrorColor());
                configuration.Value.EmulatorPath = string.Empty;
            }
            else
                break;
        }

        if (!string.IsNullOrEmpty(configuration.Value.EmulatorPath))
        {
            AnsiConsole.MarkupLine(
                "Путь к эмулятору загружен из конфигурации".MarkupSecondaryColor()
            );
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
                        .Validate(Directory.Exists)
                );
            }
            else
            {
                directoriesPath = configuration.Value.DirectoriesPath;
            }

            if (!Directory.Exists(directoriesPath))
            {
                configuration.Value.DirectoriesPath = string.Empty;
            }
            else
                break;
        }

        if (!string.IsNullOrEmpty(configuration.Value.DirectoriesPath))
        {
            AnsiConsole.MarkupLine(
                "Путь к директориям загружен из конфигурации".MarkupSecondaryColor()
            );
        }

        Paths = Directory.EnumerateDirectories(directoriesPath).ToList();

        AnsiConsole.MarkupLine($"Найдено {Paths.Count} директорий".MarkupSecondaryColor());

        const string tagsFile = "tags.txt";
        if (!File.Exists(tagsFile))
        {
            Description = await AnsiConsole.PromptAsync(
                new TextPrompt<string>("Введите описание".MarkupSecondaryColor())
                    .PromptStyle(style)
                    .AllowEmpty()
            );
        }
        else if (
            await File.ReadAllLinesAsync(tagsFile, lifetime.ApplicationStopping) is
            { Length: > 0 } readAllLines
        )
        {
            var randomNumber = Random.Shared.Next(0, readAllLines.Length);
            var randomLine = readAllLines[randomNumber];
            Description = randomLine;
            AnsiConsole.MarkupLine(
                $"Рандомное описание из tags.txt: {randomLine}".MarkupSecondaryColor()
            );
        }
        else
        {
            Description = await AnsiConsole.PromptAsync(
                new TextPrompt<string>("Введите описание".MarkupSecondaryColor())
                    .PromptStyle(style)
                    .AllowEmpty()
            );
        }

        var timeoutInMinutes = await AnsiConsole.PromptAsync(
            new TextPrompt<int>("Введите таймаут (мин)".MarkupSecondaryColor())
                .PromptStyle(style)
                .ValidationErrorMessage("Неверный формат".MarkupErrorColor())
                .Validate(t => t >= 0)
        );

        Timeout = TimeSpan.FromMinutes(timeoutInMinutes);
    }

    public async Task Connect()
    {
        var devicesStringBuilder = new StringBuilder();

        await Cli.Wrap(EmulatorPath)
            .WithArguments("-list-avds")
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(devicesStringBuilder))
            .ExecuteAsync(lifetime.ApplicationStopping);

        Avds = devicesStringBuilder
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        await KillEmulatorProcesses();

        var server = new AdbServer();
        await server.StartServerAsync(AdbPath, true, lifetime.ApplicationStopping);

        AdbClient = new AdbClient();

        if (Avds.Count != Paths.Count)
        {
            AnsiConsole.MarkupLine("Количество эмуляторов и папок не совпадает".MarkupErrorColor());
            AnsiConsole.MarkupLine(
                $"{"Эмуляторов:".MarkupPrimaryColor()} {Avds.Count.ToString().MarkupSecondaryColor()}"
            );
            AnsiConsole.MarkupLine(
                $"{"Папок:".MarkupPrimaryColor()} {Paths.Count.ToString().MarkupSecondaryColor()}"
            );
            AnsiConsole.MarkupLine(
                "Нажмите любую клавишу, что бы продолжить...".MarkupSecondaryColor()
            );
            Console.ReadKey(true);
        }
    }

    public async Task Process()
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            foreach (var (i, avd) in Avds.Index())
            {
                try
                {
                    await HandleProcess(avd, Paths[i]);
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    logger.LogError(e, e.Message);
                }
            }

            await AnsiConsole
                .Status()
                .Spinner(Spinner.Known.Balloon)
                .SpinnerStyle(style)
                .StartAsync(
                    "Таймаут...",
                    async ctx =>
                    {
                        var secondsRemaining = (int)Timeout.TotalSeconds;
                        while (secondsRemaining > 0)
                        {
                            ctx.Status($"Возобновление через {secondsRemaining} секунд(ы)...");
                            await Task.Delay(1000, lifetime.ApplicationStopping);
                            secondsRemaining--;
                        }
                    }
                );

            AnsiConsole.WriteLine();
        }
    }

    private async Task HandleProcess(string avd, string directoryPath)
    {
        using var cts = new CancellationTokenSource();
        try
        {
            if (AdbClient is null)
            {
                AnsiConsole.MarkupLine("Нет запущенных эмуляторов".MarkupErrorColor());
                AnsiConsole.MarkupLine(
                    "Нажмите любую клавишу для выхода...".MarkupSecondaryColor()
                );
                Console.ReadKey(true);
                return;
            }

            if (Avds.Count == 0)
            {
                AnsiConsole.MarkupLine("Нет загруженных эмуляторов".MarkupErrorColor());
                AnsiConsole.MarkupLine(
                    "Нажмите любую клавишу для выхода...".MarkupSecondaryColor()
                );
                Console.ReadKey(true);
                return;
            }

            var port = FindFreeEvenPortInRange();

            var now = DateTime.Now;

            await KillEmulatorProcesses();
            
            await Task.Delay(_mediumDelay, cts.Token);

            var emulatorProcess = Cli.Wrap(EmulatorPath)
                .WithArguments($"-avd {avd} -port {port} -no-snapshot-load -no-snapshot-save")
                .WithStandardOutputPipe(
                    PipeTarget.ToFile(Path.Combine(AdbLogsPath, $"output_{now:HH_mm_ss}.log"))
                )
                .WithStandardErrorPipe(
                    PipeTarget.ToFile(Path.Combine(AdbLogsPath, $"error_{now:HH_mm_ss}.log"))
                )
                .ExecuteAsync(cts.Token);

            await Task.Delay(_longDelay * 2, cts.Token);

            await AdbClient.ConnectAsync($"127.0.0.1:{port}", cts.Token);

            var continueAt = DateTime.Now.AddMinutes(5);

            AnsiConsole.MarkupLine("Ожидание полной загрузки...".MarkupPrimaryColor());

            DeviceData deviceData = default;
            while (!cts.IsCancellationRequested)
            {
                var devices = await AdbClient.GetDevicesAsync(cts.Token);
                deviceData = devices.FirstOrDefault(d => d.State == DeviceState.Online);
                if (!deviceData.IsEmpty)
                {
                    var receiver = new ConsoleOutputReceiver();
                    await AdbClient.ExecuteRemoteCommandAsync(
                        "getprop sys.boot_completed",
                        deviceData,
                        receiver,
                        cts.Token
                    );
                    var output = receiver.ToString().Trim();
                    if (output == "1")
                    {
                        break;
                    }

                    AnsiConsole.MarkupLine("*".MarkupPrimaryColor());
                }
                else
                {
                    AnsiConsole.MarkupLine("~".MarkupPrimaryColor());
                }

                if (DateTime.Now >= continueAt)
                {
                    AnsiConsole.MarkupLine(
                        "Таймаут ожидания загрузки устройства".MarkupErrorColor()
                    );

                    return;
                }

                await Task.Delay(_longDelay, cts.Token);
            }

            AnsiConsole.MarkupLine("+".MarkupPrimaryColor());

            await Task.Delay(_longDelay, cts.Token);

            var device = new DeviceClient(AdbClient, deviceData);

            AnsiConsole.MarkupLine(
                $"Начало процесса отправки {deviceData.Model}".EscapeMarkup().MarkupPrimaryColor()
            );

            // var dump = await device.DumpScreenAsync();
            // dump?.Save("dump.xml");

            var screen = await AdbClient.GetFrameBufferAsync(
                deviceData,
                lifetime.ApplicationStopping
            );
            var height = (int)screen.Header.Height;
            var width = (int)screen.Header.Width;

            var directory = Directory.GetFiles(directoryPath).ToList();
            if (directory.Count == 0)
            {
                throw new Exception($"Нет файлов в директории: {directoryPath}");
            }

            var file = directory.FirstOrDefault()!;

            using var sync = new SyncService(deviceData);
            
            AnsiConsole.MarkupLine("Загрузка файла...".MarkupPrimaryColor());
            
            await using var stream = File.OpenRead(file);
            await sync.PushAsync(
                stream,
                $"{MediaDirectory}/{Guid.CreateVersion7()}.mp4",
                UnixFileStatus.DefaultFileMode,
                DateTimeOffset.Now,
                null,
                lifetime.ApplicationStopping
            );
            stream.Close();

            await device.UpdateMediaState(MediaDirectory, lifetime.ApplicationStopping);

            File.Delete(file);
            
            AnsiConsole.MarkupLine("Загружен".MarkupPrimaryColor());

            if (await device.IsAppRunningAsync(AppName, lifetime.ApplicationStopping))
            {
                await device.StopAppAsync(AppName, cts.Token);
                await Task.Delay(_smallDelay, cts.Token);
            }

            await device.StartAppAsync(AppName, cts.Token);
            await Task.Delay(_longDelay, cts.Token);
            
            AnsiConsole.MarkupLine("Выполнение скрипта...".MarkupPrimaryColor());

            var creationTab = await device.FindElementAsync(
                "//node[@resource-id='com.instagram.android:id/creation_tab']",
                lifetime.ApplicationStopping
            );
            if (creationTab is not null)
            {
                await creationTab.ClickAsync(cts.Token);
                await Task.Delay(_smallDelay, cts.Token);

                var continueVideoEditCloseButton = device.FindElement(
                    "//node[@resource-id='com.instagram.android:id/auxiliary_button']",
                    _smallDelay
                );
                if (continueVideoEditCloseButton is not null)
                {
                    await continueVideoEditCloseButton.ClickAsync(cts.Token);
                    await Task.Delay(_smallDelay, cts.Token);
                }
            }
            else
            {
                throw new Exception("Creation Button Not Found");
            }

            var selectReelsButton = device.FindElement(
                "//node[@resource-id='com.instagram.android:id/cam_dest_clips']",
                _smallDelay
            );
            if (selectReelsButton is not null)
            {
                await selectReelsButton.ClickAsync(cts.Token);
                await Task.Delay(_smallDelay, cts.Token);
            }

            var reelsPopupCloseButton = device.FindElement(
                "//node[@resource-id='com.instagram.android:id/dialog_container']//node[@resource-id='com.instagram.android:id/primary_button' and @text='ОК']",
                _smallDelay
            );
            if (reelsPopupCloseButton is not null)
            {
                await reelsPopupCloseButton.ClickAsync(cts.Token);
                await Task.Delay(_smallDelay, cts.Token);
            }

            var firstVideoInGallery = await device.FindElementAsync(
                "//node[@resource-id='com.instagram.android:id/gallery_recycler_view']/node[@class='android.view.ViewGroup']",
                lifetime.ApplicationStopping
            );
            if (firstVideoInGallery is not null)
            {
                await firstVideoInGallery.ClickAsync(cts.Token);
                await Task.Delay(_mediumDelay, cts.Token);

                var stickerDialogCloseButton = device.FindElement(
                    "//node[@resource-id='com.instagram.android:id/auxiliary_button']",
                    _smallDelay
                );
                if (stickerDialogCloseButton is not null)
                {
                    await stickerDialogCloseButton.ClickAsync(cts.Token);
                    await Task.Delay(_smallDelay, cts.Token);
                }
            }
            else
            {
                throw new Exception("FirstVideoInGallery Not Found");
            }

            var nextButton = await device.FindElementAsync(
                "//node[@resource-id='com.instagram.android:id/clips_right_action_button']",
                lifetime.ApplicationStopping
            );
            if (nextButton is not null)
            {
                await nextButton.ClickAsync(cts.Token);
                await Task.Delay(_mediumDelay, cts.Token);
            }
            else
            {
                throw new Exception("Next Button Not Found");
            }

            var descriptionInput = await device.FindElementAsync(
                "//node[@resource-id='com.instagram.android:id/caption_input_text_view']",
                lifetime.ApplicationStopping
            );
            if (descriptionInput is not null)
            {
                await descriptionInput.ClickAsync(cts.Token);
                await Task.Delay(_smallDelay, cts.Token);
                await descriptionInput.SendTextAsync(Description, cts.Token);
                await Task.Delay(_smallDelay, cts.Token);
                await device.ClickBackButtonAsync(lifetime.ApplicationStopping);
                await Task.Delay(_smallDelay, cts.Token);
            }
            else
            {
                throw new Exception("Description Input Not Found");
            }

            var trialPeriodCheckbox = device.FindElement(
                "//node[@resource-id='com.instagram.android:id/title' and @text='Пробный период']",
                _smallDelay
            );
            if (trialPeriodCheckbox is not null)
            {
                if (
                    trialPeriodCheckbox.Attributes != null
                    && trialPeriodCheckbox.Attributes.TryGetValue("checked", out var checkedValue)
                )
                {
                    if (checkedValue == "false")
                    {
                        await trialPeriodCheckbox.ClickAsync(cts.Token);
                        await Task.Delay(_smallDelay, cts.Token);

                        var closeButton = device.FindElement(
                            "//node[@resource-id='com.instagram.android:id/bb_primary_action_container']",
                            _smallDelay
                        );
                        if (closeButton is not null)
                        {
                            await closeButton.ClickAsync(cts.Token);
                            await Task.Delay(_smallDelay, cts.Token);
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
                await device.SwipeAsync(
                    width / 2,
                    Convert.ToInt32(height / 1.3),
                    width / 2,
                    Convert.ToInt32(height / 3.3),
                    300,
                    lifetime.ApplicationStopping
                );

                await Task.Delay(_mediumDelay, cts.Token);

                var trialPeriodCheckbox1 = device.FindElement(
                    "//node[@resource-id='com.instagram.android:id/title' and @text='Пробный период']",
                    _smallDelay
                );
                if (trialPeriodCheckbox1 is not null)
                {
                    if (
                        trialPeriodCheckbox1.Attributes != null
                        && trialPeriodCheckbox1.Attributes.TryGetValue(
                            "checked",
                            out var checkedValue
                        )
                    )
                    {
                        if (checkedValue == "false")
                        {
                            await trialPeriodCheckbox1.ClickAsync(cts.Token);
                            await Task.Delay(_smallDelay, cts.Token);

                            var closeButton = device.FindElement(
                                "//node[@resource-id='com.instagram.android:id/bb_primary_action_container']",
                                _smallDelay
                            );
                            if (closeButton is not null)
                            {
                                await closeButton.ClickAsync(cts.Token);
                                await Task.Delay(_smallDelay, cts.Token);
                            }
                        }
                    }
                    else
                    {
                        throw new Exception("Checkbox Attribute Not Found");
                    }
                }
            }

            var shareButton = await device.FindElementAsync(
                "//node[@resource-id='com.instagram.android:id/share_button']",
                lifetime.ApplicationStopping
            );
            if (shareButton is not null)
            {
                await shareButton.ClickAsync(cts.Token);
                await Task.Delay(_longDelay, cts.Token);

                var promoDialogCloseButton = device.FindElement(
                    "//node[@resource-id='com.instagram.android:id/igds_promo_dialog_action_button']",
                    _smallDelay
                );
                if (promoDialogCloseButton is not null)
                {
                    await promoDialogCloseButton.ClickAsync(cts.Token);
                    await Task.Delay(_smallDelay, cts.Token);
                }

                var sharePopupCloseButton = device.FindElement(
                    "//node[@resource-id='com.instagram.android:id/clips_nux_sheet_share_button']",
                    _smallDelay
                );
                if (sharePopupCloseButton is not null)
                {
                    await sharePopupCloseButton.ClickAsync(cts.Token);
                    await Task.Delay(_smallDelay, cts.Token);
                }
            }

            await Task.Delay(_mediumDelay, cts.Token);

            while (
                device.FindElement(
                    "//node[@resource-id='com.instagram.android:id/upload_progress_bar_container']",
                    _smallDelay
                )
                    is not null
            )
            {
                await Task.Delay(_smallDelay, cts.Token);
            }

            AnsiConsole.MarkupLine(
                $"Успешная публикация {deviceData.Model}".EscapeMarkup().MarkupPrimaryColor()
            );

            await Task.Delay(_smallDelay, cts.Token);

            var files = await sync.GetDirectoryListingAsync(MediaDirectory, cts.Token);
            foreach (var fileStatistic in files)
            {
                await device.DeleteFile(
                    $"{MediaDirectory}/{fileStatistic.Path}",
                    lifetime.ApplicationStopping
                );
            }
            
            AnsiConsole.MarkupLine("Очистка файлов...".MarkupPrimaryColor());

            await device.UpdateMediaState(MediaDirectory, lifetime.ApplicationStopping);

            await Task.Delay(_smallDelay, cts.Token);
            
            AnsiConsole.MarkupLine("Остановка эмулятора...".MarkupPrimaryColor());
            
            await device.StopAppAsync(AppName, cts.Token);

            try
            {
                await AdbClient.DisconnectAsync($"127.0.0.1:{port}", cts.Token);

                var receiver = new ConsoleOutputReceiver();
                try
                {
                    await AdbClient.ExecuteRemoteCommandAsync(
                        "emu kill",
                        deviceData,
                        receiver,
                        cts.Token
                    );
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Expected emu kill error: {ex.Message}");
                }

                await Task.Delay(_longDelay, cts.Token);

                await KillEmulatorProcesses();

                await Task.Delay(_longDelay, cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Error during emulator shutdown: {ex.Message}");
                await KillEmulatorProcesses();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    private async Task KillEmulatorProcesses()
    {
        string[] processNames = ["emulator", "qemu-system-x86_64", "crashpad_handler"];
        foreach (var processName in processNames)
        {
            foreach (var emulator in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                try
                {
                    emulator.Kill(true);
                    await emulator.WaitForExitAsync();
                }
                catch (Exception e)
                {
                    logger.LogError(e, e.Message);
                }
            }
        }
        
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    private static int FindFreeEvenPortInRange(int start = 5554, int end = 5680)
    {
        for (var port = start; port <= end; port += 2)
        {
            var portInUse = IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(p => p.Port == port || p.Port == port + 1);

            if (!portInUse)
                return port;
        }

        throw new InvalidOperationException("Нет свободного порта");
    }
}

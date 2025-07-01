using System.Windows.Forms;
using MediaSessionWSProvider;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);


// Создаём хост вручную
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<SettingsService>();
        services.AddSingleton<FftService>();
        services.AddSingleton<MetadataCache>();
        services.AddHostedService<Worker>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddEventLog();
    })
    .Build();

using var tray = new TrayHost(host.Services.GetRequiredService<FftService>());

// Запускаем хост асинхронно
await host.StartAsync();

// Запускаем WinForms message loop (нужен для NotifyIcon)
Application.Run();

// После выхода из трея — корректно завершаем
await host.StopAsync();

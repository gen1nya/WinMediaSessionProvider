using System.Windows.Forms;
using MediaSessionWSProvider;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

using var tray = new TrayHost();

// Создаём хост вручную
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<AudioSpectrumService>();
        services.AddHostedService<Worker>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddEventLog();
    })
    .Build();

// Запускаем хост асинхронно
await host.StartAsync();

// Запускаем WinForms message loop (нужен для NotifyIcon)
Application.Run();

// После выхода из трея — корректно завершаем
await host.StopAsync();

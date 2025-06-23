using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MediaSessionWSProvider;

/// <summary>
/// Простейший сервис для загрузки и сохранения настроек приложения.
/// </summary>
public class SettingsService
{
    private const string FileName = "settings.json";
    private readonly ILogger<SettingsService> _logger;

    public class DataModel
    {
        public bool FftEnabled { get; set; }
        public string? DeviceId { get; set; }
    }

    public DataModel Data { get; private set; } = new();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FileName))
            {
                var json = File.ReadAllText(FileName);
                Data = JsonSerializer.Deserialize<DataModel>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Не удалось загрузить настройки");
            Data = new();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FileName, json);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Не удалось сохранить настройки");
        }
    }
}

using System;
using System.IO;
using System.Text.Json;
using PokeSwitch.Models;

namespace PokeSwitch.Services
{
    public class ConfigManager
    {
        private readonly string _configFilePath;

        public AppConfig CurrentConfig { get; private set; }

        public ConfigManager()
        {
            _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pokeswitch-config.json");
            CurrentConfig = new AppConfig();
        }

        public void Load()
        {
            if (File.Exists(_configFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        CurrentConfig = config;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading config: {ex.Message}");
                    // Keep the default config on failure
                }
            }
            else
            {
                // Create a default config if it doesn't exist
                Save();
            }
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(CurrentConfig, options);
                File.WriteAllText(_configFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}

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
            // The config file lives in the parent directory of the current executable/project (e.g., C:\Users\Md Asif\Documents\Docker\pokeswitch)
            // If running from bin/Debug/..., we should find the correct path, but for simplicity, we hardcode to the known path or relative.
            // Since this app specifically targets a particular user's environment based on the prompt, we will use a direct path for the config to ensure it works reliably in their environment.
            // A more portable way would be AppDomain.CurrentDomain.BaseDirectory + "..." but the user specifically requested:
            // "C:\Users\Md Asif\Documents\Docker\pokeswitch\pokeswitch-config.json"
            
            _configFilePath = @"C:\Users\Md Asif\Documents\Docker\pokeswitch\pokeswitch-config.json";
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

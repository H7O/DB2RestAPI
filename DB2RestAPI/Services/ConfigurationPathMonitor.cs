using Microsoft.Extensions.Primitives;

namespace DBToRestAPI.Services;

/// <summary>
/// Extension methods for enhanced configuration loading with support for dynamic additional configuration files.
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Loads additional configuration files specified in the "additional_configurations:path" section.
    /// Supports both XML and JSON files with automatic reload on change.
    /// </summary>
    /// <param name="configurationBuilder">The configuration builder to add files to</param>
    /// <param name="configuration">The current configuration to read additional paths from</param>
    /// <returns>The configuration builder for method chaining</returns>
    public static IConfigurationBuilder AddDynamicConfigurationFiles(
        this IConfigurationBuilder configurationBuilder, 
        IConfiguration configuration)
    {
        var pathsSection = configuration.GetSection("additional_configurations:path");
        
        if (pathsSection is null || !pathsSection.Exists())
            return configurationBuilder;

        var additionalConfigPaths = pathsSection.Get<List<string>>();
        
        if (additionalConfigPaths is null || !additionalConfigPaths.Any())
            return configurationBuilder;

        foreach (var path in additionalConfigPaths)
        {
            var extension = Path.GetExtension(path);
            
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                configurationBuilder.AddXmlFile(path, optional: false, reloadOnChange: true);
            }
            else if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                configurationBuilder.AddJsonFile(path, optional: false, reloadOnChange: true);
            }
        }

        return configurationBuilder;
    }
}

/// <summary>
/// Monitors changes to the "additional_configurations:path" section and logs when new configuration files
/// are added or removed. Note: Adding/removing paths requires an application restart to take effect,
/// but changes to the content of existing configuration files will reload automatically.
/// 
/// Behavior is controlled by the "additional_configurations:restart_on_path_changes" configuration setting:
/// - true: Automatically stops the application when paths change (allowing IIS/hosting environment to restart it)
/// - false (default): Logs a warning but requires manual application pool recycle/restart
/// </summary>
public class ConfigurationPathMonitor : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationPathMonitor> _logger;
    private readonly IHostApplicationLifetime _appLifetime;
    private IDisposable? _changeTokenRegistration;
    private List<string>? _currentPaths;

    public ConfigurationPathMonitor(
        IConfiguration configuration,
        ILogger<ConfigurationPathMonitor> logger,
        IHostApplicationLifetime appLifetime)
    {
        _configuration = configuration;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _currentPaths = GetCurrentPaths();
        
        var autoRestart = GetAutoRestartSetting();
        
        _logger.LogInformation(
            "Configuration path monitoring started. Current paths: {Paths}. Auto-restart on path changes: {AutoRestart}", 
            _currentPaths is not null && _currentPaths.Any() 
                ? string.Join(", ", _currentPaths) 
                : "none",
            autoRestart);

        // Monitor for changes to the additional_configurations section
        MonitorConfigurationChanges();

        return Task.CompletedTask;
    }

    private void MonitorConfigurationChanges()
    {
        _changeTokenRegistration = ChangeToken.OnChange(
            () => _configuration.GetSection("additional_configurations:path").GetReloadToken(),
            () =>
            {
                var newPaths = GetCurrentPaths();
                
                if (!PathsAreEqual(_currentPaths, newPaths))
                {
                    var autoRestart = GetAutoRestartSetting();
                    
                    _logger.LogWarning(
                        "Additional configuration paths have changed. " +
                        "Old paths: {OldPaths}, New paths: {NewPaths}. " +
                        (autoRestart 
                            ? "Auto-restart is enabled. Stopping application..." 
                            : "Auto-restart is disabled. Manual application pool recycle required for changes to take effect."),
                        _currentPaths is not null && _currentPaths.Any() 
                            ? string.Join(", ", _currentPaths) 
                            : "none",
                        newPaths is not null && newPaths.Any() 
                            ? string.Join(", ", newPaths) 
                            : "none");

                    _currentPaths = newPaths;

                    if (autoRestart)
                    {
                        _logger.LogInformation("Initiating application shutdown to apply new configuration paths...");
                        _appLifetime.StopApplication();
                    }
                }
            });
    }
    
    private bool GetAutoRestartSetting()
    {
        return _configuration.GetValue<bool>("additional_configurations:restart_on_path_changes", defaultValue: false);
    }

    private List<string>? GetCurrentPaths()
    {
        var pathsSection = _configuration.GetSection("additional_configurations:path");
        return pathsSection.Exists() ? pathsSection.Get<List<string>>() : null;
    }

    private static bool PathsAreEqual(List<string>? paths1, List<string>? paths2)
    {
        if (paths1 is null && paths2 is null) return true;
        if (paths1 is null || paths2 is null) return false;
        if (paths1.Count != paths2.Count) return false;

        return paths1.OrderBy(p => p).SequenceEqual(paths2.OrderBy(p => p));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Configuration path monitoring stopped.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _changeTokenRegistration?.Dispose();
    }
}

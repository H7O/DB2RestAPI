# Configuration Management Guide

## Overview
DB2RestAPI supports dynamic configuration file loading with automatic reload capabilities.

## Configuration Files Structure

### Primary Configuration Files
- `config/settings.xml` - Main settings file
- `appsettings.json` - ASP.NET Core application settings

### Additional Configuration Files
Additional configuration files can be specified in `settings.xml` under the `additional_configurations` section.

## How Configuration Reloading Works

### Content Changes (Automatic)
✅ **Changes to file content reload automatically** - No restart required

When you edit the content of any configuration file (settings.xml, sql.xml, api_gateway.xml, etc.), the changes are picked up automatically without requiring an application restart.

**Example:**
- Modifying a SQL query in `sql.xml` → **Auto-reloads**
- Changing a route URL in `api_gateway.xml` → **Auto-reloads**
- Updating connection strings in `settings.xml` → **Auto-reloads**

### Path Changes (Requires Restart)
⚠️ **Adding or removing configuration file paths requires restart**

When you add or remove a `<path>` entry in the `additional_configurations` section, the application needs to restart to load/unload those files.

**Example:**
```xml
<additional_configurations>
  <path>config/sql.xml</path>
  <path>config/api_gateway.xml</path>
  <path>config/new_file.xml</path>  <!-- Adding this requires restart -->
</additional_configurations>
```

## Auto-Restart Configuration

You can control whether the application automatically restarts when configuration paths change.

### Setting: `restart_on_path_changes`

Located in `settings.xml` under `additional_configurations`:

```xml
<additional_configurations>
  <path>config/sql.xml</path>
  <path>config/api_gateway.xml</path>
  <path>config/global_api_keys.xml</path>
  
  <restart_on_path_changes>false</restart_on_path_changes>
</additional_configurations>
```

### Options:

#### `false` (Default - Recommended for Production)
- Application logs a warning when paths change
- **Manual action required**: Recycle the IIS application pool to apply changes
- **Advantage**: You control exactly when the restart happens
- **Use case**: Production environments where you want to plan restarts

**Log message when paths change:**
```
[Warning] Additional configuration paths have changed. 
Old paths: config/sql.xml, config/api_gateway.xml
New paths: config/sql.xml, config/api_gateway.xml, config/new_file.xml
Manual application pool recycle required for changes to take effect.
```

#### `true` (Auto-restart)
- Application automatically stops when paths change
- IIS will automatically restart the application
- **Advantage**: Hands-free operation
- **Use case**: Development/staging environments or when immediate restart is acceptable

**Log messages when paths change:**
```
[Warning] Additional configuration paths have changed. 
Old paths: config/sql.xml, config/api_gateway.xml
New paths: config/sql.xml, config/api_gateway.xml, config/new_file.xml
Auto-restart is enabled. Stopping application...

[Information] Initiating application shutdown to apply new configuration paths...
```

## IIS Behavior

### When `restart_on_path_changes` is `true`:
1. Application detects path change
2. Application calls `IHostApplicationLifetime.StopApplication()`
3. IIS detects the application has stopped
4. IIS automatically starts the application again (default IIS behavior)
5. New configuration paths are loaded

### When `restart_on_path_changes` is `false`:
1. Application detects path change
2. Warning is logged
3. **Manual action required**: Administrator must recycle the IIS Application Pool
   - Via IIS Manager: Right-click Application Pool → Recycle
   - Via PowerShell: `Restart-WebAppPool -Name "YourAppPoolName"`

## Best Practices

### For Production:
1. Keep `restart_on_path_changes` set to `false`
2. Plan configuration path changes during maintenance windows
3. Test changes in staging first
4. Manually recycle application pool after making path changes

### For Development:
1. Set `restart_on_path_changes` to `true` for convenience
2. Monitor logs to see when automatic restarts occur

### For Support Teams:
1. **Adding new configuration files:**
   - Add the `<path>` entry to `settings.xml`
   - If `restart_on_path_changes` is `false`: Manually recycle the app pool
   - If `restart_on_path_changes` is `true`: Wait for automatic restart (check logs)

2. **Editing existing configuration files:**
   - No restart needed - just save the file
   - Changes apply automatically within seconds

3. **Troubleshooting:**
   - Check application logs for configuration reload messages
   - Verify file paths are correct (relative to application base directory)
   - Ensure configuration files are valid XML/JSON format

## Monitoring

The application logs configuration monitoring events at startup:

```
[Information] Configuration path monitoring started. 
Current paths: config/sql.xml, config/api_gateway.xml, config/global_api_keys.xml
Auto-restart on path changes: false
```

This confirms:
- Which configuration files are currently loaded
- Whether auto-restart is enabled

## Summary Table

| Change Type | Example | Auto-Reload? | Restart Required? |
|-------------|---------|--------------|-------------------|
| File content | Edit SQL query in `sql.xml` | ✅ Yes | ❌ No |
| File content | Change route in `api_gateway.xml` | ✅ Yes | ❌ No |
| File content | Update connection string | ✅ Yes | ❌ No |
| Add path | Add new `<path>config/new.xml</path>` | ❌ No | ✅ Yes |
| Remove path | Remove `<path>` entry | ❌ No | ✅ Yes |

---

**Note:** This system provides the flexibility to choose between automatic restarts (development-friendly) and manual control (production-safe) based on your operational requirements.

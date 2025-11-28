# Configuration Management Guide

## Overview
DBToRestAPI supports dynamic configuration file loading with automatic reload capabilities.

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

---

# Settings Encryption

## Overview

DBToRestAPI provides automatic encryption of sensitive configuration values such as connection strings, API secrets, and passwords. The encryption service automatically encrypts unencrypted values on startup and decrypts them at runtime, maintaining security while keeping configuration management simple.

## Encryption Methods

The service supports two encryption methods:

### 1. ASP.NET Core Data Protection API (Cross-Platform)

**Best for:** Linux, macOS, Docker containers, Kubernetes, Azure App Service, or any cross-platform deployment.

Uses the ASP.NET Core Data Protection API with key files stored in a directory you specify. Keys are portable between machines that share the same key directory.

**Configuration:**
```xml
<settings_encryption>
  <data_protection_key_path>./keys/</data_protection_key_path>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
  </sections_to_encrypt>
</settings_encryption>
```

**Or via environment variable:**
```bash
DATA_PROTECTION_KEY_PATH=./keys/
```

> **⚠️ Environment Variable Prefix Note**: If you have configured an `env_var_prefix` in your settings (e.g., `<env_var_prefix>MYAPP_</env_var_prefix>`), you must prepend that prefix to the environment variable name. For example, if your prefix is `MYAPP_`, the environment variable should be `MYAPP_DATA_PROTECTION_KEY_PATH=./keys/`.

### 2. Windows DPAPI (Windows Only)

**Best for:** Windows Server, IIS deployments where keys should be machine-bound.

Uses Windows Data Protection API with `LocalMachine` scope, allowing any user on the machine to decrypt (suitable for IIS app pools with different identities).

**Configuration:** No special configuration needed - DPAPI is used automatically on Windows when `data_protection_key_path` is not specified.

### Encryption Method Resolution

The service determines which encryption method to use in this order:

1. **If `data_protection_key_path` is configured** (config or env var) → **Data Protection API**
2. **Else if running on Windows** → **DPAPI**  
3. **Else** → **Encryption disabled** (passthrough mode)

## Configuration

### Basic Setup

Add to your `settings.xml`:

```xml
<settings_encryption>
  <!-- Optional: prefix for encrypted values (default: "encrypted:") -->
  <encryption_prefix>encrypted:</encryption_prefix>
  
  <!-- Optional: path for Data Protection keys (enables cross-platform mode) -->
  <data_protection_key_path>./keys/</data_protection_key_path>
  
  <!-- Sections to encrypt (paths match IConfiguration key paths) -->
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
    <section>authorize:providers:azure_b2c:client_secret</section>
    <section>file_management:sftp_file_store:remote_site:password</section>
  </sections_to_encrypt>
</settings_encryption>
```

### Encryption Paths

The `<section>` elements specify which configuration paths should be encrypted:

| Path | What Gets Encrypted |
|------|---------------------|
| `ConnectionStrings` | All connection strings under this section |
| `ConnectionStrings:default` | Only the specific "default" connection string |
| `authorize:providers:azure_b2c` | All values under azure_b2c (client_id, client_secret, etc.) |
| `authorize:providers:azure_b2c:client_secret` | Only the specific client_secret value |

## How It Works

### On Application Startup

1. The service scans XML configuration files for values matching the configured sections
2. **Unencrypted values** are automatically encrypted and saved back to the XML file
3. **Encrypted values** are decrypted and cached in memory
4. The application uses decrypted values at runtime while files remain encrypted

### Example Transformation

**Before (unencrypted `settings.xml`):**
```xml
<ConnectionStrings>
  <default>Server=myserver;Database=mydb;User=sa;Password=MySecret123!</default>
</ConnectionStrings>
```

**After first startup (automatically encrypted):**
```xml
<ConnectionStrings>
  <default>encrypted:CfDJ8NhY2kB...very-long-base64-string...</default>
</ConnectionStrings>
```

The original value is now encrypted in the file, but your application code accesses it as if it were unencrypted.

## Cross-Platform Deployment

### Docker / Kubernetes

1. Configure a persistent volume for the keys directory
2. Set the environment variable or mount the keys path

**docker-compose.yml:**
```yaml
services:
  api:
    image: your-api:latest
    environment:
      - DATA_PROTECTION_KEY_PATH=/app/keys/
    volumes:
      - data-protection-keys:/app/keys/

volumes:
  data-protection-keys:
```

**Kubernetes Secret:**
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: data-protection-keys
type: Opaque
# Keys are stored in the mounted volume, not in the secret itself
```

### Azure App Service

1. Configure Azure Blob Storage for key storage (recommended for scale-out)
2. Or use a persistent file share mounted to the app

### Key Portability

- **Same key directory** = values encrypted on one machine can be decrypted on another
- **Different key directory** = values cannot be decrypted (graceful failure)

## Graceful Error Handling

When decryption fails (e.g., wrong encryption method, missing keys), the service:

1. **Logs a detailed error** explaining the likely cause
2. **Returns null** for the affected value
3. **Continues running** - the application doesn't crash

**Example log message:**
```
[Error] Failed to decrypt value using DataProtection. This may indicate the value 
was encrypted with a different method, on a different machine, or the encryption 
keys have been lost. The application will continue but this setting will be null.
```

This allows you to:
- Deploy to a new environment and see which values need re-encryption
- Gracefully handle key rotation scenarios
- Debug encryption issues without application crashes

## Dependency Injection

The service is registered as both `IConfiguration` and `IEncryptedConfiguration`:

```csharp
// In Program.cs
builder.Services.AddSingleton<IEncryptedConfiguration, SettingsEncryptionService>();

// In your services - use either interface
public class MyService
{
    public MyService(IEncryptedConfiguration config)
    {
        // Access encrypted values seamlessly
        var connectionString = config.GetConnectionString("default");
        
        // Check encryption status
        if (config.IsActive)
        {
            Console.WriteLine($"Using {config.ActiveEncryptionMethod}");
        }
    }
}
```

## Properties and Methods

| Member | Description |
|--------|-------------|
| `IsActive` | Whether encryption is enabled |
| `ActiveEncryptionMethod` | The encryption method in use (`None`, `Dpapi`, `DataProtection`) |
| `GetConnectionString(name)` | Get a decrypted connection string |
| `GetValue<T>(key)` | Get a decrypted configuration value |
| `GetSection(key)` | Get a configuration section (hot-reload compatible) |
| `GetValuesUnderPath(path)` | Get all decrypted values under a parent path |

## Best Practices

### Production Deployments

1. **Use Data Protection API** for cross-platform or containerized deployments
2. **Persist the keys directory** - losing keys means losing access to encrypted values
3. **Back up your keys** before major deployments
4. **Use environment variables** for the key path to avoid committing it to source control

### Security Considerations

1. **Never commit unencrypted secrets** - let the service encrypt on first run in a secure environment
2. **Protect the keys directory** - file system permissions should restrict access
3. **Rotate keys periodically** - the Data Protection API supports key rotation
4. **Use separate keys per environment** - dev, staging, and production should have different keys

### Migrating Between Encryption Methods

When switching from DPAPI to Data Protection (or vice versa):

1. The old encrypted values will fail to decrypt (gracefully)
2. Clear the encrypted values in your config files
3. Replace with plain text values
4. Restart the application - values will be re-encrypted with the new method

### Monitoring

Check the startup logs to verify encryption is working:

```
[Information] Settings encryption initialized using DataProtection
[Information] Settings encryption processing complete. 
Encrypted sections: 3, Decrypted values: 5, Cached sections: 42
```


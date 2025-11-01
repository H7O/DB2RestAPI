﻿using DB2RestAPI.Services;
using DB2RestAPI.Settings;
using DB2RestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Com.H.Net.Ssh;
using Com.H.IO;

namespace DB2RestAPI.Middlewares
{

    public class StoreOperationTracker
    {
        public IConfigurationSection Config { get; }
        public bool? WasSuccessful { get; set; }
        public StoreOperationTracker(IConfigurationSection config)
        {
            Config = config;
            WasSuccessful = null;
        }
    }
    public class Step6FileManagement(
        RequestDelegate next,
        SettingsService settings,
        IConfiguration configuration,
        ILogger<Step6FileManagement> logger
        )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly IConfiguration _configuration = configuration;
        private readonly ILogger<Step6FileManagement> _logger = logger;
        private static readonly string _errorCode = "Step 6 - File Management Error";

        public async Task InvokeAsync(HttpContext context)
        {
            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step6FileManagement middleware",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
            #endregion


            var tempFilesTracker = context.RequestServices.GetRequiredService<TempFilesTracker>();
            if (!tempFilesTracker.GetLocalFiles().Any())
            {
                // Proceed to the next middleware
                await this._next(context);
                return;
            }

            #region if no section passed from the previous middlewares, return 500
            IConfigurationSection? section = context.Items.ContainsKey("section")
                ? context.Items["section"] as IConfigurationSection
                : null;


            if (section == null)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );

                return;
            }

            #endregion

            #region check if the request was cancelled
            if (context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Request was cancelled"
                        }
                    )
                    {
                        StatusCode = 400
                    }
                );

                return;
            }
            #endregion

            #region get settings for file management
            // Get file management settings from `file_management` section
            var fileManagementSection = section.GetSection("file_management");
            var defaultFileStoresSettings = this._configuration.GetSection("file_management");
            if (!fileManagementSection.Exists()
                && !defaultFileStoresSettings.Exists())
            {
                // No file management settings found, proceed to next middleware
                await this._next(context);
                return;
            }

            #endregion

            var stores = fileManagementSection.GetValue<string>("stores");
            if (string.IsNullOrWhiteSpace(stores))
            {
                stores = defaultFileStoresSettings.GetValue<string>("stores");
            }

            if (string.IsNullOrWhiteSpace(stores))
            {
                // No stores defined, proceed to next middleware
                await this._next(context);
                return;
            }

            var storeList = stores.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();

            if (!storeList.Any())
            {
                // No valid stores found, proceed to next middleware
                await this._next(context);
                return;
            }

            var localStores = new List<StoreOperationTracker>();
            var sftpStores = new List<StoreOperationTracker>();
            foreach (var store in stores)
            {
                var storeSection = defaultFileStoresSettings.GetSection($"local_file_store:{store}");
                if (storeSection.Exists())
                {
                    localStores.Add(new(storeSection));
                    continue;
                }
                storeSection = defaultFileStoresSettings.GetSection($"sftp_file_store:{store}");
                if (storeSection.Exists())
                {
                    sftpStores.Add(new(storeSection));
                    continue;
                }

                _logger.LogWarning("{time}: Store '{store}' not found in configuration for Step6FileManagement middleware",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                    store);
            }

            // group SFTP stores by connection settings (host, port, username, password) to minimize connections
            var groupedSftpStores = sftpStores
                // filter out stores with missing connection settings
                .Where(s =>
                {
                    return !string.IsNullOrWhiteSpace(s.Config.GetValue<string>("host"))
                        && !string.IsNullOrWhiteSpace(s.Config.GetValue<string>("username"))
                        && !string.IsNullOrWhiteSpace(s.Config.GetValue<string>("password"));
                })
                .GroupBy(s => new
                {
                    Host = s.Config.GetValue<string>("host"),
                    Port = s.Config.GetValue<int>("port", 22),
                    Username = s.Config.GetValue<string>("username"),
                    Password = s.Config.GetValue<string>("password")
                });

            if (!localStores.Any() && !groupedSftpStores.Any())
            {
                // No valid stores found, proceed to next middleware
                await this._next(context);
                return;
            }

            try
            {



                foreach (var entry in localStores)
                {
                    var localPath = entry.Config.GetValue<string>("path");
                    if (string.IsNullOrWhiteSpace(localPath))
                        continue;

                    try
                    {
                        foreach (var file in tempFilesTracker.GetLocalFiles())
                        {
                            var destinationPath = Path.Combine(localPath, Path.GetFileName(file.Value.RelativePath));
                            File.Copy(file.Key, destinationPath);
                            entry.WasSuccessful = true;
                            this._logger.LogDebug("{time}: Copied temp file '{tempFile}' to local store ({local_store_name})  at '{destinationPath}' in Step6FileManagement middleware",
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                file.Key,
                                entry.Config.Key,
                                destinationPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        entry.WasSuccessful = false;
                        bool optional = entry.Config.GetValue<bool>("optional", false);
                        if (optional)
                        {
                            this._logger.LogWarning("{time}: Failed to copy files to optional local store ({local_store_name})"
                                + " in Step6FileManagement middleware, continuing"
                                + Environment.NewLine
                                + " error: {errorMessage}"
                                ,
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                entry.Config.Key,
                                ex.Message
                                );
                            continue;
                        }
                        this._logger.LogError("{time}: Failed to copy files to local store ({local_store_name}) in Step6FileManagement middleware"
                            + Environment.NewLine
                            + " error: {errorMessage}"
                            ,
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                            entry.Config.Key,
                            ex.Message
                            );
                        throw; // Re-throw exception to be caught by outer catch
                    }
                }


                foreach (var group in groupedSftpStores)
                {
                    using Com.H.Net.Ssh.SFtpClient sftpClient = new SFtpClient(
                        group.Key.Host!,
                        group.Key.Port!,
                        group.Key.Username!,
                        group.Key.Password!
                        );
                    // sftpClient.Connect();
                    foreach (var entry in group)
                    {
                        // remote path can be empty, in which case files are uploaded to the user's home directory
                        var remotePath = entry.Config.GetValue<string>("path", string.Empty);

                        try
                        {
                            foreach (var file in tempFilesTracker.GetLocalFiles())
                            {
                                var destinationPath = Path.Combine(remotePath, Path.GetFileName(file.Value.RelativePath))
                                    .UnifyPathSeperator().Replace("\\", "/");
                                using (var fileStream = new FileStream(file.Key, FileMode.Open))
                                {
                                    await sftpClient.UploadAsync(fileStream, destinationPath);
                                }
                                entry.WasSuccessful = true;
                                this._logger.LogDebug("{time}: Uploaded temp file '{tempFile}' to SFTP store ({sftp_store_name}) at '{destinationPath}' in Step6FileManagement middleware",
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                    file.Key,
                                    entry.Config.Key,
                                    destinationPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            entry.WasSuccessful = false;
                            bool optional = entry.Config.GetValue<bool>("optional", false);
                            if (optional)
                            {
                                this._logger.LogWarning("{time}: Failed to upload files to optional SFTP store ({sftp_store_name})"
                                    + " in Step6FileManagement middleware, continuing"
                                    + Environment.NewLine
                                    + " error: {errorMessage}"
                                    ,
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                    entry.Config.Key,
                                    ex.Message
                                    );
                                continue;
                            }
                            this._logger.LogError("{time}: Failed to upload files to SFTP store ({sftp_store_name}) in Step6FileManagement middleware"
                                + Environment.NewLine
                                + " error: {errorMessage}"
                                ,
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                entry.Config.Key,
                                ex.Message
                                );
                            throw; // Re-throw exception to be caught by outer catch
                        }
                    }
                }

                // Proceed to the next middleware
                await this._next(context);
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "{errorCode}: An exception occurred in Step6FileManagement middleware: {message}",
                    _errorCode,
                    ex.Message);
                throw; // Re-throw the exception after logging
            }
            finally
            {
                // rollback local store files
                if (localStores != null)
                {
                    foreach (var entry in localStores.Where(x => x?.WasSuccessful == true))
                    {
                        // delete the files copied to local store
                        var localPath = entry.Config.GetValue<string>("path");
                        if (string.IsNullOrWhiteSpace(localPath))
                            continue;
                        foreach (var file in tempFilesTracker.GetLocalFiles())
                        {
                            try
                            {
                                var destinationPath = Path.Combine(localPath, Path.GetFileName(file.Value.RelativePath));
                                if (File.Exists(destinationPath))
                                {
                                    File.Delete(destinationPath);
                                    this._logger.LogDebug("{time}: Deleted file '{file}' from local store ({local_store_name}) during rollback in Step6FileManagement middleware",
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                        destinationPath,
                                        entry.Config.Key);
                                }
                            }
                            catch (Exception ex)
                            {
                                this._logger.LogWarning("{time}: Failed to delete file '{file}' from local store ({local_store_name}) during rollback in Step6FileManagement middleware"
                                    + Environment.NewLine
                                    + " error: {errorMessage}"
                                    ,
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                    file.Key,
                                    entry.Config.Key,
                                    ex.Message
                                    );
                            }
                        }
                    }
                }
                // rollback SFTP store files
                if (groupedSftpStores != null)
                {
                    foreach (var group in groupedSftpStores)
                    {
                        using Com.H.Net.Ssh.SFtpClient sftpClient = new SFtpClient(
                            group.Key.Host!,
                            group.Key.Port!,
                            group.Key.Username!,
                            group.Key.Password!
                            );
                        // sftpClient.Connect();
                        foreach (var entry in group.Where(x => x?.WasSuccessful == true))
                        {
                            // remote path can be empty, in which case files are uploaded to the user's home directory
                            var remotePath = entry.Config.GetValue<string>("path", string.Empty);
                            foreach (var file in tempFilesTracker.GetLocalFiles())
                            {
                                try
                                {
                                    var destinationPath = Path.Combine(remotePath, Path.GetFileName(file.Value.RelativePath))
                                        .UnifyPathSeperator().Replace("\\", "/");
                                    await sftpClient.DeleteAsync(destinationPath);
                                    this._logger.LogDebug("{time}: Deleted file '{file}' from SFTP store ({sftp_store_name}) during rollback in Step6FileManagement middleware",
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                        destinationPath,
                                        entry.Config.Key);
                                }
                                catch (Exception ex)
                                {
                                    this._logger.LogWarning("{time}: Failed to delete file '{file}' from SFTP store ({sftp_store_name}) during rollback in Step6FileManagement middleware"
                                        + Environment.NewLine
                                        + " error: {errorMessage}"
                                        ,
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"),
                                        file.Key,
                                        entry.Config.Key,
                                        ex.Message
                                        );
                                }
                            }
                        }
                    }
                }

                
                this._logger.LogDebug("{time}: Temporary files cleaned up in Step6FileManagement middleware",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
            }
        }

    }
}

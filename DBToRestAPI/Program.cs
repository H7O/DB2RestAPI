using Com.H.Cache;
using DBToRestAPI.Cache;
using DBToRestAPI.Middlewares;
using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.SqlClient;
using System.Data.Common;



var builder = WebApplication.CreateBuilder(args);


builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddXmlFile("config/settings.xml", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddDynamicConfigurationFiles(builder.Configuration)
    ;

// Load additional configuration files specified in "additional_configurations:path"
builder.Configuration.AddDynamicConfigurationFiles(builder.Configuration);


// Add services to the container.

// Register DbConnection as scoped
builder.Services.AddScoped<DbConnection>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("default")
        ?? throw new InvalidOperationException("Connection string not found");
    return new SqlConnection(connectionString);
});

builder.Services.AddScoped<TempFilesTracker>();

//builder.Services.AddScoped<DbConnection, DbConnection>(
//    provider => new Microsoft.Data.SqlClient.SqlConnection(
//        provider.GetRequiredService<IConfiguration>()
//    .GetConnectionString("default"))
//    );

builder.Services.AddHybridCache();

builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<CacheService>();

builder.Services.AddSingleton<SettingsService>();

builder.Services.AddSingleton<RouteConfigResolver>();

builder.Services.AddSingleton<QueryRouteResolver>();
builder.Services.AddSingleton<ParametersBuilder>();






// builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

builder.Services.AddHttpClient();

builder.Services.AddHttpClient("ignoreCertificateErrors", c =>
{
    // No additional configuration required here
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        // Ignore certificate errors
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    };
});



builder.Services.AddControllers();


var maxFileSize = builder.Configuration.GetValue<long?>("max_payload_size_in_bytes")
    ?? (300 * 1024 * 1024); // Default to 300MB if not found


// In Program.cs
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = maxFileSize;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxFileSize;
});


// Monitor configuration path changes
builder.Services.AddHostedService<ConfigurationPathMonitor>();

var app = builder.Build();


app.UseHttpsRedirection();

app.UseHsts();

app.UseAuthorization();

app.MapControllers();

app.UseMiddleware<Step1ServiceTypeChecks>();        // 1. Route resolution & service type determination
app.UseMiddleware<Step2CorsCheck>();                // 2. CORS headers (must be before auth for preflight)
app.UseMiddleware<Step3ApiKeysCheck>();             // 3. Local API key validation
app.UseMiddleware<Step4JwtAuthorization>();         // 4. JWT/OAuth 2.0 validation
app.UseMiddleware<Step5APIGatewayProcess>();        // 5. API Gateway proxy
app.UseMiddleware<Step6MandatoryFieldsCheck>();     // 6. Parameter validation
app.UseMiddleware<Step7FileUploadManagement>();     // 7. File upload processing
app.UseMiddleware<Step8FileDownloadManagement>();   // 8. File download processing



app.Run();
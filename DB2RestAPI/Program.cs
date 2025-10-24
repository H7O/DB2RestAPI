using Com.H.Cache;
using DB2RestAPI.Cache;
using DB2RestAPI.Middlewares;
using DB2RestAPI.Settings;
using DB2RestAPI.Services;
using System.Data.Common;



var builder = WebApplication.CreateBuilder(args);


builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddXmlFile("config/settings.xml", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddDynamicConfigurationFiles(builder.Configuration)
    ;

// Add services to the container.

builder.Services.AddScoped<DbConnection, DbConnection>(
    provider => new Microsoft.Data.SqlClient.SqlConnection(
        provider.GetRequiredService<IConfiguration>()
    .GetConnectionString("default"))
    );

builder.Services.AddHybridCache();

builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<CacheService>();

builder.Services.AddSingleton<SettingsService>();

builder.Services.AddSingleton<RouteConfigResolver>();

builder.Services.AddSingleton<QueryRouteResolver>();
builder.Services.AddSingleton<ParametersBuilder>();

// Monitor configuration path changes
builder.Services.AddHostedService<ConfigurationPathMonitor>();


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


var app = builder.Build();


app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.UseMiddleware<Step1GlobalApiKeysCheck>();
app.UseMiddleware<Step2ServiceTypeChecks>();
app.UseMiddleware<Step3LocalApiKeysCheck>();
app.UseMiddleware<Step4APIGatewayProcess>();
app.UseMiddleware<Step5MandatoryFieldsCheck>();
// app.UseMiddleware<Step6DBQueryProcess>();


app.Run();
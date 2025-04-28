using Com.H.Cache;
using DB2RestAPI.Cache;
using DB2RestAPI.Middlewares;
using DB2RestAPI.Settings;
using System.Data.Common;



var builder = WebApplication.CreateBuilder(args);


builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddXmlFile("config/settings.xml", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    ;
// read from appsettings.xml the <config> section and add the xml files specified in the <config> section

var config = builder.Configuration.GetSection("additional_configurations:path");
if (config is not null && config.Exists())
{
    var additionalConfigs = config.Get<List<string>>();
    if (additionalConfigs is not null && additionalConfigs.Any())
    {
        foreach (var path in additionalConfigs)
        {
            if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                builder.Configuration.AddXmlFile(path, optional: false, reloadOnChange: true);
                continue;
            }
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: true);
            }
        }
    }
}

// Add services to the container.

builder.Services.AddScoped<DbConnection, DbConnection>(
    provider => new Microsoft.Data.SqlClient.SqlConnection(
        provider.GetRequiredService<IConfiguration>()
    .GetConnectionString("default"))
    );


builder.Services.AddSingleton<CacheService>();

builder.Services.AddSingleton<SettingsService>();

builder.Services.AddSingleton<RouteConfigResolver>();


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
// todo: step 5 should be the caching middleware



app.Run();
using Com.H.Cache;
using DB2RestAPI.JsonBinder;
using DB2RestAPI.Middlewares;
using System.Data.Common;



var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddXmlFile("config/settings.xml", optional: false, reloadOnChange: true)
    .AddXmlFile("config/sql.xml", optional: false, reloadOnChange: true)
    .AddXmlFile("config/api_gateway.xml", optional: false, reloadOnChange: true)
    .AddXmlFile("config/global_api_keys.xml", optional: false, reloadOnChange: true);


// Add services to the container.

builder.Services.AddScoped<DbConnection, DbConnection>(
    provider => new Microsoft.Data.SqlClient.SqlConnection(
        provider.GetRequiredService<IConfiguration>()
    .GetConnectionString("default"))
    );
var cacheSection = builder.Configuration.GetSection("cache");
if (cacheSection is not null && cacheSection.Exists())
{
    // check if cacheSection has a property named "enabled" and if it is set to true
    if (bool.TryParse(cacheSection["enabled"], out bool cacheEnabled) && cacheEnabled)
    {
        builder.Services.AddSingleton(provider =>
        {
            var cache = new MemoryCache();
            bool.TryParse(cacheSection["cache_null_values"], out bool cacheNullValues);
            cache.CacheNullValues = cacheNullValues;
            if (int.TryParse(cacheSection["check_cache_expiry_interval_in_miliseconds"], out int checkCacheExpiryInterval))
                // get main cancellationToken from the host
                cache.StartAutoCleanup(TimeSpan.FromMilliseconds(checkCacheExpiryInterval), 
                    provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            else 
                cache.StartAutoCleanup(TimeSpan.FromMinutes(1), 
                    provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            return cache;
        });
    }
}




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

//builder.Services.AddMvc(options =>
//{
//    options.ModelBinderProviders.Insert(0, new JsonModelBinderProvider());
//});


builder.Services.AddControllers();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.UseMiddleware<ApiKeyMw>();


app.Run();
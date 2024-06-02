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
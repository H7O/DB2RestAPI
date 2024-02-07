using Microsoft.Extensions.Configuration;

namespace DB2RestAPI.Middlewares
{
    public class ApiKeyMw
    {
        private readonly RequestDelegate _next;
        private readonly IConfiguration _configuration;
        const string APIKEY = "x-api-key";
        public ApiKeyMw(RequestDelegate next, IConfiguration configuration)
        {
            _next = next;
            _configuration = configuration;
        }
        public async Task InvokeAsync(HttpContext context)
        {
            if (bool.TryParse(_configuration.GetSection("enable_global_api_keys")?.Value, out bool checkAPIKeys)
                && !checkAPIKeys)
            {
                await _next(context);
                return;
            }
            if (!(context.Request?.Path.Value?.StartsWith("/swagger") == true))
            {
                if (context.Request == null
                    ||
                    !context.Request.Headers.TryGetValue(APIKEY, out
                    var extractedApiKey))
                {
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";

                    await context.Response.WriteAsync(
                        @"{""success"":false, ""message"":""Api key was not provided""}");

                    return;
                }

                var apiKeysSection = _configuration.GetSection("api_keys:key");
                if (apiKeysSection == null || !apiKeysSection.Exists())
                {
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(@"{""success"": false, ""message"":""Api keys section is not defined""}");
                    return;
                }

                if (!apiKeysSection.GetChildren().Any(x => x.Value?.Equals(extractedApiKey.ToString()) == true))
                {
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(@"{""success"":false, ""message"":""Unauthorized client""}");
                    return;
                }
            }
            await _next(context);
        }
    }
}

using Com.H.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DBToRestAPI.Settings.Extensinos
{
    public static class LocalAPIKeysCheck
    {
        /// <summary>
        /// Check if the request has an API key and if it is valid
        /// </summary>
        /// <param name="section"></param>
        /// <returns>
        /// If the request requires an API key before processing
        /// and the API key is not provided or is invalid,
        /// return a response with status code 401
        /// </returns>
        public static ObjectResult? GetFailedAPIKeysCheckResponseIfAny(
            this IConfigurationSection section,
            IConfiguration configuration,
            HttpRequest request
            )
        {
            if (bool.TryParse(configuration.GetSection("enable_global_api_keys")?.Value, out bool globalAPICheckEnabled)
                && !globalAPICheckEnabled)
            {
                var apiKeys = section.GetAPIKeys();

                if (apiKeys.Length > 0)

                {
                    if (request == null
                        ||
                        !request.Headers.TryGetValue("x-api-key", out
                        var extractedApiKey))
                    {
                        return new ObjectResult(new { success = false, message = "API key was not provided" })
                        {
                            StatusCode = 401
                        };
                    }

                    if (!apiKeys.Any(x => x?.Equals(extractedApiKey.ToString()) == true))
                    {
                        //this.Response.StatusCode = 401;
                        //this.Response.ContentType = "application/json";
                        //await this.Response.WriteAsync(@"{""success"":false, ""message"":""Unauthorized client""}");
                        return new ObjectResult(new { success = false, message = "Unauthorized client" })
                        {
                            StatusCode = 401
                        };
                    }
                }
            }
            return null;

        }



        public static string[] GetAPIKeys(this IConfiguration section)
        {
            var apiKeysSection = section.GetSection("api_keys:key");

            if (apiKeysSection != null
                               && apiKeysSection.Exists())
            {
                return apiKeysSection.GetChildren().Select(x => x.Value ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
            }
            return Array.Empty<string>();
        }
    }
}
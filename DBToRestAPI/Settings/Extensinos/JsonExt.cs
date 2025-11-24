using Com.H.Text.Json;
using Microsoft.AspNetCore.Mvc;
namespace DBToRestAPI.Settings.Extensinos
{
    public static class JsonExt
    {
        public static async Task DeferredWriteAsJsonAsync(
            this HttpResponse response, 
            ObjectResult result,
            CancellationToken cancellationToken = default
            )
        {

            // if value is null, return 204
            if (result.Value == null)
            {
                response.StatusCode = result.StatusCode ?? 204;
                return;
            }

            response.StatusCode = result.StatusCode ?? 200;
            response.ContentType = "application/json";

            await result.Value.JsonSerializeAsync(response.BodyWriter, cancellationToken:cancellationToken);

        }
    }
}

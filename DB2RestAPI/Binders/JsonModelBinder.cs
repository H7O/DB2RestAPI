using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System;
using System.Text.Json;
using System.Threading.Tasks;
namespace DB2RestAPI.Binders;


public class JsonModelBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var request = bindingContext.HttpContext.Request;

        // Enable buffering to allow reading the stream multiple times
        request.EnableBuffering();

        try
        {
            // Reset the stream position to ensure we're at the beginning
            request.Body.Position = 0;

            // Deserialize directly from the request body stream
            var model = await JsonSerializer.DeserializeAsync(request.Body, bindingContext.ModelType, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // Reset the stream position after reading
            request.Body.Position = 0;

            bindingContext.Result = ModelBindingResult.Success(model);
        }
        catch (JsonException ex)
        {
            bindingContext.ModelState.AddModelError(bindingContext.ModelName, ex, bindingContext.ModelMetadata);
            bindingContext.Result = ModelBindingResult.Failed();
        }
    }
}

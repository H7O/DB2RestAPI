namespace DB2RestAPI.JsonBinder
{
    using Microsoft.AspNetCore.Mvc.ModelBinding;
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;

    public class JsonModelBinder : IModelBinder
    {
        public async Task BindModelAsync(ModelBindingContext bindingContext)
        {
            if (bindingContext == null)
            {
                throw new ArgumentNullException(nameof(bindingContext));
            }

            var jsonString = string.Empty;

            using (var reader = new StreamReader(bindingContext.HttpContext.Request.Body))
            {
                jsonString = await reader.ReadToEndAsync();
            }

            try
            {
                var model = JsonSerializer.Deserialize(jsonString, bindingContext.ModelType);
                bindingContext.Result = ModelBindingResult.Success(model);
            }
            catch (JsonException)
            {
                bindingContext.Result = ModelBindingResult.Failed();
            }
        }
    }

    public class JsonModelBinderProvider : IModelBinderProvider
    {
        public IModelBinder GetBinder(ModelBinderProviderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Metadata.IsComplexType && !context.Metadata.IsCollectionType)
            {
                return new JsonModelBinder();
            }

            return null!;
        }
    }
}

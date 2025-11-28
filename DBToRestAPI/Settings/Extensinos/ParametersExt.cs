using Com.H.Data.Common;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DBToRestAPI.Settings.Extensinos
{
    public static class ParametersExt
    {

        #region mandatory parameters
        public static string[]? GetMandatoryParameters(
            this IConfigurationSection serviceQuerySection)
        {
            var mandatoryParameters = serviceQuerySection
                .GetSection("mandatory_parameters")?.Value?
                .Split(new char[] { ',', ' ', '\n', '\r' },
                               StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return mandatoryParameters;
        }
        public static ObjectResult? GetFailedMandatoryParamsCheckIfAny(
            this IConfigurationSection serviceQuerySection,
            List<DbQueryParams> qParams,
            string[]? mandatoryParameters = null
            )
        {
            if (mandatoryParameters == null || mandatoryParameters.Length < 1)
                return null;

            List<string> keys = new List<string>();
            foreach (var qParam in qParams)
            {
                IDictionary<string, object>? model = qParam.DataModel?.GetDataModelParameters();
                if (model == null) continue;

                keys = keys.Union(model.Keys).ToList();
            }

            var missingMandatoryParams = mandatoryParameters.Where(x => !(keys.Contains(x) == true)).ToArray();

            if (missingMandatoryParams.Length > 0)
                // return a response with status code 400 (similar to BadRequest)
                return new ObjectResult(new
                {
                    success = false,
                    message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
                })
                {
                    StatusCode = 400
                };

            //return BadRequest(new
            //    {
            //        success = false,
            //        message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
            //    });

            return null;

        }

        #endregion






    }
}

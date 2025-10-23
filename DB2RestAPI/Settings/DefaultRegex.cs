using System.Text.RegularExpressions;

namespace DB2RestAPI.Settings
{
    public class DefaultRegex
    {
        // users can specify either {{var_name}} in their queries or {h{var_name}} for headers, {j{var_name}} for json fields,
        // {qs{var_name}} for query string variables, {r{var_name}} for route variables, and {f{var_name}} for form data variables
        public static readonly string DefaultJsonVariablesPattern = @"(?<open_marker>\{\{|\{j\{)(?<param>.*?)?(?<close_marker>\}\})";
        public static readonly string DefaultHeadersPattern = @"(?<open_marker>\{\{|\{h\{)(?<param>.*?)?(?<close_marker>\}\})";
        public static readonly string DefaultQueryStringPattern = @"(?<open_marker>\{\{|\{qs\{)(?<param>.*?)?(?<close_marker>\}\})";
        public static readonly string DefaultRouteVariablesPattern = @"(?<open_marker>\{\{|\{r\{)(?<param>.*?)?(?<close_marker>\}\})";
        public static readonly string DefaultFormDataVariablesPattern = @"(?<open_marker>\{\{|\{f\{)(?<param>.*?)?(?<close_marker>\}\})";


        public static readonly Regex DefaultRouteVariablesCompiledRegex = new Regex(DefaultRouteVariablesPattern, RegexOptions.Compiled);


        /// <summary>
        /// Pattern to remove the `json/` prefix from the URI route and keeps the rest of the route
        /// e.g., if the route is `json/company/employees?id=123` it will be converted to `company/employees?id=123`
        /// when calling `Regex.Replace(route, DefaultRemoveJsonPrefixFromRoutePattern, string.Empty)`
        /// </summary>
        public static readonly string DefaultRemoveJsonPrefixFromRoutePattern = @"^json\/";
        public static readonly Regex DefaultRemoveJsonPrefixFromRouteCompiledRegex = new Regex(DefaultRemoveJsonPrefixFromRoutePattern);
    }
}

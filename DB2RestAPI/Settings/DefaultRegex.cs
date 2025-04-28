using System.Text.RegularExpressions;

namespace DB2RestAPI.Settings
{
    public class DefaultRegex
    {
        public static readonly string DefaultVariablesPattern = @"(?<open_marker>\{\{)(?<param>.*?)?(?<close_marker>\}\})";
        public static readonly string DefaultHeadersPattern = @"(?<open_marker>\{header\{)(?<param>.*?)?(?<close_marker>\}\})";
        public static readonly string DefaultQueryStringPattern = @"(?<open_marker>\{qs\{)(?<param>.*?)?(?<close_marker>\}\})";
        /// <summary>
        /// Pattern to remove the `json/` prefix from the URI route and keeps the rest of the route
        /// e.g., if the route is `json/company/employees?id=123` it will be converted to `company/employees?id=123`
        /// when calling `Regex.Replace(route, DefaultRemoveJsonPrefixFromRoutePattern, string.Empty)`
        /// </summary>
        public static readonly string DefaultRemoveJsonPrefixFromRoutePattern = @"^json\/";
        public static readonly Regex DefaultRemoveJsonPrefixFromRouteCompiledRegex = new Regex(DefaultRemoveJsonPrefixFromRoutePattern);
    }
}

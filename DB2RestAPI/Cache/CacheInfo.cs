using Com.H.Cache;

namespace DB2RestAPI.Cache
{
    public class CacheInfo
    {
        public TimeSpan Duration { get; set; }
        public string Key { get; set; } = string.Empty;
    }
}

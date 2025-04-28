using Com.H.Cache;
using Com.H.Data.Common;
using Com.H.Threading;
using System.Security.Cryptography;
using System.Text;

namespace DB2RestAPI.Cache
{
    public class CacheService(IConfiguration configuration, IServiceProvider provider)
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly IServiceProvider _provider = provider;
        private MemoryCache? cache;
        private Lock cacheCreationLock = new Lock();
        private readonly AtomicGate cacheCreated = new AtomicGate();


        /// <summary>
        /// Gets the memory cache instance, initializing it if necessary.
        /// </summary>
        public MemoryCache Cache
        {
            get
            {
                // Check if the cache has already been created
                if (this.cacheCreated.IsOpen) return this.cache!;

                // Lock to ensure thread-safe initialization of the cache
                lock (cacheCreationLock)
                {
                    // If the cache is already initialized, return it
                    if (this.cache != null) return this.cache;

                    // Create a new MemoryCache instance
                    this.cache = new MemoryCache();

                    // Retrieve the cache configuration section from the main configuration
                    var cacheSection = _configuration.GetSection("cache");
                    bool cacheNullValues = false;
                    int checkCacheExpiryInterval = 60000; // Default interval in milliseconds

                    // If the cache section exists, read the configuration values
                    if (cacheSection is not null && cacheSection.Exists())
                    {
                        // Try to parse the cache_null_values setting
                        bool.TryParse(cacheSection["cache_null_values"], out cacheNullValues);

                        // Try to parse the check_cache_expiry_interval_in_miliseconds setting
                        int.TryParse(cacheSection["check_cache_expiry_interval_in_miliseconds"], out checkCacheExpiryInterval);
                        if (checkCacheExpiryInterval < 1)
                        {
                            checkCacheExpiryInterval = 60000; // Default interval in milliseconds
                        }
                    }

                    // Set the cache configuration values
                    cache.CacheNullValues = cacheNullValues;

                    // Start the auto-cleanup process for the cache
                    cache.StartAutoCleanup(
                        TimeSpan.FromMilliseconds(checkCacheExpiryInterval),
                        _provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
                    );
                    // Mark the cache as created
                    _ = this.cacheCreated.TryOpen();
                }


                // Return the initialized cache
                return cache;
            }
        }

        public T Get<T>(
            IConfigurationSection serviceSection, 
            List<QueryParams> qParams, 
            Func<T> dataFactory,
            Func<T>? nonCachedDataFactory = null
            )
        {
            var cacheInfo = GetCacheInfo(serviceSection, qParams);
            if (cacheInfo == null)
            {
                if (nonCachedDataFactory != null)
                    return nonCachedDataFactory();
                return dataFactory();
            }
            TimeSpan duration = cacheInfo.Duration;
            return this.Cache.Get<T>(cacheInfo.Key, dataFactory, duration)!;
        }

        /// <summary>
        /// Returns a cache mechanism along with the cache configuration details for a specific service section.
        /// </summary>
        /// <param name="serviceSection">The configuration section for the specific service.</param>
        /// <param name="qParams">A list of query parameters used to construct the cacheService key and to be used to evaluate cache invalidators</param>
        /// <returns>
        /// An instance of <see cref="CacheInfo"/> if caching is enabled and properly configured; otherwise, <c>null</c>.
        /// </returns>
        private CacheInfo? GetCacheInfo(IConfigurationSection serviceSection, List<QueryParams> qParams)
        {
            // Retrieve the cacheService section from the service (query or gateway) section
            var cacheSection = serviceSection.GetSection("cache");
            if (cacheSection == null || !cacheSection.Exists())
                return null;
            // Retrieve the memory cacheService section from the cacheService section
            var memorySection = cacheSection.GetSection("memory");
            if (memorySection == null || !memorySection.Exists())
                return null;

            // Determine the cacheService duration
            int duration = memorySection.GetValue<int?>("duration_in_miliseconds") ??
                this._configuration.GetValue<int?>("cacheService:default_duration_in_miliseconds") ?? -1;
            if (duration < 1)
                return null;

            // Retrieve cacheService invalidators
            var invalidatorsCsv = memorySection.GetValue<string?>("invalidators") ?? string.Empty;
            List<string> invalidators = invalidatorsCsv.Split(new char[] { ',', ' ', '\n', '\r', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            // Construct the cacheService key
            SortedDictionary<string, object> invalidatorsValues = new SortedDictionary<string, object>();
            foreach (var qParam in qParams)
            {
                IDictionary<string, object>? model = qParam.DataModel?.GetDataModelParameters();
                if (model == null) continue;
                foreach (var key in invalidators)
                {
                    if (model.ContainsKey(key))
                        invalidatorsValues[key] = model[key];
                }
            }

            var cacheKey = serviceSection.Key
                + (invalidatorsValues.Count < 1 ? string.Empty 
                : 
                string.Join("|", invalidatorsValues.Select(x => $"{x.Key}={x.Value}")));

            // convert cacheKey to a hash
            cacheKey = cacheKey.ToMD5Hash();
            // Return the CacheService class along with the CacheInfo object
            return new CacheInfo()
            {
                Cache = this.Cache,
                Duration = TimeSpan.FromMilliseconds(duration),
                Key = cacheKey
            };

        }

    }

    public static class CacheServiceExtensions
    {
        public static bool HasCacheConfiguration(this IConfigurationSection serviceQuerySection)
        {
            // Retrieve the cacheService section from the service (query or gateway) section
            var cacheSection = serviceQuerySection.GetSection("cache");
            if (cacheSection == null || !cacheSection.Exists())
                return false;
            // Retrieve the memory cacheService section from the cacheService section
            var memorySection = cacheSection.GetSection("memory");
            if (memorySection == null || !memorySection.Exists())
                return false;

            return true;
        }
    }

    internal static class StringExtensions
    {
        public static string ToMD5Hash(this string text)
        {
            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

    }
}

﻿using System.Net.Http.Headers;

namespace DB2RestAPI.Cache
{
    public class CachableHttpResponseContainer
    {
        private static readonly string[] _headersToExclude = new string[] { "Transfer-Encoding", "Content-Length" };
        public byte[] Content { get; set; } = null!;
        // Content headers
        public IHeaderDictionary ContentHeaders { get; set; } = new HeaderDictionary();

        // Headers
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();


        public static async Task<CachableHttpResponseContainer> Parse(HttpResponseMessage response)
        {
            CachableHttpResponseContainer cachedResponse = new CachableHttpResponseContainer();
            // first - copying http content headers
            foreach(var header in response.Content.Headers)
            {
                cachedResponse.ContentHeaders[header.Key] = header.Value.ToArray();
            }


            // second - copying the http headers
            foreach (var header in response.Headers
                            // exclude `Transfer-Encoding` and `Content-Length` headers
                            // as they are set by the server automatically
                            // and should not be set manually by the proxy
                            // reason for that is that the server will set the `Content-Length` header
                            // based on the actual content length, and if the proxy sets it manually
                            // it may cause issues with the response stream.
                            // And the reason why we exclude `Transfer-Encoding` header is that
                            // the server will set it based on the response content type
                            // and the proxy should not set it manually
                            // as it may cause issues with the response stream.
                            .Where(x => !_headersToExclude.Contains(x.Key))
                )
            {
                cachedResponse.Headers[header.Key] =  header.Value.ToArray();
            }

            // third - copying the content
            cachedResponse.Content = await response.Content.ReadAsByteArrayAsync();

            return cachedResponse;
        }

    }
}

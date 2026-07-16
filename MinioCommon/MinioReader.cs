using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace MinioCommon
{
    /// <summary>
    /// Reads objects from MinIO / S3-compatible storage (download + list).
    ///
    /// Symmetric counterpart of <see cref="MinioUploader"/>: same Minio 7.0.0 SDK
    /// surface, same best-effort error handling (returns bool, never throws), same
    /// thread-safety guarantees on the underlying IMinioClient.
    ///
    /// Used by Minio2MinioSync (m2ms.exe) to pull objects from a source MinIO bucket
    /// before re-uploading them to a target bucket via MinioUploader.
    /// </summary>
    public class MinioReader
    {
        private readonly IMinioClient _client;
        private readonly string _bucket;
        private readonly string _tag;
        private readonly string _pathPrefix;

        public MinioReader(string endpoint, string bucket, string accessKey, string secretKey,
            string region = "us-east-1", string pathPrefix = "", string tag = "")
        {
            _bucket = bucket;
            _tag = tag;
            // Normalize prefix (mirror MinioUploader semantics):
            //   - Convert Windows backslashes → '/' so JSON values like
            //     "data\\bronze\\blobs\\app0043" become "data/bronze/blobs/app0043".
            //   - Ensure trailing '/' so concatenation produces a clean segment boundary.
            _pathPrefix = string.IsNullOrEmpty(pathPrefix)
                ? ""
                : pathPrefix.Replace('\\', '/').TrimEnd('/') + "/";

            var uri = new Uri(endpoint);
            var clientBuilder = new MinioClient()
                .WithEndpoint(uri.Host, uri.Port)
                .WithCredentials(accessKey, secretKey)
                .WithRegion(region);
            if (uri.Scheme == "https")
            {
                clientBuilder = clientBuilder.WithSSL();
            }
            _client = clientBuilder.Build();
        }

        /// <summary>
        /// Prepends the configured path prefix to an object key. Returns the key
        /// unchanged when no prefix is set. Same as MinioUploader's helper.
        /// </summary>
        private string ApplyPrefix(string objectKey)
            => string.IsNullOrEmpty(_pathPrefix) ? objectKey : _pathPrefix + objectKey;

        /// <summary>
        /// Lists all objects under the given (unprefixed) prefix and returns their
        /// full object keys (already with PathPrefix applied). Empty prefix lists
        /// the entire bucket. Returns an empty list on failure (logged).
        /// </summary>
        public async Task<List<string>> ListAllObjectsAsync(string prefix, CancellationToken ct)
        {
            var keys = new List<string>();
            try
            {
                var fullPrefix = ApplyPrefix(prefix);
                var listArgs = new ListObjectsArgs()
                    .WithBucket(_bucket)
                    .WithPrefix(fullPrefix)
                    .WithRecursive(true);

                await foreach (var item in _client
                    .ListObjectsEnumAsync(listArgs, ct)
                    .WithCancellation(ct)
                    .ConfigureAwait(false))
                {
                    if (item != null && !string.IsNullOrEmpty(item.Key))
                    {
                        keys.Add(item.Key);
                    }
                }
            }
            catch (MinioException ex)
            {
                Logger.Error($"{_tag}列出对象失败 (prefix='{prefix}')", ex);
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}列出对象失败 (prefix='{prefix}')", ex);
            }
            return keys;
        }

        /// <summary>
        /// Downloads an object's content into a memory stream. Returns null on
        /// failure (logged). Suitable for small/medium objects; for very large
        /// files consider streaming via GetObjectFileAsync to disk first.
        /// </summary>
        public async Task<MemoryStream> GetObjectAsync(string objectKey, CancellationToken ct)
        {
            try
            {
                var fullKey = ApplyPrefix(objectKey);
                var ms = new MemoryStream();
                var args = new GetObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(fullKey)
                    .WithCallbackStream((stream, _) =>
                    {
                        using (stream)
                        {
                            stream.CopyTo(ms);
                        }
                        return Task.CompletedTask;
                    });

                await _client.GetObjectAsync(args, ct).ConfigureAwait(false);
                ms.Position = 0;
                if (ms.Length == 0)
                {
                    Logger.Warn($"{_tag}下载: 对象为空或不存在: {objectKey}");
                    ms.Dispose();
                    return null;
                }
                return ms;
            }
            catch (ObjectNotFoundException)
            {
                Logger.Warn($"{_tag}下载: 对象不存在: {objectKey}");
                return null;
            }
            catch (MinioException ex)
            {
                Logger.Error($"{_tag}下载失败 '{objectKey}'", ex);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}下载失败 '{objectKey}'", ex);
                return null;
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace MinioCommon
{
    /// <summary>
    /// Uploads and deletes objects on MinIO / S3-compatible storage.
    ///
    /// Reusable across multiple calls (the underlying IMinioClient is thread-safe).
    /// All public methods are async and never throw — failures are logged and the
    /// returned bool indicates success.
    ///
    /// Notes on migration (.NET Framework 4.6.1 + Minio 4.0.0  →  .NET 8 + Minio 7.0.0):
    ///   1) Minio 7.0.0 keeps the 4.x fluent-builder API surface (WithEndpoint /
    ///      WithCredentials / WithSSL / WithRegion / Build → IMinioClient). *Args
    ///      types live in Minio.DataModel.Args.
    ///   2) The 4.0.0 "ListObjectsAsync signature bug" is fixed in 7.0.0 via
    ///      ListObjectsEnumAsync returning IAsyncEnumerable&lt;Item&gt;.
    ///   3) All public methods are async; callers should `await`.
    /// </summary>
    public class MinioUploader
    {
        private readonly IMinioClient _client;
        private readonly string _bucket;
        private readonly string _tag;
        private readonly string _pathPrefix;

        public MinioUploader(string endpoint, string bucket, string accessKey, string secretKey,
            string region = "us-east-1", string pathPrefix = "", string tag = "")
        {
            _bucket = bucket;
            _tag = tag;
            // Normalize prefix: convert Windows backslashes → '/' so JSON values like
            // "data\\bronze\\blobs\\app0043" become "data/bronze/blobs/app0043", and
            // ensure trailing '/' so concatenation produces a clean segment boundary.
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
        /// Prepends the configured path prefix to an object key.
        /// Returns the key unchanged when no prefix is set.
        /// </summary>
        private string ApplyPrefix(string objectKey)
            => string.IsNullOrEmpty(_pathPrefix) ? objectKey : _pathPrefix + objectKey;

        /// <summary>
        /// Uploads a file to MinIO / S3. Returns true on success, false on any failure
        /// (the exception is logged). Does NOT throw.
        /// </summary>
        public async Task<bool> UploadFileAsync(string localFilePath, string objectKey, CancellationToken ct)
        {
            try
            {
                if (!File.Exists(localFilePath))
                {
                    Logger.Error($"{_tag}上传文件不存在: {localFilePath}");
                    return false;
                }

                var contentType = GetContentType(localFilePath);
                var fileInfo = new FileInfo(localFilePath);
                var fullKey = ApplyPrefix(objectKey);

                var args = new PutObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(fullKey)
                    .WithFileName(localFilePath)
                    .WithContentType(contentType);

                _ = await _client.PutObjectAsync(args, ct).ConfigureAwait(false);

                Logger.Info($"{_tag}上传成功: {fullKey} ({fileInfo.Length} 字节, {contentType})");
                return true;
            }
            catch (MinioException ex)
            {
                Logger.Error($"{_tag}MinIO 上传失败 '{objectKey}'", ex);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}上传失败 '{objectKey}'", ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes a single object from MinIO / S3. Missing objects are logged as
        /// warnings but treated as success. Other failures are logged. Does NOT throw.
        /// </summary>
        public async Task<bool> DeleteObjectAsync(string objectKey, CancellationToken ct)
        {
            try
            {
                var fullKey = ApplyPrefix(objectKey);
                var args = new RemoveObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(fullKey);
                await _client.RemoveObjectAsync(args, ct).ConfigureAwait(false);
                Logger.Info($"{_tag}删除成功: {fullKey}");
                return true;
            }
            catch (ObjectNotFoundException)
            {
                Logger.Warn($"{_tag}删除: 对象不存在(可能已删除): {objectKey}");
                return true;
            }
            catch (MinioException ex)
            {
                Logger.Error($"{_tag}删除失败 '{objectKey}'", ex);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}删除失败 '{objectKey}'", ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes all objects under a given prefix (directory) from MinIO / S3.
        /// Lists via the SDK's IAsyncEnumerable ListObjectsEnumAsync, then batch-deletes
        /// via RemoveObjectsAsync. If the batch call fails or reports per-object errors,
        /// falls back to one-by-one removal so a single bad key doesn't kill the batch.
        /// Returns true only when all objects were removed (or were already absent).
        /// The configured PathPrefix is prepended to the search prefix.
        /// </summary>
        public async Task<bool> DeleteObjectsByPrefixAsync(string prefix, CancellationToken ct)
        {
            try
            {
                var fullPrefix = ApplyPrefix(prefix);
                Logger.Info($"{_tag}列出前缀 '{fullPrefix}' 下的对象...");

                var listArgs = new ListObjectsArgs()
                    .WithBucket(_bucket)
                    .WithPrefix(fullPrefix)
                    .WithRecursive(true);

                var keys = new List<string>();
                try
                {
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
                    Logger.Error($"{_tag}列出对象失败", ex);
                    return false;
                }

                if (keys.Count == 0)
                {
                    Logger.Info($"{_tag}前缀 '{fullPrefix}' 下没有对象，无需删除");
                    return true;
                }

                Logger.Info($"{_tag}找到 {keys.Count} 个对象，开始批量删除...");

                var removeArgs = new RemoveObjectsArgs()
                    .WithBucket(_bucket)
                    .WithObjects(keys);

                try
                {
                    var errors = await _client.RemoveObjectsAsync(removeArgs, ct).ConfigureAwait(false);
                    if (errors == null || errors.Count == 0)
                    {
                        Logger.Info($"{_tag}批量删除完成: 成功={keys.Count}, 失败=0");
                        return true;
                    }

                    Logger.Warn($"{_tag}批量删除: SDK 返回 {errors.Count} 个失败, 逐个重试");
                    return await DeleteOneByOneAsync(keys, ct);
                }
                catch (Exception batchEx)
                {
                    Logger.Warn($"{_tag}批量删除请求异常, 回退到逐个删除: {batchEx.Message}");
                    return await DeleteOneByOneAsync(keys, ct);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}批量删除前缀 '{prefix}' 失败", ex);
                return false;
            }
        }

        private async Task<bool> DeleteOneByOneAsync(List<string> keys, CancellationToken ct)
        {
            int successCount = 0;
            int failCount = 0;
            foreach (var key in keys)
            {
                try
                {
                    var args = new RemoveObjectArgs()
                        .WithBucket(_bucket)
                        .WithObject(key);
                    await _client.RemoveObjectAsync(args, ct).ConfigureAwait(false);
                    successCount++;
                }
                catch (ObjectNotFoundException)
                {
                    successCount++;
                }
                catch
                {
                    failCount++;
                }
            }
            Logger.Info($"{_tag}逐个删除完成: 成功={successCount}, 失败={failCount}");
            return failCount == 0;
        }

        private static string GetContentType(string filePath)
        {
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            switch (ext)
            {
                case ".txt": return "text/plain";
                case ".html":
                case ".htm": return "text/html";
                case ".css": return "text/css";
                case ".js": return "application/javascript";
                case ".json": return "application/json";
                case ".xml": return "application/xml";
                case ".csv": return "text/csv";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".doc":
                case ".docx": return "application/msword";
                case ".xls":
                case ".xlsx": return "application/vnd.ms-excel";
                default: return "application/octet-stream";
            }
        }
    }
}
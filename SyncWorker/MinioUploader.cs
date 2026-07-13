using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using MinioCommon;

namespace SyncWorker
{
    /// <summary>
    /// Uploads and deletes objects on MinIO / S3-compatible storage.
    ///
    /// Notes on migration (.NET Framework 4.6.1 + Minio 4.0.0  →  .NET 8 + Minio 7.0.0):
    ///
    /// 1) Minio 7.0.0 (2025-11 release) keeps the 4.x fluent-builder API surface:
    ///    MinioClient.WithEndpoint / WithCredentials / WithSSL / WithRegion / Build(),
    ///    returning <see cref="IMinioClient"/>. *Args types live in
    ///    <c>Minio.DataModel.Args</c> (PutObjectArgs, RemoveObjectArgs, ListObjectsArgs,
    ///    RemoveObjectsArgs). The Item model lives in <c>Minio.DataModel</c>.
    ///
    /// 2) The "ListObjectsAsync signature bug" of Minio 4.0.0 (which forced this class to
    ///    hand-write AWS SigV4 against a raw HttpClient + XML parser) is gone in 7.0.0:
    ///    the SDK exposes <c>ListObjectsEnumAsync</c> returning
    ///    <c>IAsyncEnumerable&lt;Item&gt;</c> with a correct SigV4 implementation against
    ///    any S3-compatible endpoint. The legacy ListObjectKeysRaw / ParseListObjectsResponse
    ///    / Sha256Hex / HmacSha256 / GetSignatureKey methods (~200 LOC) have been deleted.
    ///
    /// 3) The project runtime switched to async/await. All public methods return Task.
    /// </summary>
    internal class MinioUploader
    {
        private readonly IMinioClient _client;
        private readonly string _bucket;
        private readonly string _tag;
        private readonly string _endpoint;
        private readonly string _accessKey;
        private readonly string _secretKey;
        private readonly string _region;
        private readonly string _pathPrefix;

        public MinioUploader(string endpoint, string bucket, string accessKey, string secretKey,
            string region = "us-east-1", string pathPrefix = "", string tag = "")
        {
            _bucket = bucket;
            _tag = tag;
            _endpoint = endpoint.TrimEnd('/');
            _accessKey = accessKey;
            _secretKey = secretKey;
            _region = region;
            // Normalize prefix:
            //   - Convert Windows backslashes to forward slashes so JSON values like
            //     "data\\bronze\\blobs\\app0043" become "data/bronze/blobs/app0043"
            //     (otherwise the whole path collapses into a single backslash-named
            //     "folder" in the S3/MinIO console, instead of nested levels).
            //   - Ensure trailing '/' if non-empty so concatenation with the object key
            //     always produces a clean segment boundary (e.g. 'sub' becomes 'sub/').
            _pathPrefix = string.IsNullOrEmpty(pathPrefix)
                ? ""
                : pathPrefix.Replace('\\', '/').TrimEnd('/') + "/";

            // Build the client. Minio 7.0.0 WithEndpoint takes host + port (not a full URL)
            // and a separate WithSSL() call, mirroring the 4.x API surface. .Build() returns
            // IMinioClient.
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

                // Minio 7.0.0 returns PutObjectResponse (etag + versionId). We discard.
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
        /// warnings but treated as success (the goal — "the object is gone" — is met).
        /// Other failures are logged as errors. Does NOT throw.
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
        /// Lists via the SDK's IAsyncEnumerable ListObjectsEnumAsync (signature
        /// correct since 6.0.3) and uses the batch RemoveObjectsAsync endpoint
        /// (returns Task&lt;IList&lt;DeleteError&gt;&gt;). The SDK batches up to 1000
        /// keys per HTTP delete request automatically. Returns true only when no
        /// per-object delete errors occur (or all errors are "not found"-class).
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

                // Pass full keys (already including the configured prefix) to the batch
                // delete via WithObjects(IList<string>).
                var removeArgs = new RemoveObjectsArgs()
                    .WithBucket(_bucket)
                    .WithObjects(keys);

                try
                {
                    // Returns IList<DeleteError> (per-key failures). If non-empty we still
                    // attempt the per-object fallback so that one bad key doesn't kill the
                    // whole batch.
                    var errors = await _client.RemoveObjectsAsync(removeArgs, ct).ConfigureAwait(false);
                    if (errors == null || errors.Count == 0)
                    {
                        Logger.Info($"{_tag}批量删除完成: 成功={keys.Count}, 失败=0");
                        return true;
                    }

                    Logger.Warn($"{_tag}批量删除: SDK 返回 {errors.Count} 个失败, 逐个重试");
                    int successCount = 0;
                    int failCount = 0;
                    foreach (var key in keys)
                    {
                        try
                        {
                            var oneArgs = new RemoveObjectArgs()
                                .WithBucket(_bucket)
                                .WithObject(key);
                            await _client.RemoveObjectAsync(oneArgs, ct).ConfigureAwait(false);
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
                catch (Exception batchEx)
                {
                    // The whole batch call failed (transport error). Fall back to per-object.
                    Logger.Warn($"{_tag}批量删除请求异常, 回退到逐个删除: {batchEx.Message}");
                    int successCount = 0;
                    int failCount = 0;
                    foreach (var key in keys)
                    {
                        try
                        {
                            var oneArgs = new RemoveObjectArgs()
                                .WithBucket(_bucket)
                                .WithObject(key);
                            await _client.RemoveObjectAsync(oneArgs, ct).ConfigureAwait(false);
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
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}批量删除前缀 '{prefix}' 失败", ex);
                return false;
            }
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

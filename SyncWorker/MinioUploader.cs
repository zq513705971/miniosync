using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Minio;
using Minio.Exceptions;
using MinioCommon;

namespace SyncWorker
{
    /// <summary>
    /// Uploads and deletes objects on MinIO using the official Minio .NET SDK.
    /// For listing objects, uses raw HTTP + AWS Signature V4 (Minio SDK 4.0.0's
    /// ListObjectsAsync has a signature mismatch bug against some servers).
    /// </summary>
    internal class MinioUploader
    {
        private readonly MinioClient _client;
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
            // Normalize prefix: ensure trailing '/' if non-empty (so 'sub' becomes 'sub/')
            _pathPrefix = string.IsNullOrEmpty(pathPrefix) ? "" : pathPrefix.TrimEnd('/') + "/";

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
        {
            return string.IsNullOrEmpty(_pathPrefix) ? objectKey : _pathPrefix + objectKey;
        }

        /// <summary>
        /// Uploads a file to MinIO.
        /// </summary>
        public bool UploadFile(string localFilePath, string objectKey)
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

                _client.PutObjectAsync(args, CancellationToken.None).GetAwaiter().GetResult();

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
        /// Deletes an object from MinIO.
        /// </summary>
        public bool DeleteObject(string objectKey)
        {
            try
            {
                var fullKey = ApplyPrefix(objectKey);
                var args = new RemoveObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(fullKey);

                _client.RemoveObjectAsync(args, CancellationToken.None).GetAwaiter().GetResult();
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
        /// Deletes all objects under a given prefix (directory) from MinIO.
        /// Lists objects via raw HTTP + AWS SigV4 (SDK's ListObjectsAsync is broken),
        /// then deletes each one using the SDK's working DeleteObject.
        /// The configured PathPrefix is prepended to the search prefix.
        /// </summary>
        public bool DeleteObjectsByPrefix(string prefix)
        {
            try
            {
                var fullPrefix = ApplyPrefix(prefix);
                Logger.Info($"{_tag}列出前缀 '{fullPrefix}' 下的对象...");

                var keys = ListObjectKeysRaw(fullPrefix);
                if (keys == null)
                {
                    Logger.Error($"{_tag}列出对象失败: HTTP 请求错误");
                    return false;
                }

                if (keys.Count == 0)
                {
                    Logger.Info($"{_tag}前缀 '{fullPrefix}' 下没有对象，无需删除");
                    return true;
                }

                Logger.Info($"{_tag}找到 {keys.Count} 个对象，开始逐个删除...");

                var successCount = 0;
                var failCount = 0;
                foreach (var key in keys)
                {
                    // DeleteObject receives the FULL key (already includes prefix); strip
                    // it before calling DeleteObject so we don't double-prefix.
                    var keyWithoutPrefix = !string.IsNullOrEmpty(_pathPrefix) && key.StartsWith(_pathPrefix, StringComparison.Ordinal)
                        ? key.Substring(_pathPrefix.Length)
                        : key;
                    if (DeleteObject(keyWithoutPrefix)) successCount++;
                    else failCount++;
                }

                Logger.Info($"{_tag}批量删除完成: 成功={successCount}, 失败={failCount}");
                return failCount == 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}批量删除前缀 '{prefix}' 失败", ex);
                return false;
            }
        }

        /// <summary>
        /// Lists object keys under a prefix using raw HTTP + AWS Signature V4.
        /// Bypasses Minio SDK 4.0.0's ListObjectsAsync which has a signature bug.
        /// Returns null on HTTP/network error, empty list if no objects found.
        /// </summary>
        private List<string> ListObjectKeysRaw(string prefix)
        {
            var keys = new List<string>();
            var continuationToken = "";
            try
            {
                using (var http = new HttpClient())
                {
                    do
                    {
                        // Build sorted query parameters (AWS SigV4 requires alphabetical order by name)
                        var paramDict = new SortedDictionary<string, string>
                        {
                            { "list-type", "2" },
                            { "prefix", prefix },
                            { "max-keys", "1000" }
                        };
                        if (!string.IsNullOrEmpty(continuationToken))
                            paramDict.Add("continuation-token", continuationToken);

                        // Build query string with each name/value URI-encoded
                        var queryParts = new List<string>();
                        foreach (var kv in paramDict)
                            queryParts.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value));
                        var queryString = string.Join("&", queryParts);

                        var url = $"{_endpoint}/{_bucket}?{queryString}";

                        var uriObj = new Uri(url);
                        // Canonical URI: each path segment URI-encoded, / preserved
                        var canonicalUri = "/" + Uri.EscapeDataString(_bucket).Replace("%2F", "/");
                        // Canonical query: name=value pairs sorted, URI-encoded, joined with &
                        var canonicalQuery = queryString;

                        var now = DateTime.UtcNow;
                        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
                        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                        var hostHeader = uriObj.IsDefaultPort
                            ? uriObj.Host
                            : $"{uriObj.Host}:{uriObj.Port}";

                        var payloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // SHA256 of empty string

                        // Canonical headers: lowercase name + ":" + trimmed value + "\n" for each
                        var canonicalHeaders =
                            "host:" + hostHeader.Trim() + "\n" +
                            "x-amz-content-sha256:" + payloadHash.Trim() + "\n" +
                            "x-amz-date:" + amzDate.Trim() + "\n";
                        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";

                        // Canonical request: METHOD\nURI\nQUERY\nHEADERS\n\nSIGNED\nPAYLOAD_HASH
                        // (Blank line between HEADERS and SIGNED — required by SigV4)
                        var canonicalRequest =
                            "GET\n" +
                            canonicalUri + "\n" +
                            canonicalQuery + "\n" +
                            canonicalHeaders +
                            "\n" +
                            signedHeaders + "\n" +
                            payloadHash;

                        var credentialScope = dateStamp + "/" + _region + "/s3/aws4_request";
                        var stringToSign =
                            "AWS4-HMAC-SHA256\n" +
                            amzDate + "\n" +
                            credentialScope + "\n" +
                            Sha256Hex(canonicalRequest);

                        var signingKey = GetSignatureKey(_secretKey, dateStamp, _region, "s3");
                        var signature = HmacSha256Hex(signingKey, stringToSign);

                        var authorization = "AWS4-HMAC-SHA256 Credential=" + _accessKey + "/" + credentialScope +
                                            ", SignedHeaders=" + signedHeaders + ", Signature=" + signature;

                        var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
                        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
                        request.Headers.TryAddWithoutValidation("Authorization", authorization);

                        var response = http.SendAsync(request).GetAwaiter().GetResult();
                        if (!response.IsSuccessStatusCode)
                        {
                            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            Logger.Error($"{_tag}MinIO 列出对象失败: HTTP {(int)response.StatusCode} {response.ReasonPhrase} - {body}");
                            return null;
                        }

                        var xml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        ParseListObjectsResponse(xml, keys, out continuationToken);

                    } while (!string.IsNullOrEmpty(continuationToken));
                }
                return keys;
            }
            catch (Exception ex)
            {
                Logger.Error($"{_tag}列出对象失败", ex);
                return null;
            }
        }

        /// <summary>
        /// Parses MinIO S3 ListObjectsV2 XML response.
        /// Extracts object keys and the continuation token (for pagination).
        /// </summary>
        private static void ParseListObjectsResponse(string xml, List<string> keys, out string nextContinuationToken)
        {
            nextContinuationToken = "";
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("s", "http://s3.amazonaws.com/doc/2006-03-01/");

                var contents = doc.SelectNodes("//s:Contents", ns);
                if (contents != null)
                {
                    foreach (XmlNode node in contents)
                    {
                        var keyNode = node.SelectSingleNode("s:Key", ns);
                        if (keyNode != null)
                        {
                            keys.Add(keyNode.InnerText);
                        }
                    }
                }

                var tokenNode = doc.SelectSingleNode("//s:NextContinuationToken", ns);
                if (tokenNode != null)
                {
                    nextContinuationToken = tokenNode.InnerText;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"解析列表响应失败", ex);
            }
        }

        private static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string HmacSha256Hex(byte[] key, string input)
        {
            using (var hmac = new HMACSHA256(key))
            {
                var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static byte[] HmacSha256(byte[] key, string input)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
            }
        }

        private static byte[] GetSignatureKey(string secretKey, string dateStamp, string region, string service)
        {
            var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
            var kDate = HmacSha256(kSecret, dateStamp);
            var kRegion = HmacSha256(kDate, region);
            var kService = HmacSha256(kRegion, service);
            return HmacSha256(kService, "aws4_request");
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

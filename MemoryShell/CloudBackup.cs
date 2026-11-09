using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MemoryShell;

public enum CloudProtocol
{
    WebDav,
    Ftps,
    Ftp,
    S3
}

public enum CloudBackupPolicy { Interval, OnChange }

public sealed record CloudSettings(
    CloudProtocol Protocol,
    string Endpoint,
    string Username = "",
    string Password = "",
    string Bucket = "",
    string Region = "",
    string Prefix = "",
    int IntervalMinutes = 60,
    bool Enabled = false,
    CloudBackupPolicy Policy = CloudBackupPolicy.Interval)
{
    public override string ToString() => Protocol.ToString();
}

public static class CloudBackup
{
    private const long MaxSnapshotBytes = 128L * 1024 * 1024;
    private const int MaxListBytes = 8 * 1024 * 1024;
    private const string BackupPrefix = "MemoryShell-";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly Regex SnapshotPattern = new(
        @"\AMemoryShell-[0-9]{8}T[0-9]{9}Z-[a-f0-9]{32}\.sqlite3\z", RegexOptions.CultureInvariant);
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        CheckCertificateRevocationList = true
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static void Validate(CloudSettings settings) => _ = GetTarget(settings);

    public static async Task<string> UploadAsync(CloudSettings settings, string localSnapshotPath,
        CancellationToken cancellationToken = default)
    {
        var target = GetTarget(settings);
        using var timeout = WithTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var source = new FileStream(localSnapshotPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        CheckLength(source.Length, MaxSnapshotBytes);
        var header = new byte[16];
        await source.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan().SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException(L10n.Get("S6EF720B36B27"));
        source.Position = 0;

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var name = $"{BackupPrefix}{timestamp}-{Guid.NewGuid():N}.sqlite3";
        if (target.IsFtp)
        {
            await UploadFtpAsync(settings, target, name, source, ct).ConfigureAwait(false);
        }
        else if (settings.Protocol == CloudProtocol.WebDav)
        {
            await UploadWebDavAsync(settings, target, name, source, ct).ConfigureAwait(false);
        }
        else
        {
            var payloadHash = Hex(await SHA256.HashDataAsync(source, ct).ConfigureAwait(false));
            source.Position = 0;
            await PutAsync(settings, target.SnapshotUri(name), source, payloadHash, ct).ConfigureAwait(false);
        }
        return name;
    }

    public static async Task DownloadAsync(CloudSettings settings, string snapshotName, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsSnapshotName(snapshotName))
            throw new ArgumentException(L10n.Get("SD1C008ABD06C"), nameof(snapshotName));

        var target = GetTarget(settings);
        using var timeout = WithTimeout(cancellationToken);
        var ct = timeout.Token;
        var created = false;
        try
        {
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            var uri = target.SnapshotUri(snapshotName);
            if (target.IsFtp)
            {
                await WithFtpAsync(settings, uri, WebRequestMethods.Ftp.DownloadFile, async response =>
                {
                    CheckLength(response.ContentLength, MaxSnapshotBytes);
                    using var content = response.GetResponseStream();
                    await CopyBoundedAsync(content, destination, MaxSnapshotBytes, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            else
            {
                using var request = CreateHttpRequest(settings, HttpMethod.Get, uri);
                using var response = await SendAsync(request, ct).ConfigureAwait(false);
                CheckLength(response.Content.Headers.ContentLength ?? -1, MaxSnapshotBytes);
                await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await CopyBoundedAsync(content, destination, MaxSnapshotBytes, ct).ConfigureAwait(false);
            }
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(destinationPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    public static async Task<IReadOnlyList<string>> ListAsync(CloudSettings settings,
        CancellationToken cancellationToken = default)
    {
        var target = GetTarget(settings);
        using var timeout = WithTimeout(cancellationToken);
        var ct = timeout.Token;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (target.IsFtp)
        {
            await WithFtpAsync(settings, target.Container, WebRequestMethods.Ftp.ListDirectory, async response =>
            {
                using var content = response.GetResponseStream();
                using var buffer = new MemoryStream();
                await CopyBoundedAsync(content, buffer, MaxListBytes, ct).ConfigureAwait(false);
                buffer.Position = 0;
                using var reader = new StreamReader(buffer, Encoding.UTF8);
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    AddSnapshot(names, line[(line.LastIndexOf('/') + 1)..]);
            }, ct).ConfigureAwait(false);
        }
        else if (settings.Protocol == CloudProtocol.WebDav)
        {
            using var request = CreateHttpRequest(settings, new HttpMethod("PROPFIND"), target.Container);
            request.Headers.TryAddWithoutValidation("Depth", "1");
            request.Content = new StringContent("<propfind xmlns=\"DAV:\"><prop><resourcetype/></prop></propfind>",
                Encoding.UTF8, "application/xml");
            var list = await ReadXmlAsync(request, ct).ConfigureAwait(false);
            XNamespace dav = "DAV:";
            if (list.Root?.Name != dav + "multistatus")
                throw new InvalidDataException(L10n.Get("SF0CFDA0CB754"));

            foreach (var item in list.Root.Elements(dav + "response"))
            {
                var href = item.Element(dav + "href")?.Value.Trim();
                if (href is not null && Uri.TryCreate(target.Container, href, out var uri) &&
                    uri.Scheme == target.Container.Scheme && uri.IdnHost == target.Container.IdnHost &&
                    uri.Port == target.Container.Port &&
                    uri.AbsolutePath.StartsWith(target.Container.AbsolutePath, StringComparison.Ordinal))
                {
                    var relative = uri.AbsolutePath[target.Container.AbsolutePath.Length..];
                    AddSnapshot(names, Uri.UnescapeDataString(relative));
                }
            }
        }
        else
        {
            await ListS3Async(settings, target, names, ct).ConfigureAwait(false);
        }

        return names.OrderByDescending(name => name, StringComparer.Ordinal).ToArray();
    }

    private static async Task ListS3Async(CloudSettings settings, Target target, HashSet<string> names,
        CancellationToken ct)
    {
        string? continuation = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var prefix = target.ObjectPrefix;
        do
        {
            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2",
                ["prefix"] = prefix + BackupPrefix
            };
            if (continuation is not null)
                parameters["continuation-token"] = continuation;
            var query = string.Join('&', parameters.Select(p => $"{Escape(p.Key)}={Escape(p.Value)}"));
            using var request = CreateHttpRequest(settings, HttpMethod.Get,
                new Uri($"{target.Container.AbsoluteUri}?{query}"), canonicalQuery: query);
            var list = await ReadXmlAsync(request, ct).ConfigureAwait(false);
            if (list.Root is not { } root || root.Name.LocalName != "ListBucketResult")
                throw new InvalidDataException(L10n.Get("SC607982EFC54"));

            var ns = root.Name.Namespace;
            foreach (var element in root.Elements(ns + "Contents").Elements(ns + "Key"))
            {
                if (element.Value.StartsWith(prefix, StringComparison.Ordinal))
                    AddSnapshot(names, element.Value[prefix.Length..]);
            }

            continuation = null;
            if (string.Equals(root.Element(ns + "IsTruncated")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                continuation = root.Element(ns + "NextContinuationToken")?.Value;
                if (string.IsNullOrEmpty(continuation) || !seenTokens.Add(continuation) || seenTokens.Count > 100)
                    throw new InvalidDataException(L10n.Get("SD1EB89FFAEE4"));
            }
        } while (continuation is not null);
    }

    private static bool IsSnapshotName(string? name) => name is not null && SnapshotPattern.IsMatch(name);

    private static void AddSnapshot(HashSet<string> names, string name)
    {
        if (IsSnapshotName(name))
            names.Add(name);
    }

    private static async Task UploadWebDavAsync(CloudSettings settings, Target target, string name, FileStream source,
        CancellationToken ct)
    {
        var partial = target.SnapshotUri(name + ".pending");
        try
        {
            await PutAsync(settings, partial, source, EmptySha256, ct).ConfigureAwait(false);
            using var move = CreateHttpRequest(settings, new HttpMethod("MOVE"), partial);
            move.Headers.TryAddWithoutValidation("Destination", target.SnapshotUri(name).AbsoluteUri);
            move.Headers.TryAddWithoutValidation("Overwrite", "F");
            using var response = await SendAsync(move, ct).ConfigureAwait(false);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                using var remove = CreateHttpRequest(settings, HttpMethod.Delete, partial);
                using var response = await SendAsync(remove, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (cleanupError is HttpRequestException or OperationCanceledException)
            {
                // Ignore uncommitted staging files.
            }
            throw;
        }
    }

    private static async Task PutAsync(CloudSettings settings, Uri uri, FileStream source, string payloadHash,
        CancellationToken ct)
    {
        using var request = CreateHttpRequest(settings, HttpMethod.Put, uri, payloadHash);
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        request.Content = new StreamContent(source);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.sqlite3");
        request.Content.Headers.ContentLength = source.Length;
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateHttpRequest(CloudSettings settings, HttpMethod method, Uri uri,
        string payloadHash = EmptySha256, string canonicalQuery = "")
    {
        var request = new HttpRequestMessage(method, uri);
        if (settings.Protocol == CloudProtocol.S3)
        {
            SignS3(request, settings, payloadHash, canonicalQuery);
        }
        else if (!string.IsNullOrEmpty(settings.Username))
        {
            var credentials = new byte[Encoding.UTF8.GetByteCount(settings.Username) + 1 + Encoding.UTF8.GetByteCount(settings.Password)];
            var separator = Encoding.UTF8.GetBytes(settings.Username.AsSpan(), credentials);
            credentials[separator] = (byte)':';
            Encoding.UTF8.GetBytes(settings.Password.AsSpan(), credentials.AsSpan(separator + 1));
            try { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials)); }
            finally { CryptographicOperations.ZeroMemory(credentials); }
        }
        return request;
    }

    // Sign the escaped request with AWS SigV4.
    private static void SignS3(HttpRequestMessage request, CloudSettings settings, string payloadHash,
        string canonicalQuery)
    {
        var uri = request.RequestUri!;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;
        if (!uri.IsDefaultPort)
            host += ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

        var time = DateTime.UtcNow;
        var date = time.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var stamp = time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var scope = $"{date}/{settings.Region}/s3/aws4_request";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{stamp}\n";
        var canonicalRequest = $"{request.Method.Method}\n{uri.AbsolutePath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var requestHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        var toSign = $"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{requestHash}";

        var initial = new byte[4 + Encoding.UTF8.GetByteCount(settings.Password)];
        "AWS4"u8.CopyTo(initial);
        Encoding.UTF8.GetBytes(settings.Password.AsSpan(), initial.AsSpan(4));
        byte[]? dateKey = null;
        byte[]? regionKey = null;
        byte[]? serviceKey = null;
        byte[]? signingKey = null;
        try
        {
            dateKey = Hmac(initial, date);
            regionKey = Hmac(dateKey, settings.Region);
            serviceKey = Hmac(regionKey, "s3");
            signingKey = Hmac(serviceKey, "aws4_request");
            var signature = Hex(Hmac(signingKey, toSign));
            request.Headers.Host = host;
            request.Headers.TryAddWithoutValidation("x-amz-date", stamp);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
                $"Credential={settings.Username}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initial);
            if (dateKey is not null) CryptographicOperations.ZeroMemory(dateKey);
            if (regionKey is not null) CryptographicOperations.ZeroMemory(regionKey);
            if (serviceKey is not null) CryptographicOperations.ZeroMemory(serviceKey);
            if (signingKey is not null) CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        response.Dispose();
        var description = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => L10n.Get("S2A16553DD826"),
            HttpStatusCode.NotFound => L10n.Get("S54727E41A2F7"),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => L10n.Get("S576F737C38FD"),
            _ when (int)status is >= 300 and < 400 =>
                L10n.Get("SEB89448AD998"),
            _ => L10n.Get("S1398AE577941")
        };
        throw new HttpRequestException($"{description} HTTP {(int)status}。", null, status);
    }

    private static async Task<XDocument> ReadXmlAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        CheckLength(response.Content.Headers.ContentLength ?? -1, MaxListBytes);
        await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(content, buffer, MaxListBytes, ct).ConfigureAwait(false);
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxListBytes
        });
        return await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
    }

    private static async Task UploadFtpAsync(CloudSettings settings, Target target, string name, FileStream source,
        CancellationToken ct)
    {
        // Rename only after upload completes.
        var partial = target.SnapshotUri(name + ".pending");
        try
        {
            var upload = CreateFtpRequest(settings, partial, WebRequestMethods.Ftp.UploadFile);
            upload.ContentLength = source.Length;
            using (ct.Register(upload.Abort))
            {
                await using (var output = await upload.GetRequestStreamAsync().ConfigureAwait(false))
                    await source.CopyToAsync(output, ct).ConfigureAwait(false);
                using var result = (FtpWebResponse)await upload.GetResponseAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            var rename = CreateFtpRequest(settings, partial, WebRequestMethods.Ftp.Rename);
            rename.RenameTo = name;
            using var abortRename = ct.Register(rename.Abort);
            using var renamed = (FtpWebResponse)await rename.GetResponseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (error is WebException webError)
                webError.Response?.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await WithFtpAsync(settings, partial, WebRequestMethods.Ftp.DeleteFile,
                    _ => Task.CompletedTask, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (cleanupError is WebException or IOException or OperationCanceledException)
            {
                // Ignore missing staging files.
            }

            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task WithFtpAsync(CloudSettings settings, Uri uri, string method,
        Func<FtpWebResponse, Task> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = CreateFtpRequest(settings, uri, method);
        using var abort = ct.Register(request.Abort);
        try
        {
            using var response = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
            await read(response).ConfigureAwait(false);
        }
        catch (WebException ex)
        {
            ex.Response?.Dispose();
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static FtpWebRequest CreateFtpRequest(CloudSettings settings, Uri uri, string method)
    {
#pragma warning disable SYSLIB0014 // The built-in FTP client supports explicit TLS on .NET 8.
        var request = (FtpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
        request.Method = method;
        request.EnableSsl = settings.Protocol == CloudProtocol.Ftps;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.Proxy = null;
        if (!string.IsNullOrEmpty(settings.Username))
            request.Credentials = new NetworkCredential(settings.Username, settings.Password);
        return request;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            CheckLength(total, limit);
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
    }

    private static void CheckLength(long count, long limit)
    {
        if (count > limit)
            throw new InvalidDataException(L10n.Format("SE747E7893712", limit / 1024 / 1024));
    }

    private static CancellationTokenSource WithTimeout(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromMinutes(5));
        return source;
    }

    private static Target GetTarget(CloudSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Protocol))
            throw new ArgumentException(L10n.Get("S752CB4856DC3"));
        if (settings.IntervalMinutes is < 1 or > 10080)
            throw new ArgumentException(L10n.Get("SFEFA9F30E212"));
        var isFtp = settings.Protocol is CloudProtocol.Ftp or CloudProtocol.Ftps;
        var expectedScheme = isFtp ? Uri.UriSchemeFtp : Uri.UriSchemeHttps;
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != expectedScheme || string.IsNullOrEmpty(endpoint.Host) ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 ||
            Uri.UnescapeDataString(endpoint.AbsolutePath).Any(char.IsControl))
            throw new ArgumentException(isFtp
                ? L10n.Get("SD117C71A722B")
                : L10n.Get("SFCFD95CA01CE"));

        if (settings.Protocol != CloudProtocol.S3)
        {
            if (settings.Username?.Any(c => char.IsControl(c) || (c == ':' && !isFtp)) == true ||
                settings.Password?.Any(char.IsControl) == true)
                throw new ArgumentException(L10n.Get("SF3E85941826E"));
            var url = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
            return new Target(url, "", isFtp);
        }

        if (endpoint.AbsolutePath != "/" ||
            string.IsNullOrEmpty(settings.Bucket) ||
            !Regex.IsMatch(settings.Bucket, @"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z") ||
            string.IsNullOrEmpty(settings.Region) || !Regex.IsMatch(settings.Region, @"\A[a-z0-9-]{2,64}\z") ||
            string.IsNullOrEmpty(settings.Username) || !Regex.IsMatch(settings.Username, @"\A[A-Za-z0-9_-]{1,128}\z") ||
            string.IsNullOrWhiteSpace(settings.Password))
            throw new ArgumentException(L10n.Get("SDE7749852620"));

        var prefix = settings.Prefix?.Trim('/') ?? "";
        if (Encoding.UTF8.GetByteCount(prefix) > 800 || prefix.Any(c => char.IsControl(c) || c == '\\') ||
            (prefix.Length > 0 && prefix.Split('/').Any(part => part is "" or "." or "..")))
            throw new ArgumentException(L10n.Get("S3743608527D9"));
        var container = new Uri(endpoint, Escape(settings.Bucket));
        return new Target(container, prefix.Length == 0 ? "" : prefix + "/", false);
    }

    private sealed record Target(Uri Container, string ObjectPrefix, bool IsFtp)
    {
        public Uri SnapshotUri(string name)
        {
            if (Container.Scheme == Uri.UriSchemeFtp || Container.AbsolutePath.EndsWith('/'))
                return new Uri(Container, Escape(name));

            var key = ObjectPrefix + name;
            return new Uri(Container.AbsoluteUri + "/" + string.Join('/', key.Split('/').Select(Escape)));
        }
    }
}

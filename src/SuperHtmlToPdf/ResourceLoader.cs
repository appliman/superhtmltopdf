using System.Buffers;
using System.Net;
using System.Text;

namespace SuperHtmlToPdf;

internal sealed class ResourceLoader(PdfResourcePolicy policy, CancellationToken cancellationToken)
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private long _bytes;
    private int _requests;

    internal async Task<(string Text, Uri? Base)> Document(HtmlSource source)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (source)
        {
            case HtmlSource.Markup markup:
                ArgumentNullException.ThrowIfNull(markup.Html);
                if (markup.BaseUri is { IsAbsoluteUri: false })
                {
                    throw new ArgumentException("L’URI de base doit être absolue.");
                }
                AccountInline(markup.Html);
                return (markup.Html, markup.BaseUri);
            case HtmlSource.LocalFile file:
                var localUri = new Uri(Path.GetFullPath(file.Path));
                var local = await Load(localUri, explicitFile: true).ConfigureAwait(false);
                return (Decode(local.Bytes), local.Uri);
            case HtmlSource.WebPage web:
                if (!web.Uri.IsAbsoluteUri || web.Uri.Scheme is not ("http" or "https"))
                {
                    throw new ArgumentException("Une URL SSR doit utiliser HTTP ou HTTPS.");
                }
                var remote = await Load(web.Uri).ConfigureAwait(false);
                return (Decode(remote.Bytes), remote.Uri);
            default:
                throw new ArgumentException("Source HTML inconnue.", nameof(source));
        }
    }

    internal static string Decode(byte[] bytes)
    {
        var span = bytes.AsSpan();
        var start = span.StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return Encoding.UTF8.GetString(span[start..]);
    }

    internal void AccountInline(string text)
    {
        var length = Encoding.UTF8.GetByteCount(text);
        CheckLength(length);
        AddBytes(length);
    }

    internal byte[] InlineData(string value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++_requests > policy.MaxResources)
        {
            throw new InvalidDataException("Nombre maximal de ressources dépassé.");
        }
        return DecodeData(value);
    }

    private byte[] DecodeData(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("La ressource data doit être encodée en base64.");
        }
        CheckLength((value.Length - comma - 1L) * 3 / 4 - 2);
        var bytes = Convert.FromBase64String(value[(comma + 1)..]);
        CheckLength(bytes.Length);
        AddBytes(bytes.Length);
        return bytes;
    }

    internal async Task<(byte[] Bytes, Uri Uri)> Load(Uri uri, bool explicitFile = false)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_requests > policy.MaxResources)
            {
                throw new InvalidDataException("Nombre maximal de ressources dépassé.");
            }
            if (uri.Scheme == "data")
            {
                return (DecodeData(uri.OriginalString), uri);
            }
            if (uri.IsFile)
            {
                var path = CheckFile(uri, explicitFile);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                CheckLength(file.Length);
                return (await Read(file).ConfigureAwait(false), uri);
            }
            if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                !policy.AllowedOrigins.Any(origin => IsSameOrigin(uri, origin)))
            {
                throw new UnauthorizedAccessException("Origine de ressource non autorisée.");
            }
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new HttpRequestException("Redirection sans destination.");
                uri = new Uri(uri, location);
                if (uri.Scheme is not ("http" or "https"))
                {
                    throw new UnauthorizedAccessException("Protocole de redirection non autorisé.");
                }
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length)
            {
                CheckLength(length);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return (await Read(stream).ConfigureAwait(false), uri);
        }
        throw new HttpRequestException("Trop de redirections HTTP.");
    }

    private static bool IsSameOrigin(Uri uri, Uri origin) =>
        origin.IsAbsoluteUri && uri.Scheme == origin.Scheme && uri.IdnHost == origin.IdnHost && uri.Port == origin.Port;

    private string CheckFile(Uri uri, bool explicitFile)
    {
        if (uri.IsUnc || uri.Host.Length != 0)
        {
            throw new UnauthorizedAccessException("Les fichiers réseau ne sont pas acceptés.");
        }
        var path = Path.GetFullPath(uri.LocalPath);
        if (OperatingSystem.IsWindows() && path.AsSpan(Path.GetPathRoot(path)!.Length).Contains(':'))
        {
            throw new UnauthorizedAccessException("Flux de fichier non autorisé.");
        }
        if (!explicitFile && !policy.AllowedDirectories.Any(root => Within(root, path)))
        {
            throw new UnauthorizedAccessException("Répertoire hors de la liste autorisée.");
        }
        for (FileSystemInfo? current = new FileInfo(path); current != null;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Les liens symboliques et jonctions sont refusés.");
            }
        }
        return path;
    }

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private async Task<byte[]> Read(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(0, 32768), cancellationToken).ConfigureAwait(false)) != 0)
            {
                CheckLength(output.Length + count);
                AddBytes(count);
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void AddBytes(int length)
    {
        _bytes = checked(_bytes + length);
        if (_bytes > policy.MaxTotalBytes)
        {
            throw new InvalidDataException("Volume total maximal des ressources dépassé.");
        }
    }

    private void CheckLength(long length)
    {
        if (length > policy.MaxResourceBytes)
        {
            throw new InvalidDataException("Ressource trop volumineuse.");
        }
    }
}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace NimBus.WebApp.Middleware;

/// <summary>
/// Serves the <c>.br</c>/<c>.gz</c> siblings that the Vite build (vite-plugin-compression2)
/// emits next to every asset over its 1 KB threshold. Runs <em>before</em> the SPA
/// static-file middleware: when the client advertises a matching <c>Accept-Encoding</c>
/// and the precompressed sibling exists on disk, the request path is rewritten to that
/// sibling, <c>Content-Encoding</c> + <c>Vary</c> are set, and the static-file middleware
/// streams the already-compressed bytes verbatim — no per-request CPU. When no sibling
/// exists (sub-1 KB files, images) the request falls through unchanged and the original
/// asset is served normally; dynamic response compression remains the fallback for those.
/// </summary>
/// <remarks>
/// The original <c>Content-Type</c> is preserved by handing the SPA static-file middleware
/// a content-type provider (see <see cref="PrecompressedContentTypeProvider"/>) that
/// resolves the <c>.js.br</c> extension back to the underlying asset type. Because the
/// rewrite sets <c>Content-Encoding</c> before the response body is written, the dynamic
/// response-compression middleware sees the header and never double-encodes.
/// </remarks>
public sealed class PrecompressedStaticFileMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IFileProvider _fileProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PrecompressedStaticFileMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="fileProvider">File provider rooted at the SPA static-asset directory.</param>
    public PrecompressedStaticFileMiddleware(RequestDelegate next, IFileProvider fileProvider)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
    }

    /// <summary>
    /// Rewrites the request to a precompressed sibling when content negotiation allows it,
    /// then invokes the rest of the pipeline.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task that completes when the downstream pipeline has finished.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TryNegotiate(context))
        {
            // Advertise that the response varies by Accept-Encoding so caches
            // don't serve a brotli payload to a client that only speaks gzip.
            context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool Accepts(StringValues acceptEncoding, string encoding)
    {
        foreach (var header in acceptEncoding)
        {
            if (header == null)
            {
                continue;
            }

            foreach (var part in header.Split(','))
            {
                var token = part.Trim();
                var quality = 1.0;

                // Split off the q-value (e.g. "gzip;q=1.0", "br;q=0").
                var semicolon = token.IndexOf(';', StringComparison.Ordinal);
                if (semicolon >= 0)
                {
                    var parameters = token[(semicolon + 1)..];
                    token = token[..semicolon].Trim();
                    var qIndex = parameters.IndexOf("q=", StringComparison.OrdinalIgnoreCase);
                    if (qIndex >= 0 && double.TryParse(
                            parameters[(qIndex + 2)..].Trim(),
                            NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture,
                            out var q))
                    {
                        quality = q;
                    }
                }

                if (token.Equals(encoding, StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("*", StringComparison.Ordinal))
                {
                    // q=0 is an explicit refusal of this encoding (RFC 9110).
                    return quality > 0;
                }
            }
        }

        return false;
    }

    private bool TryNegotiate(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        if (!request.Path.HasValue)
        {
            return false;
        }

        var path = request.Path.Value!;

        // Never rewrite the SPA entry document — it must stay negotiable on disk
        // and its caching headers are set explicitly downstream.
        if (path.EndsWith('/'))
        {
            return false;
        }

        var accept = request.Headers.AcceptEncoding;

        // Brotli first (smaller), then gzip — mirrors the siblings Vite emits.
        if (Accepts(accept, "br") && TryRewrite(context, ".br", "br"))
        {
            return true;
        }

        if (Accepts(accept, "gzip") && TryRewrite(context, ".gz", "gzip"))
        {
            return true;
        }

        return false;
    }

    private bool TryRewrite(HttpContext context, string siblingExtension, string contentEncoding)
    {
        var originalPath = context.Request.Path.Value;
        var siblingPath = originalPath + siblingExtension;

        var fileInfo = _fileProvider.GetFileInfo(siblingPath);
        if (!fileInfo.Exists || fileInfo.IsDirectory)
        {
            return false;
        }

        context.Request.Path = new PathString(siblingPath);
        context.Response.Headers[HeaderNames.ContentEncoding] = contentEncoding;
        return true;
    }
}

/// <summary>
/// Resolves precompressed sibling extensions back to the underlying asset's
/// <c>Content-Type</c> so the rewritten <c>.js.br</c> / <c>.css.gz</c> response still
/// carries e.g. <c>text/javascript</c> rather than <c>application/octet-stream</c>.
/// </summary>
public sealed class PrecompressedContentTypeProvider : IContentTypeProvider
{
    private readonly FileExtensionContentTypeProvider _inner = new();

    /// <summary>
    /// Maps the given subpath to its content type, stripping a trailing
    /// <c>.br</c>/<c>.gz</c> sibling extension first.
    /// </summary>
    /// <param name="subpath">The request subpath (possibly ending in <c>.br</c> or <c>.gz</c>).</param>
    /// <param name="contentType">The resolved content type, when known.</param>
    /// <returns><see langword="true"/> when the content type could be resolved.</returns>
    public bool TryGetContentType(string subpath, [MaybeNullWhen(false)] out string contentType)
    {
        ArgumentNullException.ThrowIfNull(subpath);

        if (subpath.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
        {
            subpath = subpath[..^".br".Length];
        }
        else if (subpath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            subpath = subpath[..^".gz".Length];
        }

        return _inner.TryGetContentType(subpath, out contentType);
    }
}

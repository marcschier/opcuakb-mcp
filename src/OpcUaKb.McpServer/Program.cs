using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base — Custom MCP Server
// Exposes structured search tools over the Azure AI Search index.
//
// Supports two transport modes:
//   HTTP/SSE (default): Run as a web server for hosted deployment
//   stdio:              Pass --stdio for local Copilot CLI usage
//
// Required env vars: SEARCH_ENDPOINT, SEARCH_API_KEY
// Optional: SEARCH_INDEX_NAME (default: opcua-content-index-v2)
//           AOAI_ENDPOINT — enables search_docs_rag tool (KB retrieve + GPT-4o)
//           AOAI_API_KEY  — AOAI key auth (falls back to Managed Identity)
//           KB_NAME       — knowledge base name (default: opcua-kb)
//           STORAGE_ACCOUNT_NAME — enables nodeset_ref=blob:... and the
//                                  POST /upload-nodeset endpoint
//
// Rate limiting env vars:
//   MCP_API_KEY           — API key for authenticated access
//   MCP_REQUIRE_AUTH      — "true" to reject all unauthenticated requests
//   MCP_ANON_RATE_LIMIT   — Max requests/min for anonymous callers (default: 10)
//   MCP_AUTH_RATE_LIMIT   — Max requests/min for authenticated callers (default: 0 = unlimited)
//
// NodeSet input limits:
//   MCP_NODESET_MAX_BYTES — default 52428800 (50 MB) — caps both inline
//                           uploads and outbound URL fetches.
//   MCP_NODESET_URL_ALLOWLIST — comma-separated host patterns for
//                               nodeset_url fetches.
// ═══════════════════════════════════════════════════════════════════════

const long DefaultMaxRequestBodyBytes = 64L * 1024 * 1024;

var useStdio = args.Contains("--stdio");

if (useStdio)
{
    // stdio transport for local CLI usage — no auth or rate limiting needed
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton<SearchService>();
    builder.Services.AddSingleton<KbService>();
    builder.Services.AddSingleton(_ => NodeSetLoader.CreateDefault());
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "opcua-kb", Version = "1.0.0" })
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(SearchNodesTool).Assembly);
    await builder.Build().RunAsync();
}
else
{
    // HTTP/SSE transport for hosted deployment
    var builder = WebApplication.CreateBuilder(args);

    // Allow the upload endpoint to receive NodeSets up to MCP_NODESET_MAX_BYTES
    // (default 50 MB) — Kestrel's default 30 MB cap is too small.
    var maxFetchBytes = long.TryParse(
        Environment.GetEnvironmentVariable("MCP_NODESET_MAX_BYTES"), out var mfb) && mfb > 0
            ? mfb : NodeSetLoader.DefaultMaxFetchBytes;
    var maxBodyBytes = Math.Max(maxFetchBytes + 1024 * 1024, DefaultMaxRequestBodyBytes);
    builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxBodyBytes);

    builder.Services.AddSingleton<SearchService>();
    builder.Services.AddSingleton<KbService>();
    builder.Services.AddSingleton(_ => NodeSetLoader.CreateDefault());

    // Optional BlobContainerClient for the upload endpoint. When the env
    // var isn't set, /upload-nodeset returns 503.
    var storageAccountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME");
    var nodesetContainerName = Environment.GetEnvironmentVariable("MCP_NODESET_CONTAINER") ?? "opcua-content";
    BlobContainerClient? uploadContainer = null;
    if (!string.IsNullOrWhiteSpace(storageAccountName))
    {
        uploadContainer = new BlobServiceClient(
            new Uri($"https://{storageAccountName}.blob.core.windows.net"),
            new DefaultAzureCredential())
            .GetBlobContainerClient(nodesetContainerName);
    }

    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "opcua-kb", Version = "1.0.0" })
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly(typeof(SearchNodesTool).Assembly);

    // Configuration. We split two keys:
    //   • MCP_API_KEY (read) — gates anonymous read tier (falls back to
    //                          SEARCH_API_KEY for backward compat).
    //   • MCP_UPLOAD_KEY (write) — REQUIRED for /upload-nodeset. Defaults
    //                              to MCP_API_KEY when not set; falls back
    //                              to nothing (endpoint disabled) when MCP_API_KEY
    //                              also isn't set. Never falls back to SEARCH_API_KEY.
    var readApiKey = Environment.GetEnvironmentVariable("MCP_API_KEY")
        ?? Environment.GetEnvironmentVariable("SEARCH_API_KEY");
    var explicitMcpApiKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
    var uploadApiKey = Environment.GetEnvironmentVariable("MCP_UPLOAD_KEY")
        ?? explicitMcpApiKey;

    var requireAuth = string.Equals(
        Environment.GetEnvironmentVariable("MCP_REQUIRE_AUTH"), "true", StringComparison.OrdinalIgnoreCase);
    var anonRateLimit = int.TryParse(Environment.GetEnvironmentVariable("MCP_ANON_RATE_LIMIT"), out var arl) ? arl : 10;
    var authRateLimit = int.TryParse(Environment.GetEnvironmentVariable("MCP_AUTH_RATE_LIMIT"), out var atrl) ? atrl : 0;

    // Rate limiting — partitioned by authenticated vs anonymous (per-IP)
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
            await context.HttpContext.Response.WriteAsync(
                """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Rate limit exceeded. Provide an api-key header for higher limits."},"id":""}""", ct);
        };

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var hasValidKey = !string.IsNullOrEmpty(readApiKey)
                && context.Request.Headers.TryGetValue("api-key", out var key)
                && key == readApiKey;

            if (hasValidKey)
            {
                // Authenticated tier — unlimited or configurable
                return authRateLimit > 0
                    ? RateLimitPartition.GetFixedWindowLimiter("authenticated", _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = authRateLimit,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter("authenticated");
            }

            // Anonymous tier — rate limited per IP
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"anon:{ip}", _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = anonRateLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });
    });

    var app = builder.Build();

    // Middleware order: rate limiting → OAuth endpoint handling → auth → MCP
    app.UseRateLimiter();

    // Handle OAuth discovery and fallback endpoints explicitly.
    // MCP clients MUST check /.well-known/oauth-authorization-server per RFC 8414.
    // Returning 404 tells clients there is no authorization server — auth is not required.
    // The fallback /authorize, /token, /register paths return 400 with a clear message
    // instead of falling through to the MCP handler (which would return confusing errors).
    app.Map("/authorize", () => Results.Json(
        new { error = "unsupported_grant_type", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));
    app.Map("/token", () => Results.Json(
        new { error = "unsupported_grant_type", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));
    app.Map("/register", () => Results.Json(
        new { error = "invalid_client", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));

    // Auth middleware — block or allow anonymous based on config.
    // /upload-nodeset always uses its own explicit MCP_UPLOAD_KEY/MCP_API_KEY
    // (never SEARCH_API_KEY) — see below.
    app.Use(async (context, next) =>
    {
        var isUploadEndpoint = context.Request.Path.StartsWithSegments("/upload-nodeset");

        if (isUploadEndpoint)
        {
            if (string.IsNullOrEmpty(uploadApiKey))
            {
                context.Response.StatusCode = 503;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"upload_disabled","error_description":"Set MCP_API_KEY (or MCP_UPLOAD_KEY) to enable the upload endpoint."}""");
                return;
            }

            var providedUpload = context.Request.Headers.TryGetValue("api-key", out var u) ? u.ToString() : null;
            if (providedUpload != uploadApiKey)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"unauthorized","error_description":"Valid api-key header required for /upload-nodeset."}""");
                return;
            }

            await next();
            return;
        }

        // MCP / other endpoints — fall back to the original read-side policy.
        if (!string.IsNullOrEmpty(readApiKey) && requireAuth)
        {
            var hasValidKey = context.Request.Headers.TryGetValue("api-key", out var providedKey)
                && providedKey == readApiKey;
            if (!hasValidKey)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Unauthorized: provide a valid api-key header"},"id":""}""");
                return;
            }
        }

        await next();
    });

    // POST /upload-nodeset — content-addressed NodeSet upload.
    // Accepts raw XML body OR multipart/form-data with a `file` part.
    // Returns { nodeset_ref, size_bytes, sha256 }. Blob lifecycle deletes
    // these after 1 day (see infra/main.bicep storageLifecyclePolicy).
    app.MapPost("/upload-nodeset", async (HttpRequest req, CancellationToken ct) =>
    {
        if (uploadContainer == null)
        {
            return Results.Json(
                new { error = "upload_disabled", error_description = "STORAGE_ACCOUNT_NAME is not configured." },
                statusCode: 503);
        }

        Stream? content = null;
        try
        {
            content = await ExtractUploadStreamAsync(req, maxFetchBytes, ct);
            if (content == null)
            {
                return Results.Json(
                    new { error = "no_content", error_description = "Provide an XML body, or multipart/form-data with a 'file' part." },
                    statusCode: 400);
            }

            var (sha256, buf) = await NodeSetLoader.HashAndBufferAsync(content, maxFetchBytes, ct);
            using (buf)
            {
                var path = $"uploads/{sha256}.xml";
                var blob = uploadContainer.GetBlobClient(path);
                await blob.UploadAsync(buf, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/xml" },
                }, ct);

                return Results.Json(new
                {
                    nodeset_ref = $"blob:{path}",
                    size_bytes = buf.Length,
                    sha256,
                });
            }
        }
        catch (NodeSetLoadException ex)
        {
            return Results.Json(
                new { error = "upload_failed", error_description = ex.Message },
                statusCode: 400);
        }
        catch (RequestFailedException ex)
        {
            return Results.Json(
                new { error = "storage_failure", error_description = $"{ex.Status} {ex.ErrorCode}" },
                statusCode: 502);
        }
        finally
        {
            if (content != null) await content.DisposeAsync();
        }
    });

    app.MapMcp();
    app.Run();
}

static async Task<Stream?> ExtractUploadStreamAsync(HttpRequest req, long maxBytes, CancellationToken ct)
{
    var contentType = req.ContentType ?? "";
    if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
    {
        var media = MediaTypeHeaderValue.Parse(contentType);
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary)) return null;

        var reader = new MultipartReader(boundary, req.Body);
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) != null)
        {
            var disp = section.GetContentDispositionHeader();
            if (disp == null) continue;
            var name = disp.Name.HasValue ? disp.Name.Value : null;
            var isFile = !string.IsNullOrEmpty(disp.FileName.Value) || !string.IsNullOrEmpty(disp.FileNameStar.Value);
            if (isFile && string.Equals(name, "file", StringComparison.OrdinalIgnoreCase))
            {
                // Size-bound the section as we read it so an oversized file
                // is rejected before it lands in memory.
                using var bounded = new SizeBoundedStream(section.Body, maxBytes, leaveInnerOpen: true);
                var ms = new MemoryStream();
                await bounded.CopyToAsync(ms, ct);
                ms.Position = 0;
                return ms;
            }
        }
        return null;
    }

    // Treat anything else as a raw XML body — text/xml, application/xml,
    // application/octet-stream, or empty Content-Type all work.
    if (req.ContentLength == 0) return null;
    return req.Body;
}


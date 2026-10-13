using System.IO;
using System.Net;
using System.Reflection;
using CccPrint.Configuration;
using CccPrint.Models;
using CccPrint.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CccPrint.Services;

public sealed class UpdateServer
{
    private readonly AppOptions _options;
    private readonly LibraryCatalogService _catalog;
    private readonly SourceBaselineService _baseline;
    private readonly DraftPreparationService _drafts;
    private readonly AppUpdatePackageService _appUpdates;
    private readonly TestLibraryService _testLibrary;
    private readonly PublishedLibraryService _publishedLibrary;
    private readonly ClientInventoryService _clientInventory;
    private WebApplication? _application;

    public UpdateServer(AppOptions options, LibraryCatalogService catalog)
    {
        _options = options;
        _catalog = catalog;
        _baseline = new SourceBaselineService(
            options.SourcePath,
            options.DataRoot);
        _drafts = new DraftPreparationService(
            options.SourcePath,
            options.DataRoot);
        _appUpdates = new AppUpdatePackageService(options);
        _testLibrary = new TestLibraryService(options.DataRoot, _drafts);
        _publishedLibrary = new PublishedLibraryService(
            options.DataRoot,
            options.SourcePath);
        _clientInventory = new ClientInventoryService(options.DataRoot);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null)
        {
            return;
        }

        if (_catalog.SourceAvailable)
        {
            await _baseline.EnsureInitializedAsync(cancellationToken);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.ListenAnyIP(_options.ServerPort);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var id = Guid.NewGuid().ToString("N");
            var started = System.Diagnostics.Stopwatch.StartNew();
            AdminLog.Write("http", "started", id: id);
            try
            {
                await next(context);
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
                AdminLog.Write($"http:{route}", context.RequestAborted.IsCancellationRequested ? "aborted" :
                    context.Response.StatusCode >= 400 ? "failed" : "completed",
                    reason: $"http_{context.Response.StatusCode}", id: id, elapsedMs: started.ElapsedMilliseconds);
            }
            catch (Exception exception)
            {
                AdminLog.Write("http", "failed", reason: exception.GetType().Name, id: id, elapsedMs: started.ElapsedMilliseconds);
                throw;
            }
        });

        app.MapGet("/", () => Results.Redirect("/api/health"));

        app.MapGet("/api/health", () =>
        {
            var libraries = _catalog.Scan();
            return Results.Ok(new
            {
                status = "ok",
                service = "ccc-print-update-server",
                appVersion = GetAppVersion(),
                role = "admin",
                hostName = Environment.MachineName,
                port = _options.ServerPort,
                sourceAvailable = _catalog.SourceAvailable,
                libraryCount = libraries.Count,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        });

        app.MapGet(
            "/api/v1/library/manifest",
            async (CancellationToken token) =>
            {
                return Results.Ok(await GetEffectiveManifestAsync(token));
            });

        app.MapGet(
            "/api/v1/library/status",
            async (CancellationToken token) =>
            {
                var manifest = await GetEffectiveManifestAsync(token);
                return Results.Ok(new LibrarySyncStatus(
                    manifest.Version,
                    manifest.IsTestData,
                    manifest.GeneratedAtUtc,
                    manifest.Files.Count,
                    manifest.Notice));
            });

        app.MapGet(
            "/api/v1/library/files/{version}/{fileId}",
            async (
                string version,
                string fileId,
                CancellationToken token) =>
            {
                var path = await _publishedLibrary.GetFilePathAsync(
                    version,
                    fileId,
                    token);
                return path is null
                    ? Results.NotFound()
                    : Results.File(
                        path,
                        contentType: "application/pdf",
                        fileDownloadName: Path.GetFileName(path),
                        enableRangeProcessing: true);
            });

        app.MapGet(
            "/api/v1/app-update/manifest",
            async (CancellationToken token) =>
                Results.Ok(await _appUpdates.GetManifestAsync(token)));

        app.MapGet("/api/v1/app-update/package", () =>
        {
            var packagePath = _appUpdates.GetAvailablePackagePath();
            return packagePath is null
                ? Results.NotFound()
                : Results.File(
                    packagePath,
                    contentType: "application/octet-stream",
                    fileDownloadName: Path.GetFileName(packagePath),
                    enableRangeProcessing: true);
        });

        app.MapGet(
            "/api/v1/library/test-manifest",
            async (CancellationToken token) =>
                Results.Ok(await _testLibrary.GetManifestAsync(token)));

        app.MapGet(
            "/api/v1/library/test-files/{batchId}/{fileId}",
            async (
                string batchId,
                string fileId,
                CancellationToken token) =>
            {
                var path = await _testLibrary.GetFilePathAsync(
                    batchId,
                    fileId,
                    token);
                return path is null
                    ? Results.NotFound()
                    : Results.File(
                        path,
                        contentType: "application/pdf",
                        fileDownloadName: Path.GetFileName(path),
                        enableRangeProcessing: true);
            });

        app.MapPost(
            "/api/v1/client/inventory",
            async (
                ClientLibraryInventory inventory,
                CancellationToken token) =>
            {
                try
                {
                    await _clientInventory.SaveAsync(inventory, token);
                    return Results.Ok(new { saved = true });
                }
                catch (InvalidDataException exception)
                {
                    return Results.BadRequest(new
                    {
                        error = exception.Message
                    });
                }
            });

        app.MapGet(
            "/api/v1/admin/changes",
            async (HttpContext context, CancellationToken token) =>
            {
                if (!IsLoopback(context))
                {
                    return Results.NotFound();
                }

                if (!_catalog.SourceAvailable)
                {
                    return Results.Json(
                        new { error = "源资料目录当前不可用。" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return Results.Ok(await _baseline.GetChangesAsync(token));
            });

        app.MapPost(
            "/api/v1/admin/baseline",
            async (HttpContext context, CancellationToken token) =>
            {
                if (!IsLoopback(context))
                {
                    return Results.NotFound();
                }

                if (!_catalog.SourceAvailable)
                {
                    return Results.Json(
                        new { error = "源资料目录当前不可用。" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return Results.Ok(await _baseline.ResetAsync(token));
            });

        app.MapGet(
            "/api/v1/admin/pending",
            async (HttpContext context, CancellationToken token) =>
            {
                if (!IsLoopback(context))
                {
                    return Results.NotFound();
                }

                return Results.Ok(await _drafts.GetPendingAsync(token));
            });

        app.MapPost(
            "/api/v1/admin/prepare",
            async (
                HttpContext context,
                PrepareDraftRequest request,
                CancellationToken token) =>
            {
                if (!IsLoopback(context))
                {
                    return Results.NotFound();
                }

                if (!_catalog.SourceAvailable)
                {
                    return Results.Json(
                        new { error = "源资料目录当前不可用。" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    return Results.Ok(
                        await _drafts.PrepareAsync(
                            request.RelativePaths,
                            cancellationToken: token));
                }
                catch (WpsBusyException exception)
                {
                    return Results.Conflict(new
                    {
                        code = "wps-in-use",
                        error = exception.Message
                    });
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (FileNotFoundException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (NotSupportedException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
            });

        await app.StartAsync(cancellationToken);
        _application = app;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_application is null)
        {
            return;
        }

        await _application.StopAsync(cancellationToken)
            .ConfigureAwait(false);
        await _application.DisposeAsync()
            .AsTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        _application = null;
    }

    private static string GetAppVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
               ?? "0.6.29";
    }

    private async Task<LibrarySyncManifest> GetEffectiveManifestAsync(
        CancellationToken cancellationToken)
    {
        var published = await _publishedLibrary.GetCurrentManifestAsync(
            cancellationToken);
        var test = await _testLibrary.GetManifestAsync(cancellationToken);
        return MergePublishedAndTest(published, test);
    }

    private static LibrarySyncManifest MergePublishedAndTest(
        LibrarySyncManifest published,
        LibrarySyncManifest test)
    {
        var hasPublished =
            !string.IsNullOrWhiteSpace(published.Version);
        var hasTest =
            !string.IsNullOrWhiteSpace(test.Version) &&
            test.Files.Count > 0;
        if (!hasPublished)
        {
            return hasTest ? test : published;
        }

        if (!hasTest)
        {
            return published;
        }

        var publishedSources = published.Files
            .Select(file => file.SourceRelativePath)
            .Concat(published.RemovedSourcePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingTestFiles = test.Files
            .Where(file => !publishedSources.Contains(
                file.SourceRelativePath))
            .ToArray();
        if (remainingTestFiles.Length == 0)
        {
            return published;
        }

        var remainingTestGroups = remainingTestFiles
            .Select(file => file.SourceRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new LibrarySyncManifest(
            Math.Max(published.SchemaVersion, test.SchemaVersion),
            $"{published.Version}__{test.Version}",
            true,
            published.GeneratedAtUtc > test.GeneratedAtUtc
                ? published.GeneratedAtUtc
                : test.GeneratedAtUtc,
            $"{published.Notice}；另保留 {remainingTestGroups} 组临时测试资料，" +
            "对应原件正式发布后会自动替换。",
            published.Files
                .Concat(remainingTestFiles)
                .OrderBy(
                    file => file.LibraryName,
                    StringComparer.Ordinal)
                .ThenBy(
                    file => file.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray())
        {
            RemovedSourcePaths = published.RemovedSourcePaths
        };
    }

    private static bool IsLoopback(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        return remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
    }
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Webhooks.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> NewDatabaseAsync()
    {
        var name = $"h{Guid.NewGuid():N}"[..20];
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>A webhook receiver on a real port: records what it gets, answers with the next queued status (200 when none).</summary>
public sealed class Receiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; private set; } = "";
    public ConcurrentQueue<(string Body, string? Signature, string? Event)> Got { get; } = new();
    public ConcurrentQueue<int> Answers { get; } = new();

    private Receiver(WebApplication app) => _app = app;

    public static async Task<Receiver> StartAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        var r = new Receiver(app);
        app.MapPost("/hook", async (HttpRequest req) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            r.Got.Enqueue((body, req.Headers["Winche-Signature"], req.Headers["Winche-Event"]));
            return Results.StatusCode(r.Answers.TryDequeue(out var s) ? s : 200);
        });
        await app.StartAsync();
        r.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/hook";
        return r;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>Enough of an archive for FileStorage: objects exist once "uploaded".</summary>
internal sealed class MemoryArchive : IArchive
{
    public ConcurrentDictionary<string, long> Objects { get; } = new();   // key → size
    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) =>
        Task.FromResult(new UploadSession { Url = $"mem://{path}" });
    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, DownloadHeaders? headers = null, CancellationToken ct = default) =>
        Task.FromResult(new DownloadSession { Url = $"mem://{path}", ExpiresAt = default });
    public Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(Objects.TryGetValue(path, out var size) ? new ArchiveObjectInfo(size, "\"e\"") : (ArchiveObjectInfo?)null);
    public Task DeleteAsync(string path, CancellationToken ct = default) { Objects.TryRemove(path, out _); return Task.CompletedTask; }
    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default) { foreach (var p in paths) Objects.TryRemove(p, out _); return Task.CompletedTask; }
    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var k in Objects.Keys) yield return new ArchivedObject(k, DateTime.UtcNow);
    }
    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => Task.FromResult("u");
    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) => Task.FromResult(new UploadSession { Url = "mem://part" });
    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => Task.FromResult<string?>("e");
    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) => Task.FromResult<IEnumerable<FilePart>>([]);
    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => Task.CompletedTask;
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Auth;
using Winche.Auth.DependencyInjection;
using Winche.Core;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;
using Winche.Storage.DependencyInjection;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;
using Winche.Storage.Services;

namespace Winche.Webhooks.Tests;

/// <summary>Webhooks set up in settings (signed, retried), across database, storage and auth.</summary>
[Collection("postgres")]
public class HooksTests(PostgresFixture pg)
{
    private const string MasterKey = "webhooks-tests-master-key-0123456789abcdef";

    private static async Task<IHost> StartAsync(string db, MemoryArchive archive)
    {
        var b = Host.CreateApplicationBuilder();
        b.Services.AddWincheConfig(o => { o.ConnectionString = db; o.MasterKey = MasterKey; });
        b.Services.AddWincheHooks(o =>
        {
            o.ConnectionString = db;
            o.PollInterval = TimeSpan.FromMilliseconds(50);
            o.RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)];
        });
        b.Services.AddWincheDatabase(o =>
        {
            o.ConnectionString = db;
            o.UseRulesText("match {document=**} { allow read, write: if true; }");
            o.ChangeFeed = o.ChangeFeed with { PollInterval = TimeSpan.FromMilliseconds(100) };
            o.UseHooks(h => h.PublishChanges());
        });
        b.Services.AddWincheStorage(o =>
        {
            o.ConnectionString = db;
            o.HookDelivery = new HookDeliveryOptions { PollInterval = TimeSpan.FromMilliseconds(100) };
            o.UseHooks(h => h.PublishChanges());
        });
        b.Services.AddSingleton<IArchive>(archive);
        b.Services.AddWincheAuth(o => { o.ConnectionString = db; o.Issuer = "https://test.example"; o.EmailPassword(); });
        var host = b.Build();
        await host.InitializeWincheDatabaseAsync();
        await host.InitializeWincheStorageAsync();
        await host.StartAsync();
        return host;
    }

    private static async Task SaveAsync(IHost host, params HookSettings[] hooks) =>
        await host.Services.GetRequiredService<SettingsStore>().SaveAsync(HooksSettings.Area,
            JsonSerializer.SerializeToNode(new HooksSettings { Hooks = hooks }, SettingsArea.Json)!, "admin@x", "Hooks");

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 150; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] f) => f.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task Webhooks_are_signed_matched_by_path_and_retried()
    {
        await using var receiver = await Receiver.StartAsync();
        receiver.Answers.Enqueue(503);   // the first try fails; the retry gets through
        using var host = await StartAsync(await pg.NewDatabaseAsync(), new MemoryArchive());
        await host.Services.GetRequiredService<SecretStore>().SetAsync(HookRunner.SecretName("visits"), "whsec_test", null);
        await SaveAsync(host, new HookSettings
        {
            Id = "visits", Name = "New visits", When = new HookTrigger { Source = "database", Events = ["created"], Path = "patients/{patientId}/visits/{visitId}" },
            Do = new HookActionSettings { Url = receiver.Url },
        });

        var db = host.Services.GetRequiredService<DocumentDatabase>();
        await db.WriteAsync([new SetWrite { Path = "patients/p1/visits/v1", Fields = Fields(("ward", new StringValue("A3")), ("beds", new IntegerValue(2))) }]);
        await db.WriteAsync([new SetWrite { Path = "staff/s1", Fields = Fields(("name", new StringValue("Ben"))) }]);   // no hook for this

        await Until(() => Task.FromResult(receiver.Got.Count >= 2), "the retried webhook");
        var (body, signature, evt) = receiver.Got.Last();
        Assert.True(WebhookSignature.Verify("whsec_test", signature, body));
        Assert.Equal("database.created", evt);
        var json = JsonNode.Parse(body)!;
        Assert.Equal(("p1", "v1", "A3", 2L), (json["vars"]!["patientId"]!.GetValue<string>(), json["vars"]!["visitId"]!.GetValue<string>(),
            json["data"]!["fields"]!["ward"]!.GetValue<string>(), json["data"]!["fields"]!["beds"]!.GetValue<long>()));
        Assert.Equal(receiver.Got.First().Body, body);   // the same event, sent again

        var outbox = host.Services.GetRequiredService<HookOutbox>();
        await Until(async () => (await outbox.ListAsync("visits")).FirstOrDefault()?.Status == HookOutbox.Delivered, "the delivery to be recorded");
        var delivery = Assert.Single(await outbox.ListAsync("visits"));
        Assert.Equal((2, 200), (delivery.Attempts, delivery.LastStatus));
        await host.StopAsync();
    }

    [Fact]
    public async Task A_refusal_fails_at_once_and_can_be_retried_from_the_log()
    {
        await using var receiver = await Receiver.StartAsync();
        receiver.Answers.Enqueue(404);
        using var host = await StartAsync(await pg.NewDatabaseAsync(), new MemoryArchive());
        await SaveAsync(host, new HookSettings { Id = "all", Name = "Everything", When = new HookTrigger { Source = "database" }, Do = new HookActionSettings { Url = receiver.Url } });
        await host.Services.GetRequiredService<DocumentDatabase>().WriteAsync([new SetWrite { Path = "notes/n1", Fields = Fields(("t", new StringValue("x"))) }]);
        var outbox = host.Services.GetRequiredService<HookOutbox>();
        await Until(async () => (await outbox.ListAsync("all")).FirstOrDefault()?.Status == HookOutbox.Failed, "the refusal");
        var failed = Assert.Single(await outbox.ListAsync("all"));
        Assert.Equal((1, 404), (failed.Attempts, failed.LastStatus));
        Assert.True(await outbox.RetryAsync(failed.Id));
        await Until(async () => (await outbox.ListAsync("all"))[0].Status == HookOutbox.Delivered, "the retry");
        await host.StopAsync();
    }

    [Fact]
    public async Task Hooks_saved_in_the_console_call_webhooks_only()
    {
        using var host = await StartAsync(await pg.NewDatabaseAsync(), new MemoryArchive());
        var refused = await Assert.ThrowsAsync<SettingsValidationException>(() => SaveAsync(host, new HookSettings
        {
            Id = "user-files", Name = "Delete a user's files", When = new HookTrigger { Source = "auth", Events = ["deleted"] },
            Do = new HookActionSettings { Kind = "storage.deleteFiles", Options = new Dictionary<string, string> { ["folder"] = "users/{uid}/" } },
        }));
        Assert.Contains(refused.Errors, e => e.Contains("hooks call webhooks"));
        await host.StopAsync();
    }
}

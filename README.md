# Winche.Webhooks

Webhooks for the Winche stack, set up in the Winche console: when documents, files or users change, **call a webhook**
(signed, retried, logged). No code per webhook — the app adds the packages once. To *do* something on the server
instead (delete a user's files, set claims, …), write a function with that trigger (Winche.Functions): its runs are
delivered through this package's queue too.

Database, storage and Winche Auth publish their own changes (Winche.Core's `WincheChange`); this package receives them
(it is an `IWincheChangeSink`), matches them against the webhooks and delivers them. No adapter packages.

| Source | Changes |
| --- | --- |
| Winche.Database (`PublishChanges()`) | created, updated, deleted |
| Winche.Storage (`PublishChanges()`) | uploaded, deleted, metadataUpdated |
| Winche.Auth (always) | created, deleted, disabled, enabled, claimsChanged, signedIn |

## Install

```sh
dotnet add package Winche.Webhooks
```

## Setup

```csharp
builder.Services.AddWincheConfig(c => { c.ConnectionString = conn; c.MasterKey = masterKey; });
builder.Services.AddWincheHooks(o => o.ConnectionString = conn);

builder.Services.AddWincheDatabase(o => { /* … */ o.UseHooks(h => h.PublishChanges()); });
builder.Services.AddWincheStorage(o => { /* … */ o.UseHooks(h => h.PublishChanges()); });
builder.Services.AddWincheAuth(o => { /* … */ });
```

Then, in each product's **Webhooks** page in the console: *when* (events, path pattern) → the address to call.

## Path patterns

Winche.Core's path patterns, the same as in rules and function triggers: `patients/{patientId}` matches one segment per
name; `uploads/{rest=**}` matches one or more (and `{dir=**}/report.pdf` zero or more). Captures are sent in the
body's `vars`. Events also carry their own values: `{path}`, `{id}`, `{collection}` (documents), `{path}`, `{name}`, `{directory}` (files), `{uid}`, `{email}`,
`{provider}` (users).

## Webhooks

A `POST` with a JSON body:

```json
{ "id": "…", "hook": { "id": "…", "name": "…" }, "source": "database", "type": "created",
  "path": "patients/p1", "at": "2026-11-12T20:00:00Z", "vars": { "patientId": "p1" },
  "data": { "path": "patients/p1", "fields": { "ward": "A3" } }, "before": null }
```

Headers: `Winche-Event` (`database.created`), `Winche-Delivery` (the event id — the same on retries, so receivers can
ignore repeats) and `Winche-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body">` with the hook's signing
secret (shown in the console). In .NET: `WebhookSignature.Verify(secret, header, body)`.

Any 2xx is delivered. 5xx, 408, 429 and network errors are retried after 30 s, 2 min, 10 min, 30 min, 1 h, 3 h and 6 h;
other answers fail at once. Each delivery is listed in the console (each product's Webhooks → Deliveries: waiting,
retrying with its tries so far and the last reason, delivered, failed), and failed ones can be retried from there. A
webhook that can't be reached is logged by name with the reason; a function trigger's failure is logged by the function.

## Guarantees

Database and storage events come from those packages' durable hooks, so each change is delivered at least once.
User events are queued right after the change is saved (a crash in between can lose one). Function triggers run from
the same queue and are retried the same way.

Hooks saved before 1.0 with a ready-made action (delete documents, delete files, copy claims) are dropped when the app
starts, with a version in the hooks' history saying so: write a function for them.

## License

See [LICENSE](LICENSE).

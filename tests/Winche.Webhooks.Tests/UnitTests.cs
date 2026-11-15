namespace Winche.Webhooks.Tests;

public class PathPatternTests
{
    /// <summary>As Winche.Auth publishes it.</summary>
    private static readonly WincheChangeSource AuthSource = new("auth", "Users", ["created", "deleted", "disabled", "enabled", "claimsChanged", "signedIn"],
        HasPaths: false, Variables: ["uid", "email", "provider"]);

    [Theory]
    [InlineData("patients/{patientId}", "patients/p1", "patientId=p1")]
    [InlineData("patients/{patientId}/visits/{visitId}", "patients/p1/visits/v9", "patientId=p1;visitId=v9")]
    [InlineData("uploads/{rest=**}", "uploads/a/b/c.pdf", "rest=a/b/c.pdf")]
    [InlineData("uploads/{rest=**}", "uploads", null)]   // a trailing {rest=**} is one or more segments, as in rules
    [InlineData("{dir=**}/report.pdf", "a/b/report.pdf", "dir=a/b")]
    [InlineData("patients/{patientId}", "patients/p1/visits/v9", null)]
    [InlineData("patients/{patientId}", "staff/s1", null)]
    public void Patterns_match_and_capture(string pattern, string path, string? expected)
    {
        var captures = PathPattern.Parse(pattern).Match(path);
        Assert.Equal(expected, captures is null ? null : string.Join(";", captures.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Theory]
    [InlineData("{a=**}/{b=**}", "only one recursive capture")]
    [InlineData("a/p{id}", "mixes text and a capture")]
    [InlineData("a/{id}/{id}", "captured twice")]
    [InlineData("a/*", "use {name}")]
    public void Bad_patterns_say_why(string pattern, string error)
    {
        Assert.False(PathPattern.TryParse(pattern, out _, out var why));
        Assert.Contains(error, why);
    }

    [Fact]
    public void Templates_fill_and_report_what_they_lack()
    {
        Assert.Equal("users/u1/", Template.Fill("users/{uid}/", new Dictionary<string, string> { ["uid"] = "u1" }));
        Assert.Equal(["x"], Template.Missing("a/{uid}/{x}", new HashSet<string> { "uid" }));
        Assert.Throws<InvalidOperationException>(() => Template.Fill("{nope}", new Dictionary<string, string>()));
    }

    [Fact]
    public void Signatures_verify_and_expire()
    {
        var at = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Sign("whsec_x", "{\"a\":1}", at);
        Assert.True(WebhookSignature.Verify("whsec_x", header, "{\"a\":1}", now: at));
        Assert.False(WebhookSignature.Verify("whsec_y", header, "{\"a\":1}", now: at));
        Assert.False(WebhookSignature.Verify("whsec_x", header, "{\"a\":2}", now: at));
        Assert.False(WebhookSignature.Verify("whsec_x", header, "{\"a\":1}", now: at.AddMinutes(10)));
    }

    [Fact]
    public void Settings_problems_name_the_hook_and_what_to_fix()
    {
        var catalog = new HookCatalog([Winche.Database.DependencyInjection.DatabaseChangesExtensions.Source, AuthSource], []);
        var settings = new HooksSettings
        {
            Hooks =
            [
                new HookSettings { Id = "a", Name = "Files", When = new HookTrigger { Source = "storage" }, Do = new HookActionSettings { Url = "https://x" } },
                new HookSettings { Id = "b", Name = "Typo", When = new HookTrigger { Source = "database", Events = ["removed"], Path = "a/{x" }, Do = new HookActionSettings { Url = "ftp://x" } },
                new HookSettings { Id = "c", Name = "Claims", When = new HookTrigger { Source = "auth", Path = "users/{uid}" }, Do = new HookActionSettings { Kind = "nope" } },
            ],
        };
        var problems = settings.Problems(catalog);
        Assert.Contains(problems, p => p.StartsWith("'Files': this app has no 'storage' events"));
        Assert.Contains(problems, p => p.Contains("'removed' isn't a database event"));
        Assert.Contains(problems, p => p.Contains("http(s) address"));
        Assert.Contains(problems, p => p.Contains("auth events have no path"));
        Assert.Contains(problems, p => p.Contains("'Claims': hooks call webhooks") && p.Contains("function"));
    }

    [Fact]
    public void Hooks_of_before_with_a_ready_made_action_are_dropped_and_webhooks_kept()
    {
        var settings = new HooksSettings
        {
            Hooks =
            [
                new HookSettings { Id = "a", Name = "Notify", When = new HookTrigger { Source = "database" }, Do = new HookActionSettings { Url = "https://x.example.com" } },
                new HookSettings { Id = "b", Name = "Clean up", When = new HookTrigger { Source = "auth" }, Do = new HookActionSettings { Kind = "storage.deleteFiles" } },
            ],
        };
        Assert.Equal(["a"], settings.WithoutActions().Hooks.Select(h => h.Id));
    }
}

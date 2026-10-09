using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Dashboards;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// The dashboard builder's routes: who may open, change, share and delete a dashboard, what a stale
/// save and an invalid definition get, and what the audit records.
/// </summary>
/// <remarks>
/// The real host with the store in memory: under test are the API's decisions. What PostgreSQL
/// itself guarantees - grants through roles, the revision guard, cascades - is proven against the
/// database in <see cref="DashboardStoreTests"/>.
/// </remarks>
public sealed class DashboardEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Dictionary<string, AuthenticatedUser> People = new(StringComparer.Ordinal)
    {
        ["owner"] = Person(1, Permissions.DashboardView, Permissions.DashboardCreate, Permissions.DashboardEdit, Permissions.DashboardShare, Permissions.DataExport),
        ["editor"] = Person(2, Permissions.DashboardView, Permissions.DashboardEdit, Permissions.DashboardShare),
        ["editorWithoutEdit"] = Person(3, Permissions.DashboardView),
        ["viewer"] = Person(4, Permissions.DashboardView),
        ["stranger"] = Person(5, Permissions.DashboardView, Permissions.DashboardCreate, Permissions.DashboardEdit, Permissions.DashboardShare),
        ["admin"] = Person(6, Permissions.DashboardView, Permissions.DashboardCreate, Permissions.DashboardEdit, Permissions.DashboardShare, Permissions.DashboardManage),
    };

    private const string Definition = """
        {"schemaVersion":1,"defaults":{"countBy":"bindings"},"refreshMinutes":0,"widgets":[
          {"id":"w_kpi0000001","type":"metric.kpi","configVersion":1,"title":null,"subtitle":null,
           "config":{"metric":"activeBindings"},"overrides":{},"layout":{"x":0,"y":0,"w":2,"h":3}}]}
        """;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly InMemoryDashboards _store = new();

    public DashboardEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, args) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(People.TryGetValue((string)args[0]!, out var person)
                    ? new ResolvedSession(Guid.NewGuid(), person, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))
                    : null),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, args) =>
            {
                lock (_audited)
                {
                    _audited.Add((AuditEntry)args[0]!);
                }

                return Task.CompletedTask;
            }));
            services.AddSingleton<IDashboardStore>(_store);
        }));

    private static AuthenticatedUser Person(long id, params string[] permissions) =>
        new(id, $"user{id}", $"Person {id}", false, permissions.ToFrozenSet(StringComparer.Ordinal));

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string who, HttpMethod method, string path, object? body = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body switch
            {
                null => null,
                string json => new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                _ => JsonContent.Create(body),
            },
        };
        request.Headers.Add("Cookie", $"sqm_session={who}; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Create(string name) =>
        $$"""{"name":{{JsonSerializer.Serialize(name)}},"description":"","definition":{{Definition}},"sourceTemplate":null}""";

    private async Task<long> CreateAsOwnerAsync(string name = "Operations")
    {
        var (status, body) = await SendAsync("owner", HttpMethod.Post, "/api/v1/dashboards", Create(name));
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task A_dashboard_is_created_private_and_audited_by_id_not_content()
    {
        var id = await CreateAsOwnerAsync();

        var (status, body) = await SendAsync("owner", HttpMethod.Get, $"/api/v1/dashboards/{id}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("owner", body.GetProperty("access").GetProperty("level").GetString());
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.Equal("activeBindings", body.GetProperty("definition").GetProperty("widgets")[0].GetProperty("config").GetProperty("metric").GetString());

        var entry = Assert.Single(_audited, a => a.Action == "dashboard.create");
        Assert.Equal("dashboard", entry.TargetType);
        Assert.Equal("Operations", entry.TargetName);
        Assert.DoesNotContain("activeBindings", JsonSerializer.Serialize(entry.Detail), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_dashboard_create_nobody_creates()
    {
        var (status, _) = await SendAsync("viewer", HttpMethod.Post, "/api/v1/dashboards", Create("Mine"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task An_invalid_definition_is_refused_with_where_and_why()
    {
        var bad = Create("Bad").Replace("\"metric\":\"activeBindings\"", "\"metric\":\"revenue\"", StringComparison.Ordinal);
        var (status, body) = await SendAsync("owner", HttpMethod.Post, "/api/v1/dashboards", bad);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("widgets[0].config.metric", out _));
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task Somebody_elses_dashboard_does_not_exist_for_a_stranger()
    {
        var id = await CreateAsOwnerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("stranger", HttpMethod.Get, $"/api/v1/dashboards/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("stranger", HttpMethod.Put, $"/api/v1/dashboards/{id}",
            $$"""{"name":"Mine now","description":"","definition":{{Definition}},"expectedRevision":1}""")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("stranger", HttpMethod.Delete, $"/api/v1/dashboards/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("stranger", HttpMethod.Get, $"/api/v1/dashboards/{id}/config")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("stranger", HttpMethod.Post, $"/api/v1/dashboards/{id}/duplicate", "{}")).Status);
        Assert.Equal("Operations", _store.All.Single().Name);
    }

    [Fact]
    public async Task An_editor_grant_edits_only_with_dashboard_edit()
    {
        var id = await CreateAsOwnerAsync();
        _store.Grant(id, People["editor"].UserId, GrantLevels.Editor);
        _store.Grant(id, People["editorWithoutEdit"].UserId, GrantLevels.Editor);
        var update = $$"""{"name":"Operations","description":"changed","definition":{{Definition}},"expectedRevision":1}""";

        var (refused, _) = await SendAsync("editorWithoutEdit", HttpMethod.Put, $"/api/v1/dashboards/{id}", update);
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Contains(_audited, a => a.Action == "dashboard.update" && a.Outcome == AuditOutcome.Denied);

        var (status, body) = await SendAsync("editor", HttpMethod.Put, $"/api/v1/dashboards/{id}", update);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("revision").GetInt32());
        Assert.Equal("editor", body.GetProperty("access").GetProperty("level").GetString());
    }

    [Fact]
    public async Task A_save_from_an_old_revision_is_409_with_the_winning_revision()
    {
        var id = await CreateAsOwnerAsync();
        var first = $$"""{"name":"Operations","description":"first","definition":{{Definition}},"expectedRevision":1}""";
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("owner", HttpMethod.Put, $"/api/v1/dashboards/{id}", first)).Status);

        var second = first.Replace("first", "second", StringComparison.Ordinal);
        var (status, body) = await SendAsync("owner", HttpMethod.Put, $"/api/v1/dashboards/{id}", second);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(2, body.GetProperty("currentRevision").GetInt32());
        Assert.Equal("first", _store.All.Single().Description);
    }

    [Fact]
    public async Task Only_the_owner_with_dashboard_share_or_an_administrator_shares()
    {
        var id = await CreateAsOwnerAsync();
        _store.Grant(id, People["editor"].UserId, GrantLevels.Editor);
        var grant = """{"subjectType":"user","subjectId":4,"level":"viewer"}""";

        // An editor holding dashboard.share still cannot widen access to somebody else's dashboard.
        var (refused, _) = await SendAsync("editor", HttpMethod.Post, $"/api/v1/dashboards/{id}/grants", grant);
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("editor", HttpMethod.Get, $"/api/v1/dashboards/{id}/grants")).Status);

        var (created, body) = await SendAsync("owner", HttpMethod.Post, $"/api/v1/dashboards/{id}/grants", grant);
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal("viewer", body.GetProperty("level").GetString());
        Assert.Contains(_audited, a => a.Action == "dashboard.share.grant" && a.Outcome == AuditOutcome.Success);

        var (viewerSees, viewed) = await SendAsync("viewer", HttpMethod.Get, $"/api/v1/dashboards/{id}");
        Assert.Equal(HttpStatusCode.OK, viewerSees);
        Assert.False(viewed.GetProperty("access").GetProperty("canEdit").GetBoolean());

        var adminGrant = """{"subjectType":"user","subjectId":5,"level":"editor"}""";
        Assert.Equal(HttpStatusCode.Created, (await SendAsync("admin", HttpMethod.Post, $"/api/v1/dashboards/{id}/grants", adminGrant)).Status);

        // Revoked, and gone on the next read.
        var grantId = body.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync("owner", HttpMethod.Delete, $"/api/v1/dashboards/{id}/grants/{grantId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("viewer", HttpMethod.Get, $"/api/v1/dashboards/{id}")).Status);
    }

    [Fact]
    public async Task A_grant_to_an_unknown_level_or_subject_type_is_refused()
    {
        var id = await CreateAsOwnerAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync("owner", HttpMethod.Post, $"/api/v1/dashboards/{id}/grants",
            """{"subjectType":"user","subjectId":4,"level":"owner"}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync("owner", HttpMethod.Post, $"/api/v1/dashboards/{id}/grants",
            """{"subjectType":"group","subjectId":4,"level":"viewer"}""")).Status);
    }

    [Fact]
    public async Task Only_the_owner_or_an_administrator_deletes()
    {
        var id = await CreateAsOwnerAsync();
        _store.Grant(id, People["editor"].UserId, GrantLevels.Editor);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("editor", HttpMethod.Delete, $"/api/v1/dashboards/{id}")).Status);
        Assert.Single(_store.All);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync("owner", HttpMethod.Delete, $"/api/v1/dashboards/{id}")).Status);
        Assert.Empty(_store.All);
        Assert.Contains(_audited, a => a.Action == "dashboard.delete");
    }

    [Fact]
    public async Task The_share_directory_is_for_people_who_can_share()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("viewer", HttpMethod.Get, "/api/v1/dashboards/share-directory?q=an")).Status);

        var (status, body) = await SendAsync("owner", HttpMethod.Get, "/api/v1/dashboards/share-directory?q=an");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["role"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("type").GetString()).Distinct());
    }

    [Fact]
    public async Task A_copy_is_private_named_and_carries_no_grants()
    {
        var id = await CreateAsOwnerAsync("Weekly");
        _store.Grant(id, People["viewer"].UserId, GrantLevels.Viewer);

        var (status, body) = await SendAsync("owner", HttpMethod.Post, $"/api/v1/dashboards/{id}/duplicate", "{}");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Copy of Weekly", body.GetProperty("name").GetString());
        Assert.Equal(0, body.GetProperty("grantCount").GetInt32());

        // A viewer may not copy without dashboard.create.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("viewer", HttpMethod.Post, $"/api/v1/dashboards/{id}/duplicate", "{}")).Status);
    }

    [Fact]
    public async Task A_config_file_round_trips_with_new_ids_and_no_access()
    {
        var id = await CreateAsOwnerAsync("Source");
        _store.Grant(id, People["viewer"].UserId, GrantLevels.Viewer);

        var (exported, document) = await SendAsync("owner", HttpMethod.Get, $"/api/v1/dashboards/{id}/config");
        Assert.Equal(HttpStatusCode.OK, exported);
        Assert.Equal("quadrasense.dashboard", document.GetProperty("format").GetString());
        Assert.False(document.TryGetProperty("owner", out _));
        Assert.False(document.TryGetProperty("grants", out _));

        var (imported, body) = await SendAsync("owner", HttpMethod.Post, "/api/v1/dashboards/import",
            $$"""{"document":{{document.GetRawText()}},"name":null}""");

        Assert.Equal(HttpStatusCode.Created, imported);
        Assert.Equal("Source (2)", body.GetProperty("name").GetString());
        Assert.Equal(0, body.GetProperty("grantCount").GetInt32());
        Assert.NotEqual("w_kpi0000001", body.GetProperty("definition").GetProperty("widgets")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Widget_types_follow_the_callers_permissions()
    {
        var (_, body) = await SendAsync("viewer", HttpMethod.Get, "/api/v1/dashboards/widget-types");
        var types = body.GetProperty("types").EnumerateArray().ToDictionary(t => t.GetProperty("type").GetString()!);

        Assert.True(types["metric.kpi"].GetProperty("available").GetBoolean());
        Assert.False(types["risk.list"].GetProperty("available").GetBoolean());
        Assert.Contains("risk.view", types["risk.list"].GetProperty("missingPermissions").EnumerateArray().Select(p => p.GetString()));
        Assert.False(types["tools.quickLookup"].GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task A_default_must_be_a_dashboard_the_caller_can_open_or_a_template()
    {
        var id = await CreateAsOwnerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("stranger", HttpMethod.Put, "/api/v1/dashboards/preferences/default",
            $$"""{"dashboardId":{{id}},"templateKey":null}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync("stranger", HttpMethod.Put, "/api/v1/dashboards/preferences/default",
            """{"dashboardId":null,"templateKey":"../etc"}""")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync("stranger", HttpMethod.Put, "/api/v1/dashboards/preferences/default",
            """{"dashboardId":null,"templateKey":"security-risk"}""")).Status);

        var (_, list) = await SendAsync("stranger", HttpMethod.Get, "/api/v1/dashboards");
        Assert.Equal("security-risk", list.GetProperty("default").GetProperty("templateKey").GetString());
    }

    [Fact]
    public async Task Everybody_elses_dashboards_are_listed_only_for_dashboard_manage_and_only_when_asked()
    {
        await CreateAsOwnerAsync();

        var (_, stranger) = await SendAsync("stranger", HttpMethod.Get, "/api/v1/dashboards?scope=all");
        Assert.Equal(JsonValueKind.Null, stranger.GetProperty("others").ValueKind);

        var (_, admin) = await SendAsync("admin", HttpMethod.Get, "/api/v1/dashboards");
        Assert.Equal(JsonValueKind.Null, admin.GetProperty("others").ValueKind);

        var (_, adminAll) = await SendAsync("admin", HttpMethod.Get, "/api/v1/dashboards?scope=all");
        Assert.Single(adminAll.GetProperty("others").EnumerateArray());
    }

    /// <summary>The store in memory: owner and direct user grants. Role grants are SQL, tested against the database.</summary>
    private sealed class InMemoryDashboards : IDashboardStore
    {
        private readonly List<DashboardRecord> _all = [];
        private readonly List<DashboardGrantRecord> _grants = [];
        private readonly Dictionary<long, DashboardDefaultPreference> _defaults = [];
        private long _next = 100;

        public IReadOnlyList<DashboardRecord> All
        {
            get
            {
                lock (_all)
                {
                    return [.. _all];
                }
            }
        }

        public void Grant(long dashboardId, long userId, string level)
        {
            lock (_all)
            {
                _grants.Add(new DashboardGrantRecord(_next++, dashboardId, "user", userId, $"Person {userId}", $"user{userId}", level, DateTimeOffset.UtcNow, null));
            }
        }

        private DashboardRecord View(DashboardRecord r, long reader)
        {
            var levels = _grants.Where(g => g.DashboardId == r.Id && g.SubjectType == "user" && g.SubjectId == reader).Select(g => g.Level).ToList();
            var level = levels.Contains(GrantLevels.Editor) ? GrantLevels.Editor : levels.Count > 0 ? GrantLevels.Viewer : null;
            return r with
            {
                GrantLevel = level,
                GrantCount = _grants.Count(g => g.DashboardId == r.Id),
                IsDefault = _defaults.TryGetValue(reader, out var d) && d.DashboardId == r.Id,
            };
        }

        public Task<IReadOnlyList<DashboardRecord>> ListOwnedAsync(long userId, CancellationToken ct)
        {
            lock (_all)
            {
                return Task.FromResult<IReadOnlyList<DashboardRecord>>([.. _all.Where(d => d.OwnerUserId == userId).Select(d => View(d, userId))]);
            }
        }

        public Task<IReadOnlyList<DashboardRecord>> ListSharedWithAsync(long userId, CancellationToken ct)
        {
            lock (_all)
            {
                return Task.FromResult<IReadOnlyList<DashboardRecord>>([.. _all.Select(d => View(d, userId)).Where(d => d.OwnerUserId != userId && d.GrantLevel is not null)]);
            }
        }

        public Task<IReadOnlyList<DashboardRecord>> ListOthersAsync(long userId, CancellationToken ct)
        {
            lock (_all)
            {
                return Task.FromResult<IReadOnlyList<DashboardRecord>>([.. _all.Select(d => View(d, userId)).Where(d => d.OwnerUserId != userId && d.GrantLevel is null)]);
            }
        }

        public Task<DashboardRecord?> GetAsync(long id, long readerUserId, CancellationToken ct)
        {
            lock (_all)
            {
                var found = _all.FirstOrDefault(d => d.Id == id);
                return Task.FromResult(found is null ? null : View(found, readerUserId));
            }
        }

        public Task<(DashboardWriteOutcome Outcome, DashboardRecord? Dashboard)> CreateAsync(NewDashboard dashboard, CancellationToken ct)
        {
            lock (_all)
            {
                if (_all.Any(d => d.OwnerUserId == dashboard.OwnerUserId && string.Equals(d.Name, dashboard.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    return Task.FromResult<(DashboardWriteOutcome, DashboardRecord?)>((DashboardWriteOutcome.NameTaken, null));
                }

                using var parsed = JsonDocument.Parse(dashboard.DefinitionJson);
                var record = new DashboardRecord(_next++, dashboard.OwnerUserId, $"Person {dashboard.OwnerUserId}", dashboard.Name.Trim(),
                    dashboard.Description, dashboard.DefinitionJson, dashboard.SchemaVersion, 1, dashboard.SourceTemplate,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, false, false, 0, parsed.RootElement.GetProperty("widgets").GetArrayLength());
                _all.Add(record);
                return Task.FromResult<(DashboardWriteOutcome, DashboardRecord?)>((DashboardWriteOutcome.Saved, View(record, dashboard.OwnerUserId)));
            }
        }

        public Task<(DashboardWriteOutcome Outcome, DashboardRecord? Dashboard, DashboardConflict? Conflict)> UpdateAsync(
            long id, long actorUserId, string name, string description, string definitionJson, int schemaVersion, int expectedRevision, CancellationToken ct)
        {
            lock (_all)
            {
                var index = _all.FindIndex(d => d.Id == id);
                if (index < 0)
                {
                    return Task.FromResult<(DashboardWriteOutcome, DashboardRecord?, DashboardConflict?)>((DashboardWriteOutcome.NotFound, null, null));
                }

                var current = _all[index];
                if (current.Revision != expectedRevision)
                {
                    return Task.FromResult<(DashboardWriteOutcome, DashboardRecord?, DashboardConflict?)>(
                        (DashboardWriteOutcome.Conflict, null, new DashboardConflict(current.Revision, current.UpdatedAt, current.UpdatedByDisplayName)));
                }

                var updated = current with
                {
                    Name = name, Description = description, DefinitionJson = definitionJson, Revision = current.Revision + 1,
                    UpdatedAt = DateTimeOffset.UtcNow, UpdatedByDisplayName = $"Person {actorUserId}",
                };
                _all[index] = updated;
                return Task.FromResult<(DashboardWriteOutcome, DashboardRecord?, DashboardConflict?)>((DashboardWriteOutcome.Saved, View(updated, actorUserId), null));
            }
        }

        public Task<bool> DeleteAsync(long id, CancellationToken ct)
        {
            lock (_all)
            {
                _grants.RemoveAll(g => g.DashboardId == id);
                return Task.FromResult(_all.RemoveAll(d => d.Id == id) > 0);
            }
        }

        public Task<bool> NameExistsAsync(long ownerUserId, string name, CancellationToken ct)
        {
            lock (_all)
            {
                return Task.FromResult(_all.Any(d => d.OwnerUserId == ownerUserId && string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));
            }
        }

        public Task<IReadOnlyList<DashboardGrantRecord>> ListGrantsAsync(long dashboardId, CancellationToken ct)
        {
            lock (_all)
            {
                return Task.FromResult<IReadOnlyList<DashboardGrantRecord>>([.. _grants.Where(g => g.DashboardId == dashboardId)]);
            }
        }

        public Task<(GrantWriteOutcome Outcome, DashboardGrantRecord? Grant)> AddGrantAsync(
            long dashboardId, string subjectType, long subjectId, string level, long actorUserId, int maxGrants, CancellationToken ct)
        {
            lock (_all)
            {
                var record = new DashboardGrantRecord(_next++, dashboardId, subjectType, subjectId, $"Person {subjectId}", null, level, DateTimeOffset.UtcNow, null);
                _grants.Add(record);
                return Task.FromResult<(GrantWriteOutcome, DashboardGrantRecord?)>((GrantWriteOutcome.Saved, record));
            }
        }

        public Task<(GrantWriteOutcome Outcome, DashboardGrantRecord? Grant)> UpdateGrantAsync(long dashboardId, long grantId, string level, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DashboardGrantRecord?> RemoveGrantAsync(long dashboardId, long grantId, CancellationToken ct)
        {
            lock (_all)
            {
                var grant = _grants.FirstOrDefault(g => g.DashboardId == dashboardId && g.Id == grantId);
                if (grant is not null)
                {
                    _grants.Remove(grant);
                }

                return Task.FromResult(grant);
            }
        }

        public Task SetFavoriteAsync(long userId, long dashboardId, bool favorite, CancellationToken ct) => Task.CompletedTask;

        public Task<DashboardDefaultPreference> GetDefaultAsync(long userId, CancellationToken ct)
        {
            lock (_all)
            {
                return Task.FromResult(_defaults.TryGetValue(userId, out var d) ? d : new DashboardDefaultPreference(null, null));
            }
        }

        public Task SetDefaultAsync(long userId, long? dashboardId, string? templateKey, CancellationToken ct)
        {
            lock (_all)
            {
                _defaults[userId] = new DashboardDefaultPreference(dashboardId, templateKey);
            }

            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<ShareDirectoryEntry> Users, IReadOnlyList<ShareDirectoryEntry> Roles)> SearchShareDirectoryAsync(
            string term, int limit, int minUserQuery, CancellationToken ct) =>
            Task.FromResult<(IReadOnlyList<ShareDirectoryEntry>, IReadOnlyList<ShareDirectoryEntry>)>(
                ([new ShareDirectoryEntry("user", 4, "Person 4", "user4")], [new ShareDirectoryEntry("role", 1, "Analyst", null)]));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// The same features over HTTP, on the real pipeline: what a browser sees, cookies included.
/// </summary>
public sealed class AccountSecurityEndpointTests
    : IClassFixture<IdentityFixture>, IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Password = "Vx9-quiet-owl-lantern";
    private const string NewPassword = "Zq4-amber-river-stone";
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    private readonly IdentityFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CapturingDelivery _delivery = new();
    private readonly List<long> _users = [];
    private readonly List<long> _roles = [];

    public AccountSecurityEndpointTests(
        IdentityFixture fixture, WebApplicationFactory<Program> factory)
    {
        _fixture = fixture;
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _users)
        {
            await _fixture.DeleteUserAsync(id);
        }

        foreach (var id in _roles)
        {
            await _fixture.DeleteRoleAsync(id);
        }
    }

    private void SkipWithoutDatabase()
    {
        if (!_fixture.IsAvailable)
        {
            Assert.Skip(_fixture.UnavailableReason ?? "no database");
        }
    }

    /// <summary>A host of its own, so that each test has the rate limiters to itself.</summary>
    private WebApplicationFactory<Program> Host(string? key = null) =>
        _factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("Auth:TwoStep:SecretKey", key ?? Key);
            host.ConfigureTestServices(services =>
                services.AddSingleton<IContactDelivery>(_delivery));
        });

    private async Task<(long Id, string Username, string Email)> CreateAsync(
        bool proved = true, params string[] roles)
    {
        var username = IdentityFixture.UniqueUsername("api");
        var email = $"{Guid.NewGuid():N}@example.test";

        var id = await _fixture.Users.CreateAsync(
            new NewUser(username, "Test Person", email, null, null, roles, MustChangePassword: false),
            Password, "tests", IdentityFixture.Context, Ct);

        _users.Add(id);

        if (proved)
        {
            await _fixture.ExecuteAsync(
                "UPDATE auth.user_account SET email_verified_at = now() WHERE id = @id", ("id", id));
        }

        return (id, username, email);
    }

    private async Task WaitForMessageAsync(string destination)
    {
        for (var i = 0; i < 100 && _delivery.To(destination).Count == 0; i++)
        {
            await Task.Delay(50, Ct);
        }
    }

    // ======================================================================= the page's options

    [Fact]
    public async Task The_page_is_told_what_it_may_offer()
    {
        using var host = Host();
        using var client = host.CreateClient();

        var options = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/options", Ct);

        Assert.True(options.GetProperty("recoverByEmail").GetBoolean());
        Assert.True(options.GetProperty("recoverBySms").GetBoolean());
        Assert.True(options.GetProperty("twoStepAvailable").GetBoolean());
        Assert.Equal(7, options.GetProperty("rememberDays").GetInt32());
        Assert.Equal(10, options.GetProperty("codeMinutes").GetInt32());
    }

    [Fact]
    public async Task With_no_key_the_page_is_told_two_step_is_off()
    {
        using var host = Host(key: string.Empty);
        using var client = host.CreateClient();

        var options = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/options", Ct);

        Assert.False(options.GetProperty("twoStepAvailable").GetBoolean());
    }

    // ======================================================================= saying nothing

    [Fact]
    public async Task Asking_for_a_code_answers_alike_whoever_is_named()
    {
        SkipWithoutDatabase();
        var proved = await CreateAsync();
        var unproved = await CreateAsync(proved: false);
        var deactivated = await CreateAsync();
        await _fixture.Users.SetActiveAsync(
            deactivated.Id, false, "left", "tests", IdentityFixture.Context, Ct);

        using var host = Host();
        using var client = host.CreateClient();

        var answers = new List<(HttpStatusCode Status, string Body, int Headers)>();

        foreach (var identifier in new[]
        {
            proved.Username, unproved.Username, deactivated.Username, "nobody." + Guid.NewGuid(),
        })
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/recovery/start", new { identifier, channel = "Email" }, Ct);

            answers.Add((
                response.StatusCode,
                await response.Content.ReadAsStringAsync(Ct),
                response.Headers.Count()));
        }

        Assert.Equal(HttpStatusCode.Accepted, answers[0].Status);
        Assert.Single(answers.Distinct());
    }

    [Fact]
    public async Task The_code_goes_out_after_the_answer()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/recovery/start", new { identifier = account.Username, channel = "Email" }, Ct);

        await WaitForMessageAsync(account.Email);

        Assert.Single(_delivery.To(account.Email));
    }

    [Fact]
    public async Task A_wrong_code_and_an_unknown_account_get_one_answer()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        using var host = Host();
        using var client = host.CreateClient();

        async Task<string> ErrorsAsync(string identifier)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/recovery/complete",
                new { identifier, code = "000000", newPassword = NewPassword }, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            return body.GetProperty("errors").GetRawText();
        }

        Assert.Equal(
            await ErrorsAsync(account.Username),
            await ErrorsAsync("nobody." + Guid.NewGuid()));
    }

    [Fact]
    public async Task A_new_password_can_be_set_and_signs_nobody_in()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        using var host = Host();
        using var client = host.CreateClient();

        using var start = await client.PostAsJsonAsync(
            "/api/v1/auth/recovery/start", new { identifier = account.Username, channel = "Email" }, Ct);
        await WaitForMessageAsync(account.Email);

        using var complete = await client.PostAsJsonAsync("/api/v1/auth/recovery/complete", new
        {
            identifier = account.Username,
            code = _delivery.LatestCode(account.Email),
            newPassword = NewPassword,
        }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        Assert.False(complete.Headers.Contains("Set-Cookie"));

        using var browser = new Browser(host);
        using var signIn = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = NewPassword });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact]
    public async Task A_password_problem_is_named_on_the_password()
    {
        using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/recovery/complete", new
        {
            identifier = "anyone",
            code = "123456",
            newPassword = "short",
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(body.GetProperty("errors").TryGetProperty("newPassword", out _));
    }

    [Fact]
    public async Task Asking_too_often_from_one_address_is_refused()
    {
        using var host = Host();
        using var client = host.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 8; i++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/recovery/start", new { identifier = "anyone", channel = "Email" }, Ct);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(6, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task A_channel_that_does_not_exist_is_a_bad_request_and_nothing_more()
    {
        using var host = Host();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/recovery/start", new { identifier = "anyone" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ======================================================================= remember me

    [Fact]
    public async Task Without_remember_me_the_session_cookie_dies_with_the_browser()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        using var host = Host();
        using var browser = new Browser(host);

        using var response = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("max-age", browser.SetCookie("sqm_session"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_remember_me_both_cookies_last_as_long_as_the_session()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        using var host = Host();
        using var browser = new Browser(host);

        using var response = await browser.PostAsync(
            "/api/v1/auth/login",
            new { username = account.Username, password = Password, rememberMe = true });

        var sevenDays = (int)TimeSpan.FromDays(7).TotalSeconds;

        Assert.Contains($"max-age={sevenDays}", browser.SetCookie("sqm_session"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"max-age={sevenDays}", browser.SetCookie("sqm_csrf"), StringComparison.OrdinalIgnoreCase);

        var lifetime = await _fixture.ScalarAsync<TimeSpan>(
            "SELECT absolute_expires_at - created_at FROM auth.session "
            + "WHERE user_id = @id AND remembered", ("id", account.Id));
        Assert.Equal(TimeSpan.FromDays(7), lifetime);
    }

    [Fact]
    public async Task A_remembered_session_keeps_its_limit_when_it_is_used()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var session = await _fixture.Sessions.CreateAsync(account.Id, null, null, true, Ct);
        var resolved = await _fixture.Sessions.ResolveAsync(session.Token, Ct);

        await _fixture.ExecuteAsync(
            "UPDATE auth.session SET last_seen_at = now() - interval '2 days', "
            + "idle_expires_at = now() + interval '1 day' WHERE id = @id",
            ("id", resolved!.SessionId));

        await _fixture.Sessions.TouchAsync(resolved.SessionId, Ct);

        var after = await _fixture.Sessions.ResolveAsync(session.Token, Ct);

        // Rolled forward to the seven-day limit, not to the usual eight hours.
        Assert.True(after!.IdleExpiresAt > DateTimeOffset.UtcNow.AddDays(6));
        Assert.True(after.IdleExpiresAt <= after.AbsoluteExpiresAt);
    }

    // ======================================================================= two steps

    [Fact]
    public async Task With_two_step_on_a_password_gets_a_ticket_and_no_cookie()
    {
        SkipWithoutDatabase();
        var (account, secret) = await EnrolAsync();

        using var host = Host();
        using var browser = new Browser(host);

        using var response = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ticket = body.GetProperty("ticket").GetString();

        using var me = await browser.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        using var second = await browser.PostAsync("/api/v1/auth/login/two-step", new
        {
            ticket,
            code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1),
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var user = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(user.GetProperty("twoStepEnabled").GetBoolean());

        using var meAfter = await browser.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meAfter.StatusCode);
    }

    [Fact]
    public async Task Remember_me_survives_the_second_step()
    {
        SkipWithoutDatabase();
        var (account, secret) = await EnrolAsync();

        using var host = Host();
        using var browser = new Browser(host);

        using var first = await browser.PostAsync(
            "/api/v1/auth/login",
            new { username = account.Username, password = Password, rememberMe = true });
        var ticket = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("ticket").GetString();

        using var second = await browser.PostAsync("/api/v1/auth/login/two-step", new
        {
            ticket,
            code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1),
        });

        Assert.Contains("max-age", browser.SetCookie("sqm_session"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_code_says_so_and_an_old_ticket_says_to_start_again()
    {
        SkipWithoutDatabase();
        var (account, _) = await EnrolAsync();

        using var host = Host();
        using var browser = new Browser(host);

        using var first = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });
        var ticket = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("ticket").GetString();

        using var wrong = await browser.PostAsync(
            "/api/v1/auth/login/two-step", new { ticket, code = "000000" });
        using var unknown = await browser.PostAsync(
            "/api/v1/auth/login/two-step", new { ticket = "no-such-ticket", code = "000000" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("InvalidCode",
            (await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("ChallengeExpired",
            (await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_role_that_demands_it_holds_everything_else_back()
    {
        SkipWithoutDatabase();
        var code = "api." + Guid.NewGuid().ToString("N")[..12];
        var roleId = await _fixture.Roles.CreateRoleAsync(
            code, "Test role", string.Empty, [], "tests", IdentityFixture.Context, Ct);
        _roles.Add(roleId);
        await _fixture.Roles.SetTwoStepRequiredAsync(
            roleId, true, "tests", IdentityFixture.Context, Ct);
        var account = await CreateAsync(true, code);

        using var host = Host();
        using var browser = new Browser(host);

        using var login = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });

        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("mustSetUpTwoStep").GetBoolean());

        using var blocked = await browser.SendAsync(HttpMethod.Put, "/api/v1/auth/me",
            new { displayName = "Test Person" });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        using var me = await browser.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using var setup = await browser.PostAsync(
            "/api/v1/auth/me/two-step/setup", new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
    }

    [Fact]
    public async Task Without_a_key_a_role_that_demands_it_turns_nobody_away()
    {
        SkipWithoutDatabase();
        var code = "api." + Guid.NewGuid().ToString("N")[..12];
        var roleId = await _fixture.Roles.CreateRoleAsync(
            code, "Test role", string.Empty, [], "tests", IdentityFixture.Context, Ct);
        _roles.Add(roleId);
        await _fixture.Roles.SetTwoStepRequiredAsync(
            roleId, true, "tests", IdentityFixture.Context, Ct);
        var account = await CreateAsync(true, code);

        using var host = Host(key: string.Empty);
        using var browser = new Browser(host);

        using var login = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });

        Assert.False((await login.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("mustSetUpTwoStep").GetBoolean());

        using var update = await browser.SendAsync(HttpMethod.Put, "/api/v1/auth/me",
            new { displayName = "Test Person" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    [Fact]
    public async Task Without_a_key_a_role_cannot_be_told_to_demand_it()
    {
        SkipWithoutDatabase();
        var admin = await CreateAsync(true, "administrator");
        var roleId = await _fixture.Roles.CreateRoleAsync(
            "api." + Guid.NewGuid().ToString("N")[..12], "Test role", string.Empty, [], "tests",
            IdentityFixture.Context, Ct);
        _roles.Add(roleId);

        using var host = Host(key: string.Empty);
        using var browser = new Browser(host);

        using var login = await browser.PostAsync(
            "/api/v1/auth/login", new { username = admin.Username, password = Password });
        using var response = await browser.SendAsync(
            HttpMethod.Put, $"/api/v1/roles/{roleId}/two-step", new { required = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_is_refused_while_a_role_demands_it()
    {
        SkipWithoutDatabase();
        var code = "api." + Guid.NewGuid().ToString("N")[..12];
        var roleId = await _fixture.Roles.CreateRoleAsync(
            code, "Test role", string.Empty, [], "tests", IdentityFixture.Context, Ct);
        _roles.Add(roleId);
        var (account, secret) = await EnrolAsync(code);
        await _fixture.Roles.SetTwoStepRequiredAsync(
            roleId, true, "tests", IdentityFixture.Context, Ct);

        using var host = Host();
        using var browser = new Browser(host);

        using var first = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });
        var ticket = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("ticket").GetString();
        using var second = await browser.PostAsync("/api/v1/auth/login/two-step", new
        {
            ticket,
            code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1),
        });

        using var response = await browser.PostAsync("/api/v1/auth/me/two-step/disable", new
        {
            currentPassword = Password,
            code = "000000",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_reset_their_own_second_step()
    {
        SkipWithoutDatabase();
        var admin = await CreateAsync(true, "administrator");

        using var host = Host();
        using var browser = new Browser(host);

        using var login = await browser.PostAsync(
            "/api/v1/auth/login", new { username = admin.Username, password = Password });
        using var response = await browser.PostAsync(
            $"/api/v1/users/{admin.Id}/two-step/reset", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ======================================================================= sessions

    [Fact]
    public async Task Signing_out_everywhere_else_leaves_this_device_in()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        using var host = Host();
        using var mine = new Browser(host);
        using var other = new Browser(host);

        using var a = await mine.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });
        using var b = await other.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });

        using var response = await mine.SendAsync(HttpMethod.Delete, "/api/v1/auth/me/sessions", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("count").GetInt32());

        using var stillIn = await mine.GetAsync("/api/v1/auth/me");
        using var signedOut = await other.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task The_session_list_says_which_are_remembered()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        using var host = Host();
        using var browser = new Browser(host);

        using var login = await browser.PostAsync(
            "/api/v1/auth/login",
            new { username = account.Username, password = Password, rememberMe = true });

        using var response = await browser.GetAsync("/api/v1/auth/me/sessions");
        var sessions = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(sessions[0].GetProperty("remembered").GetBoolean());
    }

    // ======================================================================= contacts

    [Fact]
    public async Task A_contact_can_be_proved_over_the_api()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        using var host = Host();
        using var browser = new Browser(host);

        using var login = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });

        Assert.False((await login.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("emailVerified").GetBoolean());

        using var send = await browser.PostAsync(
            "/api/v1/auth/me/contacts/email/code", new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);

        using var confirm = await browser.PostAsync(
            "/api/v1/auth/me/contacts/email/confirm",
            new { code = _delivery.LatestCode(account.Email) });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        using var me = await browser.GetAsync("/api/v1/auth/me");
        Assert.True((await me.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task A_channel_name_that_means_nothing_is_not_found()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        using var host = Host();
        using var browser = new Browser(host);

        using var login = await browser.PostAsync(
            "/api/v1/auth/login", new { username = account.Username, password = Password });

        using var response = await browser.PostAsync(
            "/api/v1/auth/me/contacts/pigeon/code", new { currentPassword = Password });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =======================================================================

    private async Task<((long Id, string Username, string Email) Account, byte[] Secret)> EnrolAsync(
        params string[] roles)
    {
        var account = await CreateAsync(true, roles);
        var session = await _fixture.Sessions.CreateAsync(account.Id, null, null, false, Ct);
        var resolved = await _fixture.Sessions.ResolveAsync(session.Token, Ct);

        var setup = await _fixture.TwoStep.BeginSetupAsync(
            account.Id, Password, IdentityFixture.Context, Ct);
        var secret = Decode(setup!.Secret);

        await _fixture.TwoStep.ConfirmSetupAsync(
            account.Id, Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow)), resolved!.SessionId,
            IdentityFixture.Context, Ct);

        return (account, secret);
    }

    private static byte[] Decode(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;

        foreach (var c in base32)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }

    /// <summary>Cookies and the CSRF header, as the SPA sends them.</summary>
    private sealed class Browser : IDisposable
    {
        private readonly HttpClient _client;
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _setCookies = new(StringComparer.Ordinal);

        public Browser(WebApplicationFactory<Program> host) =>
            _client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        public string SetCookie(string name) => _setCookies[name];

        public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url, null);

        public Task<HttpResponseMessage> PostAsync(string url, object body) =>
            SendAsync(HttpMethod.Post, url, body);

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body)
        {
            using var request = new HttpRequestMessage(method, url);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            if (_cookies.Count > 0)
            {
                request.Headers.Add(
                    "Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            if (_cookies.TryGetValue("sqm_csrf", out var csrf))
            {
                request.Headers.Add("X-CSRF-Token", csrf);
            }

            var response = await _client.SendAsync(request, Ct);

            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var value in values)
                {
                    var pair = value.Split(';')[0].Split('=', 2);
                    _cookies[pair[0]] = pair[1];
                    _setCookies[pair[0]] = value;
                }
            }

            return response;
        }

        public void Dispose() => _client.Dispose();
    }
}

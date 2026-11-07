using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>Two-step sign-in, against a real database.</summary>
public sealed class TwoStepTests : IClassFixture<IdentityFixture>, IAsyncLifetime
{
    private const string Password = "Vx9-quiet-owl-lantern";
    private const string NewPassword = "Zq4-amber-river-stone";

    private static readonly TimeSpan OneStep = TimeSpan.FromSeconds(30);

    private readonly IdentityFixture _fixture;
    private readonly List<long> _users = [];
    private readonly List<long> _roles = [];

    public TwoStepTests(IdentityFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthenticationContext Context => IdentityFixture.Context;

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

    private async Task<(long Id, string Username)> CreateAsync(params string[] roles)
    {
        var username = IdentityFixture.UniqueUsername("two");

        var id = await _fixture.Users.CreateAsync(
            new NewUser(username, "Test Person", null, null, null, roles, MustChangePassword: false),
            Password, "tests", Context, Ct);

        _users.Add(id);
        return (id, username);
    }

    /// <summary>An account with two-step on. The secret stays with the test, as a phone would.</summary>
    private async Task<EnrolledAccount> EnrolAsync()
    {
        var (id, username) = await CreateAsync();
        var session = await _fixture.Sessions.CreateAsync(id, null, null, false, Ct);
        var resolved = await _fixture.Sessions.ResolveAsync(session.Token, Ct);

        var setup = await _fixture.TwoStep.BeginSetupAsync(id, Password, Context, Ct);
        var secret = DecodeBase32(setup!.Secret);

        var codes = await _fixture.TwoStep.ConfirmSetupAsync(
            id, CodeNow(secret), resolved!.SessionId, Context, Ct);

        // The code just used may not be used again, so the next one is a step away.
        _fixture.Clock.Advance(OneStep);

        return new EnrolledAccount(id, username, secret, codes!);
    }

    private string CodeNow(byte[] secret) =>
        Totp.Code(secret, Totp.StepAt(_fixture.Clock.GetUtcNow()));

    private async Task<string> PasswordStepAsync(EnrolledAccount account, bool remember = false)
    {
        var outcome = await _fixture.Authenticator.AuthenticateAsync(
            account.Username, Password, Context, Ct);

        Assert.Equal(LoginStatus.TwoStepRequired, outcome.Status);

        var challenge = await _fixture.TwoStep.BeginChallengeAsync(account.Id, remember, Ct);
        return challenge.Ticket;
    }

    private static byte[] DecodeBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;

        foreach (var c in text)
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

    // ======================================================================= setting it up

    [Fact]
    public async Task Setting_up_asks_for_the_password()
    {
        SkipWithoutDatabase();
        var (id, _) = await CreateAsync();

        Assert.Null(await _fixture.TwoStep.BeginSetupAsync(id, "not it", Context, Ct));
    }

    [Fact]
    public async Task The_secret_is_given_in_the_form_an_authenticator_reads()
    {
        SkipWithoutDatabase();
        var (id, username) = await CreateAsync();

        var setup = await _fixture.TwoStep.BeginSetupAsync(id, Password, Context, Ct);

        Assert.Equal(20, DecodeBase32(setup!.Secret).Length);
        Assert.StartsWith("otpauth://totp/Quadrasense%3A", setup.Uri);
        Assert.Contains(Uri.EscapeDataString(username), setup.Uri);
        Assert.Contains($"secret={setup.Secret}", setup.Uri);
    }

    [Fact]
    public async Task A_setup_left_unfinished_changes_nothing_about_signing_in()
    {
        SkipWithoutDatabase();
        var (id, username) = await CreateAsync();

        await _fixture.TwoStep.BeginSetupAsync(id, Password, Context, Ct);

        var outcome = await _fixture.Authenticator.AuthenticateAsync(username, Password, Context, Ct);
        Assert.Equal(LoginStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task The_secret_is_not_stored_readable()
    {
        SkipWithoutDatabase();
        var (id, _) = await CreateAsync();

        var setup = await _fixture.TwoStep.BeginSetupAsync(id, Password, Context, Ct);
        var secret = DecodeBase32(setup!.Secret);

        var stored = await _fixture.ScalarAsync<byte[]>(
            "SELECT secret FROM auth.user_two_step WHERE user_id = @id", ("id", id));

        Assert.True(stored!.Length > secret.Length);
        Assert.False(stored.AsSpan().IndexOf(secret) >= 0);
    }

    [Fact]
    public async Task A_wrong_first_code_turns_nothing_on()
    {
        SkipWithoutDatabase();
        var (id, _) = await CreateAsync();

        await _fixture.TwoStep.BeginSetupAsync(id, Password, Context, Ct);

        Assert.Null(await _fixture.TwoStep.ConfirmSetupAsync(
            id, "000000", Guid.NewGuid(), Context, Ct));

        var status = await _fixture.TwoStep.GetStatusAsync(id, Ct);
        Assert.False(status.Enabled);
    }

    [Fact]
    public async Task Turning_it_on_gives_recovery_codes_stored_only_as_hashes()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();

        Assert.Equal(10, account.RecoveryCodes.Count);
        Assert.Equal(10, account.RecoveryCodes.Distinct().Count());

        var status = await _fixture.TwoStep.GetStatusAsync(account.Id, Ct);
        Assert.True(status.Enabled);
        Assert.Equal(10, status.RecoveryCodesLeft);

        var stored = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM auth.user_recovery_code "
            + "WHERE user_id = @id AND length(code_sha256) = 32", ("id", account.Id));
        Assert.Equal(10, stored);
    }

    [Fact]
    public async Task Turning_it_on_ends_every_other_session_and_keeps_this_one()
    {
        SkipWithoutDatabase();
        var (id, _) = await CreateAsync();
        var other = await _fixture.Sessions.CreateAsync(id, null, null, false, Ct);
        var mine = await _fixture.Sessions.CreateAsync(id, null, null, false, Ct);
        var resolved = await _fixture.Sessions.ResolveAsync(mine.Token, Ct);

        var setup = await _fixture.TwoStep.BeginSetupAsync(id, Password, Context, Ct);
        await _fixture.TwoStep.ConfirmSetupAsync(
            id, CodeNow(DecodeBase32(setup!.Secret)), resolved!.SessionId, Context, Ct);

        Assert.Null(await _fixture.Sessions.ResolveAsync(other.Token, Ct));
        Assert.NotNull(await _fixture.Sessions.ResolveAsync(mine.Token, Ct));
    }

    [Fact]
    public async Task It_cannot_be_set_up_twice()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();

        await Assert.ThrowsAsync<TwoStepRefusedException>(() =>
            _fixture.TwoStep.BeginSetupAsync(account.Id, Password, Context, Ct));
    }

    // ======================================================================= signing in

    [Fact]
    public async Task A_good_password_alone_is_not_a_sign_in()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        await _fixture.ExecuteAsync(
            "UPDATE auth.user_account SET failed_login_count = 3 WHERE id = @id", ("id", account.Id));

        await PasswordStepAsync(account);

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.Null(user!.LastLoginAt);
        Assert.Equal(3, user.FailedLoginCount);
    }

    [Fact]
    public async Task The_right_code_completes_the_sign_in_and_is_recorded()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account, remember: true);

        var result = await _fixture.TwoStep.CompleteChallengeAsync(
            ticket, CodeNow(account.Secret), Context, Ct);

        Assert.Equal(TwoStepResultStatus.Success, result.Status);
        Assert.Equal(account.Id, result.UserId);
        Assert.True(result.Remember);

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.NotNull(user!.LastLoginAt);
    }

    [Fact]
    public async Task A_code_typed_with_spaces_or_Persian_digits_is_the_same_code()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        var code = CodeNow(account.Secret);
        var typed = string.Concat(code[..3].Select(c => (char)('۰' + (c - '0')))) + " " + code[3..];

        var result = await _fixture.TwoStep.CompleteChallengeAsync(ticket, typed, Context, Ct);

        Assert.Equal(TwoStepResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task A_code_works_once_even_inside_its_own_thirty_seconds()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var code = CodeNow(account.Secret);

        var first = await _fixture.TwoStep.CompleteChallengeAsync(
            await PasswordStepAsync(account), code, Context, Ct);
        var second = await _fixture.TwoStep.CompleteChallengeAsync(
            await PasswordStepAsync(account), code, Context, Ct);

        Assert.Equal(TwoStepResultStatus.Success, first.Status);
        Assert.Equal(TwoStepResultStatus.InvalidCode, second.Status);
    }

    [Fact]
    public async Task A_ticket_works_once()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        await _fixture.TwoStep.CompleteChallengeAsync(ticket, CodeNow(account.Secret), Context, Ct);
        _fixture.Clock.Advance(OneStep);

        var again = await _fixture.TwoStep.CompleteChallengeAsync(
            ticket, CodeNow(account.Secret), Context, Ct);

        Assert.Equal(TwoStepResultStatus.ChallengeExpired, again.Status);
    }

    [Fact]
    public async Task A_ticket_past_its_time_fails()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        await _fixture.ExecuteAsync(
            "UPDATE auth.sign_in_challenge SET expires_at = now() - interval '1 second' "
            + "WHERE user_id = @id", ("id", account.Id));

        var result = await _fixture.TwoStep.CompleteChallengeAsync(
            ticket, CodeNow(account.Secret), Context, Ct);

        Assert.Equal(TwoStepResultStatus.ChallengeExpired, result.Status);
    }

    [Fact]
    public async Task Wrong_codes_count_towards_the_lockout_as_wrong_passwords_do()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        var results = new List<TwoStepResultStatus>();
        for (var i = 0; i < 5; i++)
        {
            var result = await _fixture.TwoStep.CompleteChallengeAsync(
                ticket, "000000", Context, Ct);
            results.Add(result.Status);
        }

        Assert.Equal(TwoStepResultStatus.InvalidCode, results[0]);
        Assert.Equal(TwoStepResultStatus.InvalidCode, results[3]);

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.Equal(5, user!.FailedLoginCount);
        Assert.NotNull(user.LockedUntil);

        var locked = await _fixture.Authenticator.AuthenticateAsync(
            account.Username, Password, Context, Ct);
        Assert.Equal(LoginStatus.AccountLocked, locked.Status);
    }

    [Fact]
    public async Task A_ticket_is_out_after_five_wrong_codes_even_with_the_right_sixth()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        for (var i = 0; i < 5; i++)
        {
            await _fixture.TwoStep.CompleteChallengeAsync(ticket, "000000", Context, Ct);
        }

        var result = await _fixture.TwoStep.CompleteChallengeAsync(
            ticket, CodeNow(account.Secret), Context, Ct);

        Assert.Equal(TwoStepResultStatus.ChallengeExpired, result.Status);
    }

    [Fact]
    public async Task Guesses_at_the_same_moment_cannot_exceed_five()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            _fixture.TwoStep.CompleteChallengeAsync(ticket, "000000", Context, Ct)));

        Assert.True(results.Count(r => r.Status == TwoStepResultStatus.ChallengeExpired) >= 15);

        var failures = await _fixture.ScalarAsync<short>(
            "SELECT failed_login_count FROM auth.user_account WHERE id = @id", ("id", account.Id));
        Assert.Equal(5, failures);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var code = account.RecoveryCodes[3];

        var first = await _fixture.TwoStep.CompleteChallengeAsync(
            await PasswordStepAsync(account), code.ToLowerInvariant(), Context, Ct);
        var second = await _fixture.TwoStep.CompleteChallengeAsync(
            await PasswordStepAsync(account), code, Context, Ct);

        Assert.Equal(TwoStepResultStatus.Success, first.Status);
        Assert.Equal(TwoStepResultStatus.InvalidCode, second.Status);

        var status = await _fixture.TwoStep.GetStatusAsync(account.Id, Ct);
        Assert.Equal(9, status.RecoveryCodesLeft);
    }

    [Fact]
    public async Task A_ticket_means_nothing_after_the_account_is_deactivated()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        await _fixture.Users.SetActiveAsync(account.Id, false, "left", "tests", Context, Ct);

        var result = await _fixture.TwoStep.CompleteChallengeAsync(
            ticket, CodeNow(account.Secret), Context, Ct);

        Assert.Equal(TwoStepResultStatus.ChallengeExpired, result.Status);
    }

    [Fact]
    public async Task A_ticket_means_nothing_after_the_password_changes()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var ticket = await PasswordStepAsync(account);

        await _fixture.Users.ResetPasswordAsync(account.Id, NewPassword, "tests", Context, Ct);

        var result = await _fixture.TwoStep.CompleteChallengeAsync(
            ticket, CodeNow(account.Secret), Context, Ct);

        Assert.Equal(TwoStepResultStatus.ChallengeExpired, result.Status);
    }

    /// <summary>
    /// The reason a code by SMS is not offered as a second step: recovery must not be a way round.
    /// </summary>
    [Fact]
    public async Task Recovering_a_password_does_not_step_around_the_second_step()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var email = $"{Guid.NewGuid():N}@example.test";
        await _fixture.ExecuteAsync(
            "UPDATE auth.user_account SET email = @email, email_verified_at = now() WHERE id = @id",
            ("email", email), ("id", account.Id));

        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);
        await _fixture.Recovery.CompleteAsync(
            account.Username, _fixture.Delivery.LatestCode(email), NewPassword, Context, Ct);

        var outcome = await _fixture.Authenticator.AuthenticateAsync(
            account.Username, NewPassword, Context, Ct);

        Assert.Equal(LoginStatus.TwoStepRequired, outcome.Status);
    }

    // ======================================================================= turning it off

    [Fact]
    public async Task Turning_it_off_takes_both_the_password_and_a_code()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();

        Assert.False(await _fixture.TwoStep.DisableAsync(
            account.Id, "not it", CodeNow(account.Secret), Context, Ct));
        Assert.False(await _fixture.TwoStep.DisableAsync(
            account.Id, Password, "000000", Context, Ct));
        Assert.True((await _fixture.TwoStep.GetStatusAsync(account.Id, Ct)).Enabled);

        Assert.True(await _fixture.TwoStep.DisableAsync(
            account.Id, Password, CodeNow(account.Secret), Context, Ct));

        var status = await _fixture.TwoStep.GetStatusAsync(account.Id, Ct);
        Assert.False(status.Enabled);
        Assert.Equal(0, status.RecoveryCodesLeft);

        var outcome = await _fixture.Authenticator.AuthenticateAsync(
            account.Username, Password, Context, Ct);
        Assert.Equal(LoginStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task New_recovery_codes_replace_the_old_ones()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();

        var fresh = await _fixture.TwoStep.RegenerateRecoveryCodesAsync(
            account.Id, Password, Context, Ct);

        var old = await _fixture.TwoStep.CompleteChallengeAsync(
            await PasswordStepAsync(account), account.RecoveryCodes[0], Context, Ct);
        var current = await _fixture.TwoStep.CompleteChallengeAsync(
            await PasswordStepAsync(account), fresh![0], Context, Ct);

        Assert.Equal(TwoStepResultStatus.InvalidCode, old.Status);
        Assert.Equal(TwoStepResultStatus.Success, current.Status);
    }

    [Fact]
    public async Task An_administrators_reset_turns_it_off_and_ends_every_session()
    {
        SkipWithoutDatabase();
        var account = await EnrolAsync();
        var session = await _fixture.Sessions.CreateAsync(account.Id, null, null, false, Ct);

        Assert.True(await _fixture.TwoStep.ResetAsync(account.Id, "tests", Context, Ct));

        Assert.Null(await _fixture.Sessions.ResolveAsync(session.Token, Ct));
        Assert.False((await _fixture.TwoStep.GetStatusAsync(account.Id, Ct)).Enabled);
        Assert.Contains(_fixture.Outbox.Work, w =>
            w is AccountNoticeWork { Notice: AccountNotice.TwoStepOff } n && n.UserId == account.Id);
    }

    // ======================================================================= a role demands it

    [Fact]
    public async Task A_role_can_demand_it_until_it_is_set_up()
    {
        SkipWithoutDatabase();
        var code = "two." + Guid.NewGuid().ToString("N")[..12];
        var roleId = await _fixture.Roles.CreateRoleAsync(
            code, "Test role", string.Empty, [], "tests", Context, Ct);
        _roles.Add(roleId);

        var (id, _) = await CreateAsync(code);
        var session = await _fixture.Sessions.CreateAsync(id, null, null, false, Ct);

        var before = await _fixture.Sessions.ResolveAsync(session.Token, Ct);
        Assert.False(before!.User.MustSetUpTwoStep);

        await _fixture.Roles.SetTwoStepRequiredAsync(roleId, true, "tests", Context, Ct);

        var demanded = await _fixture.Sessions.ResolveAsync(session.Token, Ct);
        Assert.True(demanded!.User.MustSetUpTwoStep);
        Assert.True((await _fixture.TwoStep.GetStatusAsync(id, Ct)).Required);

        var setup = await _fixture.TwoStep.BeginSetupAsync(id, Password, Context, Ct);
        await _fixture.TwoStep.ConfirmSetupAsync(
            id, CodeNow(DecodeBase32(setup!.Secret)), demanded.SessionId, Context, Ct);

        var after = await _fixture.Sessions.ResolveAsync(session.Token, Ct);
        Assert.False(after!.User.MustSetUpTwoStep);
    }

    private sealed record EnrolledAccount(
        long Id, string Username, byte[] Secret, IReadOnlyList<string> RecoveryCodes);
}

using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// Recovery, against a real database: the terms of a code are SQL, and so is what stops a guess.
/// </summary>
public sealed class AccountRecoveryTests : IClassFixture<IdentityFixture>, IAsyncLifetime
{
    private const string OldPassword = "Vx9-quiet-owl-lantern";
    private const string NewPassword = "Zq4-amber-river-stone";

    private readonly IdentityFixture _fixture;
    private readonly List<long> _users = [];

    public AccountRecoveryTests(IdentityFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthenticationContext Context => IdentityFixture.Context;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _users)
        {
            await _fixture.DeleteUserAsync(id);
        }
    }

    private void SkipWithoutDatabase()
    {
        if (!_fixture.IsAvailable)
        {
            Assert.Skip(_fixture.UnavailableReason ?? "no database");
        }
    }

    /// <summary>An account whose e-mail address and phone number are unique to this test.</summary>
    private async Task<TestAccount> CreateAsync(bool proved = true)
    {
        var username = IdentityFixture.UniqueUsername("rec");
        var marker = Guid.NewGuid().ToString("N")[..12];
        var email = $"{marker}@example.test";
        var phone = "+98912" + Random.Shared.Next(1_000_000, 9_999_999);

        var id = await _fixture.Users.CreateAsync(
            new NewUser(username, "Test Person", email, null, phone, [], MustChangePassword: false),
            OldPassword, "tests", Context, Ct);

        _users.Add(id);

        if (proved)
        {
            await _fixture.ExecuteAsync(
                "UPDATE auth.user_account SET email_verified_at = now(), phone_verified_at = now() "
                + "WHERE id = @id", ("id", id));
        }

        return new TestAccount(id, username, email, phone);
    }

    private Task AgeCodesAsync(long userId) =>
        _fixture.ExecuteAsync(
            "UPDATE auth.one_time_code SET created_at = created_at - interval '2 hours' "
            + "WHERE user_id = @id", ("id", userId));

    private async Task<string> SendCodeAsync(TestAccount account)
    {
        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);
        return _fixture.Delivery.LatestCode(account.Email);
    }

    private static string Wrong(string code) => code == "000000" ? "000001" : "000000";

    // ======================================================================= who gets a code

    [Fact]
    public async Task A_code_goes_to_a_proved_address()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);

        var sent = Assert.Single(_fixture.Delivery.To(account.Email));
        Assert.Equal(ContactChannel.Email, sent.Channel);
        Assert.Matches("^[0-9]{6} is your Quadrasense", sent.Message.Text);
        Assert.Contains(account.Username, sent.Message.Text);
    }

    [Fact]
    public async Task A_contact_that_was_never_proved_receives_nothing()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);
        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Sms, Context, Ct);

        Assert.Empty(_fixture.Delivery.To(account.Email));
        Assert.Empty(_fixture.Delivery.To(account.Phone));
    }

    [Fact]
    public async Task A_deactivated_account_receives_nothing()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await _fixture.Users.SetActiveAsync(account.Id, false, "left", "tests", Context, Ct);
        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);

        Assert.Empty(_fixture.Delivery.To(account.Email));
    }

    [Fact]
    public async Task A_proved_contact_finds_the_account_however_it_is_written()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await _fixture.Recovery.SendCodeAsync(
            account.Email.ToUpperInvariant(), ContactChannel.Email, Context, Ct);
        Assert.Single(_fixture.Delivery.To(account.Email));

        await AgeCodesAsync(account.Id);

        // The same number, written the local way, in Persian digits.
        var local = "0" + account.Phone[3..];
        var persian = string.Concat(local.Select(c => (char)('۰' + (c - '0'))));

        await _fixture.Recovery.SendCodeAsync(persian, ContactChannel.Sms, Context, Ct);
        Assert.Single(_fixture.Delivery.To(account.Phone));
    }

    [Fact]
    public async Task An_address_merely_typed_on_a_profile_finds_no_account()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        await _fixture.Recovery.SendCodeAsync(account.Email, ContactChannel.Email, Context, Ct);

        Assert.Empty(_fixture.Delivery.To(account.Email));
        var reasons = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM audit.event WHERE action = 'auth.recovery.request' "
            + "AND actor_name = @name AND detail->>'reason' = 'unknown_account'",
            ("name", account.Email));
        Assert.Equal(1, reasons);
    }

    // ======================================================================= the terms of a code

    [Fact]
    public async Task A_code_is_stored_only_as_a_hash()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        var code = await SendCodeAsync(account);

        var stored = await _fixture.ScalarAsync<string>(
            "SELECT code_hash FROM auth.one_time_code WHERE user_id = @id", ("id", account.Id));

        Assert.StartsWith("$argon2id$", stored);
        Assert.DoesNotContain(code, stored!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_lives_ten_minutes()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await SendCodeAsync(account);

        var lifetime = await _fixture.ScalarAsync<TimeSpan>(
            "SELECT expires_at - created_at FROM auth.one_time_code WHERE user_id = @id",
            ("id", account.Id));

        Assert.Equal(TimeSpan.FromMinutes(10), lifetime);
    }

    [Fact]
    public async Task The_right_code_changes_the_password_and_ends_every_session()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var session = await _fixture.Sessions.CreateAsync(account.Id, null, null, false, Ct);
        var code = await SendCodeAsync(account);

        var done = await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct);

        Assert.True(done);
        Assert.Null(await _fixture.Sessions.ResolveAsync(session.Token, Ct));

        var old = await _fixture.Authenticator.AuthenticateAsync(
            account.Username, OldPassword, Context, Ct);
        var fresh = await _fixture.Authenticator.AuthenticateAsync(
            account.Username, NewPassword, Context, Ct);

        Assert.Equal(LoginStatus.InvalidCredentials, old.Status);
        Assert.Equal(LoginStatus.Success, fresh.Status);
    }

    [Fact]
    public async Task A_reset_lifts_a_lockout_and_a_forced_change()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        await _fixture.ExecuteAsync(
            "UPDATE auth.user_account SET failed_login_count = 9, must_change_password = true, "
            + "locked_until = now() + interval '10 minutes' WHERE id = @id", ("id", account.Id));
        var code = await SendCodeAsync(account);

        await _fixture.Recovery.CompleteAsync(account.Username, code, NewPassword, Context, Ct);

        var outcome = await _fixture.Authenticator.AuthenticateAsync(
            account.Username, NewPassword, Context, Ct);

        Assert.Equal(LoginStatus.Success, outcome.Status);
        Assert.False(outcome.MustChangePassword);
    }

    [Fact]
    public async Task The_owner_is_told_after_a_reset()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        await _fixture.Recovery.CompleteAsync(account.Username, code, NewPassword, Context, Ct);

        Assert.Contains(_fixture.Outbox.Work, w =>
            w is AccountNoticeWork { Notice: AccountNotice.PasswordReset } n && n.UserId == account.Id);

        await _fixture.Recovery.SendNoticeAsync(account.Id, AccountNotice.PasswordReset, Ct);

        Assert.Contains("was just reset", _fixture.Delivery.To(account.Email)[^1].Message.Text);
        Assert.Contains("was just reset", _fixture.Delivery.To(account.Phone)[^1].Message.Text);
    }

    [Fact]
    public async Task A_code_works_once()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        Assert.True(await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct));
        Assert.False(await _fixture.Recovery.CompleteAsync(
            account.Username, code, "Yt6-winter-fern-copper", Context, Ct));
    }

    [Fact]
    public async Task A_code_past_its_time_fails()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        await _fixture.ExecuteAsync(
            "UPDATE auth.one_time_code SET expires_at = now() - interval '1 second' "
            + "WHERE user_id = @id", ("id", account.Id));

        Assert.False(await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct));
    }

    [Fact]
    public async Task Five_wrong_guesses_void_the_code_and_the_right_one_then_fails()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        for (var i = 0; i < 5; i++)
        {
            Assert.False(await _fixture.Recovery.CompleteAsync(
                account.Username, Wrong(code), NewPassword, Context, Ct));
        }

        Assert.False(await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct));

        var consumed = await _fixture.ScalarAsync<bool>(
            "SELECT consumed_at IS NOT NULL FROM auth.one_time_code WHERE user_id = @id",
            ("id", account.Id));
        Assert.True(consumed);
    }

    [Fact]
    public async Task Four_wrong_guesses_leave_the_right_code_working()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        for (var i = 0; i < 4; i++)
        {
            await _fixture.Recovery.CompleteAsync(
                account.Username, Wrong(code), NewPassword, Context, Ct);
        }

        Assert.True(await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct));
    }

    [Fact]
    public async Task Guesses_sent_at_the_same_moment_cannot_exceed_five()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _fixture.Recovery.CompleteAsync(
            account.Username, Wrong(code), NewPassword, Context, Ct)));

        var attempts = await _fixture.ScalarAsync<short>(
            "SELECT attempts FROM auth.one_time_code WHERE user_id = @id", ("id", account.Id));

        Assert.Equal(5, attempts);
        Assert.False(await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct));
    }

    [Fact]
    public async Task A_new_code_voids_the_one_before_it()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var first = await SendCodeAsync(account);

        await AgeCodesAsync(account.Id);
        var second = await SendCodeAsync(account);

        Assert.False(first != second && await _fixture.Recovery.CompleteAsync(
            account.Username, first, NewPassword, Context, Ct));
        Assert.True(await _fixture.Recovery.CompleteAsync(
            account.Username, second, NewPassword, Context, Ct));
    }

    // ======================================================================= how often

    [Fact]
    public async Task A_second_code_within_the_cooldown_is_not_sent()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);
        await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);

        Assert.Single(_fixture.Delivery.To(account.Email));
    }

    [Fact]
    public async Task No_more_than_five_codes_go_out_in_an_hour()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        for (var i = 0; i < 8; i++)
        {
            await _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct);

            // Past the cooldown, still inside the hour.
            await _fixture.ExecuteAsync(
                "UPDATE auth.one_time_code SET created_at = created_at - interval '2 minutes' "
                + "WHERE user_id = @id", ("id", account.Id));
        }

        Assert.Equal(5, _fixture.Delivery.To(account.Email).Count);
    }

    [Fact]
    public async Task Requests_at_the_same_moment_send_one_code()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            _fixture.Recovery.SendCodeAsync(account.Username, ContactChannel.Email, Context, Ct)));

        Assert.Single(_fixture.Delivery.To(account.Email));
    }

    // ======================================================================= saying nothing

    /// <summary>
    /// The claim that an unknown account cannot be told from a known one by timing rests on each
    /// path doing the same expensive thing the same number of times. One hash, on every path.
    /// </summary>
    [Fact]
    public async Task Every_failure_costs_exactly_one_hash()
    {
        SkipWithoutDatabase();
        var live = await CreateAsync();
        var idle = await CreateAsync();
        var code = await SendCodeAsync(live);

        async Task<int> Cost(string identifier, string typed)
        {
            var before = _fixture.Hasher.Work;
            Assert.False(await _fixture.Recovery.CompleteAsync(
                identifier, typed, NewPassword, Context, Ct));
            return _fixture.Hasher.Work - before;
        }

        Assert.Equal(1, await Cost("nobody." + Guid.NewGuid().ToString("N"), "123456"));
        Assert.Equal(1, await Cost(idle.Username, "123456"));
        Assert.Equal(1, await Cost(live.Username, Wrong(code)));
    }

    [Fact]
    public async Task A_password_that_breaks_the_rules_spends_no_attempt()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        await Assert.ThrowsAsync<PasswordPolicyException>(() =>
            _fixture.Recovery.CompleteAsync(account.Username, code, "short", Context, Ct));

        var attempts = await _fixture.ScalarAsync<short>(
            "SELECT attempts FROM auth.one_time_code WHERE user_id = @id", ("id", account.Id));
        Assert.Equal(0, attempts);

        Assert.True(await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct));
    }

    [Fact]
    public async Task A_password_holding_the_owners_name_leaves_the_code_usable()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();
        var code = await SendCodeAsync(account);

        await Assert.ThrowsAsync<PasswordPolicyException>(() => _fixture.Recovery.CompleteAsync(
            account.Username, code, "test person and more words", Context, Ct));

        Assert.True(await _fixture.Recovery.CompleteAsync(
            account.Username, code, NewPassword, Context, Ct));
    }

    [Fact]
    public async Task A_mail_server_that_refuses_is_recorded_and_raises_nothing()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        _fixture.Delivery.Failing = true;
        try
        {
            await _fixture.Recovery.SendCodeAsync(
                account.Username, ContactChannel.Email, Context, Ct);
        }
        finally
        {
            _fixture.Delivery.Failing = false;
        }

        var reasons = await _fixture.ScalarAsync<long>(
            "SELECT count(*) FROM audit.event WHERE action = 'auth.recovery.request' "
            + "AND actor_user_id = @id AND detail->>'reason' = 'delivery_failed'",
            ("id", account.Id));
        Assert.Equal(1, reasons);
    }

    // ======================================================================= proving a contact

    [Fact]
    public async Task Proving_a_contact_needs_the_password()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        var outcome = await _fixture.Contacts.SendCodeAsync(
            account.Id, ContactChannel.Email, "not the password", Context, Ct);

        Assert.Equal(ContactCodeOutcome.WrongPassword, outcome);
        Assert.Empty(_fixture.Delivery.To(account.Email));
    }

    [Fact]
    public async Task A_right_code_proves_an_address_and_recovery_then_reaches_it()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        Assert.Equal(ContactCodeOutcome.Sent, await _fixture.Contacts.SendCodeAsync(
            account.Id, ContactChannel.Email, OldPassword, Context, Ct));

        var code = _fixture.Delivery.LatestCode(account.Email);

        Assert.Equal(ContactConfirmOutcome.Verified, await _fixture.Contacts.ConfirmAsync(
            account.Id, ContactChannel.Email, code, Context, Ct));

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.NotNull(user!.EmailVerifiedAt);
        Assert.Null(user.PhoneVerifiedAt);

        await AgeCodesAsync(account.Id);
        await _fixture.Recovery.SendCodeAsync(account.Email, ContactChannel.Email, Context, Ct);
        Assert.Equal(2, _fixture.Delivery.To(account.Email).Count);
    }

    [Fact]
    public async Task A_proved_number_is_kept_in_international_form()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);
        var local = "0" + account.Phone[3..];

        await _fixture.Users.UpdateProfileAsync(
            account.Id, new ProfileUpdate("Test Person", account.Email, null, local), Context, Ct);

        await _fixture.Contacts.SendCodeAsync(
            account.Id, ContactChannel.Sms, OldPassword, Context, Ct);

        await _fixture.Contacts.ConfirmAsync(
            account.Id, ContactChannel.Sms, _fixture.Delivery.LatestCode(account.Phone), Context, Ct);

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.Equal(account.Phone, user!.Phone);
        Assert.NotNull(user.PhoneVerifiedAt);
    }

    [Fact]
    public async Task Changing_a_contact_takes_its_proof_away()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await _fixture.Users.UpdateProfileAsync(
            account.Id,
            new ProfileUpdate("Test Person", "someone.else@example.test", null, "+989129999999"),
            Context, Ct);

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.Null(user!.EmailVerifiedAt);
        Assert.Null(user.PhoneVerifiedAt);
    }

    [Fact]
    public async Task An_administrator_changing_a_contact_takes_its_proof_away_too()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        await _fixture.Users.UpdateAsync(
            account.Id,
            new UserUpdate("Test Person", "someone.else@example.test", null, account.Phone),
            "tests", Context, Ct);

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.Null(user!.EmailVerifiedAt);
        Assert.NotNull(user.PhoneVerifiedAt);
    }

    [Fact]
    public async Task Saving_a_profile_unchanged_keeps_the_proof()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        // The number as a person would retype it, with the address in another case.
        await _fixture.Users.UpdateProfileAsync(
            account.Id,
            new ProfileUpdate(
                "Test Person", account.Email.ToUpperInvariant(), null, "0" + account.Phone[3..]),
            Context, Ct);

        var user = await _fixture.Users.GetAsync(account.Id, Ct);
        Assert.NotNull(user!.EmailVerifiedAt);
        Assert.NotNull(user.PhoneVerifiedAt);
        Assert.Equal(account.Phone, user.Phone);
    }

    [Fact]
    public async Task A_code_sent_to_an_old_address_proves_nothing_about_the_new_one()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        await _fixture.Contacts.SendCodeAsync(
            account.Id, ContactChannel.Email, OldPassword, Context, Ct);
        var code = _fixture.Delivery.LatestCode(account.Email);

        await _fixture.Users.UpdateProfileAsync(
            account.Id,
            new ProfileUpdate("Test Person", "victim@example.test", null, null),
            Context, Ct);

        Assert.Equal(ContactConfirmOutcome.InvalidCode, await _fixture.Contacts.ConfirmAsync(
            account.Id, ContactChannel.Email, code, Context, Ct));
    }

    [Fact]
    public async Task Two_accounts_cannot_prove_one_address()
    {
        SkipWithoutDatabase();
        var first = await CreateAsync();
        var second = await CreateAsync(proved: false);

        await _fixture.Users.UpdateProfileAsync(
            second.Id, new ProfileUpdate("Test Person", first.Email, null, null), Context, Ct);

        await _fixture.Contacts.SendCodeAsync(
            second.Id, ContactChannel.Email, OldPassword, Context, Ct);

        Assert.Equal(ContactConfirmOutcome.InUse, await _fixture.Contacts.ConfirmAsync(
            second.Id, ContactChannel.Email, _fixture.Delivery.LatestCode(first.Email), Context, Ct));
    }

    [Fact]
    public async Task What_is_already_proved_is_not_proved_again()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync();

        Assert.Equal(ContactCodeOutcome.AlreadyVerified, await _fixture.Contacts.SendCodeAsync(
            account.Id, ContactChannel.Email, OldPassword, Context, Ct));
    }

    [Fact]
    public async Task A_profile_with_nothing_to_prove_says_so()
    {
        SkipWithoutDatabase();
        var account = await CreateAsync(proved: false);

        await _fixture.Users.UpdateProfileAsync(
            account.Id, new ProfileUpdate("Test Person", "not an address", null, "not a number"),
            Context, Ct);

        Assert.Equal(ContactCodeOutcome.NoContact, await _fixture.Contacts.SendCodeAsync(
            account.Id, ContactChannel.Email, OldPassword, Context, Ct));
        Assert.Equal(ContactCodeOutcome.NoContact, await _fixture.Contacts.SendCodeAsync(
            account.Id, ContactChannel.Sms, OldPassword, Context, Ct));
    }

    private sealed record TestAccount(long Id, string Username, string Email, string Phone);
}

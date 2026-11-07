using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;

namespace Sqm.Api.Endpoints;

/// <summary>
/// Getting back into an account, proving a contact, and two-step sign-in. ADR-022.
/// </summary>
public static class AccountSecurityEndpoints
{
    /// <summary>Rate-limiter partition name for the anonymous recovery endpoints.</summary>
    public const string RecoveryRateLimiter = "recovery";

    /// <summary>
    /// Rate-limiter partition name for anything that asks a signed-in person for their password
    /// or a code again.
    /// </summary>
    public const string ConfirmRateLimiter = "confirm";

    private const int MaxIdentifierLength = 320;

    /// <summary>Registers the routes.</summary>
    public static IEndpointRouteBuilder MapAccountSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapGet("/options", SignInOptions)
            .AllowAnonymous()
            .WithName("SignInOptions")
            .WithSummary("What the sign-in page may offer on this deployment.");

        group.MapPost("/recovery/start", StartRecovery)
            .AllowAnonymous()
            .RequireRateLimiting(RecoveryRateLimiter)
            .WithName("StartRecovery")
            .WithSummary("Asks for a recovery code. Answers the same whether or not the account exists.");

        group.MapPost("/recovery/complete", CompleteRecoveryAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RecoveryRateLimiter)
            .WithName("CompleteRecovery")
            .WithSummary("Sets a new password, given a recovery code.");

        group.MapPost("/me/contacts/{channel}/code", SendContactCodeAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .RequireRateLimiting(ConfirmRateLimiter)
            .WithName("SendContactCode")
            .WithSummary("Sends a code to the e-mail address or phone number on the profile.");

        group.MapPost("/me/contacts/{channel}/confirm", ConfirmContactAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .RequireRateLimiting(ConfirmRateLimiter)
            .WithName("ConfirmContact")
            .WithSummary("Marks a contact as proved, given the code sent to it.");

        group.MapGet("/me/two-step", TwoStepStatusAsync)
            .RequireAuthorization(PermissionPolicyProvider.PasswordChangeAllowed)
            .WithName("TwoStepStatus")
            .WithSummary("Whether two-step sign-in is on, and how many recovery codes are left.");

        group.MapPost("/me/two-step/setup", BeginTwoStepAsync)
            .RequireAuthorization(PermissionPolicyProvider.PasswordChangeAllowed)
            .RequireRateLimiting(ConfirmRateLimiter)
            .WithName("BeginTwoStep")
            .WithSummary("Creates a secret for an authenticator app.");

        group.MapPost("/me/two-step/confirm", ConfirmTwoStepAsync)
            .RequireAuthorization(PermissionPolicyProvider.PasswordChangeAllowed)
            .RequireRateLimiting(ConfirmRateLimiter)
            .WithName("ConfirmTwoStep")
            .WithSummary("Turns two-step sign-in on and returns recovery codes, once.");

        group.MapPost("/me/two-step/recovery-codes", NewRecoveryCodesAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .RequireRateLimiting(ConfirmRateLimiter)
            .WithName("NewRecoveryCodes")
            .WithSummary("Replaces the recovery codes.");

        group.MapPost("/me/two-step/disable", DisableTwoStepAsync)
            .RequireAuthorization(PermissionPolicyProvider.Authenticated)
            .RequireRateLimiting(ConfirmRateLimiter)
            .WithName("DisableTwoStep")
            .WithSummary("Turns two-step sign-in off, given the password and a code.");

        return app;
    }

    // ======================================================================= the sign-in page

    private static SignInOptionsResponse SignInOptions(
        IContactDelivery delivery, ITwoStepService twoStep, IOptions<AuthOptions> authOptions)
    {
        var auth = authOptions.Value;

        return new SignInOptionsResponse(
            delivery.IsAvailable(ContactChannel.Email),
            delivery.IsAvailable(ContactChannel.Sms),
            twoStep.IsAvailable,
            (int)Math.Ceiling(auth.Session.RememberedLifetime.TotalDays),
            (int)Math.Ceiling(auth.Recovery.CodeLifetime.TotalMinutes));
    }

    /// <summary>Queues the request and answers.</summary>
    /// <remarks>
    /// Nothing about any account is looked at here, on purpose: a response that does the same
    /// work for everyone cannot be timed. The queue being full is not reported either.
    /// </remarks>
    private static IResult StartRecovery(
        RecoveryStartRequest request, HttpContext http, IAccountOutbox outbox,
        IContactDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identifier = request.Identifier?.Trim();

        if (string.IsNullOrEmpty(identifier) || identifier.Length > MaxIdentifierLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["identifier"] = ["Enter your username, e-mail address or phone number."],
            });
        }

        // Whether a channel is configured is the same for every account, and the page has been
        // told already, so saying so here gives nothing away.
        if (request.Channel is not { } channel || !delivery.IsAvailable(channel))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["channel"] = ["That way of sending a code is not available."],
            });
        }

        outbox.TryEnqueue(new RecoveryCodeWork(identifier, channel, CurrentUser.Context(http)));

        return Results.Accepted();
    }

    private static async Task<IResult> CompleteRecoveryAsync(
        RecoveryCompleteRequest request, HttpContext http, IAccountRecovery recovery,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Identifier)
            || string.IsNullOrWhiteSpace(request.Code)
            || string.IsNullOrEmpty(request.NewPassword))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["Enter the code and a new password."],
            });
        }

        try
        {
            var done = await recovery.CompleteAsync(
                request.Identifier, request.Code, request.NewPassword, CurrentUser.Context(http), ct)
                .ConfigureAwait(false);

            return done
                ? Results.NoContent()
                : Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["code"] = ["That code is not right, or it has expired. Ask for a new one."],
                });
        }
        catch (PasswordPolicyException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["newPassword"] = [ex.Message],
            });
        }
    }

    // ======================================================================= contacts

    private static async Task<IResult> SendContactCodeAsync(
        string channel, ContactCodeRequest request, HttpContext http,
        IContactVerification verification, IContactDelivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ParseChannel(channel) is not { } parsed)
        {
            return Results.NotFound();
        }

        if (!delivery.IsAvailable(parsed))
        {
            return Refused("That way of sending a code is not available on this system.");
        }

        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            return WrongPassword();
        }

        var outcome = await verification.SendCodeAsync(
            CurrentUser.Require(http).UserId, parsed, request.CurrentPassword,
            CurrentUser.Context(http), ct).ConfigureAwait(false);

        return outcome switch
        {
            ContactCodeOutcome.Sent => Results.Accepted(),
            ContactCodeOutcome.WrongPassword => WrongPassword(),
            ContactCodeOutcome.NoContact => Refused(parsed == ContactChannel.Email
                ? "Add a valid e-mail address to your profile first."
                : "Add a valid phone number to your profile first."),
            ContactCodeOutcome.AlreadyVerified => Refused("This is already verified."),
            ContactCodeOutcome.TooSoon =>
                Refused("A code was sent a moment ago. Wait a minute and try again."),
            _ => Results.Problem(
                detail: "The code could not be sent. Try again later.",
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static async Task<IResult> ConfirmContactAsync(
        string channel, CodeRequest request, HttpContext http, IContactVerification verification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ParseChannel(channel) is not { } parsed)
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return WrongCode();
        }

        var outcome = await verification.ConfirmAsync(
            CurrentUser.Require(http).UserId, parsed, request.Code, CurrentUser.Context(http), ct)
            .ConfigureAwait(false);

        return outcome switch
        {
            ContactConfirmOutcome.Verified => Results.NoContent(),
            ContactConfirmOutcome.InUse =>
                Refused("Another account already uses this. Ask an administrator."),
            _ => WrongCode(),
        };
    }

    // ======================================================================= two-step

    private static async Task<TwoStepStatus> TwoStepStatusAsync(
        HttpContext http, ITwoStepService twoStep, CancellationToken ct) =>
        await twoStep.GetStatusAsync(CurrentUser.Require(http).UserId, ct).ConfigureAwait(false);

    private static async Task<IResult> BeginTwoStepAsync(
        PasswordRequest request, HttpContext http, ITwoStepService twoStep, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            return WrongPassword();
        }

        try
        {
            var setup = await twoStep.BeginSetupAsync(
                CurrentUser.Require(http).UserId, request.CurrentPassword,
                CurrentUser.Context(http), ct).ConfigureAwait(false);

            return setup is null ? WrongPassword() : Results.Ok(setup);
        }
        catch (TwoStepRefusedException ex)
        {
            return Refused(ex.Message);
        }
    }

    private static async Task<IResult> ConfirmTwoStepAsync(
        CodeRequest request, HttpContext http, ITwoStepService twoStep, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return WrongCode();
        }

        var session = CurrentUser.Session(http)!;

        var codes = await twoStep.ConfirmSetupAsync(
            session.User.UserId, request.Code, session.SessionId, CurrentUser.Context(http), ct)
            .ConfigureAwait(false);

        return codes is null ? WrongCode() : Results.Ok(new RecoveryCodesResponse(codes));
    }

    private static async Task<IResult> NewRecoveryCodesAsync(
        PasswordRequest request, HttpContext http, ITwoStepService twoStep, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            return WrongPassword();
        }

        try
        {
            var codes = await twoStep.RegenerateRecoveryCodesAsync(
                CurrentUser.Require(http).UserId, request.CurrentPassword,
                CurrentUser.Context(http), ct).ConfigureAwait(false);

            return codes is null ? WrongPassword() : Results.Ok(new RecoveryCodesResponse(codes));
        }
        catch (TwoStepRefusedException ex)
        {
            return Refused(ex.Message);
        }
    }

    private static async Task<IResult> DisableTwoStepAsync(
        DisableTwoStepRequest request, HttpContext http, ITwoStepService twoStep,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["Enter your password and a code from your authenticator."],
            });
        }

        var userId = CurrentUser.Require(http).UserId;
        var status = await twoStep.GetStatusAsync(userId, ct).ConfigureAwait(false);

        if (status.Required)
        {
            return Refused("A role you hold requires two-step sign-in.");
        }

        var done = await twoStep.DisableAsync(
            userId, request.CurrentPassword, request.Code, CurrentUser.Context(http), ct)
            .ConfigureAwait(false);

        return done
            ? Results.NoContent()
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["That password or code is not right."],
            });
    }

    // =======================================================================

    private static ContactChannel? ParseChannel(string channel) => channel switch
    {
        "email" => ContactChannel.Email,
        "sms" => ContactChannel.Sms,
        _ => null,
    };

    private static IResult WrongPassword() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["currentPassword"] = ["That is not your current password."],
        });

    private static IResult WrongCode() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["code"] = ["That code is not right, or it has expired."],
        });

    private static IResult Refused(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status409Conflict);
}

/// <summary>What the sign-in page may offer.</summary>
/// <param name="RecoverByEmail">A mail server is configured.</param>
/// <param name="RecoverBySms">An SMS gateway is configured.</param>
/// <param name="TwoStepAvailable">Authenticator sign-in can be set up.</param>
/// <param name="RememberDays">How long "remember me" lasts.</param>
/// <param name="CodeMinutes">How long a code works.</param>
public sealed record SignInOptionsResponse(
    bool RecoverByEmail,
    bool RecoverBySms,
    bool TwoStepAvailable,
    int RememberDays,
    int CodeMinutes);

/// <summary>A request for a recovery code.</summary>
public sealed record RecoveryStartRequest(string? Identifier, ContactChannel? Channel);

/// <summary>A code and the password it allows.</summary>
public sealed record RecoveryCompleteRequest(string? Identifier, string? Code, string? NewPassword);

/// <summary>The password, asked again before sending a code.</summary>
public sealed record ContactCodeRequest(string? CurrentPassword);

/// <summary>The password, asked again.</summary>
public sealed record PasswordRequest(string? CurrentPassword);

/// <summary>A code.</summary>
public sealed record CodeRequest(string? Code);

/// <summary>Both, to turn the protection off.</summary>
public sealed record DisableTwoStepRequest(string? CurrentPassword, string? Code);

/// <summary>Recovery codes, the one time they are shown.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);

using System.Globalization;
using Sqm.Api.Auth;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Domain.Devices;
using Sqm.Contracts.Devices;
using Sqm.Contracts.Lookup;
using Sqm.Domain.Identifiers;

namespace Sqm.Api.Endpoints;

/// <summary>Body for a device catalogue search.</summary>
/// <param name="Query">
/// What was typed. May be a name, or any of TAC, MSISDN, IMEI or IMSI - the server classifies it.
/// </param>
/// <param name="Manufacturer">Exact vendor name, from the facet list.</param>
/// <param name="Brand">Exact brand name.</param>
/// <param name="DeviceType">Exact GSMA device type.</param>
/// <param name="OperatingSystem">Exact operating-system string.</param>
/// <param name="MinBindings">Drop models below this population.</param>
/// <param name="Sort">Which column orders the result.</param>
/// <param name="Descending">Direction.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page; the server caps it.</param>
/// <remarks>
/// A POST with the term in the body, not a GET with it in the query string - because
/// <c>Query</c> can be a real subscriber number or SIM identity, and a GET would put it into
/// web-server access logs, browser history and any <c>Referer</c> the page emits. By the time
/// masking could apply, the value would already be in three logs.
/// </remarks>
public sealed record DeviceSearchRequest(
    string? Query = null,
    string? Manufacturer = null,
    string? Brand = null,
    string? DeviceType = null,
    string? OperatingSystem = null,
    long MinBindings = 0,
    DeviceSort Sort = DeviceSort.Bindings,
    bool Descending = true,
    int Page = 1,
    int PageSize = 40);

/// <summary>Body for a request for one device model's identifiers.</summary>
/// <param name="ActiveOnly">Exclude bindings that are no longer in force.</param>
/// <param name="From">Earliest last-change date, inclusive.</param>
/// <param name="To">Latest last-change date, inclusive.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page; the server caps it.</param>
public sealed record DeviceIdentifiersRequest(
    bool ActiveOnly = false,
    DateOnly? From = null,
    DateOnly? To = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>The Devices module: catalogue, detail, timeline, identifiers and imagery.</summary>
/// <remarks>
/// <para>
/// Four permissions, because four different things happen here and they carry different risk.
/// <c>device.view</c> opens the catalogue, which names nobody. <c>lookup.imei</c> resolves one
/// handset. <c>device.identifiers</c> lists everybody who owns a model. <c>device.image.manage</c>
/// writes. A reader may hold the first and none of the others.
/// </para>
/// <para>
/// The catalogue routes are <c>device.view</c> at the group level, and the three that reach
/// personal data check their own permission inside the handler - which means those checks are
/// <b>invisible to the pipeline's automatic audit</b>, so each writes its own entry. That is not
/// theoretical tidiness: an earlier permission check made inside a handler in this codebase
/// bypassed the audit exactly this way and the refusal was never recorded.
/// </para>
/// </remarks>
public static class DeviceEndpoints
{
    /// <summary>Largest page of models the server will return.</summary>
    private const int MaxPageSize = 100;

    /// <summary>Largest page of identifiers the server will return.</summary>
    /// <remarks>
    /// Lower than the model cap on purpose. This is the bulk-exposure route: the most populous
    /// model covers 208,895 handsets, and a caller who wants all of them should have to ask 1,045
    /// times and leave 1,045 audit entries doing it.
    /// </remarks>
    private const int MaxIdentifierPageSize = 200;

    /// <summary>Largest image the server will store. Matches the database's own check.</summary>
    private const int MaxImageBytes = 512 * 1024;

    /// <summary>Registers the device routes.</summary>
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/devices").WithTags("Devices")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DeviceView);

        group.MapPost("/search", SearchAsync)
            .WithName("SearchDevices")
            .WithSummary("A page of the device catalogue, by name or by any identifier.");

        group.MapGet("/facets", FacetsAsync)
            .WithName("GetDeviceFacets")
            .WithSummary("Values available to filter the catalogue by.");

        // The route constraint is what keeps /facets from ever being read as a TAC. Every TAC is
        // exactly eight digits - all 270,166 rows of the GSMA export, measured - so the constraint
        // is also a real validation rather than a routing trick.
        group.MapGet("/{tac:regex(^[0-9]{{8}}$)}", DetailAsync)
            .WithName("GetDevice")
            .WithSummary("Everything known about one device model.");

        group.MapGet("/{tac:regex(^[0-9]{{8}}$)}/timeline", TimelineAsync)
            .WithName("GetDeviceTimeline")
            .WithSummary("Daily add and remove counts for one device model.");

        group.MapPost("/{tac:regex(^[0-9]{{8}}$)}/identifiers", IdentifiersAsync)
            .WithName("GetDeviceIdentifiers")
            .WithSummary("A page of the identifiers bound to one device model.");

        group.MapGet("/{tac:regex(^[0-9]{{8}}$)}/image", ImageAsync)
            .WithName("GetDeviceImage")
            .WithSummary("The curated photograph for one device model, if there is one.");

        group.MapPut("/{tac:regex(^[0-9]{{8}}$)}/image", UploadImageAsync)
            .WithName("UploadDeviceImage")
            .WithSummary("Store or replace one device model's photograph.")
            .DisableAntiforgery();

        group.MapDelete("/{tac:regex(^[0-9]{{8}}$)}/image", DeleteImageAsync)
            .WithName("DeleteDeviceImage")
            .WithSummary("Remove one device model's photograph.");

        return app;
    }

    // ====================================================================== catalogue

    private static async Task<IResult> SearchAsync(
        DeviceSearchRequest request,
        HttpContext http,
        IDeviceAnalyticsStore store,
        IDeviceImageStore images,
        IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 40 : request.PageSize, 1, MaxPageSize);
        var user = CurrentUser.Require(http);

        var term = DeviceSearchTerm.Classify(request.Query);
        string? text = null;
        IReadOnlyList<string>? tacs = null;
        DeviceSearchResolution? resolution = null;

        if (term is { IsIdentifier: true } identifier)
        {
            var outcome = await ResolveIdentifierAsync(
                identifier, user, store, audit, http, ct).ConfigureAwait(false);

            resolution = outcome.Resolution;
            tacs = outcome.Tacs;

            // An identifier that resolved to nothing must NOT fall back to a text search. Matching
            // "09121234567" against manufacturer names would return an unrelated list and read as
            // an answer. An empty result with a note is the truthful outcome.
            if (tacs.Count == 0)
            {
                return Results.Ok(new DeviceListResponse(
                    0, page, pageSize, [], resolution, new SearchTiming(0, 0)));
            }
        }
        else if (term is { } plain)
        {
            text = plain.Text;
        }

        var result = await store.SearchDevicesAsync(
            new DeviceListCriteria(
                Text: text,
                Manufacturer: request.Manufacturer,
                Brand: request.Brand,
                DeviceType: request.DeviceType,
                OperatingSystem: request.OperatingSystem,
                Tacs: tacs,
                MinBindings: Math.Max(0, request.MinBindings),
                Sort: MapSort(request.Sort),
                Descending: request.Descending,
                Offset: (int)Math.Min((page - 1L) * pageSize, int.MaxValue),
                Limit: pageSize),
            ct).ConfigureAwait(false);

        // Asked by MODEL, because that is what a picture belongs to. The rows already carry
        // brand and marketing name, so the keys cost nothing to compute, and a page showing
        // several TACs of one model resolves them all to the same key - one lookup, and every
        // one of them draws the photograph instead of one drawing it and the rest a placeholder.
        var keys = result.Rows
            .Select(r => DeviceModelKey.For(r.Brand, r.Manufacturer, r.MarketingName))
            .OfType<string>()
            .ToList();

        var present = await images.GetPresentAsync(keys, ct).ConfigureAwait(false);

        bool HasImage(DeviceRow row)
        {
            var key = DeviceModelKey.For(row.Brand, row.Manufacturer, row.MarketingName);
            return key is not null && present.Contains(key);
        }

        return Results.Ok(new DeviceListResponse(
            Total: result.Total,
            Page: page,
            PageSize: pageSize,
            Items: [.. result.Rows.Select(r => ToSummary(r, HasImage(r)))],
            Resolution: resolution,
            Timing: new SearchTiming(result.ElapsedMs, result.RowsExamined)));
    }

    /// <summary>
    /// Turns an identifier into the device models it reaches, checking the permission for its kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each identifier kind needs its own permission, and a caller who lacks it gets an explicit
    /// refusal rather than an empty list: "you may not resolve an IMSI" and "no such SIM" are
    /// different facts, and showing the second for the first teaches people the data is missing
    /// when it is not.
    /// </para>
    /// <para>
    /// A refusal here is audited explicitly. The check happens inside the handler, so the
    /// authorization pipeline never sees it and would never record it.
    /// </para>
    /// </remarks>
    private static async Task<(DeviceSearchResolution Resolution, IReadOnlyList<string> Tacs)>
        ResolveIdentifierAsync(
            DeviceSearchTerm term, AuthenticatedUser user, IDeviceAnalyticsStore store, IAuditLog audit,
            HttpContext http, CancellationToken ct)
    {
        var kind = term.Kind.ToString();

        // The unknown-device sentinel is not a handset and resolves to no model: 000000 is what the
        // source writes when it does not know the device. Saying so is more use than an empty list.
        if (term.Kind is DeviceSearchKind.UnknownDeviceSentinel)
        {
            // Resolves to the empty TAC, which is a real row in the catalogue carrying 5,557,995
            // bindings. Returning nothing here would be the wrong answer twice over: the bindings
            // exist, and the reader who typed 000000 is asking about exactly them.
            return (new DeviceSearchResolution(kind, 1, true,
                "000000 is the source's marker for \"device not known\", not a handset. These are "
                + "the bindings whose IMEI identifies no model."),
                [string.Empty]);
        }

        // A TAC is the model. No query, no permission beyond device.view: eight digits identify a
        // product, not a person.
        if (term.Kind is DeviceSearchKind.Tac && term.TryGetTac(out var direct))
        {
            return (new DeviceSearchResolution(kind, 1, true, null), [direct]);
        }

        var required = term.Kind switch
        {
            DeviceSearchKind.Imei => Permissions.LookupImei,
            DeviceSearchKind.Imsi => Permissions.LookupImsi,
            DeviceSearchKind.Msisdn => Permissions.LookupSubscriber,
            _ => null,
        };

        if (required is not null && !user.Can(required))
        {
            await audit.WriteAsync(Entry(
                user, http, required, AuditOutcome.Denied, "device-search",
                new Dictionary<string, object?> { ["kind"] = kind }), ct).ConfigureAwait(false);

            return (new DeviceSearchResolution(kind, 0, false,
                $"That looks like a{(term.Kind is DeviceSearchKind.Imei ? "n" : string.Empty)} "
                + $"{kind.ToUpperInvariant()}, and you do not have the \"{required}\" permission "
                + "needed to resolve one. Ask an administrator, or search by name instead."), []);
        }

        var resolved = await store.ResolveDeviceAsync(term.Kind, term.Digits, ct)
            .ConfigureAwait(false);

        // Resolving an identifier to a person's handset is a lookup, and lookups are audited here
        // for the same reason they are on the lookup pages: with masking off by product decision,
        // this entry is the primary record of who looked at whom. The identifier itself is NOT
        // recorded - an audit log full of IMSIs is a second copy of the data it exists to protect.
        if (required is not null)
        {
            await audit.WriteAsync(Entry(
                user, http, required, AuditOutcome.Success, "device-search",
                new Dictionary<string, object?>
                {
                    ["kind"] = kind,
                    ["models"] = resolved.Tacs.Count,
                    ["elapsedMs"] = resolved.ElapsedMs,
                }), ct).ConfigureAwait(false);
        }

        var note = resolved.Tacs.Count switch
        {
            0 => $"No {kind.ToUpperInvariant()} matching that is on the network. It may never have "
                 + "been seen, or its handset may have no well-formed IMEI.",
            1 => null,
            _ => $"This {kind.ToUpperInvariant()} has been bound to {resolved.Tacs.Count} different "
                 + "device models.",
        };

        return (new DeviceSearchResolution(kind, resolved.Tacs.Count, true, note), resolved.Tacs);
    }

    private static async Task<IResult> FacetsAsync(IDeviceAnalyticsStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var facets = await store.GetDeviceFacetsAsync(ct).ConfigureAwait(false);

        return Results.Ok(new DeviceFacets(
            DeviceTypes: [.. facets.DeviceTypes.Select(f => new DeviceFacetValue(f.Value, f.Models))],
            Manufacturers: [.. facets.Manufacturers.Select(f => new DeviceFacetValue(f.Value, f.Models))],
            OperatingSystems: [.. facets.OperatingSystems.Select(f => new DeviceFacetValue(f.Value, f.Models))]));
    }

    // ========================================================================= detail

    private static async Task<IResult> DetailAsync(
        string tac,
        IDeviceAnalyticsStore store,
        IDeviceImageStore images,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(images);

        var row = await store.GetDeviceAsync(tac, ct).ConfigureAwait(false);

        if (row is null)
        {
            return Results.NotFound(new
            {
                tac,
                message = "No device model with that code is present in the latest delivery.",
            });
        }

        // The row already names the model, so resolving the picture costs no extra query.
        var modelKey = DeviceModelKey.For(
            row.Summary.Brand, row.Summary.Manufacturer, row.Summary.MarketingName);

        var image = modelKey is null
            ? null
            : await images.GetInfoAsync(modelKey, ct).ConfigureAwait(false);

        // A model absent at the first delivery has no percentage change. Answering 0 showed "+0.00%"
        // and, against a network that fell 9.3%, a green "+9.3 pts" for every new model.
        double? change = row.BindingsAtStart == 0
            ? null
            : Math.Round(100.0 * (row.Summary.Bindings - row.BindingsAtStart) / row.BindingsAtStart, 4);

        double? networkChange = row.NetworkAtStart == 0
            ? null
            : Math.Round(100.0 * (row.NetworkNow - row.NetworkAtStart) / row.NetworkAtStart, 4);

        return Results.Ok(new DeviceDetail(
            Tac: row.Summary.Tac,
            Manufacturer: row.Summary.Manufacturer,
            Vendor: row.Summary.Vendor,
            Brand: row.Summary.Brand,
            Model: row.Summary.Model,
            MarketingName: row.Summary.MarketingName,
            DeviceType: row.Summary.DeviceType,
            OperatingSystem: row.Summary.OperatingSystem,
            Oem: row.Oem,
            OrganisationId: row.OrganisationId,
            AllocationDate: row.AllocationDate,
            LastUpdatedDate: row.LastUpdatedDate,
            Bluetooth: row.Bluetooth,
            Nfc: row.Nfc,
            Wlan: row.Wlan,
            SimSlots: row.SimSlots,
            ImeiQuantity: row.ImeiQuantity,
            Bands: row.Bands,
            Capabilities: new DeviceCapabilities(
                row.HasLte, row.Has5g, row.HasEsim, row.ImsEmergency),
            Population: new DevicePopulation(
                Bindings: row.Summary.Bindings,
                Handsets: row.Summary.Handsets,
                Sims: row.Summary.Sims,
                Subscribers: row.Summary.Subscribers,
                BindingsAtStart: row.BindingsAtStart,
                ChangePercent: change,
                VsNetworkPoints: change is { } c && networkChange is { } n ? Math.Round(c - n, 4) : null,
                FirstSeen: row.Summary.FirstSeen,
                LastSeen: row.Summary.LastSeen),
            KnownToGsma: row.KnownToGsma,
            HasImage: image is not null,
            ImageSourceNote: image?.SourceNote,
            ImageUpdatedAt: image?.UpdatedAt,
            TacVersionId: row.TacVersionId));
    }

    private static async Task<IResult> TimelineAsync(
        string tac,
        DateOnly? from,
        DateOnly? toDate,
        IDeviceAnalyticsStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (from is { } f && toDate is { } t && f > t)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["The start of the range is after its end."],
            });
        }

        var outcome = await store.GetDeviceTimelineAsync(tac, from, toDate, ct).ConfigureAwait(false);

        return Results.Ok(new DeviceTimelineResponse(
            Tac: tac,
            From: from,
            To: toDate,
            EarliestAvailable: outcome.Earliest,
            LatestAvailable: outcome.Latest,
            Points: [.. outcome.Points.Select(p => new DeviceTimelinePoint(p.Date, p.Added, p.Removed))],
            Timing: new SearchTiming(outcome.ElapsedMs, outcome.RowsExamined)));
    }

    // ==================================================================== identifiers

    /// <remarks>
    /// Behind <c>device.identifiers</c>, checked here rather than on the group, because every
    /// other route in this file is open to <c>device.view</c>. Audited on every call - success and
    /// refusal alike - with the number of rows returned and never with the identifiers themselves.
    /// </remarks>
    private static async Task<IResult> IdentifiersAsync(
        string tac,
        DeviceIdentifiersRequest request,
        HttpContext http,
        IDeviceAnalyticsStore store,
        IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = CurrentUser.Require(http);

        if (!user.Can(Permissions.DeviceIdentifiers))
        {
            await audit.WriteAsync(Entry(
                user, http, Permissions.DeviceIdentifiers, AuditOutcome.Denied, "device",
                new Dictionary<string, object?> { ["tac"] = tac }), ct).ConfigureAwait(false);

            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not permitted",
                detail: "Listing every identifier bound to a device model needs the "
                        + "\"device.identifiers\" permission.");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(
            request.PageSize <= 0 ? 50 : request.PageSize, 1, MaxIdentifierPageSize);

        if (request.From is { } from && request.To is { } to && from > to)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["The start of the range is after its end."],
            });
        }

        var outcome = await store.GetDeviceIdentifiersAsync(
            new DeviceIdentifierCriteria(
                Tac: tac,
                ActiveOnly: request.ActiveOnly,
                From: request.From,
                To: request.To,
                Offset: (int)Math.Min((page - 1L) * pageSize, int.MaxValue),
                Limit: pageSize),
            ct).ConfigureAwait(false);

        var reveal = user.Can(Permissions.IdentifierReveal);

        await audit.WriteAsync(Entry(
            user, http, Permissions.DeviceIdentifiers, AuditOutcome.Success, "device",
            new Dictionary<string, object?>
            {
                ["tac"] = tac,
                ["results"] = outcome.Total,
                ["returned"] = outcome.Rows.Count,
                ["masked"] = !reveal,
                ["elapsedMs"] = outcome.ElapsedMs,
            }), ct).ConfigureAwait(false);

        return Results.Ok(new DeviceIdentifiersResponse(
            Tac: tac,
            Total: outcome.Total,
            Page: page,
            PageSize: pageSize,
            Identifiers: reveal ? IdentifierVisibility.Full : IdentifierVisibility.Masked,
            Items: [.. outcome.Rows.Select(r => new DeviceIdentifierRow(
                // The TAC half of the IMEI is what the reader already typed to get here, so it is
                // never the secret; the serial is. IdentifierMask.Imei keeps exactly that split.
                Imei: reveal ? r.Imei : IdentifierMask.Imei(r.Imei),
                Imsi: reveal ? Digits(r.Imsi) : IdentifierMask.Imsi(r.Imsi),
                Msisdn: reveal ? Digits(r.Msisdn) : IdentifierMask.Msisdn(r.Msisdn),
                IsActive: r.IsActive,
                LastChangeDate: r.LastChangeDate))],
            Timing: new SearchTiming(outcome.ElapsedMs, outcome.RowsExamined)));
    }

    // ========================================================================== image

    /// <remarks>
    /// Cached hard and revalidated by ETag. The bytes for one model change only when somebody
    /// replaces them, and a catalogue page asks for forty at once.
    /// </remarks>
    private static async Task<IResult> ImageAsync(
        string tac,
        HttpContext http,
        IDeviceImageStore images,
        IDeviceAnalyticsStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);

        var model = await ResolveModelAsync(tac, store, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound();
        }

        var image = await images.GetAsync(model.Key, ct).ConfigureAwait(false);

        if (image is null)
        {
            // 404 and not a placeholder image: the client draws its own, which stays crisp at any
            // size and follows the theme. Sending a grey rectangle over the wire would do neither.
            return Results.NotFound();
        }

        if (http.Request.Headers.IfNoneMatch.Count > 0
            && http.Request.Headers.IfNoneMatch.ToString().Contains(image.ETag, StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        http.Response.Headers.ETag = image.ETag;
        http.Response.Headers.CacheControl = "private, max-age=86400, must-revalidate";

        // The stored content type was derived from the file's own magic bytes on upload, not from
        // what the uploader claimed. nosniff makes sure the browser does not second-guess that.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";

        return Results.Bytes(image.Bytes, image.ContentType);
    }

    private static async Task<IResult> UploadImageAsync(
        string tac,
        IFormFile file,
        HttpContext http,
        IDeviceImageStore images,
        IDeviceAnalyticsStore store,
        IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var user = CurrentUser.Require(http);

        if (!user.Can(Permissions.DeviceImageManage))
        {
            await audit.WriteAsync(Entry(
                user, http, Permissions.DeviceImageManage, AuditOutcome.Denied, "device",
                new Dictionary<string, object?> { ["tac"] = tac }), ct).ConfigureAwait(false);

            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not permitted",
                detail: "Managing device images needs the \"device.image.manage\" permission.");
        }

        if (file.Length <= 0 || file.Length > MaxImageBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["file"] = [$"An image must be between 1 byte and {MaxImageBytes / 1024} KB."],
            });
        }

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        // The content type comes from the bytes, never from the upload's Content-Type header or
        // its file name. This endpoint stores something the server will later hand back with that
        // type on it, and trusting the client's word about it is how an image upload becomes a
        // way to serve arbitrary content from your own origin.
        var contentType = SniffImageType(bytes);

        if (contentType is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["file"] = ["That file is not a PNG, JPEG or WebP image."],
            });
        }

        var note = http.Request.Form.TryGetValue("sourceNote", out var supplied)
            ? supplied.ToString().Trim()
            : string.Empty;

        // The picture is stored against the MODEL, so this one upload covers every TAC of it -
        // all 18 of a Redmi Note 12S, all 184 of a Galaxy A12. A TAC the active GSMA version does
        // not name has no model to attach to and is refused rather than silently stored under a
        // key nothing will ever read.
        var model = await ResolveModelAsync(tac, store, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["tac"] = [
                    $"TAC {tac} is not in the active GSMA version, or has no marketing name, so "
                    + "there is no device model for a picture to belong to."],
            });
        }

        await images.SaveAsync(
            model.Key, model.Brand, model.Name, contentType, bytes, note, user.UserId, ct)
            .ConfigureAwait(false);

        await audit.WriteAsync(Entry(
            user, http, Permissions.DeviceImageManage, AuditOutcome.Success, "device",
            new Dictionary<string, object?>
            {
                ["tac"] = tac,
                ["model"] = model.Key,
                ["contentType"] = contentType,
                ["bytes"] = bytes.Length,
            }), ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            tac,
            model = $"{model.Brand} {model.Name}",
            contentType,
            bytes = bytes.Length,
        });
    }

    private static async Task<IResult> DeleteImageAsync(
        string tac,
        HttpContext http,
        IDeviceImageStore images,
        IDeviceAnalyticsStore store,
        IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);

        var user = CurrentUser.Require(http);

        if (!user.Can(Permissions.DeviceImageManage))
        {
            await audit.WriteAsync(Entry(
                user, http, Permissions.DeviceImageManage, AuditOutcome.Denied, "device",
                new Dictionary<string, object?> { ["tac"] = tac }), ct).ConfigureAwait(false);

            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not permitted",
                detail: "Managing device images needs the \"device.image.manage\" permission.");
        }

        var model = await ResolveModelAsync(tac, store, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound();
        }

        var removed = await images.DeleteAsync(model.Key, ct).ConfigureAwait(false);

        if (removed)
        {
            // Recorded against the model, not the TAC, because that is the scope of what was
            // deleted: this removes the picture from every TAC of the model at once.
            await audit.WriteAsync(Entry(
                user, http, Permissions.DeviceImageManage, AuditOutcome.Success, "device",
                new Dictionary<string, object?>
                {
                    ["tac"] = tac,
                    ["model"] = model.Key,
                    ["action"] = "delete",
                }), ct).ConfigureAwait(false);
        }

        return removed ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>The model a TAC belongs to, or null when the GSMA version does not name one.</summary>
    private static async Task<ResolvedModel?> ResolveModelAsync(
        string tac, IDeviceAnalyticsStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var identity = await store.GetModelIdentityAsync(tac, ct).ConfigureAwait(false);

        if (identity is null)
        {
            return null;
        }

        var key = DeviceModelKey.For(
            identity.Brand, identity.Manufacturer, identity.MarketingName);

        if (key is null)
        {
            return null;
        }

        return new ResolvedModel(
            key,
            DeviceModelKey.DisplayBrand(identity.Brand, identity.Manufacturer) ?? string.Empty,
            DeviceModelKey.DisplayName(identity.MarketingName) ?? string.Empty);
    }

    /// <summary>A TAC resolved to the model its photograph belongs to.</summary>
    private sealed record ResolvedModel(string Key, string Brand, string Name);

    // ======================================================================= plumbing

    /// <summary>
    /// Identifies an image from its own first bytes.
    /// </summary>
    /// <remarks>
    /// Three formats, three signatures. WebP needs both ends of its header checked: <c>RIFF</c>
    /// alone also starts a WAV file, and the format marker is four bytes further in.
    /// </remarks>
    private static string? SniffImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private static DeviceSummary ToSummary(DeviceRow row, bool hasImage) => new(
        Tac: row.Tac,
        Manufacturer: row.Manufacturer,
        Vendor: row.Vendor,
        Brand: row.Brand,
        Model: row.Model,
        MarketingName: row.MarketingName,
        DeviceType: row.DeviceType,
        OperatingSystem: row.OperatingSystem,
        Bindings: row.Bindings,
        Handsets: row.Handsets,
        Sims: row.Sims,
        Subscribers: row.Subscribers,
        FirstSeen: row.FirstSeen,
        LastSeen: row.LastSeen,
        HasImage: hasImage);

    /// <summary>
    /// Maps the wire enum to the application one.
    /// </summary>
    /// <remarks>
    /// The two are deliberately separate types, and this switch is the boundary where an
    /// unrecognised value becomes the default rather than reaching a query.
    /// </remarks>
    private static DeviceListSort MapSort(DeviceSort sort) => sort switch
    {
        DeviceSort.Handsets => DeviceListSort.Handsets,
        DeviceSort.Sims => DeviceListSort.Sims,
        DeviceSort.Subscribers => DeviceListSort.Subscribers,
        DeviceSort.Model => DeviceListSort.Model,
        DeviceSort.Manufacturer => DeviceListSort.Manufacturer,
        DeviceSort.Tac => DeviceListSort.Tac,
        DeviceSort.LastSeen => DeviceListSort.LastSeen,
        _ => DeviceListSort.Bindings,
    };

    private static AuditEntry Entry(
        AuthenticatedUser user, HttpContext http, string action, AuditOutcome outcome, string targetType,
        Dictionary<string, object?> detail) =>
        new(
            ActorName: user.Username,
            Action: action,
            Category: AuditCategory.Data,
            Outcome: outcome,
            ActorUserId: user.UserId,
            TargetType: targetType,
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: detail);

    private static string Digits(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}

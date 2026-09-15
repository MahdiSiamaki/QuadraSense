using System.Collections.Frozen;

namespace Sqm.Application.Identity;

/// <summary>
/// Every capability the system can require, as compile-time constants.
/// </summary>
/// <remarks>
/// <para>
/// The authoritative catalogue is the <c>auth.permission</c> table: that is what an administrator
/// grants, what the permission matrix renders, and what a future migration extends. This class
/// exists because an endpoint has to name a permission at compile time, and a string literal
/// repeated across twenty endpoints is a typo waiting to become an unprotected route.
/// </para>
/// <para>
/// The two must not drift, so a test asserts both directions against the real database:
/// every constant here has a row, and every row has a constant. A permission that exists only in
/// the database cannot be required by any code; one that exists only in code can never be granted
/// to anyone. Both failures are silent, and both are the kind that are found in production.
/// </para>
/// </remarks>
public static class Permissions
{
    // ---------------------------------------------------------------- analytics
    /// <summary>See aggregate device population figures and their breakdowns.</summary>
    public const string DashboardView = "dashboard.view";

    /// <summary>Resolve one MSISDN to the SIMs and handsets bound to it.</summary>
    /// <remarks>
    /// Deliberately separate from <see cref="DashboardView"/>. Aggregate analytics needs no
    /// ability to identify an individual, and this is the permission that exposes raw
    /// identifiers for a named person.
    /// </remarks>
    public const string LookupSubscriber = "lookup.subscriber";

    /// <summary>Download query results as a file.</summary>
    public const string DataExport = "data.export";

    /// <summary>Find the numbers and handsets bound to a SIM, by IMSI or IMSI prefix.</summary>
    /// <remarks>
    /// Separate from <see cref="LookupSubscriber"/> because the two answer different questions for
    /// different people: "which handset is this SIM in" is SIM operations, "which SIM does this
    /// person have" is subscriber support. Neither implies the other.
    /// </remarks>
    public const string LookupImsi = "lookup.imsi";

    /// <summary>Open the Devices section: the device-model catalogue and its population figures.</summary>
    /// <remarks>
    /// Not dangerous, and granted to every role including Viewer. Everything behind it is a
    /// statement about a device model - manufacturer, capabilities, how many are on the network -
    /// and none of it names anybody. A reader who can see "Samsung 43.2%" on the dashboard and
    /// cannot open Samsung has been given a chart and denied the thing it is about.
    /// </remarks>
    public const string DeviceView = "device.view";

    /// <summary>Resolve one handset to the SIMs and numbers bound to it.</summary>
    /// <remarks>
    /// The counterpart of <see cref="LookupImsi"/> one layer down: that answers "which handsets
    /// has this SIM been in", this answers "which SIMs have been in this handset".
    /// </remarks>
    public const string LookupImei = "lookup.imei";

    /// <summary>Page through every IMEI, IMSI and MSISDN bound to a device model.</summary>
    /// <remarks>
    /// <b>Bulk exposure, and deliberately not folded into <see cref="LookupImei"/>.</b> Resolving
    /// one IMEI exposes one person's handset; listing a model's identifiers exposes everybody who
    /// owns that model, and the most populous TAC here covers 208,895 handsets. Those are
    /// different acts even though they read the same table. Audited on every call, with the count
    /// of rows returned.
    /// </remarks>
    public const string DeviceIdentifiers = "device.identifiers";

    /// <summary>Upload or replace the photograph shown on a device page.</summary>
    /// <remarks>
    /// Device imagery is curated in this system: the GSMA TAC record contains none, and the
    /// deployment has no internet access to fetch any. See ADR-009.
    /// </remarks>
    public const string DeviceImageManage = "device.image.manage";

    /// <summary>See MSISDN, IMSI and IMEI in full rather than masked.</summary>
    /// <remarks>
    /// Enforced on the server: a caller without this receives the masked string, and the complete
    /// value is never in the response. Masking in the browser would leave the raw value one
    /// developer-tools panel away, and in every proxy log on the way there.
    /// </remarks>
    public const string IdentifierReveal = "identifier.reveal";

    // ------------------------------------------------------------------ imports
    /// <summary>Open the Import Center and read job history.</summary>
    public const string ImportView = "import.view";

    /// <summary>Submit a daily subscriber change file.</summary>
    public const string ImportUploadSqm = "import.upload.sqm";

    /// <summary>Submit a GSMA TAC snapshot. Does not activate it.</summary>
    public const string ImportUploadTac = "import.upload.tac";

    /// <summary>Re-run an import from the stored original file.</summary>
    public const string ImportReprocess = "import.reprocess";

    /// <summary>Request cancellation of a queued or running job.</summary>
    public const string ImportCancel = "import.cancel";

    /// <summary>Remove an import and the analytics rows it produced.</summary>
    public const string ImportDelete = "import.delete";

    /// <summary>Promote an uploaded TAC snapshot to ACTIVE.</summary>
    public const string TacActivate = "tac.activate";

    /// <summary>Return to a previously active TAC snapshot.</summary>
    public const string TacRollback = "tac.rollback";

    // ----------------------------------------------------------- administration
    /// <summary>See the user list and a user's effective permissions.</summary>
    public const string UserView = "user.view";

    /// <summary>Create, edit, activate and deactivate users; assign roles and permissions.</summary>
    public const string UserManage = "user.manage";

    /// <summary>See roles, their permissions and their members.</summary>
    public const string RoleView = "role.view";

    /// <summary>Create and edit roles and change which permissions they carry.</summary>
    public const string RoleManage = "role.manage";

    /// <summary>Read the append-only audit log.</summary>
    public const string AuditView = "audit.view";

    /// <summary>Operational settings not covered by a narrower permission.</summary>
    public const string SystemAdmin = "system.admin";

    /// <summary>Every permission code known to this build.</summary>
    public static readonly FrozenSet<string> All = new[]
    {
        DashboardView, LookupSubscriber, DataExport, LookupImsi, IdentifierReveal,
        DeviceView, LookupImei, DeviceIdentifiers, DeviceImageManage,
        ImportView, ImportUploadSqm, ImportUploadTac, ImportReprocess, ImportCancel, ImportDelete,
        TacActivate, TacRollback,
        UserView, UserManage, RoleView, RoleManage, AuditView, SystemAdmin,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The permissions that, between them, keep the system administrable.
    /// </summary>
    /// <remarks>
    /// A system whose last administrator has just removed their own <c>user.manage</c> is
    /// unrecoverable without database access. Every mutation to users, roles or grants checks
    /// that at least one active user still holds each of these, inside the same transaction, and
    /// rolls back if not. Cheap to check, impossible to undo from the UI once it has happened.
    /// </remarks>
    public static readonly IReadOnlyList<string> AdministrationCritical = [UserManage, RoleManage];
}

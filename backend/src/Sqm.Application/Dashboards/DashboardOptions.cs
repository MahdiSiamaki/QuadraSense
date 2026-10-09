namespace Sqm.Application.Dashboards;

/// <summary>The dashboard builder's limits, from the <c>Dashboards</c> configuration section.</summary>
/// <remarks>
/// The database holds a 256 KiB ceiling on a definition as a backstop; these are the limits a person
/// meets, and the client is told them (<c>GET /api/v1/dashboards</c>) so it can say them first.
/// </remarks>
public sealed class DashboardOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Dashboards";

    /// <summary>Widgets on one dashboard.</summary>
    public int MaxWidgets { get; set; } = 24;

    /// <summary>A dashboard's name, after trimming.</summary>
    public int MaxNameLength { get; set; } = 100;

    /// <summary>A dashboard's description.</summary>
    public int MaxDescriptionLength { get; set; } = 1000;

    /// <summary>A widget's own title.</summary>
    public int MaxTitleLength { get; set; } = 120;

    /// <summary>A widget's own subtitle.</summary>
    public int MaxSubtitleLength { get; set; } = 200;

    /// <summary>One widget's config, serialised. An aggregate Explorer query is the largest.</summary>
    public int MaxWidgetConfigBytes { get; set; } = 8192;

    /// <summary>The whole definition, serialised.</summary>
    public int MaxDefinitionBytes { get; set; } = 131072;

    /// <summary>Grants on one dashboard.</summary>
    public int MaxGrants { get; set; } = 100;

    /// <summary>Users, and separately roles, a share-picker search returns.</summary>
    public int ShareDirectoryLimit { get; set; } = 20;

    /// <summary>Characters a share-picker search needs before it searches users.</summary>
    public int ShareDirectoryMinQuery { get; set; } = 2;

    /// <summary>Widgets one CSV or snapshot export may include.</summary>
    public int MaxExportWidgets { get; set; } = 24;
}

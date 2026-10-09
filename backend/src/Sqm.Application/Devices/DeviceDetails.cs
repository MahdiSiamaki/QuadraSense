using Sqm.Application.Abstractions;
using Sqm.Contracts.Devices;
using Sqm.Domain.Devices;

namespace Sqm.Application.Devices;

/// <summary>
/// Everything known about one device model, as the device page and the dashboard export return it.
/// </summary>
/// <remarks>
/// One composition for both callers: the percentage changes below carry a decision (a model absent at
/// the first delivery has none), and an export that recomputed them would be free to forget it.
/// </remarks>
public static class DeviceDetails
{
    /// <summary>One model's detail, or null when no delivery names the TAC.</summary>
    /// <param name="store">The analytics store.</param>
    /// <param name="images">The curated photographs, for whether the model has one.</param>
    /// <param name="tac">An eight-digit TAC.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<DeviceDetail?> LoadAsync(
        IDeviceAnalyticsStore store, IDeviceImageStore images, string tac, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(images);

        var row = await store.GetDeviceAsync(tac, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
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

        return new DeviceDetail(
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
            TacVersionId: row.TacVersionId);
    }
}

using System.Threading.Channels;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity;

/// <summary>A bounded queue in memory. See <see cref="IAccountOutbox"/>.</summary>
/// <remarks>
/// Bounded, and dropping when full, because anyone can add to it without an account. The limiter
/// on the endpoint keeps it from filling; the bound is what makes that a fact and not a hope.
/// </remarks>
public sealed class AccountOutbox : IAccountOutbox
{
    private const int Capacity = 512;

    private readonly Channel<AccountWork> _queue = Channel.CreateBounded<AccountWork>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    /// <summary>What the background service reads.</summary>
    public ChannelReader<AccountWork> Reader => _queue.Reader;

    /// <inheritdoc />
    public bool TryEnqueue(AccountWork work) => _queue.Writer.TryWrite(work);
}

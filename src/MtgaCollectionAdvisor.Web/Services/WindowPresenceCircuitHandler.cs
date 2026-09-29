using Microsoft.AspNetCore.Components.Server.Circuits;
using MtgaCollectionAdvisor.Core.Hosting;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// Tells <see cref="WindowPresence"/> when a window attaches and detaches. Connections, not
/// circuits: a closed window's circuit is kept for hours in case it reconnects (#99), so
/// circuit-closed fires far too late to mean "the window is gone".
/// </summary>
public sealed class WindowPresenceCircuitHandler(WindowPresence presence) : CircuitHandler
{
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        presence.Connected();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        presence.Disconnected(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}

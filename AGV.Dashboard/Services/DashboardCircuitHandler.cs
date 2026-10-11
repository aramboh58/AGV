using Microsoft.AspNetCore.Components.Server.Circuits;

namespace AGV.Dashboard.Services
{
    /// <summary>
    /// Per-circuit scoped registry that lets a component register a
    /// cleanup action to run when THIS circuit's connection goes down —
    /// even on an abrupt disconnect (hard refresh, tab close) where the
    /// component's own DisposeAsync is not reliably called.
    ///
    /// Added 9/10 diagnostic session: confirmed via instance-ID logging
    /// (see ToDo14.md #11) that Dashboard.razor's DisposeAsync does NOT
    /// fire on a hard refresh — the old component instance stays
    /// permanently subscribed to DashboardBroadcaster's events, with no
    /// way to ever deliver anything to its now-dead circuit, silently
    /// consuming ThreadPool/Task capacity forever. This registry, paired
    /// with DashboardCircuitHandler below, provides a reliable
    /// alternative cleanup hook at the SignalR circuit level instead of
    /// the component lifecycle level.
    ///
    /// Registered as Scoped in Program.cs — Blazor Server creates one DI
    /// scope per circuit, so a given circuit's Dashboard component and
    /// its DashboardCircuitHandler instance automatically share the same
    /// CircuitCleanupRegistry instance.
    /// </summary>
    public sealed class CircuitCleanupRegistry
    {
        private event Action? CircuitEnding;
        private int _hasRun; // 0 = not yet run, 1 = already run — guards against double-invoke

        /// <summary>
        /// Register an action to run when this circuit's connection
        /// goes down. Safe to call multiple cleanup actions; safe for
        /// the registered action itself to also be idempotent (e.g.
        /// event -= unsubscription is already a no-op if already
        /// removed), since this registry also independently guards
        /// against running the combined callback more than once.
        /// </summary>
        public void RegisterCleanup(Action cleanup)
        {
            CircuitEnding += cleanup;
        }

        /// <summary>
        /// Run all registered cleanup actions for this circuit, exactly
        /// once. Called from DashboardCircuitHandler on connection-down
        /// and circuit-closed events.
        /// </summary>
        public void RunCleanup()
        {
            if (Interlocked.Exchange(ref _hasRun, 1) != 0)
                return; // already ran — avoid double cleanup if both
                        // OnConnectionDownAsync and OnCircuitClosedAsync
                        // fire for the same circuit

            CircuitEnding?.Invoke();
        }
    }

    /// <summary>
    /// Circuit-level lifecycle hook, registered as Scoped alongside
    /// CircuitCleanupRegistry (same DI scope = same circuit).
    ///
    /// IMPORTANT (9/11 diagnostic session): this ONLY hooks
    /// OnCircuitClosedAsync — do NOT add OnConnectionDownAsync back.
    /// An earlier version of this class also hooked
    /// OnConnectionDownAsync, reasoning that it fires promptly on any
    /// disconnect (vs. waiting for the ~3 min disconnected-circuit
    /// retention period). That was WRONG and caused a real regression,
    /// confirmed via server log: OnConnectionDownAsync fires on EVERY
    /// disconnect, including a brief, ordinary network blip that
    /// Blazor Server successfully RESUMES a few seconds later — same
    /// circuit, same component instance, no new OnInitializedAsync.
    /// The earlier code unsubscribed that still-alive, about-to-be-
    /// resumed instance from all of DashboardBroadcaster's events the
    /// moment the connection blipped, so when the circuit came back
    /// online, it was already permanently deaf to future broadcasts —
    /// producing a dashboard with perfectly working clicks (direct
    /// calls, no subscription needed) but a frozen map/sim-clock
    /// (dependent on the now-severed subscriptions). See ToDo15/16.md
    /// #11 for the full incident.
    ///
    /// OnCircuitClosedAsync is the correct hook: it is Blazor Server's
    /// own documented signal that this circuit will NEVER be reused
    /// again — either the disconnected-circuit retention period
    /// (default ~3 min) expired without a successful resume, or the
    /// circuit was explicitly torn down. This does mean an orphaned
    /// circuit from a hard refresh takes up to ~3 minutes to be
    /// cleaned up rather than immediately — that bounded delay is the
    /// correct tradeoff versus breaking every ordinary resumable
    /// reconnect, which is what the more "eager" hook did.
    /// </summary>
    public sealed class DashboardCircuitHandler : CircuitHandler
    {
        private readonly CircuitCleanupRegistry _registry;

        public DashboardCircuitHandler(CircuitCleanupRegistry registry)
        {
            _registry = registry;
        }

        public override Task OnCircuitClosedAsync(
            Circuit circuit, CancellationToken cancellationToken)
        {
            _registry.RunCleanup();
            return Task.CompletedTask;
        }
    }
}

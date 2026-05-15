using AGV.Core.Interfaces;
using AGV.Core.Messages;

namespace AGV.Host
{
    /// <summary>
    /// Default pass-through ICustomizationApi implementation.
    /// Used in Phase 1 when no site-specific customization is needed.
    ///
    /// All hooks return neutral values:
    ///   — OnMissionCreated: returns context unchanged
    ///   — OnVehicleArrivedAtPickup: always allows pickup
    ///   — OnSwapCandidateDetected: always approves swap
    ///   — All other hooks: no-op
    ///
    /// Replace with a site-specific implementation
    /// (e.g. NYTCustomization) by changing the DI registration
    /// in Program.cs — no other code changes required.
    /// </summary>
    public sealed class DefaultCustomizationApi : ICustomizationApi
    {
        // All methods use the default interface implementations
        // defined in ICustomizationApi — no overrides needed here.
        // This class exists purely as a concrete DI registration target.
    }
}
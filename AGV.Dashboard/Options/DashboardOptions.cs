namespace AGV.Dashboard.Options
{
    /// <summary>
    /// Configuration options for the Blazor fleet dashboard.
    /// Static display settings only — live data overlays are
    /// pushed via SignalR, not configured here.
    /// </summary>
    public sealed class DashboardOptions
    {
        public const string SectionName = "DashboardOptions";

        public MapOptions Map { get; set; } = new();
        public OverlayOptions Overlays { get; set; } = new();
    }

    public sealed class MapOptions
    {
        /// <summary>
        /// MoveIds that represent cross-corridor connections.
        /// These edges render with a brighter style on the floor map.
        /// </summary>
        public List<int> CrossCorridorMoveIds { get; set; } = new();

        /// <summary>
        /// Zone label overlays anchored to specific nodes.
        /// </summary>
        public List<ZoneLabelOptions> ZoneLabels { get; set; } = new();
    }

    public sealed class ZoneLabelOptions
    {
        public string Label { get; set; } = string.Empty;
        public int AnchorNodeId { get; set; }
    }

    public sealed class OverlayOptions
    {
        // Placeholder for future dynamic overlay configuration.
        // Equipment, inventory, and floor callouts will be added here.
    }
}
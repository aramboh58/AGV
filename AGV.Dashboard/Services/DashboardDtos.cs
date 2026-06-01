namespace AGV.Dashboard.Services
{
    /// <summary>Real-time vehicle position update pushed to browsers.</summary>
    public sealed record VehiclePositionDto(
        int VehicleId,
        string SerialNumber,
        decimal X,
        decimal Y,
        string NodeId);

    /// <summary>Vehicle state update pushed to browsers.</summary>
    public sealed record VehicleStateDto(
        int VehicleId,
        string SerialNumber,
        string ActivityState,
        decimal SocPercent,
        bool IsCharging,
        bool IsLoaded,
        string CurrentOrderId);

    /// <summary>Mission counter snapshot pushed to browsers.</summary>
    public sealed record MissionCounterDto(
        int Enqueued,
        int Dispatched,
        int Completed);

    /// <summary>Simulation clock update pushed to browsers.</summary>
    public sealed record SimClockDto(
        string SimTime,       // e.g. "01:23:45"
        decimal SpeedFactor,
        int TickCount);
}

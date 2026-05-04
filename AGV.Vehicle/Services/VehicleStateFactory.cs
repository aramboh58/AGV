using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Core.Messages;

// Explicit alias to avoid collision with AGV.Vehicle namespace
using VehicleEntity = AGV.Core.Entities.Vehicle;
using VehicleFactSheetEntity = AGV.Core.Entities.VehicleFactSheet;

namespace AGV.Vehicle.Services
{
    public static class VehicleStateFactory
    {
        public static VehicleStateUpdate BuildStateUpdate(
            VehicleEntity vehicle,
            VehicleStateMachine stateMachine,
            BatteryModel battery,
            IReadOnlyList<string>? errors = null)
        {
            return new VehicleStateUpdate
            {
                VehicleId = vehicle.VehicleId,
                SerialNumber = vehicle.SerialNumber,
                ActivityState = stateMachine.Activity,
                OrderState = stateMachine.OrderState,
                OperatingMode = stateMachine.OperatingMode,
                BatteryStateOfCharge = battery.StateOfChargePercent,
                IsCharging = battery.IsCharging,
                IsLoaded = stateMachine.IsLoaded,
                CurrentOrderId = string.Empty,
                LastNodeId = vehicle.CurrentNodeId ?? 0,
                OrderUpdateId = 0,
                Errors = errors ?? Array.Empty<string>(),
                ReceivedAt = DateTime.UtcNow,
            };
        }

        public static VehiclePositionUpdate BuildPositionUpdate(
            VehicleEntity vehicle,
            decimal x,
            decimal y,
            decimal headingDegrees = 0m)
        {
            return new VehiclePositionUpdate
            {
                VehicleId = vehicle.VehicleId,
                SerialNumber = vehicle.SerialNumber,
                NodeId = vehicle.CurrentNodeId ?? 0,
                MapId = vehicle.CurrentMapId,
                X = x,
                Y = y,
                ReceivedAt = DateTime.UtcNow,
            };
        }

        public static VehicleConnectionEvent BuildConnectionEvent(
            VehicleEntity vehicle,
            bool isOnline)
        {
            return new VehicleConnectionEvent
            {
                SerialNumber = vehicle.SerialNumber,
                IsOnline = isOnline,
                EventAt = DateTime.UtcNow,
            };
        }

        public static VehicleFactSheetEvent BuildFactSheetEvent(
            VehicleEntity vehicle,
            VehicleFactSheetEntity factSheet)
        {
            return new VehicleFactSheetEvent
            {
                SerialNumber = vehicle.SerialNumber,
                MaxOrderHorizonDepth = factSheet.MaxOrderHorizonDepth,
                SupportsNurbsTrajectory = factSheet.SupportsNurbsTrajectory,
                SupportedActionTypes = factSheet.SupportedActionTypes,
                MaxSpeedMs = factSheet.MaxSpeedMs,
                MaxPayloadKg = factSheet.MaxPayloadKg,
                LengthMeters = factSheet.LengthMeters,
                WidthMeters = factSheet.WidthMeters,
                ReceivedAt = DateTime.UtcNow,
            };
        }
    }
}

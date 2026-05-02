using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// Represents the current operational activity of a vehicle.
    /// Maps to VDA 5050 actionStates.
    /// </summary>
    public enum ActivityState
    {
        Idle = 0,
        TravelingToPickup = 1,
        ApproachingStand = 2,
        Picking = 3,
        TravelingLoaded = 4,
        ApproachingDrop = 5,
        Dropping = 6,
        TravelingEmpty = 7,
        QueuedForCharge = 8,
        OpportunityCharging = 9,
        TravelingToMandatoryCharge = 10,
        MandatoryCharging = 11,
        TravelingToMaintenance = 12,
        MaintenanceDrain = 13,
        MaintenanceCharge = 14,
        OutOfService = 15
    }
}
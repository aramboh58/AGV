using AGV.Core.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    public record AlertUpdate(
        AlertType Type,
        int VehicleId,
        string Message,
        DateTime Timestamp);
}

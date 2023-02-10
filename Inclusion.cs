using System.Collections.Generic;

namespace hass2mqtt
{
    public class Inclusion
    {
        public HashSet<string>? Entities { get; set; }

        public bool EntityIncluded(string entityId)
        {
            if (Entities is not null && Entities.Count > 0)
            {
                return Entities.Contains(entityId);
            }
            // Every entity if no explicit inclusions.
            return true;
        }
    }
}

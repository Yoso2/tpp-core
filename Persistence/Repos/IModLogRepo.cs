using System.Threading.Tasks;
using NodaTime;
using Persistence.Models;

namespace Persistence.Repos
{
    public interface IModLogRepo
    {
        Task<ModLog> LogModAction(User user, string reason, string rule, Instant timestamp);
        Task<long> CountRecentBans(User user, Instant cutoff);
    }
}

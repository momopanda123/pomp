using System.Threading;
using System.Threading.Tasks;
namespace Pomp.Features
{
    /// <summary>Optional native presentation of observed engine states, not task execution or verification.</summary>
    public interface IBackgroundTaskNotifications
    {
        Task UpdateBackgroundTasksAsync(string summary, string? notice, CancellationToken cancellationToken);
    }
}

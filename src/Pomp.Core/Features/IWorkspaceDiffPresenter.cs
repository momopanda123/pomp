using System.Threading;
using System.Threading.Tasks;
namespace Pomp.Features
{
    /// <summary>Shows immutable review text in a native viewer without opening or changing source files.</summary>
    public interface IWorkspaceDiffPresenter
    {
        Task ShowWorkspaceDiffAsync(string path, string before, string after, long revision, CancellationToken cancellationToken);
    }
}

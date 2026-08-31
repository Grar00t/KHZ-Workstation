using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Repositories;

internal interface IRepositoryInspector
{
    Task<RepositorySnapshot> InspectAsync(
        string directory,
        CancellationToken cancellationToken = default);
}

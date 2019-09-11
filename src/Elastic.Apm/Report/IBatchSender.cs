using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Elastic.Apm.Report
{
	public interface IBatchSender
	{
		Task SendBatchAsync(IEnumerable<object> batch, CancellationToken cancellationToken);
	}
}

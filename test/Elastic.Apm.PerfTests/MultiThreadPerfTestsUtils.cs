using System;
using System.Collections.Generic;
using System.Linq;

namespace Elastic.Apm.PerfTests
{
	internal static class MultiThreadPerfTestsUtils
	{
		internal static IEnumerable<int> NumberOfThreadsVariants() =>
			new[]
			{
				Environment.ProcessorCount - 2,
				Environment.ProcessorCount / 2,
				Environment.ProcessorCount * 2,
				2,
				1
			}.Distinct();

	}
}

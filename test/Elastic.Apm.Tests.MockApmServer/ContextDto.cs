using System.Collections.Generic;
using Elastic.Apm.Api;

namespace Elastic.Apm.Tests.MockApmServer
{
	internal class ContextDto
	{
		public Request Request { get; set; }
		public Response Response { get; set; }
		public Dictionary<string, string> Tags { get; set; }
		public User User { get; set; }
	}
}
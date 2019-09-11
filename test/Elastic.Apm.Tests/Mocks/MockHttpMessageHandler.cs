using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Elastic.Apm.Tests.Mocks {
	public class MockHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

		public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) =>
			_sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));

		public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) =>
			_sendAsync = (request, _) => sendAsync(request) ?? throw new ArgumentNullException(nameof(sendAsync));

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			_sendAsync(request, cancellationToken);
	}
}

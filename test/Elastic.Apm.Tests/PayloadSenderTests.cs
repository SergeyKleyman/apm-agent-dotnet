using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Elastic.Apm.Api;
using Elastic.Apm.Config;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Metrics;
using Elastic.Apm.Model;
using Elastic.Apm.Report;
using Elastic.Apm.Tests.Mocks;
using Elastic.Apm.Tests.TestHelpers;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using static Elastic.Apm.Tests.TestHelpers.FluentAssertionsUtils;
// ReSharper disable ImplicitlyCapturedClosure

namespace Elastic.Apm.Tests
{
	public class PayloadSenderTests : LoggingTestBase
	{
		public PayloadSenderTests(ITestOutputHelper xUnitOutputHelper) : base(xUnitOutputHelper, nameof(PayloadSenderTests)) { }

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(ConfigConsts.DefaultValues.MaxBatchEventCount - 1)]
		[InlineData(ConfigConsts.DefaultValues.MaxBatchEventCount)]
		[InlineData(ConfigConsts.DefaultValues.MaxBatchEventCount + 1)]
		[InlineData(ConfigConsts.DefaultValues.MaxQueueEventCount - 1)]
		[InlineData(ConfigConsts.DefaultValues.MaxQueueEventCount)]
		[InlineData(ConfigConsts.DefaultValues.MaxQueueEventCount + 1)]
		public void Dispose_stops_the_thread(int numberOfEventsToEnqueue) =>
			CreateObjectsAndTest((payloadSender, agent) =>
			{
				// ReSharper disable AccessToDisposedClosure
				payloadSender.Thread.IsAlive.Should().BeTrue();
				numberOfEventsToEnqueue.Repeat(() => payloadSender.QueueTransaction(CreateDummyTransaction(agent)));
				payloadSender.Dispose();
				payloadSender.Thread.IsAlive.Should().BeFalse();
				// ReSharper restore AccessToDisposedClosure
			});

		[Fact]
		public void calling_after_Dispose_throws() =>
			CreateObjectsAndTest((payloadSender, agent) =>
			{
				payloadSender.QueueTransaction(CreateDummyTransaction(agent));
				payloadSender.Dispose();
				AsAction(() => payloadSender.QueueTransaction(CreateDummyTransaction(agent)))
					.Should()
					.ThrowExactly<ObjectDisposedException>()
					.WithMessage($"*{nameof(PayloadSenderV2)}*");
			});

		[Theory]
		[InlineData("SecretToken")]
		[InlineData(null)]
		public Task Authorization_request_header_contains_Bearer_token_when_configured(string secretToken)
		{
			var authenticationHeaderTcs = new TaskCompletionSource<AuthenticationHeaderValue>();

			// ReSharper disable once ImplicitlyCapturedClosure
			var handler = new MockHttpMessageHandler(request =>
			{
				authenticationHeaderTcs.SetResult(request.Headers.Authorization);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			});

			return CreateObjectsAndTest(new TestAgentConfigurationReader(Logger, secretToken: secretToken, flushInterval: "1s"), handler,
				async (payloadSender, agent) =>
				{
					payloadSender.QueueTransaction(new Transaction(agent, "TestName", "TestType"));

					var authHeader = await authenticationHeaderTcs.Task;
					if (secretToken == null)
						authHeader.Should().BeNull();
					else
					{
						authHeader.Should().NotBeNull();
						authHeader.Scheme.Should().Be("Bearer");
						authHeader.Parameter.Should().Be(secretToken);
					}
				});
		}

		[Fact]
		public Task UserAgent_request_header()
		{
			var userAgentHeaderTcs = new TaskCompletionSource<HttpHeaderValueCollection<ProductInfoHeaderValue>>();

			var handler = new MockHttpMessageHandler(request =>
			{
				userAgentHeaderTcs.SetResult(request.Headers.UserAgent);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
			});

			return CreateObjectsAndTest(handler, async (payloadSender, agent) =>
			{
				payloadSender.QueueTransaction(new Transaction(agent, "TestName", "TestType"));

				var userAgentHeader = await userAgentHeaderTcs.Task;
				userAgentHeader
					.Should()
					.NotBeEmpty()
					.And.HaveCount(3);

				userAgentHeader.First().Product.Name.Should().Be($"elasticapm-{Consts.AgentName}");
				userAgentHeader.First().Product.Version.Should().NotBeEmpty();

				userAgentHeader.Skip(1).First().Product.Name.Should().Be("System.Net.Http");
				userAgentHeader.Skip(1).First().Product.Version.Should().NotBeEmpty();

				userAgentHeader.Skip(2).First().Product.Name.Should().NotBeEmpty();
				userAgentHeader.Skip(2).First().Product.Version.Should().NotBeEmpty();
			});
		}

		private static async Task CreateObjectsAndTest(IApmLogger logger, Func<PayloadSenderV2, ApmAgent, Task> test
			, IConfigurationReader configurationReaderArg = null
			, HttpMessageHandler httpMessageHandler = null
		)
		{
			var configurationReader = configurationReaderArg ?? new TestAgentConfigurationReader(logger);
			var system = new Api.System();
			var service = Service.GetDefaultService(configurationReader, logger);
			var batchSender = new BatchSender(logger, configurationReader, service, system, httpMessageHandler);

			using (var payloadSender = new PayloadSenderV2(logger, configurationReader, service, system, batchSender))
			{
				payloadSender.Thread.IsAlive.Should().BeTrue();

				using (var agent = new ApmAgent(new TestAgentComponents(payloadSender: payloadSender)))
					await test(payloadSender, agent);

				payloadSender.Thread.IsAlive.Should().BeFalse();
			}
		}

		private Task CreateObjectsAndTest(IConfigurationReader configurationReader, HttpMessageHandler httpMessageHandler,
			Func<PayloadSenderV2, ApmAgent, Task> test
		) =>
			CreateObjectsAndTest(Logger, test, configurationReader, httpMessageHandler);

		private static void CreateObjectsAndTest(IApmLogger logger, Action<PayloadSenderV2, ApmAgent> test
			, IConfigurationReader configurationReader = null
			, HttpMessageHandler httpMessageHandler = null
		) =>
			CreateObjectsAndTest(logger, (payloadSender, agent) =>
				{
					test(payloadSender, agent);
					return Task.CompletedTask;
				}, configurationReader, httpMessageHandler)
				.Wait();

		private void CreateObjectsAndTest(Action<PayloadSenderV2, ApmAgent> test
			, IConfigurationReader configurationReader = null
			, HttpMessageHandler httpMessageHandler = null
		) =>
			CreateObjectsAndTest(Logger, (payloadSender, agent) =>
				{
					test(payloadSender, agent);
					return Task.CompletedTask;
				}, configurationReader, httpMessageHandler)
				.Wait();

		private Task CreateObjectsAndTest(HttpMessageHandler httpMessageHandler, Func<PayloadSenderV2, ApmAgent, Task> test) =>
			CreateObjectsAndTest(new TestAgentConfigurationReader(Logger, flushInterval: "1s"), httpMessageHandler, test);

		private static Transaction CreateDummyTransaction(ApmAgent agent, long? testEventIndex = null) =>
			new Transaction(agent, testEventIndex?.ToString() ?? "dummy_transaction_name", "dummy_transaction_type");

		private static Span CreateDummySpan(ApmAgent agent, long? testEventIndex = null)
		{
			var enclosingTx = CreateDummyTransaction(agent);

			return new Span(testEventIndex?.ToString() ?? "dummy_span_name", "dummy_span_type", /* parentId */ enclosingTx.Id, enclosingTx.TraceId,
				enclosingTx,
				/* isSampled */ true, agent.PayloadSender, agent.Logger, agent.ConfigurationReader,
				agent.TracerInternal.CurrentExecutionSegmentsContainer);
		}

		private static MetricSet CreateDummyMetricSet(ApmAgent agent, long? testEventIndex = null) =>
			new MetricSet(testEventIndex ?? long.MaxValue,
				new List<MetricSample> { new MetricSample("key_1", 1.0), new MetricSample("key_9", 99.0), new MetricSample("key_A", 123.0) });

		private static Error CreateDummyError(ApmAgent agent, long? testEventIndex = null)
		{
			var enclosingTx = CreateDummyTransaction(agent);

			return new Error(new CapturedException { Message = testEventIndex?.ToString() ?? "Dummy error message" }, enclosingTx, enclosingTx.Id,
				agent.Logger);
		}

		private static void VerifyDummyTransaction(ITransaction tx, long? testEventIndex = null)
		{
			tx.Name.Should().Be(testEventIndex?.ToString() ?? "dummy_transaction_name");
			tx.Type.Should().Be("dummy_transaction_type");
		}

		private static void VerifyDummySpan(ISpan span, long? testEventIndex = null)
		{
			span.Name.Should().Be(testEventIndex?.ToString() ?? "dummy_span_name");
			span.Type.Should().Be("dummy_span_type");
			span.IsSampled.Should().BeTrue();
		}

		private static void VerifyDummyError(Error error, long? testEventIndex = null) =>
			error.Exception.Message.Should().Be(testEventIndex?.ToString() ?? "Dummy error message");

		private static void VerifyDummyMetricSet(IMetricSet metricSet, long? testEventIndex = null)
		{
			metricSet.TimeStamp.Should().Be(testEventIndex ?? long.MaxValue);
			metricSet.Samples.Should().HaveCount(3);
			metricSet.Samples.First().KeyValue.Should().Be(new MetricSample("key_1", 1.0).KeyValue);
			metricSet.Samples.Skip(1).First().KeyValue.Should().Be(new MetricSample("key_9", 99.0).KeyValue);
			metricSet.Samples.Skip(2).First().KeyValue.Should().Be(new MetricSample("key_A", 123.0).KeyValue);
		}

		public class MockedTimeTests : LoggingTestBase
		{
			private readonly Lazy<RandomTestHelper> _randomTestHelper;

			public MockedTimeTests(ITestOutputHelper testOutputHelper) :
				base(testOutputHelper, $"{nameof(PayloadSenderTests)}.{nameof(MockedTimeTests)}") =>
				_randomTestHelper = new Lazy<RandomTestHelper>(() => new RandomTestHelper(1471127204, XunitOutputHelper, LoggerForNonXunitSinks));

			internal Random RandomGenerator => _randomTestHelper.Value.GetInstance();

			private static IEnumerable<FlushIntervalTestArgs> GenFlushIntervalTestArgsVariants()
			{
				var idCounter = 0L;
				var defaultFlushInterval = ConfigConsts.DefaultValues.FlushIntervalInMilliseconds.Milliseconds();
				var flushIntervalVariants = new[]
				{
					TimeSpan.Zero, 10.Milliseconds(), 100.Milliseconds(), defaultFlushInterval, defaultFlushInterval * 10,
					defaultFlushInterval / 10
				};

				foreach (var flushInterval in flushIntervalVariants)
				{
					yield return new FlushIntervalTestArgs
					{
						Id = ++idCounter,
						FlushInterval = flushInterval,
						EventCountToFirstFlush = 1,
						EventCountToSecondFlush = 1,
						MaxBatchEventCount = ConfigConsts.DefaultValues.MaxBatchEventCount
					};
				}

				var maxBatchEventCountVariants = new[]
				{
					1, 2, ConfigConsts.DefaultValues.MaxBatchEventCount, ConfigConsts.DefaultValues.MaxBatchEventCount * 2
				};

				foreach (var flushInterval in new[] { TimeSpan.Zero, defaultFlushInterval })
				{
					foreach (var maxBatchEventCount in maxBatchEventCountVariants)
					{
						foreach (var eventCountToFirstFlush in FirstFlushEventCountVariants(maxBatchEventCount))
						{
							foreach (var eventCountToSecondFlush in SecondFlushEventCountVariants(maxBatchEventCount))
							{
								yield return new FlushIntervalTestArgs
								{
									Id = ++idCounter,
									FlushInterval = flushInterval,
									EventCountToFirstFlush = eventCountToFirstFlush,
									EventCountToSecondFlush = eventCountToSecondFlush,
									MaxBatchEventCount = maxBatchEventCount
								};
							}
						}
					}
				}

				IEnumerable<int> FirstFlushEventCountVariants(int maxBatchEventCount)
				{
					//- 1, 2, 3 elements
					//- batch + 0,1,2,3 elements
					//- 2*batch + 0,1,2,3 elements

					foreach (var baseCount in new[] { 0, maxBatchEventCount, 2 * maxBatchEventCount })
					{
						foreach (var deltaCount in new[] { -2, -1, 0, 1, 2 })
						{
							if (baseCount + deltaCount < 0) continue;

							yield return baseCount + deltaCount;
						}
					}
				}

				IEnumerable<int> SecondFlushEventCountVariants(int maxBatchEventCount)
				{
					foreach (var baseCount in new[] { 0, maxBatchEventCount })
					{
						foreach (var deltaCount in new[] { -1, 0, 1, 2 })
						{
							if (baseCount + deltaCount < 0) continue;

							yield return baseCount + deltaCount;
						}
					}
				}
			}

			[Fact]
			public void FlushInterval_MaxBatchEventCount_tests()
			{
				// Add .Skip(262).Take(1) to run only with args with Id = 263
				// That is: foreach (var args in GenFlushIntervalTestArgsVariants().Skip(262).Take(1))
				foreach (var args in GenFlushIntervalTestArgsVariants().Skip(581).Take(1))
				{
					try
					{
						Logger.Info()?.Log("Starting sub-test... args: {args}", args);
						FlushIntervalMaxBatchEventCountTestImpl(args);
						Logger.Info()?.Log("Successfully completed sub-test. args: {args}", args);
					}
					catch (Exception ex)
					{
						Logger.Error()
							?.LogException(ex, "Sub-test failed. args: {args}. Random seed: {RandomSeed}.", args, _randomTestHelper.Value.Seed);
						throw;
					}
				}
			}

			private void FlushIntervalMaxBatchEventCountTestImpl(FlushIntervalTestArgs args) =>
				SetupSutAndTest(new SutEnvConfig(args.FlushInterval, args.MaxBatchEventCount), sutEnv =>
				{
					sutEnv.MockTimer.FastForward(RandomGenerator.Next().Milliseconds(), "Randomize starting time instant");

					args.EventCountToFirstFlush.Repeat(sutEnv.EnqueueRandomDummyEvent);

					sutEnv.MockTimer.FastForward(args.FlushInterval / 2, "Delimit 1st and 2nd flushes");

					args.EventCountToSecondFlush.Repeat(sutEnv.EnqueueRandomDummyEvent);

					sutEnv.MockTimer.FastForward(args.FlushInterval / 2, "1st flush");

					if (args.FlushInterval == TimeSpan.Zero)
					{
						// Since FlushInterval is 0 each event is sent immediately (i.e., possibly in its own size 1 batch)
						sutEnv.WaitVerifyAllowAnySplitByBatchesClear(sutEnv.Dequeue(args.EventCountToFirstFlush + args.EventCountToSecondFlush));
					}
					else
					{
						var eventCountToSecondFlushRemainder = args.EventCountToSecondFlush;

						if (args.EventCountToFirstFlush != 0)
						{
							if (args.EventCountToFirstFlush + args.EventCountToSecondFlush < args.MaxBatchEventCount)
								sutEnv.WaitVerifyClear(sutEnv.Dequeue(args.EventCountToFirstFlush, args.MaxBatchEventCount));
							else
							{
								eventCountToSecondFlushRemainder =
									(args.EventCountToFirstFlush + args.EventCountToSecondFlush) % args.MaxBatchEventCount;
								sutEnv.WaitVerifyClear(sutEnv.Dequeue(
									args.EventCountToFirstFlush + args.EventCountToSecondFlush - eventCountToSecondFlushRemainder
									, args.MaxBatchEventCount));
							}
						}

						// ReSharper disable once InvertIf
						if (eventCountToSecondFlushRemainder != 0)
						{
							sutEnv.MockTimer.FastForward(args.FlushInterval / 2, "2nd flush");

							sutEnv.WaitVerifyClear(sutEnv.Dequeue(eventCountToSecondFlushRemainder, args.MaxBatchEventCount));
						}
					}
				});

			private void SetupSutAndTest(SutEnvConfig sutEnvConfig, Action<SutEnv> test)
			{
				var configurationReader = new TestAgentConfigurationReader(flushInterval: sutEnvConfig.FlushInterval,
					maxBatchEventCount: sutEnvConfig.MaxBatchEventCount);
				var service = Service.GetDefaultService(configurationReader, Logger);
				var mockTimer = new MockAgentTimer(logger: Logger);
				var mockBatchSender = new MockBatchSender();

				using (var payloadSender = new PayloadSenderV2(Logger, configurationReader, service, new Api.System()
					, mockBatchSender, mockTimer))
				using (var agent = new ApmAgent(new TestAgentComponents(payloadSender: payloadSender)))
					test(new SutEnv(payloadSender, agent, mockTimer, this, mockBatchSender));

				// After payload sender is stopped there should not be any pending delays
				// TODO
//				mockTimer.PendingDelayTasksCount.Should().Be(0);
			}

			public class FlushIntervalTestArgs
			{
				internal int EventCountToFirstFlush { get; set; }
				internal int EventCountToSecondFlush { get; set; }
				internal TimeSpan FlushInterval { get; set; }
				internal long Id;
				internal int MaxBatchEventCount { get; set; }

				public override string ToString() => new ToStringBuilder("")
				{
					{ nameof(Id), Id },
					{ nameof(EventCountToFirstFlush), EventCountToFirstFlush },
					{ nameof(EventCountToSecondFlush), EventCountToSecondFlush },
					{ nameof(FlushInterval), FlushInterval },
					{ nameof(MaxBatchEventCount), MaxBatchEventCount }
				}.ToString();
			}

			internal readonly struct SutEnvConfig
			{
				internal SutEnvConfig(TimeSpan? flushInterval = null, int? maxBatchEventCount = null)
				{
					FlushInterval = flushInterval.HasValue ? flushInterval.Value.TotalMilliseconds + "ms" : null;
					MaxBatchEventCount = maxBatchEventCount.HasValue ? $"{maxBatchEventCount}" : null;
				}

				internal readonly string FlushInterval;
				internal readonly string MaxBatchEventCount;
			}

			internal class SutEnv
			{
				internal readonly ApmAgent Agent;

				internal readonly MockAgentTimer MockTimer;
				internal readonly PayloadSenderV2 PayloadSender;
				private readonly Queue<EventData> _eventsQueuedByTest = new Queue<EventData>();
				private readonly MockBatchSender _mockBatchSender;
				private readonly MockedTimeTests _mockedTimeTests;
				private readonly ThreadSafeLongCounter _testEventIndexCounter = new ThreadSafeLongCounter();
				private readonly IApmLogger _logger;

				internal SutEnv(PayloadSenderV2 payloadSender, ApmAgent agent, MockAgentTimer mockTimer, MockedTimeTests mockedTimeTests
					, MockBatchSender mockBatchSender
				)
				{
					PayloadSender = payloadSender;
					Agent = agent;
					MockTimer = mockTimer;
					_mockedTimeTests = mockedTimeTests;
					_mockBatchSender = mockBatchSender;
					_logger = mockedTimeTests.Logger.Scoped($"{nameof(PayloadSenderTests)}.{nameof(MockedTimeTests)}.{nameof(SutEnv)}");
				}

				internal void EnqueueDummyTransaction() => EnqueueDummyEvent(CreateDummyTransaction, PayloadSender.QueueTransaction);

				internal void EnqueueDummySpan() => EnqueueDummyEvent(CreateDummySpan, PayloadSender.QueueSpan);

				internal void EnqueueDummyError() => EnqueueDummyEvent(CreateDummyError, PayloadSender.QueueError);

				internal void EnqueueDummyMetricSet() => EnqueueDummyEvent(CreateDummyMetricSet, PayloadSender.QueueMetrics);

				internal void EnqueueRandomDummyEvent()
				{
					Thread.CurrentThread.Should().NotBe(PayloadSender.Thread);

					var enqueueEventActions = new[] { (Action)EnqueueDummyTransaction, EnqueueDummySpan, EnqueueDummyError, EnqueueDummyMetricSet };
					var actionIndex = _mockedTimeTests.RandomGenerator.Next(0, enqueueEventActions.Length);
					enqueueEventActions[actionIndex]();
				}

				internal IEnumerable<IEnumerable<object>> Dequeue(int numberOfEventsToDequeue, int maxBatchEventCount)
				{
					Thread.CurrentThread.Should().NotBe(PayloadSender.Thread);

					var result = new List<IEnumerable<object>>();
					for (var remainder = numberOfEventsToDequeue; remainder > 0; remainder -= maxBatchEventCount)
						result.Add(Dequeue(Math.Min(remainder, maxBatchEventCount)));
					return result;
				}

				internal IEnumerable<object> Dequeue(int numberOfEventsToDequeue)
				{
					Thread.CurrentThread.Should().NotBe(PayloadSender.Thread);

					_eventsQueuedByTest.Count.Should().BeGreaterOrEqualTo(numberOfEventsToDequeue);
					var dequeuedEvents = new object[numberOfEventsToDequeue];
					numberOfEventsToDequeue.Repeat(i =>
					{
						var eventData = _eventsQueuedByTest.Dequeue();

						switch (eventData.EventObject)
						{
							case Transaction tx:
								VerifyDummyTransaction(tx, eventData.TestEventIndex);
								break;
							case Span span:
								VerifyDummySpan(span, eventData.TestEventIndex);
								break;
							case Error error:
								VerifyDummyError(error, eventData.TestEventIndex);
								break;
							case MetricSet metricSet:
								VerifyDummyMetricSet(metricSet, eventData.TestEventIndex);
								break;
						}

						dequeuedEvents[i] = eventData.EventObject;
					});
					return dequeuedEvents;
				}

				internal void WaitVerifyAllowAnySplitByBatchesClear(IEnumerable<object> expectedEvents) =>
					WaitVerifyClearImpl(
						// ReSharper disable PossibleMultipleEnumeration
						() =>
						{
							var actualEventsCount = _mockBatchSender.SendCalls.Sum(c => c.Batch.Length);
							if (actualEventsCount < expectedEvents.Count()) return false;

							actualEventsCount.Should().Be(expectedEvents.Count());

							var expectedEventsRemainder = expectedEvents;

							_mockBatchSender.SendCalls.ForEach(sendCall =>
							{
								VerifySendCall(sendCall);
								foreach (var eventObj in sendCall.Batch)
								{
									eventObj.Should().BeSameAs(expectedEventsRemainder.First());
									expectedEventsRemainder = expectedEventsRemainder.Skip(1);
								}
							});

							return true;
						},
						$"events count: {expectedEvents.Count()}"
						// ReSharper restore PossibleMultipleEnumeration
					);

				internal void WaitVerifyClear(IEnumerable<IEnumerable<object>> expectedEventBatches) =>
					WaitVerifyClearImpl(
						// ReSharper disable PossibleMultipleEnumeration
						() =>
						{
							if (_mockBatchSender.SendCalls.Count < expectedEventBatches.Count()) return false;

							_mockBatchSender.SendCalls.Count.Should().Be(expectedEventBatches.Count());

							foreach (var (actualSendCall, expectedEventBatch) in _mockBatchSender.SendCalls.Zip(expectedEventBatches,
								(a, b) => (a, b)))
							{
								VerifySendCall(actualSendCall);
								actualSendCall.Batch.Length.Should().Be(expectedEventBatch.Count());
								StructuralComparisons.StructuralEqualityComparer.Equals(actualSendCall.Batch, expectedEventBatch).Should().BeTrue();
							}

							return true;
						},
						$"batches count: {expectedEventBatches.Count()}, events count: {expectedEventBatches.Sum(b => b.Count())}"
						// ReSharper restore PossibleMultipleEnumeration
					);

				private void VerifySendCall(MockBatchSender.SendArgs sendCall)
				{
					sendCall.IsCancellationRequested.Should().BeFalse();
					sendCall.CurrentThread.Should().Be(PayloadSender.Thread);
				}

				private void WaitVerifyClearImpl(Func<bool> verifyFunc, string dbgExpectedDesc)
				{
					Thread.CurrentThread.Should().NotBe(PayloadSender.Thread);

					try
					{
						WaitVerifyClearLoopImpl(verifyFunc, dbgExpectedDesc);
					}
					catch (Exception ex)
					{
						_logger.Error()?.LogException(ex, nameof(WaitVerifyClear) + " failed");
						throw;
					}
				}

				private void WaitVerifyClearLoopImpl(Func<bool> verifyFunc, string dbgExpectedDesc)
				{
					var maxTotalTimeToWait = 30.Seconds();
					var timeToWaitBetweenChecks = 10.Milliseconds();
					var minTimeBetweenLogs = 1.Second();

					var stopwatch = Stopwatch.StartNew();
					var attemptCount = 0;
					TimeSpan? elapsedOnLastWaitingLog = null;
					while (true)
					{
						++attemptCount;
						if (verifyFunc())
						{
							_logger.Info()?.Log($"Verification succeeded. attemptCount: {attemptCount}", attemptCount);
							_mockBatchSender.Clear();
							break;
						}

						var elapsedTime = stopwatch.Elapsed;
						if (elapsedTime > maxTotalTimeToWait)
						{
							throw new XunitException("Verification failed even after max allotted time to wait."
								+ $" elapsedTime: {elapsedTime}."
								+ $" attemptCount: {attemptCount}."
								+ $" mockBatchSender.SendCalls.Count: {_mockBatchSender.SendCalls.Count}."
								+ $" Expected: {dbgExpectedDesc}.");
						}

						if (!elapsedOnLastWaitingLog.HasValue || elapsedOnLastWaitingLog.Value + minTimeBetweenLogs <= elapsedTime)
						{
							_logger.Debug()
								?.Log("Waiting until next check..."
									+ $" Actual: send calls count: {_mockBatchSender.SendCalls.Count}"
									+ $", events count: {_mockBatchSender.SendCalls.Sum(x => x.Batch.Length)}."
									+ $" Expected: {dbgExpectedDesc}."
									+ $" elapsedTime: {elapsedTime}."
									+ $" attemptCount: {attemptCount}."
									+ $" maxTotalTimeToWait: {maxTotalTimeToWait}."
									+ $" timeToWaitBetweenChecks: {timeToWaitBetweenChecks}.");
							elapsedOnLastWaitingLog = elapsedTime;
						}
						Thread.Sleep(timeToWaitBetweenChecks);
					}
				}

				private void EnqueueDummyEvent<TEvent>(Func<ApmAgent, long?, TEvent> createEvent, Action<TEvent> enqueueEvent)
				{
					var testEventIndex = _testEventIndexCounter.Increment();
					var eventObj = createEvent(Agent, testEventIndex);
					_eventsQueuedByTest.Enqueue(new EventData(eventObj, testEventIndex));
					enqueueEvent(eventObj);
				}

				private readonly struct EventData
				{
					internal EventData(object eventObject, long testEventIndex)
					{
						EventObject = eventObject;
						TestEventIndex = testEventIndex;
					}

					internal readonly object EventObject;
					internal readonly long TestEventIndex;
				}
			}
		}

		public class RealTimeTests : LoggingTestBase
		{
			private static readonly IEnumerable<TimeSpan?> FlushIntervalVariants = new TimeSpan?[]
			{
				null, ConfigConsts.DefaultValues.FlushIntervalInMilliseconds.Milliseconds(), TimeSpan.Zero, 10.Milliseconds(), 100.Milliseconds(),
				1.Seconds(), 1.Hours(), 1.Days()
			};

			private static readonly TimeSpan VeryLongFlushInterval = 1.Hours();
			private static readonly TimeSpan VeryShortFlushInterval = 1.Seconds();

			public RealTimeTests(ITestOutputHelper xUnitOutputHelper) : base(xUnitOutputHelper) { }

			private static IEnumerable<TestArgs> TestArgsVariantsWithVeryLongFlushInterval =>
				TestArgsVariants(args => args.FlushInterval.HasValue && args.FlushInterval >= VeryLongFlushInterval);

			private static IEnumerable<TestArgs> TestArgsVariantsWithoutIndex()
			{
				yield return new TestArgs();

				var maxQueueEventCountVariants = new int?[] { null, 1, 2, 3, 10, ConfigConsts.DefaultValues.MaxQueueEventCount };
				var batchVsQueueCountDeltas = new[] { -2, -1, 0 };

				foreach (var flushInterval in FlushIntervalVariants)
				{
					foreach (var maxQueueEventCount in maxQueueEventCountVariants)
					{
						if (maxQueueEventCount == null) continue;

						foreach (var delta in batchVsQueueCountDeltas)
						{
							var maxBatchEventCount = maxQueueEventCount + delta;
							if (maxBatchEventCount < 1) continue;

							yield return new TestArgs
							{
								FlushInterval = flushInterval, MaxBatchEventCount = maxBatchEventCount, MaxQueueEventCount = maxQueueEventCount
							};
						}
					}
				}
			}

			private static IEnumerable<TestArgs> TestArgsVariants(Func<TestArgs, bool> predicate = null)
			{
				var counter = 0;
				foreach (var argsVariant in TestArgsVariantsWithoutIndex())
				{
					if (predicate != null && !predicate(argsVariant)) continue;

					argsVariant.ArgsIndex = ++counter;
					yield return argsVariant;
				}
			}

			private static bool EnqueueDummyEvent(PayloadSenderV2 payloadSender, ApmAgent agent, int txIndex)
			{
				var tx = new Transaction(agent, $"Tx #{txIndex}", "TestType");
				return payloadSender.EnqueueEvent(tx, tx.Id, "Transaction");
			}

			[Fact]
			internal void MaxQueueEventCount_should_be_enforced_before_send()
			{
				foreach (var args in TestArgsVariantsWithVeryLongFlushInterval)
				{
					Logger.Debug()?.Log("Starting sub-test... args: {args}", args);

					var sendTcs = new TaskCompletionSource<object>();

					var handler = new MockHttpMessageHandler(async (r, c) =>
					{
						await sendTcs.Task;
						return new HttpResponseMessage(HttpStatusCode.OK);
					});

					var configurationReader = args.BuildConfigurationReader(Logger);

					CreateObjectsAndTest((payloadSender, agent) =>
						{
							int? txIndexResumedEnqueuing = null;
							for (var txIndex = 1; txIndex <= args.MaxQueueEventCount + args.MaxBatchEventCount + 10; ++txIndex)
							{
								var enqueuedSuccessfully = EnqueueDummyEvent(payloadSender, agent, txIndex);

								if (txIndex <= args.MaxQueueEventCount)
								{
									enqueuedSuccessfully.Should().BeTrue($"txIndex: {txIndex}, args: {args}");
									continue;
								}

								// It's possible that the events for the first batch have already been dequeued
								// so we can be sure that queue doesn't have any free space left only after MaxQueueEventCount + MaxBatchEventCount events

								if (enqueuedSuccessfully && !txIndexResumedEnqueuing.HasValue) txIndexResumedEnqueuing = txIndex;

								enqueuedSuccessfully.Should()
									.Be(txIndex - txIndexResumedEnqueuing < args.MaxBatchEventCount
										, $"txIndex: {txIndex}, txIndexResumedEnqueuing: {txIndexResumedEnqueuing}, args: {args}");
							}

							sendTcs.SetResult(null);
						}
						, configurationReader, handler);
				}
			}

			[Fact]
			public async Task MaxQueueEventCount_should_be_enforced_after_send()
			{
				foreach (var args in TestArgsVariantsWithVeryLongFlushInterval)
				{
					Logger.Debug()?.Log("Starting sub-test... args: {args}", args);

					var sendTcs = new TaskCompletionSource<object>();
					var firstBatchDequeuedTcs = new TaskCompletionSource<object>();

					var handler = new MockHttpMessageHandler(async (r, c) =>
					{
						firstBatchDequeuedTcs.SetResult(null);
						await sendTcs.Task;
						return new HttpResponseMessage(HttpStatusCode.OK);
					});

					var configurationReader = args.BuildConfigurationReader(Logger);

					await CreateObjectsAndTest(async (payloadSender, agent) =>
						{
							var txIndex = 1;
							for (; txIndex <= args.MaxQueueEventCount; ++txIndex)
								EnqueueDummyEvent(payloadSender, agent, txIndex).Should().BeTrue($"txIndex: {txIndex}, args: {args}");

							await firstBatchDequeuedTcs.Task;

							for (; txIndex <= args.MaxQueueEventCount + args.MaxBatchEventCount + 10; ++txIndex)
							{
								EnqueueDummyEvent(payloadSender, agent, txIndex)
									.Should()
									.Be(txIndex <= args.MaxQueueEventCount + args.MaxBatchEventCount
										, $"txIndex: {txIndex}, args: {args}");
							}

							sendTcs.SetResult(null);
						}
						, configurationReader, handler);
				}
			}

			[Fact]
			public async Task MaxBatchEventCount_test()
			{
				var numberOfBatchesVariants = new[] { 1, 2, 3, 10 };

				foreach (var args in TestArgsVariantsWithVeryLongFlushInterval)
				{
					foreach (var expectedNumberOfBatches in numberOfBatchesVariants)
					{
						Logger.Debug()?.Log("Starting sub-test... args: {args}", args);

						var expectedNumberOfBatchesSentTcs = new TaskCompletionSource<object>();

						var actualNumberOfBatches = 0;
						var handler = new MockHttpMessageHandler((r, c) =>
						{
							if (Interlocked.Increment(ref actualNumberOfBatches) == expectedNumberOfBatches)
								expectedNumberOfBatchesSentTcs.SetResult(null);
							return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
						});

						var configurationReader = args.BuildConfigurationReader(Logger);

						await CreateObjectsAndTest(async (payloadSender, agent) =>
							{
								var numberOfEventsEnqueuedSuccessfully = 0;
								for (var txIndex = 1;; ++txIndex)
								{
									if (EnqueueDummyEvent(payloadSender, agent, txIndex))
										++numberOfEventsEnqueuedSuccessfully;
									else
										Thread.Yield();

									if (numberOfEventsEnqueuedSuccessfully == expectedNumberOfBatches * args.MaxBatchEventCount)
										break;
								}

								(await Task.WhenAny(expectedNumberOfBatchesSentTcs.Task, Task.Delay(30.Seconds())))
									.Should()
									.Be(expectedNumberOfBatchesSentTcs.Task
										, $"because numberOfEventsEnqueuedSuccessfully: {numberOfEventsEnqueuedSuccessfully}");
							}
							, configurationReader, handler);
					}
				}
			}

			[Fact]
			public void FlushInterval_test()
			{
				var argsVariantsCounter = 0;
				var numberOfEventsToSendVariants = new[] { 1, 2, 3, 10 };

				foreach (var flushInterval in FlushIntervalVariants.Where(x => x.HasValue && x.Value <= VeryShortFlushInterval))
				{
					foreach (var numberOfEventsToSend in numberOfEventsToSendVariants)
					{
						var args = new TestArgs { ArgsIndex = ++argsVariantsCounter, FlushInterval = flushInterval };
						Logger.Debug()?.Log("Starting sub-test... args: {args}", args);

						var batchSentBarrier = new Barrier(2);
						var barrierTimeout = 30.Seconds();

						var handler = new MockHttpMessageHandler((r, c) =>
						{
							batchSentBarrier.SignalAndWait(barrierTimeout).Should().BeTrue();
							return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
						});

						var configurationReader = new TestArgs { FlushInterval = flushInterval }.BuildConfigurationReader(Logger);

						CreateObjectsAndTest((payloadSender, agent) =>
						{
							for (var txIndex = 1; txIndex <= numberOfEventsToSend; ++txIndex)
							{
								EnqueueDummyEvent(payloadSender, agent, txIndex).Should().BeTrue($"txIndex: {txIndex}, args: {args}");
								batchSentBarrier.SignalAndWait(barrierTimeout).Should().BeTrue($"txIndex: {txIndex}, args: {args}");
							}
						}, configurationReader, handler);
					}
				}
			}

			private void CreateObjectsAndTest(Action<PayloadSenderV2, ApmAgent> test
				, IConfigurationReader configurationReader = null
				, HttpMessageHandler httpMessageHandler = null
			) =>
				PayloadSenderTests.CreateObjectsAndTest(Logger, test, configurationReader, httpMessageHandler);

			private Task CreateObjectsAndTest(Func<PayloadSenderV2, ApmAgent, Task> test
				, IConfigurationReader configurationReader = null
				, HttpMessageHandler httpMessageHandler = null
			) =>
				PayloadSenderTests.CreateObjectsAndTest(Logger, test, configurationReader, httpMessageHandler);

			internal class TestArgs
			{
				internal int ArgsIndex { get; set; }
				internal TimeSpan? FlushInterval { get; set; }
				internal int? MaxBatchEventCount { get; set; }
				internal int? MaxQueueEventCount { get; set; }

				internal TestAgentConfigurationReader BuildConfigurationReader(IApmLogger logger) =>
					new TestAgentConfigurationReader(logger
						, flushInterval: FlushInterval.HasValue ? $"{FlushInterval.Value.TotalMilliseconds}ms" : null
						, maxBatchEventCount: MaxBatchEventCount?.ToString()
						, maxQueueEventCount: MaxQueueEventCount?.ToString());

				public override string ToString() => new ToStringBuilder("")
				{
					{ nameof(ArgsIndex), ArgsIndex },
					{ nameof(MaxQueueEventCount), MaxQueueEventCount },
					{ nameof(MaxBatchEventCount), MaxBatchEventCount },
					{ nameof(FlushInterval), FlushInterval }
				}.ToString();
			}
		}
	}
}

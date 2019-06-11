﻿using System.Collections.Generic;
using System.Linq;
using Elastic.Apm.Api;
using Elastic.Apm.Model;

namespace Elastic.Apm.Tests.Mocks
{
	internal class MockPayloadSink
	{
		public readonly List<IError> Errors = new List<IError>();
		public readonly List<ISpan> Spans = new List<ISpan>();
		public readonly List<ITransaction> Transactions = new List<ITransaction>();

		public Error FirstError => Errors.First() as Error;

		/// <summary>
		/// The 1. Span on the 1. Transaction
		/// </summary>
		public Span FirstSpan => Spans.First() as Span;

		public Transaction FirstTransaction => Transactions.First() as Transaction;

		public Span[] SpansOnFirstTransaction => Spans.Where(n => n.TransactionId == Transactions.First().Id).Select(n => n as Span).ToArray();

		public void Clear()
		{
			Spans.Clear();
			Errors.Clear();
			Transactions.Clear();
		}
	}
}

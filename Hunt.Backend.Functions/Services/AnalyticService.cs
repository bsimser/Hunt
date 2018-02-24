﻿using System;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.DataContracts;

namespace Hunt.Backend.Functions
{
	public class AnalyticService : IDisposable
	{
		static TelemetryClient _telemetryClient;
		RequestTelemetry _requestTelemetry;

		private IOperationHolder<RequestTelemetry> _operation;

        public AnalyticService(RequestTelemetry requestTelemetry)
		{
			if(_telemetryClient == null)
			{
				_telemetryClient = new TelemetryClient(TelemetryConfiguration.Active);
				_telemetryClient.InstrumentationKey = Environment.GetEnvironmentVariable("APP_INSIGHTS_KEY");
			}

			_requestTelemetry = requestTelemetry;

			// start tracking request operation
			_operation = _telemetryClient.StartOperation(_requestTelemetry);
		}

		public void TrackException(Exception e)
		{
			// track exceptions that occur
			_telemetryClient.TrackException(e);
			EventHubService.Instance.SendEvent($"EXCEPTION OCCURRED\n\tMessage:\t{e.Message}\n\tStack:\t{e.StackTrace}");
		}

		public void Dispose()
		{
			// stop the operation (and track telemetry).
			_telemetryClient.StopOperation(_operation);
		}
	}
}
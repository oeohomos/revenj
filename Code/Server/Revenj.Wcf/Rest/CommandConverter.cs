﻿using System;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Net;
using Revenj.Api;
using Revenj.Extensibility;
using Revenj.Processing;
using Revenj.Serialization;
using Revenj.Utility;

namespace Revenj.Wcf
{
	public class CommandConverter : ICommandConverter
	{
		private readonly IRestApplication Application;
		private readonly IObjectFactory ObjectFactory;
		private readonly IProcessingEngine ProcessingEngine;
		private readonly IWireSerialization Serialization;

		public CommandConverter(
			IRestApplication application,
			IObjectFactory objectFactory,
			IProcessingEngine processingEngine,
			IWireSerialization serialization)
		{
			Contract.Requires(application != null);
			Contract.Requires(objectFactory != null);
			Contract.Requires(processingEngine != null);
			Contract.Requires(serialization != null);

			this.Application = application;
			this.ObjectFactory = objectFactory;
			this.ProcessingEngine = processingEngine;
			this.Serialization = serialization;
		}

		public Stream PassThrough<TCommand, TArgument>(TArgument argument, string accept, AdditionalCommand[] additionalCommands)
		{
			var start = Stopwatch.GetTimestamp();
			var engine = ProcessingEngine;
			var sessionID = ThreadContext.Request.GetHeader("X-Revenj-Session-ID");
			if (sessionID != null)
			{
				var scope = ObjectFactory.FindScope(sessionID);
				if (scope == null)
					return Utility.ReturnError("Unknown session: " + sessionID, HttpStatusCode.BadRequest);
				engine = scope.Resolve<IProcessingEngine>();
			}
			var commands = new ObjectCommandDescription[1 + (additionalCommands != null ? additionalCommands.Length : 0)];
			commands[0] = new ObjectCommandDescription { Data = argument, CommandType = typeof(TCommand) };
			for (int i = 1; i < commands.Length; i++)
			{
				var ac = additionalCommands[i - 1];
				commands[i] = new ObjectCommandDescription { RequestID = ac.ToHeader, CommandType = ac.CommandType, Data = ac.Argument };
			}
			var stream = RestApplication.ExecuteCommands<object>(engine, Serialization, commands, accept);
			var elapsed = (decimal)(Stopwatch.GetTimestamp() - start) / TimeSpan.TicksPerMillisecond;
			ThreadContext.Response.Headers.Add("X-Duration", elapsed.ToString(CultureInfo.InvariantCulture));
			return stream;
		}

		public Stream ConvertStream<TCommand, TArgument>(TArgument argument)
		{
			var match = new UriTemplateMatch();
			match.RelativePathSegments.Add(typeof(TCommand).FullName);
			ThreadContext.Request.UriTemplateMatch = match;
			if (argument == null)
				return Application.Get();
			using (var ms = ChunkedMemoryStream.Create())
			{
				Serialization.Serialize(argument, ThreadContext.Request.ContentType, ms);
				ms.Position = 0;
				return Application.Post(ms);
			}
		}
	}
}

﻿using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using Clio.Common;

namespace Clio.Command
{
	public abstract class RemoteCommand<TEnvironmentOptions> : Command<TEnvironmentOptions>
		where TEnvironmentOptions : EnvironmentOptions
	{
		protected string RootPath => EnvironmentSettings.IsNetCore
			? EnvironmentSettings.Uri : EnvironmentSettings.Uri + @"/0";

		protected virtual string ServicePath { get; set; }

		protected string ServiceUri => RootPath + ServicePath;

		protected IApplicationClient ApplicationClient { get; }
		protected EnvironmentSettings EnvironmentSettings { get; }

		protected RemoteCommand(IApplicationClient applicationClient,
				EnvironmentSettings environmentSettings) {
			ApplicationClient = applicationClient;
			EnvironmentSettings = environmentSettings;
		}

		protected RemoteCommand(EnvironmentSettings environmentSettings) {
			EnvironmentSettings = environmentSettings;
		}

		public virtual HttpMethod HttpMethod => HttpMethod.Post;

		protected int Login() {
			try {
				Console.WriteLine($"Try login to {EnvironmentSettings.Uri} with {EnvironmentSettings.Login} credentials...");
				ApplicationClient.Login();
				Console.WriteLine("Login done");
				return 0;
			} catch (WebException we) {
				HttpWebResponse errorResponse = we.Response as HttpWebResponse;
				if (errorResponse.StatusCode == HttpStatusCode.NotFound) {
					Console.WriteLine($"Application {EnvironmentSettings.Uri} not found");
				}
				return 1;
			}
		}


		public override int Execute(TEnvironmentOptions options) {
			try {
				ExecuteRemoteCommand(options);
				Console.WriteLine("Done");
				return 0;
			} 
			catch (SilentException ex) {
				return 1;
			}
			catch (Exception e) {
				Console.WriteLine(e.Message);
				return 1;
			}
		}

		protected virtual void ExecuteRemoteCommand(TEnvironmentOptions options) {
			string response;
			if (HttpMethod == HttpMethod.Post) {
				response = ApplicationClient.ExecutePostRequest(ServiceUri, GetRequestData(options));
			} else {
				response = ApplicationClient.ExecuteGetRequest(ServiceUri);
			}
			ProceedResponse(response);
		}

		protected virtual void ProceedResponse(string response) {
		}

		protected virtual string GetRequestData(TEnvironmentOptions options) {
			return "{}";
		}

	}
}

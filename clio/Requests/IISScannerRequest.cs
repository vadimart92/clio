﻿using Clio.Command;
using Clio.UserEnvironment;
using MediatR;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Clio.Requests
{
	public class IISScannerRequest : IExtenalLink
	{
		public string Content {
			get; set;
		}
	}

	/// <summary>
	/// Finds path to appSetting.json
	/// </summary>
	/// <remarks>
	/// Handles extenral link requests
	/// <example><code>clio externalLink clio://IISScannerRequest</code></example>
	/// </remarks>
	/// <example>
	/// </example>
	internal class IISScannerHandler : BaseExternalLinkHandler, IRequestHandler<IISScannerRequest>
	{
		private readonly ISettingsRepository _settingsRepository;
		private readonly RegAppCommand _regCommand;

		public IISScannerHandler(ISettingsRepository settingsRepository, RegAppCommand regCommand)
		{
			_settingsRepository = settingsRepository;
			_regCommand = regCommand;
		}

		public async Task<Unit> Handle(IISScannerRequest request, CancellationToken cancellationToken)
		{
			Uri.TryCreate(request.Content, UriKind.Absolute, out _clioUri);

			IEnumerable<UnregisteredSite> unregSites = _findUnregisteredCreatioSites(_settingsRepository);

			var r = ClioParams?["return"];
			if (r == "remote")
			{
				const string userName = @"TSCRM\k.krylov";          //userName that has access to the remote host
				const string password = "************";             //password
				const string computername = "ts1-mrkt-web01";       //remote host with IIS that we're going to get sites from

				int i = 1;
				(await _test((userName, password, computername))).ToList().ForEach(async site =>
				{
					await Console.Out.WriteLineAsync($"({i++}) {site.Key} - {site.Value}");
				});

				//Here I would call regApp command but instead I will write total
				await Console.Out.WriteLineAsync($"**** TOTAL: {i - 1} new sites ****");
			}
			if (r == "count")
			{
				Console.WriteLine(unregSites.Count());
			}
			if (r == "details")
			{
				var json = JsonSerializer.Serialize(unregSites);
				Console.WriteLine(json);
			}
			if (r == "registerAll")
			{
				unregSites.ToList().ForEach(site =>
				{
					_regCommand.Execute(new RegAppOptions
					{
						IsNetCore = site.siteType == SiteType.Core,
						Uri = site.Uris.FirstOrDefault().ToString(),
						Name = site.siteBinding.name,
						Login = "Supervisor",
						Password = "Supervisor",
						Maintainer = "Customer",
						CheckLogin = false
					});
				});
			}
			return new Unit();
		}


		private sealed record SiteBinding(string name, string state, string binding, string path)
		{
		}

		private sealed record UnregisteredSite(SiteBinding siteBinding, IList<Uri> Uris, SiteType siteType)
		{
		}

		private enum SiteType
		{
			NetFramework,
			Core,
			NotCreatioSite
		}

		/// <summary>
		/// Finds Creatio Sites in IIS that are not registered with clio
		/// </summary>
		private static readonly Func<ISettingsRepository, IEnumerable<UnregisteredSite>> _findUnregisteredCreatioSites = (_settingsRepository) =>
		{
			return _getBindings().Where(site =>
			{
				bool isRegisteredEnvironment = false;
				_convertBindingToUri(site.binding).ForEach(uri =>
				{
					var key = _settingsRepository.FindEnvironmentNameByUri(uri.ToString());
					if (!string.IsNullOrEmpty(key) && !isRegisteredEnvironment)
					{
						isRegisteredEnvironment = true;
					}
				});
				return !isRegisteredEnvironment;
			})
			.Where(site => _detectSiteType(site.path) != SiteType.NotCreatioSite)
			.Select(site =>
			{
				return new UnregisteredSite(
					siteBinding: site,
					Uris: _convertBindingToUri(site.binding),
					siteType: _detectSiteType(site.path));
			});
		};


		/// <summary>
		/// Executes appcmd.exe with arguments and captures output
		/// </summary>
		private static readonly Func<string, string> _appcmd = (args) =>
		{
			const string dirPath = "C:\\Windows\\System32\\inetsrv\\";
			const string exeName = "appcmd.exe";
			return Process.Start(new ProcessStartInfo
			{
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				Arguments = args,
				FileName = Path.Join(dirPath, exeName),
			}).StandardOutput.ReadToEnd();
		};



		private static Func<(string userName, string password, string computerName), Task<Dictionary<string, Uri>>> _test = async (args) =>
		{
			var securestring = new SecureString();
			foreach (char c in args.password)
			{
				securestring.AppendChar(c);
			}

			PSCredential creds = new(args.userName, securestring);
			WSManConnectionInfo connectionInfo = new()
			{
				Credential = creds,
				ComputerName = args.computerName
			};

			Runspace runspace = RunspaceFactory.CreateRunspace(connectionInfo);
			runspace.OpenAsync();

			List<PSObject> tempList = new();
			string script = $"Invoke-Command -ComputerName ts1-mrkt-web01 -ScriptBlock {{Import-Module WebAdministration; Get-WebApplication | ConvertTo-Json}}";
			using (var ps1 = PowerShell.Create())
			{
				ps1?.AddScript(script);
				var result1 = await ps1.InvokeAsync().ConfigureAwait(false);
				tempList.AddRange(result1);
			}

			var distinctListOfWebApps = JsonSerializer.Deserialize<List<WebAppDto>>(tempList.FirstOrDefault().ToString())
			.Where(a => a.PhysicalPath.EndsWith("Terrasoft.WebApp") && a.path.EndsWith("/0"))
			.Select(i => (
				siteName: Regex.Match(i.ItemXPath, "(@name=')(.*?)(' and @id='\\d?')")?.Groups[2].Value?.Trim(),
				sitePath: i.path.Replace("/0", "")
			))
			.ToList();


			tempList.Clear();
			string getSite = $"Invoke-Command -ComputerName ts1-mrkt-web01 -ScriptBlock {{Import-Module WebAdministration; Get-WebSite | ConvertTo-Json}}";
			using (var ps2 = PowerShell.Create())
			{
				ps2?.AddScript(getSite);
				var result2 = await ps2.InvokeAsync().ConfigureAwait(false);
				tempList.AddRange(result2);
			}
			runspace.CloseAsync();
			runspace.Dispose();


			Dictionary<string, List<Uri>> remoteSites = new();
			JsonSerializer.Deserialize<List<WebSiteDto>>(tempList.FirstOrDefault().ToString())
			.Where(dto => distinctListOfWebApps.Select(s => s.siteName).Contains(dto.name))
			.ToList()
			.ForEach(i =>
			{
				string newString = i.bindings.Collection.Replace(" *", "/*").Replace(" ", ",");
				remoteSites.Add(i.name, _convertBindingToUri(newString));
			});



			Dictionary<string, Uri> result = new();
			distinctListOfWebApps.ForEach(i =>
			{
				var uri = remoteSites[i.siteName].Where(u => u.Host != "localhost").FirstOrDefault();
				var newUri = new Uri(uri, i.sitePath);
				result.Add($"{i.siteName}:{i.sitePath}", newUri);

			});

			return result;
		};



		/// <summary>
		/// Gets data from appcmd.exe
		/// </summary>
		private static readonly Func<IEnumerable<SiteBinding>> _getBindings = () =>
		{
			return XElement.Parse(_appcmd("list sites /xml"))
				.Elements("SITE")
				.Select(site => _getSiteBindingFromXmlElement(site));
		};


		/// <summary>
		/// Splits IIS Binding into list
		/// </summary>
		private static readonly Func<string, List<string>> _splitBinding = (binding) =>
		{
			if (binding.Contains(','))
			{
				return binding.Split(',').ToList();
			}
			else
			{
				return new List<string> { binding };
			};
		};

		/// <summary>
		/// Splits IIS Binding
		/// </summary>
		private static readonly Func<string, List<Uri>> _convertBindingToUri = (binding) =>
		{
			//List<string> internalStrings = new();
			List<Uri> result = new();

			//"http/*:7080:localhost,http/*:7080:kkrylovn
			_splitBinding(binding).ForEach(item =>
			{
				//http/*:7080:localhost
				//http/*:80:
				var items = item.Split(':');
				if (items.Length >= 3)
				{
					string hostName = (string.IsNullOrEmpty(items[2])) ? "localhost" : items[2];
					string port = items[1];
					string other = items[0];
					string protocol = other.Replace("/*", "");
					string url = $"{protocol}://{hostName}:{port}";

					if (Uri.TryCreate(url, UriKind.Absolute, out Uri value))
					{
						result.Add(value);
					}
				}
			});
			return result;
		};

		/// <summary>
		/// Converts XElement to Sitebinding
		/// </summary>
		private static readonly Func<XElement, SiteBinding> _getSiteBindingFromXmlElement = (xmlElement) =>
		{
			return new SiteBinding(
				name: xmlElement.Attribute("SITE.NAME")?.Value,
				state: xmlElement.Attribute("state")?.Value,
				binding: xmlElement.Attribute("bindings")?.Value,
				path: _appcmd($"list VDIR {xmlElement.Attribute("SITE.NAME").Value}/ /text:physicalPath").Trim()
			);
		};


		/// <summary>
		/// Detect Site Type
		/// </summary>
		private static Func<string, SiteType> _detectSiteType = (path) =>
		{
			var webapp = Path.Join(path, "Terrasoft.WebApp");
			var configuration = Path.Join(path, "Terrasoft.Configuration");

			if (new DirectoryInfo(webapp).Exists)
			{
				return SiteType.NetFramework;
			}

			if (new DirectoryInfo(configuration).Exists)
			{
				return SiteType.Core;
			}

			return SiteType.NotCreatioSite;
		};

	}


	public record WebAppDto(string ElementTagName, string path, string enabledProtocols, string PhysicalPath, string ItemXPath);

	public record WebSiteDto(string name, int id, Bindings bindings);

	public record Bindings(string Collection);
}

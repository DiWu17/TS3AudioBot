// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;

namespace TS3AudioBot.Web;

public sealed class WebServer : IDisposable
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
	private const string DefaultFrontendFolder = "WebInterface";

	private CancellationTokenSource? cancelToken;
	private readonly ConfWeb config;
	private readonly CoreInjector coreInjector;
	private Api.WebApi? api;

	public WebServer(ConfWeb config, CoreInjector coreInjector)
	{
		this.config = config;
		this.coreInjector = coreInjector;
	}

	// TODO write server to be reload-able
	public void StartWebServer()
	{
		var startWebServer = false;

		if (config.Api.Enabled || config.Interface.Enabled)
		{
			if (!config.Api.Enabled)
				Log.Warn("The api is required for the webinterface to work properly; The api is now implicitly enabled. Enable the api in the config to remove this warning.");

			if (!coreInjector.TryCreate<Api.WebApi>(out var api))
				throw new Exception("Could not create Api object.");

			this.api = api;
			startWebServer = true;
		}

		if (startWebServer)
		{
			StartWebServerInternal();
		}
	}

	public string? FindWebFolder()
	{
		var webData = config.Interface;
		if (string.IsNullOrEmpty(webData.Path))
		{
			for (int i = 0; i < 5; i++)
			{
				var up = Path.Combine(Enumerable.Repeat("..", i).ToArray());
				var checkDir = Path.Combine(up, DefaultFrontendFolder);
				if (Directory.Exists(checkDir))
				{
					// 优先查找 dist 目录（构建后的文件）
					var distDir = Path.Combine(checkDir, "dist");
					if (Directory.Exists(distDir))
					{
						var fullPath = Path.GetFullPath(distDir);
						Log.Info("Found WebInterface dist folder at: {0}", fullPath);
						return fullPath;
					}
					// 如果 dist 不存在，查找 WebInterface 目录本身
					var fullPath2 = Path.GetFullPath(checkDir);
					Log.Warn("WebInterface dist folder not found at: {0}, using WebInterface directory instead. Please run 'npm run build' in WebInterface folder.", Path.Combine(fullPath2, "dist"));
					return fullPath2;
				}
			}

			var asmPath = Path.GetDirectoryName(typeof(Core).Assembly.Location)!;
			var asmWebPath = Path.GetFullPath(Path.Combine(asmPath, DefaultFrontendFolder));
			if (Directory.Exists(asmWebPath))
			{
				// 优先查找 dist 目录
				var distDir = Path.Combine(asmWebPath, "dist");
				if (Directory.Exists(distDir))
				{
					var fullPath = Path.GetFullPath(distDir);
					Log.Info("Found WebInterface dist folder at: {0}", fullPath);
					return fullPath;
				}
				// 如果 dist 不存在，使用 WebInterface 目录
				Log.Warn("WebInterface dist folder not found at: {0}, using WebInterface directory instead. Please run 'npm run build' in WebInterface folder.", Path.Combine(asmWebPath, "dist"));
				return asmWebPath;
			}
		}
		else if (Directory.Exists(webData.Path))
		{
			var fullPath = Path.GetFullPath(webData.Path);
			Log.Info("Using configured WebInterface path: {0}", fullPath);
			return fullPath;
		}

		return null;
	}

	private void StartWebServerInternal()
	{
		cancelToken?.Cancel();
		cancelToken?.Dispose();
		cancelToken = new CancellationTokenSource();

		var host = new WebHostBuilder()
			.SuppressStatusMessages(true)
			.ConfigureLogging((context, logging) =>
			{
				logging.ClearProviders();
			})
			.UseKestrel(kestrel =>
			{
				kestrel.Limits.MaxRequestBodySize = 3_000_000; // 3 MiB should be enough
			})
			.ConfigureServices(services =>
			{
				services.AddCors(options =>
				{
					options.AddPolicy("TS3AB", builder =>
					{
						builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
					});
				});
			})
			.Configure(app =>
			{
				app.UseCors("TS3AB");

				if (api != null) // api enabled
				{
					app.Map(new PathString("/api"), map =>
					{
						map.Run(async ctx =>
						{
							using var _scope = ScopeContext.PushProperty("BotId", "Api");
							await Log.SwallowAsync(() => api.ProcessApiV1Call(ctx));
						});
					});
				}

				if (config.Interface.Enabled)
				{
					app.UseFileServer();
				}

				var applicationLifetime = app.ApplicationServices.GetRequiredService<IApplicationLifetime>();
				applicationLifetime.ApplicationStopping.Register(OnShutdown);
			});

		if (config.Interface.Enabled)
		{
			var baseDir = FindWebFolder();
			if (baseDir is null)
			{
				Log.Error("Can't find a WebInterface path to host. Try specifying the path to host in the config");
			}
			else
			{
				// 检查目录中是否有 index.html 文件
				var indexFile = Path.Combine(baseDir, "index.html");
				if (!File.Exists(indexFile))
				{
					Log.Warn("index.html not found in WebInterface directory: {0}. The web interface may not work properly. Make sure to run 'npm run build' in the WebInterface folder.", baseDir);
				}
				host.UseWebRoot(baseDir);
				Log.Info("WebInterface root set to: {0}", baseDir);
			}
		}

		var addrs = config.Hosts.Value;
		if (addrs.Contains("*"))
		{
			host.ConfigureKestrel(kestrel => { kestrel.ListenAnyIP(config.Port.Value); });
		}
		else if (addrs.Count == 1 && addrs[0] == "localhost")
		{
			host.ConfigureKestrel(kestrel => { kestrel.ListenLocalhost(config.Port.Value); });
		}
		else
		{
			host.UseUrls(addrs.Select(uri => new UriBuilder(uri) { Port = config.Port }.Uri.AbsoluteUri).ToArray());
		}

		Log.Info("Starting Webserver on port {0}", config.Port.Value);
		new Func<Task>(async () =>
		{
			try
			{
				await host.Build().RunAsync(cancelToken.Token);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "The webserver could not be started");
				return;
			}
		})();
	}

	public static void OnShutdown()
	{
		Log.Info("WebServer has closed");
	}

	public void Dispose()
	{
		Log.Info("WebServer is closing");

		cancelToken?.Cancel();
		cancelToken?.Dispose();
		cancelToken = null;
	}
}

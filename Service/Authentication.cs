using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.OidcClient;
using IdentityModel.OidcClient.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace Fyn.Windows.Service
{
    public static class Authentication
    {
        public static ValueTask<LoginResult> Signin(String authority)
        {
            SystemBrowser browser = new SystemBrowser(5002);
            String redirectUri = $"http://127.0.0.1:{browser.Port}";

            OidcClientOptions options = new OidcClientOptions
            {
                Authority = authority,
                ClientId = "Shell.Windows",
                RedirectUri = redirectUri,
                Scope = "openid profile email",
                FilterClaims = false,

                Browser = browser,
                IdentityTokenValidator = new JwtHandlerIdentityTokenValidator(),
                RefreshTokenInnerHttpHandler = new HttpClientHandler(),
            };

            options.LoggerFactory.AddSerilog(
                new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}", 
                        theme: AnsiConsoleTheme.Code
                    )
                    .CreateLogger()
            );

            return new ValueTask<LoginResult>(new OidcClient(options).LoginAsync());
        }
    }

    public class SystemBrowser : IBrowser
    {
        public Int32 Port { get; }
        private readonly String _path;

        public SystemBrowser(Int32? port = null, String? path = null)
        {
            _path = path;
            Port = port ?? GetRandomUnusedPort();
        }

        private static Int32 GetRandomUnusedPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            Int32 port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            return port;
        }

        public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken)
        {
            await using LoopbackHttpListener listener = new LoopbackHttpListener(Port, _path);
            await listener.Start();

            OpenBrowser(options.StartUrl);

            try
            {
                String? result = await listener.WaitForCallbackAsync();

                return String.IsNullOrWhiteSpace(result)
                    ? new BrowserResult
                    {
                        ResultType = BrowserResultType.UnknownError,
                        Error = "Empty response.",
                    }
                    : new BrowserResult
                    {
                        ResultType = BrowserResultType.Success,
                        Response = result,
                    };
            }
            catch (TaskCanceledException ex)
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.Timeout,
                    Error = ex.Message,
                };
            }
            catch (Exception ex)
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.UnknownError,
                    Error = ex.Message,
                };
            }
        }
        public static void OpenBrowser(String url)
        {
            // hack because of this: https://github.com/dotnet/corefx/issues/10361
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                url = url.Replace("&", "^&");
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start(url);
            }
        }
    }

    public class LoopbackHttpListener : IAsyncDisposable
    {
        const Int32 DefaultTimeout = 300_000;

        private readonly IWebHost _host;
        private readonly TaskCompletionSource<String> _source = new TaskCompletionSource<String>();

        public LoopbackHttpListener(Int32 port, String? path = null)
        {
            _host = new WebHostBuilder()
                .UseUrls($"http://127.0.0.1:{port}/{path?.TrimStart('/')}")
                .UseKestrel()
                .Configure(builder =>
                {
                    builder.Run(async context =>
                    {
                        switch (context.Request.Method)
                        {
                            case "GET":
                                {
                                    await SetResult(context.Request.QueryString.Value, context);
                                    break;
                                }

                            case "POST" when !context.Request.ContentType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase):
                                {
                                    context.Response.StatusCode = 415;
                                    break;
                                }

                            case "POST":
                                {
                                    using StreamReader sr = new StreamReader(context.Request.Body, Encoding.UTF8);
                                    await SetResult(await sr.ReadToEndAsync(), context);
                                    break;
                                }

                            default:
                                {
                                    context.Response.StatusCode = 405;
                                    break;
                                }
                        }
                    });
                })
                .ConfigureLogging(options =>
                {
                    options.AddSerilog(
                        new LoggerConfiguration()
                            .MinimumLevel.Debug()
                            .Enrich.FromLogContext()
                            .WriteTo.Console(
                                outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}",
                                theme: AnsiConsoleTheme.Code
                            )
                            .CreateLogger()
                    );
                })
                .Build();
        }

        public Task Start()
        {
            return _host.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(500);

            _host.Dispose();
        }

        private async ValueTask SetResult(String value, HttpContext context)
        {
            try
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<h1>You can now return to the application.</h1>");
                await context.Response.Body.FlushAsync();

                _source.TrySetResult(value);
            }
            catch (Exception exception)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<h1>Invalid request.</h1>");

#if DEBUG
                await context.Response.WriteAsync($"<p>{exception.Message}</p>");
                await context.Response.WriteAsync($"<p>{exception.StackTrace}</p>");
#endif

                await context.Response.Body.FlushAsync();
            }
        }

        public async ValueTask<String> WaitForCallbackAsync(Int32 timeout = DefaultTimeout)
        {
            await Task.Delay(timeout);

            _source.TrySetCanceled();

            return await _source.Task;
        }
    }
}

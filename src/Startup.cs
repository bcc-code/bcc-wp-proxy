using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.ReverseProxy.Service.Proxy;
using Microsoft.ReverseProxy.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;
using BCC.WPProxy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.FileProviders;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace bcc_wp_proxy
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }


        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            var settings = Configuration.GetSection("WPProxy").Get<WPProxySettings>();

            if (!settings.UseRedis)
            {
                services.AddDistributedMemoryCache();
            }
            else
            {
                services.AddStackExchangeRedisCache(cnf =>
                {
                    cnf.InstanceName = settings.RedisInstanceName;
                    cnf.Configuration = $"{settings.RedisIpAddress}:{settings.RedisPort},DefaultDatabase=1";
                });

                // Ensure cookies work across all container instances
                var redis = ConnectionMultiplexer.Connect($"{settings.RedisIpAddress}:{settings.RedisPort}");
                services.AddDataProtection()
                        .PersistKeysToStackExchangeRedis(redis, "wp-proxy-dataprotection-keys");

            }
            services.AddMemoryCache();

            var cacheProvider = services.BuildServiceProvider();

            if (string.IsNullOrEmpty(settings.GoogleStorageBucket))
            {
                services.AddSingleton<IFileStore>(c => new PhysicalFileStore(new PhysicalFileProvider(Directory.GetCurrentDirectory() + "/Files")));
            }
            else
            {
                services.AddSingleton<IFileStore>(c => new GCPStorageStore(settings, cacheProvider.GetRequiredService<IDistributedCache>()));
            }

            services.AddSingleton(c => settings);
            services.AddSingleton<EndpointSelector, WPProxyEndpointSelector>();
            services.AddHttpProxy();
            services.AddHttpClient();
            services.AddReverseProxy().AddConfig();
            services.AddAuthorization(options =>
            {
                options.AddPolicy(WPProxySettings.AuthorizationPolicy, policy =>
                {
                    policy.RequireAuthenticatedUser();
                });
            });


            services.Configure<CookiePolicyOptions>(options =>
            {
                options.MinimumSameSitePolicy = Microsoft.AspNetCore.Http.SameSiteMode.Unspecified;
                options.Secure = CookieSecurePolicy.SameAsRequest;
                options.OnAppendCookie = cookieContext => CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
                options.OnDeleteCookie = cookieContext => CheckSameSite(cookieContext.Context, cookieContext.CookieOptions);
            });

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
           .AddCookie(o =>
           {
               if (settings.UseRedis)
               {
                   o.SessionStore = new RedisSessionStore(cacheProvider.GetRequiredService<IMemoryCache>(), cacheProvider.GetRequiredService<IDistributedCache>());
               }
               else
               {
                   o.SessionStore = new InMemorySessionStore();
               }
           })
           .AddOpenIdConnect("Auth0", options =>
           {
               // Set the authority to your Auth0 domain
               options.Authority = Configuration["Authentication:Authority"];

               // Configure the Auth0 Client ID and Client Secret
               options.ClientId = Configuration["Authentication:ClientId"];
               options.ClientSecret = Configuration["Authentication:ClientSecret"];

               options.Events.OnRedirectToIdentityProvider = context =>
               {
                   // Required for getting access token in JWT format (for widgets)
                   context.ProtocolMessage.SetParameter("audience", Configuration["Authentication:Audience"]);
                   return Task.CompletedTask;
               };

               options.Events.OnRedirectToIdentityProviderForSignOut = context =>
               {
                   context.ProtocolMessage.IssuerAddress =
                      $"{Configuration["Authentication:Authority"].TrimEnd('/')}/{Configuration["Authentication:LogoutEndpoint"].TrimStart('/')}";
                   return Task.CompletedTask;
               };

               options.Events.OnRemoteFailure = context =>
               {
                   var log = context.HttpContext.RequestServices.GetService<ILogger<Startup>>();
                   var errorMessage = "Login callback failed with following error: " + (context.Failure?.Message ?? "");
                   var hasUserAgent = context.HttpContext.Request.Headers.TryGetValue(HeaderNames.UserAgent, out StringValues userAgent);
                   if (hasUserAgent)
                   {
                       errorMessage += ". User Agent: " + userAgent.ToString();
                   }
                   var hasReferer = context.HttpContext.Request.Headers.TryGetValue(HeaderNames.Referer, out StringValues referer);
                   if (hasReferer)
                   {
                       errorMessage += ". Referrer: " + referer.ToString();
                   }

                   log.LogError(context.Failure, errorMessage);

                   return Task.CompletedTask;
               };

               // Set response type to code
               options.ResponseType = OpenIdConnectResponseType.Code;


               options.SaveTokens = true;

               options.GetClaimsFromUserInfoEndpoint = false;

               options.UseTokenLifetime = true;

               // Configure the scope
               options.Scope.Clear();
               options.Scope.Add(Configuration["Authentication:Scopes"]);

               // Set the callback path, so Auth0 will call back to /callback
               // Also ensure that you have added the URL as an Allowed Callback URL in your Auth0 dashboard
               options.CallbackPath = new PathString("/callback");

               // Configure the Claims Issuer to be Auth0
               options.ClaimsIssuer = "Auth0";

           });
            

            // Add framework services.
            services.AddControllersWithViews();

            services.AddSingleton<WPCacheService>();
            services.AddSingleton<WPApiClient>();
            services.AddSingleton<WPUserService>();
            services.AddSingleton<WPMessageHandler>();
            services.AddSingleton<WPMessageInvokerFactory>();
            services.AddApplicationInsightsTelemetry();
        }

        //Ref: https://programmer.group/asp.net-core-3.1-browser-sniffing-solves-missing-cookies-in-some-browsers.html

        private void CheckSameSite(HttpContext httpContext, CookieOptions options)
        {

            if (options.SameSite == Microsoft.AspNetCore.Http.SameSiteMode.None)
            {
                var userAgent = httpContext.Request.Headers["User-Agent"].ToString();
                if (UserAgentDisallowsSameSiteNone(userAgent))
                {
                    options.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Unspecified;
                }

            }
        }

        private static bool UserAgentDisallowsSameSiteNone(string userAgent)
        {

            // Check if a null or empty string has been passed in, since this
            // will cause further interrogation of the useragent to fail.
            if (String.IsNullOrWhiteSpace(userAgent))
                return false;

            // Cover all iOS based browsers here. This includes:
            // - Safari on iOS 12 for iPhone, iPod Touch, iPad
            // - WkWebview on iOS 12 for iPhone, iPod Touch, iPad
            // - Chrome on iOS 12 for iPhone, iPod Touch, iPad
            // All of which are broken by SameSite=None, because they use the iOS networking
            // stack.
            if (userAgent.Contains("CPU iPhone OS 12") ||
                userAgent.Contains("iPad; CPU OS 12"))
            {
                return true;
            }

            // Cover Mac OS X based browsers that use the Mac OS networking stack. 
            // This includes:
            // - Safari on Mac OS X.
            // This does not include:
            // - Chrome on Mac OS X
            // Because they do not use the Mac OS networking stack.
            if (userAgent.Contains("Macintosh; Intel Mac OS X 10_14") &&
                userAgent.Contains("Version/") && userAgent.Contains("Safari"))
            {
                return true;
            }

            // Cover Chrome 50-69, because some versions are broken by SameSite=None, 
            // and none in this range require it.
            // Note: this covers some pre-Chromium Edge versions, 
            // but pre-Chromium Edge does not require SameSite=None.
            if (userAgent.Contains("Chrome/5") || userAgent.Contains("Chrome/6"))
            {
                return true;
            }

            return false;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IHttpProxy httpProxy, WPMessageInvokerFactory messageInvoker, WPProxySettings settings)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }


            app.UseStaticFiles();
            app.UseRouting();
            app.UseCookiePolicy();
            app.UseAuthentication();
            app.UseAuthorization();
            

            app.Use(next => context =>
            {
                // Force https scheme (since request inside a docker container may appear to be http)
                context.Request.Scheme = "https";
                return next(context);
            });

            var transformer = new WPRequestTransformer(); // or HttpTransformer.Default;
            var requestOptions = new RequestProxyOptions { 
                Timeout = TimeSpan.FromSeconds(100),
                Version = new Version(1,1), // Not all servers support HTTP 2
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher            
            };
            app.UseEndpoints(endpoints =>
            {
                // Dont't authenticate manifest.json
                endpoints.Map("/manifest.json", async httpContext => //
                {
                    await httpProxy.ProxyAsync(httpContext, settings.SourceAddress, messageInvoker.Create(), requestOptions, transformer);
                });

                endpoints.Map("/{**catch-all}", async httpContext => //
                {
                    await httpProxy.ProxyAsync(httpContext, settings.SourceAddress, messageInvoker.Create(), requestOptions, transformer);

                    var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
                    if (errorFeature != null)
                    {
                        var error = errorFeature.Error;
                        var exception = errorFeature.Exception;
                        throw errorFeature.Exception;
                    }
                })
                .RequireAuthorization(WPProxySettings.AuthorizationPolicy);

                endpoints.MapDefaultControllerRoute();

            });
        }

        // Workaround to ensure that "catch all" proxy endpoint is only run if other endpoints are not present
        public class WPProxyEndpointSelector : EndpointSelector
        {
            public override Task SelectAsync(HttpContext httpContext, CandidateSet candidates)
            {

                for (var i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Endpoint.DisplayName != "/{**catch-all}" || candidates.Count == 1)
                    {
                        httpContext.SetEndpoint(candidates[i].Endpoint);
                    }
                }
                return Task.CompletedTask;
            }
        }
    }
}

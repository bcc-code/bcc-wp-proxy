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
            var configuration = Configuration.GetSection("WPProxy").Get<WPProxySettings>();

            if (!configuration.UseRedis)
            {
                services.AddDistributedMemoryCache();
            }
            else
            {
                services.AddStackExchangeRedisCache(cnf =>
                {
                    cnf.InstanceName = configuration.RedisInstanceName;
                    cnf.Configuration = $"{configuration.RedisIpAddress}:{configuration.RedisPort},DefaultDatabase=1";
                });

                // Ensure cookies work across all container instances
                var redis = ConnectionMultiplexer.Connect($"{configuration.RedisIpAddress}:{configuration.RedisPort}");
                services.AddDataProtection()
                        .PersistKeysToStackExchangeRedis(redis, "wp-proxy-dataprotection-keys");

            }

            services.AddSingleton(c => configuration);
            services.AddSingleton<EndpointSelector, WPProxyEndpointSelector>();
            services.AddHttpProxy();
            services.AddHttpClient();
            services.AddReverseProxy().AddConfig();
            services.AddAuthorization(options =>
            {
                options.AddPolicy(WPProxySettings.AuthorizationPolicy, policy => policy.RequireAuthenticatedUser());
            });

            //services.ConfigureSameSiteNoneCookies();

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
           .AddCookie()
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
            services.AddMemoryCache();

            // Add framework services.
            services.AddControllersWithViews();

            services.AddSingleton<CacheService>();
            services.AddSingleton<WPApiClient>();
            services.AddSingleton<WPUserService>();
            services.AddSingleton<WPMessageHandler>();
            services.AddSingleton<WPMessageInvokerFactory>();
            services.AddApplicationInsightsTelemetry();
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
            app.UseAuthentication();
            app.UseAuthorization();

            //app.UseEndpoints(endpoints => {
            //    endpoints.MapDefaultControllerRoute();

            //    endpoints.MapReverseProxy();
            //});


            //app.Use(async (context, next) =>
            //{
            //    Endpoint endpoint = context.GetEndpoint();

            //    await next();
            //});


            app.Use(next => context =>
            {
                // Force https scheme (since request inside a docker container may appear to be http)
                context.Request.Scheme = "https";
                return next(context);
            });

            var transformer = new WPRequestTransformer(); // or HttpTransformer.Default;
            var requestOptions = new RequestProxyOptions { Timeout = TimeSpan.FromSeconds(100) };
            app.UseEndpoints(endpoints =>
            {
                endpoints.Map("/{**catch-all}", async httpContext => //
                {

                    await httpProxy.ProxyAsync(httpContext, settings.DestinationAddress, messageInvoker.Create(), requestOptions, transformer);

                    var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
                    if (errorFeature != null)
                    {
                        var error = errorFeature.Error;
                        var exception = errorFeature.Exception;
                    }
                })
                .RequireAuthorization(WPProxySettings.AuthorizationPolicy);

                endpoints.MapDefaultControllerRoute();

                //endpoints.MapReverseProxy();



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

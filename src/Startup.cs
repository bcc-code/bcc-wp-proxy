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

namespace bcc_wp_proxy
{
    public class Startup
    {
        public IConfiguration Configuration;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }


        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(c => Configuration.GetSection("WPProxy").Get<WPProxySettings>());
            services.AddSingleton<EndpointSelector, WPProxyEndpointSelector>();
            services.AddHttpProxy();
            services.AddReverseProxy().AddConfig();
            services.AddAuthorization(options =>
            {
                options.AddPolicy(WPProxySettings.AuthorizationPolicy, policy => policy.RequireAuthenticatedUser());
            });

            //services.ConfigureSameSiteNoneCookies();

            services.AddAuthentication(options => {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
           .AddCookie()
           .AddOpenIdConnect("Auth0", options => {
                // Set the authority to your Auth0 domain
                options.Authority = Configuration["Authentication:Authority"];

                // Configure the Auth0 Client ID and Client Secret
                options.ClientId = Configuration["Authentication:ClientId"];
                options.ClientSecret = Configuration["Authentication:ClientSecret"];

                // Set response type to code
                options.ResponseType = OpenIdConnectResponseType.Code;

                // Configure the scope
                options.Scope.Clear();
                options.Scope.Add("openid");

                // Set the callback path, so Auth0 will call back to /callback
                // Also ensure that you have added the URL as an Allowed Callback URL in your Auth0 dashboard
                options.CallbackPath = new PathString("/callback");

                // Configure the Claims Issuer to be Auth0
                options.ClaimsIssuer = "Auth0";
           });
           services.AddMemoryCache();

            // Add framework services.
            services.AddControllersWithViews();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IHttpProxy httpProxy, IMemoryCache cache, WPProxySettings settings)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }



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

            //    /// Do stuff
            //    await next();
            //});


            app.Use(next => context => {
                // Force https scheme (since request inside a docker container may appear to be http)
                context.Request.Scheme = "https";
                return next(context);
            });

            var httpClient = new HttpMessageInvoker(new WPMessageHandler(cache, settings));
            var transformer = new WPRequestTransformer(); // or HttpTransformer.Default;
            var requestOptions = new RequestProxyOptions { Timeout = TimeSpan.FromSeconds(100) };
            app.UseEndpoints(endpoints =>
            {

                endpoints.Map("/{**catch-all}", async httpContext => //
                {
                    await httpProxy.ProxyAsync(httpContext, settings.DestinationAddress, httpClient,
                        requestOptions, transformer);

                    var errorFeature = httpContext.Features.Get<IProxyErrorFeature>();
                    if (errorFeature != null)
                    {
                        var error = errorFeature.Error;
                        var exception = errorFeature.Exception;
                    }
                }).RequireAuthorization(WPProxySettings.AuthorizationPolicy);

                endpoints.MapDefaultControllerRoute();

                //endpoints.MapReverseProxy();



            });
        }

        // Workaround to ensure that "catch all" proxy endpoint is only run if other endpoints are not present
        public class WPProxyEndpointSelector : EndpointSelector
        {
            public override Task SelectAsync(HttpContext httpContext, CandidateSet candidates)
            {
                
                for (var i=0; i<candidates.Count; i++)
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

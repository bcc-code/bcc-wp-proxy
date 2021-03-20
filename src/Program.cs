using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace bcc_wp_proxy
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) {

            return Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                })
                .ConfigureAppConfiguration((_, config) =>
                {
                    var gcpProjectId = Environment.GetEnvironmentVariable("GCP_ProjectID");
                    if (false && !string.IsNullOrEmpty(gcpProjectId)) //NB: Temporarily disabled provider, because the Google SDK is not compatible with .net 5
                    {
                        config.AddGCMSecretsConfiguration(gcpProjectId);
                    }
                });

            }
    }
}

using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using BCC.WPProxy;
using System.Collections.Concurrent;

namespace Microsoft.Extensions.Configuration
{
    public static class ConfigurationBuilderExtensions
    {
        public static IConfigurationBuilder AddGCMSecretsConfiguration(this IConfigurationBuilder builder, string gcpProjectId) => builder.Add(new GCPSecretsSource(gcpProjectId));
    }
}

namespace BCC.WPProxy
{

    public class GCPSecretsSource : IConfigurationSource
    {
        public GCPSecretsSource(string gcpProjectName)
        {
            GcpProjectId = gcpProjectName;
        }

        public string GcpProjectId { get; }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new GCPSecretsConfigurationProvider(builder, GcpProjectId);
        }
    }

    public class GCPSecretsConfigurationProvider: ConfigurationProvider
    {
        public string GcpProjectId { get; }

        public GCPSecretsConfigurationProvider(IConfigurationBuilder builder, string gcpProjectId)
        {
            GcpProjectId = gcpProjectId;
        }

        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string parentPath)
        {
            return new string[] { };
        }

        private ConcurrentDictionary<string, string> _config = new ConcurrentDictionary<string, string>();

        public override void Load()
        {
            var client = SecretManagerServiceClient.Create();

            var clientIdSecretName = new SecretVersionName( GcpProjectId, "Authentication__ClientId", "latest");
            var clientId = client.AccessSecretVersion(clientIdSecretName)?.Payload?.Data?.ToStringUtf8();
            if (!string.IsNullOrEmpty(clientId))
            {
                _config["Authentication:ClientId"] = clientId;
            }

            var clientSecretSecretName = new SecretVersionName(GcpProjectId, "Authentication__ClientSecret", "latest");
            var clientSecret = client.AccessSecretVersion(clientSecretSecretName)?.Payload?.Data?.ToStringUtf8();
            if (!string.IsNullOrEmpty(clientSecret))
            {
                _config["Authentication:ClientSecret"] = clientSecret;
            }

            base.Load();
        }

        public override bool TryGet(string key, out string value)
        {
            value = null;
            if (_config.ContainsKey(key))
            {
                value = _config[key];
                return true;
            }
            return false;
        }

        public override void Set(string key, string value)
        {
            base.Set(key, value);
        }
    }
}

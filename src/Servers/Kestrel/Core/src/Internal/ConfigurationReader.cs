// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using Microsoft.Extensions.Configuration;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal
{
    internal class ConfigurationReader
    {
        private const string ProtocolsKey = "Protocols";
        private const string CertificatesKey = "Certificates";
        private const string CertificateKey = "Certificate";
        private const string SslProtocolsKey = "SslProtocols";
        private const string EndpointDefaultsKey = "EndpointDefaults";
        private const string EndpointsKey = "Endpoints";
        private const string UrlKey = "Url";

        private readonly IConfiguration _configuration;

        private IDictionary<string, CertificateConfig> _certificates;
        private EndpointDefaults _endpointDefaults;
        private IEnumerable<EndpointConfig> _endpoints;

        public ConfigurationReader(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public IDictionary<string, CertificateConfig> Certificates => _certificates ??= ReadCertificates();
        public EndpointDefaults EndpointDefaults => _endpointDefaults ??= ReadEndpointDefaults();
        public IEnumerable<EndpointConfig> Endpoints => _endpoints ??= ReadEndpoints();

        private IDictionary<string, CertificateConfig> ReadCertificates()
        {
            var certificates = new Dictionary<string, CertificateConfig>(0);

            var certificatesConfig = _configuration.GetSection(CertificatesKey).GetChildren();
            foreach (var certificateConfig in certificatesConfig)
            {
                certificates.Add(certificateConfig.Key, new CertificateConfig(certificateConfig));
            }

            return certificates;
        }

        // "EndpointDefaults": {
        //    "Protocols": "Http1AndHttp2",
        //    "SslProtocols": [ "Tls11", "Tls12", "Tls13"],
        // }
        private EndpointDefaults ReadEndpointDefaults()
        {
            var configSection = _configuration.GetSection(EndpointDefaultsKey);
            return new EndpointDefaults
            {
                Protocols = ParseProtocols(configSection[ProtocolsKey]),
                SslProtocols = ParseSslProcotols(configSection.GetSection(SslProtocolsKey))
            };
        }

        private IEnumerable<EndpointConfig> ReadEndpoints()
        {
            var endpoints = new List<EndpointConfig>();

            var endpointsConfig = _configuration.GetSection(EndpointsKey).GetChildren();
            foreach (var endpointConfig in endpointsConfig)
            {
                // "EndpointName": {
                //    "Url": "https://*:5463",
                //    "Protocols": "Http1AndHttp2",
                //    "SslProtocols": [ "Tls11", "Tls12", "Tls13"],
                //    "Certificate": {
                //        "Path": "testCert.pfx",
                //        "Password": "testPassword"
                //    }
                // }

                var url = endpointConfig[UrlKey];
                if (string.IsNullOrEmpty(url))
                {
                    throw new InvalidOperationException(CoreStrings.FormatEndpointMissingUrl(endpointConfig.Key));
                }

                var endpoint = new EndpointConfig
                {
                    Name = endpointConfig.Key,
                    Url = url,
                    Protocols = ParseProtocols(endpointConfig[ProtocolsKey]),
                    ConfigSection = endpointConfig,
                    Certificate = new CertificateConfig(endpointConfig.GetSection(CertificateKey)),
                    SslProtocols = ParseSslProcotols(endpointConfig.GetSection(SslProtocolsKey))
                };

                endpoints.Add(endpoint);
            }

            return endpoints;
        }

        private static HttpProtocols? ParseProtocols(string protocols)
        {
            if (Enum.TryParse<HttpProtocols>(protocols, ignoreCase: true, out var result))
            {
                return result;
            }

            return null;
        }

        private static SslProtocols? ParseSslProcotols(IConfigurationSection sslProtocols)
        {
            var stringProtocols = sslProtocols.Get<string[]>();

            return stringProtocols?.Aggregate(SslProtocols.None, (acc, current) =>
            {
                if (Enum.TryParse(current, ignoreCase: true, out SslProtocols parsed))
                {
                    return acc | parsed;
                }

                return acc;
            });
        }
    }

    // "EndpointDefaults": {
    //    "Protocols": "Http1AndHttp2",
    //    "SslProtocols": [ "Tls11", "Tls12", "Tls13"],
    // }
    internal class EndpointDefaults
    {
        public HttpProtocols? Protocols { get; set; }
        public SslProtocols? SslProtocols { get; set; }
    }

    // "EndpointName": {
    //    "Url": "https://*:5463",
    //    "Protocols": "Http1AndHttp2",
    //    "SslProtocols": [ "Tls11", "Tls12", "Tls13"],
    //    "Certificate": {
    //        "Path": "testCert.pfx",
    //        "Password": "testPassword"
    //    }
    // }
    internal class EndpointConfig
    {
        private IConfigurationSection _configSection;
        private ConfigSectionClone _configSectionClone;

        public string Name { get; set; }
        public string Url { get; set; }
        public HttpProtocols? Protocols { get; set; }
        public SslProtocols? SslProtocols { get; set; }
        public CertificateConfig Certificate { get; set; }

        // Compare config sections because it's accessible to app developers via an Action<EndpointConfiguration> callback.
        // We cannot rely entirely on comparing config sections for equality, because KestrelConfigurationLoader.Reload() sets
        // EndpointConfig properties to their default values. If a default value changes, the properties would no longer be equal,
        // but the config sections could still be equal.
        public IConfigurationSection ConfigSection
        {
            get => _configSection;
            set
            {
                _configSection = value;
                // The IConfigrationSection will mutate, so we need to take a snapshot to compare against later and check for changes.
                _configSectionClone = new ConfigSectionClone(value);
            }
        }

        public override bool Equals(object obj) =>
            obj is EndpointConfig other &&
            Name == other.Name &&
            Url == other.Url &&
            (Protocols ?? ListenOptions.DefaultHttpProtocols) == (other.Protocols ?? ListenOptions.DefaultHttpProtocols) &&
            Certificate == other.Certificate &&
            (SslProtocols ?? System.Security.Authentication.SslProtocols.None) == (other.SslProtocols ?? System.Security.Authentication.SslProtocols.None) &&
            _configSectionClone == other._configSectionClone;

        public override int GetHashCode() => HashCode.Combine(Name, Url, Protocols ?? ListenOptions.DefaultHttpProtocols, Certificate, _configSectionClone);

        public static bool operator ==(EndpointConfig lhs, EndpointConfig rhs) => lhs is null ? rhs is null : lhs.Equals(rhs);
        public static bool operator !=(EndpointConfig lhs, EndpointConfig rhs) => !(lhs == rhs);
    }

    // "CertificateName": {
    //      "Path": "testCert.pfx",
    //      "Password": "testPassword"
    // }
    internal class CertificateConfig
    {
        public CertificateConfig(IConfigurationSection configSection)
        {
            ConfigSection = configSection;
            ConfigSection.Bind(this);
        }

        public IConfigurationSection ConfigSection { get; }

        // File
        public bool IsFileCert => !string.IsNullOrEmpty(Path);

        public string Path { get; set; }

        public string Password { get; set; }

        // Cert store

        public bool IsStoreCert => !string.IsNullOrEmpty(Subject);

        public string Subject { get; set; }

        public string Store { get; set; }

        public string Location { get; set; }

        public bool? AllowInvalid { get; set; }

        public override bool Equals(object obj) =>
            obj is CertificateConfig other &&
            Path == other.Path &&
            Password == other.Password &&
            Subject == other.Subject &&
            Store == other.Store &&
            Location == other.Location &&
            (AllowInvalid ?? false) == (other.AllowInvalid ?? false);

        public override int GetHashCode() => HashCode.Combine(Path, Password, Subject, Store, Location, AllowInvalid ?? false);

        public static bool operator ==(CertificateConfig lhs, CertificateConfig rhs) => lhs is null ? rhs is null : lhs.Equals(rhs);
        public static bool operator !=(CertificateConfig lhs, CertificateConfig rhs) => !(lhs == rhs);
    }
}

// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Agent.Service.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Autofac;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Core.ConfigSources;
    using Microsoft.Azure.Devices.Edge.Agent.Core.Serde;
    using Microsoft.Azure.Devices.Edge.Agent.Docker;
    using Microsoft.Azure.Devices.Edge.Agent.IoTHub;
    using Microsoft.Azure.Devices.Edge.Agent.IoTHub.ConfigSources;
    using Microsoft.Azure.Devices.Edge.Agent.IoTHub.Reporters;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Configuration;

    public class TwinConfigSourceModule : Module
    {
        readonly string edgeDeviceConnectionString;
        readonly string backupConfigFilePath;
        const string DockerType = "docker";
        readonly IConfiguration configuration;
        readonly VersionInfo versionInfo;

        public TwinConfigSourceModule(
            string edgeDeviceConnectionString,
            string backupConfigFilePath,
            IConfiguration config,
            VersionInfo versionInfo
        )
        {            
            this.edgeDeviceConnectionString = Preconditions.CheckNonWhiteSpace(edgeDeviceConnectionString, nameof(edgeDeviceConnectionString));
            this.backupConfigFilePath = Preconditions.CheckNonWhiteSpace(backupConfigFilePath, nameof(backupConfigFilePath));
            this.configuration = Preconditions.CheckNotNull(config, nameof(config));
            this.versionInfo = Preconditions.CheckNotNull(versionInfo, nameof(versionInfo));
        }

        protected override void Load(ContainerBuilder builder)
        {
            // IDeviceClientProvider
            builder.Register(c => new DeviceClientProvider(this.edgeDeviceConnectionString, this.configuration.GetValue<string>(Constants.UpstreamProtocolKey).ToUpstreamProtocol()))
                .As<IDeviceClientProvider>()
                .SingleInstance();

            // IEdgeAgentConnection
            builder.Register(
                c =>
                {
                    var serde = c.Resolve<ISerde<DeploymentConfig>>();
                    var deviceClientprovider = c.Resolve<IDeviceClientProvider>();
                    IEdgeAgentConnection edgeAgentConnection = new EdgeAgentConnection(deviceClientprovider, serde);
                    return edgeAgentConnection;
                })
                .As<IEdgeAgentConnection>()
                .SingleInstance();

            // Task<IConfigSource>
            builder.Register(
                    c =>
                    {
                        var serde = c.Resolve<ISerde<DeploymentConfigInfo>>();
                        var edgeAgentConnection = c.Resolve<IEdgeAgentConnection>();
                        var twinConfigSource = new TwinConfigSource(edgeAgentConnection, this.configuration);
                        IConfigSource backupConfigSource = new FileBackupConfigSource(this.backupConfigFilePath, twinConfigSource, serde);
                        return Task.FromResult(backupConfigSource);
                    })
                .As<Task<IConfigSource>>()
                .SingleInstance();

            // Task<IReporter>
            builder.Register(
                    async c =>
                    {
                        var runtimeInfoDeserializerTypes = new Dictionary<string, Type>
                        {
                            [DockerType] = typeof(DockerReportedRuntimeInfo),
                            [Constants.Unknown] = typeof(UnknownRuntimeInfo)
                        };

                        var edgeAgentDeserializerTypes = new Dictionary<string, Type>
                        {
                            [DockerType] = typeof(EdgeAgentDockerRuntimeModule)
                        };

                        var edgeHubDeserializerTypes = new Dictionary<string, Type>
                        {
                            [DockerType] = typeof(EdgeHubDockerRuntimeModule),
                            [Constants.Unknown] = typeof(UnknownEdgeHubModule)
                        };

                        var moduleDeserializerTypes = new Dictionary<string, Type>
                        {
                            [DockerType] = typeof(DockerRuntimeModule)
                        };

                        var deserializerTypesMap = new Dictionary<Type, IDictionary<string, Type>>
                        {
                            { typeof(IRuntimeInfo), runtimeInfoDeserializerTypes },
                            { typeof(IEdgeAgentModule), edgeAgentDeserializerTypes },
                            { typeof(IEdgeHubModule), edgeHubDeserializerTypes },
                            { typeof(IModule), moduleDeserializerTypes }
                        };

                        return new IoTHubReporter(
                            c.Resolve<IEdgeAgentConnection>(),
                            await c.Resolve<Task<IEnvironment>>(),
                            new TypeSpecificSerDe<AgentState>(deserializerTypesMap),
                            this.versionInfo
                        ) as IReporter;
                    }
                )
                .As<Task<IReporter>>()
                .SingleInstance();

            base.Load(builder);
        }
    }
}
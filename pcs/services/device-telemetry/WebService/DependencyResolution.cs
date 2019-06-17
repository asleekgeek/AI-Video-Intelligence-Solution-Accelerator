// Copyright (c) Microsoft. All rights reserved.

using System.Reflection;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Azure.EventHubs.Processor;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.Services.Storage;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.ActionsAgent.EventHub;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.Services.Diagnostics;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.Services.External;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.Services.Http;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.Services.Runtime;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.Services.Storage.CosmosDB;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.Services.Storage.TimeSeries;
using Microsoft.Azure.IoTSolutions.DeviceTelemetry.WebService.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Azure.IoTSolutions.DeviceTelemetry.WebService
{
    public class DependencyResolution
    {
        /// <summary>
        /// Autofac configuration. Find more information here:
        /// @see http://docs.autofac.org/en/latest/integration/aspnetcore.html
        /// </summary>
        public static IContainer Setup(IServiceCollection services)
        {
            var builder = new ContainerBuilder();

            builder.Populate(services);

            AutowireAssemblies(builder);
            SetupCustomRules(builder);

            var container = builder.Build();
            RegisterFactory(container);

            return container;
        }

        /// <summary>
        /// Autowire interfaces to classes from all the assemblies, to avoid
        /// manual configuration. Note that autowiring works only for interfaces
        /// with just one implementation.
        /// @see http://autofac.readthedocs.io/en/latest/register/scanning.html
        /// </summary>
        private static void AutowireAssemblies(ContainerBuilder builder)
        {
            var assembly = Assembly.GetEntryAssembly();
            builder.RegisterAssemblyTypes(assembly).AsImplementedInterfaces();

            // Auto-wire additional assemblies
            assembly = typeof(IServicesConfig).GetTypeInfo().Assembly;
            builder.RegisterAssemblyTypes(assembly).AsImplementedInterfaces();

            assembly = typeof(ActionsAgent.IAgent).GetTypeInfo().Assembly;
            builder.RegisterAssemblyTypes(assembly).AsImplementedInterfaces();
        }

        /// <summary>Setup Custom rules overriding autowired ones.</summary>
        private static void SetupCustomRules(ContainerBuilder builder)
        {
            // Make sure the configuration is read only once.
            IConfig config = new Config(new ConfigData(new Logger(Uptime.ProcessId, LogLevel.Info)));
            builder.RegisterInstance(config).As<IConfig>().SingleInstance();

            // Service configuration is generated by the entry point, so we
            // prepare the instance here.
            builder.RegisterInstance(config.ServicesConfig).As<IServicesConfig>().SingleInstance();

            // Instantiate only one logger
            // TODO: read log level from configuration
            var logger = new Logger(Uptime.ProcessId, LogLevel.Debug);
            builder.RegisterInstance(logger).As<ILogger>().SingleInstance();

            // Set up storage client for Cosmos DB
            var storageClient = new StorageClient(config.ServicesConfig, logger);
            builder.RegisterInstance(storageClient).As<IStorageClient>().SingleInstance();

            // Set up BLOB storage client
            ICloudStorageWrapper cloudStorageWrapper = new CloudStoragWrapper();
            IBlobStorageHelper blobStorageHelper = new BlobStorageHelper(config.BlobStorageConfig, cloudStorageWrapper, logger);
            builder.RegisterInstance(blobStorageHelper).As<IBlobStorageHelper>().SingleInstance();

            // Http
            IHttpClient httpClient = new HttpClient(logger);
            builder.RegisterInstance(httpClient).As<IHttpClient>();

            // Setup Time Series Insights Client
            var timeSeriesClient = new TimeSeriesClient(httpClient, config.ServicesConfig, logger);
            builder.RegisterInstance(timeSeriesClient).As<ITimeSeriesClient>().SingleInstance();

            // Auth and CORS setup
            Auth.Startup.SetupDependencies(builder, config);

            // By default Autofac uses a request lifetime, creating new objects
            // for each request, which is good to reduce the risk of memory
            // leaks, but not so good for the overall performance.
            //builder.RegisterType<CLASS_NAME>().As<INTERFACE_NAME>().SingleInstance();
            builder.RegisterType<UserManagementClient>().As<IUserManagementClient>().SingleInstance();
            builder.RegisterType<DiagnosticsClient>().As<IDiagnosticsClient>().SingleInstance();

            // Event Hub Classes
            IEventProcessorHostWrapper eventProcessorHostWrapper = new EventProcessorHostWrapper();
            builder.RegisterInstance(eventProcessorHostWrapper).As<IEventProcessorHostWrapper>().SingleInstance();
            IEventProcessorFactory eventProcessorFactory = new ActionsEventProcessorFactory(logger, config.ServicesConfig, httpClient);
            builder.RegisterInstance(eventProcessorFactory).As<IEventProcessorFactory>().SingleInstance();
        }

        private static void RegisterFactory(IContainer container)
        {
            Factory.RegisterContainer(container);
        }

        /// <summary>
        /// Provide factory pattern for dependencies that are instantiated
        /// multiple times during the application lifetime.
        /// How to use:
        /// <code>
        /// class MyClass : IMyClass {
        ///     public MyClass(DependencyInjection.IFactory factory) {
        ///         this.factory = factory;
        ///     }
        ///     public SomeMethod() {
        ///         var instance1 = this.factory.Resolve<ISomething>();
        ///         var instance2 = this.factory.Resolve<ISomething>();
        ///         var instance3 = this.factory.Resolve<ISomething>();
        ///     }
        /// }
        /// </code>
        /// </summary>
        public interface IFactory
        {
            T Resolve<T>();
        }

        public class Factory : IFactory
        {
            private static IContainer container;

            public static void RegisterContainer(IContainer c)
            {
                container = c;
            }

            public T Resolve<T>()
            {
                return container.Resolve<T>();
            }
        }
    }
}

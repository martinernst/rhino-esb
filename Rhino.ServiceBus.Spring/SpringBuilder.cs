using System;
using System.Linq;
using System.Messaging;
using Rhino.ServiceBus.Actions;
using Rhino.ServiceBus.Config;
using Rhino.ServiceBus.Convertors;
using Rhino.ServiceBus.DataStructures;
using Rhino.ServiceBus.Impl;
using Rhino.ServiceBus.Internal;
using Rhino.ServiceBus.LoadBalancer;
using Rhino.ServiceBus.MessageModules;
using Rhino.ServiceBus.Msmq;
using Rhino.ServiceBus.Msmq.TransportActions;
using Spring.Context;
using ErrorAction = Rhino.ServiceBus.Msmq.TransportActions.ErrorAction;
using LoadBalancerConfiguration = Rhino.ServiceBus.LoadBalancer.LoadBalancerConfiguration;
using System.Collections.Generic;
using System.Reflection;
using Rhino.ServiceBus.Transport;

namespace Rhino.ServiceBus.Spring
{
    [CLSCompliant(false)]
    public class SpringBuilder : IBusContainerBuilder
    {
        private readonly AbstractRhinoServiceBusConfiguration config;
        private readonly IConfigurableApplicationContext applicationContext;

        public SpringBuilder(AbstractRhinoServiceBusConfiguration config, IConfigurableApplicationContext applicationContext)
        {
            this.config = config;
            this.applicationContext = applicationContext;
            config.BuildWith(this);
        }

        public void WithInterceptor(IConsumerInterceptor interceptor)
        {
            applicationContext.ObjectFactory.AddObjectPostProcessor(new ConsumerInterceptor(interceptor, applicationContext));
        }

        public void RegisterDefaultServices(IEnumerable<Assembly> assemblies)
        {
            applicationContext.RegisterSingleton<IServiceLocator>(() => new SpringServiceLocator(applicationContext));
            foreach (var assembly in assemblies)
                applicationContext.RegisterSingletons<IBusConfigurationAware>(assembly);

            var locator = applicationContext.Get<IServiceLocator>();
            foreach (var busConfigurationAware in applicationContext.GetAll<IBusConfigurationAware>())
                busConfigurationAware.Configure(config, this, locator);

            foreach (var module in config.MessageModules)
                applicationContext.RegisterSingleton(module, module.FullName);

            applicationContext.RegisterSingleton<IReflection>(() => new SpringReflection());
            applicationContext.RegisterSingleton(config.SerializerType);
            applicationContext.RegisterSingleton<IEndpointRouter>(() => new EndpointRouter());
        }

        public void RegisterBus()
        {
            var busConfig = (RhinoServiceBusConfiguration)config;

            applicationContext.RegisterSingleton<IStartableServiceBus>(() => new DefaultServiceBus(applicationContext.Get<IServiceLocator>(),
                                                   applicationContext.Get<ITransport>(),
                                                   applicationContext.Get<ISubscriptionStorage>(),
                                                   applicationContext.Get<IReflection>(),
                                                   applicationContext.GetAll<IMessageModule>().ToArray(),
                                                   busConfig.MessageOwners.ToArray(),
                                                   applicationContext.Get<IEndpointRouter>()));

            applicationContext.RegisterSingleton(() => new CreateQueuesAction(applicationContext.Get<IQueueStrategy>(), applicationContext.Get<IServiceBus>()));
        }

        public void RegisterPrimaryLoadBalancer()
        {
            var loadBalancerConfig = (LoadBalancerConfiguration)config;

            applicationContext.RegisterSingleton(() =>
            {
                MsmqLoadBalancer balancer = new MsmqLoadBalancer(applicationContext.Get<IMessageSerializer>(),
                    applicationContext.Get<IQueueStrategy>(),
                    applicationContext.Get<IEndpointRouter>(),
                    loadBalancerConfig.Endpoint,
                    loadBalancerConfig.ThreadCount,
                    loadBalancerConfig.Transactional,
                    applicationContext.Get<IMessageBuilder<Message>>(),
                    applicationContext.Get<ITransactionStrategy>());
                    balancer.ReadyForWorkListener = applicationContext.Get<MsmqReadyForWorkListener>();
                    return balancer;
                });

            applicationContext.RegisterSingleton<IDeploymentAction>(() => new CreateLoadBalancerQueuesAction(applicationContext.Get<IQueueStrategy>(), applicationContext.Get<MsmqLoadBalancer>()));
        }

        public void RegisterSecondaryLoadBalancer()
        {
            var loadBalancerConfig = (LoadBalancerConfiguration)config;

            applicationContext.RegisterSingleton<MsmqLoadBalancer>(() =>
            {
                MsmqSecondaryLoadBalancer balancer =
                    new MsmqSecondaryLoadBalancer(applicationContext.Get<IMessageSerializer>(),
                        applicationContext.Get<IQueueStrategy>(),
                        applicationContext.Get<IEndpointRouter>(),
                        loadBalancerConfig.Endpoint,
                        loadBalancerConfig.PrimaryLoadBalancer,
                        loadBalancerConfig.ThreadCount,
                        loadBalancerConfig.Transactional,
                        applicationContext.Get<IMessageBuilder<Message>>(),
                        applicationContext.Get<ITransactionStrategy>());
                    balancer.ReadyForWorkListener = applicationContext.Get<MsmqReadyForWorkListener>();
                    return balancer;
                });

            applicationContext.RegisterSingleton<IDeploymentAction>(() => new CreateLoadBalancerQueuesAction(applicationContext.Get<IQueueStrategy>(), applicationContext.Get<MsmqLoadBalancer>()));
        }

        public void RegisterReadyForWork()
        {
            var loadBalancerConfig = (LoadBalancerConfiguration)config;

            applicationContext.RegisterSingleton(
                () => new MsmqReadyForWorkListener(applicationContext.Get<IQueueStrategy>(),
                    loadBalancerConfig.ReadyForWork,
                    loadBalancerConfig.ThreadCount,
                    applicationContext.Get<IMessageSerializer>(),
                    applicationContext.Get<IEndpointRouter>(),
                    loadBalancerConfig.Transactional,
                    applicationContext.Get<IMessageBuilder<Message>>(),
                    applicationContext.Get<ITransactionStrategy>()));

            applicationContext.RegisterSingleton<IDeploymentAction>(() => new CreateReadyForWorkQueuesAction(applicationContext.Get<IQueueStrategy>(), applicationContext.Get<MsmqReadyForWorkListener>()));
        }

        public void RegisterLoadBalancerEndpoint(Uri loadBalancerEndpoint)
        {
            applicationContext.RegisterSingleton(typeof(LoadBalancerMessageModule).FullName, () => new LoadBalancerMessageModule(
                                                                                                                                loadBalancerEndpoint,
                                                                                                                                applicationContext.Get<IEndpointRouter>()));
        }

        public void RegisterLoggingEndpoint(Uri logEndpoint)
        {
            applicationContext.RegisterSingleton(typeof(MessageLoggingModule).FullName, () => new MessageLoggingModule(applicationContext.Get<IEndpointRouter>(), logEndpoint));
            applicationContext.RegisterSingleton<IDeploymentAction>(() => new CreateLogQueueAction(applicationContext.Get<IQueueStrategy>(), applicationContext.Get<MessageLoggingModule>(), applicationContext.Get<ITransport>()));
        }

        public void RegisterSingleton<T>(Func<T> func)
            where T : class
        {
            T singleton = null;
            applicationContext.RegisterSingleton<T>(() => singleton == null ? singleton = func() : singleton);
        }
        public void RegisterSingleton<T>(string name, Func<T> func)
            where T : class
        {
            T singleton = null;
            applicationContext.RegisterSingleton<T>(name, () => singleton == null ? singleton = func() : singleton);
        }

        public void RegisterAll<T>(params Type[] excludes)
            where T : class { RegisterAll<T>((Predicate<Type>)(x => !x.IsAbstract && !x.IsInterface && typeof(T).IsAssignableFrom(x) && !excludes.Contains(x))); }
        public void RegisterAll<T>(Predicate<Type> condition)
            where T : class
        {
            typeof(T).Assembly.GetTypes()
                .Where(x => condition(x))
                .ToList()
                .ForEach(x => applicationContext.RegisterSingleton(x, x.FullName));
        }

        public void RegisterSecurity(byte[] key)
        {
            applicationContext.RegisterSingleton<IEncryptionService>(() => new RijndaelEncryptionService(key));
            applicationContext.RegisterSingleton<IValueConvertor<WireEncryptedString>>(() => new WireEncryptedStringConvertor(applicationContext.Get<IEncryptionService>()));
            applicationContext.RegisterSingleton<IElementSerializationBehavior>(() => new WireEncryptedMessageConvertor(applicationContext.Get<IEncryptionService>()));
        }

        public void RegisterNoSecurity()
        {
            applicationContext.RegisterSingleton<IValueConvertor<WireEncryptedString>>(() => new ThrowingWireEncryptedStringConvertor());
            applicationContext.RegisterSingleton<IElementSerializationBehavior>(() => new ThrowingWireEncryptedMessageConvertor());
        }
    }
}

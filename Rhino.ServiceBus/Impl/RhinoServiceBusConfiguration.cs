using System;
using System.Linq;
using System.Collections.Generic;
using System.Configuration;
using System.Transactions;
using Rhino.ServiceBus.Config;
using Rhino.ServiceBus.Msmq;

namespace Rhino.ServiceBus.Impl
{
    public class RhinoServiceBusConfiguration : AbstractRhinoServiceBusConfiguration
    {
        private readonly List<MessageOwner> messageOwners = new List<MessageOwner>();

        public IEnumerable<MessageOwner> MessageOwners
        {
            get { return messageOwners; }
        }

        protected override void ReadBusConfiguration()
        {
            base.ReadBusConfiguration();
            new MessageOwnersConfigReader(ConfigurationSection, messageOwners).ReadMessageOwners();
        }

        protected override void ApplyConfiguration()
        {
            var busConfig = ConfigurationSection.Bus;
            if (busConfig == null)
                throw new ConfigurationErrorsException("Could not find 'bus' node in configuration");

            if (busConfig.NumberOfRetries.HasValue)
                NumberOfRetries = busConfig.NumberOfRetries.Value;

            if (busConfig.ThreadCount.HasValue)
                ThreadCount = busConfig.ThreadCount.Value;

            var isolationLevel = busConfig.QueueIsolationLevel;
            if (!string.IsNullOrEmpty(isolationLevel))
                queueIsolationLevel = (IsolationLevel)Enum.Parse(typeof(IsolationLevel), isolationLevel);

            if (busConfig.ConsumeInTransaction.HasValue)
                consumeInTxn = busConfig.ConsumeInTransaction.Value;

            if (busConfig.UseDtc.HasValue)
                UseDtc = busConfig.UseDtc.Value;

            var uriString = busConfig.Endpoint;
            Uri endpoint;
            if (!Uri.TryCreate(uriString, UriKind.Absolute, out endpoint))
                throw new ConfigurationErrorsException(
                    "Attribute 'endpoint' on 'bus' has an invalid value '" + uriString + "'");
            Endpoint = endpoint;

            var transactionalString = busConfig.Transactional;

            bool temp;
            if (bool.TryParse(transactionalString, out temp))
                Transactional = temp ? TransactionalOptions.Transactional : TransactionalOptions.NonTransactional;
            else if (transactionalString != null)
                throw new ConfigurationErrorsException(
                    "Attribute 'transactional' on 'bus' has an invalid value '" + transactionalString + "'");

            var assemblies = ConfigurationSection.Assemblies;
            if (assemblies != null)
                foreach (AssemblyElement assembly in assemblies)
                    scanAssemblies.Add(assembly.Assembly);
        }

        public override void Configure()
        {
            base.Configure();
            Builder.RegisterBus();
        }

        public AbstractRhinoServiceBusConfiguration UseFlatQueueStructure()
        {
            UseFlatQueue = true;
            return this;
        }
    }
}

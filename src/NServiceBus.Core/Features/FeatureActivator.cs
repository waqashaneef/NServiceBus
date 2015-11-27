namespace NServiceBus.Features
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using NServiceBus.ObjectBuilder;
    using Pipeline;
    using NServiceBus.Settings;

    class FeatureActivator
    {
        public FeatureActivator(SettingsHolder settings)
        {
            this.settings = settings;
        }

        internal List<FeatureDiagnosticData> Status
        {
            get { return features.Select(f => f.Diagnostics).ToList(); }
        }

        public void Add(Feature feature)
        {
            if (feature.IsEnabledByDefault)
            {
                settings.EnableFeatureByDefault(feature.GetType());
            }

            features.Add(new FeatureInfo(feature, new FeatureDiagnosticData
            {
                EnabledByDefault = feature.IsEnabledByDefault,
                Name = feature.Name,
                Version = feature.Version,
                Dependencies = feature.Dependencies.AsReadOnly(),
            }));
        }

        public FeaturesReport SetupFeatures(IConfigureComponents container, PipelineSettings pipelineSettings)
        {
            // featuresToActivate is enumerated twice because after setting defaults some new features might got activated.
            var sourceFeatures = Sort(features);

            var enabledFeatures = new List<FeatureInfo>();
            while (true)
            {
                var featureToActivate = sourceFeatures.FirstOrDefault(x => settings.IsFeatureEnabled(x.Feature.GetType()));
                if (featureToActivate == null)
                {
                    break;
                }
                sourceFeatures.Remove(featureToActivate);
                enabledFeatures.Add(featureToActivate);
                featureToActivate.Feature.ConfigureDefaults(settings);
            }

            foreach (var feature in enabledFeatures)
            {
                ActivateFeature(feature, enabledFeatures, container, pipelineSettings);
            }
            settings.PreventChanges();

            return new FeaturesReport(features.Select(t => t.Diagnostics).ToList());
        }

        public async Task StartFeatures(IBuilder builder, IBusContext context)
        {
            foreach (var feature in features.Where(f => f.Feature.IsActive))
            {
                foreach (var taskFactory in feature.TaskFactories)
                {
                    var task = taskFactory(builder);
                    feature.Tasks.Add(task);

                    await task.PerformStartup(context).ConfigureAwait(false);
                }
            }
        }

        public async Task StopFeatures(IBusContext context)
        {
            foreach (var feature in features.Where(f => f.Feature.IsActive))
            {
                foreach (var task in feature.Tasks)
                {
                    await task.PerformStop(context).ConfigureAwait(false);

                    DisposeIfNecessary(task);
                }
            }
        }

        static void DisposeIfNecessary(FeatureStartupTask task)
        {
            var disposableTask = task as IDisposable;
            disposableTask?.Dispose();
        }

        static List<FeatureInfo> Sort(IEnumerable<FeatureInfo> features)
        {
            // Step 1: create nodes for graph
            var nameToNodeDict = new Dictionary<string, Node>();
            var allNodes = new List<Node>();
            foreach (var feature in features)
            {
                // create entries to preserve order within
                var node = new Node
                {
                    FeatureState = feature
                };

                nameToNodeDict[feature.Feature.Name] = node;
                allNodes.Add(node);
            }

            // Step 2: create edges dependencies
            foreach (var node in allNodes)
            {
                foreach (var dependencyName in node.FeatureState.Feature.Dependencies.SelectMany(listOfDependencyNames => listOfDependencyNames))
                {
                    Node referencedNode;
                    if (nameToNodeDict.TryGetValue(dependencyName, out referencedNode))
                    {
                        node.previous.Add(referencedNode);
                    }
                }
            }

            // Step 3: Perform Topological Sort
            var output = new List<FeatureInfo>();
            foreach (var node in allNodes)
            {
                node.Visit(output);
            }

            return output;
        }

        bool ActivateFeature(FeatureInfo featureInfo, List<FeatureInfo> featuresToActivate, IConfigureComponents container, PipelineSettings pipelineSettings)
        {
            if (featureInfo.Feature.IsActive)
            {
                return true;
            }

            Func<List<string>, bool> dependencyActivator = dependencies =>
            {
                var dependantFeaturesToActivate = new List<FeatureInfo>();

                foreach (var dependency in dependencies.Select(dependencyName => featuresToActivate
                    .SingleOrDefault(f => f.Feature.Name == dependencyName))
                    .Where(dependency => dependency != null))
                {
                    dependantFeaturesToActivate.Add(dependency);
                }
                return dependantFeaturesToActivate.Aggregate(false, (current, f) => current | ActivateFeature(f, featuresToActivate, container, pipelineSettings));
            };
            var featureType = featureInfo.Feature.GetType();
            if (featureInfo.Feature.Dependencies.All(dependencyActivator))
            {
                featureInfo.Diagnostics.DependenciesAreMeet = true;

                var context = new FeatureConfigurationContext(settings, container, pipelineSettings);
                if (!HasAllPrerequisitesSatisfied(featureInfo.Feature, featureInfo.Diagnostics, context))
                {
                    settings.MarkFeatureAsDeactivated(featureType);
                    return false;
                }
                settings.MarkFeatureAsActive(featureType);
                featureInfo.Feature.SetupFeature(context);
                featureInfo.TaskFactories = context.TaskFactories;
                featureInfo.Diagnostics.StartupTasks = context.TaskNames;
                featureInfo.Diagnostics.Active = true;
                return true;
            }
            settings.MarkFeatureAsDeactivated(featureType);
            featureInfo.Diagnostics.DependenciesAreMeet = false;
            return false;
        }

        static bool HasAllPrerequisitesSatisfied(Feature feature, FeatureDiagnosticData diagnosticData, FeatureConfigurationContext context)
        {
            diagnosticData.PrerequisiteStatus = feature.CheckPrerequisites(context);

            return diagnosticData.PrerequisiteStatus.IsSatisfied;
        }

        List<FeatureInfo> features = new List<FeatureInfo>();
        SettingsHolder settings;

        class FeatureInfo
        {
            public FeatureInfo(Feature feature, FeatureDiagnosticData diagnostics)
            {
                Diagnostics = diagnostics;
                Feature = feature;
                Tasks = new List<FeatureStartupTask>();
            }

            public FeatureDiagnosticData Diagnostics { get; }
            public Feature Feature { get; }
            public IReadOnlyList<Func<IBuilder, FeatureStartupTask>> TaskFactories { get; set; }
            public List<FeatureStartupTask> Tasks { get; } 

            public override string ToString()
            {
                return $"{Feature.Name} [{Feature.Version}]";
            }
        }

        class Node
        {
            internal void Visit(ICollection<FeatureInfo> output)
            {
                if (visited)
                {
                    return;
                }
                visited = true;
                foreach (var n in previous)
                {
                    n.Visit(output);
                }
                output.Add(FeatureState);
            }

            internal FeatureInfo FeatureState;
            internal List<Node> previous = new List<Node>();
            bool visited;
        }
    }
}
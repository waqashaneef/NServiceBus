﻿namespace NServiceBus.AcceptanceTests.Sagas
{
    using System;
    using System.Threading.Tasks;
    using EndpointTemplates;
    using AcceptanceTesting;
    using Features;
    using NServiceBus.AcceptanceTests.Routing;
    using NUnit.Framework;
    using ScenarioDescriptors;

    //Repro for #1323
    public class When_started_by_event_from_another_saga : NServiceBusAcceptanceTest
    {
        [Test]
        public async Task Should_start_the_saga_and_request_a_timeout()
        {
            await Scenario.Define<Context>()
                .WithEndpoint<SagaThatPublishesAnEvent>(b =>
                    b.When(c => c.IsEventSubscriptionReceived,
                            bus =>
                            {
                                bus.SendLocal(new StartSaga
                                {
                                    DataId = Guid.NewGuid()
                                });
                                return Task.FromResult(0);
                            })
                )
                .WithEndpoint<SagaThatIsStartedByTheEvent>(
                    b => b.Given((bus, context) =>
                    {
                        bus.Subscribe<SomethingHappenedEvent>();

                        if (context.HasNativePubSubSupport)
                            context.IsEventSubscriptionReceived = true;
                        return Task.FromResult(0);
                    }))
                .Done(c => c.DidSaga1Complete && c.DidSaga2Complete)
                .Repeat(r => r.For(Transports.Default))
                .Should(c => Assert.True(c.DidSaga1Complete && c.DidSaga2Complete))
                .Run();
        }

        public class Context : ScenarioContext
        {
            public bool DidSaga1Complete { get; set; }
            public bool DidSaga2Complete { get; set; }
            public bool IsEventSubscriptionReceived { get; set; }
        }

        public class SagaThatPublishesAnEvent : EndpointConfigurationBuilder
        {
            public SagaThatPublishesAnEvent()
            {
                EndpointSetup<DefaultPublisher>(b =>
                {
                    b.EnableFeature<TimeoutManager>();
                    b.OnEndpointSubscribed<Context>((s, context) =>
                    {
                        context.IsEventSubscriptionReceived = true;
                    });
                });
            }

            public class EventFromOtherSaga1 : Saga<EventFromOtherSaga1.EventFromOtherSaga1Data>, IAmStartedByMessages<StartSaga>, IHandleTimeouts<EventFromOtherSaga1.Timeout1>
            {
                public Context Context { get; set; }

                public Task Handle(StartSaga message)
                {
                    Data.DataId = message.DataId;

                    //Publish the event, which will start the second saga
                    Bus.Publish<SomethingHappenedEvent>(m => { m.DataId = message.DataId; });

                    //Request a timeout
                    RequestTimeout<Timeout1>(TimeSpan.FromSeconds(5));

                    return Task.FromResult(0);
                }

                public Task Timeout(Timeout1 state)
                {
                    MarkAsComplete();
                    Context.DidSaga1Complete = true;
                    return Task.FromResult(0);
                }

                public class EventFromOtherSaga1Data : ContainSagaData
                {
                    public virtual Guid DataId { get; set; }
                }

                public class Timeout1
                {
                }

                protected override void ConfigureHowToFindSaga(SagaPropertyMapper<EventFromOtherSaga1Data> mapper)
                {
                    mapper.ConfigureMapping<StartSaga>(m => m.DataId).ToSaga(s => s.DataId);
                }
            }
        }

        public class SagaThatIsStartedByTheEvent : EndpointConfigurationBuilder
        {
            public SagaThatIsStartedByTheEvent()
            {
                EndpointSetup<DefaultServer>(c =>
                {
                    c.EnableFeature<TimeoutManager>();
                    c.DisableFeature<AutoSubscribe>();
                })
                    .AddMapping<SomethingHappenedEvent>(typeof(SagaThatPublishesAnEvent));

            }

            public class EventFromOtherSaga2 : Saga<EventFromOtherSaga2.EventFromOtherSaga2Data>, IAmStartedByMessages<SomethingHappenedEvent>, IHandleTimeouts<EventFromOtherSaga2.Saga2Timeout>
            {
                public Context Context { get; set; }

                public Task Handle(SomethingHappenedEvent message)
                {
                    Data.DataId = message.DataId;
                    //Request a timeout
                    RequestTimeout<Saga2Timeout>(TimeSpan.FromSeconds(5));
                    return Task.FromResult(0);
                }

                public Task Timeout(Saga2Timeout state)
                {
                    MarkAsComplete();
                    Context.DidSaga2Complete = true;
                    return Task.FromResult(0);
                }

                public class EventFromOtherSaga2Data : ContainSagaData
                {
                    public virtual Guid DataId { get; set; }
                }

                public class Saga2Timeout
                {
                }

                protected override void ConfigureHowToFindSaga(SagaPropertyMapper<EventFromOtherSaga2Data> mapper)
                {
                    mapper.ConfigureMapping<SomethingHappenedEvent>(m => m.DataId).ToSaga(s => s.DataId);
                }
            }
        }

        [Serializable]
        public class StartSaga : ICommand
        {
            public Guid DataId { get; set; }
        }

        public interface SomethingHappenedEvent : BaseEvent
        {
          
        }

        public interface BaseEvent : IEvent
        {
            Guid DataId { get; set; }
        }
    }
}

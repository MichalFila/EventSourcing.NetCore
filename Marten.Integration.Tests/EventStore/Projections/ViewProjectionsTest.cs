using System;
using System.Collections.Generic;
using System.Linq;
using Marten.Events.Projections;
using Marten.Integration.Tests.TestsInfrasructure;
using SharpTestsEx;
using Xunit;

namespace Marten.Integration.Tests.EventStore.Projections
{
    public class ViewProjectionsTest: MartenTest
    {
        public interface IIssueEvent
        {
            Guid IssueId { get; set; }
        }

        public class IssueCreated: IIssueEvent
        {
            public Guid IssueId { get; set; }
            public string Description { get; set; }
        }

        public class IssueUpdated: IIssueEvent
        {
            public Guid IssueId { get; set; }
            public string Description { get; set; }
        }

        public class Issue
        {
            public Guid IssueId { get; set; }

            public string Description { get; set; }
        }

        public class IssuesList
        {
            public Guid Id { get; set; }
            public List<Issue> List { get; private set; }

            public IssuesList()
            {
                List = new List<Issue>();
            }

            public void Apply(IssueCreated @event)
            {
                List.Add(new Issue { IssueId = @event.IssueId, Description = @event.Description });
            }

            public void Apply(IssueUpdated @event)
            {
                var issue = List.Single(t => t.IssueId == @event.IssueId);

                issue.Description = @event.Description;
            }
        }

        public class IssueDescriptionView
        {
            public Guid Id { get; set; }
            public IDictionary<Guid, string> Descriptions { get; } = new Dictionary<Guid, string>();

            public void ApplyEvent(IssueCreated @event)
            {
                Descriptions.Add(@event.IssueId, @event.Description);
            }

            public void ApplyEvent(IssueUpdated @event)
            {
                Descriptions[@event.IssueId] = @event.Description;
            }
        }

        public class IssuesListViewProjection: ViewProjection<IssueDescriptionView, Guid>
        {
            public IssuesListViewProjection()
            {
                ProjectEventToSingleRecord<IssueCreated>((view, @event) => view.ApplyEvent(@event));
                ProjectEventToSingleRecord<IssueUpdated>((view, @event) => view.ApplyEvent(@event));
            }

            private ViewProjection<IssueDescriptionView, Guid> ProjectEventToSingleRecord<TEvent>(Action<IssueDescriptionView, TEvent> handler) where TEvent : class
            {
                return ProjectEvent((documentSession, ev) => FindIdOfRecord(documentSession) ?? Guid.NewGuid(), handler);
            }

            private Guid? FindIdOfRecord(IDocumentSession documentSession)
            {
                return documentSession.Query<IssueDescriptionView>()
                                   .Select(t => (Guid?)t.Id).SingleOrDefault();
            }
        }

        protected override IDocumentSession CreateSession(Action<StoreOptions> setStoreOptions)
        {
            var store = DocumentStore.For(options =>
            {
                options.Connection(Settings.ConnectionString);
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.DatabaseSchemaName = SchemaName;
                options.Events.DatabaseSchemaName = SchemaName;

                //It's needed to manualy set that inline aggegation should be applied
                options.Events.InlineProjections.AggregateStreamsWith<IssuesList>();
                options.Events.InlineProjections.Add(new IssuesListViewProjection());
            });

            return store.OpenSession();
        }

        [Fact]
        public void GivenEvents_WhenInlineTransformationIsApplied_ThenReturnsSameNumberOfTransformedItems()
        {
            var issue1Id = Guid.NewGuid();
            var issue2Id = Guid.NewGuid();

            var events = new IIssueEvent[]
            {
                new IssueCreated {IssueId = issue1Id, Description = "Description 1"},
                new IssueUpdated {IssueId = issue1Id, Description = "Description 1 New"},
                new IssueCreated {IssueId = issue2Id, Description = "Description 2"},
                new IssueUpdated {IssueId = issue1Id, Description = "Description 1 Super New"},
                new IssueUpdated {IssueId = issue2Id, Description = "Description 2 New"},
            };

            //1. Create events
            var streamId = EventStore.StartStream<IssuesList>(events).Id;

            Session.SaveChanges();

            //2. Get live agregation
            var issuesListFromLiveAggregation = EventStore.AggregateStream<IssuesList>(streamId);

            //3. Get inline aggregation
            var issuesListFromInlineAggregation = Session.Load<IssuesList>(streamId);

            var projection = Session.Query<IssueDescriptionView>().FirstOrDefault();

            issuesListFromLiveAggregation.Should().Not.Be.Null();
            issuesListFromInlineAggregation.Should().Not.Be.Null();

            issuesListFromLiveAggregation.List.Count.Should().Be.EqualTo(2);
            issuesListFromLiveAggregation.List.Count.Should().Be.EqualTo(issuesListFromInlineAggregation.List.Count);
        }
    }
}

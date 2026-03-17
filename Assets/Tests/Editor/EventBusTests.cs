using Events;
using NUnit.Framework;

namespace Tests.Editor {
    public class EventBusTests {
        private sealed class RootEvent : GameEvent { }
        private sealed class ChildEvent : GameEvent { }
        private sealed class CounterEvent : GameEvent { }

        [SetUp]
        public void SetUp() {
            EventBus.Clear();
            EventBus.SetLoggingEnabled(false);
            EventBus.SetFailureCaptureEnabled(false);
            EventBus.SetFailureFileLogging(false);
            EventBus.SetFailureFailFastEnabled(false);
        }

        [TearDown]
        public void TearDown() {
            EventBus.Clear();
        }

        [Test]
        public void Publish_InvokesSubscriber() {
            var callCount = 0;

            EventBus.Subscribe<CounterEvent>(_ => callCount++);
            EventBus.Publish(new CounterEvent());

            Assert.That(callCount, Is.EqualTo(1));
        }

        [Test]
        public void Subscribe_DuplicateHandler_InvokesOnlyOnce() {
            var callCount = 0;
            System.Action<CounterEvent> handler = _ => callCount++;

            EventBus.Subscribe(handler);
            EventBus.Subscribe(handler);
            EventBus.Publish(new CounterEvent());

            Assert.That(callCount, Is.EqualTo(1));
        }

        [Test]
        public void UnsubscribeAll_RemovesSubscriberHandlers() {
            var callCount = 0;
            var subscriber = new object();

            // Wrap handlers to ensure Target points at subscriber.
            System.Action<CounterEvent> boundA = evt => {
                _ = subscriber;
                HandlerA();
            };

            EventBus.Subscribe(boundA);
            EventBus.Subscribe((System.Action<RootEvent>)BoundB);
            EventBus.UnsubscribeAll(boundA.Target);

            EventBus.Publish(new CounterEvent());
            EventBus.Publish(new RootEvent());

            Assert.That(callCount, Is.EqualTo(0));
            return;

            void HandlerA() => callCount++;
            
            void HandlerB() => callCount++;
            
            void BoundB(RootEvent evt) {
                _ = subscriber;
                HandlerB();
            }
        }

        [Test]
        public void NestedPublish_AssignsParentCorrelationAndDepth() {
            RootEvent observedRoot = null;
            ChildEvent observedChild = null;

            EventBus.Subscribe<RootEvent>(rootEvent => {
                observedRoot = rootEvent;
                EventBus.Publish(new ChildEvent());
            });
            EventBus.Subscribe<ChildEvent>(childEvent => { observedChild = childEvent; });

            EventBus.Publish(new RootEvent());

            Assert.That(observedRoot, Is.Not.Null);
            Assert.That(observedRoot.CorrelationId, Is.Not.Empty);
            Assert.That(observedRoot.ParentCorrelationId, Is.EqualTo(string.Empty));
            Assert.That(observedRoot.CorrelationDepth, Is.EqualTo(1));

            Assert.That(observedChild, Is.Not.Null);
            Assert.That(observedChild.CorrelationId, Is.Not.Empty);
            Assert.That(observedChild.ParentCorrelationId, Is.EqualTo(observedRoot.CorrelationId));
            Assert.That(observedChild.CorrelationDepth, Is.EqualTo(2));
        }
    }
}

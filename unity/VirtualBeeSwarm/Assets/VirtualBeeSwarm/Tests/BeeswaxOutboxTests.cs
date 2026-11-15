using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Beeswax;
using NUnit.Framework;
using UnityEngine;

namespace VirtualBeeSwarm.Tests
{
    public class BeeswaxOutboxTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _root;
        BeeswaxBus _bus;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Outbox test");
            _root.SetActive(false);
            _bus = _root.AddComponent<BeeswaxBus>();
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_root);

        [Test]
        public void ChoicesStayQueuedUntilWelcomeAndOnlyReplayForTheirOwnGroup()
        {
            Welcome("group-a");
            Assert.That(_bus.SendChoiceMade("feed", "store_honey"), Is.False);
            Invoke("HandleOpened");
            PlayWelcomeWithoutGroup();
            Assert.That(QueueCount, Is.EqualTo(1));

            Welcome("group-a");
            Assert.That(Flush("group-a", Time.realtimeSinceStartup), Has.Count.EqualTo(1));
            Assert.That(QueueCount, Is.Zero);

            Assert.That(_bus.SendChoiceMade("feed", "store_honey"), Is.False);
            Invoke("HandleOpened");
            Welcome("group-b");
            Assert.That(QueueCount, Is.Zero);
            Assert.That(Flush("group-b", Time.realtimeSinceStartup), Is.Empty);
        }

        [Test]
        public void SameIdResetAndOutboxExpiryDropQueuedChoices()
        {
            Assert.That(_bus.SendChoiceMade("feed", "store_honey"), Is.False);
            Assert.That(QueueCount, Is.Zero, "unassigned choices have only a local outcome");
            Welcome("group-a");
            Assert.That(_bus.SendChoiceMade("feed", "store_honey"), Is.False);
            Invoke("HandleJson", "{\"type\":\"group.reset\",\"group_id\":\"group-a\"}", false);
            Assert.That(QueueCount, Is.Zero);

            Assert.That(_bus.SendChoiceMade("feed", "store_honey"), Is.False);
            Assert.That(Flush("group-a", Time.realtimeSinceStartup + 11f), Is.Empty);
            Assert.That(QueueCount, Is.Zero);
        }

        int QueueCount => ((IList)typeof(BeeswaxBus).GetField("_outbox", PrivateInstance).GetValue(_bus)).Count;

        void Welcome(string group) =>
            Invoke("HandleJson", "{\"type\":\"welcome\",\"group_id\":\"" + group + "\"}", false);

        void PlayWelcomeWithoutGroup() => Invoke("HandleJson", "{\"type\":\"welcome\"}", false);

        List<string> Flush(string group, float now)
        {
            var sent = new List<string>();
            Func<string, bool> send = json => { sent.Add(json); return true; };
            Invoke("FlushOutbox", group, now, send);
            return sent;
        }

        void Invoke(string method, params object[] args) =>
            typeof(BeeswaxBus).GetMethod(method, PrivateInstance).Invoke(_bus, args);
    }
}

using NUnit.Framework;

namespace VirtualBeeSwarm.Ask.Tests
{
    /// <summary>The ASK BEESWAX card's state machine (AskFlow), driven with plain times: open, listen, think, answer, idle, backstops.</summary>
    public class AskFlowTests
    {
        static bool Has(AskCue c, AskCue f) => (c & f) == f;

        static AskFlow Opened()
        {
            var f = new AskFlow();
            f.Open();
            return f;
        }

        [Test]
        public void OpenListenThinkAnswerIdle()
        {
            var f = Opened();
            Assert.AreEqual(AskState.Idle, f.State);
            Assert.AreEqual(AskFlow.StatusIdle, f.Status);

            var c = f.TalkStarted(1f);
            Assert.AreEqual(AskState.Listening, f.State);
            Assert.IsTrue(Has(c, AskCue.StopVoice), "barge-in stops Beeswax");
            Assert.AreEqual(AskFlow.StatusListening, f.Status);

            c = f.TalkEnded(2f);
            Assert.AreEqual(AskState.Thinking, f.State);
            Assert.IsTrue(Has(c, AskCue.Tilt | AskCue.BuzzOn));
            Assert.IsTrue(f.Status.StartsWith("Checking"));

            f.Tick(2.1f, true, false);
            c = f.Transcript("why do bees dance", true, 2.6f);
            Assert.AreEqual("You asked: \"why do bees dance\"", f.Status);
            c = f.Tick(4.6f, true, false);
            Assert.IsTrue(Has(c, AskCue.Tilt), "tilt again every 2.5 s");
            Assert.AreEqual("You asked: \"why do bees dance\"", f.Status, "the dots never overwrite what was asked");

            c = f.LineStarted(AskLines.FillerId, 3f);
            Assert.IsTrue(Has(c, AskCue.BuzzOff));
            Assert.AreEqual(AskState.Thinking, f.State, "the filler is not the answer");

            c = f.LineStarted("", 5f);
            Assert.AreEqual(AskState.Answering, f.State);
            Assert.IsTrue(Has(c, AskCue.Nod));
            c = f.Tick(5.1f, false, true);
            Assert.AreEqual(AskState.Answering, f.State);
            c = f.Tick(9f, false, false);
            Assert.AreEqual(AskState.Idle, f.State);
            Assert.IsTrue(Has(c, AskCue.StampApproved | AskCue.Minutes));
            Assert.AreEqual(1, f.Minutes);
            Assert.AreEqual("MINUTES: 1 QUESTION NOTED", AskFlow.MinutesText(f.Minutes));
        }

        [Test]
        public void RefusalShakesStampsGrievanceAndDoesNotCount()
        {
            var f = Opened();
            f.TalkStarted(0f);
            f.TalkEnded(1f);
            f.Tick(1.1f, true, false);
            var c = f.LineStarted(AskLines.RefuseId, 3f);
            Assert.IsTrue(Has(c, AskCue.Shake | AskCue.StampGrievance));
            c = f.Tick(8f, false, false);
            Assert.IsFalse(Has(c, AskCue.StampApproved));
            Assert.AreEqual(0, f.Minutes);
        }

        [Test]
        public void ShortPressGivesTheHint()
        {
            var f = Opened();
            f.TalkStarted(0f);
            f.TalkEnded(0.2f);
            var c = f.Tick(0.5f, false, false);
            Assert.AreEqual(AskState.Thinking, f.State);
            c = f.Tick(0.8f, false, false);
            Assert.AreEqual(AskState.Idle, f.State);
            Assert.AreEqual(AskFlow.StatusShort, f.Status);
            Assert.IsTrue(Has(c, AskCue.BuzzOff));
        }

        [Test]
        public void NonAnswersAndThanksGetNoStampAndUnrelatedLinesAreNotTheReply()
        {
            var f = Opened();
            f.TalkStarted(0f);
            f.TalkEnded(1f);
            f.Tick(1.2f, true, false);
            Assert.AreEqual(AskCue.None, f.LineStarted("room_map_tour", 1.5f, reply: false), "a scripted line is not the reply");
            Assert.IsTrue(f.ExpectingReply);
            f.LineStarted("", 2f, reply: true, counts: false); // the Hive's off-list / stall / not-heard text
            Assert.AreEqual(AskState.Answering, f.State);
            var end = f.Tick(5f, false, false);
            Assert.AreEqual(0, f.Minutes);
            Assert.IsFalse(Has(end, AskCue.StampApproved));

            f.TalkStarted(6f);
            f.TalkEnded(7f);
            f.Tick(7.2f, true, false);
            f.Intent("ack");
            f.LineStarted("", 7.5f);
            end = f.Tick(10f, false, false);
            Assert.AreEqual(0, f.Minutes, "thanks is not a question");

            Assert.IsTrue(AskLines.IsNonAnswer("", AskLines.Stall.caption));
            Assert.IsTrue(AskLines.IsNonAnswer("", AskLines.OffList.caption));
            Assert.IsFalse(AskLines.IsNonAnswer("", AskLines.Bananas.caption));
        }

        [Test]
        public void WatchdogSaysStallOnceAndItDoesNotCount()
        {
            var f = Opened();
            f.WatchdogSeconds = 7f;
            f.TalkStarted(0f);
            f.TalkEnded(1f);
            f.Tick(1.2f, true, false);
            Assert.IsFalse(Has(f.Tick(7.9f, true, false), AskCue.SayStall));
            var c = f.Tick(8.1f, false, false);
            Assert.IsTrue(Has(c, AskCue.SayStall));
            Assert.IsFalse(Has(f.Tick(8.2f, false, false), AskCue.SayStall), "once");
            f.LineStarted("ask_stall", 8.2f);
            var end = f.Tick(12f, false, false);
            Assert.AreEqual(AskState.Idle, f.State);
            Assert.AreEqual(0, f.Minutes);
            Assert.IsFalse(Has(end, AskCue.StampApproved));
        }

        [Test]
        public void FastTranscriptKeepsWaitingEvenWhenPendingWasNeverSampled()
        {
            var flow = Opened();
            flow.TalkStarted(0f);
            flow.TalkEnded(1f);
            flow.Transcript("why do bees dance", true, 1.1f);
            flow.Intent("ask");
            flow.Tick(2f, false, false);
            Assert.IsTrue(flow.ExpectingReply);
            Assert.AreEqual(AskFlow.Asked("why do bees dance"), flow.Status);
        }

        [Test]
        public void MissingMicrophoneExplainsHowToContinue()
        {
            var flow = Opened();
            Assert.IsTrue(Has(flow.MicrophoneUnavailable(false), AskCue.Status));
            Assert.AreEqual(AskFlow.StatusMicPermission, flow.Status);
            Assert.IsTrue(Has(flow.MicrophoneUnavailable(true), AskCue.Status));
            Assert.AreEqual(AskFlow.StatusMicUnavailable, flow.Status);
            flow.TalkStarted(0f);
            Assert.AreEqual(AskFlow.StatusListening, flow.Status);
        }

        [Test]
        public void EmptyTranscriptWaitsForTheHiveThenSaysNotHeard()
        {
            var f = Opened();
            f.TalkStarted(0f);
            f.TalkEnded(1f);
            f.Tick(1.2f, true, false);
            f.Transcript("", true, 2f);
            Assert.AreEqual(AskFlow.StatusNotHeard, f.Status);
            Assert.IsFalse(Has(f.Tick(3.5f, false, false), AskCue.SayNotHeard));
            Assert.IsTrue(Has(f.Tick(4.1f, false, false), AskCue.SayNotHeard));
        }

        [Test]
        public void EmptyTranscriptAndTheHiveSpeaksFirst()
        {
            var f = Opened();
            f.TalkStarted(0f);
            f.TalkEnded(1f);
            f.Tick(1.2f, true, false);
            f.Transcript("", true, 2f);
            f.LineStarted("", 2.5f); // the Hive's ask_not_heard
            Assert.IsFalse(Has(f.Tick(4.6f, false, true), AskCue.SayNotHeard));
            f.Tick(6f, false, false);
            Assert.AreEqual(0, f.Minutes);
        }

        [Test]
        public void EchoIntentIsNotAStall()
        {
            var f = Opened();
            f.TalkStarted(0f);
            f.TalkEnded(1f);
            f.Tick(1.2f, true, false);
            f.Intent("unknown");
            var c = f.Tick(2.5f, false, false);
            Assert.AreEqual(AskState.Idle, f.State);
            Assert.IsFalse(Has(f.Tick(9f, false, false), AskCue.SayStall));
        }

        [Test]
        public void ChipAndOfflinePress()
        {
            var f = Opened();
            var c = f.Chip("Why bananas?", 1f);
            Assert.IsTrue(Has(c, AskCue.StopVoice));
            Assert.AreEqual("You asked: \"Why bananas?\"", f.Status);
            f.LineStarted("ask_bananas", 1f);
            f.Tick(1.6f, false, false); // within a frame or two the line is speaking; a quiet frame after 0.3 s ends it
            Assert.AreEqual(1, f.Minutes);

            c = f.OfflinePress(3f);
            Assert.IsTrue(Has(c, AskCue.SayOffline));
            Assert.AreEqual(AskFlow.StatusOffline, f.Status);
            f.LineStarted("ask_offline", 3f);
            f.Tick(4f, false, false);
            Assert.AreEqual(1, f.Minutes, "the offline line is not a question");
        }

        [Test]
        public void ClosedCardStillThinksOnAwaitingReply()
        {
            var f = new AskFlow();
            var c = f.Tick(1f, true, false);
            Assert.IsTrue(Has(c, AskCue.Tilt | AskCue.BuzzOn));
            Assert.AreEqual(AskCue.None, f.Tick(1.1f, true, false) & AskCue.BuzzOn);
            c = f.LineStarted("room_whatif_fire", 2f);
            Assert.IsTrue(Has(c, AskCue.BuzzOff));
            Assert.IsFalse(f.Thinking);
            Assert.AreEqual(AskCue.None, f.Tick(2.1f, false, true));
            Assert.IsFalse(f.IsOpen);
        }

        [Test]
        public void BuzzStopsAfterSixSeconds()
        {
            var f = Opened();
            f.TalkStarted(0f);
            f.TalkEnded(1f);
            f.Tick(1.1f, true, false);
            Assert.IsTrue(f.Buzzing);
            Assert.IsTrue(Has(f.Tick(7.2f, true, false), AskCue.BuzzOff));
            Assert.IsFalse(f.Buzzing);
        }

        [Test]
        public void CloseAndResetClearMinutes()
        {
            var f = Opened();
            f.Chip("Why bananas?", 0f);
            f.LineStarted("ask_bananas", 0f);
            f.Tick(1f, false, false);
            Assert.AreEqual(1, f.Minutes);
            f.Close();
            Assert.AreEqual(AskState.Closed, f.State);
            Assert.AreEqual(1, f.Minutes, "closing keeps the count for the group");
            Assert.IsTrue(Has(f.Close(resetMinutes: true), AskCue.Minutes));
            Assert.AreEqual(0, f.Minutes);
        }
    }
}

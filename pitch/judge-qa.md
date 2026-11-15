# Judge Q&A

> Rules: answer in two or three sentences, then stop. If we don't know, say "we don't know yet" or "we didn't test that." Never round up.
> **[LIVE]** / **[CLIPS]** = pick the answer that matches the Sat 11 AM Orbis gate. **[GLASSES]** = only if the glasses landed. **[HORNET]** = only if the hornet hum shipped.
> Anyone can answer, but by default: Visko/AI/backend questions → Rome. Unity/hands/swarm → TODO (U1/U2). Art/sound → TODO (M). Bees/facts/education → whoever has read `devpost.md` → Fact sources.
> Names: say HONEYMOON, Beeswax, and "the meadow window". Nobody needs the rocket's name.

---

## The window

**1. Is the window really live?**
**[LIVE]** Yes. That's Visko's Orbis world model running on Reactor's GPUs right now, and when you made a choice or said "fire," we changed its prompt mid-stream. You can see the stream state and frame rate on the Beat Board. (Turn the phone toward them.)
**[CLIPS]** Not today. The live stream didn't pass our 11 AM test (TODO: real reason), so the window is playing Orbis clips we generated for each scene. The live code path is in the repo (`hive/orbis-relay/`).

**2. So it's a video on a flat quad?**
Yes, on purpose. It's a window, and the 3D things cross out of it: the embers, the swarm, the rocket. Orbis makes 832×480, so we keep the window window-sized; blown up to a wall it would look soft.

**3. Why not just play a video?**
**[LIVE]** The window never cuts. Orbis morphs from the frame it's showing into the fire, and it starts when you say the words, not on a timer. Our recorded clips are the backup, and they cut.
**[CLIPS]** Today it is a video, and you can see it cut between scenes. Live, it morphs from whatever frame it's showing, starting when you say the words.

**4. Why not run Orbis on the Quest? Reactor has an Android SDK.**
The Reactor key stays on our laptop instead of on a headset that judges pass around, and the operator can watch the stream and switch to clips from the Beat Board. TODO Rome: confirm these are the real reasons, or give the real one.

## The game

**5. Is Beeswax a chatbot?**
Beeswax follows a prerecorded script through the ride. On a connected run, Ask Beeswax can answer a short question after the finale, and the game can use voice input to change the meadow window.

**6. Can I fail? Does what I pick change anything?**
You can't lose the hive yet. Beeswax goes to the Moon whatever you do, because that's the joke. Your picks change what the window shows and what he says, and the Board counts wrong picks as grievances. The headset and Board show the time to finish the four repairs.

**7. Why mixed reality and not a phone?**
Your hands are free. You lift the hive from the meadow floor, pump the smoker with the lever, and see embers drift into the room around you.

**8. How do I know the hive is light with no haptics?**
You see it instead of feeling it: the hive comes up with no resistance and Beeswax says "Light, right? That's bad." TODO: only true once the build does this. Beekeepers call this hefting.

**9. Are your bees Japanese honeybees?** *[HORNET]*
No, it's a regular Western honeybee hive, the kind in US backyards. The hornet ball is the Japanese honeybee's move, and Western honeybees mostly can't pull it off. That's why Beeswax says "My cousins in Japan do this. Never tried it."

**10. Is the hornet thing real?** *[HORNET]*
Yes. Japanese honeybees mob a giant hornet into a ball of more than 500 bees and vibrate their flight muscles until it hits about 46 to 47 °C, and the hornet dies while the bees survive (Ono et al., *Nature* 1995). Later work found the CO2 building up inside the ball helps kill it too. In our game, the headset mic does pitch detection on your hum, and that heats the thermometer.

**11. Why bees and the Moon?**
The theme is *Take Me To The Moon*, so we made the bees the ones who want to leave. No flowers, no bees. That's the Moon. US beekeepers lost an estimated 55.6% of their managed colonies in 2024–25, the worst year since the survey started in 2010, and the "better" year after that was still 39.9%.

**12. Are the facts true?**
Yes, every one is sourced on our Devpost. The only one that isn't science is "don't eat a banana near my house," and that's a joke.

**13. Did you test it with real people?**
TODO: fill in after the Sat 6 PM run with strangers. Say how many people, what broke, what they laughed at, and how many picked the honey. If we didn't get to it, say "only with our team."

## The AI and the build

**14. What did the AI actually do, and what's scripted?**
Scripted: the stations, their outcomes, the score, and Beeswax's lines, pre-recorded in an ElevenLabs voice. AI: speech-to-text on what you said, Claude picking what you meant from a fixed list when a keyword check doesn't catch it, and Orbis generating the window's world. Unity only runs things from the list, so the model can't break the game.

**15. Isn't it less impressive that the AI only picks from a list?**
We did that on purpose. A live demo with a judge in a headset can't afford a model inventing an event Unity doesn't know about, so the AI decides what you meant and Unity decides what happens. TODO, only once it works: a what-if we never planned for still changes the window, because Claude writes a safe Orbis prompt for it.

**16. Did an AI write your code?**
TODO: decide the team answer tonight and confirm it before anyone says it. Draft from what PLAN.md's log shows: "A lot of it, yes. Rome ran AI coding agents (Claude Code) that wrote most of the Hive server, the Orbis relay and the Unity client scripts, and drafted our docs; Rome reviewed and tested them. The humans did TODO: the game design, Beeswax's script edits, the 3D art and the Unity scenes (who did which?)." Also TODO: say where the HONEYMOON idea came from (Adityash's hive pitch + an AI-assisted brainstorm Rome ran). The coding agents are listed with our other AI services.

**17. What did you build in 36 hours, and what existed before?**
All of our code started here: the repo was created Friday night. That's the Unity app, the Hive server and Beat Board, the Orbis relay, the Unity client scripts, Beeswax's voice and lines, and the Orbis prompts. Before: Unity, the Meta XR SDK, the Anthropic/ElevenLabs/Reactor SDKs, and open-source pieces like NativeWebSocket and faster-whisper. TODO: list any 3D models or sounds from an asset store or library.

**18. What happens if the Wi-Fi dies?**
The scripted ride can still finish. For the what-if, release push-to-talk once to trigger the fire and again to put it out, or let the backup timers run. Prerecorded Beeswax lines and window clips play locally. Speech recognition, live answers, and live Orbis require a connection.

**19. What does one run cost?**
Orbis bills about $0.58 per connected minute, so about $2.30 for the four minutes, more with warm-up time between groups, plus a few cents of speech-to-text, Claude and voice. TODO: real numbers from the dashboards. In clips mode it costs almost nothing, which matters for a classroom.

**20. What was the hardest part?**
Getting a real-time world model into a headset. Orbis has no Unity SDK, so we wrote a relay that turns the WebRTC stream into JPEG frames over a WebSocket, then designed around the 2 to 4 second morph: Beeswax talks during every wait, and the embers are Unity, so they're instant. TODO: swap in Saturday's real hardest part if it was something else.

**21. What do the glasses do?**
**[GLASSES]** A second player looks at a real object, like the honey jar or the syrup bottle, and presses the button. Claude names it from the photo, and that becomes the choice in the headset. Beeswax also talks in their ear.
**[no glasses]** They didn't make it into this demo. The glasses app is in the repo and was meant to let a second player pick choices by looking at real props. We put the headset game first.

## Where it goes

**22. How would a school use this?**
A five-minute station in a science class: one student plays, the class watches the cast and yells what-ifs, and the teacher runs the Beat Board. The kit would be the app, a printed mat, the real props and a one-page fact sheet with sources. We haven't tried it with a class, so that's a plan, not a result.

**23. What's next?**
More hives, each with a different problem (farmland and pesticide drift, a city rooftop, a desert), and the classroom kit. TODO: only what the team actually wants to keep building after this weekend.

---

## Questions we hope they don't ask (answer anyway)

- **"Which parts are pre-recorded?"** Every line Beeswax says on his own. The window clips, if we're in clips mode. Say it before they ask.
- **"Why isn't your headset in ManageXR?"** (Only if asked. Don't volunteer it to the ManageXR folks.) Enrolling needs a factory reset of our only headset, and there's a known Meta issue with scene-aware apps on managed headsets that ManageXR tracks. For a classroom we'd go through Meta's Horizon managed app store, which is ManageXR's documented route.
- **"Is the score real?"** Yes, it's a timer from when the clock starts to when the door opens. TODO: confirm.
- **"Does the hornet ball work for your bees?"** See 9.

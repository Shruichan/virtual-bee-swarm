# Rules for AI agents (Claude Code, Codex, Cursor, …)

This is a 36-hour hackathon repo. Several people, and several agents, commit to it at the same time.
**[PLAN.md](PLAN.md) is the team's shared plan and live chat log.** Treat it as the source of truth.

1. **Start of every session or task:** `git pull --rebase`, then read PLAN.md in full.
2. **Before you start a task:** claim it in PLAN.md → *Who's doing what* (owner = your human's name), add a Live log line, then commit and push PLAN.md right away so nobody duplicates the work.
3. **Keep PLAN.md current, often.** Whenever you change the plan, make a decision, finish something, hit a blocker, or need something from someone, append a Live log line *and* fix any outline section that's now wrong. Small, frequent updates beat one big one.
4. **Log format:** `- YYYY-MM-DD HH:MM PT · Name (agent) · message`. Append at the very end of the file. Never edit or delete other people's log lines. Address someone with "→ Name".
5. **Git hygiene:** Use plain branch names and keep game work on the shared branch listed in PLAN.md. `git pull --rebase` before every commit. Commit PLAN.md changes on their own, then push immediately. **Never** force-push, rewrite history, or `git reset --hard` over other people's work. The union merge for PLAN.md keeps both sides' lines. After a pull, check for duplicates and tidy them.
6. **Secrets:** this repo is public. Never commit API keys or tokens, and never paste them into PLAN.md. Keys go in a `.env` file (git ignores it) on the machine that needs them.
7. **Stay in your lane:** keep one writer per path and follow the ownership rules in [PLAN.md](PLAN.md#house-rules). Coordinate in PLAN.md before editing claimed code or assets.
8. **Scope:** build for the ~5-minute scripted demo described in PLAN.md. Pre-made, polished beats beat open-ended systems.
9. **Unity scene:** `VirtualBeeSwarm.unity` is generated. Make changes in the scene builder, tuning, or art prefabs, then rebuild. [Unity setup and smoke tests](unity/VirtualBeeSwarm/README.md)

# NativeWebSocket (vendored)

Embedded copy of [endel/NativeWebSocket](https://github.com/endel/NativeWebSocket), branch `upm` at commit
`ea014c9ae534d56111962d96f89f8a046e302dc9` (2026-03-05, package version 1.1.6). That is exactly what
`packages-lock.json` had locked for the git dependency `https://github.com/endel/NativeWebSocket.git#upm`.

It's vendored (PLAN.md S1.10) so builds don't depend on GitHub being reachable. The files are unchanged from that
commit, minus one orphan `WebSocketExample.meta` (its folder isn't in the commit, so Unity would delete it on every
import). `LICENSE.md` is the project's Apache 2.0 licence, taken from the repo's master branch.

To update it, replace this folder with the `upm` branch at the new commit and keep this file and `LICENSE.md`. Don't
add the git URL back to `Packages/manifest.json`: the embedded package wins over it anyway.

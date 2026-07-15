# Vendored reference — survival-test wire contract

These files are **verbatim mirrors** of the survival-test client's protocol
documentation and wire contract, copied here so the MCGalaxy server side has the
spec on hand without a second checkout. They are **reference only** — the
authoritative copies live in the ClassiCube fork and win any disagreement.

| File | Upstream path |
|---|---|
| `survival-handshake.md` | `doc/survival-handshake.md` |
| `networking-plan.md` | `doc/networking-plan.md` |
| `SurvivalNet.h` | `src/SurvivalNet.h` — the wire contract the server is built against |

**Source:** `UmbreoClaw/ClassiCube`, branch `survival-test`
(https://github.com/UmbreoClaw/ClassiCube/tree/survival-test)
**Pulled:** 2026-07-15
**Licence:** ClassiCube is BSD-3-Clause (see the header in `SurvivalNet.h`);
these docs are reproduced under the same terms.

## Keeping them fresh

They do **not** auto-update. When the upstream contract changes, re-pull:

```sh
BASE=https://raw.githubusercontent.com/UmbreoClaw/ClassiCube/survival-test
curl -sSL $BASE/doc/survival-handshake.md -o doc/survival-support/reference/survival-handshake.md
curl -sSL $BASE/doc/networking-plan.md    -o doc/survival-support/reference/networking-plan.md
curl -sSL $BASE/src/SurvivalNet.h         -o doc/survival-support/reference/SurvivalNet.h
```

If `SurvivalNet.h` changes, reconcile `MCGalaxy/Network/SurvivalNet.cs` with it
and bump the `SurvivalTest` CPE extension version on both sides (see
`../roadmap.md`).

# ChunkPilot reachability probe

An optional, stateless Cloudflare Worker that answers one question for ChunkPilot: *can a TCP
connection made from outside this home network reach the address this request arrived from, on this
port?*

It is not part of the ChunkPilot desktop application, is never contacted unless a person presses
**Check from outside**, and ChunkPilot is fully usable without it.

The security invariant, the API contract, the privacy model and the manual deployment steps live in
[`docs/EXTERNAL-REACHABILITY-PROBE.md`](../../docs/EXTERNAL-REACHABILITY-PROBE.md).

```bash
npm install
npm test
npm run typecheck
```

Tests need no Cloudflare account and open no sockets: the TCP connector is injected, and every test
uses a recording fake.

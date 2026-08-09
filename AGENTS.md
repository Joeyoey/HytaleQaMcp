# Hytale QA MCP agent rules

- This repository is offline-only QA infrastructure. Never weaken the OFFLINE, loopback, one-client, pinned PID/path/hash/HWND, or authenticated observer predicates.
- Never automate OAuth, credentials, UAC, authenticated servers, public servers, packet injection, teleportation, direct damage, or arbitrary caller-supplied world coordinates.
- Keep `Blocked`, `Untested`, `InvalidEvidence`, and `AbortedSafety` truthful.
- Product-specific worlds, mods, scenarios, lifecycle scripts, and observer state belong in project profiles/adapters, not this core.
- Use `HYTALE_QA_PROFILE` for the project profile and `HYTALE_QA_HOME` for this repository.
- Preserve evidence as append-only, hash-manifested artifacts beneath the configured evidence root.


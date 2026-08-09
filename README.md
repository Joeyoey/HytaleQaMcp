# Hytale QA MCP

Portable, fail-closed, offline-only Hytale client QA for Windows. The repository contains the reusable MCP server, deterministic scenario runner, proof engine, native input/capture worker, and evidence pipeline. A game/mod project supplies a small JSON profile, lifecycle scripts, scenarios, and a read-only observer adapter.

## Portability

The core has no project-name, world-name, Docker-port, checkout-location, or Windows-user assumptions. Configure a project with `.hytale-qa/profile.json` and launch with:

```powershell
.\scripts\Start-HytaleQaMcp.ps1 -Profile C:\path\to\project\.hytale-qa\profile.json -Build
```

Paths may be absolute or profile-relative and support `{profileDir}`, `{projectRoot}`, `{toolRoot}`, `{hytaleLatest}`, `{appData}`, `{localAppData}`, and normal environment-variable expansion. The profile also pins the isolated Docker endpoint, project, container, and network names; these are safety identities, not defaults hidden in the core. `HYTALE_QA_HOME`, `HYTALE_QA_PROFILE`, and `HYTALE_QA_WORKER_PATH` are the canonical environment variables.

The native worker currently requires Windows 10 build 20348 or newer and .NET 9. “Any computer” therefore means any supported Windows computer with Hytale installed, the project adapter available, and a genuinely launcher-owned offline world. Other operating systems can validate catalogs but cannot perform Windows input/capture.

## Project adapter contract

A project owns:

- offline launcher/server lifecycle scripts referenced by the profile;
- scenario and suite catalogs;
- read-only authenticated observer state and target projection;
- a versioned observer-capability manifest declaring exact semantic inventory selectors, semantic UI operations/assertions, event types, hash subjects/fields, and objective visual assertions;
- its isolated world/mod staging policy.

The core owns every safety predicate, process lease, semantic input compiler, capture backend, evidence hash chain, MCP method, and run coordinator. Product adapters cannot bypass core safety.

See [profiles/example.profile.json](profiles/example.profile.json) and [profiles/profile.schema.json](profiles/profile.schema.json).

## Original worker design

This is a fail-closed Windows input and capture worker for an **already running, isolated, offline-only** Hytale client. It never launches Hytale and it refuses input until an allowlisted process has an exclusive lease.

## Safety model

Acquiring a process lease does not enable input. A separate `session.arm` transition requires a fresh matching offline-server proof containing auth mode, endpoint, Docker project/container/network, server hash, and observer nonce. Evidence mode and capabilities become immutable at that transition. Every input event then revalidates:

- offline attestation and an explicit IP loopback server endpoint;
- exactly one `HytaleClient.exe` process;
- PID, process creation time, full executable path, SHA-256 and HWND identity;
- HWND ownership and foreground focus.

An independent 100 ms watchdog revalidates focus, identity, and freshness of the offline observer heartbeat even when no input RPC arrives. The orchestrator must refresh `session.heartbeat` at least every two seconds with a newly observed matching offline proof. The worker releases every tracked key and mouse button when the heartbeat becomes stale, focus is lost, an operation fails, the lease is released, stdin closes, Ctrl+C is received, or the worker is disposed. A watchdog trip is fail-closed for that armed session. `SetForegroundWindow` is best-effort; input remains blocked until foreground ownership is independently observed.

Screen and audio capture are bound to the same immutable process identity. A screenshot validates HWND ownership before and after capture and is written through a same-directory temporary file. Audio records only the leased PID and its descendants, polls the offline proof/lease predicate every 50 ms, and destroys its temporary file on a stale proof, changed PID, changed executable hash/HWND, lease release, worker error, or explicit abort. There is deliberately no system-wide loopback fallback because another application could otherwise contaminate evidence.

No Hytale launch, memory access, DLL injection, packet injection, network control, authentication handling, direct damage or process termination exists in this worker.

## Offline observer boundary

The orchestrator reads the isolated server session nonce from `run/qa-offline/control/session.json`; new records store it encrypted with Windows CurrentUser DPAPI. It then validates the authoritative versioned heartbeat under `control/spool/events` and performs an authenticated `HEALTH` request over the bind-mounted file spool before treating the server as armed. The checks require:

- a fresh schema-v2 heartbeat with `OFFLINE`, valid packet evidence, the exact bridge boundary, matching nonce SHA-256, and a canonical HMAC;
- a live schema-v1 `HEALTH` response attesting `DOCKER_SHARED_VOLUME_SPOOL`;
- strictly increasing request sequences and unique request IDs, allocated through an OS-exclusive spool lock and nonce-HMAC durable sequence ledger so separate orchestrator instances cannot race or replay a lower counter;
- a `HYTALE-QA-SPOOL/2` request HMAC over the canonical request body using the DPAPI-protected session secret; the raw nonce is never written into request files;
- an immutable schema-v2 response whose request ID, sequence, echoed request SHA-256, payload SHA-256, and canonical response HMAC match exactly.

Only read-only `HEALTH` and `OBSERVE` commands are implemented. Requests time out without retrying under a new identity, preventing one logical request from being executed twice. `OBSERVE` is rejected when packet evidence is invalid, incomplete, or reports dropped facts.

## Build and test

```powershell
dotnet build .\Hytale.Qa.sln -c Release
dotnet test .\Hytale.Qa.sln -c Release --no-build
```

Optional MP4 encoding is disabled until `config/ffmpeg.allowlist.example.json` is copied to the ignored `config/ffmpeg.allowlist.local.json` and updated with an exact absolute executable path and SHA-256.

Run the line-oriented JSON-RPC 2.0 worker with a required confined artifact root and optional new trace path:

```powershell
dotnet run --project .\src\Hytale.Qa.Worker -c Release -- --artifact-root .\artifacts --trace .\artifacts\session.ndjson
```

The worker reads one JSON-RPC request per stdin line and emits exactly one response per stdout line. `WorkerRpcProcessClient` is its host controller; low-level methods remain private to that controller and are not exposed as raw MCP input tools.

Methods:

- `worker.capabilities`
- `lease.acquire`, `lease.state`, `lease.focus`, `lease.release`
- `session.arm`, `session.heartbeat`, `session.state`
- `safety.check`
- `input.key`, `input.mouseButton`, `input.mouseMoveRelative`, `input.pointerMoveClient`, `input.releaseAll`
- `capture.screenshot`
- `audio.start`, `audio.state`, `audio.stop`, `audio.abort`
- `rolling.start`, `rolling.state`, `rolling.stop`, `rolling.abort`

`lease.acquire` requires a `SessionSafetyPolicy`, process id and correlation id. Virtual keys use Win32 virtual-key values. Gameplay aiming is relative. Semantic UI pointer movement is normalized to the pinned client area and is accepted only from authenticated observer nodes inside the orchestrator; no MCP method accepts pointer coordinates.

`audio.start` accepts a lease id, correlation id and `.wav` destination. It returns a capture id. `audio.stop` requires that capture id and returns the finalized artifact. WAV output is 44.1 kHz stereo 16-bit PCM. If the target has no active render stream, Windows supplies silence; this is still a valid process-isolated observation, not proof that a cue played.

## Host controller and MCP surface

`WorkerControlService` requires launcher-owned process/world proof and an authenticated `HEALTH` exchange before starting the worker. After attachment, a 500 ms loop checks the fresh nonce-bound observer heartbeat plus every authenticated process/file pin. Each cycle has a one-second deadline. The worker independently rejects an unchanged/stale observation timestamp. Any stale proof, identity mismatch, deadline, PID/HWND/hash drift, or worker RPC failure releases tracked input and media capture best-effort, then terminates only the worker process tree.

The controller never launches or terminates Hytale. Physical attachment is available only through `qa_client_attach_launcher_singleplayer`; the legacy Docker `qa_client_attach` surface is removed because a dedicated-server heartbeat cannot prove that an unrelated retail client is connected to that endpoint. The worker independently requires exactly one `HytaleClient.exe` and pins its creation time, path, audited SHA-256, and HWND. Media and trace artifacts are confined to the persistent, session-scoped `run/qa-evidence/artifacts/<session-uuid>` tree; durable run reports live under `run/qa-evidence/runs`, outside ephemeral launcher control state.

The MCP exposes safety, focus, screenshot, process-audio, rolling capture, `qa_observe_sole_player`, release-all, and bounded observer-derived navigation/interaction/combat/UI actions. It never accepts caller-supplied player positions, targets, candidates, health, cooldowns, world coordinates, or UI coordinates. Gameplay actions compile server-authored targets into short W/A/S/D, jump, relative-mouse, and allowlisted button actions. UI actions resolve one enabled `semanticUi.nodes` entry, contain its normalized center to the pinned HWND client area, physically click, then require an authenticated observer revision and declared post-operation evidence. Missing, ambiguous, disabled, stale, or out-of-bounds nodes fail closed. No MCP tool exposes raw virtual keys, arbitrary mouse input, commands, packets, teleport, or direct damage.

`qa_scenario_run` executes through one durable serial lease. `qa_run_state`, `qa_run_cancel`, and `qa_run_result` expose the cursor, cancellation/teardown, scenario reports, canonical hashes, runtime pins, artifact gaps, and a SHA-256 artifact manifest; top-level terminal phases preserve Failed, Blocked, Untested, InvalidEvidence, and AbortedSafety instead of labeling them Completed. Before worker attachment, the runtime rejects unsupported capabilities and requires authenticated proof of the exact fixture player, seeds, snapshot, and loadout.

`qa_capabilities_audit` joins the scenario catalog to the project's observer-capability manifest. It reports supported steps per operation and the exact uninstrumented selectors, event types, hash subjects/fields, and visual assertions. Method presence alone never counts as coverage.

Inventory mutation adapters are player-facing: `inventory.equip`, `inventory.move`, `inventory.reroll`, `inventory.salvage`, and `inventory.recover` first resolve the exact current signed item from `nativeInventory`, select it through the visible semantic UI when necessary, and then click only the operation node published for that subject. Reroll is a preview-plus-commit sequence. Salvage requires the page's armed confirmation state. An operation passes only when `semanticUi.operationEvidence` or `nativeInventory.operationEvidence` proves every declared result field. A project may advertise only the operations and selectors it truly projects; unadvertised fixture setup remains Untested.

Suite manifests require `freshSessionPerScenario`. `qa_suite_run` consumes the first already-armed launcher-owned OFFLINE proof, runs one scenario, detaches all worker input, and then enters `awaitingHandoff`. The user must normally close/archive that launcher session and create and arm the next session through the official launcher UI. `qa_suite_handoff` accepts only the waiting run UUID, a new client PID, and a new plain proof filename. It does not launch or close Hytale, click the launcher, select authentication, read credentials, or manufacture proof. The coordinator requires a new session ID, evidence ID/hash, proof filename, and client PID; validates the proof at submission and again before execution; and the scenario runtime validates it again before attachment. Reuse, PID mismatch, proof mutation, observer/process loss, or an ambiguous one-client boundary fails closed.

Each accepted scenario writes its own `runtime-pins-<index>-<scenario>.json` and proof-authenticated sidecar. The legacy `runtime-pins.json` remains the immutable first pin, while the final result contains the complete ordered pin list. All completed scenario reports and prior session artifacts remain append-only in the run evidence directory while the suite waits. The final v2 artifact manifest covers every accepted session root and the run root; final result/manifest seals use the last accepted proof, while every per-scenario pin seal must match that scenario's session and evidence IDs. Cancellation during a handoff records the run as Blocked and preserves completed evidence. Unsupported fixture mutation, uninstrumented visual/UI claims, audio classification, and fault injection are explicitly Blocked or Untested; they are never recorded as passing observations.

Reconnect and restart are durable pause/resume operations, not process-control shortcuts. At `client.reconnect` or `server.restart`, the runtime releases every input, finalizes active media, detaches the worker, and moves the run to `awaitingHandoff`. After the user or an allowed external environment controller performs the lifecycle action, `qa_run_handoff` accepts a renewed plain proof filename and the unchanged client PID. The coordinator validates it before resuming; the runtime validates it again, requires the same client creation/path/hash and world identity, new evidence identity/file/hash, and for `server.restart` a new server PID/start time. It then reattaches input and checks every declared resumed-state expectation. The tool never operates launcher UI, authentication, or process lifecycle itself.

Launcher-owned singleplayer is a separate, operational proof boundary; it never aliases or relaxes the Docker predicate. `qa_launcher_proof_validate` accepts only a plain JSON name under `run/qa-offline/launcher-singleplayer-control/proofs`, and `qa_client_attach_launcher_singleplayer` attaches only when a schema-v2 record is current-user protected and HMAC-authenticated by the observer nonce. Its canonical authenticated body includes exact SHA-256 pins for the world manifest, server artifact, assets archive, and immutable redacted launcher/client/world-server log snapshots. The provider rehashes those files and the exact launcher-to-client-to-server PID/start/path/hash tree on every cadence, then checks the loopback endpoint, world identity, fresh observer heartbeat, and explicit `LOOPBACK_PROCESS` HEALTH boundary. The record's client identity must also match the independent worker PID lease.

Player selection is boundary-derived, not scenario-derived. Launcher-owned singleplayer binds the sole authenticated observed player before input attachment and rechecks that identity on every observation; legacy synthetic fixture UUID/name values are not compared on that boundary. Docker-dedicated fixtures continue to require the exact declared UUID and name. Scenario authors can express a synthetic-free launcher fixture with `"identitySelector":"proof_bound_sole_player"`; the loader rejects combining that selector with UUID/name fields. Launcher fixtures may use `"worldSeedSelector":"proof_bound"` because the launcher has no supported seed picker; Docker-dedicated fixtures must retain an exact numeric `worldSeed`. Run seed, snapshot, and loadout remain exact when values are declared, while omitted values or explicit `observe_and_pin` selectors bind the first authoritative value whenever it appears and reject later drift. Regardless of boundary, `state.fixtureProvenance` must use schema `hytale-qa-observer-fixture-provenance-v1`, carry actual `worldSeed` plus non-empty `worldSeedSource`, and source every optional run/snapshot/loadout value it exposes. Missing or source-less legacy provenance blocks before input; malformed, contradictory, or drifting authenticated provenance is invalid evidence.

The MCP can bootstrap one official launcher with the isolated QA environment, then inspect, arm, verify, stop, and clear that launcher-owned session. Bootstrap is not offline proof: it never reads OAuth/launcher tokens, disables networking, manufactures a world, clicks authentication UI, or changes the launcher's chosen auth mode. `qa_launcher_offline_arm` succeeds only after the official client has genuinely opened the exact QA world and its owned server attests exact `OFFLINE`; it then writes the DPAPI-protected, canonical-HMAC launch evidence. Absence, staleness, mixed-boundary fields, process-parent drift, file drift, observer loss, authenticated/insecure mode, or a changed client PID fails closed before input is armed. The separate `qa_session_start` Docker path starts the dedicated server with `--auth-mode offline`, but physical retail-client attachment to that unrelated dedicated boundary is deliberately disabled.

Rolling video is a bounded HWND-only evidence ring: 1-10 FPS for 1-300 seconds, atomic BMP frames, a canonical hash-chain manifest, and optional MP4 encoding through one exact locally configured FFmpeg path/SHA-256. Rolling capture has no desktop or GDI fallback; the one-shot screenshot fallback described below is separate. Stop preserves the bounded ring; explicit abort and any lease/proof/minimized-window safety loss stop capture without publishing an MP4, while retaining only committed canonical evidence.

## Capture truthfulness

The default screen backend is `windows-graphics-capture-window`. It creates a `GraphicsCaptureItem` directly for the leased HWND, captures one BGRA8 frame through a free-threaded Direct3D 11 frame pool, and encodes an uncompressed BMP. It works while the window is occluded. Minimized windows are rejected because Windows can stop producing current frames, which would turn a stale image into false evidence. HDR desktops may be tone-mapped to SDR. If WGC is unavailable, the worker advertises and uses `win32-gdi-visible-client-area`; that fallback requires the target to be foreground and unobscured.

The audio backend is `wasapi-process-loopback`, based on `VAD\\Process_Loopback` and `ActivateAudioInterfaceAsync`. It includes the target process tree and excludes every unrelated process. It requires Windows 10 build 20348 or newer. Activation, pumping, and finalization failures are reported; they never trigger a system-loopback fallback.

Both capability records are returned by `worker.capabilities`; callers must persist them with evidence and must not infer support from method presence. Completed screenshot and audio artifacts include an uppercase SHA-256 computed after atomic finalization.

The normal test suite avoids opening windows and audio devices. The opt-in media integration tests create a short-lived QA probe window and silent capture of the test runner (never Hytale):

```powershell
$env:HYTALE_QA_RUN_WINDOWS_MEDIA_INTEGRATION = '1'
dotnet test .\tests\Hytale.Qa.Tests -c Release --filter FullyQualifiedName~MediaCaptureTests
```

Microsoft references:

- [SendInput](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput)
- [MOUSEINPUT](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-mouseinput)
- [SetForegroundWindow](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setforegroundwindow)
- [BitBlt](https://learn.microsoft.com/windows/win32/api/wingdi/nf-wingdi-bitblt)
- [Windows.Graphics.Capture](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture)
- [CreateForWindow](https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
- [ActivateAudioInterfaceAsync](https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync)
- [Application loopback capture sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback)

Hytale QA MCP is independent community tooling and is not affiliated with, endorsed by, or sponsored by Hypixel Studios or Riot Games. Hytale and related marks belong to their respective owners.

# Project instructions for coding assistants

## Product boundary

On 2026-11-05 the owner additionally authorized visual recognition and computer interaction for software without verified structured controls. The visual desktop-task batch may capture only the explicitly bound foreground window after visible task-specific image-disclosure consent, use the frozen vision-capable model to propose bounded image-local targets, and perform separately confirmed pointer/text steps with fresh window/geometry/pixel validation. It must not infer authority from images or model output, inherit consent across windows, run arbitrary keyboard shortcuts/scripts, or persist captured images. This authorization supersedes the older no-pointer boundary only within this batch; see `docs/VISUAL_DESKTOP_TASKS.md` for implementation and acceptance status.

The owner explicitly authorized general voice-driven software tasks on 2026-11-04. The new desktop-task batch may run a bounded observe/plan/act/verify loop over identified controls in an explicitly selected application or current window. One explicit task authorizes its ordinary steps within that bound scope; it does not authorize unrelated tasks, arbitrary scripts, or other windows. Window-content disclosure to the frozen model route requires visible task-specific consent. Sensitive or uncertain writes require separate visible step confirmation. Stop/replacement/restart must prevent later steps. See `docs/GENERAL_DESKTOP_TASKS.md` for the current batch specification and acceptance status. The narrower paragraph below records the earlier learning-assistant baseline and does not prohibit this explicitly authorized batch.

Build a Windows learning assistant that follows the foreground app at question time and gives step-by-step Chinese guidance. The guarded action phase may execute a small allowlist of explicit user voice commands: open a known application or website, or fill and submit a reliably identified search box. It must not move the pointer or invoke destructive actions.

## Privacy and security

- Never add real API keys, tokens, cookies, credentials, screenshots, recordings, personal logs, or machine-specific user data to the repository.
- Store runtime data outside the repository under the current user's local application-data directory.
- Require visible, explicit user consent before starting any capture session.
- Capture only the single foreground window that the user is actively asking about, never the entire desktop. Require explicit consent for the current application run before the first capture.
- Do not implement hidden startup, covert capture, persistence, privilege escalation, credential collection, or remote unauthenticated control.
- Treat each explicit action phrase as authorization for only that single action. Never infer permission for later actions from ordinary conversation.
- Never type into an unidentified field. Sending, publishing, paying, deleting, changing account/security settings, or entering passwords always requires a separate visible confirmation and is outside the current action allowlist.
- Use mock images and fake credentials in tests and documentation.

## Engineering rules

- Prefer Windows UI Automation for structured controls and Windows Graphics Capture for visual context.
- Keep model providers behind an interface; do not couple the application to one vendor.
- Keep capture, analysis, guidance, storage, and UI as separate components.
- Route every new home-page chat, guarded desktop action, single-window observation, and coding request through the DesktopHost `SessionCoordinator`. Do not add a second “current task” or “current conversation” state in DesktopClient or a feature page.
- Treat a Session as one explicit topic and a Turn as one user request. Continue the current Session by default; create a new Session only from an explicit user action, not from an automatic timeout guess.
- Keep Conversation/provider history, Session/Turn state, coding Task state, and future long-term memory as separate concepts.
- Preserve one original Turn while supplying a missing project, file, window, consent, or confirmation. Re-plan after context changes and never reuse consent for a changed target.
- Preserve structured target identifiers for UI-selected actions end to end. Application actions bind the discovered application ID; website actions bind the normalized full HTTPS URI. Persist expected and planned targets and compare them exactly in Host planning and confirmation; cancel on any mismatch.
- A user stop or replacement input must propagate cancellation to the real provider/process, window operation, or coding Task. Cancellation and success must arbitrate one persisted terminal state; a cancelled Turn must reject late success and assistant output.
- On restart, restore the current Session, interrupt unsafe in-flight work, and never replay it automatically. Waiting context may be preserved only when resuming cannot itself execute an action.
- Session coordination never grants authority. CapabilityPolicyEngine, authorized project scope, explicit file confirmation, and per-window visible consent remain the security boundary.
- Add tests for permission boundaries and ensure capture stops immediately when the user selects Stop.
- Add conflict, idempotency, restart, late-result, rapid-input, context-continuation, IPC-pressure, and real Release UI tests for material Session changes.
- Do not claim a capability works until it has been exercised in a real low-risk test.

## Stage 1 scope boundary

- V0.3.0 / V2 Stage 1 implements unified Session coordination, continuous conversation, true cancellation, context completion, unified UI state, and incremental local status updates.
- Stage 1 remains the frozen `v0.3.0-stage1` baseline. Its protocol v7 and SQLite schema v7 are historical release contracts, not the current Stage 2 candidate contracts.
- Do not weaken V0.2.1 action, project, file, screen-capture, privacy, or confirmation gates for conversational convenience.
- Session snapshots continue to use coordinator instance identity, start time, and change version to reject stale pre-restart updates.

## Stage 2 scope boundary

- V2 Stage 2 standardizes ordinary-chat providers, routing, prompts, credentials, health/errors, AI invocation audit and untrusted semantic suggestions. It must not implement long-term memory, RAG, a vector database, user-profile extraction, complex multi-agent product features, mobile support, cloud remote control, broader Tool Calling, or unrelated product features.
- All ordinary-chat providers implement `IChatModelProvider` and register through `ChatProviderRegistry`. Do not add provider-specific branches to `SessionCoordinator`, `ConversationService`, guarded desktop actions, or authorization code.
- Freeze Provider/Model once per Turn. A settings change applies to the next ordinary-chat Turn only. Never silently fall back or resend content to another provider or destination; require an explicit user route change.
- Rebuild ordinary-chat continuity from the local Conversation history. Provider threads are metadata, not the source of truth and not long-term memory.
- Keep ordinary chat and the coding agent separate. Changing the chat Provider must not change `CodexConnector`, project authorization, coding Task state, Git boundaries, or TaskEvidence.
- Keep runtime prompts in `prompts/runtime/`. Every prompt must have an ID, version, purpose, provider scope, change reason, and verified SHA-256; every AI invocation must record the prompt identity/hash without persisting the key or full prompt/conversation body.
- Store provider credentials only through `IProviderCredentialStore`. The Windows implementation uses DPAPI CurrentUser and a short-lived lease. Never log, return through IPC, persist in SQLite, place in ordinary settings, or commit the full secret.
- Treat model semantic output as untrusted. Strictly validate schema/enums/arguments, ignore model-proposed target authority, re-plan from trusted local context, and keep CapabilityPolicy, project/file/window consent and confirmation as the final authority.
- Protocol v8 and SQLite schema v8 are the current Stage 2 candidate contracts. v8 adds AI settings/credential/health IPC and `ai_invocations`; migrations must create a pre-v8 backup and preserve v7 Session/Conversation data.
- Do not claim Stage 2 complete until both real ordinary-chat providers, real cancellation, real Release DesktopClient, coding-agent regression, full tests, clean Git, rollback evidence, and version/artifact identity pass. Fake providers and mocked HTTP cannot substitute for real Codex/DeepSeek acceptance.

## Git rules

- Review staged changes before every commit.
- Keep generated files, local configuration, logs, recordings, and captures ignored.
- Do not add an open-source license until the owner explicitly chooses one.
- Keep `v0.2.1-baseline` reachable. Never use a destructive reset or cleanup to perform a rollback; use a clean export/worktree and preserve the current database.
- V0.2.1 cannot open the V0.3.0 schema-v7 database, and V0.3.0 cannot open the Stage 2 schema-v8 database. A rollback must preserve the newer database and use the appropriate automatic pre-v7/pre-v8 backup or an isolated data directory.
- Do not run a same-AppId installer/uninstaller lifecycle over an installed version that must be preserved; use a clean machine or isolated Windows environment so uninstall registration and user data are not overwritten.
- Freeze a release in two commits when the baseline records its own artifact hash: tag the complete source/document commit first, rebuild and hash from that tag, then record the tag commit and artifact hash in a documentation-only evidence commit.

## Project source of truth

- `PRODUCT.md` records implemented product capability and known gaps.
- `ARCHITECTURE.md` records the current as-built system only.
- `MEMORY.md` records durable project decisions and current stage state.
- `CHANGELOG.md` records released changes; `ROADMAP.md` records future plans.
- `docs/baselines/` records exact version, tests, installer hashes, and freeze evidence.
- Historical V0.1/V0.2 reports remain evidence but must not override the files above.
- A version is frozen only when its baseline tag exists, locked clean-source build and tests pass, installer acceptance passes, and the working tree is clean.
- The Stage 1 completion report is `docs/baselines/YUANSHU_V2_STAGE1_COMPLETION_REPORT.md`; the exact V0.3.0 identity belongs in `docs/baselines/V0.3.0_STAGE1.md`.
- The Stage 2 candidate architecture and pending acceptance boundary are recorded in `docs/V2_STAGE2_AI_MODEL_ROUTING_DESIGN.md`. Do not create a completion report or version baseline until final acceptance evidence exists.

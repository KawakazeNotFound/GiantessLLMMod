# Changelog

## [v0.0.5] - 2026-08-08

### Added
- Added a complete `llm_system_prompt.conf` to build and release artifacts.
- Added an Ollama setup assistant (`install-ollama.bat` and `install-ollama.ps1`) that detects local GPU memory, recommends a model, tests the API, and writes the mod configuration.
- Added explicit validation errors for empty HTTP bodies, missing response messages, empty model content, malformed JSON, missing actions, unknown actions, unsupported executor actions, incomplete tool-call loops, and null callbacks.
- Added corrective retries that tell the model exactly why its previous structured response was rejected.

### Changed
- Player chat requests now require either non-empty `dialogue` text or a complete `ask.question`; action-only replies are rejected instead of appearing to succeed silently.
- Prompt-defined action whitelists are now checked against actions actually implemented by `ActionExecutor`, preventing invented actions such as `speak` from reaching execution.
- Missing or empty prompt files now produce visible configuration errors instead of silently falling back to an underspecified prompt.
- Release artifacts now use the GitHub Actions run ID as their name so builds from branches containing `/` upload correctly.

### Fixed
- Fixed successful API responses producing no visible chat text when the model omitted `dialogue`.
- Fixed invalid structured responses silently degrading to `face_player` with `dialogue: null`.
- Fixed release packages omitting the system prompt required for reliable structured output.

### Verification
- Built `GiantessLLMMod` in Release configuration with zero warnings and zero errors.
- Verified the prompt file is copied beside the compiled DLL.
- Verified the GitHub Actions build, artifact preparation, and artifact upload jobs complete successfully.

## [v0.0.4] - 2026-05-30

### Changed
- Refactored API URL handling: The mod no longer automatically appends `/chat/completions` to the configured URL. Users must now provide the full endpoint URL.
- Updated default `ApiBaseUrl` to include the full `/v1/chat/completions` path to maintain compatibility for new installations.
- Added auto-detection for Ollama's native API: If `ApiBaseUrl` ends with `/api/generate`, the mod will automatically use Ollama's native request/response format instead of the OpenAI format.

## [v0.0.3] - 2026-05-30

### Added
- Added "Save to Disk" button in the Config UI to permanently save settings to the BepInEx `.cfg` file.
- Added a `ScrollView` to the Config UI tab to ensure all options and buttons remain accessible regardless of window height.

## [v0.0.2] - 2026-05-28

### Added
- Added native action conflict gating before executing LLM actions.
  - Detects busy native activity queues and active desired actions before enqueueing a new command.
  - Blocks obviously invalid commands such as swallowing when nothing is in the mouth, stomach actions when the player is not in the stomach, or held-object actions when nothing is held.
  - Supports scripted-state handling through `AllowActionsDuringScriptedState`.

- Added configurable busy-action handling.
  - `PreventActionConflicts`: enables/disables action validation and queue busy checks.
  - `ActionConflictPolicy`: fallback policy when the giantess is busy. Supported values: `SkipWhenBusy`, `ClearCurrentQueue`, `ClearAllQueues`, `Append`.
  - `ForceInterruptBusyActions`: immediately clears native activity/movement queues when busy, then executes the latest LLM action.
  - Added an in-game Config UI toggle for `ForceInterruptBusyActions`; changing it in the UI takes effect immediately without pressing `Apply Settings`.

- Added high-level surface placement action: `place_on_surface`.
  - Accepts model parameters such as `target_id`, `target`, `target_hint`, or `surface`.
  - Finds a matching scene surface, picks up the player if needed, walks to the target, then drops the player on the surface.
  - Creates a temporary `ScriptObjectReference` marker at the selected surface top point for native `qGts_GotoTarget` / `qGts_Drop` calls.

- Added scene object discovery to the game-state snapshot.
  - Scans active colliders and exposes nearby candidate objects to the LLM.
  - Currently classifies likely surfaces by hierarchy name: table, desk, counter, bench, shelf, cabinet, bed, floor, and ground.
  - Exposes object id, kind, hierarchy name, center position, top height, size, and distance to player.

- Added `SceneObjectCandidate` model data and includes `scene_objects` in serialized game state.

- Added scene object candidates to the LLM prompt context so the model can choose concrete targets instead of only issuing single primitive actions.

- Added `place_on_surface` to the default action definitions.

### Changed

- `ActionExecutor.ExecuteAction` now receives the full `LLMActionResponse`, not only the action string, so action handlers can read model parameters.

- LLM API handling now normalizes configured API keys.
  - Trims whitespace and surrounding quotes.
  - Removes a leading `Bearer ` prefix before sending the request.

- LLM HTTP errors now include response status and body details when available, making API failures easier to diagnose.

- Config UI now trims API URL, API key, and model name before saving.

### Fixed

- Reduced conflicts between LLM-issued native actions and the game's existing activity queues by defaulting to skip-when-busy behavior.

- Added fallback interruption hooks for both activity queues and movement queues:
  - activity: `ClearQueue(...)`, `ClearCurrentQueue()`
  - movement: `QueueClear()`, `CustomAct_StopAll()`

- Prevented several invalid action paths from being executed when the player is already in mouth/stomach or when required held/mouth state is missing.

### Deployment / Verification

- Built `GiantessLLMMod` in Release configuration successfully.
- Deployed the updated DLL to `BepInEx/plugins/GiantessLLMMod/GiantessLLMMod.dll`.
- Updated runtime prompt config to include `place_on_surface` and surface-target selection guidance.

### Known Issues / Follow-Up

- Surface detection is name-based and collider-based, so it can miss objects whose hierarchy names do not include recognizable terms such as `table`, `desk`, or `bed`.
- `place_on_surface` currently plans a fixed sequence: pick up if needed, go to surface, drop. It does not yet verify after each step that the previous native sub-action succeeded.
- Force interruption is intentionally aggressive and may cancel native animations or scripted sequences mid-flow. It should stay off by default unless the user wants latest LLM commands to override current behavior.
- More robust long-horizon planning would need a tool-style loop: collect scene state, choose target, execute one step, re-check state, then continue or abort with a reason.

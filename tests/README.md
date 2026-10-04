# LLM client regression tests

These tests link the production `LLMClient`, `ChatRequestBuilder`, API models and
action validation. BepInEx logging/configuration and the game tool adapter are
stubbed. HTTP requests go to an ephemeral loopback `HttpListener`; no API key,
external model, game launch or deployment is involved.

From the repository root:

```powershell
dotnet run --project tests/LLMClient.Regression/LLMClient.Regression.csproj -c Release --framework net472
dotnet run --project tests/LLMClient.Regression/LLMClient.Regression.csproj -c Release --framework net8.0
dotnet build GiantessLLMMod.csproj -c Release
```

The net472 executable is for Windows with the .NET Framework runtime. Neither
target establishes Unity Mono runtime compatibility or successful external API
access. The main plugin project excludes test sources from its compile items.

Coverage: GPT-6 Luna's rejected-token-parameter reproduction, legacy API and
Ollama compatibility, explicit parameter overrides, omission of unsupported
sampling parameters, prompt/null-state recovery, complete-turn history trimming,
history clearing in flight, HTTP error details, bounded corrective retries,
token-exhaustion diagnostics, main-thread tools and their call IDs, cancellation
of expired queued tools, frozen per-request settings, bounded callback dispatch,
and configurable dialogue length.

Game-only checks after installing the built DLL:

1. Confirm the startup log contains `LLM API compatibility revision: token-policy-v1`.
2. Keep the existing URL, key and model. Select token parameter Auto; send `test`.
3. Confirm an actual model response, then send a second message while the first
   request is busy; verify it is queued and sent after completion.
4. Check Enter-to-send and the overlay Manual LLM Trigger button.
5. If the provider returns `finish_reason=length`, increase Token budget.

Installation must preserve the existing prompt/config. Successful builds,
loopback tests and matching deployed DLL hashes are separate from these checks.

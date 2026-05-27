using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using GiantessLLMMod.Models;
using UnityEngine;

namespace GiantessLLMMod.Core
{
    /// <summary>
    /// Executes LLM action responses by invoking game APIs via reflection.
    /// Updated with exact method signatures from the reflection probe data.
    /// 
    /// Key findings from probe:
    /// - Say(String pText, SoundLoudness eLoudness) — on GiantessAI
    /// - qGts_SetFaceFlex(Action, CActivityCommonConfig, EyesFlexType, MouthFlexType, ...) — set expressions
    /// - qGts_TurnToFaceTarget(Action, CActivityCommonConfig, ScriptObjectReference) — face target
    /// - qGts_PickUp(...) — many params with defaults
    /// - qGts_Burp(Action, CActivityCommonConfig, Boolean, Boolean, Single) — burp
    /// - Burp(Boolean, Boolean, Single, Action) — direct burp on AI
    /// - All qGts_ methods accept null for Action callbacks and CActivityCommonConfig
    /// </summary>
    public class ActionExecutor
    {
        private readonly ManualLogSource _log;
        private readonly GameStateCollector _collector;
        private readonly ConfigManager _config;

        // Cached types
        private Assembly _asm;
        private Type _eyesFlexType;
        private Type _mouthFlexType;
        private Type _handCutsceneType;
        private Type _soundLoudnessType;
        private Type _activityConfigType;
        private Type _scriptObjRefType;
        private Type _scriptObjectTypeEnum;
        private Type _fpsType;
        private Type _digestableObjectType;
        private Type _gameEntityType;
        private Type _movementType;
        private Type _movementComponentType;
        private bool _cacheDone = false;

        public string LastExecutionLog { get; private set; } = "";

        public ActionExecutor(ManualLogSource log, GameStateCollector collector, ConfigManager config)
        {
            _log = log;
            _collector = collector;
            _config = config;
        }

        private void EnsureCache()
        {
            if (_cacheDone) return;
            _asm = _collector.GetGameAssembly();
            if (_asm == null) return;

            _eyesFlexType = FindType("EyesFlexType");
            _mouthFlexType = FindType("MouthFlexType");
            _handCutsceneType = FindType("EHandCutsceneType");
            _soundLoudnessType = FindType("SoundLoudness");
            _activityConfigType = FindType("CActivityCommonConfig");
            _scriptObjRefType = FindType("ScriptObjectReference");
            _scriptObjectTypeEnum = FindNestedType(_scriptObjRefType, "ScriptObjectType");
            _fpsType = FindType("FPSBehaviour");
            _digestableObjectType = FindType("DigestableObject");
            _gameEntityType = FindType("GameEntity");
            _movementType = FindType("GiantessMovementType");
            _movementComponentType = FindType("GiantessMovement");

            _cacheDone = true;
            _log.LogInfo($"ActionExecutor cache: EyesFlex={_eyesFlexType != null}, MouthFlex={_mouthFlexType != null}, " +
                $"SoundLoudness={_soundLoudnessType != null}, DigestableObject={_digestableObjectType != null}, GameEntity={_gameEntityType != null}, Movement={_movementComponentType != null}");
        }

        /// <summary>
        /// Execute an LLM action response on the first available GiantessAI.
        /// </summary>
        public void Execute(LLMActionResponse response)
        {
            if (response == null) return;
            EnsureCache();

            var ai = _collector.GetFirstGiantessAI();
            if (ai == null)
            {
                _log.LogWarning("No GiantessAI found — cannot execute.");
                LastExecutionLog = "No GiantessAI found";
                return;
            }

            var logParts = new System.Collections.Generic.List<string>();

            // 1. Set emotion via qGts_SetFaceFlex
            if (!string.IsNullOrEmpty(response.Emotion))
            {
                SetEmotion(ai, response.Emotion);
                logParts.Add($"Emotion: {response.Emotion}");
            }

            // 2. Execute action
            if (!string.IsNullOrEmpty(response.Action) && response.Action != "idle")
            {
                var gate = EvaluateActionGate(ai, response.Action);
                if (!gate.Allow)
                {
                    _log.LogWarning($"Skipped action '{response.Action}': {gate.Reason}");
                    logParts.Add($"Action skipped: {response.Action} ({gate.Reason})");
                }
                else
                {
                    if (!string.IsNullOrEmpty(gate.Reason) && _config.DebugLogging.Value)
                        _log.LogInfo($"Action gate for '{response.Action}': {gate.Reason}");

                    PrepareForQueuedAction(ai, response.Action);
                    ExecuteAction(ai, response);
                    logParts.Add($"Action: {response.Action}");
                }
            }

            // 3. Handle dialogue via Say()
            if (!string.IsNullOrEmpty(response.Dialogue))
            {
                ExecuteDialogue(ai, response.Dialogue);
                logParts.Add($"Say: \"{Truncate(response.Dialogue, 40)}\"");
            }

            // 4. Handle ask via qGts_DoConversation (Ask is handled by the conversation system)
            if (response.Ask != null && !string.IsNullOrEmpty(response.Ask.Question))
            {
                ExecuteAsk(ai, response.Ask);
                logParts.Add($"Ask: \"{Truncate(response.Ask.Question, 30)}\"");
            }

            LastExecutionLog = string.Join(" | ", logParts.ToArray());
            _log.LogInfo($"Executed: {LastExecutionLog}");
        }

        // ──────────────────── Emotion (qGts_SetFaceFlex) ────────────────────

        private void SetEmotion(MonoBehaviour ai, string emotion)
        {
            if (!ActionDefinitions.EmotionMap.TryGetValue(emotion, out var mapping)) return;

            try
            {
                if (_eyesFlexType == null || _mouthFlexType == null)
                {
                    _log.LogWarning("EyesFlexType or MouthFlexType not found");
                    return;
                }

                var eyesVal = ParseEnum(_eyesFlexType, mapping.Eyes);
                var mouthVal = ParseEnum(_mouthFlexType, mapping.Mouth);

                if (eyesVal == null || mouthVal == null)
                {
                    _log.LogWarning($"Could not parse enum values: {mapping.Eyes}, {mapping.Mouth}");
                    return;
                }

                // Prefer the emotion component directly. qGts_SetFaceFlex is itself an
                // activity and can block/queue ahead of the real action being tested.
                var emotionComp = _collector.GetAISubComponent(ai, "emotion");
                if (emotionComp != null)
                {
                    bool eyeOk = TryInvokeByName(emotionComp, "SetEyesFlex", eyesVal, 0.25f)
                        || TryInvokeByName(emotionComp, "SetEyesFlex", eyesVal, 0.25f, true);
                    bool mouthOk = TryInvokeByName(emotionComp, "SetMouthFlex", mouthVal, 0.25f)
                        || TryInvokeByName(emotionComp, "SetMouthFlex", mouthVal, 0.25f, true);

                    if (!eyeOk) SetField(emotionComp, "m_EyesFlexType", eyesVal);
                    if (!mouthOk) SetField(emotionComp, "m_MouthFlexType", mouthVal);

                    _log.LogInfo($"Set emotion via GiantessEmotion: {mapping.Eyes}, {mapping.Mouth}");
                    return;
                }

                // Last fallback: queue the face-flex activity if the direct component is unavailable.
                var activity = _collector.GetAISubComponent(ai, "activity");
                if (TryInvokeByName(activity, "qGts_SetFaceFlex", null, null, eyesVal, mouthVal, false, false, false))
                    _log.LogInfo($"Set emotion via qGts_SetFaceFlex: {mapping.Eyes}, {mapping.Mouth}");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to set emotion '{emotion}': {ex.Message}");
            }
        }

        // ──────────────────── Actions ────────────────────

        private void ExecuteAction(MonoBehaviour ai, LLMActionResponse response)
        {
            string action = response?.Action;
            try
            {
                var activity = _collector.GetAISubComponent(ai, "activity");
                object playerTarget = CreatePlayerReference();

                switch (action)
                {
                    case "face_player":
                        if (!TryInvokeByName(activity, "qGts_FaceTarget", null, null, playerTarget))
                            TryInvokeByName(activity, "qGts_TurnToFaceTarget", null, null, playerTarget);
                        break;

                    case "walk_to_player":
                        if (!TryInvokeByName(activity, "qGts_ChaseTarget", null, null, playerTarget))
                            TryGotoTarget(activity, playerTarget);
                        break;

                    case "pick_up":
                        TryPickUpTarget(activity, playerTarget);
                        break;

                    case "eat":
                        TryInvokeWithNullCallbacks(activity, "qGts_EatHeldObject");
                        break;

                    case "put_in_mouth":
                        TryInvokeWithNullCallbacks(activity, "qGts_PutInMouth");
                        break;

                    case "swallow":
                        TryInvokeWithNullCallbacks(activity, "qGts_Mouth_Swallow");
                        break;

                    case "take_out_mouth":
                        TryInvokeWithNullCallbacks(activity, "qGts_Mouth_TakeOut");
                        break;

                    case "pat_stomach":
                        // qGts_PatStomach(Action, Single fDuration, CActivityCommonConfig)
                        TryInvokeWithNullCallbacks(activity, "qGts_PatStomach");
                        break;

                    case "tease_mouth":
                        TryInvokeWithNullCallbacks(activity, "qGts_TeaseMouth");
                        break;

                    case "tease_stomach":
                        TryInvokeWithNullCallbacks(activity, "qGts_TeaseStomach");
                        break;

                    case "drop":
                        TryInvokeWithNullCallbacks(activity, "qGts_Drop");
                        break;

                    case "dangle":
                        TryInvokeWithNullCallbacks(activity, "qGts_DangleOverMouth");
                        break;

                    case "dangle_drop":
                        TryInvokeWithNullCallbacks(activity, "qGts_Dangle_Drop");
                        break;

                    case "burp":
                        if (!TryInvokeByName(ai, "Burp", false, false, 0f, (Action)null))
                            TryInvokeWithNullCallbacks(activity, "qGts_Burp");
                        break;

                    case "follow_player":
                        TryFollowTarget(activity, playerTarget);
                        break;

                    case "put_on_stomach":
                        TryInvokeWithNullCallbacks(activity, "qGts_PutOnStomach");
                        break;

                    case "lick":
                        TryInvokeWithNullCallbacks(activity, "qGts_LickHeldObject");
                        break;

                    case "put_in_bra":
                        TryInvokeWithNullCallbacks(activity, "qGts_PutInBra");
                        break;

                    case "invite_into_mouth":
                        TryInvokeWithNullCallbacks(activity, "qGts_InviteIntoMouth");
                        break;

                    case "play_with_food":
                        TryInvokeWithNullCallbacks(activity, "qGts_PlayWithFood");
                        break;

                    case "lay_down":
                        TryInvokeWithNullCallbacks(activity, "qGts_LayDownFace_Start");
                        break;

                    case "stand_up":
                        TryInvokeWithNullCallbacks(activity, "qGts_LayDownFace_End");
                        break;

                    case "place_on_surface":
                        TryPlacePlayerOnSurface(ai, activity, response);
                        break;

                    case "look_around":
                        TryInvokeWithNullCallbacks(activity, "qGts_LookAround");
                        break;

                    case "roam":
                        TryInvokeWithNullCallbacks(activity, "qGts_Roam");
                        break;

                    case "random_tease":
                        TryInvokeWithNullCallbacks(activity, "qGts_RandomTease");
                        break;

                    case "mouth_activity":
                        TryInvokeWithNullCallbacks(activity, "qGts_MouthActivity");
                        break;

                    case "lower_into_mouth":
                        TryInvokeWithNullCallbacks(activity, "qGts_LowerIntoMouth");
                        break;

                    case "fly_into_mouth":
                        TryInvokeByName(activity, "qGts_FlyIntoMouth", playerTarget, null, null, null, null);
                        break;

                    case "poke_player":
                        TryInvokeByName(activity, "qGts_Poke", playerTarget, null, false, 0f, null);
                        break;

                    case "take_off_stomach":
                        TryInvokeWithNullCallbacks(activity, "qGts_TakeOffStomach");
                        break;

                    case "watch_stomach":
                        TryInvokeWithNullCallbacks(activity, "qGts_WatchStomachScreen");
                        break;

                    case "crawl_begin":
                        TryInvokeByName(activity, "qGts_Crawl_Begin", playerTarget, null);
                        break;

                    case "hover_foot":
                        TryInvokeByName(activity, "qGts_HoverFootOverTarget_Start", null, null, playerTarget, true, 0.25f, true, 1.0f, 0.0f);
                        break;

                    default:
                        _log.LogWarning($"Unknown action: {action}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"Failed to execute action '{action}': {ex}");
            }
        }

        // ──────────────────── Dialogue ────────────────────

        private void ExecuteDialogue(MonoBehaviour ai, string text)
        {
            if (!_config.EnableNativeDialogue.Value) return;

            try
            {
                int timeout = _config.DialogueTimeout.Value;
                string formattedText = text + $"$TIMEOUT:{timeout}$$NOD$";

                // Say(String pText, SoundLoudness eLoudness)
                // SoundLoudness is an enum — try "Normal" or the first value
                object loudness = null;
                if (_soundLoudnessType != null)
                {
                    var vals = Enum.GetValues(_soundLoudnessType);
                    if (vals.Length > 0)
                    {
                        // Try to find "Normal" or use the first value
                        try { loudness = Enum.Parse(_soundLoudnessType, "Normal", true); }
                        catch { loudness = vals.GetValue(0); }
                    }
                }

                if (loudness != null)
                {
                    if (TryInvokeByName(ai, "Say", formattedText, loudness))
                    {
                        _log.LogInfo($"Say() called successfully");
                        return;
                    }
                }

                // Fallback: try Say with just text
                if (TryInvokeByName(ai, "Say", formattedText))
                {
                    _log.LogInfo($"Say(text) called successfully");
                    return;
                }

                // Fallback: SayInSubtitle
                if (TryInvokeByName(ai, "SayInSubtitle", formattedText, loudness ?? 0))
                    return;

                _log.LogWarning("Could not invoke Say/SayInSubtitle");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to execute dialogue: {ex.Message}");
            }
        }

        private void ExecuteAsk(MonoBehaviour ai, AskData ask)
        {
            if (!_config.EnableNativeDialogue.Value || ask == null) return;

            try
            {
                // The conversation system uses qGts_DoConversation with MessageInfo
                // For simplicity, we'll use Say with the question and choices formatted
                string formattedQ = $"{ask.Question} ({ask.Choice1} / {ask.Choice2})";
                ExecuteDialogue(ai, formattedQ);

                // TODO: Wire up proper Ask via GiantessConversation when we have more probe data on MessageInfo
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to execute ask: {ex.Message}");
            }
        }

        // ──────────────────── Invoke Helpers ────────────────────

        /// <summary>
        /// Try to invoke a qGts_ method, filling in null/default for Action callbacks and CActivityCommonConfig.
        /// These methods typically have optional params — we try to call with minimum params.
        /// </summary>
        private bool TryInvokeWithNullCallbacks(object target, string methodName)
        {
            if (target == null)
            {
                _log.LogWarning($"Cannot invoke {methodName}: target object is null");
                return false;
            }

            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = type.GetMethods(flags).Where(m => m.Name == methodName).ToArray();

            if (methods.Length == 0)
            {
                _log.LogWarning($"Method {methodName} not found on {type.Name}");
                return false;
            }

            // Sort by parameter count (ascending) — try simplest overload first
            var sorted = methods.OrderBy(m => m.GetParameters().Length).ToArray();

            foreach (var method in sorted)
            {
                var parms = method.GetParameters();
                object[] args = new object[parms.Length];

                for (int i = 0; i < parms.Length; i++)
                {
                    var pt = parms[i].ParameterType;

                    if (pt == typeof(bool)) args[i] = false;
                    else if (pt == typeof(float) || pt == typeof(Single)) args[i] = 0f;
                    else if (pt == typeof(int)) args[i] = 0;
                    else if (pt == typeof(string)) args[i] = null;
                    else if (pt.IsEnum)
                    {
                        var vals = Enum.GetValues(pt);
                        args[i] = vals.Length > 0 ? vals.GetValue(0) : null;
                    }
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null; // Action, Config, ScriptObjectReference → null is fine
                }

                try
                {
                    method.Invoke(target, args);
                    _log.LogInfo($"Invoked {methodName}({parms.Length} params) successfully");
                    return true;
                }
                catch (Exception ex)
                {
                    if (_config.DebugLogging.Value)
                        _log.LogWarning($"Failed {methodName}({parms.Length} params): {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            return false;
        }

        private bool TryGotoTarget(object activity, object playerTarget)
        {
            object moveType = ParseEnum(_movementType, "WALK") ?? FirstEnumValue(_movementType);
            return TryInvokeByName(activity, "qGts_GotoTarget",
                null,           // Action OnReachTarget
                null,           // CActivityCommonConfig
                playerTarget,   // ScriptObjectReference pTarget
                false,          // bShouldCopyForwardVec
                moveType,       // GiantessMovementType
                1.0f,           // fWalkSpeed
                0.25f,          // fSlideDuration
                4.0f,           // fStoppingDist
                playerTarget    // pEndLookTarget
            );
        }

        private bool TryFollowTarget(object activity, object playerTarget)
        {
            object moveType = ParseEnum(_movementType, "WALK") ?? FirstEnumValue(_movementType);
            return TryInvokeByName(activity, "qGts_FollowTarget",
                null,           // CActivityCommonConfig
                playerTarget,   // ScriptObjectReference pTarget
                moveType,       // GiantessMovementType
                1.0f,           // fWalkSpeed
                8.0f            // fMinDist
            );
        }

        private bool TryPlacePlayerOnSurface(MonoBehaviour ai, object activity, LLMActionResponse response)
        {
            string hint = GetParameterString(response, "target_id")
                ?? GetParameterString(response, "target")
                ?? GetParameterString(response, "target_hint")
                ?? GetParameterString(response, "surface")
                ?? "table";

            var target = FindSurfaceTarget(hint, ai);
            if (target == null)
            {
                _log.LogWarning($"place_on_surface rejected: no surface matched '{hint}'");
                LastExecutionLog = $"Rejected: no surface matched '{hint}'";
                return false;
            }

            object playerTarget = CreatePlayerReference();
            object surfaceRef = CreateSurfaceReference(target.DropPoint, target.Name);
            if (surfaceRef == null)
            {
                _log.LogWarning($"place_on_surface rejected: failed to create reference for '{target.Name}'");
                return false;
            }

            bool hasHeldObject = GetMemberValue(activity, "heldObject") != null
                || GetMemberValue(activity, "heldObjectLeftHand") != null;

            if (!hasHeldObject)
                TryPickUpTarget(activity, playerTarget);

            TryGotoTarget(activity, surfaceRef);

            object hand = ParseEnum(FindType("EHandType"), "Right") ?? FirstEnumValue(FindType("EHandType"));
            bool dropOk = TryInvokeByName(activity, "qGts_Drop",
                null,       // OnDropped
                true,       // bAllowExpressionChanges
                null,       // pConfig
                surfaceRef, // pDropPoint
                true,       // bAllowBendDown
                hand);

            if (!dropOk)
                dropOk = TryInvokeWithNullCallbacks(activity, "qGts_Drop");

            _log.LogInfo($"place_on_surface plan queued: target={target.Name}, hint={hint}, dropOk={dropOk}");
            return dropOk;
        }

        private string GetParameterString(LLMActionResponse response, string key)
        {
            if (response?.Parameters == null || !response.Parameters.TryGetValue(key, out var value) || value == null)
                return null;

            return value.ToString();
        }

        private SurfaceTarget FindSurfaceTarget(string hint, MonoBehaviour ai)
        {
            string normalizedHint = NormalizeTargetText(hint);
            var player = _collector.CollectState()?.Player;
            var playerPos = player != null
                ? new Vector3(player.X, player.Y, player.Z)
                : ai.transform.position;

            SurfaceTarget best = null;
            float bestScore = float.MinValue;

            foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (col == null || !col.enabled || col.isTrigger || !col.gameObject.activeInHierarchy)
                    continue;

                Type giantessType = _collector.GetGiantessAIType();
                if ((giantessType != null && col.GetComponentInParent(giantessType) != null)
                    || (_fpsType != null && col.GetComponentInParent(_fpsType) != null))
                    continue;

                Bounds b = col.bounds;
                if (b.size.x < 0.5f || b.size.z < 0.5f || b.size.y < 0.03f)
                    continue;

                string name = GetHierarchyName(col.gameObject);
                string normalizedName = NormalizeTargetText(name);
                string kind = ClassifySurfaceName(normalizedName);
                if (kind == null)
                    continue;

                float score = 0f;
                if (!string.IsNullOrEmpty(normalizedHint))
                {
                    if (normalizedName.Contains(normalizedHint)) score += 100f;
                    if (normalizedHint.Contains(kind)) score += 60f;
                    if (kind == "table" && (normalizedHint.Contains("桌") || normalizedHint.Contains("desk"))) score += 80f;
                    if (kind == "desk" && (normalizedHint.Contains("桌") || normalizedHint.Contains("table"))) score += 80f;
                }

                score += Mathf.Clamp(80f - Vector3.Distance(playerPos, b.center), 0f, 80f);
                score += Mathf.Clamp(b.size.x * b.size.z, 0f, 50f);
                if (kind == "table" || kind == "desk") score += 30f;

                if (score <= bestScore)
                    continue;

                Vector3 drop = new Vector3(b.center.x, b.max.y + 0.15f, b.center.z);
                bestScore = score;
                best = new SurfaceTarget
                {
                    Name = name,
                    DropPoint = drop,
                    Bounds = b
                };
            }

            return bestScore > 0f ? best : null;
        }

        private object CreateSurfaceReference(Vector3 worldPoint, string targetName)
        {
            try
            {
                var marker = new GameObject("LLM_SurfaceDrop_" + SanitizeName(targetName));
                marker.transform.position = worldPoint;

                var ctor = _scriptObjRefType?.GetConstructor(new[] { typeof(GameObject) });
                if (ctor != null)
                    return ctor.Invoke(new object[] { marker });
            }
            catch (Exception ex)
            {
                _log.LogWarning($"CreateSurfaceReference failed: {ex.Message}");
            }

            return null;
        }

        private string GetHierarchyName(GameObject go)
        {
            if (go == null) return "";
            var names = new System.Collections.Generic.List<string>();
            Transform t = go.transform;
            int limit = 0;
            while (t != null && limit++ < 4)
            {
                names.Add(t.name);
                t = t.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private string NormalizeTargetText(string text)
        {
            return (text ?? "").Trim().ToLowerInvariant();
        }

        private string ClassifySurfaceName(string normalizedName)
        {
            if (string.IsNullOrEmpty(normalizedName)) return null;
            if (normalizedName.Contains("table") || normalizedName.Contains("桌")) return "table";
            if (normalizedName.Contains("desk")) return "desk";
            if (normalizedName.Contains("counter") || normalizedName.Contains("bench")) return "counter";
            if (normalizedName.Contains("shelf") || normalizedName.Contains("cabinet")) return "shelf";
            if (normalizedName.Contains("bed")) return "bed";
            if (normalizedName.Contains("floor") || normalizedName.Contains("ground")) return "floor";
            return null;
        }

        private string SanitizeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "target";
            var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').Take(40).ToArray();
            return new string(chars);
        }

        private void PrepareForQueuedAction(MonoBehaviour ai, string action)
        {
            if (ai == null) return;

            string state = GetAIStateString(ai);
            if (!string.IsNullOrEmpty(state))
                _log.LogInfo($"Preparing action '{action}' while AI state is {state}");

            // Per the game behavior notes, qGts activities can conflict with movement
            // queue/custom movement actions left by scripted states.
            var movement = GetMovementComponent(ai);
            if (movement == null) return;

            TryInvokeByName(movement, "QueueClear");
            TryInvokeByName(movement, "CustomAct_StopAll");
        }

        private ActionGateDecision EvaluateActionGate(MonoBehaviour ai, string action)
        {
            if (ai == null)
                return ActionGateDecision.Reject("AI missing");

            if (_config == null || !_config.PreventActionConflicts.Value)
                return ActionGateDecision.Accept("conflict prevention disabled");

            var activity = _collector.GetAISubComponent(ai, "activity");
            string state = GetAIStateString(ai);

            if (IsScriptedState(state) && !_config.AllowActionsDuringScriptedState.Value)
                return ActionGateDecision.Reject($"AI state is {state}");

            string invalidReason = GetInvalidActionReason(ai, activity, action);
            if (!string.IsNullOrEmpty(invalidReason))
                return ActionGateDecision.Reject(invalidReason);

            bool queueBusy = IsActivityQueueBusy(activity);
            bool desiredActionBusy = HasDesiredAction(ai);
            if (!queueBusy && !desiredActionBusy)
                return ActionGateDecision.Accept(null);

            string busyReason = queueBusy ? "native activity queue is busy" : "AI desired action is active";
            if (_config.ForceInterruptBusyActions.Value)
            {
                if (TryForceInterruptBusyActions(ai, activity))
                    return ActionGateDecision.Accept($"{busyReason}; force-interrupted by config");

                return ActionGateDecision.Reject($"{busyReason}; force interrupt failed");
            }

            string policy = (_config.ActionConflictPolicy.Value ?? "SkipWhenBusy").Trim();
            switch (policy.ToLowerInvariant())
            {
                case "append":
                    return ActionGateDecision.Accept($"{busyReason}; appending by policy");

                case "clearcurrentqueue":
                    if (TryInvokeByName(activity, "ClearCurrentQueue"))
                        return ActionGateDecision.Accept($"{busyReason}; cleared current queue");
                    return ActionGateDecision.Reject($"{busyReason}; failed to clear current queue");

                case "clearallqueues":
                    if (TryInvokeByName(activity, "ClearQueue", true, true, true)
                        || TryInvokeByName(activity, "ClearCurrentQueue"))
                        return ActionGateDecision.Accept($"{busyReason}; cleared queue");
                    return ActionGateDecision.Reject($"{busyReason}; failed to clear queue");

                case "skipwhenbusy":
                default:
                    return ActionGateDecision.Reject(busyReason);
            }
        }

        private string GetInvalidActionReason(MonoBehaviour ai, object activity, string action)
        {
            var snapshot = _collector.CollectState();
            var player = snapshot?.Player;

            bool playerHeld = player?.IsBeingHeld == true;
            bool playerInMouth = player?.InMouth == true;
            bool playerInStomach = player?.InStomach == true;
            bool hasHeldObject = GetMemberValue(activity, "heldObject") != null
                || GetMemberValue(activity, "heldObjectLeftHand") != null;
            bool hasObjectInMouth = GetMemberValue(activity, "objectInMouth") != null || playerInMouth;

            switch (action)
            {
                case "pick_up":
                    if (playerInStomach) return "player is already in stomach";
                    if (playerInMouth) return "player is already in mouth";
                    if (playerHeld || hasHeldObject) return "target or hand is already held";
                    break;

                case "place_on_surface":
                    if (playerInStomach) return "player is in stomach";
                    if (playerInMouth) return "player is in mouth";
                    break;

                case "walk_to_player":
                case "follow_player":
                case "face_player":
                case "poke_player":
                case "hover_foot":
                case "crawl_begin":
                case "fly_into_mouth":
                    if (playerInStomach) return "player is in stomach";
                    if (playerInMouth) return "player is in mouth";
                    break;

                case "eat":
                case "put_in_mouth":
                case "drop":
                case "dangle":
                case "dangle_drop":
                case "tease_mouth":
                case "tease_stomach":
                case "put_on_stomach":
                case "lick":
                case "put_in_bra":
                case "lower_into_mouth":
                case "random_tease":
                case "play_with_food":
                    if (!hasHeldObject && !playerHeld) return "no held object";
                    break;

                case "swallow":
                case "take_out_mouth":
                case "mouth_activity":
                    if (!hasObjectInMouth) return "no object in mouth";
                    break;

                case "take_off_stomach":
                    if (!playerHeld && !playerInStomach) return "player is not on/in stomach";
                    break;

                case "pat_stomach":
                case "watch_stomach":
                    if (!playerInStomach) return "player is not in stomach";
                    break;

                case "burp":
                    if (!playerInStomach && GetBurpBuildUp(ai) <= 0.01f)
                        return "no burp buildup and player is not in stomach";
                    break;
            }

            return null;
        }

        private bool TryForceInterruptBusyActions(MonoBehaviour ai, object activity)
        {
            bool clearedActivity = false;
            if (activity != null)
            {
                clearedActivity = TryInvokeByName(activity, "ClearQueue", true, true, true)
                    || TryInvokeByName(activity, "ClearCurrentQueue")
                    || TryInvokeByName(activity, "ClearQueue");
            }

            bool clearedMovement = false;
            var movement = GetMovementComponent(ai);
            if (movement != null)
            {
                bool queueClear = TryInvokeByName(movement, "QueueClear");
                bool customStop = TryInvokeByName(movement, "CustomAct_StopAll");
                clearedMovement = queueClear || customStop;
            }

            return clearedActivity || clearedMovement;
        }

        private bool IsScriptedState(string state)
        {
            return string.Equals(state, "GTSSCRIPT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "EXEC_SCRIPT", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsActivityQueueBusy(object activity)
        {
            if (activity == null) return false;

            var runningAny = GetMemberValue(activity, "IsRunningCodeOnAnySubFrame");
            if (runningAny is bool running && running)
                return true;

            var subframes = GetMemberValue(activity, "m_SubFrameList") as IEnumerable;
            if (subframes != null)
            {
                foreach (var subframe in subframes)
                {
                    if (subframe == null) continue;

                    int count = GetCollectionCount(GetMemberValue(subframe, "ActivityQueue"));
                    bool isDone = GetBoolMember(subframe, "IsDone", count == 0);
                    if (count > 0 && !isDone)
                        return true;
                }

                return false;
            }

            return GetCollectionCount(GetMemberValue(activity, "queue")) > 0;
        }

        private bool HasDesiredAction(MonoBehaviour ai)
        {
            object desired = InvokeNoArg(ai, "GetDesiredAction");
            object actionType = GetMemberValue(desired, "type");
            if (actionType == null) return false;

            string value = actionType.ToString();
            return !string.IsNullOrEmpty(value)
                && !string.Equals(value, "Nothing", StringComparison.OrdinalIgnoreCase);
        }

        private float GetBurpBuildUp(MonoBehaviour ai)
        {
            object stomach = _collector.GetAISubComponent(ai, "stomach");
            object value = GetMemberValue(stomach, "m_BurpBuildUp");
            if (value is float f) return f;
            if (value is IConvertible)
            {
                try { return Convert.ToSingle(value); }
                catch { return 0f; }
            }
            return 0f;
        }

        private string GetAIStateString(MonoBehaviour ai)
        {
            try
            {
                var fi = ai.GetType().GetField("m_State",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return fi?.GetValue(ai)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private object GetMovementComponent(MonoBehaviour ai)
        {
            try
            {
                var fi = ai.GetType().GetField("m_Movement",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var movement = fi?.GetValue(ai);
                if (movement != null) return movement;

                return _movementComponentType != null ? ai.GetComponent(_movementComponentType) : null;
            }
            catch
            {
                return null;
            }
        }

        private bool TryPickUpTarget(object activity, object playerTarget)
        {
            if (playerTarget == null)
            {
                _log.LogWarning("Cannot pick up: player target reference is null");
                return false;
            }

            // The older qGts_PickUp has no explicit target and can enqueue an invalid activity
            // when called from outside the game's own script context. Prefer target-aware APIs.
            if (TryInvokePickUpNew(activity, "qGts_PickUp_New2", playerTarget))
                return true;

            if (TryInvokePickUpNew(activity, "qGts_PickUp_New", playerTarget))
                return true;

            return TryInvokeWithSmartDefaults(activity, "qGts_PickUpTargetOverTable", playerTarget);
        }

        private bool TryInvokePickUpNew(object activity, string methodName, object playerTarget)
        {
            if (activity == null || playerTarget == null) return false;

            object hand = ParseEnum(FindType("EHandType"), "Right") ?? FirstEnumValue(FindType("EHandType"));
            object ikModifier = ParseEnum(FindType("AvatarIKGoalModifier"), "Grab")
                ?? ParseEnum(FindType("AvatarIKGoalModifier"), "Normal")
                ?? FirstEnumValue(FindType("AvatarIKGoalModifier"));
            object relationship = ParseEnum(FindType("GiantessRelationshipComparison"), "DOESNT_MATTER")
                ?? FirstEnumValue(FindType("GiantessRelationshipComparison"));
            object upsideDown = ParseEnum(FindType("EUpsideDownModifier"), "Never")
                ?? FirstEnumValue(FindType("EUpsideDownModifier"));
            object endState = ParseEnum(FindType("EPickUpTargetIntendedActivityType"), "HoldInFrontOfFace")
                ?? FirstEnumValue(FindType("EPickUpTargetIntendedActivityType"));

            if (methodName == "qGts_PickUp_New2")
            {
                return TryInvokeByName(activity, methodName,
                    playerTarget,        // Target
                    null,                // OnPickUpSuccess
                    null,                // OnPickUpFailed
                    null,                // OnObjectStolenFromUs
                    null,                // OnObjectMistaken
                    hand,
                    ikModifier,
                    relationship,
                    1.0f,                // fGrabDuration
                    0.25f,               // fIKTransitionDuration
                    1.0f,                // fEatSpeed
                    false,               // bMustBeAbleToSee
                    true,                // bAllowChase
                    true,                // bAllowPickUpSubsitution
                    true,                // bAllowBendDown
                    false,               // bAllowSquat
                    false,               // bAllowImmediatelyEat
                    false,               // bAllowSwallow
                    true,                // bAllowInterruptions
                    true,                // bHandFollowsTarget
                    false,               // bAllowDesiredEndStateChange
                    false,               // bAllowDriveBy
                    upsideDown,
                    null,                // fGetIsMistaken
                    endState,
                    null,                // ApplyDesiredEndState
                    null,                // BuildHelperInstSet
                    null                 // pConfig
                );
            }

            return TryInvokeByName(activity, methodName,
                playerTarget,
                null,
                null,
                null,
                null,
                hand,
                ikModifier,
                relationship,
                1.0f,
                0.25f,
                1.0f,
                false,
                true,
                true,
                true,
                false,
                false,
                false,
                true,
                true,
                false,
                false,
                upsideDown,
                null,
                endState,
                null,
                null,
                null
            );
        }

        /// <summary>
        /// Try to invoke a method with specific argument values.
        /// </summary>
        private bool TryInvokeByName(object obj, string methodName, params object[] args)
        {
            if (obj == null) return false;
            var type = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = type.GetMethods(flags).Where(m => m.Name == methodName).ToArray();

            foreach (var method in methods)
            {
                var parms = method.GetParameters();
                if (parms.Length != args.Length) continue;

                // Try to match param types
                bool match = true;
                object[] convertedArgs = new object[args.Length];
                for (int i = 0; i < parms.Length; i++)
                {
                    if (args[i] == null)
                    {
                        convertedArgs[i] = null;
                        if (parms[i].ParameterType.IsValueType && Nullable.GetUnderlyingType(parms[i].ParameterType) == null)
                            match = false;
                    }
                    else if (parms[i].ParameterType.IsAssignableFrom(args[i].GetType()))
                    {
                        convertedArgs[i] = args[i];
                    }
                    else
                    {
                        match = false;
                    }
                }

                if (!match) continue;

                try
                {
                    method.Invoke(obj, convertedArgs);
                    return true;
                }
                catch (Exception ex)
                {
                    if (_config.DebugLogging.Value)
                        _log.LogWarning($"Invoke {methodName} failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            return false;
        }

        private object InvokeNoArg(object obj, string methodName)
        {
            if (obj == null) return null;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var method = obj.GetType().GetMethods(flags)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 0);
                return method?.Invoke(obj, null);
            }
            catch (Exception ex)
            {
                if (_config.DebugLogging.Value)
                    _log.LogWarning($"InvokeNoArg {methodName} failed: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }

        private object GetMemberValue(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type = obj.GetType();

                var prop = type.GetProperty(name, flags);
                if (prop != null)
                    return prop.GetValue(obj, null);

                var field = type.GetField(name, flags);
                if (field != null)
                    return field.GetValue(obj);
            }
            catch (Exception ex)
            {
                if (_config.DebugLogging.Value)
                    _log.LogWarning($"GetMemberValue {name} failed: {ex.Message}");
            }

            return null;
        }

        private bool GetBoolMember(object obj, string name, bool defaultValue)
        {
            object value = GetMemberValue(obj, name);
            if (value is bool b) return b;
            return defaultValue;
        }

        private int GetCollectionCount(object collection)
        {
            if (collection == null) return 0;
            if (collection is ICollection nonGeneric) return nonGeneric.Count;

            try
            {
                var prop = collection.GetType().GetProperty("Count",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object value = prop?.GetValue(collection, null);
                if (value is int count) return count;
            }
            catch { }

            return 0;
        }

        private bool TryInvokeWithSmartDefaults(object target, string methodName, object playerTarget)
        {
            if (target == null) return false;

            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = type.GetMethods(flags)
                .Where(m => m.Name == methodName)
                .OrderBy(m => m.GetParameters().Length)
                .ToArray();

            foreach (var method in methods)
            {
                var parms = method.GetParameters();
                object[] args = new object[parms.Length];

                for (int i = 0; i < parms.Length; i++)
                    args[i] = BuildSmartDefault(parms[i], playerTarget);

                try
                {
                    method.Invoke(target, args);
                    _log.LogInfo($"Invoked {methodName}({parms.Length} params) successfully");
                    return true;
                }
                catch (Exception ex)
                {
                    if (_config.DebugLogging.Value)
                        _log.LogWarning($"Failed {methodName}({parms.Length} params): {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            return false;
        }

        private object BuildSmartDefault(ParameterInfo parameter, object playerTarget)
        {
            var pt = parameter.ParameterType;
            string name = parameter.Name ?? "";

            if (_scriptObjRefType != null && pt == _scriptObjRefType)
                return playerTarget;

            if (pt == typeof(bool))
            {
                if (name.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (name.IndexOf("ImmediatelyEat", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (name.IndexOf("Swallow", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (name.IndexOf("MustBeAbleToSee", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (name.IndexOf("Allow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf("HandFollowsTarget", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            if (pt == typeof(float) || pt == typeof(Single))
            {
                if (name.IndexOf("GrabDuration", StringComparison.OrdinalIgnoreCase) >= 0) return 1.0f;
                if (name.IndexOf("IKTransitionDuration", StringComparison.OrdinalIgnoreCase) >= 0) return 0.25f;
                if (name.IndexOf("EatSpeed", StringComparison.OrdinalIgnoreCase) >= 0) return 1.0f;
                return 0f;
            }

            if (pt == typeof(int)) return 0;
            if (pt == typeof(string)) return null;
            if (pt.IsEnum) return PreferredEnumValue(pt);
            if (pt.IsValueType) return Activator.CreateInstance(pt);

            return null;
        }

        private object PreferredEnumValue(Type enumType)
        {
            if (enumType == null || !enumType.IsEnum) return null;

            string[] preferredNames = {
                "DEFAULT", "Default", "NONE", "None", "ANY", "Any",
                "RIGHT", "Right", "RIGHT_HAND", "RightHand",
                "HOLD", "Hold", "NORMAL", "Normal"
            };

            foreach (var name in preferredNames)
            {
                try
                {
                    if (Enum.IsDefined(enumType, name))
                        return Enum.Parse(enumType, name);
                }
                catch { }
            }

            return FirstEnumValue(enumType);
        }

        private object CreatePlayerReference()
        {
            if (_scriptObjRefType == null) return null;

            try
            {
                GameObject playerObj = null;

                if (_fpsType != null)
                {
                    var fps = UnityEngine.Object.FindObjectOfType(_fpsType);
                    if (fps != null)
                    {
                        var fpsComp = fps as Component;
                        playerObj = fpsComp != null ? fpsComp.gameObject : null;

                        var digestRef = TryCreateDigestablePlayerReference(playerObj, fps);
                        if (digestRef != null) return digestRef;
                    }
                }

                if (playerObj == null)
                    playerObj = GameObject.FindGameObjectWithTag("Player");

                if (playerObj != null)
                {
                    var digestRef = TryCreateDigestablePlayerReference(playerObj, null);
                    if (digestRef != null) return digestRef;

                    var ctor = _scriptObjRefType.GetConstructor(new[] { typeof(GameObject) });
                    if (ctor != null) return ctor.Invoke(new object[] { playerObj });
                }

                if (_scriptObjectTypeEnum != null)
                {
                    object playerType = ParseEnum(_scriptObjectTypeEnum, "PLAYER");
                    var ctor = _scriptObjRefType.GetConstructor(new[] { _scriptObjectTypeEnum });
                    if (playerType != null && ctor != null) return ctor.Invoke(new[] { playerType });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to create player ScriptObjectReference: {ex.Message}");
            }

            return null;
        }

        private object TryCreateDigestablePlayerReference(GameObject playerObj, object fps)
        {
            if (_digestableObjectType == null || _gameEntityType == null || playerObj == null)
                return null;

            Component digest = null;
            try
            {
                digest = playerObj.GetComponent(_digestableObjectType);

                if (digest == null && fps != null)
                {
                    var allDigestables = UnityEngine.Object.FindObjectsOfType(_digestableObjectType);
                    foreach (var obj in allDigestables)
                    {
                        var comp = obj as Component;
                        if (comp == null) continue;

                        var fi = _digestableObjectType.GetField("m_FPSBehaviour",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (fi != null && ReferenceEquals(fi.GetValue(comp), fps))
                        {
                            digest = comp;
                            break;
                        }
                    }
                }

                if (digest == null)
                    return null;

                object entity = Activator.CreateInstance(_gameEntityType);
                var setHandle = _gameEntityType.GetMethod("SetHandle",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                setHandle?.Invoke(entity, new object[] { digest });

                var ctor = _scriptObjRefType.GetConstructor(new[] { _gameEntityType });
                if (ctor != null)
                {
                    _log.LogInfo("Created player ScriptObjectReference via DigestableObject/GameEntity");
                    return ctor.Invoke(new[] { entity });
                }
            }
            catch (Exception ex)
            {
                if (_config.DebugLogging.Value)
                    _log.LogWarning($"Digestable player reference failed: {ex.Message}");
            }

            return null;
        }

        // ──────────────────── Utility ────────────────────

        private Type FindType(string name)
        {
            if (_asm == null) return null;
            return _asm.GetTypes().FirstOrDefault(t => t.Name == name);
        }

        private Type FindNestedType(Type type, string name)
        {
            if (type == null) return null;
            return type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(t => t.Name == name);
        }

        private object ParseEnum(Type enumType, string valueName)
        {
            if (enumType == null || string.IsNullOrEmpty(valueName)) return null;
            try { return Enum.Parse(enumType, valueName, true); }
            catch { return null; }
        }

        private object FirstEnumValue(Type enumType)
        {
            if (enumType == null || !enumType.IsEnum) return null;
            var vals = Enum.GetValues(enumType);
            return vals.Length > 0 ? vals.GetValue(0) : null;
        }

        private void SetField(object obj, string fieldName, object value)
        {
            if (obj == null || value == null) return;
            var fi = obj.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null)
            {
                try { fi.SetValue(obj, value); }
                catch (Exception ex) { _log.LogWarning($"SetField {fieldName}: {ex.Message}"); }
            }
        }

        private string Truncate(string s, int max) =>
            s != null && s.Length > max ? s.Substring(0, max) + "..." : s;

        private struct ActionGateDecision
        {
            public bool Allow;
            public string Reason;

            public static ActionGateDecision Accept(string reason)
            {
                return new ActionGateDecision { Allow = true, Reason = reason };
            }

            public static ActionGateDecision Reject(string reason)
            {
                return new ActionGateDecision { Allow = false, Reason = reason ?? "blocked" };
            }
        }

        private class SurfaceTarget
        {
            public string Name;
            public Vector3 DropPoint;
            public Bounds Bounds;
        }
    }
}

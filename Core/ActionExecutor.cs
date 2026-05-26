using System;
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
        private Type _fpsType;
        private Type _movementType;
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
            _fpsType = FindType("FPSBehaviour");
            _movementType = FindType("GiantessMovementType");

            _cacheDone = true;
            _log.LogInfo($"ActionExecutor cache: EyesFlex={_eyesFlexType != null}, MouthFlex={_mouthFlexType != null}, " +
                $"SoundLoudness={_soundLoudnessType != null}");
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
                ExecuteAction(ai, response.Action);
                logParts.Add($"Action: {response.Action}");
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

                // Method: qGts_SetFaceFlex(Action OnDone, CActivityCommonConfig pConfig,
                //         EyesFlexType, MouthFlexType, Boolean isMouthOpen, Boolean isMouthAlwaysOpen, Boolean enableRandomSwallow)
                var activity = _collector.GetAISubComponent(ai, "activity");
                bool success = TryInvokeByName(activity, "qGts_SetFaceFlex",
                    null,       // Action OnDone
                    null,       // CActivityCommonConfig pConfig
                    eyesVal,    // EyesFlexType
                    mouthVal,   // MouthFlexType
                    false,      // bIsMouthOpen
                    false,      // bIsMouthAlwaysOpen
                    false       // bEnableRandomSwallow
                );

                if (!success)
                {
                    // Fallback: set fields directly on GiantessEmotion
                    var emotionComp = _collector.GetAISubComponent(ai, "emotion");
                    if (emotionComp != null)
                    {
                        bool eyeOk = TryInvokeByName(emotionComp, "SetEyesFlex", eyesVal, 0.25f, true);
                        bool mouthOk = TryInvokeByName(emotionComp, "SetMouthFlex", mouthVal, 0.25f, true);

                        if (!eyeOk) SetField(emotionComp, "m_EyesFlexType", eyesVal);
                        if (!mouthOk) SetField(emotionComp, "m_MouthFlexType", mouthVal);

                        _log.LogInfo($"Set emotion via GiantessEmotion: {mapping.Eyes}, {mapping.Mouth}");
                    }
                }
                else
                {
                    _log.LogInfo($"Set emotion via qGts_SetFaceFlex: {mapping.Eyes}, {mapping.Mouth}");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to set emotion '{emotion}': {ex.Message}");
            }
        }

        // ──────────────────── Actions ────────────────────

        private void ExecuteAction(MonoBehaviour ai, string action)
        {
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

        private bool TryPickUpTarget(object activity, object playerTarget)
        {
            if (playerTarget == null)
            {
                _log.LogWarning("Cannot pick up: player target reference is null");
                return false;
            }

            // The older qGts_PickUp has no explicit target and can enqueue an invalid activity
            // when called from outside the game's own script context. Prefer target-aware APIs.
            if (TryInvokeWithSmartDefaults(activity, "qGts_PickUp_New2", playerTarget))
                return true;

            if (TryInvokeWithSmartDefaults(activity, "qGts_PickUp_New", playerTarget))
                return true;

            return TryInvokeWithSmartDefaults(activity, "qGts_PickUpTargetOverTable", playerTarget);
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
                if (_fpsType != null)
                {
                    var fps = UnityEngine.Object.FindObjectOfType(_fpsType);
                    if (fps != null)
                    {
                        var ctor = _scriptObjRefType.GetConstructor(new[] { _fpsType });
                        if (ctor != null) return ctor.Invoke(new object[] { fps });
                    }
                }

                GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null)
                {
                    var ctor = _scriptObjRefType.GetConstructor(new[] { typeof(GameObject) });
                    if (ctor != null) return ctor.Invoke(new object[] { playerObj });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to create player ScriptObjectReference: {ex.Message}");
            }

            return null;
        }

        // ──────────────────── Utility ────────────────────

        private Type FindType(string name)
        {
            if (_asm == null) return null;
            return _asm.GetTypes().FirstOrDefault(t => t.Name == name);
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
    }
}

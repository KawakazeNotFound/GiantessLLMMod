using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using GiantessLLMMod.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GiantessLLMMod.Core
{
    /// <summary>
    /// Collects game state from live Unity objects using reflection.
    /// Caches Type and FieldInfo lookups for performance.
    /// </summary>
    public class GameStateCollector
    {
        private readonly ManualLogSource _log;
        private readonly ConfigManager _config;

        // Cached assembly + types
        private Assembly _gameAssembly;
        private Type _giantessAIType;
        private Type _fpsType;
        private Type _digestType;
        private Type _heightCacheType;
        private Type _personalityType;
        private Type _stomachType;
        private Type _entityMemoryType;
        private Type _reimuType;
        private Type _firstPersonAioType;
        private Type _mouthTriggerType;

        // Scene-local object registry. Unity discovery APIs are intentionally kept
        // out of recurring snapshot/event paths and are rerun only after a scene change.
        private int _cachedSceneHandle = int.MinValue;
        private MonoBehaviour _cachedFps;
        private MonoBehaviour _cachedReimu;
        private MonoBehaviour _cachedFirstPersonAio;
        private MonoBehaviour[] _cachedGiantessAIs = Array.Empty<MonoBehaviour>();
        private MonoBehaviour[] _cachedMouthTriggers = Array.Empty<MonoBehaviour>();
        private readonly List<SceneColliderEntry> _sceneColliderEntries = new List<SceneColliderEntry>();
        private float _nextMissingObjectRetryTime;

        private sealed class SceneColliderEntry
        {
            public Collider Collider;
            public int RootId;
            public string Name;
            public string Kind;
        }

        // Cached field/method info
        private bool _cacheBuilt = false;

        // GiantessAI fields
        private FieldInfo _ai_state;
        private FieldInfo _ai_personality;
        private FieldInfo _ai_stomach;
        private FieldInfo _ai_emotion;
        private FieldInfo _ai_memory;
        private FieldInfo _ai_activityInterface;
        private FieldInfo _ai_cache;
        private FieldInfo _ai_conversation;
        private FieldInfo _ai_horniness;
        private FieldInfo _ai_hunger;
        private PropertyInfo _ai_heldObject;
        private PropertyInfo _ai_heldObjectLeft;
        private PropertyInfo _ai_objectInMouth;
        private PropertyInfo _ai_playerMemory;
        private MethodInfo _ai_rememberEntity;

        // Personality fields
        private FieldInfo _pers_hunger;
        private FieldInfo _pers_playfulness;
        private FieldInfo _pers_predatorType;

        // Stomach fields
        private FieldInfo _stom_activity;
        private FieldInfo _stom_acid;
        private FieldInfo _stom_burp;
        private FieldInfo _stom_food;

        // DigestableObject fields
        private FieldInfo _dig_health;
        private FieldInfo _dig_alive;
        private FieldInfo _dig_inStomach;
        private FieldInfo _dig_inMouth;
        private FieldInfo _dig_held;

        // EntityMemory fields
        private FieldInfo _mem_canSee;
        private FieldInfo _mem_canReallySee;
        private FieldInfo _mem_currentLocation;
        private FieldInfo _mem_relationship;
        private FieldInfo _mem_relStrength;
        private FieldInfo _mem_interest;
        private FieldInfo _mem_anger;
        private FieldInfo _mem_wantsAttention;
        private FieldInfo _mem_hasSeenBefore;
        private FieldInfo _mem_hasSpokenWith;
        private FieldInfo _mem_hasPickedUp;
        private FieldInfo _mem_hasSwallowed;
        private FieldInfo _mem_hasTeased;
        private FieldInfo _mem_isBeingHeld;
        private FieldInfo _mem_isTouchingUs;
        private FieldInfo _mem_isStandingOnUs;
        private FieldInfo _mem_isLookingAtUs;
        private FieldInfo _mem_timeInLocation;
        private PropertyInfo _mem_canSeeProp;
        private PropertyInfo _mem_canReallySeeProp;
        private PropertyInfo _mem_currentLocationProp;
        private PropertyInfo _mem_relationshipProp;
        private PropertyInfo _mem_relStrengthProp;
        private PropertyInfo _mem_interestProp;
        private PropertyInfo _mem_angerProp;
        private PropertyInfo _mem_wantsAttentionProp;
        private PropertyInfo _mem_hasSeenBeforeProp;
        private PropertyInfo _mem_hasSpokenWithProp;
        private PropertyInfo _mem_hasPickedUpProp;
        private PropertyInfo _mem_hasSwallowedProp;
        private PropertyInfo _mem_hasTeasedProp;
        private PropertyInfo _mem_isBeingHeldProp;
        private PropertyInfo _mem_isTouchingUsProp;
        private PropertyInfo _mem_isStandingOnUsProp;
        private PropertyInfo _mem_isLookingAtUsProp;
        private PropertyInfo _mem_timeInLocationProp;

        private readonly Dictionary<string, StomachTrendSample> _previousStomachSamples = new Dictionary<string, StomachTrendSample>();

        private struct StomachTrendSample
        {
            public float Time;
            public float Activity;
            public float Acid;
            public float Burp;
        }

        public GameStateCollector(ManualLogSource log, ConfigManager config = null)
        {
            _log = log;
            _config = config;
        }

        /// <summary>
        /// Build reflection cache. Call once after game level loads.
        /// </summary>
        public bool BuildCache()
        {
            try
            {
                _gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

                if (_gameAssembly == null)
                {
                    _log.LogError("Assembly-CSharp not found!");
                    return false;
                }

                _giantessAIType = FindType("GiantessAI");
                _fpsType = FindType("FPSBehaviour");
                _digestType = FindType("DigestableObject");
                _heightCacheType = FindType("HeightCache");
                _personalityType = FindType("GiantessPersonality");
                _stomachType = FindType("StomachLogic");
                _entityMemoryType = FindType("EntityMemory");
                _reimuType = FindType("ReimuAnimationController");
                _firstPersonAioType = FindType("FirstPersonAIO");
                _mouthTriggerType = FindType("MouthTrigger");

                if (_giantessAIType != null)
                    CacheGiantessFields();
                if (_personalityType != null)
                    CachePersonalityFields();
                if (_stomachType != null)
                    CacheStomachFields();
                if (_digestType != null)
                    CacheDigestFields();
                if (_entityMemoryType != null)
                    CacheEntityMemoryFields();

                _cacheBuilt = true;
                RefreshSceneCache(force: true);
                _log.LogInfo($"Reflection cache built. GiantessAI={_giantessAIType != null}, FPS={_fpsType != null}, Digest={_digestType != null}");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"Failed to build reflection cache: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Collect a full game state snapshot.
        /// </summary>
        public GameStateSnapshot CollectState(bool includeSceneObjects = true)
        {
            if (!_cacheBuilt)
            {
                BuildCache();
                if (!_cacheBuilt) return null;
            }

            RefreshSceneCache();

            var snapshot = new GameStateSnapshot
            {
                Timestamp = Time.time
            };

            // Collect player data
            snapshot.Player = CollectPlayerState();

            // Collect all giantess data
            snapshot.Giantesses = CollectGiantessStates(snapshot.Player);
            if (includeSceneObjects)
                snapshot.SceneObjects = CollectSceneObjectCandidates(snapshot.Player);

            return snapshot;
        }

        /// <summary>
        /// Collect only player flags required by EventWatcher. This deliberately
        /// excludes giantess snapshots, scene candidates, sorting and trend samples.
        /// </summary>
        public PlayerState CollectEventPlayerState()
        {
            if (!_cacheBuilt)
            {
                BuildCache();
                if (!_cacheBuilt) return null;
            }

            RefreshSceneCache();
            return CollectPlayerState();
        }

        // ──────────────────── Player ────────────────────

        private PlayerState CollectPlayerState()
        {
            var state = new PlayerState();

            try
            {
                // Prefer FPSBehaviour. The tagged player object is not always the object
                // that owns ReimuAnimationController/FPS fields.
                MonoBehaviour fpsMb = _cachedFps;

                GameObject playerObj = fpsMb != null
                    ? fpsMb.gameObject
                    : GameObject.FindGameObjectWithTag("Player");

                if (playerObj == null) return state;

                var pos = playerObj.transform.position;
                state.X = pos.x;
                state.Y = pos.y;
                state.Z = pos.z;

                // Get DigestableObject component
                if (_digestType != null)
                {
                    var digest = playerObj.GetComponent(_digestType);
                    if (digest == null)
                    {
                        // It might be on a child/parent
                        digest = playerObj.GetComponentInChildren(_digestType);
                    }

                    if (digest != null)
                    {
                        state.Health = GetField<float>(digest, _dig_health, 100f);
                        state.IsAlive = GetField<bool>(digest, _dig_alive, true);
                        state.InStomach = GetField<bool>(digest, _dig_inStomach, false);
                        state.InMouth = GetField<bool>(digest, _dig_inMouth, false);
                        state.IsBeingHeld = GetField<bool>(digest, _dig_held, false);
                    }
                }

                // Try to get FPSBehaviour for more accurate player data
                if (_fpsType != null)
                {
                    var fps = fpsMb ?? playerObj.GetComponent(_fpsType);
                    if (fps == null) fps = playerObj.GetComponentInChildren(_fpsType);
                    if (fps != null)
                    {
                        // From probe: _health=100, maxHealth=100, m_IsDead=False, m_InStomach=False, m_BeingSwallowed=False
                        state.Health = GetFieldByName<float>(fps, "_health");
                        state.MaxHealth = GetFieldByName<float>(fps, "maxHealth");
                        state.IsAlive = !GetFieldByName<bool>(fps, "m_IsDead");
                        state.InStomach = GetFieldByName<bool>(fps, "m_InStomach");
                        state.IsBeingHeld = GetFieldByName<bool>(fps, "isGrabbed");

                        // Check ReimuAnimationController for mouth/held state
                        if (_reimuType != null)
                        {
                            var reimu = _cachedReimu ?? playerObj.GetComponentInChildren(_reimuType);
                            if (reimu == null)
                            {
                                var reimuProp = fps.GetType().GetProperty("Reimu", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                reimu = GetPropertyValue(fps, reimuProp) as MonoBehaviour;
                            }
                            if (reimu != null)
                            {
                                state.InMouth = state.InMouth || GetFieldByName<bool>(reimu, "m_IsInMouth");
                                state.IsBeingHeld = state.IsBeingHeld || GetFieldByName<bool>(reimu, "m_IsBeingHeld");
                            }
                        }

                        // Check FirstPersonAIO for grounded/sliding state
                        if (_firstPersonAioType != null)
                        {
                            var fpa = _cachedFirstPersonAio ?? playerObj.GetComponent(_firstPersonAioType);
                            if (fpa == null) fpa = playerObj.GetComponentInChildren(_firstPersonAioType);
                            if (fpa != null)
                            {
                                state.IsBeingHeld = state.IsBeingHeld || GetFieldByName<bool>(fpa, "isGrabbed");
                            }
                        }
                    }
                }

                state.InMouth = state.InMouth || IsPlayerInMouthFromGiantessAI() || IsPlayerInMouthFromMouthTrigger();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Error collecting player state: {ex.Message}");
            }

            return state;
        }

        private bool IsPlayerInMouthFromGiantessAI()
        {
            if (_giantessAIType == null) return false;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (var ai in _cachedGiantessAIs)
                {
                    if (ai == null) continue;

                    var playerInMouthProp = ai.GetType().GetProperty("playerInMouth", flags)
                        ?? ai.GetType().GetProperty("PlayerInMouth", flags);
                    var playerInMouth = GetPropertyValue(ai, playerInMouthProp);
                    if (playerInMouth != null)
                    {
                        if (playerInMouth is bool b && b) return true;
                        if (!(playerInMouth is bool)) return true;
                    }

                    var objectInMouthProp = ai.GetType().GetProperty("objectInMouth", flags)
                        ?? ai.GetType().GetProperty("ObjectInMouth", flags);
                    if (GetPropertyValue(ai, objectInMouthProp) != null)
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (_log != null) _log.LogWarning($"Error checking GiantessAI mouth state: {ex.Message}");
            }

            return false;
        }

        private bool IsPlayerInMouthFromMouthTrigger()
        {
            if (_mouthTriggerType == null) return false;

            try
            {
                foreach (var trigger in _cachedMouthTriggers)
                {
                    if (GetFieldByName<bool>(trigger, "m_IsPlayerInMouth"))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (_log != null) _log.LogWarning($"Error checking MouthTrigger state: {ex.Message}");
            }

            return false;
        }

        // ──────────────────── Giantess ────────────────────

        private List<GiantessState> CollectGiantessStates(PlayerState player)
        {
            var list = new List<GiantessState>();

            if (_giantessAIType == null) return list;

            try
            {
                foreach (var ai in _cachedGiantessAIs)
                {
                    try
                    {
                        var gs = CollectSingleGiantess(ai, player);
                        if (gs != null) list.Add(gs);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"Error collecting giantess: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Error finding GiantessAI objects: {ex.Message}");
            }

            return list.OrderBy(g => g.DistanceToPlayer).ToList();
        }

        private GiantessState CollectSingleGiantess(MonoBehaviour ai, PlayerState player)
        {
            if (ai == null) return null;

            var gs = new GiantessState();
            gs.Name = ai.gameObject.name;

            var pos = ai.transform.position;
            gs.X = pos.x;
            gs.Y = pos.y;
            gs.Z = pos.z;

            // Distance to player
            gs.DistanceToPlayer = Vector3.Distance(
                new Vector3(player.X, player.Y, player.Z),
                pos
            );

            // Current state
            var stateVal = GetField<object>(ai, _ai_state, null);
            gs.CurrentState = stateVal?.ToString() ?? "Unknown";

            // Horniness (might be directly on GiantessAI)
            gs.Horniness = GetField<float>(ai, _ai_horniness, 0f);

            // Hunger is directly on GiantessAI (probe confirmed: m_Hunger = 0.2152479)
            gs.Hunger = GetField<float>(ai, _ai_hunger, 0f);
            
            // Personality name
            var personality = GetField<object>(ai, _ai_personality, null);
            if (personality != null)
            {
                // m_PersonalityName is AINameData - try to get .name or ToString()
                var nameField = FindField(personality.GetType(), "m_PersonalityName");
                if (nameField != null)
                {
                    var nameObj = nameField.GetValue(personality);
                    if (nameObj != null) gs.Name = nameObj.ToString();
                }
            }

            // Stomach sub-object
            var stomach = GetField<object>(ai, _ai_stomach, null);
            if (stomach != null)
            {
                gs.StomachActivity = GetField<float>(stomach, _stom_activity, 0f);
                gs.StomachAcid = GetField<float>(stomach, _stom_acid, 0f);
                gs.BurpBuildUp = GetField<float>(stomach, _stom_burp, 0f);
                gs.DigestedFood = GetField<float>(stomach, _stom_food, 0f);
                ApplyStomachTrend(gs);
            }

            // Held objects
            gs.HeldObjectName = GetPropertyStr(ai, _ai_heldObject);
            gs.HeldObjectLeftName = GetPropertyStr(ai, _ai_heldObjectLeft);

            var mouthObj = GetPropertyValue(ai, _ai_objectInMouth);
            gs.HasObjectInMouth = mouthObj != null;

            // Memory about player
            gs.PlayerMemory = CollectPlayerMemory(ai);

            return gs;
        }

        private void ApplyStomachTrend(GiantessState gs)
        {
            string key = gs.Name ?? "Unknown";
            float now = Time.time;
            float threshold = _config?.TrendThreshold.Value ?? 0.05f;

            if (_previousStomachSamples.TryGetValue(key, out var prev))
            {
                float dt = Math.Max(0.001f, now - prev.Time);

                gs.StomachActivityDelta = gs.StomachActivity - prev.Activity;
                gs.StomachActivityRate = gs.StomachActivityDelta / dt;
                if (Math.Abs(gs.StomachActivityRate) < threshold) gs.StomachActivityRate = 0f;

                gs.StomachAcidDelta = gs.StomachAcid - prev.Acid;
                gs.StomachAcidRate = gs.StomachAcidDelta / dt;
                if (Math.Abs(gs.StomachAcidRate) < threshold) gs.StomachAcidRate = 0f;

                gs.BurpBuildUpDelta = gs.BurpBuildUp - prev.Burp;
                gs.BurpBuildUpRate = gs.BurpBuildUpDelta / dt;
                if (Math.Abs(gs.BurpBuildUpRate) < threshold) gs.BurpBuildUpRate = 0f;
            }

            _previousStomachSamples[key] = new StomachTrendSample
            {
                Time = now,
                Activity = gs.StomachActivity,
                Acid = gs.StomachAcid,
                Burp = gs.BurpBuildUp
            };
        }

        private List<SceneObjectCandidate> CollectSceneObjectCandidates(PlayerState player)
        {
            var candidates = new List<SceneObjectCandidate>();

            try
            {
                var playerPos = new Vector3(player.X, player.Y, player.Z);
                foreach (var entry in _sceneColliderEntries)
                {
                    var col = entry.Collider;
                    if (col == null || !col.enabled || col.isTrigger)
                        continue;

                    var go = col.gameObject;
                    if (go == null || !go.activeInHierarchy)
                        continue;

                    Bounds b = col.bounds;
                    if (b.size.x < 0.5f || b.size.z < 0.5f || b.size.y < 0.03f)
                        continue;

                    candidates.Add(new SceneObjectCandidate
                    {
                        Id = $"{entry.Kind}:{entry.RootId}",
                        Name = entry.Name,
                        Kind = entry.Kind,
                        X = b.center.x,
                        Y = b.center.y,
                        Z = b.center.z,
                        TopY = b.max.y,
                        Width = b.size.x,
                        Depth = b.size.z,
                        Height = b.size.y,
                        DistanceToPlayer = Vector3.Distance(playerPos, b.center)
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Error collecting scene objects: {ex.Message}");
            }

            return candidates
                .OrderBy(c => c.Kind == "table" || c.Kind == "desk" ? 0 : 1)
                .ThenBy(c => c.DistanceToPlayer)
                .Take(_config?.MaxSceneObjects.Value ?? 16)
                .ToList();
        }

        /// <summary>
        /// Rebuild references and the classified collider index once per Unity scene.
        /// Missing gameplay objects get a low-frequency retry because some scenes spawn
        /// them shortly after sceneLoaded.
        /// </summary>
        private void RefreshSceneCache(bool force = false)
        {
            int sceneHandle = SceneManager.GetActiveScene().handle;
            bool sceneChanged = sceneHandle != _cachedSceneHandle;
            bool missingGameplayObjects = _cachedFps == null ||
                (_giantessAIType != null && _cachedGiantessAIs.All(ai => ai == null));

            if (!force && !sceneChanged)
            {
                if (!missingGameplayObjects || Time.unscaledTime < _nextMissingObjectRetryTime)
                    return;
            }

            _cachedSceneHandle = sceneHandle;
            _nextMissingObjectRetryTime = Time.unscaledTime + 1f;

            _cachedFps = _fpsType == null
                ? null
                : UnityEngine.Object.FindObjectOfType(_fpsType) as MonoBehaviour;
            _cachedReimu = _reimuType == null
                ? null
                : UnityEngine.Object.FindObjectOfType(_reimuType) as MonoBehaviour;
            _cachedFirstPersonAio = _firstPersonAioType == null
                ? null
                : UnityEngine.Object.FindObjectOfType(_firstPersonAioType) as MonoBehaviour;
            _cachedGiantessAIs = FindMonoBehaviours(_giantessAIType);
            _cachedMouthTriggers = FindMonoBehaviours(_mouthTriggerType);

            if (force || sceneChanged)
                RebuildSceneColliderIndex();
        }

        private MonoBehaviour[] FindMonoBehaviours(Type type)
        {
            if (type == null) return Array.Empty<MonoBehaviour>();

            return UnityEngine.Object.FindObjectsOfType(type)
                .OfType<MonoBehaviour>()
                .ToArray();
        }

        private void RebuildSceneColliderIndex()
        {
            _sceneColliderEntries.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    if (col == null || !col.enabled || col.isTrigger)
                        continue;

                    var go = col.gameObject;
                    if (go == null || !go.activeInHierarchy)
                        continue;

                    if ((_giantessAIType != null && go.GetComponentInParent(_giantessAIType) != null) ||
                        (_fpsType != null && go.GetComponentInParent(_fpsType) != null))
                        continue;

                    string name = GetHierarchyName(go);
                    string kind = ClassifySceneObject(name);
                    if (kind == null)
                        continue;

                    Bounds b = col.bounds;
                    if (b.size.x < 0.5f || b.size.z < 0.5f || b.size.y < 0.03f)
                        continue;

                    int rootId = go.transform.root.gameObject.GetInstanceID();
                    if (!seen.Add(kind + ":" + rootId))
                        continue;

                    _sceneColliderEntries.Add(new SceneColliderEntry
                    {
                        Collider = col,
                        RootId = rootId,
                        Name = name,
                        Kind = kind
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Error indexing scene objects: {ex.Message}");
            }
        }

        private string GetHierarchyName(GameObject go)
        {
            try
            {
                var names = new List<string>();
                Transform t = go.transform;
                int limit = 0;
                while (t != null && limit++ < 4)
                {
                    names.Add(t.name);
                    t = t.parent;
                }
                return string.Join("/", names.ToArray());
            }
            catch
            {
                return go != null ? go.name : "";
            }
        }

        private string ClassifySceneObject(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.ToLowerInvariant();

            string[] tableKws = (_config?.TableKeywords.Value ?? "table,desk,桌,counter,bench")
                .Split(',')
                .Select(k => k.Trim().ToLower())
                .Where(k => !string.IsNullOrEmpty(k))
                .ToArray();
            string[] bedKws = (_config?.BedKeywords.Value ?? "bed,床,sofa,couch")
                .Split(',')
                .Select(k => k.Trim().ToLower())
                .Where(k => !string.IsNullOrEmpty(k))
                .ToArray();

            foreach (var kw in tableKws)
            {
                if (n.Contains(kw)) return "table";
            }

            foreach (var kw in bedKws)
            {
                if (n.Contains(kw)) return "bed";
            }

            if (n.Contains("shelf") || n.Contains("cabinet"))
                return "shelf";
            if (n.Contains("floor") || n.Contains("ground"))
                return "floor";

            return null;
        }

        private PlayerMemoryData CollectPlayerMemory(MonoBehaviour ai)
        {
            var data = new PlayerMemoryData();

            try
            {
                if (_entityMemoryType == null) return data;

                object memory = null;

                // Best path confirmed by probe: GiantessAI has a playerMemory property.
                memory = GetPropertyValue(ai, _ai_playerMemory);

                // Fallback: find the player object and ask the memory module for it.
                GameObject playerObj = null;
                if (memory == null)
                {
                    playerObj = GameObject.FindGameObjectWithTag("Player");
                    if (playerObj == null) return data;
                }

                if (memory == null && _ai_rememberEntity != null)
                {
                    memory = TryInvokeRememberEntity(_ai_rememberEntity, ai, playerObj);
                }

                if (memory == null)
                {
                    var memoryModule = GetField<object>(ai, _ai_memory, null);
                    if (memoryModule != null)
                    {
                        var remMethod = FindRememberEntityMethod(memoryModule.GetType());
                        if (remMethod != null)
                            memory = TryInvokeRememberEntity(remMethod, memoryModule, playerObj);
                    }
                }

                if (memory == null) return data;

                // Read EntityMemory fields
                data.CanSee = GetProperty<bool>(memory, _mem_canSeeProp, GetField<bool>(memory, _mem_canSee, false));
                data.CanReallySee = GetProperty<bool>(memory, _mem_canReallySeeProp, GetField<bool>(memory, _mem_canReallySee, false));
                var loc = GetProperty<object>(memory, _mem_currentLocationProp, GetField<object>(memory, _mem_currentLocation, null));
                data.CurrentLocation = loc?.ToString() ?? "Unknown";
                var rel = GetProperty<object>(memory, _mem_relationshipProp, GetField<object>(memory, _mem_relationship, null));
                data.Relationship = rel?.ToString() ?? "Unknown";
                data.RelationshipStrength = GetProperty<float>(memory, _mem_relStrengthProp, GetField<float>(memory, _mem_relStrength, 0f));
                data.Interest = GetProperty<float>(memory, _mem_interestProp, GetField<float>(memory, _mem_interest, 0f));
                data.AngerValue = GetProperty<float>(memory, _mem_angerProp, GetField<float>(memory, _mem_anger, 0f));
                data.WantsAttention = GetProperty<float>(memory, _mem_wantsAttentionProp, GetField<float>(memory, _mem_wantsAttention, 0f));
                data.HasSeenBefore = GetProperty<bool>(memory, _mem_hasSeenBeforeProp, GetField<bool>(memory, _mem_hasSeenBefore, false));
                data.HasSpokenWith = GetProperty<bool>(memory, _mem_hasSpokenWithProp, GetField<bool>(memory, _mem_hasSpokenWith, false));
                data.HasPickedUp = GetProperty<bool>(memory, _mem_hasPickedUpProp, GetField<bool>(memory, _mem_hasPickedUp, false));
                data.HasSwallowed = GetProperty<bool>(memory, _mem_hasSwallowedProp, GetField<bool>(memory, _mem_hasSwallowed, false));
                data.HasTeased = GetProperty<bool>(memory, _mem_hasTeasedProp, GetField<bool>(memory, _mem_hasTeased, false));
                data.IsBeingHeld = GetProperty<bool>(memory, _mem_isBeingHeldProp, GetField<bool>(memory, _mem_isBeingHeld, false));
                data.IsTouchingUs = GetProperty<bool>(memory, _mem_isTouchingUsProp, GetField<bool>(memory, _mem_isTouchingUs, false));
                data.IsStandingOnUs = GetProperty<bool>(memory, _mem_isStandingOnUsProp, GetField<bool>(memory, _mem_isStandingOnUs, false));
                data.IsLookingAtUs = GetProperty<bool>(memory, _mem_isLookingAtUsProp, GetField<bool>(memory, _mem_isLookingAtUs, false));
                data.TimeSpentInCurrentLocation = GetProperty<float>(memory, _mem_timeInLocationProp, GetField<float>(memory, _mem_timeInLocation, 0f));
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Error collecting player memory: {ex.Message}");
            }

            return data;
        }

        // ──────────────────── Cache Building ────────────────────

        private void CacheGiantessFields()
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _ai_state = FindField(_giantessAIType, "m_State", "m_eCurrentState", "m_CurrentState");
            _ai_personality = FindField(_giantessAIType, "m_Personality");
            _ai_stomach = FindField(_giantessAIType, "m_Stomach");
            _ai_emotion = FindField(_giantessAIType, "m_Emotion");
            _ai_memory = FindField(_giantessAIType, "m_Memory");
            _ai_activityInterface = FindField(_giantessAIType, "m_ActivityInterface");
            _ai_cache = FindField(_giantessAIType, "m_Cache");
            _ai_conversation = FindField(_giantessAIType, "m_Conversation");
            _ai_horniness = FindField(_giantessAIType, "m_Horniness");
            _ai_hunger = FindField(_giantessAIType, "m_Hunger");

            // Properties for held objects
            _ai_heldObject = _giantessAIType.GetProperty("heldObject", flags)
                ?? _giantessAIType.GetProperty("HeldObject", flags);
            _ai_heldObjectLeft = _giantessAIType.GetProperty("heldObjectLeftHand", flags)
                ?? _giantessAIType.GetProperty("HeldObjectLeftHand", flags);
            _ai_objectInMouth = _giantessAIType.GetProperty("objectInMouth", flags)
                ?? _giantessAIType.GetProperty("ObjectInMouth", flags);
            _ai_playerMemory = _giantessAIType.GetProperty("playerMemory", flags)
                ?? _giantessAIType.GetProperty("PlayerMemory", flags);

            // If properties not found, try fields
            if (_ai_heldObject == null)
            {
                var f = FindField(_giantessAIType, "heldObject", "m_HeldObject", "m_heldObject");
                // Store as FieldInfo, we'll handle in GetPropertyStr
            }

            _ai_rememberEntity = FindRememberEntityMethod(_giantessAIType);

            LogCacheResult("GiantessAI.m_eCurrentState", _ai_state);
            LogCacheResult("GiantessAI.m_Personality", _ai_personality);
            LogCacheResult("GiantessAI.m_Stomach", _ai_stomach);
            LogCacheResult("GiantessAI.m_Memory", _ai_memory);
            LogCacheResult("GiantessAI.playerMemory", _ai_playerMemory);
        }

        private void CachePersonalityFields()
        {
            // GiantessPersonality has m_Config (PersonalityConfiguration) — hunger/predator might be there
            // But m_Hunger is directly on GiantessAI (confirmed by probe: m_Hunger = 0.2152479)
            _pers_hunger = FindField(_personalityType, "m_Config");
            _pers_playfulness = FindField(_personalityType, "m_Config");
            _pers_predatorType = FindField(_personalityType, "m_Config");
        }

        private void CacheStomachFields()
        {
            // Confirmed field names from probe:
            _stom_activity = FindField(_stomachType, "m_Activity");
            _stom_acid = FindField(_stomachType, "m_AcidFillAmount");
            _stom_burp = FindField(_stomachType, "m_BurpBuildUp");
            _stom_food = FindField(_stomachType, "m_DigestedFoodAmount");
        }

        private void CacheDigestFields()
        {
            // DigestableObject field names are not straightforward - use FPSBehaviour for player data
            // Player state comes from FPSBehaviour: _health, maxHealth, m_IsDead, m_InStomach, m_BeingSwallowed
            // and from DigestableObject: m_HeldBy_Location, m_IsPlayerCorpse
            _dig_health = FindField(_digestType, "m_fHealth", "m_Health", "health");
            _dig_alive = FindField(_digestType, "m_IsFullyDigested");
            _dig_inStomach = FindField(_digestType, "m_InStomachAcid");
            _dig_inMouth = FindField(_digestType, "m_WasLastInMouth");
            _dig_held = FindField(_digestType, "m_HeldBy_Location");
        }

        private void CacheEntityMemoryFields()
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _mem_canSeeProp = _entityMemoryType.GetProperty("CanSee", flags);
            _mem_canReallySeeProp = _entityMemoryType.GetProperty("CanReallySee", flags);
            _mem_currentLocationProp = _entityMemoryType.GetProperty("CurrentLocation", flags);
            _mem_relationshipProp = _entityMemoryType.GetProperty("Relationship", flags);
            _mem_relStrengthProp = _entityMemoryType.GetProperty("RelationshipStrength", flags);
            _mem_interestProp = _entityMemoryType.GetProperty("Interest", flags);
            _mem_angerProp = _entityMemoryType.GetProperty("AngerValue", flags);
            _mem_wantsAttentionProp = _entityMemoryType.GetProperty("WantsAttention", flags);
            _mem_hasSeenBeforeProp = _entityMemoryType.GetProperty("HasSeenBefore", flags);
            _mem_hasSpokenWithProp = _entityMemoryType.GetProperty("HasSpokenWith", flags);
            _mem_hasPickedUpProp = _entityMemoryType.GetProperty("HasPickedUp", flags);
            _mem_hasSwallowedProp = _entityMemoryType.GetProperty("HasSwallowed", flags);
            _mem_hasTeasedProp = _entityMemoryType.GetProperty("HasTeased", flags);
            _mem_isBeingHeldProp = _entityMemoryType.GetProperty("IsBeingHeld", flags);
            _mem_isTouchingUsProp = _entityMemoryType.GetProperty("IsTouchingUs", flags);
            _mem_isStandingOnUsProp = _entityMemoryType.GetProperty("IsStandingOnUs", flags);
            _mem_isLookingAtUsProp = _entityMemoryType.GetProperty("IsLookingAtUs", flags);
            _mem_timeInLocationProp = _entityMemoryType.GetProperty("TimeSpentInCurrentLocation", flags);

            _mem_canSee = FindField(_entityMemoryType, "CanSee", "_CanSee", "m_CanSee", "canSee");
            _mem_canReallySee = FindField(_entityMemoryType, "CanReallySee", "_CanSeeReal", "_CanReallySee", "m_CanReallySee");
            _mem_currentLocation = FindField(_entityMemoryType, "CurrentLocation", "_LastSeenLocation", "_LastDetectedLocation", "m_CurrentLocation", "currentLocation");
            _mem_relationship = FindField(_entityMemoryType, "Relationship", "_Relationship", "m_Relationship", "relationship");
            _mem_relStrength = FindField(_entityMemoryType, "RelationshipStrength", "_RelationshipStrength", "m_RelationshipStrength");
            _mem_interest = FindField(_entityMemoryType, "Interest", "_Interest", "m_Interest", "interest");
            _mem_anger = FindField(_entityMemoryType, "AngerValue", "_AngerValue", "m_AngerValue", "anger");
            _mem_wantsAttention = FindField(_entityMemoryType, "WantsAttention", "_WantsAttention", "m_WantsAttention");
            _mem_hasSeenBefore = FindField(_entityMemoryType, "HasSeenBefore", "_HasSeenBefore", "m_HasSeenBefore");
            _mem_hasSpokenWith = FindField(_entityMemoryType, "HasSpokenWith", "_HasSpokenWith", "m_HasSpokenWith");
            _mem_hasPickedUp = FindField(_entityMemoryType, "HasPickedUp", "_HasPickedUp", "m_HasPickedUp");
            _mem_hasSwallowed = FindField(_entityMemoryType, "HasSwallowed", "_HasSwallowed", "m_HasSwallowed");
            _mem_hasTeased = FindField(_entityMemoryType, "HasTeased", "_HasTeased", "m_HasTeased");
            _mem_isBeingHeld = FindField(_entityMemoryType, "IsBeingHeld", "_IsBeingHeld", "m_IsBeingHeld");
            _mem_isTouchingUs = FindField(_entityMemoryType, "IsTouchingUs", "_IsTouchingUs", "m_IsTouchingUs");
            _mem_isStandingOnUs = FindField(_entityMemoryType, "IsStandingOnUs", "_IsStandingOnUs", "m_IsStandingOnUs");
            _mem_isLookingAtUs = FindField(_entityMemoryType, "IsLookingAtUs", "_IsLookingAtUs", "m_IsLookingAtUs");
            _mem_timeInLocation = FindField(_entityMemoryType, "TimeSpentInCurrentLocation", "_TimeSpentInCurrentLocation", "m_TimeSpentInCurrentLocation");
        }

        // ──────────────────── Helpers ────────────────────

        private Type FindType(string name)
        {
            var type = _gameAssembly.GetTypes().FirstOrDefault(t => t.Name == name);
            if (type == null)
            {
                // Case-insensitive fallback
                type = _gameAssembly.GetTypes().FirstOrDefault(t =>
                    string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            if (type == null) _log.LogWarning($"Type not found: {name}");
            return type;
        }

        /// <summary>
        /// Find a field by trying multiple candidate names (handles naming convention differences).
        /// </summary>
        private FieldInfo FindField(Type type, params string[] candidates)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var name in candidates)
            {
                var fi = type.GetField(name, flags);
                if (fi != null) return fi;
            }
            // Last resort: case-insensitive search for the first candidate
            var target = candidates[0].ToLower().Replace("m_", "").Replace("_", "");
            foreach (var fi in type.GetFields(flags))
            {
                var normalized = fi.Name.ToLower().Replace("m_", "").Replace("_", "");
                if (normalized == target) return fi;
            }
            return null;
        }

        private T GetField<T>(object obj, FieldInfo fi, T defaultVal)
        {
            if (fi == null || obj == null) return defaultVal;
            try
            {
                object val = fi.GetValue(obj);
                if (val == null) return defaultVal;
                return (T)Convert.ChangeType(val, typeof(T));
            }
            catch
            {
                try { return (T)fi.GetValue(obj); } catch { return defaultVal; }
            }
        }

        private T GetProperty<T>(object obj, PropertyInfo pi, T defaultVal)
        {
            if (pi == null || obj == null) return defaultVal;
            try
            {
                object val = pi.GetValue(obj, null);
                if (val == null) return defaultVal;
                return (T)Convert.ChangeType(val, typeof(T));
            }
            catch
            {
                try { return (T)pi.GetValue(obj, null); } catch { return defaultVal; }
            }
        }

        private T GetFieldByName<T>(object obj, params string[] candidates)
        {
            if (obj == null) return default;
            var fi = FindField(obj.GetType(), candidates);
            if (fi == null) return default;
            try { return (T)fi.GetValue(obj); } catch { return default; }
        }

        private string GetPropertyStr(object obj, PropertyInfo pi)
        {
            if (pi == null || obj == null) return null;
            try
            {
                var val = pi.GetValue(obj, null);
                if (val == null) return null;
                if (val is GameObject go) return go.name;
                if (val is MonoBehaviour mb) return mb.gameObject.name;
                return val.ToString();
            }
            catch { return null; }
        }

        private object GetPropertyValue(object obj, PropertyInfo pi)
        {
            if (pi == null || obj == null) return null;
            try { return pi.GetValue(obj, null); } catch { return null; }
        }

        private MethodInfo FindRememberEntityMethod(Type type)
        {
            if (type == null) return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return type.GetMethods(flags)
                .Where(m => m.Name == "RememberEntity")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        private object TryInvokeRememberEntity(MethodInfo method, object target, GameObject playerObj)
        {
            if (method == null || target == null || playerObj == null) return null;

            try
            {
                var parameters = method.GetParameters();
                var args = new object[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var pType = parameters[i].ParameterType;
                    if (pType == typeof(bool))
                    {
                        args[i] = true;
                    }
                    else if (pType == typeof(GameObject) || pType.IsAssignableFrom(playerObj.GetType()))
                    {
                        args[i] = playerObj;
                    }
                    else if (pType == typeof(Transform) || pType.IsAssignableFrom(playerObj.transform.GetType()))
                    {
                        args[i] = playerObj.transform;
                    }
                    else
                    {
                        var component = playerObj.GetComponent(pType);
                        if (component != null)
                            args[i] = component;
                        else if (parameters[i].HasDefaultValue)
                            args[i] = parameters[i].DefaultValue;
                        else
                            return null;
                    }
                }

                return method.Invoke(target, args);
            }
            catch
            {
                return null;
            }
        }

        private void LogCacheResult(string name, object fi)
        {
            if (fi != null)
                _log.LogInfo($"  Cached: {name} ✓");
            else
                _log.LogWarning($"  Not found: {name} ✗");
        }

        // ──────────────────── Public Accessors (for ActionExecutor) ────────────────────

        /// <summary>Get the first GiantessAI MonoBehaviour, or null.</summary>
        public MonoBehaviour GetFirstGiantessAI()
        {
            if (_giantessAIType == null) return null;

            RefreshSceneCache();
            var all = _cachedGiantessAIs.Where(ai => ai != null).ToList();
            if (all.Count == 0) return null;

            var player = CollectPlayerState();
            var playerPos = new Vector3(player.X, player.Y, player.Z);

            return all
                .OrderBy(ai => Vector3.Distance(ai.transform.position, playerPos))
                .FirstOrDefault();
        }

        /// <summary>Get the GiantessAI type (for casting/reflection).</summary>
        public Type GetGiantessAIType() => _giantessAIType;

        /// <summary>Get the game assembly.</summary>
        public Assembly GetGameAssembly() => _gameAssembly;

        /// <summary>Get a sub-component field value from a GiantessAI instance.</summary>
        public object GetAISubComponent(Component ai, string subName)
        {
            switch (subName.ToLower())
            {
                case "personality": return GetField<object>(ai, _ai_personality, null);
                case "stomach":    return GetField<object>(ai, _ai_stomach, null);
                case "emotion":    return GetField<object>(ai, _ai_emotion, null);
                case "memory":     return GetField<object>(ai, _ai_memory, null);
                case "activity":   return GetField<object>(ai, _ai_activityInterface, null);
                case "cache":      return GetField<object>(ai, _ai_cache, null);
                case "conversation": return GetField<object>(ai, _ai_conversation, null);
                case "movement":   return GetFieldByName<object>(ai, "m_Movement");
                default: return null;
            }
        }

        public object GetRawProperty(Component ai, string path)
        {
            try
            {
                object target = ai;
                string[] parts = path.Split('.');

                foreach (var part in parts)
                {
                    if (target == null) return null;
                    
                    // Try as sub-component first (ai, stomach, etc)
                    if (ReferenceEquals(target, ai))
                    {
                        var comp = GetAISubComponent(ai, part);
                        if (comp != null) { target = comp; continue; }
                    }

                    object current = target;
                    target = GetField<object>(current, part, null);
                    if (target != null) continue;

                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var prop = current.GetType().GetProperty(part, flags);
                    if (prop != null && prop.GetIndexParameters().Length == 0)
                        target = prop.GetValue(current, null);
                }

                return target;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to get raw property {path}: {ex.Message}");
                return null;
            }
        }

        private T GetField<T>(object target, string name, T defaultValue)
        {
            if (target == null) return defaultValue;
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return (T)field.GetValue(target);
            return defaultValue;
        }
    }
}

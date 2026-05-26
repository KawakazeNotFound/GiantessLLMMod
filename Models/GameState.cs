using System.Collections.Generic;
using Newtonsoft.Json;

namespace GiantessLLMMod.Models
{
    /// <summary>
    /// Complete snapshot of the current game state, sent to the LLM as context.
    /// </summary>
    public class GameStateSnapshot
    {
        [JsonProperty("timestamp")]
        public float Timestamp;

        [JsonProperty("player")]
        public PlayerState Player;

        [JsonProperty("giantesses")]
        public List<GiantessState> Giantesses = new List<GiantessState>();

        [JsonProperty("player_input")]
        public string PlayerInput;

        [JsonProperty("player_choice")]
        public string PlayerChoice;

        [JsonProperty("recent_events")]
        public List<string> RecentEvents = new List<string>();
    }

    public class PlayerState
    {
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("z")] public float Z;
        [JsonProperty("health")] public float Health;
        [JsonProperty("max_health")] public float MaxHealth;
        [JsonProperty("is_alive")] public bool IsAlive;
        [JsonProperty("in_stomach")] public bool InStomach;
        [JsonProperty("in_mouth")] public bool InMouth;
        [JsonProperty("in_throat")] public bool InThroat;
        [JsonProperty("is_being_held")] public bool IsBeingHeld;
        [JsonProperty("height")] public float Height;
    }

    public class GiantessState
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("z")] public float Z;
        [JsonProperty("current_state")] public string CurrentState;
        [JsonProperty("distance_to_player")] public float DistanceToPlayer;

        // Personality
        [JsonProperty("predator_type")] public string PredatorType;
        [JsonProperty("hunger")] public float Hunger;
        [JsonProperty("horniness")] public float Horniness;
        [JsonProperty("playful_chance")] public float PlayfulChance;

        // Stomach
        [JsonProperty("stomach_activity")] public float StomachActivity;
        [JsonProperty("stomach_activity_delta")] public float StomachActivityDelta;
        [JsonProperty("stomach_activity_rate")] public float StomachActivityRate;
        [JsonProperty("stomach_acid")] public float StomachAcid;
        [JsonProperty("stomach_acid_delta")] public float StomachAcidDelta;
        [JsonProperty("stomach_acid_rate")] public float StomachAcidRate;
        [JsonProperty("burp_buildup")] public float BurpBuildUp;
        [JsonProperty("burp_buildup_delta")] public float BurpBuildUpDelta;
        [JsonProperty("burp_buildup_rate")] public float BurpBuildUpRate;
        [JsonProperty("digested_food")] public float DigestedFood;

        // Holding state
        [JsonProperty("held_object")] public string HeldObjectName;
        [JsonProperty("held_object_left")] public string HeldObjectLeftName;
        [JsonProperty("has_object_in_mouth")] public bool HasObjectInMouth;

        // Memory about player
        [JsonProperty("player_memory")] public PlayerMemoryData PlayerMemory;
    }

    public class PlayerMemoryData
    {
        [JsonProperty("can_see")] public bool CanSee;
        [JsonProperty("can_really_see")] public bool CanReallySee;
        [JsonProperty("current_location")] public string CurrentLocation;
        [JsonProperty("relationship")] public string Relationship;
        [JsonProperty("relationship_strength")] public float RelationshipStrength;
        [JsonProperty("interest")] public float Interest;
        [JsonProperty("anger")] public float AngerValue;
        [JsonProperty("wants_attention")] public float WantsAttention;
        [JsonProperty("has_seen_before")] public bool HasSeenBefore;
        [JsonProperty("has_spoken_with")] public bool HasSpokenWith;
        [JsonProperty("has_picked_up")] public bool HasPickedUp;
        [JsonProperty("has_swallowed")] public bool HasSwallowed;
        [JsonProperty("has_teased")] public bool HasTeased;
        [JsonProperty("is_being_held")] public bool IsBeingHeld;
        [JsonProperty("is_touching_us")] public bool IsTouchingUs;
        [JsonProperty("is_standing_on_us")] public bool IsStandingOnUs;
        [JsonProperty("is_looking_at_us")] public bool IsLookingAtUs;
        [JsonProperty("time_in_location")] public float TimeSpentInCurrentLocation;
    }
}

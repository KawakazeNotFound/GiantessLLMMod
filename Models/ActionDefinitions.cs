using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GiantessLLMMod.Models
{
    /// <summary>
    /// Defines available actions, emotions, and their mappings to game enums.
    /// Updated with exact enum values from probe data:
    /// - EyesFlexType: EYES_NORMAL(0)..EYES_CLOSED_SAD(18)
    /// - MouthFlexType: MOUTH_NORMAL(0)..MOUTH_TRIANGLE(29)
    /// </summary>
    public static class ActionDefinitions
    {
        /// <summary>
        /// Available actions the LLM can choose.
        /// Each maps to a qGts_ method on GiantessAI.
        /// </summary>
        public static readonly Dictionary<string, string> AvailableActions = new Dictionary<string, string>
        {
        };

        private static readonly Dictionary<string, string> DefaultActions = new Dictionary<string, string>
        {
            { "idle",             "Do nothing, observe" },
            { "face_player",      "Turn to face the player (qGts_FaceTarget)" },
            { "walk_to_player",   "Walk towards the player (qGts_GotoTarget)" },
            { "follow_player",    "Follow the player around (qGts_FollowTarget)" },
            { "pick_up",          "Pick up the player (qGts_PickUp)" },
            { "eat",              "Eat the held object (qGts_EatHeldObject)" },
            { "put_in_mouth",     "Place the held object in mouth (qGts_PutInMouth)" },
            { "swallow",          "Swallow object from mouth (qGts_Mouth_Swallow)" },
            { "take_out_mouth",   "Take object out of mouth (qGts_Mouth_TakeOut)" },
            { "pat_stomach",      "Pat/rub stomach (qGts_PatStomach)" },
            { "tease_mouth",      "Tease with mouth (qGts_TeaseMouth)" },
            { "tease_stomach",    "Tease about stomach (qGts_TeaseStomach)" },
            { "drop",             "Drop held object (qGts_Drop)" },
            { "dangle",           "Dangle object over mouth (qGts_DangleOverMouth)" },
            { "dangle_drop",      "Drop dangled object into mouth (qGts_Dangle_Drop)" },
            { "burp",             "Burp (Burp/qGts_Burp)" },
            { "put_on_stomach",   "Place player on stomach (qGts_PutOnStomach)" },
            { "lick",             "Lick the held object (qGts_LickHeldObject)" },
            { "put_in_bra",       "Place object in bra (qGts_PutInBra)" },
            { "invite_into_mouth","Invite player to enter mouth (qGts_InviteIntoMouth)" },
            { "play_with_food",   "Play with the player as food (qGts_PlayWithFood)" },
            { "lay_down",         "Lay down face up (qGts_LayDownFace_Start)" },
            { "stand_up",         "Stand back up (qGts_LayDownFace_End)" },
            { "place_on_surface", "Pick up the player if needed, move to a named scene surface, and place the player there" },
        };

        /// <summary>
        /// Emotion mappings → (EyesFlexType, MouthFlexType).
        /// Uses exact enum names from probe data.
        /// </summary>
        public static readonly Dictionary<string, EmotionMapping> EmotionMap = new Dictionary<string, EmotionMapping>
        {
        };

        private static readonly Dictionary<string, EmotionMapping> DefaultEmotions = new Dictionary<string, EmotionMapping>
        {
            // ── Basic ──
            { "neutral",    new EmotionMapping("EYES_NORMAL",     "MOUTH_NORMAL") },
            { "happy",      new EmotionMapping("EYES_HAPPY",      "MOUTH_HAPPY") },
            { "angry",      new EmotionMapping("EYES_ANGRY",      "MOUTH_ANGRY") },
            { "curious",    new EmotionMapping("EYES_CURIOUS",    "MOUTH_CURIOUS") },
            { "excited",    new EmotionMapping("EYES_EXCITED",    "MOUTH_EXCITED") },
            { "surprised",  new EmotionMapping("EYES_SURPRISED",  "MOUTH_OPEN") },
            { "sad",        new EmotionMapping("EYES_SAD",        "MOUTH_FROWN") },

            // ── Playful / Mischievous ──
            { "playful",    new EmotionMapping("EYES_PLAYFUL",    "MOUTH_GRIN") },
            { "smug",       new EmotionMapping("EYES_INTEREST",   "MOUTH_SMIRK") },
            { "mischievous",new EmotionMapping("EYES_PLAYFUL",    "MOUTH_SMIRK2") },

            // ── Romantic / Intimate ──
            { "horny",      new EmotionMapping("EYES_HORNY",      "MOUTH_SMILE_BLUSH") },
            { "aroused",    new EmotionMapping("EYES_HORNY2",     "MOUTH_GRIN_BLUSH") },
            { "pleasure",   new EmotionMapping("EYES_PLEASURE",   "MOUTH_YUMMY") },

            // ── Predatory ──
            { "hungry",     new EmotionMapping("EYES_INTEREST",   "MOUTH_HUNGRY") },
            { "craving",    new EmotionMapping("EYES_INTENSE_STARE","MOUTH_HUNGRY2") },
            { "licking",    new EmotionMapping("EYES_HORNY",      "MOUTH_LICK_LIPS") },
            { "tasting",    new EmotionMapping("EYES_PLEASURE",   "MOUTH_LICK_UP") },
            { "savoring",   new EmotionMapping("EYES_SHUT_TIGHT", "MOUTH_YUMMY") },
            { "crazy",      new EmotionMapping("EYES_CRAZY",      "MOUTH_CRAZY") },

            // ── Shy / Upset ──
            { "shy",        new EmotionMapping("EYES_UPSET_SHY",  "MOUTH_UPSET_SHY") },
            { "embarrassed",new EmotionMapping("EYES_UPSET_SHY",  "MOUTH_GRIN_BLUSH") },
            { "crying",     new EmotionMapping("EYES_CRY",        "MOUTH_FROWN") },

            // ── Misc ──
            { "sleepy",     new EmotionMapping("EYES_CLOSED",     "MOUTH_NORMAL") },
            { "intense",    new EmotionMapping("EYES_INTENSE_STARE","MOUTH_INTEREST") },
            { "gentle",     new EmotionMapping("EYES_HAPPY",      "MOUTH_SMILE") },
            { "smiling",    new EmotionMapping("EYES_NORMAL",     "MOUTH_SMALL_GRIN") },
            { "grinning",   new EmotionMapping("EYES_HAPPY",      "MOUTH_GRIN") },
        };

        static ActionDefinitions()
        {
            ResetToDefaults();
        }

        public static void ResetToDefaults()
        {
            AvailableActions.Clear();
            foreach (var item in DefaultActions)
                AvailableActions[item.Key] = item.Value;

            EmotionMap.Clear();
            foreach (var item in DefaultEmotions)
                EmotionMap[item.Key] = item.Value;
        }

        public static void LoadFromPromptConfig(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var actions = new Dictionary<string, string>();
            var emotions = new Dictionary<string, EmotionMapping>();
            string section = "";

            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }

                    if (section == "action_whitelist")
                        ParseActionLine(trimmed, actions);
                    else if (section == "emotion_whitelist")
                        ParseEmotionLine(trimmed, emotions);
                }
            }

            if (actions.Count > 0)
            {
                AvailableActions.Clear();
                foreach (var item in actions)
                    AvailableActions[item.Key] = item.Value;
            }

            if (emotions.Count > 0)
            {
                EmotionMap.Clear();
                foreach (var item in emotions)
                    EmotionMap[item.Key] = item.Value;
            }
        }

        private static void ParseActionLine(string line, Dictionary<string, string> actions)
        {
            foreach (var part in SplitCsv(line))
            {
                int eq = part.IndexOf('=');
                string name = eq >= 0 ? part.Substring(0, eq).Trim() : part.Trim();
                string desc = eq >= 0 ? part.Substring(eq + 1).Trim() : "";

                if (string.IsNullOrEmpty(name))
                    continue;

                actions[name] = string.IsNullOrEmpty(desc)
                    ? (DefaultActions.ContainsKey(name) ? DefaultActions[name] : "Configured action")
                    : desc;
            }
        }

        private static void ParseEmotionLine(string line, Dictionary<string, EmotionMapping> emotions)
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                foreach (var name in SplitCsv(line))
                {
                    if (DefaultEmotions.TryGetValue(name, out var mapping))
                        emotions[name] = mapping;
                }
                return;
            }

            string emotion = line.Substring(0, eq).Trim();
            var values = SplitCsv(line.Substring(eq + 1)).ToArray();
            if (!string.IsNullOrEmpty(emotion) && values.Length >= 2)
                emotions[emotion] = new EmotionMapping(values[0], values[1]);
        }

        private static IEnumerable<string> SplitCsv(string value)
        {
            return (value ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0);
        }

        /// <summary>
        /// Generate the actions list string for the system prompt.
        /// </summary>
        public static string GetActionsDescription()
        {
            return "actions: " + string.Join(", ", AvailableActions.Keys);
        }

        /// <summary>
        /// Generate the emotions list string for the system prompt.
        /// </summary>
        public static string GetEmotionsDescription()
        {
            return "emotions: " + string.Join(", ", EmotionMap.Keys);
        }
    }

    public class EmotionMapping
    {
        public string Eyes { get; }
        public string Mouth { get; }

        public EmotionMapping(string eyes, string mouth)
        {
            Eyes = eyes;
            Mouth = mouth;
        }
    }
}

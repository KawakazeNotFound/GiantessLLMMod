using System.Collections.Generic;
using BepInEx.Logging;
using GiantessLLMMod.Models;
using UnityEngine;

namespace GiantessLLMMod.Core
{
    /// <summary>
    /// Watches for significant game state changes and generates event descriptions.
    /// Compares previous state snapshot with current to detect transitions.
    /// </summary>
    public class EventWatcher
    {
        private readonly ManualLogSource _log;
        private readonly List<string> _pendingEvents = new List<string>();
        private readonly Dictionary<string, float> _lastEventTime = new Dictionary<string, float>();
        private PlayerState _prevPlayer;
        private readonly Dictionary<string, string> _prevGiantessState = new Dictionary<string, string>();

        public EventWatcher(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Compare new state with previous and detect events.
        /// </summary>
        public void Update(GameStateSnapshot current)
        {
            if (current?.Player == null) return;

            UpdatePlayer(current.Player);

            // Check giantess state changes
            foreach (var g in current.Giantesses)
            {
                string key = g.Name ?? "Unknown";
                if (_prevGiantessState.TryGetValue(key, out string prevState))
                {
                    // Script/action state changes are often caused by the LLM action itself.
                    // Treating them as events creates a feedback loop of repeated LLM calls.
                }
                _prevGiantessState[key] = g.CurrentState;
            }
        }

        /// <summary>
        /// Compare only the inexpensive player flags needed for automatic events.
        /// </summary>
        public void UpdatePlayer(PlayerState p)
        {
            if (p == null) return;

            if (_prevPlayer != null)
            {
                // Player entered stomach
                if (!_prevPlayer.InStomach && p.InStomach)
                    AddEvent("Player was swallowed and is now in the giantess's stomach!");

                // Player escaped stomach
                if (_prevPlayer.InStomach && !p.InStomach)
                    AddEvent("Player escaped from the stomach!");

                // Player entered mouth
                if (!_prevPlayer.InMouth && p.InMouth)
                    AddEvent("Player is now inside the giantess's mouth!");

                // Player left mouth
                if (_prevPlayer.InMouth && !p.InMouth && !p.InStomach)
                    AddEvent("Player was taken out of the mouth.");

                // Player was picked up
                if (!_prevPlayer.IsBeingHeld && p.IsBeingHeld)
                    AddEvent("The giantess picked up the player!");

                // Player was put down
                if (_prevPlayer.IsBeingHeld && !p.IsBeingHeld && !p.InMouth && !p.InStomach)
                    AddEvent("The giantess put the player down.");

                // Death/revive can flicker while entering a scene, so do not use it as
                // an automatic LLM event.
            }

            // Save current as previous
            _prevPlayer = new PlayerState
            {
                X = p.X, Y = p.Y, Z = p.Z,
                Health = p.Health, MaxHealth = p.MaxHealth,
                IsAlive = p.IsAlive,
                InStomach = p.InStomach, InMouth = p.InMouth, InThroat = p.InThroat,
                IsBeingHeld = p.IsBeingHeld, Height = p.Height
            };
        }

        /// <summary>
        /// Get all pending events and clear the queue.
        /// </summary>
        public List<string> FlushEvents()
        {
            var events = new List<string>(_pendingEvents);
            _pendingEvents.Clear();
            return events;
        }

        /// <summary>
        /// Whether there are any pending events.
        /// </summary>
        public bool HasEvents => _pendingEvents.Count > 0;

        private void AddEvent(string description)
        {
            float now = Time.time;
            if (_lastEventTime.TryGetValue(description, out float lastTime) && now - lastTime < 10f)
                return;

            _lastEventTime[description] = now;
            _pendingEvents.Add(description);
            if (_pendingEvents.Count > 10)
                _pendingEvents.RemoveAt(0);
            _log.LogInfo($"[Event] {description}");
        }
    }
}

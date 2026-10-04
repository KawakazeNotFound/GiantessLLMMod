using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GiantessLLMMod.Core
{
    public enum PerfMetric
    {
        UiSnapshot,
        EventPlayerPoll,
        FullSnapshot,
        SnapshotPlayer,
        SnapshotGiantesses,
        SnapshotSceneObjects,
        SceneCacheRebuild,
        LlmRoundTrip,
        ActionExecution,
        OverlayDraw,
        Count
    }

    public sealed class PerfMetricSnapshot
    {
        public string Name;
        public long Calls;
        public double LastMs;
        public double AverageMs;
        public double MaxMs;
    }

    public sealed class PerfHistorySnapshot
    {
        public float[] Fps;
        public float[] FrameMs;
        public float[] ManagedMb;
        public int Count;
    }

    /// <summary>
    /// Low-allocation runtime counters for this mod. Optional sections become visible
    /// only after their code path exists and records a sample.
    /// </summary>
    public sealed class PerformanceMonitor
    {
        private sealed class MetricData
        {
            public long Calls;
            public double TotalMs;
            public double LastMs;
            public double MaxMs;
        }

        private static readonly string[] Names =
        {
            "UI snapshot",
            "Event player poll",
            "Full LLM snapshot",
            "  Player state",
            "  Giantess states",
            "  Scene objects",
            "Scene cache rebuild",
            "LLM round trip",
            "Action execution",
            "Overlay draw"
        };

        private readonly object _sync = new object();
        private readonly MetricData[] _metrics = new MetricData[(int)PerfMetric.Count];
        private float _fps;
        private float _frameMs;
        private long _managedBytes;
        private int _gcCollections;
        private float _nextSystemSampleTime;
        private int _lastGc0;
        private int _lastGc1;
        private int _lastGc2;
        private bool _sceneStatsAvailable;
        private int _activeColliderCount;
        private int _cachedSurfaces;
        private int _giantessCount;
        private int _mouthTriggerCount;
        private const int HistoryCapacity = 120;
        private readonly float[] _fpsHistory = new float[HistoryCapacity];
        private readonly float[] _frameHistory = new float[HistoryCapacity];
        private readonly float[] _memoryHistory = new float[HistoryCapacity];
        private int _historyWriteIndex;
        private int _historyCount;
        private float _nextHistorySampleTime;

        public PerformanceMonitor()
        {
            for (int i = 0; i < _metrics.Length; i++)
                _metrics[i] = new MetricData();

            _lastGc0 = GC.CollectionCount(0);
            _lastGc1 = GC.CollectionCount(1);
            _lastGc2 = GC.CollectionCount(2);
        }

        public float Fps => _fps;
        public float FrameMs => _frameMs;
        public long ManagedBytes => _managedBytes;
        public int GcCollections => _gcCollections;
        public bool SceneStatsAvailable => _sceneStatsAvailable;
        public int ActiveColliderCount => _activeColliderCount;
        public int CachedSurfaces => _cachedSurfaces;
        public int GiantessCount => _giantessCount;
        public int MouthTriggerCount => _mouthTriggerCount;

        public void UpdateFrame(float unscaledDeltaTime, float unscaledTime)
        {
            if (unscaledDeltaTime > 0f)
            {
                float instantFps = 1f / unscaledDeltaTime;
                float instantMs = unscaledDeltaTime * 1000f;
                _fps = _fps <= 0f ? instantFps : _fps + (instantFps - _fps) * 0.08f;
                _frameMs = _frameMs <= 0f ? instantMs : _frameMs + (instantMs - _frameMs) * 0.08f;
            }

            if (unscaledTime >= _nextSystemSampleTime)
            {
                _nextSystemSampleTime = unscaledTime + 1f;
                _managedBytes = GC.GetTotalMemory(false);

                int gc0 = GC.CollectionCount(0);
                int gc1 = GC.CollectionCount(1);
                int gc2 = GC.CollectionCount(2);
                _gcCollections = (gc0 - _lastGc0) + (gc1 - _lastGc1) + (gc2 - _lastGc2);
                _lastGc0 = gc0;
                _lastGc1 = gc1;
                _lastGc2 = gc2;
            }

            if (unscaledTime >= _nextHistorySampleTime)
            {
                _nextHistorySampleTime = unscaledTime + 0.25f;
                _fpsHistory[_historyWriteIndex] = _fps;
                _frameHistory[_historyWriteIndex] = _frameMs;
                _memoryHistory[_historyWriteIndex] = _managedBytes / (1024f * 1024f);
                _historyWriteIndex = (_historyWriteIndex + 1) % HistoryCapacity;
                if (_historyCount < HistoryCapacity) _historyCount++;
            }
        }

        public Scope Measure(PerfMetric metric)
        {
            return new Scope(this, metric, Stopwatch.GetTimestamp());
        }

        public void RecordElapsed(PerfMetric metric, long startTimestamp)
        {
            Record(metric, TicksToMilliseconds(Stopwatch.GetTimestamp() - startTimestamp));
        }

        public long GetTimestamp() => Stopwatch.GetTimestamp();

        public void Record(PerfMetric metric, double milliseconds)
        {
            int index = (int)metric;
            if (index < 0 || index >= _metrics.Length) return;

            lock (_sync)
            {
                var data = _metrics[index];
                data.Calls++;
                data.TotalMs += milliseconds;
                data.LastMs = milliseconds;
                if (milliseconds > data.MaxMs) data.MaxMs = milliseconds;
            }
        }

        public List<PerfMetricSnapshot> GetActiveMetrics()
        {
            var result = new List<PerfMetricSnapshot>();
            lock (_sync)
            {
                for (int i = 0; i < _metrics.Length; i++)
                {
                    var data = _metrics[i];
                    if (data.Calls == 0) continue;
                    result.Add(new PerfMetricSnapshot
                    {
                        Name = Names[i],
                        Calls = data.Calls,
                        LastMs = data.LastMs,
                        AverageMs = data.TotalMs / data.Calls,
                        MaxMs = data.MaxMs
                    });
                }
            }
            return result;
        }

        public PerfHistorySnapshot GetHistory()
        {
            var result = new PerfHistorySnapshot
            {
                Fps = new float[_historyCount],
                FrameMs = new float[_historyCount],
                ManagedMb = new float[_historyCount],
                Count = _historyCount
            };

            int first = (_historyWriteIndex - _historyCount + HistoryCapacity) % HistoryCapacity;
            for (int i = 0; i < _historyCount; i++)
            {
                int source = (first + i) % HistoryCapacity;
                result.Fps[i] = _fpsHistory[source];
                result.FrameMs[i] = _frameHistory[source];
                result.ManagedMb[i] = _memoryHistory[source];
            }
            return result;
        }

        public void SetSceneStats(int activeColliderCount, int cachedSurfaces, int giantessCount, int mouthTriggerCount)
        {
            _sceneStatsAvailable = true;
            _activeColliderCount = activeColliderCount;
            _cachedSurfaces = cachedSurfaces;
            _giantessCount = giantessCount;
            _mouthTriggerCount = mouthTriggerCount;
        }

        public void Reset()
        {
            lock (_sync)
            {
                foreach (var data in _metrics)
                {
                    data.Calls = 0;
                    data.TotalMs = 0;
                    data.LastMs = 0;
                    data.MaxMs = 0;
                }

                Array.Clear(_fpsHistory, 0, _fpsHistory.Length);
                Array.Clear(_frameHistory, 0, _frameHistory.Length);
                Array.Clear(_memoryHistory, 0, _memoryHistory.Length);
                _historyWriteIndex = 0;
                _historyCount = 0;
            }
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        public struct Scope : IDisposable
        {
            private readonly PerformanceMonitor _owner;
            private readonly PerfMetric _metric;
            private readonly long _start;

            internal Scope(PerformanceMonitor owner, PerfMetric metric, long start)
            {
                _owner = owner;
                _metric = metric;
                _start = start;
            }

            public void Dispose()
            {
                _owner?.RecordElapsed(_metric, _start);
            }
        }
    }
}

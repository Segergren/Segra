using Segra.Backend.Core.Models;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// Samples the number of frames the recording output has actually delivered and keeps the
    /// breakpoints needed to turn a wall-clock time into a position in the file.
    /// </summary>
    internal sealed class MediaClock : IDisposable
    {
        private const int SampleIntervalMs = 1000;
        private const int MaxSamples = 240;

        private readonly DateTime _startWall;
        private readonly uint _fps;
        private readonly Func<int> _deliveredFrames;
        private readonly object _lock = new();
        private readonly MediaClockTimeline _timeline = new();
        private readonly System.Threading.Timer _timer;

        public MediaClock(DateTime startWall, uint fps, Func<int> deliveredFrames)
        {
            _startWall = startWall;
            _fps = fps > 0 ? fps : 60;
            _deliveredFrames = deliveredFrames;
            _timer = new System.Threading.Timer(_ => Sample(), null, SampleIntervalMs, SampleIntervalMs);
        }

        public MediaClockTimeline? Snapshot()
        {
            lock (_lock)
            {
                return _timeline.LostSeconds >= 1 ? _timeline.Copy() : null;
            }
        }

        public void Dispose() => _timer.Dispose();

        private void Sample()
        {
            int delivered;
            try
            {
                delivered = _deliveredFrames();
            }
            catch
            {
                return;
            }

            if (delivered <= 0) return; // nothing encoded yet

            double media = delivered / (double)_fps;
            double wall = (DateTime.Now - _startWall).TotalSeconds;

            lock (_lock)
            {
                List<MediaClockSample> samples = _timeline.Samples;

                if (samples.Count == 0)
                {
                    samples.Add(new MediaClockSample { WallSeconds = wall, MediaSeconds = media });
                    return;
                }

                MediaClockSample last = samples[^1];

                if (media < last.MediaSeconds) return;

                if (media == last.MediaSeconds)
                {
                    last.WallSeconds = wall;
                    return;
                }

                samples.Add(new MediaClockSample { WallSeconds = wall, MediaSeconds = media });
                if (samples.Count > MaxSamples) samples.RemoveAt(1);
            }
        }
    }
}

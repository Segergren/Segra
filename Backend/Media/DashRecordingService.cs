namespace Segra.Backend.Media
{
    public static class DashRecordingService
    {
        // Segment duration: 2 seconds.
        // Rolling Buffer: Maintain a 30-minute window (approx. 900 segments).
        // Stitching: Clips must be created using -c copy (stream copy) from the DASH source for instant processing.
        // flags -window_size 900, -remove_at_exit 1, and -use_template 1.

        public const string FormatName = "dash";
        public const string MuxerSettings = "window_size=900 remove_at_exit=1 use_template=1 seg_duration=2 streaming=1 ldash=1 write_prft=1";

        public static string GetDashFormatName()
        {
            return FormatName;
        }

        public static string GetDashMuxerSettings()
        {
            return MuxerSettings;
        }

        public static string GetOutputFileName()
        {
            return "session.mpd";
        }
    }
}

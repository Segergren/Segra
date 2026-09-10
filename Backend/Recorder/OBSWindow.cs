namespace Segra.Backend.Recorder
{
    internal class OBSWindow : Form
    {
        // Completed once the native window exists, so OBS can attach a swap chain to it.
        public static readonly TaskCompletionSource<nint> HandleReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OBSWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;

            Task.Run(() => OBSService.InitializeAsync());
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HandleReady.TrySetResult(Handle);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Hide();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW to prevent from showing in Alt+Tab
                return cp;
            }
        }
    }
}

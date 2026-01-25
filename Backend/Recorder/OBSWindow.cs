namespace Segra.Backend.Recorder
{
    internal class OBSWindow : Form
    {
        public OBSWindow()
        {
            // Hide the form
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;

            // Initialize OBS utils asynchronously
            Task.Run(() => OBSService.InitializeAsync());
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Hide(); // Ensure the form is hidden on load
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

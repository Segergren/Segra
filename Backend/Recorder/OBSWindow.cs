namespace Segra.Backend.Recorder
{
    internal class OBSWindow : Form
    {
        public OBSWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;

            Task.Run(() => OBSService.InitializeAsync());
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

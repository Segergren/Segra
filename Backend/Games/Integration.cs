namespace Segra.Backend.Games
{
    public abstract class Integration
    {
        public string? ExePath { get; set; }
        public abstract Task Start();
        public abstract Task Shutdown();
    }
}

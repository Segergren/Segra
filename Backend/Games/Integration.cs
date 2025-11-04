namespace Segra.Backend.Games
{
    public abstract class Integration
    {
        public abstract Task Start();
        public abstract Task Shutdown();
    }
}

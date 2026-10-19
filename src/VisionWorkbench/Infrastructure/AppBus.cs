using CommunityToolkit.Mvvm.Messaging;

namespace VisionWorkbench.Infrastructure;

// 不同窗体共享同一个实例；分发在发布线程，界面订阅者自行调度到 Dispatcher。
public sealed class AppBus
{
    private readonly WeakReferenceMessenger messenger = new();
    public void Publish<T>(T message) where T : class => messenger.Send(message);
    public IDisposable Subscribe<T>(Action<T> callback) where T : class => new Subscription<T>(messenger, callback);

    private sealed class Subscription<T> : IDisposable where T : class
    {
        private readonly WeakReferenceMessenger messenger;
        private readonly Action<T> callback;
        public Subscription(WeakReferenceMessenger messenger, Action<T> callback)
        {
            this.messenger = messenger;
            this.callback = callback;
            messenger.Register<Subscription<T>, T>(this, static (receiver, message) => receiver.callback(message));
        }
        public void Dispose() => messenger.UnregisterAll(this);
    }
}

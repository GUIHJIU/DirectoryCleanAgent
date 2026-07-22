using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Everything.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DirectoryCleanAgent.Tests.Infrastructure;

[CollectionDefinition("Everything")]
public class EverythingTestCollection : ICollectionFixture<EverythingTestFixture>
{
}

public sealed class EverythingTestFixture : IAsyncLifetime
{
    private ServiceProvider? _sdkProvider;
    public bool IsAvailable { get; private set; }
    public IEverythingSdk? SharedSdk { get; private set; }

    public async Task InitializeAsync()
    {
        EverythingTestHelper.ResetCache();
        IsAvailable = EverythingTestHelper.IsAvailable;
        if (IsAvailable)
        {
            try
            {
                var services = new ServiceCollection();
                services.AddLogging(b => { b.ClearProviders(); b.SetMinimumLevel(LogLevel.Warning); });
                services.AddEverythingServices();
                _sdkProvider = services.BuildServiceProvider();
                SharedSdk = _sdkProvider.GetRequiredService<IEverythingSdk>();
            }
            catch { SharedSdk = null; }
        }
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_sdkProvider is not null)
        {
            try { await _sdkProvider.DisposeAsync(); }
            catch { }
        }
    }
}

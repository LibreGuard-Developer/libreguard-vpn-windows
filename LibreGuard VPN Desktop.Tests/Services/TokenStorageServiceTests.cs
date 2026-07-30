using LibreGuard_VPN_Desktop.Services;
using System.Security.Cryptography;
using System.Text;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class TokenStorageServiceTests
{
    [Fact]
    public void UpdatePlanType_WhenPlanChanges_PersistsPlanAndRaisesSessionChanged()
    {
        var sessionPath = Path.Combine(Path.GetTempPath(), "LibreGuardVPN.Tests", Guid.NewGuid().ToString("N"), "session.secure");
        var service = new TokenStorageService(sessionPath);
        var eventCount = 0;
        service.SessionChanged += () => eventCount++;

        service.StoreSession("access", "refresh", "user-1", "pro@example.test", "Free");
        service.UpdatePlanType("Pro");

        var reloaded = new TokenStorageService(sessionPath);

        Assert.Equal(2, eventCount);
        Assert.Equal("Pro", service.PlanType);
        Assert.Equal("Pro", reloaded.PlanType);
    }

    [Fact]
    public void UpdatePlanType_WhenPlanIsUnchanged_DoesNotRaiseSessionChanged()
    {
        var sessionPath = Path.Combine(Path.GetTempPath(), "LibreGuardVPN.Tests", Guid.NewGuid().ToString("N"), "session.secure");
        var service = new TokenStorageService(sessionPath);
        service.StoreSession("access", "refresh", "user-1", "pro@example.test", "Pro");

        var eventCount = 0;
        service.SessionChanged += () => eventCount++;

        service.UpdatePlanType("pro");

        Assert.Equal(0, eventCount);
        Assert.Equal("Pro", service.PlanType);
    }

    [Fact]
    public void HasToken_WhenRefreshTokenIsMissing_ReturnsFalse()
    {
        var sessionPath = Path.Combine(Path.GetTempPath(), "LibreGuardVPN.Tests", Guid.NewGuid().ToString("N"), "session.secure");
        var service = new TokenStorageService(sessionPath);
        service.StoreSession("access", "refresh", "user-1", "pro@example.test", "Pro");

        service.RefreshToken = null;

        Assert.False(service.HasToken);
    }

    [Fact]
    public void LoadSession_WhenPersistedSessionIsMissingRefreshToken_DiscardsIt()
    {
        var sessionPath = Path.Combine(Path.GetTempPath(), "LibreGuardVPN.Tests", Guid.NewGuid().ToString("N"), "session.secure");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);

        var json = """{"AccessToken":"access","RefreshToken":"","UserId":"user-1","Email":"pro@example.test","PlanType":"Pro"}""";
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(sessionPath, protectedBytes);

        var service = new TokenStorageService(sessionPath);

        Assert.False(service.HasToken);
        Assert.Null(service.AccessToken);
        Assert.Null(service.RefreshToken);
        Assert.False(File.Exists(sessionPath));
    }
}

using AIMemory.Api.Distributed;

namespace AIMemory.Tests.Unit.Distributed;

/// <summary>
/// Phase 11: <c>POST /api/admin/distributed/enable</c> now honors a user-supplied
/// <c>bindInterface</c> (the desktop UI sends one from a picker). The validator that
/// gates this parameter has its own seam so we can exercise the rules without a live
/// Kestrel listener. Endpoint-side wiring is exercised by the smoke test.
/// </summary>
public class BindInterfaceValidatorTests
{
    [Fact]
    public void Empty_NoOverride()
    {
        // Phase-7a behavior: no bindInterface supplied → caller falls back to its existing
        // default (currently 0.0.0.0). Empty is NOT the same as invalid.
        var result = BindInterfaceValidator.Validate(null);
        Assert.True(result.IsEmpty);
        Assert.False(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Whitespace_TreatedAsEmpty()
    {
        var result = BindInterfaceValidator.Validate("   ");
        Assert.True(result.IsEmpty);
    }

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.10")]
    public void ValidIpv4_AcceptedAndNormalized(string raw)
    {
        var result = BindInterfaceValidator.Validate(raw);
        Assert.True(result.IsValid);
        Assert.Equal(raw, result.NormalizedAddress);
        Assert.False(result.IsLoopback);
    }

    [Fact]
    public void Wildcard_Accepted()
    {
        // 0.0.0.0 = "all interfaces"; canonical for Allow-Remote.
        var result = BindInterfaceValidator.Validate("0.0.0.0");
        Assert.True(result.IsValid);
        Assert.Equal("0.0.0.0", result.NormalizedAddress);
        Assert.False(result.IsLoopback);
    }

    [Fact]
    public void IPv6Wildcard_Accepted()
    {
        // ::  ≡ all IPv6 interfaces; we accept it even though Kestrel may not bind it on
        // every platform — that's a downstream concern.
        var result = BindInterfaceValidator.Validate("::");
        Assert.True(result.IsValid);
        Assert.False(result.IsLoopback);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Loopback_AcceptedWithFlag(string raw)
    {
        // Loopback is accepted but flagged; the endpoint logs a warning so the user knows
        // remote ingestors won't be able to reach the API.
        var result = BindInterfaceValidator.Validate(raw);
        Assert.True(result.IsValid);
        Assert.True(result.IsLoopback);
    }

    [Fact]
    public void LocalhostString_NormalizedToLoopbackIp()
    {
        // "localhost" is a friendly label some pickers send rather than a raw IP. We normalize
        // it to 127.0.0.1 so the persisted config is consistently an IP literal.
        var result = BindInterfaceValidator.Validate("localhost");
        Assert.True(result.IsValid);
        Assert.Equal("127.0.0.1", result.NormalizedAddress);
        Assert.True(result.IsLoopback);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("256.256.256.256")]
    [InlineData("garbage")]
    [InlineData("https://10.0.0.1")]
    public void Invalid_RejectedWithMessage(string raw)
    {
        var result = BindInterfaceValidator.Validate(raw);
        Assert.False(result.IsValid);
        Assert.False(result.IsEmpty);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains(raw, result.ErrorMessage);
    }
}

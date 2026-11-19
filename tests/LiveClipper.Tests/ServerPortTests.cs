using System.Net;
using System.Net.Sockets;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// 默认端口必须落在 Windows 的 TCP 保留段之外，且被别的进程占住时要能顺延。
/// 背景：5179 落在保留段 5114–5213 内，在那类机器上启动直接 EACCES；这条测试把它钉住。
/// </summary>
public class ServerPortTests
{
    // `netsh interface ipv4 show excludedportrange protocol=tcp` 在本机报出的保留段之一。
    private const int ReservedRangeStart = 5114;
    private const int ReservedRangeEnd = 5213;

    [Fact]
    public void DefaultPort_IsOutsideWindowsReservedRange()
    {
        Assert.InRange(Program.DefaultPort, 1024, 65535);
        Assert.False(
            Program.DefaultPort >= ReservedRangeStart && Program.DefaultPort <= ReservedRangeEnd,
            $"默认端口 {Program.DefaultPort} 落在 Windows TCP 保留段 {ReservedRangeStart}–{ReservedRangeEnd} 内");
    }

    [Fact]
    public void ResolvePort_WithoutOverride_UsesDefaultPort()
    {
        Assert.Equal(Program.DefaultPort, Program.ResolvePort(null));
        Assert.Equal(Program.DefaultPort, Program.ResolvePort("   "));
    }

    [Fact]
    public void ResolvePort_ExplicitValueWins_EvenInsideReservedRange()
    {
        // 显式指定就是用户说了算：保留段内能不能绑是运行时的事，校验只负责范围。
        Assert.Equal(5179, Program.ResolvePort("5179"));
        Assert.Equal(15000, Program.ResolvePort("15000"));
    }

    [Fact]
    public void ResolvePort_OutOfRange_FallsBackToDefault()
    {
        Assert.Equal(Program.DefaultPort, Program.ResolvePort("80"));
        Assert.Equal(Program.DefaultPort, Program.ResolvePort("70000"));
        Assert.Equal(Program.DefaultPort, Program.ResolvePort("abc"));
    }

    [Fact]
    public void ChoosePort_SkipsPortHeldByAnotherProcess()
    {
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var occupied = ((IPEndPoint)busy.LocalEndpoint).Port;

        var chosen = Program.ChoosePort(occupied);

        Assert.NotEqual(occupied, chosen);
        Assert.InRange(chosen, 1024, 65535);
        // 顺延后的端口必须真的能绑上，否则等于把一个绑不上的端口发给 Kestrel。
        var probe = new TcpListener(IPAddress.Loopback, chosen);
        probe.Start();
        probe.Stop();
    }
}

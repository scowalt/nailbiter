using System.Net;
using System.Net.Sockets;
using Xunit;

namespace backend.tests;

public sealed class DisposableHostPortsTests
{
    [Fact]
    public void Selection_names_the_range_when_every_port_in_it_is_taken()
    {
        using var held = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        held.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        held.Listen();
        var port = ((IPEndPoint)held.LocalEndPoint!).Port;
        var error = Assert.Throws<InvalidOperationException>(() => DisposableHostPorts.SelectFree(port, port));
        Assert.Equal($"Every host port in {port}-{port} is in use.", error.Message);
    }
}

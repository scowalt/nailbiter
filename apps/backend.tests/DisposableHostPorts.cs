using System.Net;
using System.Net.Sockets;

namespace backend.tests;

internal static class DisposableHostPorts
{
    public const int First = 61000;
    public const int Last = ushort.MaxValue;

    public static string Range { get; } = FormattableString.Invariant($"{First}-{Last}");

    public static ushort SelectFree()
    {
        using var reservation = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true,
            ExclusiveAddressUse = true
        };
        reservation.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return checked((ushort)((IPEndPoint)reservation.LocalEndPoint!).Port);
    }
}

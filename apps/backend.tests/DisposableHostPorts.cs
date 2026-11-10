using System.Net;
using System.Net.Sockets;

namespace backend.tests;

internal static class DisposableHostPorts
{
    public const int First = 61000;
    public const int Last = ushort.MaxValue;

    public static string Range { get; } = FormattableString.Invariant($"{First}-{Last}");

    public static ushort SelectFree() => SelectFree(First, Last);

    internal static ushort SelectFree(int first, int last)
    {
        var count = last - first + 1;
        var start = Random.Shared.Next(count);
        for (var offset = 0; offset < count; offset++)
        {
            var port = first + (start + offset) % count;
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
            {
                DualMode = true,
                ExclusiveAddressUse = true
            };
            try
            {
                probe.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return checked((ushort)port);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
            }
        }
        throw new InvalidOperationException($"Every host port in {first}-{last} is in use.");
    }
}

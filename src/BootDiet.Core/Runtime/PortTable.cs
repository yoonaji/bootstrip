using System.Buffers.Binary;
using System.Net;

namespace BootDiet.Core.Runtime;

/// <summary>
/// GetExtendedTcpTable / GetExtendedUdpTable 이 돌려주는 표(바이트 배열)를 해석한다.
/// 표 앞 4바이트는 행 개수, 그 뒤로 고정 크기 행이 이어진다. 포트는 네트워크 바이트 순서.
/// </summary>
public static class PortTable
{
    private const int Tcp4Row = 24;  // state, localAddr, localPort, remoteAddr, remotePort, pid
    private const int Tcp6Row = 56;  // localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid
    private const int Udp4Row = 12;  // localAddr, localPort, pid
    private const int Udp6Row = 28;  // localAddr[16], scope, localPort, pid

    public static IReadOnlyList<PortSample> ParseTcp4(ReadOnlySpan<byte> table) =>
        Parse(table, Tcp4Row, row => new PortSample("TCP", V4(row[4..8]), Port(row[8..12]), Pid(row[20..24])));

    public static IReadOnlyList<PortSample> ParseTcp6(ReadOnlySpan<byte> table) =>
        Parse(table, Tcp6Row, row => new PortSample("TCP", V6(row[..16]), Port(row[20..24]), Pid(row[52..56])));

    public static IReadOnlyList<PortSample> ParseUdp4(ReadOnlySpan<byte> table) =>
        Parse(table, Udp4Row, row => new PortSample("UDP", V4(row[..4]), Port(row[4..8]), Pid(row[8..12])));

    public static IReadOnlyList<PortSample> ParseUdp6(ReadOnlySpan<byte> table) =>
        Parse(table, Udp6Row, row => new PortSample("UDP", V6(row[..16]), Port(row[20..24]), Pid(row[24..28])));

    private delegate PortSample RowReader(ReadOnlySpan<byte> row);

    private static IReadOnlyList<PortSample> Parse(ReadOnlySpan<byte> table, int rowSize, RowReader read)
    {
        if (table.Length < 4)
            return [];
        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        var result = new List<PortSample>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            var offset = 4 + i * rowSize;
            if (offset + rowSize > table.Length)
                break;
            result.Add(read(table.Slice(offset, rowSize)));
        }
        return result;
    }

    private static string V4(ReadOnlySpan<byte> b) => new IPAddress(b).ToString();
    private static string V6(ReadOnlySpan<byte> b) => new IPAddress(b).ToString();
    private static int Port(ReadOnlySpan<byte> b) => (b[0] << 8) | b[1];
    private static int Pid(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32LittleEndian(b);
}

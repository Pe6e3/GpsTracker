using System.Collections.Concurrent;

namespace GpsTcpProxy;

public sealed class ConnectionManager
{
    private int _nextConnectionId;
    private readonly ConcurrentDictionary<int, ProxyConnection> _connections = new();

    public ProxyConnection Register(string clientIp)
    {
        var connectionId = Interlocked.Increment(ref _nextConnectionId);
        var connection = new ProxyConnection(connectionId, clientIp);
        _connections[connectionId] = connection;
        return connection;
    }

    public void Unregister(ProxyConnection connection) =>
        _connections.TryRemove(connection.ConnectionId, out _);

    public IReadOnlyCollection<ProxyConnection> GetActiveConnections() =>
        _connections.Values.ToArray();
}

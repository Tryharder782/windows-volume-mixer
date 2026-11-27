using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VolumeMixer.Models;

namespace VolumeMixer.Services;

public sealed class TabWebSocketServer : IDisposable
{
    private sealed class ClientConnection
    {
        public string ClientId { get; } = Guid.NewGuid().ToString("N")[..8];
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private sealed class ExtensionMessage
    {
        public string? type { get; set; }
        public string? browser { get; set; }
        public List<TabPayload>? tabs { get; set; }
    }

    private sealed class TabPayload
    {
        public int id { get; set; }
        public string? title { get; set; }
        public string? favIconUrl { get; set; }
        public bool audible { get; set; }
        public bool hasMedia { get; set; }
        public bool isPlaying { get; set; }
        public double volume { get; set; } = 100;
        public bool muted { get; set; }
        public string? mediaTitle { get; set; }
        public string? mediaArtist { get; set; }
        public string? artworkUrl { get; set; }
        public double position { get; set; }
        public double duration { get; set; }
        public bool isLive { get; set; }
        public bool canPrev { get; set; }
        public bool canNext { get; set; }
    }

    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<string, List<BrowserTabItem>> _tabsByClient = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _faviconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingFavicons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private TcpListener? _listener;

    public event Action? TabsUpdated;

    public bool HasConnectedClients => !_clients.IsEmpty;

    public TabWebSocketServer(int port = 49152)
    {
        _port = port;
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch
        {
            // Port in use or restricted
        }
    }

    public List<BrowserTabItem> GetTabsSnapshot()
    {
        var list = new List<BrowserTabItem>();
        foreach (var kvp in _tabsByClient)
        {
            list.AddRange(kvp.Value);
        }
        return list;
    }

    public void SendSetTabVolume(string clientId, int tabId, double volume, bool muted)
    {
        if (!_clients.TryGetValue(clientId, out var client)) return;

        var payload = JsonSerializer.Serialize(new
        {
            type = "setVolume",
            tabId,
            volume = Math.Clamp(Math.Round(volume), 0, 100),
            muted
        });

        _ = SendTextFrameAsync(client, payload);
    }

    public void SendToggleTabPlayPause(string clientId, int tabId)
        => SendTabTransportCommand(clientId, tabId, "togglePlayPause");

    public void SendTabTransportCommand(string clientId, int tabId, string type)
    {
        if (!_clients.TryGetValue(clientId, out var client)) return;

        var payload = JsonSerializer.Serialize(new
        {
            type,
            tabId
        });

        _ = SendTextFrameAsync(client, payload);
    }

    public void SendTabSeek(string clientId, int tabId, double positionSeconds)
    {
        if (!_clients.TryGetValue(clientId, out var client)) return;

        var payload = JsonSerializer.Serialize(new
        {
            type = "seek",
            tabId,
            position = Math.Max(0, positionSeconds)
        });

        _ = SendTextFrameAsync(client, payload);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(tcp, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, ct);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        ClientConnection? conn = null;
        try
        {
            var stream = tcp.GetStream();
            var requestText = await ReadHttpUpgradeRequestAsync(stream, ct);
            if (string.IsNullOrEmpty(requestText))
            {
                tcp.Dispose();
                return;
            }

            var match = Regex.Match(requestText, @"Sec-WebSocket-Key:\s*(.+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                tcp.Dispose();
                return;
            }

            string swk = match.Groups[1].Value.Trim();
            string swkAccept = Convert.ToBase64String(
                SHA1.HashData(Encoding.UTF8.GetBytes(swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                $"Sec-WebSocket-Accept: {swkAccept}\r\n\r\n";

            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes, ct);

            conn = new ClientConnection { Tcp = tcp, Stream = stream };
            _clients[conn.ClientId] = conn;
            TabsUpdated?.Invoke();

            while (!ct.IsCancellationRequested && tcp.Connected)
            {
                var (opcode, payload) = await ReadFrameAsync(stream, ct);
                if (opcode == 0x8 || payload == null) break; // Close

                if (opcode == 0x9) // Ping -> Pong
                {
                    await SendFrameAsync(conn, 0xA, payload);
                    continue;
                }

                if (opcode == 0x1 && payload.Length > 0) // Text
                {
                    string json = Encoding.UTF8.GetString(payload);
                    ProcessClientMessage(conn.ClientId, json);
                }
            }
        }
        catch
        {
            // Client disconnected
        }
        finally
        {
            if (conn != null)
            {
                _clients.TryRemove(conn.ClientId, out _);
                _tabsByClient.TryRemove(conn.ClientId, out _);
                TabsUpdated?.Invoke();
                conn.WriteLock.Dispose();
            }
            try { tcp.Dispose(); } catch { }
        }
    }

    private void ProcessClientMessage(string clientId, string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<ExtensionMessage>(json);
            if (msg == null) return;

            if (msg.type == "ping")
            {
                if (_clients.TryGetValue(clientId, out var client))
                {
                    _ = SendTextFrameAsync(client, "{\"type\":\"pong\"}");
                }
                return;
            }

            if (msg.type != "tabs" || msg.tabs == null) return;

            string browser = string.IsNullOrWhiteSpace(msg.browser) ? "Browser" : msg.browser;
            var items = new List<BrowserTabItem>(msg.tabs.Count);

            foreach (var t in msg.tabs)
            {
                if (!t.audible && !t.hasMedia) continue;

                bool canControl = t.hasMedia || t.audible;
                string fallbackTitle = string.IsNullOrWhiteSpace(t.title) ? "Вкладка" : t.title;
                var item = new BrowserTabItem
                {
                    TabId = t.id,
                    ClientId = clientId,
                    FavIconUrl = t.favIconUrl,
                    ArtworkUrl = t.artworkUrl,
                    Icon = GetOrFetchImage(t.favIconUrl, 24),
                    MediaThumbnail = GetOrFetchImage(t.artworkUrl, 96),
                    MediaTitle = string.IsNullOrWhiteSpace(t.mediaTitle) ? fallbackTitle : t.mediaTitle,
                    MediaArtist = string.IsNullOrWhiteSpace(t.mediaArtist) ? browser : t.mediaArtist,
                    CanPrev = t.canPrev || t.duration > 0,
                    CanNext = t.canNext || t.duration > 0
                };
                item.UpdateFromExtension(
                    t.volume,
                    t.muted,
                    fallbackTitle,
                    browser,
                    t.audible,
                    t.isPlaying,
                    canControl);

                item.UpdateTimelineFromSource(
                    t.position,
                    t.duration,
                    t.isLive,
                    !t.isLive && t.duration > 0);

                item.OnChanged = (vol, muted) => SendSetTabVolume(clientId, t.id, vol, muted);
                if (canControl)
                {
                    item.OnTogglePlayPause = () =>
                    {
                        item.IsPlaying = !item.IsPlaying;
                        SendToggleTabPlayPause(clientId, t.id);
                    };
                    item.OnPrevious = () => SendTabTransportCommand(clientId, t.id, "previous");
                    item.OnNext = () => SendTabTransportCommand(clientId, t.id, "next");
                    item.OnSeek = sec => SendTabSeek(clientId, t.id, sec);
                }
                items.Add(item);
            }

            _tabsByClient[clientId] = items;
            TabsUpdated?.Invoke();
        }
        catch
        {
            // Ignore malformed message
        }
    }

    private static async Task<string> ReadHttpUpgradeRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (sb.Length < 8192)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) return string.Empty;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }
        return sb.ToString();
    }

    private static async Task<(byte Opcode, byte[]? Payload)> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[2];
        if (!await ReadExactAsync(stream, header, 2, ct)) return (0x8, null);

        byte opcode = (byte)(header[0] & 0x0F);
        bool masked = (header[1] & 0x80) != 0;
        long payloadLen = header[1] & 0x7F;

        if (payloadLen == 126)
        {
            byte[] ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, 2, ct)) return (0x8, null);
            payloadLen = (ext[0] << 8) | ext[1];
        }
        else if (payloadLen == 127)
        {
            byte[] ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, 8, ct)) return (0x8, null);
            payloadLen = 0;
            for (int i = 0; i < 8; i++)
                payloadLen = (payloadLen << 8) | ext[i];
        }

        if (payloadLen < 0 || payloadLen > 1_048_576) return (0x8, null);

        byte[]? maskKey = null;
        if (masked)
        {
            maskKey = new byte[4];
            if (!await ReadExactAsync(stream, maskKey, 4, ct)) return (0x8, null);
        }

        byte[] payload = new byte[payloadLen];
        if (payloadLen > 0 && !await ReadExactAsync(stream, payload, (int)payloadLen, ct))
            return (0x8, null);

        if (masked && maskKey != null)
        {
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= maskKey[i % 4];
        }

        return (opcode, payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    private static Task SendTextFrameAsync(ClientConnection client, string text)
        => SendFrameAsync(client, 0x1, Encoding.UTF8.GetBytes(text));

    private static async Task SendFrameAsync(ClientConnection client, byte opcode, byte[] payload)
    {
        bool acquired = false;
        try
        {
            await client.WriteLock.WaitAsync();
            acquired = true;

            using var ms = new MemoryStream();
            ms.WriteByte((byte)(0x80 | (opcode & 0x0F)));

            if (payload.Length < 126)
            {
                ms.WriteByte((byte)payload.Length);
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                ms.WriteByte(126);
                ms.WriteByte((byte)(payload.Length >> 8));
                ms.WriteByte((byte)(payload.Length & 0xFF));
            }
            else
            {
                ms.WriteByte(127);
                long len = payload.Length;
                for (int i = 7; i >= 0; i--)
                    ms.WriteByte((byte)((len >> (8 * i)) & 0xFF));
            }

            ms.Write(payload, 0, payload.Length);
            byte[] frame = ms.ToArray();
            await client.Stream.WriteAsync(frame);
        }
        catch
        {
            // Ignore write failures or disposed lock on closed sockets
        }
        finally
        {
            if (acquired)
            {
                try { client.WriteLock.Release(); } catch { }
            }
        }
    }

    private ImageSource? GetOrFetchImage(string? url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string cacheKey = $"{decodeWidth}|{url}";
        if (_faviconCache.TryGetValue(cacheKey, out var cached)) return cached;

        if (_pendingFavicons.TryAdd(cacheKey, 1))
        {
            _ = Task.Run(async () =>
            {
                ImageSource? decoded = null;
                try
                {
                    byte[]? bytes = null;
                    if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        int comma = url.IndexOf(',');
                        if (comma > 0 && url[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                        {
                            bytes = Convert.FromBase64String(url[(comma + 1)..]);
                        }
                    }
                    else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        bytes = await _httpClient.GetByteArrayAsync(url, _cts.Token);
                    }

                    if (bytes is { Length: > 0 })
                    {
                        using var ms = new MemoryStream(bytes);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = decodeWidth;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        decoded = bmp;
                    }
                }
                catch
                {
                    decoded = null;
                }
                finally
                {
                    _faviconCache[cacheKey] = decoded;
                    _pendingFavicons.TryRemove(cacheKey, out _);
                    if (decoded != null)
                    {
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            foreach (var tabList in _tabsByClient.Values)
                            {
                                foreach (var tab in tabList)
                                {
                                    if (decodeWidth <= 32 && string.Equals(tab.FavIconUrl, url, StringComparison.OrdinalIgnoreCase))
                                    {
                                        tab.Icon = decoded;
                                    }
                                    if (decodeWidth > 32 && string.Equals(tab.ArtworkUrl, url, StringComparison.OrdinalIgnoreCase))
                                    {
                                        tab.MediaThumbnail = decoded;
                                    }
                                }
                            }
                            TabsUpdated?.Invoke();
                        });
                    }
                }
            });
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var client in _clients.Values)
        {
            try { client.Tcp.Dispose(); } catch { }
        }
        _clients.Clear();
        _httpClient.Dispose();
        _cts.Dispose();
    }
}

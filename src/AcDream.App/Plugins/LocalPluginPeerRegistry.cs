using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Plugins;

internal sealed class LocalPluginPeerRegistry : IDisposable
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private const long MaximumDocumentBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Guid _instanceId;
    private bool _disposed;

    public LocalPluginPeerRegistry(
        string directory,
        TimeProvider? timeProvider = null,
        Guid? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _time = timeProvider ?? TimeProvider.System;
        _instanceId = instanceId ?? Guid.NewGuid();
        _path = Path.Combine(_directory, $"peer-{_instanceId:N}.json");
        ClientId = BitConverter.ToUInt32(_instanceId.ToByteArray(), 0);
        if (ClientId == 0u)
            ClientId = 1u;
    }

    public uint ClientId { get; private set; }

    public void Publish(in PluginNetworkClient client)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(_directory);
        var document = PeerDocument.From(
            client with { ClientId = ClientId },
            _instanceId,
            _time.GetUtcNow().ToUnixTimeMilliseconds());
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public IReadOnlyList<PluginNetworkClient> CaptureRemoteClients()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Directory.Exists(_directory))
            return Array.Empty<PluginNetworkClient>();
        long newestAllowed = _time.GetUtcNow().Subtract(StaleAfter)
            .ToUnixTimeMilliseconds();
        var result = new List<PluginNetworkClient>();
        foreach (string file in Directory.EnumerateFiles(
            _directory,
            "peer-*.json",
            SearchOption.TopDirectoryOnly))
        {
            if (file.Equals(_path, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length is <= 0 or > MaximumDocumentBytes)
                    continue;
                PeerDocument? document = JsonSerializer.Deserialize<PeerDocument>(
                    File.ReadAllText(file),
                    JsonOptions);
                if (document is null
                    || document.InstanceId == _instanceId
                    || document.UpdatedUnixMs < newestAllowed
                    || document.ClientId == 0u
                    || document.PlayerId == 0u
                    || string.IsNullOrWhiteSpace(document.Name)
                    || document.Name.Length > 128
                    || document.WorldName is null
                    || document.WorldName.Length > 128
                    || document.Tags is null
                    || document.Tags.Length > 128
                    || !double.IsFinite(document.EastWest)
                    || !double.IsFinite(document.NorthSouth)
                    || !double.IsFinite(document.Elevation)
                    || !float.IsFinite(document.Heading))
                {
                    continue;
                }
                result.Add(document.ToClient());
            }
            catch (IOException)
            {
                // A peer can atomically replace or remove its own heartbeat
                // between enumeration and read. It will reappear next scan.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }
        return result
            .OrderBy(static client => client.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static client => client.ClientId)
            .ToArray();
    }

    public void Withdraw()
    {
        if (_disposed)
            return;
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Withdraw();
        _disposed = true;
    }

    private sealed class PeerDocument
    {
        public Guid InstanceId { get; set; }
        public long UpdatedUnixMs { get; set; }
        public uint ClientId { get; set; }
        public uint PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string WorldName { get; set; } = string.Empty;
        public string[] Tags { get; set; } = [];
        public uint CellId { get; set; }
        public double EastWest { get; set; }
        public double NorthSouth { get; set; }
        public double Elevation { get; set; }
        public bool IsOutdoor { get; set; }
        public float Heading { get; set; }
        public uint CurrentHealth { get; set; }
        public uint CurrentMana { get; set; }
        public uint CurrentStamina { get; set; }
        public uint MaxHealth { get; set; }
        public uint MaxMana { get; set; }
        public uint MaxStamina { get; set; }

        public static PeerDocument From(
            in PluginNetworkClient client,
            Guid instanceId,
            long updatedUnixMs) => new()
        {
            InstanceId = instanceId,
            UpdatedUnixMs = updatedUnixMs,
            ClientId = client.ClientId,
            PlayerId = client.PlayerId,
            Name = client.Name,
            WorldName = client.WorldName,
            Tags = client.Tags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToArray(),
            CellId = client.Position.CellId,
            EastWest = client.Position.EastWest,
            NorthSouth = client.Position.NorthSouth,
            Elevation = client.Position.Elevation,
            IsOutdoor = client.Position.IsOutdoor,
            Heading = client.Heading,
            CurrentHealth = client.CurrentHealth,
            CurrentMana = client.CurrentMana,
            CurrentStamina = client.CurrentStamina,
            MaxHealth = client.MaxHealth,
            MaxMana = client.MaxMana,
            MaxStamina = client.MaxStamina,
        };

        public PluginNetworkClient ToClient() => new(
            ClientId,
            PlayerId,
            Name,
            WorldName,
            new PluginNavigationPosition(
                CellId,
                EastWest,
                NorthSouth,
                Elevation,
                Heading,
                IsOutdoor),
            Tags,
            CurrentHealth,
            CurrentMana,
            CurrentStamina,
            MaxHealth,
            MaxMana,
            MaxStamina,
            Heading);
    }
}

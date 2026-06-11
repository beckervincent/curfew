using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Curfew.Core;
using Curfew.Core.Security;

namespace Curfew.Service;

/// <summary>
/// Hosts the config-write named pipe as SYSTEM. The app sends config writes here
/// once <c>config.db</c> is read-only for ordinary users; the service verifies the
/// parent passcode and performs the write itself. The pipe ACL lets any
/// authenticated user connect, but every write is passcode-gated (except the
/// first-run bootstrap before a passcode exists), so connecting buys nothing.
/// </summary>
internal sealed class ConfigPipeServer
{
    private readonly SettingsStore _config;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <param name="config">A config-writable settings store owned by the service.</param>
    public ConfigPipeServer(SettingsStore config) => _config = config;

    /// <summary>Accepts connections until cancelled. Never throws out of the loop.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleConnectionAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ServiceLog.Write($"config pipe: {ex.Message}");
                // Brief pause so a persistent failure cannot spin the CPU.
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ConfigPipe.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server);
        var writer = new StreamWriter(server) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        var response = Handle(line);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), ct).ConfigureAwait(false);
    }

    private ConfigResponse Handle(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new ConfigResponse(false, "empty request");

        ConfigRequest? request;
        try { request = JsonSerializer.Deserialize<ConfigRequest>(line, Json); }
        catch (JsonException) { return new ConfigResponse(false, "bad request"); }
        if (request is null) return new ConfigResponse(false, "bad request");

        return request.Op switch
        {
            ConfigPipe.OpSet => HandleSet(request),
            _ => new ConfigResponse(false, $"unknown op '{request.Op}'"),
        };
    }

    private ConfigResponse HandleSet(ConfigRequest request)
    {
        if (string.IsNullOrEmpty(request.Key) || request.Value is null)
            return new ConfigResponse(false, "key/value required");

        // Only config keys may be written through the pipe; state is child-writable.
        if (SettingsPartition.StoreFor(request.Key) != SettingsStoreKind.Config)
            return new ConfigResponse(false, "not a config key");

        // Gate on the parent passcode, except the first-run bootstrap (no passcode yet).
        var stored = _config.Get("passcode");
        if (!string.IsNullOrEmpty(stored) && !PasscodeHash.Verify(request.Passcode, stored))
            return new ConfigResponse(false, "passcode required");

        _config.Set(request.Key, request.Value);
        return new ConfigResponse(true);
    }
}

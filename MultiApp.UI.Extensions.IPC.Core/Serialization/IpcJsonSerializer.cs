using System.Text.Json;
using MultiApp.UI.Extensions.IPC.Core.Protocol;

namespace MultiApp.UI.Extensions.IPC.Core.Serialization;

public static class IpcJsonSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string SerializePacket(IpcPacket packet) => JsonSerializer.Serialize(packet, JsonOptions);

    public static IpcPacket DeserializePacket(string json)
    {
        var packet = JsonSerializer.Deserialize<IpcPacket>(json, JsonOptions);
        return packet ?? throw new InvalidOperationException("Deserialized packet cannot be null.");
    }

    public static string? SerializePayload<T>(T payload)
    {
        if (payload is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static T? DeserializePayload<T>(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
    }

    public static object? DeserializePayload(string? payloadJson, Type payloadType)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize(payloadJson, payloadType, JsonOptions);
    }
}

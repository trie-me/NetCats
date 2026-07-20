using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using PurrfectSeat.Contracts;

namespace PurrfectSeat.Wasm.Services;

/// <summary>Exports the browser-local booking service to the existing JavaScript client.</summary>
[SupportedOSPlatform("browser")]
public static partial class WasmBookingBridge
{
    private static BrowserBookingApi? api;

    internal static void Initialize(BrowserBookingApi bookingApi) => api = bookingApi;

    [JSExport]
    public static async Task<string> ListPerformancesJson()
    {
        var service = GetApi();
        return JsonSerializer.Serialize((await service.ListPerformancesAsync().ConfigureAwait(false)).ToArray(), WasmJsonContext.Default.PerformanceSummaryResponseArray);
    }

    [JSExport]
    public static async Task<string> GetAvailabilityJson(string performanceId)
    {
        var response = Guid.TryParse(performanceId, out var id)
            ? await GetApi().GetAvailabilityAsync(id).ConfigureAwait(false)
            : BrowserApiResponse<AvailabilityResponse>.Validation("Invalid performance identifier.");
        return JsonSerializer.Serialize(response, WasmJsonContext.Default.BrowserApiResponseAvailabilityResponse);
    }

    [JSExport]
    public static async Task<string> PlaceHoldJson(string performanceId, string requestJson)
    {
        if (!Guid.TryParse(performanceId, out var id) || JsonSerializer.Deserialize(requestJson, WasmJsonContext.Default.HoldSeatsRequest) is not HoldSeatsRequest request)
        {
            return JsonSerializer.Serialize(BrowserApiResponse<HoldResponse>.Validation("Invalid hold request."), WasmJsonContext.Default.BrowserApiResponseHoldResponse);
        }

        return JsonSerializer.Serialize(await GetApi().PlaceHoldAsync(id, request.CustomerId, request.Seats).ConfigureAwait(false), WasmJsonContext.Default.BrowserApiResponseHoldResponse);
    }

    [JSExport]
    public static async Task<string> ConfirmHoldJson(string performanceId, string holdId, string requestJson)
    {
        if (!Guid.TryParse(performanceId, out var performance) || !Guid.TryParse(holdId, out var hold) || JsonSerializer.Deserialize(requestJson, WasmJsonContext.Default.ConfirmHoldRequest) is not ConfirmHoldRequest request)
        {
            return JsonSerializer.Serialize(BrowserApiResponse<HoldResponse>.Validation("Invalid confirmation request."), WasmJsonContext.Default.BrowserApiResponseHoldResponse);
        }

        return JsonSerializer.Serialize(await GetApi().ConfirmHoldAsync(performance, hold, request.PaymentMethodToken, request.IdempotencyKey).ConfigureAwait(false), WasmJsonContext.Default.BrowserApiResponseHoldResponse);
    }

    [JSExport]
    public static async Task<string> CancelHoldJson(string performanceId, string holdId)
    {
        if (!Guid.TryParse(performanceId, out var performance) || !Guid.TryParse(holdId, out var hold))
        {
            return JsonSerializer.Serialize(BrowserApiResponse<object>.Validation("Invalid hold identifier."), WasmJsonContext.Default.BrowserApiResponseObject);
        }

        return JsonSerializer.Serialize(await GetApi().CancelHoldAsync(performance, hold).ConfigureAwait(false), WasmJsonContext.Default.BrowserApiResponseObject);
    }

    private static BrowserBookingApi GetApi() => api ?? throw new InvalidOperationException("The browser booking service has not started.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PerformanceSummaryResponse[]))]
[JsonSerializable(typeof(AvailabilityResponse))]
[JsonSerializable(typeof(HoldResponse))]
[JsonSerializable(typeof(HoldSeatsRequest))]
[JsonSerializable(typeof(ConfirmHoldRequest))]
[JsonSerializable(typeof(BrowserApiResponse<AvailabilityResponse>))]
[JsonSerializable(typeof(BrowserApiResponse<HoldResponse>))]
[JsonSerializable(typeof(BrowserApiResponse<object>))]
internal partial class WasmJsonContext : JsonSerializerContext;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Familiar.Server.Auth;

namespace Familiar.Server.Tests.Infrastructure;

/// <summary>Small helpers over the auth API, so tests read as the flows they test.</summary>
public static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<HttpResponseMessage> RegisterAsync(this HttpClient client, string username, string password = "correct horse") =>
        client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, $"{username}@example.test", password));

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, string username, string password = "correct horse") =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));

    public static async Task<DeviceLoginResponse> DeviceLoginAsync(this HttpClient client, string username, string device = "Test phone", string password = "correct horse")
    {
        var response = await client.PostAsJsonAsync("/api/auth/devices/login", new DeviceLoginRequest(username, password, device));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceLoginResponse>(Json))!;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>A client that sends no cookies, only the device token - as the phone apps do.</summary>
    public static HttpClient DeviceClient(this FamiliarAppFactory factory, string token)
    {
        var client = factory.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>
/// Client for the League Client Update (LCU) REST API running on localhost.
/// Used to read and override the player's chat presence without any MITM proxy.
/// </summary>
internal class LcuClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    private LcuClient(int port, string authToken)
    {
        _baseUrl = $"https://127.0.0.1:{port}";

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{authToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
    }

    /// <summary>
    /// Attempts to find the running LeagueClientUx process and extract its API connection
    /// details (port + auth token) from the command line via WMIC.
    /// Returns null if the process is not running or details cannot be parsed.
    /// </summary>
    internal static LcuClient? TryCreate()
    {
        try
        {
            var processes = Process.GetProcessesByName("LeagueClientUx");
            if (processes.Length == 0) return null;

            var wmic = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = "process where name=\"LeagueClientUx.exe\" get commandline",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            wmic.Start();
            var output = wmic.StandardOutput.ReadToEnd();
            wmic.WaitForExit();

            var portMatch = Regex.Match(output, @"--app-port=(\d+)");
            var tokenMatch = Regex.Match(output, @"--remoting-auth-token=([^\s""]+)");

            if (!portMatch.Success || !tokenMatch.Success)
            {
                Trace.WriteLine("Found LeagueClientUx but could not parse connection details.");
                return null;
            }

            var port = int.Parse(portMatch.Groups[1].Value);
            var token = tokenMatch.Groups[1].Value;

            Trace.WriteLine($"Connected to LeagueClientUx API on port {port}.");
            return new LcuClient(port, token);
        }
        catch (Exception e)
        {
            Trace.WriteLine("Error finding LCU process: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets the current availability from the LCU chat endpoint.
    /// Returns values like "chat", "away", "dnd", "offline", "mobile", or null on failure.
    /// </summary>
    internal async Task<string?> GetAvailabilityAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/lol-chat/v1/me");
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonSerializer.Deserialize<JsonNode>(content);
            return json?["availability"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the player's chat availability via the LCU API.
    /// Valid values: "chat" (online), "away", "dnd", "offline", "mobile".
    /// </summary>
    internal async Task<bool> SetAvailabilityAsync(string availability)
    {
        try
        {
            var body = new JsonObject { ["availability"] = availability };
            var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync($"{_baseUrl}/lol-chat/v1/me", content);

            if (response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"Set availability to '{availability}'.");
                return true;
            }

            var error = await response.Content.ReadAsStringAsync();
            Trace.WriteLine($"Failed to set availability to '{availability}': {response.StatusCode} - {error}");
            return false;
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Error setting availability: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks whether the LCU API is still responding.
    /// </summary>
    internal async Task<bool> IsAliveAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/lol-chat/v1/me");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

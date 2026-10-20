using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.GenAI.Types;
using Pengin1011.Services.AI;

namespace Pengin1011.Tests;

public sealed class GoogleAIEndpointTests {
	[Theory]
	[InlineData(AIPresetProvider.Gemini, "", "", "https://generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Gemini, "", "https://generativelanguage.googleapis.com:443/", "https://generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Gemini, "", " HTTPS://GENERATIVELANGUAGE.GOOGLEAPIS.COM/ ", "https://generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "global", "", "https://aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "us-central1", "", "https://us-central1-aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "europe-west4", "", "https://europe-west4-aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "asia-southeast1", "", "https://asia-southeast1-aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "us", "", "https://aiplatform.us.rep.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "eu", "", "https://aiplatform.eu.rep.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "us-central1", "https://aiplatform.googleapis.com/", "https://aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "us-central1", "https://us-east4-aiplatform.googleapis.com", "https://us-east4-aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "us", "https://aiplatform.us.rep.googleapis.com:443/", "https://aiplatform.us.rep.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "eu", "https://aiplatform.eu.rep.googleapis.com", "https://aiplatform.eu.rep.googleapis.com")]
	public void OfficialEndpoint_ValidatesAndResolves(AIPresetProvider provider, string location, string baseUrl, string expected) {
		var preset = Preset(provider, location, baseUrl);

		Assert.Empty(AIPresetStore.ValidatePreset(preset, [], null));
		Assert.True(GoogleAIEndpoint.TryResolve(preset, out var resolved));
		Assert.Equal(expected, resolved);
	}

	[Theory]
	[InlineData(AIPresetProvider.Gemini, "http://generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com:8443")]
	[InlineData(AIPresetProvider.Gemini, "https://key@generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Gemini, "https://@generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com?key=x")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com?")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com#fragment")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com/v1beta")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com/v1/..")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com.evil.example")]
	[InlineData(AIPresetProvider.Gemini, "https://evil-generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com.")]
	[InlineData(AIPresetProvider.Gemini, "https://generativelanguage.googleapis.com\n/")]
	[InlineData(AIPresetProvider.Gemini, "https://aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Gemini, "https://127.0.0.1")]
	[InlineData(AIPresetProvider.Gemini, "not-a-url")]
	[InlineData(AIPresetProvider.Vertex, "http://aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "https://aiplatform.googleapis.com:8443")]
	[InlineData(AIPresetProvider.Vertex, "https://aiplatform.googleapis.com.evil.example")]
	[InlineData(AIPresetProvider.Vertex, "https://sub.us-central1-aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "https://us-aiplatform.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "https://aiplatform.asia.rep.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "https://generativelanguage.googleapis.com")]
	[InlineData(AIPresetProvider.Vertex, "https://us-central1-aiplatform.googleapis.com/path")]
	[InlineData(AIPresetProvider.Vertex, "https://us-central1-aiplatform.googleapis.com?key=x")]
	public void UnofficialEndpoint_RejectsBeforeBackendCreatesTransport(AIPresetProvider provider, string baseUrl) {
		var preset = Preset(provider, "us-central1", baseUrl);
		var factoryCalls = 0;

		Assert.Contains(AIPresetStore.ValidatePreset(preset, [], null), static error => error.Contains(L.Prefix("AIPresetBaseUrlInvalid")));
		var exception = Assert.Throws<ArgumentException>(() => new GeminiBackend(preset, new ClientOptions { HttpClientFactory = () => {
			factoryCalls++;
			return new HttpClient();
		} }));
		Assert.Equal("preset", exception.ParamName);
		Assert.Equal(0, factoryCalls);
	}

	[Theory]
	[InlineData("us-central1.evil.example/")]
	[InlineData("us-central1:443")]
	[InlineData("../us-central1")]
	public void EmptyVertexEndpoint_InvalidLocationCannotChangeDefaultHost(string location) {
		var preset = Preset(AIPresetProvider.Vertex, location, "");

		Assert.False(GoogleAIEndpoint.TryResolve(preset, out _));
		Assert.Contains(AIPresetStore.ValidatePreset(preset, [], null), static error => error.Contains(L.Prefix("AIPresetBaseUrlInvalid")));
	}

	[Fact]
	public async Task ProductionTransport_DoesNotRedirectCredentialHeader() {
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var endpoint = (IPEndPoint)listener.LocalEndpoint;
		var received = ServeRedirectAsync(listener, cancellation.Token);
		using var client = GeminiBackend.CreateHttpClient();
		client.DefaultRequestHeaders.Add("x-goog-api-key", "fake-key");

		using var response = await client.GetAsync($"http://127.0.0.1:{endpoint.Port}/", cancellation.Token);

		Assert.Equal(HttpStatusCode.Found, response.StatusCode);
		Assert.Equal("http://127.0.0.1:1/leak", response.Headers.Location?.AbsoluteUri);
		Assert.Contains("x-goog-api-key: fake-key", await received);
	}

	private static async Task<string> ServeRedirectAsync(TcpListener listener, CancellationToken ct) {
		using var connection = await listener.AcceptTcpClientAsync(ct);
		await using var stream = connection.GetStream();
		using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
		var request = new StringBuilder();
		while (await reader.ReadLineAsync(ct) is { Length: > 0 } line) request.AppendLine(line);
		await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/leak\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
		return request.ToString();
	}

	private static AIPreset Preset(AIPresetProvider provider, string location, string baseUrl) {
		return new AIPreset { Name = "google", Provider = provider, ApiKey = "fake-key", Model = "model", Project = "project", Location = location, BaseUrl = baseUrl };
	}
}

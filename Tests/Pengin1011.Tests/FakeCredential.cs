namespace Pengin1011.Tests;

internal sealed class FakeCredential : Google.Apis.Auth.OAuth2.ICredential {
	public void Initialize(Google.Apis.Http.ConfigurableHttpClient httpClient) {
	}

	public Task InterceptAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) {
		return Task.CompletedTask;
	}

	public Task<bool> HandleResponseAsync(System.Net.Http.HttpResponseMessage response, bool retrySupported, CancellationToken cancellationToken) {
		return Task.FromResult(false);
	}

	public Task<string> GetAccessTokenForRequestAsync(string? authUri = null, CancellationToken cancellationToken = default) {
		return Task.FromResult("fake-access-token");
	}
}

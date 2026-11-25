namespace Pengin1011.Services.AI;

internal sealed class NoRetryPolicy : System.ClientModel.Primitives.ClientRetryPolicy {
	public static readonly NoRetryPolicy Instance = new();

	protected override bool ShouldRetry(System.ClientModel.Primitives.PipelineMessage message, Exception? exception) {
		return false;
	}
}

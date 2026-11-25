using System.Threading;
using System.Threading.Tasks;

namespace ProjectFrontier.Llm
{
    /// <summary>
    /// Swappable text-generation backend. Implementations return the raw completion; parsing,
    /// timeouts, fallback and logging are handled by <see cref="LlmTurnService"/>.
    /// Implementations must honour <paramref name="ct"/> and throw on any transport or API error.
    /// </summary>
    public interface ILLMProvider
    {
        string Name { get; }

        /// <summary>True for providers that need the network (the service skips them when offline).</summary>
        bool RequiresNetwork { get; }

        Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct);
    }
}

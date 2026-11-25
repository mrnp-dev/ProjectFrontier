namespace ProjectFrontier.Llm
{
    /// <summary>Cheap token estimate (~4 chars per token for English, +4 per chat message overhead).</summary>
    public static class TokenEstimator
    {
        public const int PerMessageOverhead = 4;

        public static int Estimate(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

        public static int Estimate(ChatMessage message) => Estimate(message.Content) + PerMessageOverhead;
    }
}

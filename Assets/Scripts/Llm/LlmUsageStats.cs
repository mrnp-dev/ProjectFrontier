namespace ProjectFrontier.Llm
{
    /// <summary>Running per-session token stats, for the Milestone 2 gate (avg ~500 in / &lt;= 130 out).</summary>
    public sealed class LlmUsageStats
    {
        public int Turns { get; private set; }
        public int FallbackTurns { get; private set; }
        public int NonJsonTurns { get; private set; }
        public long TotalPromptTokens { get; private set; }
        public long TotalCompletionTokens { get; private set; }
        public int MaxPromptTokens { get; private set; }
        public int MaxCompletionTokens { get; private set; }

        public float AvgPromptTokens => Turns == 0 ? 0f : (float)TotalPromptTokens / Turns;
        public float AvgCompletionTokens => Turns == 0 ? 0f : (float)TotalCompletionTokens / Turns;

        public void Record(LlmTurnResult r)
        {
            Turns++;
            if (r.UsedFallbackProvider) FallbackTurns++;
            if (!r.ParsedJson) NonJsonTurns++;
            TotalPromptTokens += r.PromptTokens;
            TotalCompletionTokens += r.CompletionTokens;
            if (r.PromptTokens > MaxPromptTokens) MaxPromptTokens = r.PromptTokens;
            if (r.CompletionTokens > MaxCompletionTokens) MaxCompletionTokens = r.CompletionTokens;
        }

        public override string ToString() =>
            $"turns={Turns} avgIn={AvgPromptTokens:0} avgOut={AvgCompletionTokens:0} maxIn={MaxPromptTokens} maxOut={MaxCompletionTokens} fallback={FallbackTurns} nonJson={NonJsonTurns}";
    }
}

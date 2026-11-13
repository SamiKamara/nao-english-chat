namespace NaoOpenAIChat;

public sealed class EnergyGate(double noiseFloor)
{
    private double? candidateAt;
    public double Threshold { get; } = Math.Max(150, noiseFloor * 2.2 + 35);
    public double? FirstSpeech { get; private set; }
    public double LastSpeech { get; private set; }
    public bool HasSpeech => FirstSpeech.HasValue;
    public bool Update(double seconds, double energy)
    {
        if (energy >= Threshold * (HasSpeech ? 0.65 : 1))
        {
            candidateAt ??= seconds;
            if (HasSpeech || seconds - candidateAt >= 0.18)
            {
                FirstSpeech ??= candidateAt.Value;
                LastSpeech = seconds;
            }
        }
        else if (!HasSpeech) candidateAt = null;
        return HasSpeech && seconds - LastSpeech >= 1.25;
    }
}

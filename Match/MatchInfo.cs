using System.Collections.Immutable;
using Common.PkmnModels;

namespace Match
{
    public enum Side { Blue, Red }

    public record MatchResult(Side? Winner);

    public record MatchInfo(
        IImmutableList<Pokemon> TeamBlue,
        IImmutableList<Pokemon> TeamRed)
    {
        public string? Stage { get; init; }
        public string? FieldEffect { get; init; }
        public float Speed { get; init; } = 1f;
    }
}

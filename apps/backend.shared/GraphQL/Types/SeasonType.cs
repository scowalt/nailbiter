namespace backend.GraphQL.Types;

public enum SeasonType
{
    PRESEASON = 1,
    REGULAR = 2,
    POSTSEASON = 3
}

public static class SeasonTypeExtensions
{
    public static int ToDbValue(this SeasonType seasonType) => (int)seasonType;

    public static SeasonType? FromDbValue(int? value) => value switch
    {
        1 => SeasonType.PRESEASON,
        2 => SeasonType.REGULAR,
        3 => SeasonType.POSTSEASON,
        _ => null
    };
}

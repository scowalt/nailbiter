namespace backend.GraphQL.Types;

public enum Sport
{
    NBA,
    NFL,
    NHL,
    MLB,
    F1,
    NASCAR,
    INDYCAR,
    WORLD_CUP,
    CFB
}

public static class SportExtensions
{
    public static string ToDbValue(this Sport sport) => sport switch
    {
        Sport.WORLD_CUP => "world_cup",
        _ => sport.ToString().ToLower()
    };

    public static Sport? FromDbValue(string? value) => value?.ToUpper() switch
    {
        "NBA" => Sport.NBA,
        "NFL" => Sport.NFL,
        "NHL" => Sport.NHL,
        "MLB" => Sport.MLB,
        "F1" => Sport.F1,
        "NASCAR" => Sport.NASCAR,
        "INDYCAR" => Sport.INDYCAR,
        "WORLD_CUP" => Sport.WORLD_CUP,
        "CFB" => Sport.CFB,
        _ => null
    };
}

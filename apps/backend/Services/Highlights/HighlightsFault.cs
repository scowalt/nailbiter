using backend.Data;

namespace backend.Services.Highlights;

public sealed class HighlightsFault : Exception
{
    private HighlightsFault(string message) : base(message) { }

    public static Exception Reportable(Exception exception) => DatabaseFailure.Is(exception) ? exception : From(exception);

    public static HighlightsFault From(Exception exception)
    {
        var types = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            types.Add(current is HttpRequestException { StatusCode: { } status }
                ? $"{current.GetType().FullName} {(int)status}" : current.GetType().FullName!);
        return new(string.Join(" <- ", types));
    }
}

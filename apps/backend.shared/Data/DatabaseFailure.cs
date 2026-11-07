using System.Data.Common;

namespace backend.Data;

public static class DatabaseFailure
{
    public static bool Is(Exception? exception) => exception switch
    {
        null or OperationCanceledException => false,
        DbException => true,
        _ => Is(exception.InnerException)
    };
}

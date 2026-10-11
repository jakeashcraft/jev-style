using System.Net;

namespace JevStyle.Decisions;

/// <summary>Base type for decision failures.</summary>
public class DecisionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DecisionException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public DecisionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public DecisionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The server answered with an HTTP error.</summary>
public sealed class DecisionServerException : DecisionException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="message">Error message, preferably the server's own.</param>
    /// <param name="responseBody">Raw response body (truncated), when available.</param>
    /// <param name="retryAfter">How long the server asked the client to wait before retrying, when it said.</param>
    public DecisionServerException(HttpStatusCode statusCode, string message, string? responseBody = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Raw response body (truncated), when available.</summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// How long the server asked the client to wait before retrying, from <c>retry-after-ms</c> or
    /// <c>Retry-After</c> (seconds or an HTTP date). Null when the response did not say.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The server answered, but not in a form a decision can be read from.</summary>
public sealed class DecisionFormatException : DecisionException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong with the response.</param>
    public DecisionFormatException(string message)
        : base(message)
    {
    }
}

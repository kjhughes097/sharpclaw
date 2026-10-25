namespace SharpClaw.Cli;

public sealed class CliOptions
{
    public required string Url { get; init; }
    public required string AdminKey { get; init; }

    public static CliOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("SHARPCLAW_URL");
        if (string.IsNullOrWhiteSpace(url)) url = "http://localhost:8080";
        var key = Environment.GetEnvironmentVariable("SHARPCLAW_ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new CliException("SHARPCLAW_ADMIN_KEY is required.");
        return new CliOptions { Url = url.TrimEnd('/'), AdminKey = key };
    }
}

/// <summary>A user-facing CLI error whose message is printed and causes a non-zero exit.</summary>
public sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
}

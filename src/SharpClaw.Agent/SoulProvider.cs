using Microsoft.Extensions.Logging;

namespace SharpClaw.Agent;

public sealed class SoulProvider : ISoulProvider
{
    private const string Default = "You are a helpful assistant.";
    private readonly string _path;
    private readonly ILogger<SoulProvider> _logger;

    public SoulProvider(AgentConfig config, ILogger<SoulProvider> logger)
    {
        _path = config.SoulPath;
        _logger = logger;
    }

    public string Read()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("could not read soul file at {SoulPath}: {Error}", _path, ex.GetType().Name);
            return Default;
        }

        _logger.LogWarning("soul file missing or empty at {SoulPath}; using default", _path);
        return Default;
    }
}

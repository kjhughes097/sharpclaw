using SharpClaw.Cli;

try
{
    var options = CliOptions.FromEnvironment();
    using var gateway = new CliGateway(options);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    await Commands.RunAsync(args, gateway, Console.Out, Console.In, cts.Token);
    return 0;
}
catch (CliException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}

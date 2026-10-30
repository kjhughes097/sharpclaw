using SharpClaw.Sandbox;
using SharpClaw.Sandbox.Infrastructure;

// Code in the sandbox runs as this same user: make our /proc entries (and the service secret in
// our environment) unreadable to it before anything else happens.
Native.MakeNonDumpable();

var options = RunnerOptions.FromEnvironment();
Directory.CreateDirectory("/tmp/home");
var app = RunnerApp.Build(options);
app.Run();

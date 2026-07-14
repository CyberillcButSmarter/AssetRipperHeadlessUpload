using Ookii.CommandLine;
using System.ComponentModel;

namespace AssetRipper.GUI.Web;

[GeneratedParser]
[ParseOptions(IsPosix = true)]
internal sealed partial class Arguments
{
	[CommandLineArgument(DefaultValue = WebApplicationLauncher.Defaults.Port)]
	[Description("If nonzero, the application will attempt to host on this port, instead of finding a random unused port.")]
	public int Port { get; set; }

	[CommandLineArgument(DefaultValue = WebApplicationLauncher.Defaults.Host)]
	[Description("The address to bind to. Defaults to 127.0.0.1 (loopback only). Use 0.0.0.0 to accept connections from other machines on your LAN/VPN.")]
	public string Host { get; set; } = WebApplicationLauncher.Defaults.Host;

	[CommandLineArgument(DefaultValue = WebApplicationLauncher.Defaults.Log)]
	[Description("If true, the application will log to a file.")]
	public bool Log { get; set; }

	[CommandLineArgument(DefaultValue = WebApplicationLauncher.Defaults.LogPath)]
	[Description("The file location at which to save the log, or a sensible default if not provided.")]
	public string? LogPath { get; set; }

	[CommandLineArgument("local-web-file")]
	[Description("Files provided with this option will replace online sources.")]
	public string[]? LocalWebFiles { get; set; }

	[CommandLineArgument(DefaultValue = false)]
	[Description("If true, a browser window will not be launched automatically.")]
	public bool Headless { get; set; }
}

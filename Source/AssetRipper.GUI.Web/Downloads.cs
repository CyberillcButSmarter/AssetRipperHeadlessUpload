using AssetRipper.Import.Logging;
using AssetRipper.Web.Extensions;
using Microsoft.AspNetCore.Http;
using System.IO.Compression;

namespace AssetRipper.GUI.Web;

/// <summary>
/// Export flow for a remotely-driven host. The stock export endpoints write to the
/// server's own disk, which is useless when driven from another machine. Instead an
/// export runs as a background job (so its log can be shown live in the browser),
/// the result is zipped and cached per game (so re-exporting the same game is
/// skipped), and streamed back on demand. See <see cref="ExportManager"/>.
/// </summary>
internal static class Downloads
{
	internal static class UnityProject
	{
		public static Task HandleStart(HttpContext context) => Start(context, ExportKind.UnityProject);
	}

	internal static class PrimaryContent
	{
		public static Task HandleStart(HttpContext context) => Start(context, ExportKind.PrimaryContent);
	}

	private static Task Start(HttpContext context, ExportKind kind)
	{
		if (!GameFileLoader.IsLoaded)
		{
			context.Response.Redirect("/Commands");
			return Task.CompletedTask;
		}

		// Begin decides start-fresh vs serve-cache vs busy. Only a fresh start kicks off
		// the background work; the others already have a result or one is in flight.
		if (ExportManager.Begin(kind) == BeginOutcome.Started)
		{
			_ = Task.Run(() => RunExport(kind));
		}

		// Everyone lands on the live progress page.
		context.Response.Redirect("/Export/Progress");
		return Task.CompletedTask;
	}

	private static async Task RunExport(ExportKind kind)
	{
		string workingDirectory = Path.Combine(Path.GetTempPath(), "AssetRipperExports", Guid.NewGuid().ToString("N"));
		string exportDirectory = Path.Combine(workingDirectory, "export");
		string zipPath = Path.Combine(workingDirectory, "AssetRipper_export.zip");

		try
		{
			Directory.CreateDirectory(exportDirectory);

			if (kind == ExportKind.UnityProject)
			{
				await GameFileLoader.ExportUnityProject(exportDirectory);
			}
			else
			{
				await GameFileLoader.ExportPrimaryContent(exportDirectory);
			}

			ExportManager.Append("Compressing export into a zip...");
			ZipFile.CreateFromDirectory(exportDirectory, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

			// Keep only the zip; the raw export tree can be large.
			try
			{
				Directory.Delete(exportDirectory, true);
			}
			catch (Exception ex)
			{
				Logger.Warning(LogCategory.Export, $"Could not remove raw export tree: {ex.Message}");
			}

			long megabytes = new FileInfo(zipPath).Length / (1024 * 1024);
			ExportManager.Append($"Export complete ({megabytes} MB). Ready to download.");
			ExportManager.Complete(kind, zipPath);
		}
		catch (Exception ex)
		{
			Logger.Error(LogCategory.Export, $"Export failed: {ex}");
			ExportManager.Fail($"Export failed: {ex.Message}");
			try
			{
				Directory.Delete(workingDirectory, true);
			}
			catch
			{
				// best-effort cleanup
			}
		}
	}

	/// <summary>GET /Export/Progress - the live progress/log page.</summary>
	public static async Task HandleProgressPage(HttpContext context)
	{
		context.Response.ContentType = "text/html; charset=utf-8";
		context.Response.DisableCaching();
		await context.Response.WriteAsync(ProgressPageHtml);
	}

	/// <summary>GET /Export/Progress/Poll - plain-text log; state in the X-Export-State header.</summary>
	public static async Task HandlePoll(HttpContext context)
	{
		(ExportRunState state, string log, bool downloadReady) = ExportManager.Snapshot();
		context.Response.DisableCaching();
		context.Response.Headers["X-Export-State"] = state.ToString();
		context.Response.Headers["X-Download-Ready"] = downloadReady ? "1" : "0";
		context.Response.ContentType = "text/plain; charset=utf-8";
		await context.Response.WriteAsync(log);
	}

	/// <summary>GET /Export/Download - streams the completed (cached) zip.</summary>
	public static async Task HandleDownload(HttpContext context)
	{
		string? zip = ExportManager.CompletedZip();
		if (zip is null)
		{
			context.Response.Redirect("/Export/Progress");
			return;
		}

		context.Response.ContentType = "application/zip";
		context.Response.Headers.ContentDisposition = "attachment; filename=\"AssetRipper_export.zip\"";
		await context.Response.SendFileAsync(zip);
	}

	private const string ProgressPageHtml = """
		<!DOCTYPE html>
		<html lang="en">
		<head>
			<meta charset="utf-8">
			<meta name="viewport" content="width=device-width, initial-scale=1">
			<title>Export progress</title>
			<style>
				body { font-family: system-ui, sans-serif; margin: 1.5rem; background: #1d2021; color: #ebdbb2; }
				h2 { margin-top: 0; }
				#status { font-weight: bold; }
				#status.fail { color: #fb4934; }
				#status.done { color: #b8bb26; }
				pre { background: #282828; color: #ebdbb2; padding: 1rem; border-radius: .35rem;
					height: 60vh; overflow: auto; white-space: pre-wrap; word-break: break-word; }
				.btn { display: inline-block; padding: .5rem 1rem; margin-right: .5rem; border-radius: .35rem;
					text-decoration: none; color: #1d2021; background: #83a598; }
				.btn.dl { background: #b8bb26; }
			</style>
		</head>
		<body>
			<h2>Export progress</h2>
			<p id="status">Starting&hellip;</p>
			<pre id="log"></pre>
			<p>
				<a class="btn" href="/Commands">&larr; Back</a>
				<a class="btn dl" id="dl" href="/Export/Download" style="display:none">Download .zip</a>
			</p>
			<script>
				let finished = false;
				const logEl = document.getElementById('log');
				const statusEl = document.getElementById('status');
				const dlEl = document.getElementById('dl');
				async function poll() {
					try {
						const r = await fetch('/Export/Progress/Poll', { cache: 'no-store' });
						const state = r.headers.get('X-Export-State') || '';
						const ready = r.headers.get('X-Download-Ready') === '1';
						logEl.textContent = await r.text();
						logEl.scrollTop = logEl.scrollHeight;
						if (state === 'Completed') {
							statusEl.textContent = 'Export complete.'; statusEl.className = 'done';
							if (ready) { dlEl.style.display = 'inline-block'; }
							finished = true;
						} else if (state === 'Failed') {
							statusEl.textContent = 'Export failed — see log below.'; statusEl.className = 'fail';
							finished = true;
						} else if (state === 'Running') {
							statusEl.textContent = 'Exporting…';
						} else {
							statusEl.textContent = 'Idle.';
						}
					} catch (e) { /* keep polling */ }
					if (!finished) { setTimeout(poll, 1000); }
				}
				poll();
			</script>
		</body>
		</html>
		""";
}

using AssetRipper.Import.Logging;
using Microsoft.AspNetCore.Http;
using System.IO.Compression;

namespace AssetRipper.GUI.Web;

/// <summary>
/// Streams export results back to a remote browser as a downloadable zip.
/// The stock export endpoints write to a path on the server's own disk, which is
/// useless when the host is driven from another machine. These handlers export to
/// a temporary directory, zip it, send it, and clean up - so the decompiled output
/// comes back to whoever uploaded the game.
/// </summary>
internal static class Downloads
{
	internal static class UnityProject
	{
		public static Task HandleGetRequest(HttpContext context) => Handle(context, unityProject: true);
	}

	internal static class PrimaryContent
	{
		public static Task HandleGetRequest(HttpContext context) => Handle(context, unityProject: false);
	}

	private static async Task Handle(HttpContext context, bool unityProject)
	{
		if (!GameFileLoader.IsLoaded)
		{
			context.Response.Redirect("/Commands");
			return;
		}

		string workingDirectory = Path.Combine(Path.GetTempPath(), "AssetRipperExports", Guid.NewGuid().ToString("N"));
		string exportDirectory = Path.Combine(workingDirectory, "export");
		string zipPath = Path.Combine(workingDirectory, "AssetRipper_export.zip");
		Directory.CreateDirectory(exportDirectory);

		try
		{
			if (unityProject)
			{
				await GameFileLoader.ExportUnityProject(exportDirectory);
			}
			else
			{
				await GameFileLoader.ExportPrimaryContent(exportDirectory);
			}

			ZipFile.CreateFromDirectory(exportDirectory, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

			context.Response.ContentType = "application/zip";
			context.Response.Headers.ContentDisposition = "attachment; filename=\"AssetRipper_export.zip\"";
			await context.Response.SendFileAsync(zipPath);
		}
		catch (Exception ex)
		{
			Logger.Error(LogCategory.Export, $"Export download failed: {ex}");
			if (!context.Response.HasStarted)
			{
				context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			}
		}
		finally
		{
			try
			{
				Directory.Delete(workingDirectory, true);
			}
			catch (Exception ex)
			{
				Logger.Warning(LogCategory.Export, $"Could not clean up export temp directory: {ex.Message}");
			}
		}
	}
}

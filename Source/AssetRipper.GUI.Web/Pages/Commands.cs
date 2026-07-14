using AssetRipper.Import.Logging;
using AssetRipper.NativeDialogs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.IO.Compression;

namespace AssetRipper.GUI.Web.Pages;

public static class Commands
{
	private const string RootPath = "/";
	private const string CommandsPath = "/Commands";

	/// <summary>
	/// For documentation purposes
	/// </summary>
	/// <param name="Path">The file system path.</param>
	internal record PathFormData(string Path);

	internal static RouteHandlerBuilder AcceptsFormDataContainingPath(this RouteHandlerBuilder builder)
	{
		return builder.Accepts<PathFormData>("application/x-www-form-urlencoded");
	}

	private static bool TryGetCreateSubfolder(IFormCollection form)
	{
		if (form.TryGetValue("CreateSubfolder", out StringValues values))
		{
			return values == "true";
		}

		return false;
	}

	public readonly struct LoadFile : ICommand
	{
		static async Task<string?> ICommand.Execute(HttpRequest request)
		{
			IFormCollection form = await request.ReadFormAsync();

			string[]? paths;
			if (form.TryGetValue("Path", out StringValues values))
			{
				paths = values;
			}
			else if (NativeDialog.Supported)
			{
				paths = await OpenFileDialog.OpenFiles();
			}
			else
			{
				return CommandsPath;
			}

			if (paths is { Length: > 0 })
			{
				GameFileLoader.LoadAndProcess(paths);
			}
			return null;
		}
	}

	public readonly struct LoadFolder : ICommand
	{
		static async Task<string?> ICommand.Execute(HttpRequest request)
		{
			IFormCollection form = await request.ReadFormAsync();

			string[]? paths;
			if (form.TryGetValue("Path", out StringValues values))
			{
				paths = values;
			}
			else if (NativeDialog.Supported)
			{
				paths = await OpenFolderDialog.OpenFolders();
			}
			else
			{
				return CommandsPath;
			}

			if (paths is { Length: > 0 })
			{
				GameFileLoader.LoadAndProcess(paths);
			}
			return null;
		}
	}

	/// <summary>
	/// Accepts a multipart/form-data upload of one or more game files (an APK, an
	/// executable, or a zipped data folder), streams them to a temporary directory on
	/// the server, extracts any zip archives, and then loads and processes them.
	/// This is what makes the host usable remotely: a browser on another machine can
	/// upload the game and have it decompiled server-side, without any native file dialog.
	/// </summary>
	public readonly struct Upload : ICommand
	{
		static async Task<string?> ICommand.Execute(HttpRequest request)
		{
			if (!request.HasFormContentType || !MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? mediaType))
			{
				return CommandsPath;
			}

			string? boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
			if (string.IsNullOrEmpty(boundary))
			{
				return CommandsPath;
			}

			string uploadDirectory = CreateFreshUploadDirectory();
			List<string> savedFiles = [];

			MultipartReader reader = new(boundary, request.Body);
			MultipartSection? section;
			while ((section = await reader.ReadNextSectionAsync()) is not null)
			{
				if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out ContentDispositionHeaderValue? disposition)
					|| !disposition.IsFileDisposition())
				{
					continue;
				}

				// Path.GetFileName strips any directory components a malicious client might send.
				string fileName = Path.GetFileName(disposition.FileNameStar.Value ?? disposition.FileName.Value ?? "");
				if (string.IsNullOrEmpty(fileName))
				{
					continue;
				}

				string destination = Path.Combine(uploadDirectory, fileName);
				await using (FileStream fileStream = File.Create(destination))
				{
					await section.Body.CopyToAsync(fileStream);
				}
				Logger.Info(LogCategory.Import, $"Received upload '{fileName}'.");
				savedFiles.Add(destination);
			}

			if (savedFiles.Count == 0)
			{
				return CommandsPath;
			}

			List<string> loadPaths = [];
			foreach (string file in savedFiles)
			{
				if (string.Equals(Path.GetExtension(file), ".zip", StringComparison.OrdinalIgnoreCase))
				{
					// A zipped folder: extract it and load the resulting directory.
					string extractedDirectory = Path.Combine(uploadDirectory, Path.GetFileNameWithoutExtension(file));
					Directory.CreateDirectory(extractedDirectory);
					ZipFile.ExtractToDirectory(file, extractedDirectory, overwriteFiles: true);
					loadPaths.Add(extractedDirectory);
				}
				else
				{
					loadPaths.Add(file);
				}
			}

			GameFileLoader.LoadAndProcess(loadPaths);
			return null;
		}

		private static string CreateFreshUploadDirectory()
		{
			string baseDirectory = Path.Combine(Path.GetTempPath(), "AssetRipperUploads");
			// Only one game is loaded at a time, so clear previous uploads to avoid filling the disk.
			if (Directory.Exists(baseDirectory))
			{
				try
				{
					Directory.Delete(baseDirectory, true);
				}
				catch (Exception ex)
				{
					Logger.Warning(LogCategory.Import, $"Could not clear previous uploads: {ex.Message}");
				}
			}
			string directory = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			return directory;
		}
	}

	public readonly struct ExportUnityProject : ICommand
	{
		static async Task<string?> ICommand.Execute(HttpRequest request)
		{
			IFormCollection form = await request.ReadFormAsync();

			string? path;
			if (form.TryGetValue("Path", out StringValues values))
			{
				path = values;
			}
			else
			{
				return CommandsPath;
			}

			if (!string.IsNullOrEmpty(path))
			{
				bool createSubfolder = TryGetCreateSubfolder(form);
				path = MaybeAppendTimestampedSubfolder(path, createSubfolder);
				await GameFileLoader.ExportUnityProject(path);
			}
			return null;
		}
	}

	public readonly struct ExportPrimaryContent : ICommand
	{
		static async Task<string?> ICommand.Execute(HttpRequest request)
		{
			IFormCollection form = await request.ReadFormAsync();

			string? path;
			if (form.TryGetValue("Path", out StringValues values))
			{
				path = values;
			}
			else
			{
				return CommandsPath;
			}

			if (!string.IsNullOrEmpty(path))
			{
				bool createSubfolder = TryGetCreateSubfolder(form);
				path = MaybeAppendTimestampedSubfolder(path, createSubfolder);
				await GameFileLoader.ExportPrimaryContent(path);
			}
			return null;
		}
	}

	private static string MaybeAppendTimestampedSubfolder(string path, bool append)
	{
		if (append)
		{
			string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
			string subfolder = $"AssetRipper_export_{timestamp}";
			return Path.Combine(path, subfolder);
		}

		return path;
	}

	public readonly struct Reset : ICommand
	{
		static Task<string?> ICommand.Execute(HttpRequest request)
		{
			GameFileLoader.Reset();
			return Task.FromResult<string?>(null);
		}
	}

	public static async Task HandleCommand<T>(HttpContext context) where T : ICommand
	{
		string? redirectionTarget = await T.Execute(context.Request);
		context.Response.Redirect(redirectionTarget ?? RootPath);
	}
}

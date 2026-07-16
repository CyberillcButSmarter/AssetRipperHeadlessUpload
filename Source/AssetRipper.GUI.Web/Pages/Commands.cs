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
			// Canonical root (with a trailing separator) used to verify every saved file
			// stays inside the upload directory.
			string uploadRoot = Path.GetFullPath(uploadDirectory) + Path.DirectorySeparatorChar;
			try
			{
				int savedCount = 0;

				MultipartReader reader = new(boundary, request.Body);
				MultipartSection? section;
				while ((section = await reader.ReadNextSectionAsync()) is not null)
				{
					if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out ContentDispositionHeaderValue? disposition)
						|| !disposition.IsFileDisposition())
					{
						continue;
					}

					// When a folder is uploaded (a "webkitdirectory" input) the browser puts
					// each file's path relative to the chosen folder in the filename, e.g.
					// "MyGame/Managed/x.dll". Preserve that structure - stripping it to the
					// bare name would flatten the folder and destroy the game layout. A plain
					// file upload just yields a single-segment name. SanitizeRelativePath keeps
					// the subfolders while removing any absolute/".." path-traversal attempt.
					string? relativePath = SanitizeRelativePath(disposition.FileNameStar.Value ?? disposition.FileName.Value);
					if (relativePath is null)
					{
						continue;
					}

					// Defense-in-depth: resolve the final path and require it to stay inside
					// the upload directory, so no crafted name can escape even if sanitizing missed something.
					string destination = Path.GetFullPath(Path.Combine(uploadDirectory, relativePath));
					if (!destination.StartsWith(uploadRoot, StringComparison.Ordinal))
					{
						Logger.Warning(LogCategory.Import, $"Rejected upload path escaping the upload directory: '{relativePath}'");
						continue;
					}

					// Stream to a .part file and rename on completion, so a client that
					// disconnects mid-transfer can never leave a partial file that looks
					// complete. If CopyToAsync throws, the catch below discards everything.
					Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
					string partialPath = destination + ".part";
					await using (FileStream fileStream = File.Create(partialPath))
					{
						await section.Body.CopyToAsync(fileStream);
					}
					File.Move(partialPath, destination, overwrite: true);
					savedCount++;
				}

				if (savedCount == 0)
				{
					// Nothing usable arrived - don't leave an empty temp dir behind.
					TryDeleteDirectory(uploadDirectory);
					return CommandsPath;
				}
				Logger.Info(LogCategory.Import, $"Received {savedCount} uploaded file(s).");

				// Auto-extract any top-level zips (a zipped folder), then hand AssetRipper the
				// top-level entries of the upload dir. That uniformly covers a single file,
				// several files, or an uploaded folder's root directory.
				foreach (string zip in Directory.GetFiles(uploadDirectory, "*.zip", SearchOption.TopDirectoryOnly))
				{
					string extractedDirectory = Path.Combine(uploadDirectory, Path.GetFileNameWithoutExtension(zip));
					Directory.CreateDirectory(extractedDirectory);
					ZipFile.ExtractToDirectory(zip, extractedDirectory, overwriteFiles: true);
					File.Delete(zip);
				}

				List<string> loadPaths = [.. Directory.GetFileSystemEntries(uploadDirectory, "*", SearchOption.TopDirectoryOnly)];
				GameFileLoader.LoadAndProcess(loadPaths);
				return null;
			}
			catch (Exception ex)
			{
				// An interrupted upload or a corrupt/truncated zip must not leave junk
				// on disk or a half-loaded state. Discard this upload's temp files.
				Logger.Error(LogCategory.Import, $"Upload failed, discarding: {ex.Message}");
				TryDeleteDirectory(uploadDirectory);
				throw;
			}
		}

		/// <summary>
		/// Turns a browser-supplied (possibly nested) upload filename into a safe path
		/// relative to the upload directory: subfolders are preserved, but any rooted
		/// path, drive prefix, "." / ".." segment or invalid character is stripped, so a
		/// malicious client cannot write outside the upload directory. Returns null if
		/// nothing usable remains.
		/// </summary>
		private static string? SanitizeRelativePath(string? raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}

			char[] invalid = Path.GetInvalidFileNameChars();
			List<string> parts = [];
			foreach (string segment in raw.Replace('\\', '/').Split('/'))
			{
				if (segment.Length == 0 || segment == "." || segment == "..")
				{
					continue;
				}
				string cleaned = segment;
				foreach (char c in invalid)
				{
					cleaned = cleaned.Replace(c.ToString(), "");
				}
				// Re-check AFTER stripping: removing invalid chars (e.g. NUL, or ':' on
				// Windows) could turn a benign-looking segment into "." or ".." and
				// reintroduce traversal.
				if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
				{
					continue;
				}
				parts.Add(cleaned);
			}

			return parts.Count == 0 ? null : Path.Combine([.. parts]);
		}

		private static void TryDeleteDirectory(string directory)
		{
			try
			{
				if (Directory.Exists(directory))
				{
					Directory.Delete(directory, true);
				}
			}
			catch (Exception ex)
			{
				Logger.Warning(LogCategory.Import, $"Could not clean up upload directory: {ex.Message}");
			}
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

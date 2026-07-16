using AssetRipper.Import.Logging;

namespace AssetRipper.GUI.Web;

internal enum ExportKind
{
	UnityProject,
	PrimaryContent,
}

internal enum ExportRunState
{
	Idle,
	Running,
	Completed,
	Failed,
}

internal enum BeginOutcome
{
	Started,
	ServedFromCache,
	Busy,
}

/// <summary>
/// Coordinates one background export at a time. It captures the export's log output
/// (via <see cref="CaptureLogger"/>) so the browser can show it live, and caches the
/// resulting zip per <see cref="ExportKind"/> so re-exporting the same loaded game is
/// skipped. <see cref="InvalidateForNewGame"/> clears the cache when a different game
/// is loaded or reset.
/// </summary>
internal static class ExportManager
{
	private static readonly object Sync = new();
	private static readonly List<string> LogLines = [];
	private static readonly Dictionary<ExportKind, string> CachedZips = [];
	private const int MaxLines = 5000;

	private static string? resultZip;

	public static ExportRunState State { get; private set; } = ExportRunState.Idle;

	public static ILogger CaptureLogger { get; } = new ProgressLogger();

	/// <summary>
	/// Decides what an export request should do for the current game: start a fresh
	/// export, serve a previously-cached result, or refuse because one is in flight.
	/// </summary>
	public static BeginOutcome Begin(ExportKind kind)
	{
		lock (Sync)
		{
			if (State == ExportRunState.Running)
			{
				return BeginOutcome.Busy;
			}

			if (CachedZips.TryGetValue(kind, out string? cached) && File.Exists(cached))
			{
				LogLines.Clear();
				LogLines.Add($"This game was already exported as {kind}. Returning the previous result - no re-export needed.");
				resultZip = cached;
				State = ExportRunState.Completed;
				return BeginOutcome.ServedFromCache;
			}

			LogLines.Clear();
			LogLines.Add($"Starting {kind} export...");
			resultZip = null;
			State = ExportRunState.Running;
			return BeginOutcome.Started;
		}
	}

	public static void Append(string line)
	{
		lock (Sync)
		{
			LogLines.Add(line);
			if (LogLines.Count > MaxLines)
			{
				LogLines.RemoveRange(0, LogLines.Count - MaxLines);
			}
		}
	}

	public static void Complete(ExportKind kind, string zipPath)
	{
		lock (Sync)
		{
			CachedZips[kind] = zipPath;
			resultZip = zipPath;
			State = ExportRunState.Completed;
		}
	}

	public static void Fail(string message)
	{
		lock (Sync)
		{
			LogLines.Add(message);
			State = ExportRunState.Failed;
		}
	}

	public static (ExportRunState State, string Log, bool DownloadReady) Snapshot()
	{
		lock (Sync)
		{
			bool ready = State == ExportRunState.Completed && resultZip is not null && File.Exists(resultZip);
			return (State, string.Join('\n', LogLines), ready);
		}
	}

	public static string? CompletedZip()
	{
		lock (Sync)
		{
			return State == ExportRunState.Completed && resultZip is not null && File.Exists(resultZip) ? resultZip : null;
		}
	}

	/// <summary>
	/// Called when a new game is loaded or reset: the cached exports no longer apply.
	/// </summary>
	public static void InvalidateForNewGame()
	{
		lock (Sync)
		{
			foreach (string zip in CachedZips.Values)
			{
				TryDeleteExport(zip);
			}
			CachedZips.Clear();
			LogLines.Clear();
			resultZip = null;
			if (State != ExportRunState.Running)
			{
				State = ExportRunState.Idle;
			}
		}
	}

	private static void TryDeleteExport(string zipPath)
	{
		try
		{
			string? dir = Path.GetDirectoryName(zipPath);
			// Each export lives in its own temp dir under AssetRipperExports; remove the whole dir.
			if (dir is not null && dir.Contains("AssetRipperExports") && Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
			else if (File.Exists(zipPath))
			{
				File.Delete(zipPath);
			}
		}
		catch (Exception ex)
		{
			Logger.Warning(LogCategory.Export, $"Could not clean up cached export: {ex.Message}");
		}
	}

	/// <summary>
	/// An <see cref="ILogger"/> registered globally that mirrors log output into the
	/// export buffer while an export is running, so it can be shown live in the browser.
	/// </summary>
	private sealed class ProgressLogger : ILogger
	{
		public void Log(LogType type, LogCategory category, string message)
		{
			if (State == ExportRunState.Running)
			{
				Append(type == LogType.Info ? message : $"[{type}] {message}");
			}
		}

		public void BlankLine(int numLines)
		{
		}
	}
}

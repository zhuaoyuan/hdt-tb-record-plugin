using System;
using System.IO;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Newtonsoft.Json;

namespace HdtTbRecordPlugin
{
	public static class JsonWriter
	{
		private static readonly string RuntimeDebugLogPath = @"c:\projects\github\react-flask-hello\.cursor\debug.log";

		public static void WriteTurns(string outputDirectory, Models.GameRecord game, System.Collections.Generic.List<Models.TurnItem> turns)
		{
			try
			{
				// 每局一个文件，按开始时间与 game id 命名。
				Directory.CreateDirectory(outputDirectory);
				var safeStart = game.StartTimeUtc.ToString("yyyyMMdd_HHmmss");
				var fileName = $"{safeStart}_{game.GameId}.json";
				var path = Path.Combine(outputDirectory, fileName);
				var lastTurn = turns.Count > 0 ? turns[turns.Count - 1] : null;
				// #region agent log
				RuntimeDebugLog("H12", "JsonWriter.cs:WriteTurns", "write_turns_summary",
					new
					{
						path,
						turnsCount = turns.Count,
						lastTurnNumber = lastTurn?.TurnNumber ?? 0,
						lastPlayerBoardCount = lastTurn?.PlayerBoard?.Count ?? 0,
						lastOpponentBoardCount = lastTurn?.OpponentBoard?.Count ?? 0,
						lastPlayerBoardSource = lastTurn?.PlayerBoardSource,
						lastOpponentBoardSource = lastTurn?.OpponentBoardSource,
						lastPlayerEndHealth = lastTurn?.PlayerEndHealth ?? 0,
						lastPlayerEndArmor = lastTurn?.PlayerEndArmor ?? 0,
						lastOpponentEndHealth = lastTurn?.OpponentEndHealth ?? 0,
						lastOpponentEndArmor = lastTurn?.OpponentEndArmor ?? 0
					});
				// #endregion
				var json = JsonConvert.SerializeObject(turns, Formatting.Indented);
				File.WriteAllText(path, json);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		private static void RuntimeDebugLog(string hypothesisId, string location, string message, object data)
		{
			try
			{
				var payload = new
				{
					id = $"log_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}",
					timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
					runId = "pre-fix",
					hypothesisId,
					location,
					message,
					data
				};
				var json = JsonConvert.SerializeObject(payload);
				File.AppendAllText(RuntimeDebugLogPath, json + Environment.NewLine);
			}
			catch
			{
				// ignore debug log failures
			}
		}
	}
}

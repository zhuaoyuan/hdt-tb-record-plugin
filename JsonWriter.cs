using System;
using System.IO;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Newtonsoft.Json;

namespace HdtTbRecordPlugin
{
	public static class JsonWriter
	{
		public static void WriteTurns(string outputDirectory, Models.GameRecord game, System.Collections.Generic.List<Models.TurnItem> turns)
		{
			try
			{
				// 每局一个文件，按开始时间与 game id 命名。
				Directory.CreateDirectory(outputDirectory);
				var safeStart = game.StartTimeUtc.ToString("yyyyMMdd_HHmmss");
				var fileName = $"{safeStart}_{game.GameId}.json";
				var path = Path.Combine(outputDirectory, fileName);
				var json = JsonConvert.SerializeObject(turns, Formatting.Indented);
				File.WriteAllText(path, json);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}
	}
}

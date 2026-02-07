using System;
using System.Collections.Generic;

namespace HdtTbRecordPlugin.Models
{
	public class GameRecord
	{
		// 每局战棋一条记录，结束时序列化为 JSON。
		public string GameId { get; set; } = Guid.NewGuid().ToString();
		public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
		public DateTime? EndTimeUtc { get; set; }
		public string? GameType { get; set; }
		public int? LocalPlayerId { get; set; }
		// 每回合战斗快照。
		public List<RoundSnapshot> Rounds { get; } = new List<RoundSnapshot>();
		// 商店事件（尽力推断）。
		public List<ShopEvent> ShopEvents { get; } = new List<ShopEvent>();
		// 原始 block（用于离线解析）。
		public List<RawBlockEvent> RawBlocks { get; } = new List<RawBlockEvent>();
		public string? Result { get; set; }
	}
}

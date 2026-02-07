using System;

namespace HdtTbRecordPlugin.Models
{
	public class ShopEvent
	{
		// 由商店阶段日志推断的事件（尽力而为）。
		public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
		public int TurnNumber { get; set; }
		public string? Type { get; set; }
		public string? CardId { get; set; }
		public string? CardName { get; set; }
		public int? TechLevel { get; set; }
	}
}

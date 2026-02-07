using System;

namespace HdtTbRecordPlugin.Models
{
	public class RawBlockEvent
	{
		// 保存原始 BLOCK_START 数据以便离线分析。
		public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
		public string? BlockType { get; set; }
		public string? CardId { get; set; }
		public int SourceEntityId { get; set; }
		public int PlayerId { get; set; }
		public string? TargetCardId { get; set; }
		public string? TriggerKeyword { get; set; }
		public string? Raw { get; set; }
	}
}

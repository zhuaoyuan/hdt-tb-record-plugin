using System.Collections.Generic;
using HearthDb.Enums;

namespace HdtTbRecordPlugin.Models
{
	public class EntityState
	{
		// 从 Power.log 还原的最小实体内存模型。
		public EntityState(int id)
		{
			Id = id;
		}

		public int Id { get; }
		// 实体最后已知的 CardId（日志早期可能为空）。
		public string? CardId { get; set; }
		// Controller 是玩家 id。
		public int Controller { get; set; }
		// Zone 与位置用于阵容快照与排序。
		public Zone Zone { get; set; } = Zone.INVALID;
		public int ZonePosition { get; set; }
		// CardType 用于识别英雄/随从。
		public CardType CardType { get; set; } = CardType.INVALID;
		// PlayerId 用于定位战棋玩家实体。
		public int PlayerId { get; set; }
		// 完整 Tag 集合，用于快照与派生状态。
		public Dictionary<GameTag, int> Tags { get; } = new Dictionary<GameTag, int>();
	}
}

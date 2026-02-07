using System.Collections.Generic;

namespace HdtTbRecordPlugin.Models
{
	public class HeroSnapshot
	{
		// 英雄在战斗开始/结束时的快照。
		public string? CardId { get; set; }
		public string? Name { get; set; }
		public int Health { get; set; }
		public int Armor { get; set; }
		public int TechLevel { get; set; }
		public bool IsDead { get; set; }
		public Dictionary<string, int>? Tags { get; set; }
	}
}

using System.Collections.Generic;

namespace HdtTbRecordPlugin.Models
{
	public class MinionSnapshot
	{
		// 场上随从快照。
		public string? CardId { get; set; }
		public string? Name { get; set; }
		public int ZonePosition { get; set; }
		public int Attack { get; set; }
		public int Health { get; set; }
		public int MaxHealth { get; set; }
		public int Damage { get; set; }
		public List<string> Statuses { get; set; } = new List<string>();
		public Dictionary<string, int>? Tags { get; set; }
	}
}

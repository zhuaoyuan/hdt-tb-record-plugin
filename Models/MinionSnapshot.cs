using System.Collections.Generic;
using Newtonsoft.Json;

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
		public int Premium { get; set; }
		public int TechLevel { get; set; }
		public List<string> Statuses { get; set; } = new List<string>();
		[JsonIgnore]
		public Dictionary<string, int>? Tags { get; set; }
	}
}

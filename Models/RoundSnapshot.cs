using System;
using System.Collections.Generic;

namespace HdtTbRecordPlugin.Models
{
	public class RoundSnapshot
	{
		// 指定回合的战斗开始快照。
		public int TurnNumber { get; set; }
		public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
		public int? OpponentPlayerId { get; set; }
		public HeroSnapshot? PlayerHero { get; set; }
		public HeroSnapshot? OpponentHero { get; set; }
		public List<MinionSnapshot> PlayerBoard { get; set; } = new List<MinionSnapshot>();
		public List<MinionSnapshot> OpponentBoard { get; set; } = new List<MinionSnapshot>();
		public CombatResult? Combat { get; set; }
	}

	public class CombatResult
	{
		// 由战斗前后英雄血量差推导的结果。
		public string? Outcome { get; set; }
		public int DamageToOpponent { get; set; }
		public int DamageToPlayer { get; set; }
	}
}

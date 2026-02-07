using System.Collections.Generic;

namespace HdtTbRecordPlugin.Models
{
	public class TurnItem
	{
		public int TurnNumber { get; set; }
		public HeroInfo? PlayerHero { get; set; }
		public HeroInfo? OpponentHero { get; set; }
		public int PlayerStartHealth { get; set; }
		public int PlayerStartArmor { get; set; }
		public int OpponentStartHealth { get; set; }
		public int OpponentStartArmor { get; set; }
		public int PlayerEndHealth { get; set; }
		public int PlayerEndArmor { get; set; }
		public int OpponentEndHealth { get; set; }
		public int OpponentEndArmor { get; set; }
		public int PlayerTechLevel { get; set; }
		public int OpponentTechLevel { get; set; }
		public List<MinionSnapshot> PlayerBoard { get; set; } = new List<MinionSnapshot>();
		public List<MinionSnapshot> OpponentBoard { get; set; } = new List<MinionSnapshot>();
		public string? PlayerBoardSource { get; set; }
		public string? OpponentBoardSource { get; set; }
		public string? Outcome { get; set; }
	}

	public class HeroInfo
	{
		public string? CardId { get; set; }
		public string? Name { get; set; }
	}
}

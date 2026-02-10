using System;
using System.Linq;
using System.Reflection;

namespace DumpGameTags
{
	internal static class Program
	{
		private static int Main(string[] args)
		{
			if(args.Length == 0)
			{
				Console.Error.WriteLine("Usage: DumpGameTags <path-to-HearthDb.dll>");
				return 2;
			}
			var path = args[0];
			var asm = Assembly.LoadFrom(path);
			var gameTag = asm.GetType("HearthDb.Enums.GameTag");
			var cardType = asm.GetType("HearthDb.Enums.CardType");
			var zone = asm.GetType("HearthDb.Enums.Zone");
			var race = asm.GetType("HearthDb.Enums.Race");
			if(gameTag == null)
			{
				Console.Error.WriteLine("GameTag not found");
				return 1;
			}

			DumpEnum(gameTag, "GameTag");
			if(cardType != null) DumpEnum(cardType, "CardType");
			if(zone != null) DumpEnum(zone, "Zone");
			if(race != null) DumpEnum(race, "Race");
			return 0;
		}

		private static void DumpEnum(Type enumType, string label)
		{
			Console.WriteLine($"[{label}]");
			var values = Enum.GetValues(enumType).Cast<object>()
				.Select(v => new { Name = Enum.GetName(enumType, v), Value = Convert.ToInt32(v) })
				.OrderBy(v => v.Value)
				.ToList();
			foreach(var v in values)
				Console.WriteLine($"{v.Value}\t{v.Name}");
			Console.WriteLine();
		}
	}
}

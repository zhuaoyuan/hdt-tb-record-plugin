using System;
using System.IO;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;
using HdtEntity = Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity;
using HdtTbRecordPlugin.Models;
using Newtonsoft.Json;

namespace HdtTbRecordPlugin
{
	public partial class PowerLogParser
	{
		private void InitDiagnostics()
		{
			try
			{
				if(!(Core.Game?.IsBattlegroundsMatch ?? false))
					return;
				_matchStartUtc = DateTime.UtcNow;
				_matchId = BuildMatchId();
				_diagnosticsDirectory = Path.Combine(_config.OutputDirectory, "diagnostics", _matchId);
				_rawEventsPath = Path.Combine(_diagnosticsDirectory, "raw_events.jsonl");
				_snapshotsPath = Path.Combine(_diagnosticsDirectory, "snapshots.jsonl");
				_expectationsPath = Path.Combine(_diagnosticsDirectory, "expectations.json");
				_coreInputPath = Path.Combine(_diagnosticsDirectory, "core_input.jsonl");
				EnsureDiagnosticsDirectory();
				RecordRawEvent("MATCH_START", null, new
				{
					gameType = _currentGame?.GameType,
					localPlayerId = _localPlayerId
				});
			}
			catch
			{
			}
		}

		private void RecordRawEvent(string kind, string? line, object? data)
		{
			if(!(Core.Game?.IsBattlegroundsMatch ?? false))
				return;
			if(string.IsNullOrEmpty(_rawEventsPath) || !EnsureDiagnosticsDirectory())
				return;
			var payload = new
			{
				eventType = "raw_event",
				kind,
				timestampUtc = DateTime.UtcNow.ToString("O"),
				matchId = _matchId,
				turnNumber = _turnNumber,
				combatActive = _combatActive,
				localPlayerId = _localPlayerId,
				currentOpponentPlayerId = _currentOpponentPlayerId,
				nextOpponentPlayerId = _nextOpponentPlayerId,
				gameEntityId = _gameEntityId,
				line,
				data
			};
			WriteJsonLine(_rawEventsPath, payload);
		}

		private void RecordSnapshot(string snapshotType, TurnItem? round, object? data)
		{
			if(!(Core.Game?.IsBattlegroundsMatch ?? false))
				return;
			if(string.IsNullOrEmpty(_snapshotsPath) || !EnsureDiagnosticsDirectory())
				return;
			var payload = new
			{
				eventType = "snapshot",
				snapshotType,
				timestampUtc = DateTime.UtcNow.ToString("O"),
				matchId = _matchId,
				turnNumber = round?.TurnNumber ?? _turnNumber,
				combatActive = _combatActive,
				localPlayerId = _localPlayerId,
				currentOpponentPlayerId = _currentOpponentPlayerId,
				data,
				snapshot = round != null ? BuildTurnSnapshot(round) : null,
				inputs = new
				{
					core = BuildCoreInputSnapshot(),
					watcher = BuildWatcherInputSnapshot()
				}
			};
			WriteJsonLine(_snapshotsPath, payload);
		}

		private void RecordCoreInput(string reason)
		{
			if(!(Core.Game?.IsBattlegroundsMatch ?? false))
				return;
			if(string.IsNullOrEmpty(_coreInputPath) || !EnsureDiagnosticsDirectory())
				return;
			var payload = new
			{
				eventType = "core_input",
				reason,
				timestampUtc = DateTime.UtcNow.ToString("O"),
				matchId = _matchId,
				turnNumber = _turnNumber,
				combatActive = _combatActive,
				localPlayerId = _localPlayerId,
				currentOpponentPlayerId = _currentOpponentPlayerId,
				core = BuildCoreInputSnapshot()
			};
			WriteJsonLine(_coreInputPath, payload);
		}

		private object BuildCoreInputSnapshot()
		{
			var game = Core.Game;
			var player = game?.Player;
			var opponent = game?.Opponent;
			var playerEntity = game?.PlayerEntity;
			var opponentEntity = game?.OpponentEntity;
			var playerHero = player?.Hero;
			var opponentHero = opponent?.Hero;
			return new
			{
				gameType = game?.CurrentGameType.ToString(),
				playerId = player?.Id ?? 0,
				opponentId = opponent?.Id ?? 0,
				playerEntityId = playerEntity?.Id ?? 0,
				opponentEntityId = opponentEntity?.Id ?? 0,
				playerHeroEntityId = playerEntity?.GetTag(GameTag.HERO_ENTITY) ?? 0,
				opponentHeroEntityId = opponentEntity?.GetTag(GameTag.HERO_ENTITY) ?? 0,
				playerHero = BuildCoreHeroSnapshot(playerHero),
				opponentHero = BuildCoreHeroSnapshot(opponentHero),
				playerEntity = BuildCorePlayerEntitySnapshot(playerEntity),
				opponentEntity = BuildCorePlayerEntitySnapshot(opponentEntity),
				playerBoard = BuildCoreBoardSnapshot(player?.Board),
				opponentBoard = BuildCoreBoardSnapshot(opponent?.Board)
			};
		}

		private object? BuildCoreHeroSnapshot(HdtEntity? hero)
		{
			if(hero == null)
				return null;
			return new
			{
				entityId = hero.Id,
				cardId = hero.CardId,
				healthTag = GetTagValue(hero, GameTag.HEALTH),
				armorTag = GetTagValue(hero, GameTag.ARMOR),
				damageTag = GetTagValue(hero, GameTag.DAMAGE)
			};
		}

		private object? BuildCorePlayerEntitySnapshot(HdtEntity? entity)
		{
			if(entity == null)
				return null;
			return new
			{
				entityId = entity.Id,
				cardId = entity.CardId,
				healthTag = GetTagValue(entity, GameTag.HEALTH),
				armorTag = GetTagValue(entity, GameTag.ARMOR),
				damageTag = GetTagValue(entity, GameTag.DAMAGE),
				techLevelTag = GetTagValue(entity, GameTag.PLAYER_TECH_LEVEL)
			};
		}

		private object? BuildCoreBoardSnapshot(System.Collections.Generic.IEnumerable<HdtEntity>? board)
		{
			if(board == null)
				return null;
			return board.Select(entity => new
			{
				entityId = entity.Id,
				cardId = entity.CardId,
				atkTag = GetTagValue(entity, GameTag.ATK),
				healthTag = GetTagValue(entity, GameTag.HEALTH),
				damageTag = GetTagValue(entity, GameTag.DAMAGE),
				zoneTag = GetTagValue(entity, GameTag.ZONE),
				zonePositionTag = GetTagValue(entity, GameTag.ZONE_POSITION)
			}).ToList();
		}

		private object BuildWatcherInputSnapshot()
		{
			var info = GetWatcherInfo();
			var ids = new int[0];
			lock(_watcherLock)
			{
				ids = _watcherOpponentEntityIds.ToArray();
			}
			return new
			{
				idsCount = ids.Length,
				firstIds = ids.Take(8).ToArray(),
				ageMs = info.ageMs
			};
		}

		private object BuildTurnSnapshot(TurnItem round)
		{
			return new
			{
				turnNumber = round.TurnNumber,
				playerHero = round.PlayerHero,
				opponentHero = round.OpponentHero,
				playerStartHealth = round.PlayerStartHealth,
				playerStartArmor = round.PlayerStartArmor,
				opponentStartHealth = round.OpponentStartHealth,
				opponentStartArmor = round.OpponentStartArmor,
				playerEndHealth = round.PlayerEndHealth,
				playerEndArmor = round.PlayerEndArmor,
				opponentEndHealth = round.OpponentEndHealth,
				opponentEndArmor = round.OpponentEndArmor,
				playerTechLevel = round.PlayerTechLevel,
				opponentTechLevel = round.OpponentTechLevel,
				playerBoard = round.PlayerBoard,
				opponentBoard = round.OpponentBoard,
				playerBoardSource = round.PlayerBoardSource,
				opponentBoardSource = round.OpponentBoardSource,
				outcome = round.Outcome
			};
		}

		private void WriteExpectations()
		{
			if(!(Core.Game?.IsBattlegroundsMatch ?? false))
				return;
			if(string.IsNullOrEmpty(_expectationsPath) || !EnsureDiagnosticsDirectory())
				return;
			var expectations = new
			{
				matchId = _matchId,
				startedAtUtc = _matchStartUtc.ToString("O"),
				endedAtUtc = DateTime.UtcNow.ToString("O"),
				gameType = _currentGame?.GameType,
				localPlayerId = _localPlayerId,
				turns = _turnItems.Select(turn => new
				{
					turnNumber = turn.TurnNumber,
					expected = turn,
					observed = turn,
					notes = ""
				}).ToList()
			};
			WriteJsonFile(_expectationsPath, expectations);
		}

		private bool EnsureDiagnosticsDirectory()
		{
			if(string.IsNullOrEmpty(_diagnosticsDirectory))
				return false;
			try
			{
				Directory.CreateDirectory(_diagnosticsDirectory);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private void WriteJsonLine(string path, object payload)
		{
			try
			{
				var json = JsonConvert.SerializeObject(payload);
				File.AppendAllText(path, json + Environment.NewLine);
			}
			catch
			{
			}
		}

		private void WriteJsonFile(string path, object payload)
		{
			try
			{
				var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
				File.WriteAllText(path, json);
			}
			catch
			{
			}
		}

		private string BuildMatchId()
		{
			var timePart = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
			return SanitizePathComponent($"{timePart}_{Guid.NewGuid():N}");
		}

		private string SanitizePathComponent(string value)
		{
			var invalid = Path.GetInvalidFileNameChars();
			foreach(var ch in invalid)
				value = value.Replace(ch.ToString(), "_");
			return value;
		}
	}
}

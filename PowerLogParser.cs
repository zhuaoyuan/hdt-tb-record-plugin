using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;
using HdtEntity = Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity;
using Hearthstone_Deck_Tracker.LogReader;
using HdtTbRecordPlugin.Models;
using HearthWatcher.EventArgs;
using Newtonsoft.Json;

namespace HdtTbRecordPlugin
{
	public class PowerLogParser
	{
		// HDT 用于判断战棋战斗阶段切换的数值 Tag。
		private const GameTag BaconCombatSetupTag = (GameTag)3533;
		private const GameTag BaconSetupTag = (GameTag)2022;

		// 从 Power.log 还原的实体状态表。
		private readonly Dictionary<int, EntityState> _entities = new Dictionary<int, EntityState>();
		// 会被转为随从状态标记的 Tag 列表。
		private readonly List<GameTag> _statusTags = new List<GameTag>
		{
			GameTag.TAUNT,
			GameTag.DIVINE_SHIELD,
			GameTag.WINDFURY,
			GameTag.MEGA_WINDFURY,
			GameTag.STEALTH,
			GameTag.REBORN,
			GameTag.POISONOUS,
			GameTag.FROZEN,
			GameTag.SILENCED
		};
		private readonly HashSet<GameTag> _snapshotRefreshTags = new HashSet<GameTag>
		{
			GameTag.ZONE,
			GameTag.ZONE_POSITION,
			GameTag.CONTROLLER,
			GameTag.ATK,
			GameTag.HEALTH,
			GameTag.DAMAGE,
			GameTag.ARMOR
		};

		private PluginConfig _config = new PluginConfig();
		// 当前正在构建的对局记录。
		private GameRecord? _currentGame;
		private int _gameEntityId;
		private int _turnTag;
		private int _turnNumber;
		private bool _combatActive;
		private readonly List<TurnItem> _turnItems = new List<TurnItem>();
		private TurnItem? _currentTurnItem;
		private int _currentOpponentPlayerId;
		private int _nextOpponentPlayerId;
		private int _localPlayerId;
		private bool _gameWritten;
		private bool _needsCombatRefresh;
		private int _pendingTagEntityId;
		private int _combatStartPlayerDamageTag;
		private int _combatStartOpponentDamageTag;
		private readonly Dictionary<int, int> _heroEntityDamageTag = new Dictionary<int, int>();
		private readonly object _watcherLock = new object();
		private List<int> _watcherOpponentEntityIds = new List<int>();
		private DateTime _watcherOpponentUpdatedUtc;
		private readonly Dictionary<int, int> _playerIdToHeroEntityId = new Dictionary<int, int>();
		private readonly Dictionary<int, int> _playerEntityIdToHeroEntityId = new Dictionary<int, int>();
		private static readonly string RuntimeDebugLogPath = @"c:\projects\github\react-flask-hello\.cursor\debug.log";

		public void Configure(PluginConfig config)
		{
			// 更新解析器配置但不重置状态。
			_config = config;
		}

		public void UpdateOpponentBoardFromWatcher(OpponentBoardArgs args)
		{
			if(args == null)
				return;
			var ids = new List<int>();
			var boardCardCount = 0;
			var boardCards = GetBoardCardsEnumerable(args);
			if(boardCards != null)
			{
				foreach(var card in boardCards)
				{
					if(card == null)
						continue;
					boardCardCount++;
					if(TryGetEntityId(card, out var entityId) && entityId > 0)
						ids.Add(entityId);
				}
			}

			ids = ids.Distinct().ToList();

			lock(_watcherLock)
			{
				_watcherOpponentEntityIds = ids;
				_watcherOpponentUpdatedUtc = DateTime.UtcNow;
			}

			DebugLog($"watcher_opponent_board cards={boardCardCount} ids={string.Join(",", ids.Take(8))} " +
				$"updated={_watcherOpponentUpdatedUtc:O}");

			// #region agent log
			RuntimeDebugLog("H3", "PowerLogParser.cs:UpdateOpponentBoardFromWatcher", "watcher_board_update",
				new
				{
					boardCardCount,
					idsCount = ids.Count,
					firstIds = ids.Take(6).ToArray(),
					combatActive = _combatActive,
					updatedUtc = _watcherOpponentUpdatedUtc.ToString("O")
				});
			// #endregion

			if(_combatActive)
				RefreshCombatSnapshotIfNeeded("watcher", null, null);
		}

		private static System.Collections.IEnumerable? GetBoardCardsEnumerable(object args)
		{
			var boardCardsProperty = args.GetType().GetProperty("BoardCards");
			if(boardCardsProperty == null)
				return null;
			return boardCardsProperty.GetValue(args) as System.Collections.IEnumerable;
		}

		private static bool TryGetEntityId(object card, out int entityId)
		{
			entityId = 0;
			var entityIdProperty = card.GetType().GetProperty("EntityId");
			if(entityIdProperty == null)
				return false;
			var value = entityIdProperty.GetValue(card);
			if(value is int id)
			{
				entityId = id;
				return true;
			}
			return false;
		}

		public void ProcessLine(string line)
		{
			if(string.IsNullOrWhiteSpace(line))
				return;

			if(HandleEntityTagLine(line))
				return;

			// Power.log 中出现 CREATE_GAME 表示新对局开始。
			if(line.Contains("CREATE_GAME"))
			{
				StartNewGame();
				_pendingTagEntityId = 0;
				return;
			}

			// 看到基础实体时开始跟踪。
			if(_currentGame == null && (LogConstants.PowerTaskList.GameEntityRegex.IsMatch(line) || LogConstants.PowerTaskList.PlayerEntityRegex.IsMatch(line)))
				StartNewGame();

			UpdateLocalPlayerId();

			HandleGameEntity(line);
			HandlePlayerEntity(line);
			var pendingSet = HandleFullEntity(line);
			pendingSet = HandleUpdatingEntity(line) || pendingSet;
			if(!pendingSet)
				_pendingTagEntityId = 0;
			HandleTagChange(line);
			HandleBlockStart(line);
		}

		public void Flush()
		{
			// 插件卸载时尽量落盘未完成数据。
			if(_currentGame != null && _turnItems.Count > 0)
				WriteAndReset();
		}

		private void StartNewGame()
		{
			// 重置前先写出上一局（若存在）。
			if(_currentGame != null && _turnItems.Count > 0)
				WriteAndReset();
			ResetState();
			_gameWritten = false;
			_currentGame = new GameRecord
			{
				GameType = Core.Game?.CurrentGameType.ToString(),
				LocalPlayerId = _localPlayerId
			};
		}

		private void ResetState()
		{
			// 清空上一局的所有派生状态。
			_entities.Clear();
			_gameEntityId = 0;
			_turnTag = 0;
			_turnNumber = 0;
			_combatActive = false;
			_turnItems.Clear();
			_currentTurnItem = null;
			_currentOpponentPlayerId = 0;
			_nextOpponentPlayerId = 0;
			_needsCombatRefresh = false;
			_pendingTagEntityId = 0;
			_playerIdToHeroEntityId.Clear();
			_playerEntityIdToHeroEntityId.Clear();
		}

		private void UpdateLocalPlayerId()
		{
			// 使用 HDT 当前玩家 id（若可用），否则尝试推断。
			var localId = ResolveLocalPlayerId();
			if(localId > 0)
				_localPlayerId = localId;
		}

		private void HandleGameEntity(string line)
		{
			// 记录 GameEntity id，用于 TURN/STATE 等 Tag。
			var match = LogConstants.PowerTaskList.GameEntityRegex.Match(line);
			if(!match.Success)
				return;
			if(int.TryParse(match.Groups["id"].Value, out var id))
			{
				_gameEntityId = id;
				GetOrCreateEntity(id);
			}
		}

		private void HandlePlayerEntity(string line)
		{
			// 记录玩家实体以及 PlayerId 映射。
			var match = LogConstants.PowerTaskList.PlayerEntityRegex.Match(line);
			if(!match.Success)
				return;
			if(int.TryParse(match.Groups["id"].Value, out var id))
			{
				var entity = GetOrCreateEntity(id);
				if(int.TryParse(match.Groups["playerId"].Value, out var playerId))
				{
					entity.PlayerId = playerId;
					entity.Tags[GameTag.PLAYER_ID] = playerId;
				}
			}
		}

		private bool HandleFullEntity(string line)
		{
			// FULL_ENTITY 提供初始 zone 与 cardId。
			var match = LogConstants.PowerTaskList.CreationRegex.Match(line);
			if(!match.Success)
				return false;
			if(!int.TryParse(match.Groups["id"].Value, out var id))
				return false;
			var zone = GameTagHelper.ParseEnum<Zone>(match.Groups["zone"].Value);
			var cardId = match.Groups["cardId"].Value;
			var entity = GetOrCreateEntity(id);
			entity.Zone = zone;
			entity.CardId = string.IsNullOrEmpty(cardId) ? entity.CardId : cardId;
			if(entity.CardType == CardType.INVALID && !string.IsNullOrEmpty(entity.CardId))
			{
				var card = Database.GetCardFromId(entity.CardId);
				if(card?.TypeEnum != null)
					entity.CardType = card.TypeEnum.Value;
			}
			_pendingTagEntityId = id;
			return true;
		}

		private bool HandleUpdatingEntity(string line)
		{
			// SHOW_ENTITY / CHANGE_ENTITY 可能揭示 cardId。
			var match = LogConstants.PowerTaskList.UpdatingEntityRegex.Match(line);
			if(!match.Success)
				return false;
			var cardId = match.Groups["cardId"].Value;
			var rawEntity = match.Groups["entity"].Value;
			var entityId = TryGetEntityId(rawEntity);
			if(entityId <= 0)
				return false;
			var entity = GetOrCreateEntity(entityId);
			if(!string.IsNullOrEmpty(cardId))
				entity.CardId = cardId;
			_pendingTagEntityId = entityId;
			return true;
		}

		private bool HandleEntityTagLine(string line)
		{
			var trimmed = line.TrimStart();
			if(!trimmed.StartsWith("tag=", StringComparison.Ordinal))
				return false;
			if(_pendingTagEntityId <= 0)
				return true;
			var valueIndex = trimmed.IndexOf(" value=", StringComparison.Ordinal);
			if(valueIndex <= 4)
				return true;

			var tagText = trimmed.Substring(4, valueIndex - 4).Trim();
			var valueText = trimmed.Substring(valueIndex + 7).Trim();
			if(string.IsNullOrEmpty(tagText))
				return true;

			var tag = ParseGameTag(tagText);
			var value = GameTagHelper.ParseTag(tag, valueText);
			var entity = GetOrCreateEntity(_pendingTagEntityId);
			entity.Tags.TryGetValue(tag, out var prevValue);
			if(prevValue == value)
				return true;
			entity.Tags[tag] = value;
			ApplyDerivedTag(entity, tag, value);
			RefreshCombatSnapshotIfNeeded("tag_line", entity, tag);
			return true;
		}

		private void HandleTagChange(string line)
		{
			// TAG_CHANGE 更新实体 Tag，并驱动快照生成。
			var match = LogConstants.PowerTaskList.TagChangeRegex.Match(line);
			if(!match.Success)
				return;

			var rawEntity = match.Groups["entity"].Value;
			var entityId = TryGetEntityId(rawEntity);
			// 以名称出现的 GameEntity 映射到已记录的 id。
			if(entityId <= 0 && rawEntity == "GameEntity" && _gameEntityId > 0)
				entityId = _gameEntityId;
			if(entityId <= 0)
				return;

			var entity = GetOrCreateEntity(entityId);

			var tag = ParseGameTag(match.Groups["tag"].Value);
			var value = GameTagHelper.ParseTag(tag, match.Groups["value"].Value);
			entity.Tags.TryGetValue(tag, out var prevValue);
			if(prevValue == value)
				return;
			entity.Tags[tag] = value;

			// 更新派生字段并处理特殊 Tag。
			ApplyDerivedTag(entity, tag, value);
			if(tag == GameTag.DAMAGE && entity.CardType == CardType.HERO)
				_heroEntityDamageTag[entity.Id] = value;
			HandleTurnTag(entityId, tag, value);
			HandlePlayState(entityId, tag, value);
			HandleNextOpponent(tag, value, entity);
			HandleCombatState(tag, prevValue, value);
			HandleHeroEntityTag(entityId, tag, value, entity);
			HandleShopEvents(tag, prevValue, value, entity);
			if((tag == GameTag.HEALTH || tag == GameTag.ARMOR || tag == GameTag.DAMAGE)
			   && (entity.CardType == CardType.HERO || entity.CardType == CardType.PLAYER)
			   && (entity.Zone == Zone.PLAY || entity.CardType == CardType.PLAYER))
			{
				var localHeroEntityId = TryGetHeroEntityId(_localPlayerId);
				if(localHeroEntityId <= 0)
					localHeroEntityId = ResolveCoreHeroEntityId(_localPlayerId);
				var opponentHeroEntityId = TryGetHeroEntityId(_currentOpponentPlayerId);
				if(opponentHeroEntityId <= 0)
					opponentHeroEntityId = ResolveCoreHeroEntityId(_currentOpponentPlayerId);
				var corePlayerHealth = Core.Game?.Player?.Hero?.GetTag(GameTag.HEALTH) ?? 0;
				var corePlayerArmor = Core.Game?.Player?.Hero?.GetTag(GameTag.ARMOR) ?? 0;
				var coreOpponentHealth = Core.Game?.Opponent?.Hero?.GetTag(GameTag.HEALTH) ?? 0;
				var coreOpponentArmor = Core.Game?.Opponent?.Hero?.GetTag(GameTag.ARMOR) ?? 0;
				var role = entity.Id == localHeroEntityId ? "player"
					: entity.Id == opponentHeroEntityId ? "opponent"
					: "unknown";
				var healthTag = GetTagValue(entity, GameTag.HEALTH);
				var armorTag = GetTagValue(entity, GameTag.ARMOR);
				var damageTag = GetTagValue(entity, GameTag.DAMAGE);
				// #region agent log
				RuntimeDebugLog("H19", "PowerLogParser.cs:HandleTagChange", "hero_or_player_stat_change",
					new
					{
						tag = tag.ToString(),
						prevValue,
						value,
						entityId,
						entityCardType = entity.CardType.ToString(),
						entityPlayerId = entity.PlayerId,
						entityController = entity.Controller,
						role,
						turnNumber = _turnNumber,
						combatActive = _combatActive,
						hasCurrentTurnItem = _currentTurnItem != null,
						currentOpponentPlayerId = _currentOpponentPlayerId,
						coreOpponentId = Core.Game?.Opponent?.Id ?? 0,
						localHeroEntityId,
						opponentHeroEntityId,
						healthTag,
						armorTag,
						damageTag,
						corePlayerHealth,
						corePlayerArmor,
						coreOpponentHealth,
						coreOpponentArmor
					});
				// #endregion
				if(entity.CardType == CardType.HERO)
					ApplyEndHealthFromHeroTagChange(tag, value, entity);
			}
			RefreshCombatSnapshotIfNeeded("tag_change", entity, tag);

			// 对局结束：写出记录。
			if(tag == GameTag.STATE && value == (int)State.COMPLETE)
			{
				// #region agent log
				RuntimeDebugLog("H20", "PowerLogParser.cs:HandleTagChange", "state_complete",
					new
					{
						turnNumber = _turnNumber,
						localPlayerId = _localPlayerId,
						currentOpponentPlayerId = _currentOpponentPlayerId,
						corePlayerId = Core.Game?.Player?.Id ?? 0,
						coreOpponentId = Core.Game?.Opponent?.Id ?? 0,
						corePlayerHealth = Core.Game?.Player?.Hero?.GetTag(GameTag.HEALTH) ?? 0,
						corePlayerArmor = Core.Game?.Player?.Hero?.GetTag(GameTag.ARMOR) ?? 0,
						coreOpponentHealth = Core.Game?.Opponent?.Hero?.GetTag(GameTag.HEALTH) ?? 0,
						coreOpponentArmor = Core.Game?.Opponent?.Hero?.GetTag(GameTag.ARMOR) ?? 0
					});
				// #endregion
				WriteAndReset();
			}
		}

		private void HandleBlockStart(string line)
		{
			// 原始 block 可选保存，便于离线解析。
			var match = LogConstants.PowerTaskList.BlockStartRegex.Match(line);
			if(!match.Success)
				return;

			if(_config.SaveRawBlocks && _currentGame != null)
			{
				int.TryParse(match.Groups["id"].Value, out var sourceEntityId);
				int.TryParse(match.Groups["player"].Value, out var playerId);
				var target = match.Groups["target"].Value;
				string? targetCardId = null;
				if(!string.IsNullOrWhiteSpace(target))
				{
					var targetMatch = LogConstants.PowerTaskList.CardIdRegex.Match(target);
					if(targetMatch.Success)
						targetCardId = targetMatch.Groups["cardId"].Value;
				}

				_currentGame.RawBlocks.Add(new RawBlockEvent
				{
					BlockType = match.Groups["type"].Value,
					CardId = match.Groups["Id"].Value,
					SourceEntityId = sourceEntityId,
					PlayerId = playerId,
					TargetCardId = targetCardId,
					TriggerKeyword = match.Groups["triggerKeyword"].Value,
					Raw = line
				});
			}

			if(_combatActive)
				RefreshCombatSnapshotIfNeeded("block_start", null, null);
		}

		private void ApplyDerivedTag(EntityState entity, GameTag tag, int value)
		{
			// 将常用 Tag 值同步到字段以便快速访问。
			switch(tag)
			{
				case GameTag.ZONE:
					entity.Zone = (Zone)value;
					break;
				case GameTag.ZONE_POSITION:
					entity.ZonePosition = value;
					break;
				case GameTag.CONTROLLER:
					entity.Controller = value;
					break;
				case GameTag.CARDTYPE:
					entity.CardType = (CardType)value;
					break;
				case GameTag.PLAYER_ID:
					entity.PlayerId = value;
					if(_playerEntityIdToHeroEntityId.TryGetValue(entity.Id, out var heroEntityId))
					{
						_playerEntityIdToHeroEntityId.Remove(entity.Id);
						if(heroEntityId > 0)
							_playerIdToHeroEntityId[entity.PlayerId] = heroEntityId;
					}
					break;
			}
		}

		private void HandleTurnTag(int entityId, GameTag tag, int value)
		{
			// TURN 存在于 GameEntity，用于计算战棋回合数。
			if(entityId != _gameEntityId || tag != GameTag.TURN)
				return;
			_turnTag = value;
			_turnNumber = (_turnTag + 1) / 2;
		}

		private void HandlePlayState(int entityId, GameTag tag, int value)
		{
			// 本地玩家实体的 PlayState 决定胜/负/平。
			if(_currentGame == null || tag != GameTag.PLAYSTATE)
				return;
			if(_localPlayerId <= 0)
				return;
			if(!_entities.TryGetValue(entityId, out var entity))
				return;
			if(entity.PlayerId != _localPlayerId)
				return;
			_currentGame.Result = ((PlayState)value).ToString();
		}

		private void HandleNextOpponent(GameTag tag, int value, EntityState entity)
		{
			// 记录下一位对手 id，用于关联战斗快照。
			if(tag != GameTag.NEXT_OPPONENT_PLAYER_ID)
				return;
			if(_localPlayerId > 0 && entity.PlayerId != _localPlayerId)
				return;
			_nextOpponentPlayerId = value;

			// #region agent log
			RuntimeDebugLog("H1", "PowerLogParser.cs:HandleNextOpponent", "next_opponent_player_id",
				new
				{
					value,
					entityId = entity.Id,
					entityPlayerId = entity.PlayerId,
					localPlayerId = _localPlayerId
				});
			// #endregion
		}

		private void HandleCombatState(GameTag tag, int prevValue, int value)
		{
			// 通过战棋 setup 相关 Tag 判断战斗切换。
			if(tag != BaconCombatSetupTag && tag != BaconSetupTag)
				return;

			// #region agent log
			RuntimeDebugLog("H11", "PowerLogParser.cs:HandleCombatState", "combat_state_transition",
				new
				{
					tag = tag.ToString(),
					prevValue,
					value,
					combatActive = _combatActive,
					turnNumber = _turnNumber,
					localPlayerId = _localPlayerId,
					currentOpponentPlayerId = _currentOpponentPlayerId,
					nextOpponentPlayerId = _nextOpponentPlayerId
				});
			// #endregion

			if(tag == BaconCombatSetupTag && prevValue == 1 && value == 0)
			{
				if(_combatActive)
					return;
				// #region agent log
				RuntimeDebugLog("H14", "PowerLogParser.cs:HandleCombatState", "combat_start_call",
					new
					{
						tag = tag.ToString(),
						prevValue,
						value,
						turnNumber = _turnNumber,
						localPlayerId = _localPlayerId,
						currentOpponentPlayerId = _currentOpponentPlayerId,
						nextOpponentPlayerId = _nextOpponentPlayerId
					});
				// #endregion
				_combatActive = true;
				StartCombatSnapshot();
			}
			else if(tag == BaconSetupTag && prevValue == 1 && value == 0)
			{
				if(!_combatActive)
					return;
				// #region agent log
				RuntimeDebugLog("H14", "PowerLogParser.cs:HandleCombatState", "combat_end_call",
					new
					{
						tag = tag.ToString(),
						prevValue,
						value,
						turnNumber = _turnNumber,
						localPlayerId = _localPlayerId,
						currentOpponentPlayerId = _currentOpponentPlayerId,
						nextOpponentPlayerId = _nextOpponentPlayerId,
						corePlayerId = Core.Game?.Player?.Id ?? 0,
						coreOpponentId = Core.Game?.Opponent?.Id ?? 0,
						corePlayerHeroCardId = Core.Game?.Player?.Hero?.CardId,
						coreOpponentHeroCardId = Core.Game?.Opponent?.Hero?.CardId
					});
				// #endregion
				EndCombatSnapshot();
				// #region agent log
				RuntimeDebugLog("H14", "PowerLogParser.cs:HandleCombatState", "combat_end_done",
					new
					{
						tag = tag.ToString(),
						turnNumber = _turnNumber,
						localPlayerId = _localPlayerId,
						currentOpponentPlayerId = _currentOpponentPlayerId
					});
				// #endregion
				_combatActive = false;
			}
		}

		private void HandleHeroEntityTag(int entityId, GameTag tag, int value, EntityState entity)
		{
			if(tag != GameTag.HERO_ENTITY)
				return;
			if(value <= 0)
				return;
			if(entity.PlayerId > 0)
			{
				_playerIdToHeroEntityId[entity.PlayerId] = value;
				return;
			}
			_playerEntityIdToHeroEntityId[entityId] = value;
		}

		private void HandleShopEvents(GameTag tag, int prevValue, int value, EntityState entity)
		{
			// 尽力推断商店行为（非战斗阶段）。
			if(_currentGame == null || !_config.RecordShopEvents)
				return;

			if(tag == GameTag.PLAYER_TECH_LEVEL && !_combatActive)
			{
				_currentGame.ShopEvents.Add(new ShopEvent
				{
					TurnNumber = _turnNumber,
					Type = "TavernUpgrade",
					TechLevel = value
				});
			}

			if(tag == GameTag.ZONE && !_combatActive && entity.Controller == _localPlayerId)
			{
				var prevZone = (Zone)prevValue;
				var newZone = (Zone)value;
				if(prevZone == Zone.SETASIDE && (newZone == Zone.HAND || newZone == Zone.PLAY))
				{
					var type = entity.CardType == CardType.MINION ? "BuyMinion"
						: entity.CardType == CardType.SPELL ? "BuySpell"
						: "Buy";
					var card = Database.GetCardFromId(entity.CardId);
					_currentGame.ShopEvents.Add(new ShopEvent
					{
						TurnNumber = _turnNumber,
						Type = type,
						CardId = entity.CardId,
						CardName = card?.Name
					});
				}
			}
		}

		private void StartCombatSnapshot()
		{
			// 战斗开始时抓取双方阵容快照。
			if(_currentGame == null || !(Core.Game?.IsBattlegroundsMatch ?? false))
				return;

			Core.Game?.SnapshotBattlegroundsBoardState();

			var localId = ResolveLocalPlayerId();
			var opponentId = _nextOpponentPlayerId > 0 ? _nextOpponentPlayerId : Core.Game?.Opponent?.Id ?? 0;

			var playerHeroSnapshot = BuildHeroSnapshot(localId);
			var opponentHeroSnapshot = BuildHeroSnapshot(opponentId);
			var playerBoard = BuildBoard(localId, allowWatcher: false, out var playerBoardSource);
			var opponentBoard = BuildBoard(opponentId, allowWatcher: true, out var opponentBoardSource);

			var localHeroEntityId = TryGetHeroEntityId(localId);
			var opponentHeroEntityId = TryGetHeroEntityId(opponentId);
			var localPlayerEntityId = TryGetPlayerEntity(localId)?.Id ?? 0;
			var opponentPlayerEntityId = TryGetPlayerEntity(opponentId)?.Id ?? 0;
			var corePlayerEntityId = Core.Game?.PlayerEntity?.Id ?? 0;
			var coreOpponentEntityId = Core.Game?.OpponentEntity?.Id ?? 0;
			_combatStartPlayerDamageTag = 0;
			_combatStartOpponentDamageTag = 0;
			if(localHeroEntityId > 0 && _heroEntityDamageTag.TryGetValue(localHeroEntityId, out var localDamage))
				_combatStartPlayerDamageTag = localDamage;
			if(opponentHeroEntityId > 0 && _heroEntityDamageTag.TryGetValue(opponentHeroEntityId, out var opponentDamage))
				_combatStartOpponentDamageTag = opponentDamage;

			// #region agent log
			RuntimeDebugLog("H1", "PowerLogParser.cs:StartCombatSnapshot", "combat_start_ids",
				new
				{
					turnNumber = _turnNumber,
					localId,
					localPlayerId = _localPlayerId,
					nextOpponentPlayerId = _nextOpponentPlayerId,
					coreOpponentId = Core.Game?.Opponent?.Id ?? 0,
					corePlayerId = Core.Game?.Player?.Id ?? 0,
					corePlayerEntityId,
					coreOpponentEntityId,
					entities = _entities.Count,
					playerHeroMap = _playerIdToHeroEntityId.Count,
					localHeroEntityId,
					opponentHeroEntityId,
					combatStartPlayerDamageTag = _combatStartPlayerDamageTag,
					combatStartOpponentDamageTag = _combatStartOpponentDamageTag,
					localPlayerEntityId,
					opponentPlayerEntityId,
					playerHeroCardId = playerHeroSnapshot?.CardId,
					opponentHeroCardId = opponentHeroSnapshot?.CardId,
					playerBoardCount = playerBoard.Count,
					opponentBoardCount = opponentBoard.Count,
					playerBoardSource,
					opponentBoardSource
				});
			// #endregion
			var round = new TurnItem
			{
				TurnNumber = _turnNumber,
				PlayerHero = ToHeroInfo(playerHeroSnapshot),
				OpponentHero = ToHeroInfo(opponentHeroSnapshot),
				PlayerBoard = playerBoard,
				OpponentBoard = opponentBoard,
				PlayerBoardSource = playerBoardSource,
				OpponentBoardSource = opponentBoardSource
			};
			round.PlayerStartHealth = playerHeroSnapshot?.Health ?? 0;
			round.PlayerStartArmor = playerHeroSnapshot?.Armor ?? 0;
			round.OpponentStartHealth = opponentHeroSnapshot?.Health ?? 0;
			round.OpponentStartArmor = opponentHeroSnapshot?.Armor ?? 0;
			round.PlayerTechLevel = playerHeroSnapshot?.TechLevel ?? GetPlayerTechLevel(localId);
			round.OpponentTechLevel = opponentHeroSnapshot?.TechLevel ?? GetPlayerTechLevel(opponentId);

			_currentOpponentPlayerId = opponentId;
			_turnItems.Add(round);
			_currentTurnItem = round;
			var missingReason = GetSnapshotMissingReason(round);
			_needsCombatRefresh = !string.IsNullOrEmpty(missingReason);
			LogRoundSnapshot("combat_start", round);
			DebugLog($"combat_start turn={_turnNumber} local={localId} opponent={opponentId} " +
				$"playerHero={(playerHeroSnapshot != null ? "ok" : "null")} opponentHero={(opponentHeroSnapshot != null ? "ok" : "null")} " +
				$"playerBoard={round.PlayerBoard.Count} opponentBoard={round.OpponentBoard.Count} " +
				$"playerBoardSource={round.PlayerBoardSource} opponentBoardSource={round.OpponentBoardSource} " +
				$"missing={(string.IsNullOrEmpty(missingReason) ? "no" : "yes")} reason={missingReason}");
		}

		private void EndCombatSnapshot()
		{
			// 通过战斗前后英雄血量差计算伤害。
			// #region agent log
			RuntimeDebugLog("H10", "PowerLogParser.cs:EndCombatSnapshot", "combat_end_enter",
				new
				{
					hasCurrentTurnItem = _currentTurnItem != null,
					turnNumber = _currentTurnItem?.TurnNumber ?? 0,
					localPlayerId = _localPlayerId,
					opponentPlayerId = _currentOpponentPlayerId
				});
			// #endregion
			if(_currentTurnItem == null)
				return;
			var currentPlayer = BuildHeroSnapshot(_localPlayerId);
			var currentOpponent = BuildHeroSnapshot(_currentOpponentPlayerId);
			var corePlayer = Core.Game?.Player;
			var coreOpponent = Core.Game?.Opponent;
			var corePlayerHero = corePlayer?.Hero;
			var coreOpponentHero = coreOpponent?.Hero;
			// #region agent log
			RuntimeDebugLog("H10", "PowerLogParser.cs:EndCombatSnapshot", "combat_end_sources",
				new
				{
					localPlayerId = _localPlayerId,
					opponentPlayerId = _currentOpponentPlayerId,
					playerHeroCardId = _currentTurnItem.PlayerHero?.CardId,
					opponentHeroCardId = _currentTurnItem.OpponentHero?.CardId,
					currentPlayerHealth = currentPlayer?.Health ?? 0,
					currentPlayerArmor = currentPlayer?.Armor ?? 0,
					currentOpponentHealth = currentOpponent?.Health ?? 0,
					currentOpponentArmor = currentOpponent?.Armor ?? 0,
					corePlayerId = corePlayer?.Id ?? 0,
					coreOpponentId = coreOpponent?.Id ?? 0,
					corePlayerHeroId = corePlayerHero?.Id ?? 0,
					corePlayerHealth = corePlayerHero?.GetTag(GameTag.HEALTH) ?? 0,
					corePlayerArmor = corePlayerHero?.GetTag(GameTag.ARMOR) ?? 0,
					corePlayerHeroCardId = corePlayerHero?.CardId,
					coreOpponentHeroId = coreOpponentHero?.Id ?? 0,
					coreOpponentHealth = coreOpponentHero?.GetTag(GameTag.HEALTH) ?? 0,
					coreOpponentArmor = coreOpponentHero?.GetTag(GameTag.ARMOR) ?? 0,
					coreOpponentHeroCardId = coreOpponentHero?.CardId
				});
			// #endregion
			if(currentPlayer != null)
			{
				_currentTurnItem.PlayerEndHealth = currentPlayer.Health;
				_currentTurnItem.PlayerEndArmor = currentPlayer.Armor;
			}
			if(currentOpponent != null)
			{
				_currentTurnItem.OpponentEndHealth = currentOpponent.Health;
				_currentTurnItem.OpponentEndArmor = currentOpponent.Armor;
			}

			var damageToPlayer = Math.Max(0, _currentTurnItem.PlayerStartHealth - (currentPlayer?.Health ?? 0));
			var damageToOpponent = Math.Max(0, _currentTurnItem.OpponentStartHealth - (currentOpponent?.Health ?? 0));

			var outcome = "Tie";
			if(damageToOpponent > 0 && damageToPlayer == 0)
				outcome = "Win";
			else if(damageToPlayer > 0 && damageToOpponent == 0)
				outcome = "Loss";

			_currentTurnItem.Outcome = outcome;
			DebugLog($"combat_end turn={_currentTurnItem.TurnNumber} outcome={outcome} " +
				$"playerEndHp={_currentTurnItem.PlayerEndHealth} opponentEndHp={_currentTurnItem.OpponentEndHealth}");
			_currentTurnItem = null;
			_currentOpponentPlayerId = 0;
		}

		private HeroSnapshot? BuildHeroSnapshot(int playerId)
		{
			// 根据玩家 id 定位对应英雄实体。
			if(playerId <= 0)
				return null;

			EntityState? hero = null;
			var heroEntityId = TryGetHeroEntityId(playerId);
			if(heroEntityId <= 0)
			{
				var coreHeroEntityId = ResolveCoreHeroEntityId(playerId);
				if(coreHeroEntityId > 0)
				{
					heroEntityId = coreHeroEntityId;
					if(!_playerIdToHeroEntityId.ContainsKey(playerId))
						_playerIdToHeroEntityId[playerId] = coreHeroEntityId;
				}
			}
			if(heroEntityId > 0 && _entities.TryGetValue(heroEntityId, out var mappedHero))
				hero = mappedHero;
			if(hero == null)
			{
				hero = _entities.Values.FirstOrDefault(e =>
					GetCardType(e) == CardType.HERO &&
					GetEffectiveZone(e) == Zone.PLAY &&
					(GetController(e) == playerId || e.PlayerId == playerId));
			}

			if(hero == null)
			{
				var coreHeroEntity = TryGetCoreHeroEntity(heroEntityId);
				if(coreHeroEntity != null)
					return BuildHeroSnapshotFromHdtEntity(coreHeroEntity, playerId);

				// #region agent log
				RuntimeDebugLog("H5", "PowerLogParser.cs:BuildHeroSnapshot", "hero_snapshot_missing",
					new
					{
						playerId,
						heroEntityId,
						corePlayerId = Core.Game?.Player?.Id ?? 0,
						coreOpponentId = Core.Game?.Opponent?.Id ?? 0,
						corePlayerEntityId = Core.Game?.PlayerEntity?.Id ?? 0,
						coreOpponentEntityId = Core.Game?.OpponentEntity?.Id ?? 0,
						coreHeroEntityId = ResolveCoreHeroEntityId(playerId),
						entities = _entities.Count
					});
				// #endregion
				return null;
			}

			var health = GetTagValue(hero, GameTag.HEALTH);
			var damage = GetTagValue(hero, GameTag.DAMAGE);
			var armor = GetTagValue(hero, GameTag.ARMOR);
			var playerEntity = TryGetPlayerEntity(playerId);
			var playerHealth = 0;
			var playerDamage = 0;
			var playerArmor = 0;
			if(playerEntity != null)
			{
				playerHealth = GetTagValue(playerEntity, GameTag.HEALTH);
				playerDamage = GetTagValue(playerEntity, GameTag.DAMAGE);
				playerArmor = GetTagValue(playerEntity, GameTag.ARMOR);
				if(health <= 0 && playerHealth > 0)
				{
					health = playerHealth;
					damage = playerDamage;
				}
				if(armor <= 0 && playerArmor > 0)
					armor = playerArmor;
			}
			var techLevel = GetPlayerTechLevel(playerId);
			var card = Database.GetCardFromId(hero.CardId);
			var coreHeroEntityTags = TryGetCoreHeroEntity(heroEntityId);
			if(coreHeroEntityTags != null)
			{
				var coreHealth = GetTagValue(coreHeroEntityTags, GameTag.HEALTH);
				var coreDamage = GetTagValue(coreHeroEntityTags, GameTag.DAMAGE);
				var coreArmor = GetTagValue(coreHeroEntityTags, GameTag.ARMOR);
				var shouldOverrideHealth = coreHealth > 0 && health == 0;
				var shouldOverrideArmor = coreArmor > 0 && armor == 0;
				if(shouldOverrideHealth || shouldOverrideArmor)
				{
					// #region agent log
					RuntimeDebugLog("H6", "PowerLogParser.cs:BuildHeroSnapshot", "core_hero_tags",
						new
						{
							playerId,
							heroEntityId,
							health,
							damage,
							armor,
							coreHealth,
							coreDamage,
							coreArmor,
							coreTagsCount = coreHeroEntityTags.Tags?.Count ?? 0
						});
					// #endregion
					if(shouldOverrideHealth)
					{
						health = coreHealth;
						damage = coreDamage;
					}
					if(shouldOverrideArmor)
						armor = coreArmor;
				}
			}

			var heroCardType = hero != null ? GetCardType(hero).ToString() : null;
			var heroTagsCount = hero?.Tags?.Count ?? 0;
			var playerEntityCardId = playerEntity?.CardId;
			var playerEntityCardType = playerEntity != null ? GetCardType(playerEntity).ToString() : null;
			var playerTagsCount = playerEntity?.Tags?.Count ?? 0;
			var playerEntityHasPlayerIdTag = playerEntity != null && playerEntity.Tags.ContainsKey(GameTag.PLAYER_ID);

			// #region agent log
			RuntimeDebugLog("H2", "PowerLogParser.cs:BuildHeroSnapshot", "hero_snapshot",
				new
				{
					playerId,
					heroEntityId,
					heroFound = hero != null,
					cardId = hero?.CardId,
					heroZone = hero != null ? GetEffectiveZone(hero).ToString() : null,
					heroController = hero != null ? GetController(hero) : 0,
					heroPlayerId = hero?.PlayerId ?? 0,
					heroCardType,
					heroTagsCount,
					heroHealthTag = health,
					heroDamageTag = damage,
					heroArmorTag = armor,
					techLevel,
					playerEntityId = playerEntity?.Id ?? 0,
					playerEntityCardId,
					playerEntityCardType,
					playerTagsCount,
					playerEntityHasPlayerIdTag,
					playerHealthTag = playerHealth,
					playerDamageTag = playerDamage,
					playerArmorTag = playerArmor
				});
			// #endregion

			return new HeroSnapshot
			{
				CardId = hero.CardId,
				Name = card?.Name,
				Health = Math.Max(0, health - damage),
				Armor = armor,
				TechLevel = techLevel,
				IsDead = health - damage <= 0,
				Tags = ToTagMap(hero.Tags)
			};
		}

		private HeroInfo? ToHeroInfo(HeroSnapshot? hero)
		{
			if(hero == null)
				return null;
			return new HeroInfo
			{
				CardId = hero.CardId,
				Name = hero.Name
			};
		}

		private List<MinionSnapshot> BuildBoard(int playerId, bool allowWatcher, out string source)
		{
			// 为指定玩家构建有序的随从列表。
			if(playerId <= 0)
			{
				source = "PowerLog";
				return new List<MinionSnapshot>();
			}

			var corePlayerId = Core.Game?.Player?.Id ?? 0;
			if(!allowWatcher && corePlayerId == playerId)
			{
				var coreBoard = BuildBoardFromCorePlayer();
				if(coreBoard.Count > 0)
				{
					source = "HDTPlayerBoard";
					// #region agent log
					RuntimeDebugLog("H8", "PowerLogParser.cs:BuildBoard", "board_from_core_player",
						new { playerId, count = coreBoard.Count, source });
					// #endregion
					return coreBoard;
				}
			}

			if(allowWatcher && IsOpponentPlayerId(playerId))
			{
				var watcherBoard = BuildBoardFromWatcher(playerId, out var watcherSource);
				if(watcherBoard.Count > 0)
				{
					var watcherInfo = GetWatcherInfo();
					source = watcherSource;
					// #region agent log
					RuntimeDebugLog("H3", "PowerLogParser.cs:BuildBoard", "board_from_watcher",
						new
						{
							playerId,
							count = watcherBoard.Count,
							source = watcherSource,
							allowWatcher,
							isOpponent = IsOpponentPlayerId(playerId),
							watcherIdsCount = watcherInfo.idsCount,
							watcherUpdatedAgeMs = watcherInfo.ageMs
						});
					// #endregion
					return watcherBoard;
				}
			}

			if(allowWatcher && IsOpponentPlayerId(playerId))
			{
				var hdtBoard = BuildBoardFromHdtSnapshot(playerId, out var hdtSource);
				if(hdtBoard.Count > 0)
				{
					var watcherInfo = GetWatcherInfo();
					source = hdtSource;
					// #region agent log
					RuntimeDebugLog("H3", "PowerLogParser.cs:BuildBoard", "board_from_hdt_snapshot",
						new
						{
							playerId,
							count = hdtBoard.Count,
							source = hdtSource,
							allowWatcher,
							isOpponent = IsOpponentPlayerId(playerId),
							watcherIdsCount = watcherInfo.idsCount,
							watcherUpdatedAgeMs = watcherInfo.ageMs
						});
					// #endregion
					return hdtBoard;
				}
			}

			var minions = BuildBoardFromEntities(playerId);
			if(!allowWatcher && minions.Count == 0)
			{
				var coreBoardCount = GetCoreBoardCount(playerId);
				if(coreBoardCount > 0)
				{
					// #region agent log
					RuntimeDebugLog("H8", "PowerLogParser.cs:BuildBoard", "core_board_available",
						new { playerId, coreBoardCount });
					// #endregion
				}
			}
			if(minions.Count == 0)
			{
				var hdtBoard = BuildBoardFromHdtSnapshot(playerId, out var hdtSource);
				if(hdtBoard.Count > 0)
				{
					source = hdtSource;
					// #region agent log
					RuntimeDebugLog("H8", "PowerLogParser.cs:BuildBoard", "board_from_hdt_fallback",
						new { playerId, count = hdtBoard.Count, source = hdtSource });
					// #endregion
					return hdtBoard;
				}
			}
			var powerWatcherInfo = allowWatcher ? GetWatcherInfo() : (idsCount: 0, ageMs: -1L);
			source = "PowerLog";
			// #region agent log
			RuntimeDebugLog("H4", "PowerLogParser.cs:BuildBoard", "board_from_powerlog",
				new
				{
					playerId,
					count = minions.Count,
					source,
					allowWatcher,
					isOpponent = IsOpponentPlayerId(playerId),
					watcherIdsCount = powerWatcherInfo.idsCount,
					watcherUpdatedAgeMs = powerWatcherInfo.ageMs
				});
			// #endregion
			return minions;
		}

		private List<MinionSnapshot> BuildBoardFromEntities(int playerId)
		{
			var minions = _entities.Values
				.Where(e => GetController(e) == playerId && GetCardType(e) == CardType.MINION && GetEffectiveZone(e) == Zone.PLAY)
				.OrderBy(e => GetEffectiveZonePosition(e))
				.ToList();

			return minions.Select(BuildMinionSnapshot).ToList();
		}

		private List<MinionSnapshot> BuildBoardFromCorePlayer()
		{
			var player = Core.Game?.Player;
			if(player == null)
				return new List<MinionSnapshot>();
			return player.Board
				.Where(e => e != null && e.IsMinion && e.IsInZone(Zone.PLAY))
				.OrderBy(e => e.ZonePosition)
				.Select(BuildMinionSnapshot)
				.ToList();
		}

		private List<MinionSnapshot> BuildBoardFromWatcher(int playerId, out string source)
		{
			source = "Watcher";
			List<int> ids;
			lock(_watcherLock)
			{
				ids = _watcherOpponentEntityIds.ToList();
			}

			if(ids.Count == 0 || playerId <= 0)
				return new List<MinionSnapshot>();

			var parsedFoundCount = 0;
			var parsedMinionCount = 0;
			var parsedInPlayCount = 0;
			var parsedControllerMatchCount = 0;
			var coreFoundCount = 0;
			var coreMinionInPlayCount = 0;
			var coreControllerMatchCount = 0;
			var minions = new List<MinionSnapshot>();
			foreach(var id in ids)
			{
				if(!_entities.TryGetValue(id, out var entity))
				{
					if(Core.Game?.Entities.TryGetValue(id, out var coreMissingEntity) == true)
					{
						coreFoundCount++;
						if(coreMissingEntity.IsMinion && coreMissingEntity.IsInZone(Zone.PLAY))
							coreMinionInPlayCount++;
						if(coreMissingEntity.IsControlledBy(playerId))
							coreControllerMatchCount++;
					}
					continue;
				}
				parsedFoundCount++;
				if(GetCardType(entity) != CardType.MINION)
				{
					continue;
				}
				parsedMinionCount++;
				if(GetController(entity) != playerId)
				{
					continue;
				}
				parsedControllerMatchCount++;
				if(GetEffectiveZone(entity) != Zone.PLAY)
				{
					continue;
				}
				parsedInPlayCount++;
				minions.Add(BuildMinionSnapshot(entity));
			}

			// #region agent log
			RuntimeDebugLog("H9", "PowerLogParser.cs:BuildBoardFromWatcher", "watcher_ids_resolution",
				new
				{
					playerId,
					idsCount = ids.Count,
					parsedFoundCount,
					parsedMinionCount,
					parsedControllerMatchCount,
					parsedInPlayCount,
					coreFoundCount,
					coreMinionInPlayCount,
					coreControllerMatchCount
				});
			// #endregion

			return minions.OrderBy(m => m.ZonePosition).ToList();
		}

		private List<MinionSnapshot> BuildBoardFromHdtSnapshot(int playerId, out string source)
		{
			source = "HDTBoardSnapshot";
			var heroEntityId = TryGetHeroEntityId(playerId);
			if(heroEntityId <= 0)
				heroEntityId = ResolveCoreHeroEntityId(playerId);
			if(heroEntityId <= 0)
				return new List<MinionSnapshot>();
			var snapshot = Core.Game?.GetBattlegroundsBoardStateFor(heroEntityId);
			if(snapshot?.Entities == null || snapshot.Entities.Length == 0)
				return new List<MinionSnapshot>();

			return snapshot.Entities
				.Where(e => e != null && e.IsMinion && e.IsInZone(Zone.PLAY))
				.OrderBy(e => e.ZonePosition)
				.Select(BuildMinionSnapshot)
				.ToList();
		}

		private bool IsOpponentPlayerId(int playerId)
		{
			var coreOpponentId = Core.Game?.Opponent?.Id ?? 0;
			return playerId > 0 && (playerId == _currentOpponentPlayerId || playerId == _nextOpponentPlayerId || playerId == coreOpponentId);
		}

		private MinionSnapshot BuildMinionSnapshot(EntityState entity)
		{
			// 将实体 Tag 映射为随从快照。
			var card = Database.GetCardFromId(entity.CardId);
			var atk = GetTagValue(entity, GameTag.ATK);
			var health = GetTagValue(entity, GameTag.HEALTH);
			var damage = GetTagValue(entity, GameTag.DAMAGE);
			var statuses = _statusTags
				.Where(t => GetTagValue(entity, t) > 0)
				.Select(t => t.ToString())
				.ToList();

			return new MinionSnapshot
			{
				CardId = entity.CardId,
				Name = card?.Name,
				ZonePosition = GetEffectiveZonePosition(entity),
				Attack = atk,
				MaxHealth = health,
				Damage = damage,
				Health = Math.Max(0, health - damage),
				Statuses = statuses,
				Tags = ToTagMap(entity.Tags)
			};
		}

		private MinionSnapshot BuildMinionSnapshot(HdtEntity entity)
		{
			var card = Database.GetCardFromId(entity.CardId);
			var atk = GetTagValue(entity, GameTag.ATK);
			var health = GetTagValue(entity, GameTag.HEALTH);
			var damage = GetTagValue(entity, GameTag.DAMAGE);
			var statuses = _statusTags
				.Where(t => GetTagValue(entity, t) > 0)
				.Select(t => t.ToString())
				.ToList();

			return new MinionSnapshot
			{
				CardId = entity.CardId,
				Name = card?.Name,
				ZonePosition = entity.ZonePosition,
				Attack = atk,
				MaxHealth = health,
				Damage = damage,
				Health = Math.Max(0, health - damage),
				Statuses = statuses,
				Tags = ToTagMap(entity.Tags)
			};
		}

		private int GetPlayerTechLevel(int playerId)
		{
			// 酒馆等级存储在玩家实体 Tag 中。
			var playerEntity = TryGetPlayerEntity(playerId);
			if(playerEntity == null)
			{
				var corePlayerEntity = ResolveCorePlayerEntity(playerId);
				if(corePlayerEntity == null)
					return 0;
				return GetTagValue(corePlayerEntity, GameTag.PLAYER_TECH_LEVEL);
			}
			var techLevel = GetTagValue(playerEntity, GameTag.PLAYER_TECH_LEVEL);
			if(techLevel == 0)
			{
				var corePlayerEntity = ResolveCorePlayerEntity(playerId);
				var coreTech = corePlayerEntity != null ? GetTagValue(corePlayerEntity, GameTag.PLAYER_TECH_LEVEL) : 0;
				if(coreTech > 0)
				{
					// #region agent log
					RuntimeDebugLog("H7", "PowerLogParser.cs:GetPlayerTechLevel", "core_player_tech",
						new { playerId, techLevel, coreTech });
					// #endregion
					techLevel = coreTech;
				}
			}
			return techLevel;
		}

		private HdtEntity? TryGetCoreHeroEntity(int heroEntityId)
		{
			if(heroEntityId <= 0)
				return null;
			var entities = Core.Game?.Entities;
			if(entities == null)
				return null;
			return entities.TryGetValue(heroEntityId, out var entity) ? entity : null;
		}

		private HdtEntity? ResolveCorePlayerEntity(int playerId)
		{
			var game = Core.Game;
			if(game == null || playerId <= 0)
				return null;
			if(game.Player?.Id == playerId)
				return game.PlayerEntity;
			if(game.Opponent?.Id == playerId)
				return game.OpponentEntity;
			return null;
		}

		private int GetCoreBoardCount(int playerId)
		{
			var heroEntityId = ResolveCoreHeroEntityId(playerId);
			if(heroEntityId <= 0)
				return 0;
			var snapshot = Core.Game?.GetBattlegroundsBoardStateFor(heroEntityId);
			if(snapshot?.Entities == null)
				return 0;
			return snapshot.Entities.Count(e => e != null && e.IsMinion && e.IsInZone(Zone.PLAY));
		}

		private HeroSnapshot BuildHeroSnapshotFromHdtEntity(HdtEntity hero, int playerId)
		{
			var health = GetTagValue(hero, GameTag.HEALTH);
			var damage = GetTagValue(hero, GameTag.DAMAGE);
			var armor = GetTagValue(hero, GameTag.ARMOR);
			var techLevel = GetPlayerTechLevel(playerId);
			var card = Database.GetCardFromId(hero.CardId);
			return new HeroSnapshot
			{
				CardId = hero.CardId,
				Name = card?.Name,
				Health = Math.Max(0, health - damage),
				Armor = armor,
				TechLevel = techLevel,
				IsDead = health - damage <= 0,
				Tags = ToTagMap(hero.Tags)
			};
		}

		private EntityState? TryGetPlayerEntity(int playerId)
		{
			if(playerId <= 0)
				return null;
			return _entities.Values.FirstOrDefault(e =>
				e.PlayerId == playerId && e.Tags.ContainsKey(GameTag.PLAYER_ID));
		}

		private int GetTagValue(EntityState entity, GameTag tag)
		{
			// 未出现的 Tag 默认 0。
			return entity.Tags.TryGetValue(tag, out var value) ? value : 0;
		}

		private int GetTagValue(HdtEntity entity, GameTag tag)
		{
			return entity.Tags.TryGetValue(tag, out var value) ? value : 0;
		}

		private int TryGetHeroEntityId(int playerId)
		{
			if(playerId <= 0)
				return 0;
			if(_playerIdToHeroEntityId.TryGetValue(playerId, out var heroEntityId) && heroEntityId > 0)
				return heroEntityId;
			var hero = _entities.Values.FirstOrDefault(e =>
				GetCardType(e) == CardType.HERO &&
				GetEffectiveZone(e) == Zone.PLAY &&
				(GetController(e) == playerId || e.PlayerId == playerId));
			return hero?.Id ?? 0;
		}

		private int ResolveLocalPlayerId()
		{
			var localId = _localPlayerId > 0 ? _localPlayerId : 0;
			if(localId <= 0)
				localId = Core.Game?.Player?.Id ?? 0;
			if(localId <= 0)
				localId = TryGetPlayerIdFromCorePlayer(Core.Game?.Player);
			if(localId <= 0)
				localId = InferLocalPlayerIdFromEntities();
			return localId;
		}

		private int TryGetPlayerIdFromCorePlayer(object? player)
		{
			if(player == null)
				return 0;
			var playerId = TryReadIntProperty(player, "PlayerId");
			if(playerId > 0)
				return playerId;
			var entityId = TryReadIntProperty(player, "EntityId");
			if(entityId <= 0)
				entityId = TryReadIntProperty(player, "PlayerEntityId");
			if(entityId > 0)
				return TryGetPlayerIdFromEntity(entityId);
			return 0;
		}

		private int InferLocalPlayerIdFromEntities()
		{
			if(_entities.Count == 0)
				return 0;
			var playerEntity = _entities.Values.FirstOrDefault(e =>
				e.PlayerId > 0 && e.Tags.ContainsKey(GameTag.PLAYER_ID));
			return playerEntity?.PlayerId ?? 0;
		}

		private int TryGetPlayerIdFromEntity(int entityId)
		{
			if(!_entities.TryGetValue(entityId, out var entity))
				return 0;
			if(entity.PlayerId > 0)
				return entity.PlayerId;
			var controller = GetController(entity);
			if(controller > 0)
				return controller;
			var playerIdTag = GetTagValue(entity, GameTag.PLAYER_ID);
			return playerIdTag > 0 ? playerIdTag : 0;
		}

		private int TryReadIntProperty(object target, string name)
		{
			var prop = target.GetType().GetProperty(name);
			if(prop == null || prop.PropertyType != typeof(int))
				return 0;
			return (int)prop.GetValue(target);
		}

		private int GetController(EntityState entity)
		{
			var lettuceController = GetTagValue(entity, GameTag.LETTUCE_CONTROLLER);
			if(lettuceController > 0)
				return lettuceController;
			if(entity.Controller > 0)
				return entity.Controller;
			var tagValue = GetTagValue(entity, GameTag.CONTROLLER);
			return tagValue > 0 ? tagValue : entity.Controller;
		}

		private Zone GetEffectiveZone(EntityState entity)
		{
			var fakeZone = GetTagValue(entity, GameTag.FAKE_ZONE);
			if(fakeZone > 0)
				return (Zone)fakeZone;
			if(entity.Zone != Zone.INVALID)
				return entity.Zone;
			var zone = GetTagValue(entity, GameTag.ZONE);
			return zone > 0 ? (Zone)zone : entity.Zone;
		}

		private int GetEffectiveZonePosition(EntityState entity)
		{
			var fakePos = GetTagValue(entity, GameTag.FAKE_ZONE_POSITION);
			if(fakePos > 0)
				return fakePos;
			if(entity.ZonePosition > 0)
				return entity.ZonePosition;
			var pos = GetTagValue(entity, GameTag.ZONE_POSITION);
			return pos > 0 ? pos : entity.ZonePosition;
		}

		private CardType GetCardType(EntityState entity)
		{
			if(entity.CardType != CardType.INVALID)
				return entity.CardType;
			if(!string.IsNullOrEmpty(entity.CardId))
			{
				var card = Database.GetCardFromId(entity.CardId);
				if(card?.TypeEnum != null)
					return card.TypeEnum.Value;
			}
			return entity.CardType;
		}

		private Dictionary<string, int> ToTagMap(Dictionary<GameTag, int> tags)
		{
			// 将 Tag 字典转换为 JSON 友好的 map。
			var dict = new Dictionary<string, int>();
			foreach(var kvp in tags)
				dict[kvp.Key.ToString()] = kvp.Value;
			return dict;
		}

		private void RefreshCombatSnapshotIfNeeded(string reason, EntityState? entity, GameTag? tag)
		{
			if(!_combatActive || _currentTurnItem == null || !_needsCombatRefresh)
				return;
			if(tag.HasValue && !_snapshotRefreshTags.Contains(tag.Value))
				return;
			if(entity != null)
			{
				var cardType = GetCardType(entity);
				if(cardType != CardType.HERO && cardType != CardType.MINION && string.IsNullOrEmpty(entity.CardId))
					return;
			}

			TryRefreshCurrentTurnSnapshot(reason);
		}

		private void TryRefreshCurrentTurnSnapshot(string reason)
		{
			if(_currentTurnItem == null)
				return;

			var localId = ResolveLocalPlayerId();
			var opponentId = _currentOpponentPlayerId > 0 ? _currentOpponentPlayerId
				: (_nextOpponentPlayerId > 0 ? _nextOpponentPlayerId : Core.Game?.Opponent?.Id ?? 0);

			var playerHeroSnapshot = BuildHeroSnapshot(localId);
			var opponentHeroSnapshot = BuildHeroSnapshot(opponentId);
			var playerBoard = BuildBoard(localId, allowWatcher: false, out var playerBoardSource);
			var opponentBoard = BuildBoard(opponentId, allowWatcher: true, out var opponentBoardSource);

			var updated = false;

			if(playerHeroSnapshot != null)
			{
				if(_currentTurnItem.PlayerStartHealth <= 0 && playerHeroSnapshot.Health > 0)
				{
					_currentTurnItem.PlayerStartHealth = playerHeroSnapshot.Health;
					updated = true;
				}
				if(_currentTurnItem.PlayerStartArmor <= 0 && playerHeroSnapshot.Armor > 0)
				{
					_currentTurnItem.PlayerStartArmor = playerHeroSnapshot.Armor;
					updated = true;
				}
				if(_currentTurnItem.PlayerTechLevel <= 0 && playerHeroSnapshot.TechLevel > 0)
				{
					_currentTurnItem.PlayerTechLevel = playerHeroSnapshot.TechLevel;
					updated = true;
				}
			}

			if(opponentHeroSnapshot != null)
			{
				if(_currentTurnItem.OpponentStartHealth <= 0 && opponentHeroSnapshot.Health > 0)
				{
					_currentTurnItem.OpponentStartHealth = opponentHeroSnapshot.Health;
					updated = true;
				}
				if(_currentTurnItem.OpponentStartArmor <= 0 && opponentHeroSnapshot.Armor > 0)
				{
					_currentTurnItem.OpponentStartArmor = opponentHeroSnapshot.Armor;
					updated = true;
				}
				if(_currentTurnItem.OpponentTechLevel <= 0 && opponentHeroSnapshot.TechLevel > 0)
				{
					_currentTurnItem.OpponentTechLevel = opponentHeroSnapshot.TechLevel;
					updated = true;
				}
			}

			if(playerBoard.Count > _currentTurnItem.PlayerBoard.Count)
			{
				_currentTurnItem.PlayerBoard = playerBoard;
				_currentTurnItem.PlayerBoardSource = playerBoardSource;
				updated = true;
			}
			if(opponentBoard.Count > _currentTurnItem.OpponentBoard.Count)
			{
				_currentTurnItem.OpponentBoard = opponentBoard;
				_currentTurnItem.OpponentBoardSource = opponentBoardSource;
				updated = true;
			}

			var missingReason = GetSnapshotMissingReason(_currentTurnItem);
			_needsCombatRefresh = !string.IsNullOrEmpty(missingReason);

			if(updated)
				LogRoundSnapshot($"combat_refresh:{reason}", _currentTurnItem);
			DebugLog($"combat_refresh reason={reason} updated={(updated ? "yes" : "no")} " +
				$"playerBoard={_currentTurnItem.PlayerBoard.Count} opponentBoard={_currentTurnItem.OpponentBoard.Count} " +
				$"playerBoardSource={_currentTurnItem.PlayerBoardSource} opponentBoardSource={_currentTurnItem.OpponentBoardSource} " +
				$"playerHp={_currentTurnItem.PlayerStartHealth}/{_currentTurnItem.PlayerStartArmor} " +
				$"opponentHp={_currentTurnItem.OpponentStartHealth}/{_currentTurnItem.OpponentStartArmor} " +
				$"missing={(string.IsNullOrEmpty(missingReason) ? "no" : "yes")} missingReason={missingReason}");
		}

		private string GetSnapshotMissingReason(TurnItem round)
		{
			var reasons = new List<string>();
			if(round.PlayerStartHealth <= 0 && round.PlayerStartArmor <= 0)
				reasons.Add("playerHeroHpArmor");
			if(round.OpponentStartHealth <= 0 && round.OpponentStartArmor <= 0)
				reasons.Add("opponentHeroHpArmor");
			if(round.PlayerBoard.Count == 0)
				reasons.Add("playerBoard");
			if(round.OpponentBoard.Count == 0)
				reasons.Add("opponentBoard");
			return string.Join(",", reasons);
		}

		private void LogRoundSnapshot(string reason, TurnItem round)
		{
			if(round == null)
				return;
			var playerBoard = round.PlayerBoard
				.OrderBy(m => m.ZonePosition)
				.Select(m => $"{m.ZonePosition}:{m.CardId}")
				.ToList();
			var opponentBoard = round.OpponentBoard
				.OrderBy(m => m.ZonePosition)
				.Select(m => $"{m.ZonePosition}:{m.CardId}")
				.ToList();

			var message = $"round_snapshot reason={reason} turn={round.TurnNumber} " +
				$"playerHp={round.PlayerStartHealth}/{round.PlayerStartArmor} opponentHp={round.OpponentStartHealth}/{round.OpponentStartArmor} " +
				$"playerBoard={string.Join(",", playerBoard)} opponentBoard={string.Join(",", opponentBoard)}";
			WriteRoundSnapshotLog(message);
		}

		private void WriteRoundSnapshotLog(string message)
		{
			try
			{
				var path = Path.Combine(PluginConfig.PluginDirectory, "round_snapshots.log");
				var dir = Path.GetDirectoryName(path);
				if(!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
			}
			catch
			{
			}
		}

		private void WriteAndReset()
		{
			// 至少包含一个回合快照才写出。
			if(_currentGame == null || _turnItems.Count == 0)
			{
				ResetState();
				_currentGame = null;
				return;
			}
			if(_gameWritten)
			{
				ResetState();
				_currentGame = null;
				return;
			}

			_currentGame.EndTimeUtc = DateTime.UtcNow;
			_currentGame.LocalPlayerId = _localPlayerId > 0 ? _localPlayerId : _currentGame.LocalPlayerId;
			if(string.IsNullOrWhiteSpace(_currentGame.GameType) && Core.Game != null)
				_currentGame.GameType = Core.Game.CurrentGameType.ToString();

			if(Core.Game?.IsBattlegroundsMatch ?? false)
			{
				ApplyEndHealthFallback();
				// #region agent log
				RuntimeDebugLog("H13", "PowerLogParser.cs:WriteAndReset", "write_turns_call",
					new
					{
						turnsCount = _turnItems.Count,
						lastTurnNumber = _turnItems.Count > 0 ? _turnItems[_turnItems.Count - 1].TurnNumber : 0,
						lastPlayerBoardCount = _turnItems.Count > 0 ? _turnItems[_turnItems.Count - 1].PlayerBoard.Count : 0,
						lastOpponentBoardCount = _turnItems.Count > 0 ? _turnItems[_turnItems.Count - 1].OpponentBoard.Count : 0,
						lastPlayerBoardSource = _turnItems.Count > 0 ? _turnItems[_turnItems.Count - 1].PlayerBoardSource : null,
						lastOpponentBoardSource = _turnItems.Count > 0 ? _turnItems[_turnItems.Count - 1].OpponentBoardSource : null,
						lastPlayerEndHealth = _turnItems.Count > 0 ? _turnItems[_turnItems.Count - 1].PlayerEndHealth : 0,
						lastOpponentEndHealth = _turnItems.Count > 0 ? _turnItems[_turnItems.Count - 1].OpponentEndHealth : 0,
						corePlayerId = Core.Game?.Player?.Id ?? 0,
						coreOpponentId = Core.Game?.Opponent?.Id ?? 0,
						corePlayerHealth = Core.Game?.Player?.Hero?.GetTag(GameTag.HEALTH) ?? 0,
						corePlayerArmor = Core.Game?.Player?.Hero?.GetTag(GameTag.ARMOR) ?? 0,
						coreOpponentHealth = Core.Game?.Opponent?.Hero?.GetTag(GameTag.HEALTH) ?? 0,
						coreOpponentArmor = Core.Game?.Opponent?.Hero?.GetTag(GameTag.ARMOR) ?? 0
					});
				// #endregion
				// 将 JSON 写入配置的输出目录。
				_gameWritten = true;
				JsonWriter.WriteTurns(_config.OutputDirectory, _currentGame, _turnItems);
			}

			ResetState();
			_currentGame = null;
		}

		private void DebugLog(string message)
		{
			if(!_config.KeepDebugLogs)
				return;
			try
			{
				var path = string.IsNullOrWhiteSpace(_config.DebugLogPath)
					? PluginConfig.DefaultDebugLogPath
					: _config.DebugLogPath;
				var dir = Path.GetDirectoryName(path);
				if(!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
			}
			catch
			{
			}
		}

		private void ApplyEndHealthFallback()
		{
			if(_turnItems.Count == 0)
				return;
			var lastTurn = _turnItems[_turnItems.Count - 1];
			var corePlayerHero = Core.Game?.Player?.Hero;
			var coreOpponentHero = Core.Game?.Opponent?.Hero;
			if(corePlayerHero == null || coreOpponentHero == null)
				return;
			var corePlayerHealth = corePlayerHero.GetTag(GameTag.HEALTH);
			var corePlayerArmor = corePlayerHero.GetTag(GameTag.ARMOR);
			var coreOpponentHealth = coreOpponentHero.GetTag(GameTag.HEALTH);
			var coreOpponentArmor = coreOpponentHero.GetTag(GameTag.ARMOR);
			var updated = false;
			if(lastTurn.PlayerEndHealth == 0 && corePlayerHealth > 0)
			{
				lastTurn.PlayerEndHealth = corePlayerHealth;
				updated = true;
			}
			if(lastTurn.PlayerEndArmor == 0 && corePlayerArmor > 0)
			{
				lastTurn.PlayerEndArmor = corePlayerArmor;
				updated = true;
			}
			if(lastTurn.OpponentEndHealth == 0 && coreOpponentHealth > 0)
			{
				lastTurn.OpponentEndHealth = coreOpponentHealth;
				updated = true;
			}
			if(lastTurn.OpponentEndArmor == 0 && coreOpponentArmor > 0)
			{
				lastTurn.OpponentEndArmor = coreOpponentArmor;
				updated = true;
			}
			if(updated)
			{
				var damageToPlayer = Math.Max(0, lastTurn.PlayerStartHealth - lastTurn.PlayerEndHealth);
				var damageToOpponent = Math.Max(0, lastTurn.OpponentStartHealth - lastTurn.OpponentEndHealth);
				var outcome = "Tie";
				if(damageToOpponent > 0 && damageToPlayer == 0)
					outcome = "Win";
				else if(damageToPlayer > 0 && damageToOpponent == 0)
					outcome = "Loss";
				lastTurn.Outcome = outcome;
				// #region agent log
				RuntimeDebugLog("H15", "PowerLogParser.cs:ApplyEndHealthFallback", "end_health_fallback",
					new
					{
						turnNumber = lastTurn.TurnNumber,
						playerHeroCardId = lastTurn.PlayerHero?.CardId,
						opponentHeroCardId = lastTurn.OpponentHero?.CardId,
						localPlayerId = _localPlayerId,
						opponentPlayerId = _currentOpponentPlayerId,
						coreOpponentId = Core.Game?.Opponent?.Id ?? 0,
						playerEndHealth = lastTurn.PlayerEndHealth,
						playerEndArmor = lastTurn.PlayerEndArmor,
						opponentEndHealth = lastTurn.OpponentEndHealth,
						opponentEndArmor = lastTurn.OpponentEndArmor,
						playerStartHealth = lastTurn.PlayerStartHealth,
						playerStartArmor = lastTurn.PlayerStartArmor,
						opponentStartHealth = lastTurn.OpponentStartHealth,
						opponentStartArmor = lastTurn.OpponentStartArmor,
						corePlayerHealth,
						corePlayerArmor,
						coreOpponentHealth,
						coreOpponentArmor,
						outcome
					});
				// #endregion
			}
		}

		private void ApplyEndHealthFromHeroTagChange(GameTag tag, int value, EntityState entity)
		{
			if(_turnItems.Count == 0)
				return;
			var lastTurn = _turnItems[_turnItems.Count - 1];
			if(lastTurn == null)
				return;
			if(lastTurn.TurnNumber != _turnNumber)
				return;
			var heroCardId = entity.CardId;
			var isPlayerHero = !string.IsNullOrEmpty(heroCardId) && heroCardId == lastTurn.PlayerHero?.CardId;
			var isOpponentHero = !string.IsNullOrEmpty(heroCardId) && heroCardId == lastTurn.OpponentHero?.CardId;
			if(!isPlayerHero && !isOpponentHero)
			{
				var localHeroEntityId = TryGetHeroEntityId(_localPlayerId);
				if(localHeroEntityId <= 0)
					localHeroEntityId = ResolveCoreHeroEntityId(_localPlayerId);
				var opponentHeroEntityId = TryGetHeroEntityId(_currentOpponentPlayerId);
				if(opponentHeroEntityId <= 0)
					opponentHeroEntityId = ResolveCoreHeroEntityId(_currentOpponentPlayerId);
				isPlayerHero = entity.Id == localHeroEntityId && localHeroEntityId > 0;
				isOpponentHero = entity.Id == opponentHeroEntityId && opponentHeroEntityId > 0;
			}
			if(!isPlayerHero && !isOpponentHero)
				return;

			var updated = false;
			if(tag == GameTag.HEALTH)
			{
				if(isPlayerHero && value > 0 && lastTurn.PlayerEndHealth != value)
				{
					lastTurn.PlayerEndHealth = value;
					updated = true;
				}
				if(isOpponentHero && value > 0 && lastTurn.OpponentEndHealth != value)
				{
					lastTurn.OpponentEndHealth = value;
					updated = true;
				}
			}
			else if(tag == GameTag.ARMOR)
			{
				if(isPlayerHero && value >= 0 && lastTurn.PlayerEndArmor != value)
				{
					lastTurn.PlayerEndArmor = value;
					updated = true;
				}
				if(isOpponentHero && value >= 0 && lastTurn.OpponentEndArmor != value)
				{
					lastTurn.OpponentEndArmor = value;
					updated = true;
				}
			}
			else if(tag == GameTag.DAMAGE)
			{
				var healthTag = GetTagValue(entity, GameTag.HEALTH);
				var baselineDamage = isPlayerHero ? _combatStartPlayerDamageTag : _combatStartOpponentDamageTag;
				var damageDelta = Math.Max(0, value - baselineDamage);
				var startHealth = isPlayerHero ? lastTurn.PlayerStartHealth : lastTurn.OpponentStartHealth;
				var startArmor = isPlayerHero ? lastTurn.PlayerStartArmor : lastTurn.OpponentStartArmor;
				var computedHealth = 0;
				var computedArmor = 0;
				if(healthTag > 0)
				{
					computedHealth = healthTag;
					computedArmor = startArmor;
				}
				else
				{
					computedArmor = Math.Max(0, startArmor - damageDelta);
					var healthDamage = Math.Max(0, damageDelta - startArmor);
					computedHealth = Math.Max(0, startHealth - healthDamage);
				}
				if(isPlayerHero)
				{
					if(lastTurn.PlayerEndHealth != computedHealth)
					{
						lastTurn.PlayerEndHealth = computedHealth;
						updated = true;
					}
					if(lastTurn.PlayerEndArmor != computedArmor)
					{
						lastTurn.PlayerEndArmor = computedArmor;
						updated = true;
					}
				}
				if(isOpponentHero)
				{
					if(lastTurn.OpponentEndHealth != computedHealth)
					{
						lastTurn.OpponentEndHealth = computedHealth;
						updated = true;
					}
					if(lastTurn.OpponentEndArmor != computedArmor)
					{
						lastTurn.OpponentEndArmor = computedArmor;
						updated = true;
					}
				}
				// #region agent log
				RuntimeDebugLog("H21", "PowerLogParser.cs:ApplyEndHealthFromHeroTagChange", "end_health_from_damage",
					new
					{
						turnNumber = lastTurn.TurnNumber,
						heroCardId,
						damage = value,
						healthTag,
						baselineDamage,
						computedHealth,
						computedArmor,
						damageDelta,
						startHealth,
						startArmor,
						playerEndHealth = lastTurn.PlayerEndHealth,
						playerEndArmor = lastTurn.PlayerEndArmor,
						opponentEndHealth = lastTurn.OpponentEndHealth,
						opponentEndArmor = lastTurn.OpponentEndArmor
					});
				// #endregion
			}

			if(updated)
			{
				var damageToPlayer = Math.Max(0, lastTurn.PlayerStartHealth - lastTurn.PlayerEndHealth);
				var damageToOpponent = Math.Max(0, lastTurn.OpponentStartHealth - lastTurn.OpponentEndHealth);
				var outcome = "Tie";
				if(damageToOpponent > 0 && damageToPlayer == 0)
					outcome = "Win";
				else if(damageToPlayer > 0 && damageToOpponent == 0)
					outcome = "Loss";
				lastTurn.Outcome = outcome;
				// #region agent log
				RuntimeDebugLog("H18", "PowerLogParser.cs:ApplyEndHealthFromHeroTagChange", "end_health_from_tag",
					new
					{
						turnNumber = lastTurn.TurnNumber,
						tag = tag.ToString(),
						value,
						heroCardId,
						playerHeroCardId = lastTurn.PlayerHero?.CardId,
						opponentHeroCardId = lastTurn.OpponentHero?.CardId,
						playerEndHealth = lastTurn.PlayerEndHealth,
						playerEndArmor = lastTurn.PlayerEndArmor,
						opponentEndHealth = lastTurn.OpponentEndHealth,
						opponentEndArmor = lastTurn.OpponentEndArmor,
						outcome
					});
				// #endregion
			}
		}

		private EntityState GetOrCreateEntity(int id)
		{
			// 确保实体记录存在于缓存中。
			if(!_entities.TryGetValue(id, out var entity))
			{
				entity = new EntityState(id);
				_entities[id] = entity;
			}
			return entity;
		}

		private int TryGetEntityId(string rawEntity)
		{
			// 实体可能是数字 id 或序列化块文本。
			if(string.IsNullOrWhiteSpace(rawEntity))
				return 0;
			if(rawEntity.StartsWith("["))
			{
				var entityMatch = LogConstants.PowerTaskList.EntityRegex.Match(rawEntity);
				if(entityMatch.Success && int.TryParse(entityMatch.Groups["id"].Value, out var id))
					return id;
			}
			if(int.TryParse(rawEntity, out var rawId))
				return rawId;
			return 0;
		}

		private GameTag ParseGameTag(string rawTag)
		{
			// GameTag 可能是数字或枚举名。
			if(int.TryParse(rawTag, out var rawValue))
				return (GameTag)rawValue;
			return GameTagHelper.ParseEnum<GameTag>(rawTag);
		}

		private int ResolveCoreHeroEntityId(int playerId)
		{
			var game = Core.Game;
			if(game == null || playerId <= 0)
				return 0;
			if(game.Player?.Id == playerId)
				return game.PlayerEntity?.GetTag(GameTag.HERO_ENTITY) ?? 0;
			if(game.Opponent?.Id == playerId)
				return game.OpponentEntity?.GetTag(GameTag.HERO_ENTITY) ?? 0;
			return 0;
		}

		private (int idsCount, long ageMs) GetWatcherInfo()
		{
			lock(_watcherLock)
			{
				var count = _watcherOpponentEntityIds.Count;
				if(_watcherOpponentUpdatedUtc == default)
					return (count, -1);
				var age = DateTime.UtcNow - _watcherOpponentUpdatedUtc;
				return (count, (long)age.TotalMilliseconds);
			}
		}

		private void RuntimeDebugLog(string hypothesisId, string location, string message, object data)
		{
			try
			{
				var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				var payload = new
				{
					id = $"log_{timestamp}_{Guid.NewGuid():N}",
					timestamp,
					runId = "pre-fix",
					hypothesisId,
					location,
					message,
					data
				};
				var json = JsonConvert.SerializeObject(payload);
				File.AppendAllText(RuntimeDebugLogPath, json + Environment.NewLine);
			}
			catch
			{
			}
		}
	}
}

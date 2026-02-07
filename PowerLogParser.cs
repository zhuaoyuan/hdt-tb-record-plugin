using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.LogReader;
using HdtTbRecordPlugin.Models;
using HearthWatcher.EventArgs;

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
		private readonly object _watcherLock = new object();
		private List<int> _watcherOpponentEntityIds = new List<int>();
		private DateTime _watcherOpponentUpdatedUtc;

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
		}

		private void UpdateLocalPlayerId()
		{
			// 使用 HDT 当前玩家 id（若可用）。
			if(Core.Game?.Player?.Id > 0)
				_localPlayerId = Core.Game.Player.Id;
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
			HandleTurnTag(entityId, tag, value);
			HandlePlayState(entityId, tag, value);
			HandleNextOpponent(tag, value, entity);
			HandleCombatState(tag, prevValue, value);
			HandleShopEvents(tag, prevValue, value, entity);
			RefreshCombatSnapshotIfNeeded("tag_change", entity, tag);

			// 对局结束：写出记录。
			if(tag == GameTag.STATE && value == (int)State.COMPLETE)
				WriteAndReset();
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
		}

		private void HandleCombatState(GameTag tag, int prevValue, int value)
		{
			// 通过战棋 setup 相关 Tag 判断战斗切换。
			if(tag != BaconCombatSetupTag && tag != BaconSetupTag)
				return;

			if(tag == BaconCombatSetupTag && prevValue == 1 && value == 0)
			{
				_combatActive = true;
				StartCombatSnapshot();
			}
			else if(tag == BaconCombatSetupTag && prevValue == 0 && value == 1)
			{
				EndCombatSnapshot();
				_combatActive = false;
			}
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

			var localId = _localPlayerId > 0 ? _localPlayerId : Core.Game?.Player?.Id ?? 0;
			var opponentId = _nextOpponentPlayerId > 0 ? _nextOpponentPlayerId : Core.Game?.Opponent?.Id ?? 0;

			var playerHeroSnapshot = BuildHeroSnapshot(localId);
			var opponentHeroSnapshot = BuildHeroSnapshot(opponentId);
			var playerBoard = BuildBoard(localId, allowWatcher: false, out var playerBoardSource);
			var opponentBoard = BuildBoard(opponentId, allowWatcher: true, out var opponentBoardSource);
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
			DebugLog($"combat_start turn={_turnNumber} local={localId} opponent={opponentId} " +
				$"playerHero={(playerHeroSnapshot != null ? "ok" : "null")} opponentHero={(opponentHeroSnapshot != null ? "ok" : "null")} " +
				$"playerBoard={round.PlayerBoard.Count} opponentBoard={round.OpponentBoard.Count} " +
				$"playerBoardSource={round.PlayerBoardSource} opponentBoardSource={round.OpponentBoardSource} " +
				$"missing={(string.IsNullOrEmpty(missingReason) ? "no" : "yes")} reason={missingReason}");
		}

		private void EndCombatSnapshot()
		{
			// 通过战斗前后英雄血量差计算伤害。
			if(_currentTurnItem == null)
				return;
			var currentPlayer = BuildHeroSnapshot(_localPlayerId);
			var currentOpponent = BuildHeroSnapshot(_currentOpponentPlayerId);
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

			var hero = _entities.Values.FirstOrDefault(e =>
				GetCardType(e) == CardType.HERO &&
				GetEffectiveZone(e) == Zone.PLAY &&
				(GetController(e) == playerId || e.PlayerId == playerId));

			if(hero == null)
				return null;

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

			if(allowWatcher && IsOpponentPlayerId(playerId))
			{
				var watcherBoard = BuildBoardFromWatcher(out var watcherSource);
				if(watcherBoard.Count > 0)
				{
					source = watcherSource;
					return watcherBoard;
				}
			}

			var minions = BuildBoardFromEntities(playerId);
			source = "PowerLog";
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

		private List<MinionSnapshot> BuildBoardFromWatcher(out string source)
		{
			source = "Watcher";
			List<int> ids;
			lock(_watcherLock)
			{
				ids = _watcherOpponentEntityIds.ToList();
			}

			if(ids.Count == 0)
				return new List<MinionSnapshot>();

			var minions = new List<MinionSnapshot>();
			foreach(var id in ids)
			{
				if(!_entities.TryGetValue(id, out var entity))
				{
					continue;
				}
				if(GetCardType(entity) != CardType.MINION)
				{
					continue;
				}
				minions.Add(BuildMinionSnapshot(entity));
			}

			return minions.OrderBy(m => m.ZonePosition).ToList();
		}

		private bool IsOpponentPlayerId(int playerId)
		{
			return playerId > 0 && (playerId == _currentOpponentPlayerId || playerId == _nextOpponentPlayerId);
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

		private int GetPlayerTechLevel(int playerId)
		{
			// 酒馆等级存储在玩家实体 Tag 中。
			var playerEntity = _entities.Values.FirstOrDefault(e =>
				e.PlayerId == playerId && e.Tags.ContainsKey(GameTag.PLAYER_TECH_LEVEL));
			if(playerEntity == null)
				return 0;
			return GetTagValue(playerEntity, GameTag.PLAYER_TECH_LEVEL);
		}

		private int GetTagValue(EntityState entity, GameTag tag)
		{
			// 未出现的 Tag 默认 0。
			return entity.Tags.TryGetValue(tag, out var value) ? value : 0;
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

			var localId = _localPlayerId > 0 ? _localPlayerId : Core.Game?.Player?.Id ?? 0;
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
	}
}

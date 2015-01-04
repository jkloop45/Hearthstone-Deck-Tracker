﻿#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Replay;

#endregion

namespace Hearthstone_Deck_Tracker
{
	public class HsLogReader
	{
		#region Properties

		//should be about 180,000 lines
		private const int MaxFileLength = 6000000;

		private readonly Regex _cardMovementRegex = new Regex(@"\w*(cardId=(?<Id>(\w*))).*(zone\ from\ (?<from>((\w*)\s*)*))((\ )*->\ (?<to>(\w*\s*)*))*.*");
		private readonly Regex _otherIdRegex = new Regex(@".*\[.*(id=(?<Id>(\d+))).*");

		private readonly string _fullOutputPath;
		
		private readonly Regex _cardAlreadyInCacheRegex = new Regex(@"somehow\ the\ card\ def\ for\ (?<id>(\w+_\w+))\ was\ already\ in\ the\ cache...");
		private readonly Regex _unloadCardRegex = new Regex(@"unloading\ name=(?<id>(\w+_\w+))\ family=CardPrefab\ persistent=False");
		private readonly Regex _opponentPlayRegex = new Regex(@"\w*(zonePos=(?<zonePos>(\d+))).*(zone\ from\ OPPOSING\ HAND).*");
		private readonly Regex _entityNameRegex = new Regex(@"TAG_CHANGE\ Entity=(?<name>(\w+))\ tag=PLAYER_ID\ value=(?<value>(\d))");
		private readonly Regex _powerListRegex = new Regex(@"GameState.DebugPrintPowerList\(\)\ -\ Count=(?<count>(\d+))");

		private readonly Regex _tagChangeRegex = new Regex(@"TAG_CHANGE\ Entity=(?<entity>(.+))\ tag=(?<tag>(\w+))\ value=(?<value>(\w+))");
		private readonly Regex _entityRegex = new Regex(@"(?=id=(?<id>(\d+)))(?=name=(?<name>(\w+)))?(?=zone=(?<zone>(\w+)))?(?=zonePos=(?<zonePos>(\d+)))?(?=cardId=(?<cardId>(\w+)))?(?=player=(?<player>(\d+)))?(?=type=(?<type>(\w+)))?");
		private readonly Regex _creationRegex = new Regex(@"FULL_ENTITY\ -\ Creating\ ID=(?<id>(\d+))\ CardID=(?<cardId>(\w*))");
		private readonly Regex _creationTagRegex = new Regex(@"tag=(?<tag>(\w+))\ value=(?<value>(\w+))");
		private readonly Regex _updatingEntityRegex = new Regex(@"SHOW_ENTITY\ -\ Updating\ Entity=(?<entity>(.+))\ CardID=(?<cardId>(\w*))");
		private readonly Regex _playerEntityRegex = new Regex(@"Player\ EntityID=(?<id>(\d+))\ PlayerID=(?<playerId>(\d+))\ GameAccountId=(?<gameAccountId>(.+))");
		private readonly Regex _gameEntityRegex = new Regex(@"GameEntity\ EntityID=(?<id>(\d+))");


		private readonly int _updateDelay;
		private readonly Regex _zoneRegex = new Regex(@"\w*(zone=(?<zone>(\w*)).*(zone\ from\ FRIENDLY\ DECK)\w*)");


		private long _currentOffset;
		private bool _doUpdate;
		private bool _first;
		private long _lastGameEnd;
		private long _previousSize;
		private int _addToTurn;
		private bool _gameEnded;

		private bool _waitForModeDetection;
		private int _currentEntityId;
		private dynamic _waitForController;
		private readonly List<Entity> _tmpEntities = new List<Entity>();
		private bool _currentEntityHasCardId;
		#endregion

		private readonly Regex _heroPowerRegex = new Regex(@".*ACTION_START.*(cardId=(?<Id>(\w*))).*SubType=POWER.*");
		private bool _opponentUsedHeroPower;
		private bool _playerUsedHeroPower;
		private IGameHandler _gameHandler;
        /// <summary>
        /// Update deckTracker interface (true by default)
        /// </summary>
		private readonly bool _ifaceUpdateNeeded = true;

		private HsLogReader()
		{
			var hsDirPath = Config.Instance.HearthstoneDirectory;
			var updateDelay = Config.Instance.UpdateDelay;
			_updateDelay = updateDelay == 0 ? 100 : updateDelay;
			while(hsDirPath.EndsWith("\\") || hsDirPath.EndsWith("/"))
				hsDirPath = hsDirPath.Remove(hsDirPath.Length - 1);
			_fullOutputPath = @hsDirPath + @"\Hearthstone_Data\output_log.txt";
		}


		private HsLogReader(string hsDirectory, int updateDeclay, bool interfaceUpdateNeeded)
		{
			var hsDirPath = hsDirectory;
			var updateDelay = updateDeclay;
			_ifaceUpdateNeeded = interfaceUpdateNeeded;

			_updateDelay = updateDelay == 0 ? 100 : updateDelay;
			while (hsDirPath.EndsWith("\\") || hsDirPath.EndsWith("/"))
				hsDirPath = hsDirPath.Remove(hsDirPath.Length - 1);
			_fullOutputPath = @hsDirPath + @"\Hearthstone_Data\output_log.txt";
		}



		public static HsLogReader Instance { get; private set; }

        /// <summary>
        /// 
        /// </summary>
		public static void Create()
		{
			Instance = new HsLogReader();
		}

        /// <summary>
        /// Create HsLogReader instance with custom parameters
        /// Can be used when Config class was not ininialized 
        /// </summary>
        /// <param name="hsDirectory"> Game directory </param>
        /// <param name="updateDeclay">Log file update Declay</param>
        /// <param name="ifaceUpdateNeeded">Update UI flag. Can be set to false, if UI updating  is not required </param>
		public static void Create(string hsDirectory, int updateDeclay, bool ifaceUpdateNeeded = true)
		{
			Instance = new HsLogReader(hsDirectory, updateDeclay, ifaceUpdateNeeded);
		}

		public int GetTurnNumber()
		{
			if(!Game.IsMulliganDone)
				return 0;
			if(_addToTurn == -1)
			{
				var firstPlayer = Game.Entities.FirstOrDefault(e => e.Value.HasTag(GAME_TAG.FIRST_PLAYER));
				if(firstPlayer.Value != null)
				{
					_addToTurn = firstPlayer.Value.GetTag(GAME_TAG.CONTROLLER) == Game.PlayerId ? 0 : 1;
				}
			}
			Entity entity;
			if(Game.Entities.TryGetValue(1, out entity))
				return (entity.Tags[GAME_TAG.TURN] + (_addToTurn == -1 ? 0 : _addToTurn)) / 2;
			return 0;
		}

        /// <summary>
        /// Start tracking gamelogs with default impelementaion of GameEventHandler
        /// </summary>
		public void Start()
        {
	        _addToTurn = -1;
			_first = true;
			_doUpdate = true;
	        _gameEnded = false;
			_gameHandler = new GameEventHandler();
			ReadFileAsync();
		}

        /// <summary>
        /// Start tracking gamelogs with custom impelementaion of GameEventHandler
        /// </summary>
        /// <param name="gh"> Custom Game handler implementation </param>
		public void Start(IGameHandler gh)
		{
			_addToTurn = -1;
			_first = true;
			_doUpdate = true;
			_gameHandler = gh;
			_gameEnded = false;
			ReadFileAsync();
		}
		

		public void Stop()
		{
			_doUpdate = false;
		}

		private async void ReadFileAsync()
		{
			while(_doUpdate)
			{
				if(File.Exists(_fullOutputPath) && Game.IsRunning)
				{
					//find end of last game (avoids reading the full log on start)
					if(_first)
					{
						using(var fs = new FileStream(_fullOutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
						{
							var fileOffset = 0L;
							if(fs.Length > MaxFileLength)
							{
								fileOffset = fs.Length - MaxFileLength;
								fs.Seek(fs.Length - MaxFileLength, SeekOrigin.Begin);
							}
							_previousSize = FindLastGameStart(fs) + fileOffset;
							_currentOffset = _previousSize;
							_first = false;
						}
					}

					using(var fs = new FileStream(_fullOutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					{
						fs.Seek(_previousSize, SeekOrigin.Begin);
						if(fs.Length == _previousSize)
						{
							await Task.Delay(_updateDelay);
							continue;
						}
						var newLength = fs.Length;

						using(var sr = new StreamReader(fs))
						{
							var newLines = sr.ReadToEnd();
							if(!newLines.EndsWith("\n"))
							{
								//hearthstone log apparently does not append full lines
								await Task.Delay(_updateDelay);
								continue;
							}
							
							Analyze(newLines);
							
							if (_ifaceUpdateNeeded)
								Helper.UpdateEverything();
						}

						_previousSize = newLength;
					}
				}

				await Task.Delay(_updateDelay);
			}
		}

		private long FindLastGameStart(FileStream fs)
		{
			using(var sr = new StreamReader(fs))
			{
				long offset = 0, tempOffset = 0;
				var lines = sr.ReadToEnd().Split('\n');

				foreach(var line in lines)
				{
					tempOffset += line.Length + 1;
					if(line.StartsWith("[Bob] legend rank") || line.Contains("CREATE_GAME"))
						offset = tempOffset;
				}

				return offset;
			}
		}

		private void Analyze(string log)
		{
			var logLines = log.Split('\n');
			foreach(var logLine in logLines)
			{
				_currentOffset += logLine.Length + 1;
				#region [Power]
				if(logLine.StartsWith("[Power]"))
				{
					if(_gameEntityRegex.IsMatch(logLine))
					{
						_gameHandler.HandleGameStart();
						var match = _gameEntityRegex.Match(logLine);
						var id = int.Parse(match.Groups["id"].Value);
						if(!Game.Entities.ContainsKey(id))
							Game.Entities.Add(id, new Entity(id));
						_currentEntityId = id;
					}
					else if(_playerEntityRegex.IsMatch(logLine))
					{
						var match = _playerEntityRegex.Match(logLine);
						var id = int.Parse(match.Groups["id"].Value);
						if(!Game.Entities.ContainsKey(id))
							Game.Entities.Add(id, new Entity(id));
						_currentEntityId = id;
					}
					else if(_tagChangeRegex.IsMatch(logLine))
					{
						var match = _tagChangeRegex.Match(logLine);
						var rawEntity = match.Groups["entity"].Value;
						if(rawEntity.StartsWith("[") && _entityRegex.IsMatch(rawEntity))
						{
							var entity = _entityRegex.Match(rawEntity);
							var id = int.Parse(entity.Groups["id"].Value);
							TagChange(match.Groups["tag"].Value, id, match.Groups["value"].Value);
						}
						else
						{
							var entity = Game.Entities.FirstOrDefault(x => x.Value.Name == rawEntity);
							if(entity.Value == null)
							{
								//while the id is unknown, store in tmp entities
								var tmpEntity = _tmpEntities.FirstOrDefault(x => x.Name == rawEntity);
								if(tmpEntity == null)
								{
									tmpEntity = new Entity(_tmpEntities.Count + 1);
									tmpEntity.Name = rawEntity;
                                    _tmpEntities.Add(tmpEntity);
								}
								GAME_TAG tag;
								Enum.TryParse(match.Groups["tag"].Value, out tag);
								var value = ParseTagValue(tag, match.Groups["value"].Value);
								tmpEntity.SetTag(tag, value);
								if(tmpEntity.HasTag(GAME_TAG.ENTITY_ID))
								{
									var id = tmpEntity.GetTag(GAME_TAG.ENTITY_ID);
									if(Game.Entities.ContainsKey(id))
									{
										Game.Entities[id].Name = tmpEntity.Name;
										foreach(var t in tmpEntity.Tags)
										{
											Game.Entities[id].SetTag(t.Key, t.Value);
										}
										_tmpEntities.Remove(tmpEntity);
										Logger.WriteLine("COPIED TMP ENTITY (" + rawEntity + ")");
									}
									else
									{
										Logger.WriteLine("TMP ENTITY (" + rawEntity + ") NOW HAS A KEY, BUT GAME.ENTITIES DOES NOT CONTAIN THIS KEY");
									}
								}
							}
							else
							{
								TagChange(match.Groups["tag"].Value, entity.Key, match.Groups["value"].Value);
							}
						}
					}
					else if(_creationRegex.IsMatch(logLine))
					{
						var match = _creationRegex.Match(logLine);
						var id = int.Parse(match.Groups["id"].Value);
						var cardId = match.Groups["cardId"].Value;
						if(!Game.Entities.ContainsKey(id))
							Game.Entities.Add(id, new Entity(id) { CardId = cardId });
						_currentEntityId = id;
						_currentEntityHasCardId = !string.IsNullOrEmpty(cardId);
					}
					else if(_updatingEntityRegex.IsMatch(logLine))
					{
						var match = _updatingEntityRegex.Match(logLine);
						var cardId = match.Groups["cardId"].Value;
						var rawEntity = match.Groups["entity"].Value;
						if(rawEntity.StartsWith("[") && _entityRegex.IsMatch(rawEntity))
						{
							var entity = _entityRegex.Match(rawEntity);
							var id = int.Parse(entity.Groups["id"].Value);
							_currentEntityId = id;
							if(!Game.Entities.ContainsKey(id))
								Game.Entities.Add(id, new Entity(id));
							Game.Entities[id].CardId = cardId;
						}
					}
					else if(_creationTagRegex.IsMatch(logLine) && !logLine.Contains("HIDE_ENTITY"))
					{
						var match = _creationTagRegex.Match(logLine);
						TagChange(match.Groups["tag"].Value, _currentEntityId, match.Groups["value"].Value);
						
					}
					else if(logLine.Contains("Begin Spectating") && Game.IsInMenu)
					{
						_gameHandler.SetGameMode(GameMode.Spectator);
					}
					else if(_entityNameRegex.IsMatch(logLine))
					{
						var match = _entityNameRegex.Match(logLine);
						var name = match.Groups["name"].Value;
						var player = int.Parse(match.Groups["value"].Value);
						if(player == 1)
							_gameHandler.HandlePlayerName(name);
						else if(player == 2)
							_gameHandler.HandleOpponentName(name);
					}
					else if((Game.PlayerId >= 0 && Game.Entities.First(e => e.Value.HasTag(GAME_TAG.PLAYER_ID) && e.Value.GetTag(GAME_TAG.PLAYER_ID) == Game.PlayerId).Value.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && !_playerUsedHeroPower) 
						|| Game.OpponentId >= 0 && Game.Entities.First(e => e.Value.HasTag(GAME_TAG.PLAYER_ID) && e.Value.GetTag(GAME_TAG.PLAYER_ID) == Game.OpponentId).Value.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && !_opponentUsedHeroPower)
					{
						if(_heroPowerRegex.IsMatch(logLine))
						{
							var id = _heroPowerRegex.Match(logLine).Groups["Id"].Value.Trim();
							if(!string.IsNullOrEmpty(id))
							{
								var heroPower = Game.GetCardFromId(id);
								if(heroPower.Type == "Hero Power")
								{
									if(Game.Entities.First(e => e.Value.HasTag(GAME_TAG.PLAYER_ID) && e.Value.GetTag(GAME_TAG.PLAYER_ID) == Game.PlayerId).Value.GetTag(GAME_TAG.CURRENT_PLAYER) == 1)
									{
										_gameHandler.HandlePlayerHeroPower(id, GetTurnNumber());
										_playerUsedHeroPower = true;
									}
									else
									{
										_gameHandler.HandleOpponentHeroPower(id, GetTurnNumber());
										_opponentUsedHeroPower = true;
									}
								}
							}
						}
					}
				}
				#endregion
				#region [Asset]
				else if(logLine.StartsWith("[Asset]"))
				{
					if(logLine.ToLower().Contains("victory_screen_start") && Game.CurrentGameStats != null && Game.CurrentGameStats.Result == GameResult.None)
                        _gameHandler.HandleWin();
					else if(logLine.ToLower().Contains("defeat_screen_start") && Game.CurrentGameStats != null && Game.CurrentGameStats.Result == GameResult.None)
						_gameHandler.HandleLoss();
					else if(logLine.Contains("rank"))
					{
						_gameHandler.SetGameMode(GameMode.Ranked);
						if(_waitForModeDetection)
							GameEnd();
					}
					else if(_unloadCardRegex.IsMatch(logLine) && Game.CurrentGameMode == GameMode.Arena)
						_gameHandler.HandlePossibleArenaCard(_unloadCardRegex.Match(logLine).Groups["id"].Value);
				}
				#endregion
				#region [Bob]
				else if(logLine.StartsWith("[Bob] ---RegisterScreenPractice---"))
				{
					_gameHandler.SetGameMode(GameMode.Practice);
					if(_waitForModeDetection)
						GameEnd();
				}
				else if(logLine.StartsWith("[Bob] ---RegisterScreenTourneys---"))
				{
					_gameHandler.SetGameMode(GameMode.Casual);
					if(_waitForModeDetection)
						GameEnd();
				}
				else if(logLine.StartsWith("[Bob] ---RegisterScreenForge---"))
				{
					_gameHandler.SetGameMode(GameMode.Arena);
					if(_waitForModeDetection)
						GameEnd();
					Game.ResetArenaCards();
				}
				else if(logLine.StartsWith("[Bob] ---RegisterScreenFriendly---"))
				{
					_gameHandler.SetGameMode(GameMode.Friendly);
					if(_waitForModeDetection)
						GameEnd();
				}
				else if(logLine.StartsWith("[Bob] ---RegisterScreenBox---"))
				{
					//game ended -  back in menu
					if(Game.CurrentGameMode == GameMode.Spectator)
						GameEnd();
					else
					{
						_gameHandler.SetGameMode(GameMode.None);
						_waitForModeDetection = true;
					}
				}
				#endregion
				#region [Rachelle]
				else if(logLine.StartsWith("[Rachelle]"))
				{
					if(_cardAlreadyInCacheRegex.IsMatch(logLine) && Game.CurrentGameMode == GameMode.Arena)
						_gameHandler.HandlePossibleArenaCard(_cardAlreadyInCacheRegex.Match(logLine).Groups["id"].Value);
				}
				#endregion
				#region [Zone]
				else if(logLine.StartsWith("[Zone]"))
				{
					if(_cardMovementRegex.IsMatch(logLine))
					{
						var match = _cardMovementRegex.Match(logLine);

						var id = match.Groups["Id"].Value.Trim();
						var from = match.Groups["from"].Value.Trim();
						var to = match.Groups["to"].Value.Trim();

						if(_zoneRegex.IsMatch(logLine))
							_gameHandler.PlayerSetAside(id);

						//game start/end
						if(id.Contains("HERO") || (id.Contains("NAX") && id.Contains("_01")))
						{
							if(!from.Contains("PLAY"))
							{
								if(to.Contains("FRIENDLY"))
								{
									_gameHandler.SetPlayerHero(CardIds.HeroIdDict[id]);
								}
								else if(to.Contains("OPPOSING"))
								{
									string heroName;
									if(CardIds.HeroIdDict.TryGetValue(id, out heroName))
										_gameHandler.SetOpponentHero(heroName);
								}
							}
							continue;
						}

						if((from.Contains("PLAY") || from.Contains("HAND") || from.Contains("SECRET") || to.Contains("PLAY")) && logLine.Contains("->") && !string.IsNullOrEmpty(id))
							Game.LastZoneChangedCardId = id;

					}
				}
				#endregion

				if(_first)
					break;
			}
		}

		private void GameEnd()
		{
			_waitForModeDetection = false;
			_gameHandler.HandleGameEnd(true);
			_lastGameEnd = _currentOffset;
			//ClearLog();
		}

		private int _lastId;

		private ReplayKeyPoint _proposedKeyPoint;
		private void ProposeKeyPoint(KeyPointType type, int id, ActivePlayer player)
		{
			if(_proposedKeyPoint != null)
			{
				ReplayMaker.Generate(_proposedKeyPoint.Type, _proposedKeyPoint.Id, _proposedKeyPoint.Player);
				Console.WriteLine(_count + " tag changes since proposed kp (same id twice - " + type + ")");
				_count = 0;
			}
			_proposedKeyPoint = new ReplayKeyPoint(null, type, id, player);
		}

		private int _count;
		private void TagChange(string rawTag, int id, string rawValue, bool isRecursive = false)
		{
			if(_lastId != id && _proposedKeyPoint != null)
			{
				ReplayMaker.Generate(_proposedKeyPoint.Type, _proposedKeyPoint.Id, _proposedKeyPoint.Player);
				_proposedKeyPoint = null;
				Console.WriteLine(_count  + " tag changes since proposed kp");
				_count = 0;
			}
			else
				_count++;
			_lastId = id;
			if(!Game.Entities.ContainsKey(id))
				Game.Entities.Add(id, new Entity(id));
			GAME_TAG tag;
			if(!Enum.TryParse(rawTag, out tag))
			{
				int tmp;
				if(int.TryParse(rawTag, out tmp) && Enum.IsDefined(typeof(GAME_TAG), tmp))
					tag = (GAME_TAG)tmp;
			}
			var value = ParseTagValue(tag, rawValue);
			var prevZone = Game.Entities[id].GetTag(GAME_TAG.ZONE);
			Game.Entities[id].SetTag(tag, value);

			if(tag == GAME_TAG.CONTROLLER && _waitForController != null && Game.PlayerId == -1)
			{
				if(_currentEntityHasCardId)
				{
					Game.Entities.First(e => e.Value.GetTag(GAME_TAG.PLAYER_ID) == 1).Value.IsPlayer = value == 1;
					Game.Entities.First(e => e.Value.GetTag(GAME_TAG.PLAYER_ID) == 2).Value.IsPlayer = value != 1;
					Game.PlayerId = value;
					Game.OpponentId = value == 1 ? 2 : 1;
				}
				else
				{
					Game.Entities.First(e => e.Value.GetTag(GAME_TAG.PLAYER_ID) == 1).Value.IsPlayer = value != 1;
					Game.Entities.First(e => e.Value.GetTag(GAME_TAG.PLAYER_ID) == 2).Value.IsPlayer = value == 1;
					Game.PlayerId = value == 1 ? 2 : 1;
					Game.OpponentId = value;
				}
			}
			var controller = Game.Entities[id].GetTag(GAME_TAG.CONTROLLER);
			string player = Game.Entities[id].HasTag(GAME_TAG.CONTROLLER)
				                ? (controller == Game.PlayerId ? "FRIENDLY" : "OPPOSING")
				                : "";
			var cardId = Game.Entities[id].CardId;
			if(tag == GAME_TAG.ZONE)
			{
				//Logger.WriteLine("--------" + player + " " + Game.Entities[id].CardId + " " + (TAG_ZONE)prevZone + " -> " +
				//                 (TAG_ZONE)value);

				if(((TAG_ZONE)value == TAG_ZONE.HAND || ((TAG_ZONE)value == TAG_ZONE.PLAY) && Game.IsMulliganDone) &&
				   _waitForController == null)
				{
					if(!Game.IsMulliganDone)
						prevZone = (int)TAG_ZONE.DECK;
					if(controller == 0)
					{
						Game.Entities[id].SetTag(GAME_TAG.ZONE, prevZone);
						_waitForController = new {Tag = rawTag, Id = id, Value = rawValue};
						Logger.WriteLine("CURRENTLY NO CONTROLLER SET FOR CARD, WAITING...");
						return;
					}
				}
				switch((TAG_ZONE)prevZone)
				{
					case TAG_ZONE.DECK:
						switch((TAG_ZONE)value)
						{
							case TAG_ZONE.HAND:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerDraw(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.Draw, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentDraw(GetTurnNumber());
									ProposeKeyPoint(KeyPointType.Draw, id, ActivePlayer.Opponent);
								}
								break;
							case TAG_ZONE.REMOVEDFROMGAME:
							case TAG_ZONE.GRAVEYARD:
							case TAG_ZONE.PLAY:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerDeckDiscard(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.DeckDiscard, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentDeckDiscard(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.DeckDiscard, id, ActivePlayer.Opponent);
								}
								break;
							case TAG_ZONE.SECRET:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerSecretPlayed(cardId, GetTurnNumber(), true);
									ProposeKeyPoint(KeyPointType.SecretPlayed, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentSecretPlayed(cardId, -1, GetTurnNumber(), true, id);
									ProposeKeyPoint(KeyPointType.SecretPlayed, id, ActivePlayer.Player);
								}
								break;
						}
						break;
					case TAG_ZONE.GRAVEYARD:
						break;
					case TAG_ZONE.HAND:
						switch((TAG_ZONE)value)
						{
							case TAG_ZONE.PLAY:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerPlay(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.Play, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentPlay(cardId, Game.Entities[id].GetTag(GAME_TAG.ZONE_POSITION), GetTurnNumber());
									ProposeKeyPoint(KeyPointType.Play, id, ActivePlayer.Opponent);
								}
								break;
							case TAG_ZONE.REMOVEDFROMGAME:
							case TAG_ZONE.GRAVEYARD:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerHandDiscard(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.HandDiscard, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentHandDiscard(cardId, Game.Entities[id].GetTag(GAME_TAG.ZONE_POSITION),
									                                       GetTurnNumber());
									ProposeKeyPoint(KeyPointType.HandDiscard, id, ActivePlayer.Opponent);
								}
								break;
							case TAG_ZONE.SECRET:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerSecretPlayed(cardId, GetTurnNumber(), false);
									ProposeKeyPoint(KeyPointType.SecretPlayed, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentSecretPlayed(cardId, Game.Entities[id].GetTag(GAME_TAG.ZONE_POSITION),
									                                        GetTurnNumber(), false, id);
									ProposeKeyPoint(KeyPointType.SecretPlayed, id, ActivePlayer.Opponent);
								}
								break;
							case TAG_ZONE.DECK:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerMulligan(cardId);
									ProposeKeyPoint(KeyPointType.Mulligan, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentMulligan(Game.Entities[id].GetTag(GAME_TAG.ZONE_POSITION));
									ProposeKeyPoint(KeyPointType.Mulligan, id, ActivePlayer.Opponent);
								}
								break;
						}
						break;
					case TAG_ZONE.PLAY:
						switch((TAG_ZONE)value)
						{
							case TAG_ZONE.HAND:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerBackToHand(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.PlayToHand, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentPlayToHand(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.PlayToHand, id, ActivePlayer.Opponent);
								}
								break;
							case TAG_ZONE.DECK:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerPlayToDeck(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.PlayToDeck, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
									_gameHandler.HandleOpponentPlayToDeck(cardId, GetTurnNumber());
								ProposeKeyPoint(KeyPointType.PlayToDeck, id, ActivePlayer.Opponent);
								break;
							case TAG_ZONE.GRAVEYARD:
								if(Game.Entities[id].HasTag(GAME_TAG.HEALTH))
								{
									if(controller == Game.PlayerId)
										ProposeKeyPoint(KeyPointType.Death, id, ActivePlayer.Player);
									else if(controller == Game.OpponentId)
										ProposeKeyPoint(KeyPointType.Death, id, ActivePlayer.Opponent);
								}
								break;
						}
						break;
					case TAG_ZONE.SECRET:
						switch((TAG_ZONE)value)
						{
							case TAG_ZONE.SECRET:
							case TAG_ZONE.GRAVEYARD:
								if(controller == Game.PlayerId)
									ProposeKeyPoint(KeyPointType.SecretTriggered, id, ActivePlayer.Player);
								if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentSecretTrigger(cardId, GetTurnNumber(), id);
									ProposeKeyPoint(KeyPointType.SecretTriggered, id, ActivePlayer.Opponent);
								}
								break;
						}
						break;
					case TAG_ZONE.SETASIDE:
					case TAG_ZONE.CREATED:
					case TAG_ZONE.INVALID:
					case TAG_ZONE.REMOVEDFROMGAME:
						switch((TAG_ZONE)value)
						{
							case TAG_ZONE.PLAY:
								if(controller == Game.PlayerId)
									ProposeKeyPoint(KeyPointType.Summon, id, ActivePlayer.Player);
								if(controller == Game.OpponentId)
									ProposeKeyPoint(KeyPointType.Summon, id, ActivePlayer.Opponent);
								break;
							case TAG_ZONE.HAND:
								if(controller == Game.PlayerId)
								{
									_gameHandler.HandlePlayerGet(cardId, GetTurnNumber());
									ProposeKeyPoint(KeyPointType.Obtain, id, ActivePlayer.Player);
								}
								else if(controller == Game.OpponentId)
								{
									_gameHandler.HandleOpponentGet(GetTurnNumber());
									ProposeKeyPoint(KeyPointType.Obtain, id, ActivePlayer.Opponent);
								}
								break;
						}
						break;
				}
			}
			else if(tag == GAME_TAG.PLAYSTATE && !_gameEnded)
			{
				if(Game.Entities[id].IsPlayer)
				{
					if(value == (int)TAG_PLAYSTATE.WON)
					{
						GameEndKeyPoint(true, id);
						_gameHandler.HandleGameEnd(false);
						_gameHandler.HandleWin();
						_gameEnded = true;
					}
					else if(value == (int)TAG_PLAYSTATE.LOST)
					{
						GameEndKeyPoint(false, id);
						_gameHandler.HandleGameEnd(false);
						_gameHandler.HandleLoss();
						_gameEnded = true;
					}
				}
				else
				{
					if(value == (int)TAG_PLAYSTATE.WON)
					{
						GameEndKeyPoint(false, Game.Entities.First(x => x.Value.IsPlayer).Key);
						_gameHandler.HandleGameEnd(false);
						_gameHandler.HandleLoss();
						_gameEnded = true;
					}
					else if(value == (int)TAG_PLAYSTATE.LOST)
					{
						GameEndKeyPoint(true, Game.Entities.First(x => x.Value.IsPlayer).Key);
						_gameHandler.HandleGameEnd(false);
						_gameHandler.HandleWin();
						_gameEnded = true;
					}
				}
			}
			else if(tag == GAME_TAG.CURRENT_PLAYER && value == 1)
				_gameHandler.TurnStart(Game.Entities[id].IsPlayer ? ActivePlayer.Player : ActivePlayer.Opponent, GetTurnNumber());
			else if(tag == GAME_TAG.NUM_ATTACKS_THIS_TURN && value > 0)
			{
				if(controller == Game.PlayerId)
					ProposeKeyPoint(KeyPointType.Attack, id, ActivePlayer.Player);
				else if(controller == Game.OpponentId)
					ProposeKeyPoint(KeyPointType.Attack, id, ActivePlayer.Opponent);

			}
			else if(tag == GAME_TAG.ZONE_POSITION)
			{
				/*var zone = Game.Entities[id].GetTag(GAME_TAG.ZONE);
				if(zone == (int)TAG_ZONE.HAND)
				{
					if(controller == Game.PlayerId)
						ProposeKeyPoint(KeyPointType.HandPos, id, ActivePlayer.Player);
					else if(controller == Game.OpponentId)
						ProposeKeyPoint(KeyPointType.HandPos, id, ActivePlayer.Opponent);
				}
				else if(zone == (int)TAG_ZONE.PLAY)
				{
					if(controller == Game.PlayerId)
						ProposeKeyPoint(KeyPointType.BoardPos, id, ActivePlayer.Player);
					else if(controller == Game.OpponentId)
						ProposeKeyPoint(KeyPointType.BoardPos, id, ActivePlayer.Opponent);
				}*/
			}
			else if(tag == GAME_TAG.CARD_TARGET && value > 0)
			{
				if(controller == Game.PlayerId)
					ProposeKeyPoint(KeyPointType.PlaySpell, id, ActivePlayer.Player);
				else if(controller == Game.OpponentId)
					ProposeKeyPoint(KeyPointType.PlaySpell, id, ActivePlayer.Opponent);
			}
			else if(tag == GAME_TAG.EQUIPPED_WEAPON && value == 0)
			{
				if(controller == Game.PlayerId)
					ProposeKeyPoint(KeyPointType.WeaponDestroyed, id, ActivePlayer.Player);
				else if(controller == Game.OpponentId)
					ProposeKeyPoint(KeyPointType.WeaponDestroyed, id, ActivePlayer.Opponent);
			}
			else if(tag == GAME_TAG.EXHAUSTED && value > 0)
			{
				if(Game.Entities[id].GetTag(GAME_TAG.CARDTYPE) == (int)TAG_CARDTYPE.HERO_POWER)
				{
					if(controller == Game.PlayerId)
						ProposeKeyPoint(KeyPointType.HeroPower, id, ActivePlayer.Player);
					else if(controller == Game.OpponentId)
						ProposeKeyPoint(KeyPointType.HeroPower, id, ActivePlayer.Opponent);
				}
			}
			if(_waitForController != null)
			{
				if(!isRecursive)
				{
					TagChange((string)_waitForController.Tag, (int)_waitForController.Id, (string)_waitForController.Value, true);
					_waitForController = null;
				}

			}
		}

		private void GameEndKeyPoint(bool victory, int id)
		{
			if(_proposedKeyPoint != null)
			{
				ReplayMaker.Generate(_proposedKeyPoint.Type, _proposedKeyPoint.Id, _proposedKeyPoint.Player);
				_proposedKeyPoint = null;
			}
			ReplayMaker.Generate(victory ? KeyPointType.Victory : KeyPointType.Defeat, id, ActivePlayer.Player);
		}

		private int ParseTagValue(GAME_TAG tag, string rawValue)
		{
			int value;
			if(tag == GAME_TAG.ZONE)
			{
				TAG_ZONE zone;
				Enum.TryParse(rawValue, out zone);
				value = (int)zone;
			}
			else if(tag == GAME_TAG.MULLIGAN_STATE)
			{
				TAG_MULLIGAN state;
				Enum.TryParse(rawValue, out state);
				value = (int)state;
			}
			else if(tag == GAME_TAG.PLAYSTATE)
			{
				TAG_PLAYSTATE state;
				Enum.TryParse(rawValue, out state);
				value = (int)state;
			}
			else if(tag == GAME_TAG.CARDTYPE)
			{
				TAG_CARDTYPE type;
				Enum.TryParse(rawValue, out type);
				value = (int)type;
			}
			else
				int.TryParse(rawValue, out value);
			return value;
		}

		public void ClearLog()
		{
			if(Config.Instance.ClearLogFileAfterGame)
			{
				try
				{
					using(var fs = new FileStream(_fullOutputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
					using(var sw = new StreamWriter(fs))
						sw.Write("");
					Logger.WriteLine("Cleared log file");
					Reset(true);
				}
				catch(Exception e)
				{
					Logger.WriteLine("Error cleared log file: " + e.Message);
				}
			}
		}

		internal void Reset(bool full)
		{
			if(full)
			{
				_previousSize = 0;
				_currentOffset = 0;
			}
			else
			{
				_currentOffset = _lastGameEnd;
				_previousSize = _lastGameEnd;
			}
			_first = true;
			_addToTurn = -1;
			_gameEnded = false;
		}
	}
}
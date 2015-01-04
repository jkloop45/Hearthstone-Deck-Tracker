using System.Linq;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace Hearthstone_Deck_Tracker.Replay
{
	public class ReplayKeyPoint
	{
		public Entity[] Data;
		public KeyPointType Type;
		public ActivePlayer Player;
		public int Id;

		public int Turn { get { return Data[0].GetTag(GAME_TAG.TURN); } }

		public ReplayKeyPoint(Entity[] data, KeyPointType type, int id, ActivePlayer player)
		{
			if(data != null)
				Data = Helper.DeepClone(data);
			Type = type;
			Id = id;
		    Player = player;
		}

		public override string ToString()
		{
		    string additionalInfo = "";
            if (Type == KeyPointType.Attack)
            {
                var attackerId = Data[0].GetTag(GAME_TAG.PROPOSED_ATTACKER);
                var attackerCardId = Data.First(x => x.Id == attackerId).CardId;
                if (!string.IsNullOrEmpty(attackerCardId))
                    additionalInfo += Game.GetCardFromId(attackerCardId).LocalizedName;
                
                additionalInfo += " -> ";
                
                var defenderId = Data[0].GetTag(GAME_TAG.PROPOSED_DEFENDER);
                var defenderCardId = Data.First(x => x.Id == defenderId).CardId;
                if (!string.IsNullOrEmpty(defenderCardId))
                    additionalInfo += Game.GetCardFromId(defenderCardId).LocalizedName;
            }
			else if(Type == KeyPointType.PlaySpell)
			{
				var entity = Data.First(x => x.Id == Id);
				if(!string.IsNullOrEmpty(entity.CardId))
					additionalInfo += Game.GetCardFromId(entity.CardId).LocalizedName;

				additionalInfo += " -> ";

				var targetId = entity.GetTag(GAME_TAG.CARD_TARGET);
				var targetCardId = Data.First(x => x.Id == targetId).CardId;
				if(!string.IsNullOrEmpty(targetCardId))
					additionalInfo += Game.GetCardFromId(targetCardId).LocalizedName;
			}
            else
            {
                
                var entityCardId = Data.First(x => x.Id == Id).CardId;
                if (!string.IsNullOrEmpty(entityCardId))
                    additionalInfo = Game.GetCardFromId(entityCardId).LocalizedName;
                else
                    additionalInfo = "Entity " + Id;
            }
			return string.Format("({0}) - {1} {2} \n[{3}]", Turn, Player, Type, additionalInfo);
		}
	}
}
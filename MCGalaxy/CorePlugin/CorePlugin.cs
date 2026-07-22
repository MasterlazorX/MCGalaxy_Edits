/*
    Copyright 2015-2024 MCGalaxy
        
    Dual-licensed under the Educational Community License, Version 2.0 and
    the GNU General Public License, Version 3 (the "Licenses"); you may
    not use this file except in compliance with the Licenses. You may
    obtain a copy of the Licenses at
    
    https://opensource.org/license/ecl-2-0/
    https://www.gnu.org/licenses/gpl-3.0.html
    
    Unless required by applicable law or agreed to in writing,
    software distributed under the Licenses are distributed on an "AS IS"
    BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
    or implied. See the Licenses for the specific language governing
    permissions and limitations under the Licenses.
*/
using System;
using MCGalaxy.Events;
using MCGalaxy.Events.EconomyEvents;
using MCGalaxy.Events.EntityEvents;
using MCGalaxy.Events.LevelEvents;
using MCGalaxy.Events.PlayerEvents;
using MCGalaxy.Events.ServerEvents;

namespace MCGalaxy.Core {

    public sealed class CorePlugin : Plugin {
        public override string name { get { return "CorePlugin"; } }

        public override void Load(bool startup) {
            OnPlayerConnectEvent.Register(ConnectHandler.HandleConnect, Priority.Critical);
            OnPlayerCommandEvent.Register(ChatHandler.HandleCommand, Priority.Critical);
            OnChatEvent.Register(ChatHandler.HandleOnChat, Priority.Critical);
            OnPlayerStartConnectingEvent.Register(ConnectingHandler.HandleConnecting, Priority.Critical);
            
            OnSentMapEvent.Register(MiscHandlers.HandleSentMap, Priority.Critical);
            OnPlayerMoveEvent.Register(MiscHandlers.HandlePlayerMove, Priority.Critical);
            OnPlayerClickEvent.Register(MiscHandlers.HandlePlayerClick, Priority.Critical);
            OnChangedZoneEvent.Register(MiscHandlers.HandleChangedZone, Priority.Critical);
            
            OnEcoTransactionEvent.Register(EcoHandlers.HandleEcoTransaction, Priority.Critical);
            OnModActionEvent.Register(ModActionHandler.HandleModAction, Priority.Critical);
            OnPluginMessageReceivedEvent.Register(Network.SurvivalNet.HandlePluginMessage, Priority.Critical);
            OnPlayerDiedEvent.Register(Network.SurvivalNet.OnPlayerDied, Priority.Low);
            OnPlayerDyingEvent.Register(Network.SurvivalNet.OnPlayerDying, Priority.Low);
            OnJoinedLevelEvent.Register(Network.SurvivalNet.OnJoinedLevel, Priority.Low);
            OnBlockChangingEvent.Register(Network.SurvivalInventory.OnBlockChanging, Priority.Low);
            OnEntitySpawnedEvent.Register(Network.SurvivalInventory.OnEntitySpawned, Priority.Low);
            OnJoiningLevelEvent.Register(Network.SurvivalInventory.OnJoiningLevel, Priority.Low);
            OnPlayerDisconnectEvent.Register(Network.SurvivalInventory.OnPlayerDisconnect, Priority.Low);
            OnLevelLoadedEvent.Register(Network.SurvivalBlocks.OnLevelLoaded, Priority.Low);
            Network.SurvivalNet.Start();
        }
        
        public override void Unload(bool shutdown) {
            OnPlayerConnectEvent.Unregister(ConnectHandler.HandleConnect);
            OnPlayerCommandEvent.Unregister(ChatHandler.HandleCommand);
            OnChatEvent.Unregister(ChatHandler.HandleOnChat);
            OnPlayerStartConnectingEvent.Unregister(ConnectingHandler.HandleConnecting);
            
            OnSentMapEvent.Unregister(MiscHandlers.HandleSentMap);
            OnPlayerMoveEvent.Unregister(MiscHandlers.HandlePlayerMove);
            OnPlayerClickEvent.Unregister(MiscHandlers.HandlePlayerClick);
            OnChangedZoneEvent.Unregister(MiscHandlers.HandleChangedZone);
            
            OnEcoTransactionEvent.Unregister(EcoHandlers.HandleEcoTransaction);
            OnModActionEvent.Unregister(ModActionHandler.HandleModAction);
            OnPluginMessageReceivedEvent.Unregister(Network.SurvivalNet.HandlePluginMessage);
            OnPlayerDiedEvent.Unregister(Network.SurvivalNet.OnPlayerDied);
            OnPlayerDyingEvent.Unregister(Network.SurvivalNet.OnPlayerDying);
            OnJoinedLevelEvent.Unregister(Network.SurvivalNet.OnJoinedLevel);
            OnBlockChangingEvent.Unregister(Network.SurvivalInventory.OnBlockChanging);
            OnJoiningLevelEvent.Unregister(Network.SurvivalInventory.OnJoiningLevel);
            OnPlayerDisconnectEvent.Unregister(Network.SurvivalInventory.OnPlayerDisconnect);
            OnLevelLoadedEvent.Unregister(Network.SurvivalBlocks.OnLevelLoaded);
            Network.SurvivalNet.Stop();
        }
    }
}

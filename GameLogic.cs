using System;
using System.Collections.Generic;
using System.Linq;
using Lidgren.Network;
using System.IO;
using System.Numerics;

namespace StickFightLanServer
{
    public struct ServerMapInfo
    {
        public int MapId;
        public string MapName;
        public byte MapTypeForClient;

        public ServerMapInfo(int id, string name, byte type = 0)
        {
            MapId = id;
            MapName = name;
            MapTypeForClient = type;
        }
    }

    public class GameLogic
    {
        private GameServer serverInstance;
        private List<ServerWeapon> worldWeapons;
        private List<ServerMapInfo> availableMaps;
        private int currentMapIndex = -1;
        private byte lastWinnerIndex = 255;
        private Random spawnRandom = new Random();

        private float timeSinceLastStateBroadcast = 0f;
        private const float PlayerStateBroadcastInterval = 1f / 20f;

        public GameLogic(GameServer server)
        {
            serverInstance = server;
            worldWeapons = new List<ServerWeapon>();
            availableMaps = new List<ServerMapInfo>();
            InitializeDefaultMaps();
            SelectNextMap();
            InitializeWorldWeapons();
        }

        private void InitializeDefaultMaps()
        {
            availableMaps.Add(new ServerMapInfo(1, "StickFightMap", 0));
            availableMaps.Add(new ServerMapInfo(2, "AnotherMap", 0));
            availableMaps.Add(new ServerMapInfo(3, "ThirdMap", 0));
            
            serverInstance.Log($"[GameLogic] Initialized {availableMaps.Count} maps.");
            if (availableMaps.Count == 0)
            {
                serverInstance.Log("[GameLogic] WARNING: No maps configured. Map changing will not work.");
            }
        }
        
        private ServerMapInfo GetCurrentMapInfo()
        {
            if (currentMapIndex >= 0 && currentMapIndex < availableMaps.Count)
            {
                return availableMaps[currentMapIndex];
            }
            if (availableMaps.Count > 0)
            {
                currentMapIndex = 0;
                return availableMaps[0]; 
            }
            serverInstance.Log("[GameLogic] CRITICAL: No maps available and GetCurrentMapInfo was called!");
            return new ServerMapInfo(-1, "Unknown/NoMap", 0); 
        }

        private void SelectNextMap()
        {
            if (availableMaps.Count == 0)
            {
                currentMapIndex = -1;
                serverInstance.Log("[GameLogic] No maps available to select.");
                return;
            }
            currentMapIndex = (currentMapIndex + 1) % availableMaps.Count;
            serverInstance.Log($"[GameLogic] Next map selected: {GetCurrentMapInfo().MapName} (ID: {GetCurrentMapInfo().MapId})");
        }

        private void InitializeWorldWeapons()
        {
            worldWeapons.Clear(); 
            ServerMapInfo currentMap = GetCurrentMapInfo();
            serverInstance.Log($"[GameLogic] Initializing weapons for map: {currentMap.MapName} (ID: {currentMap.MapId})");

            if (currentMap.MapId == 1)
            {
                worldWeapons.Add(new ServerWeapon(101, 5.0f, 2.0f));
                worldWeapons.Add(new ServerWeapon(205, -3.0f, 1.5f));
            }
            else if (currentMap.MapId == 2)
            {
                worldWeapons.Add(new ServerWeapon(310, 0.0f, 10.0f));
                worldWeapons.Add(new ServerWeapon(102, 2.0f, 3.0f));
            }
            else
            {
                 worldWeapons.Add(new ServerWeapon(101, 1.0f, 1.0f)); 
                 worldWeapons.Add(new ServerWeapon(201, -1.0f, 1.0f));
            }
            
            serverInstance.Log("[GameLogic] Initialized world weapons. Count: " + worldWeapons.Count);
        }

        public void HandleClientReadyUp(ConnectedClient client, byte[] payload)
        {
            if (client != null)
            {
                client.IsReady = true;
                serverInstance.Log($"[GameLogic] Client {client.PlayerName} (Index: {client.PlayerServerIndex}) is now READY.");
                CheckAndStartMatch();
            }
        }

        public void CheckAndStartMatch()
        {
            if (serverInstance.ConnectedClients.Count == 0) return;

            bool allReady = true;
            foreach (var clientEntry in serverInstance.ConnectedClients)
            {
                ConnectedClient client = clientEntry.Value;
                if (client == null || !client.IsInitialized) 
                {
                    allReady = false; 
                    break;
                }
                if (!client.IsReady)
                {
                    allReady = false;
                    break;
                }
            }

            if (allReady)
            {
                serverInstance.Log("[GameLogic] All players are READY! Starting match sequence.");
                
                SelectNextMap(); 
                ServerMapInfo mapToLoad = GetCurrentMapInfo();

                if (mapToLoad.MapId == -1)
                {
                    serverInstance.Log("[GameLogic] ERROR: Cannot start match, no valid map selected.");
                    return;
                }

                serverInstance.Log($"[GameLogic] Sending MapChange for map: {mapToLoad.MapName} (ID: {mapToLoad.MapId})");
                using (MemoryStream ms = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(ms))
                    {
                        writer.Write(mapToLoad.MapId);      
                        writer.Write(lastWinnerIndex);     
                    }
                    byte[] mapChangePayload = ms.ToArray();
                    byte[] mapChangePacket = PacketHandler.WriteMessageBuffer(mapChangePayload, MsgType.MapChange);
                    serverInstance.BroadcastMessage(mapChangePacket, NetDeliveryMethod.ReliableOrdered);
                }
                
                InitializeWorldWeapons();
                BroadcastWorldWeaponState();

                serverInstance.Log("[GameLogic] Broadcasting StartMatch.");
                byte[] startMatchPacket = PacketHandler.WriteMessageBuffer(new byte[0], MsgType.StartMatch);
                serverInstance.BroadcastMessage(startMatchPacket, NetDeliveryMethod.ReliableOrdered);
                
                foreach (var clientEntry in serverInstance.ConnectedClients)
                { 
                    ConnectedClient client = clientEntry.Value;
                    if (client != null)
                    {
                        client.IsReady = false;
                        client.IsSpawned = false; 
                    }
                }
                lastWinnerIndex = 255; 
            }
            else
            {
                serverInstance.Log("[GameLogic] Not all players are ready or initialized yet.");
            }
        }

        private void BroadcastWorldWeaponState()
        {
            ServerMapInfo currentMap = GetCurrentMapInfo();
            serverInstance.Log($"[GameLogic] Broadcasting current world weapon states for map {currentMap.MapName}.");
            foreach (var weapon in worldWeapons)
            {
                if (!weapon.IsHeld)
                {
                    using (MemoryStream payloadStream = new MemoryStream())
                    {
                        using (BinaryWriter writer = new BinaryWriter(payloadStream))
                        {
                            writer.Write(weapon.WeaponID);
                            writer.Write(weapon.PositionX);
                            writer.Write(weapon.PositionY);
                        }
                        byte[] weaponSpawnedPayload = payloadStream.ToArray();
                        byte[] weaponSpawnedPacket = PacketHandler.WriteMessageBuffer(weaponSpawnedPayload, MsgType.WeaponSpawned);
                        serverInstance.BroadcastMessage(weaponSpawnedPacket, NetDeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }

        public void HandleClientInit(ConnectedClient client, byte[] payload)
        {
            if (client == null)
            {
                serverInstance.Log("[GameLogic] HandleClientInit received null client.");
                return;
            }

            if (payload != null && payload.Length > 0)
            {
                try
                {
                    string receivedName = System.Text.Encoding.UTF8.GetString(payload);
                    if (!string.IsNullOrWhiteSpace(receivedName))
                    {
                        client.PlayerName = receivedName.Trim();
                        serverInstance.Log($"[GameLogic] Client {client.RemoteUniqueIdentifier} set name to: {client.PlayerName}");
                    }
                }
                catch (Exception ex)
                {
                    serverInstance.Log($"[GameLogic] Error parsing player name from ClientInit payload for {client.RemoteUniqueIdentifier}: {ex.Message}. Keeping default name: {client.PlayerName}");
                }
            }
            
            client.IsInitialized = true;
            serverInstance.Log($"[GameLogic] Client {client.PlayerName} (Index: {client.PlayerServerIndex}) has INITIALIZED.");
            SendInitialGameState(client);
        }

        public void SendInitialGameState(ConnectedClient newClient)
        {
            if (newClient == null || newClient.Connection == null)
            {
                serverInstance.Log("[GameLogic] SendInitialGameState received null client or connection.");
                return;
            }

            serverInstance.Log($"[GameLogic] Preparing to send initial game state to {newClient.PlayerName} (Index: {newClient.PlayerServerIndex}).");

            ServerMapInfo currentMap = GetCurrentMapInfo();
            if (currentMap.MapId != -1)
            {
                serverInstance.Log($"[GameLogic] Sending MapChange to {newClient.PlayerName}. MapID: {currentMap.MapId}, Winner: {lastWinnerIndex}");
                using (MemoryStream ms = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(ms))
                    {
                        writer.Write(currentMap.MapId);
                        writer.Write(lastWinnerIndex);
                    }
                    byte[] mapChangePayload = ms.ToArray();
                    byte[] mapChangePacket = PacketHandler.WriteMessageBuffer(mapChangePayload, MsgType.MapChange);
                    serverInstance.SendMessageToClient(newClient, mapChangePacket, NetDeliveryMethod.ReliableOrdered);
                }
            }
            else
            {
                 serverInstance.Log($"[GameLogic] No current map selected, not sending MapChange to new client {newClient.PlayerName}.");
            }

            serverInstance.Log($"[GameLogic] Informing {newClient.PlayerName} about other existing players.");
            foreach (var existingClientEntry in serverInstance.GetAllClients()) 
            {
                ConnectedClient existingClient = existingClientEntry.Value;
                if (existingClient.RemoteUniqueIdentifier == newClient.RemoteUniqueIdentifier) continue;
                
                if (existingClient.IsInitialized)
                {
                    if (existingClient.IsSpawned)
                    {
                        serverInstance.Log($"[GameLogic] Existing player {existingClient.PlayerName} (Index {existingClient.PlayerServerIndex}) is Initialized and Spawned. Sending ClientSpawned to {newClient.PlayerName}.");
                        using (var stream = new MemoryStream())
                        {
                            using (var writer = new BinaryWriter(stream))
                            {
                                writer.Write(existingClient.PlayerServerIndex);
                                writer.Write(existingClient.Position.X); 
                                writer.Write(existingClient.Position.Y); 
                                writer.Write(existingClient.PlayerName ?? string.Empty);
                                writer.Write((int)existingClient.PlayerServerIndex);
                                
                                writer.Write(existingClient.Velocity.X);
                                writer.Write(existingClient.Velocity.Y);
                                writer.Write(existingClient.AimDirection);
                                writer.Write(existingClient.IsJumping);
                                writer.Write(existingClient.IsGrounded);
                                writer.Write(existingClient.HoldingWeaponID); 
                                writer.Write(existingClient.Health); 
                            }
                            byte[] spawnPayload = stream.ToArray();
                            byte[] spawnPacket = PacketHandler.WriteMessageBuffer(spawnPayload, MsgType.ClientSpawned);
                            serverInstance.SendMessageToClient(newClient, spawnPacket, NetDeliveryMethod.ReliableOrdered);
                            serverInstance.Log($"[GameLogic] To {newClient.PlayerName}: Sent existing player ClientSpawned P{existingClient.PlayerServerIndex} ('{existingClient.PlayerName}'). Payload: {spawnPayload.Length}");
                        }
                    }
                    else
                    {
                        serverInstance.Log($"[GameLogic] Existing player {existingClient.PlayerName} (Index {existingClient.PlayerServerIndex}) is Initialized but NOT Spawned. Sending ClientJoined to {newClient.PlayerName}.");
                        using (MemoryStream payloadStream = new MemoryStream())
                        {
                            using (BinaryWriter writer = new BinaryWriter(payloadStream))
                            {
                                writer.Write(existingClient.PlayerServerIndex);
                                writer.Write(existingClient.PlayerName ?? string.Empty);
                            }
                            byte[] clientJoinedPayload = payloadStream.ToArray();
                            byte[] clientJoinedPacket = PacketHandler.WriteMessageBuffer(clientJoinedPayload, MsgType.ClientJoined);
                            serverInstance.SendMessageToClient(newClient, clientJoinedPacket, NetDeliveryMethod.ReliableOrdered);
                        }
                    }
                }
            }

            serverInstance.Log($"[GameLogic] Sending world weapon states to {newClient.PlayerName} for map {currentMap.MapName}.");
            foreach (var weapon in worldWeapons) 
            {
                if (!weapon.IsHeld) 
                {
                    using (MemoryStream payloadStream = new MemoryStream())
                    {
                        using (BinaryWriter writer = new BinaryWriter(payloadStream))
                        {
                            writer.Write(weapon.WeaponID);
                            writer.Write(weapon.PositionX);
                            writer.Write(weapon.PositionY);
                        }
                        byte[] weaponSpawnedPayload = payloadStream.ToArray();
                        byte[] weaponSpawnedPacket = PacketHandler.WriteMessageBuffer(weaponSpawnedPayload, MsgType.WeaponSpawned);
                        serverInstance.SendMessageToClient(newClient, weaponSpawnedPacket, NetDeliveryMethod.ReliableOrdered);
                    }
                }
            }
            serverInstance.Log($"[GameLogic] Finished sending initial game state to {newClient.PlayerName}.");
        }

        private Vector2 GetRandomSpawnPoint()
        {
            float x = (float)(spawnRandom.NextDouble() * 10.0 - 5.0);
            float y = (float)(spawnRandom.NextDouble() * 2.0 + 1.0);
            return new Vector2(x, y);
        }

        public void HandlePlayerSpawnRequest(NetIncomingMessage msg, ConnectedClient requestingClient)
        {
            if (requestingClient.PlayerServerIndex == 255) {
                serverInstance.Log($"[GameLogic] Player {requestingClient.PlayerName} with invalid index 255 tried to spawn.");
                return;
            }

            if (!requestingClient.IsInitialized) {
                serverInstance.Log($"[GameLogic] Player {requestingClient.PlayerName} (Index: {requestingClient.PlayerServerIndex}) requested spawn but is not initialized.");
                return;
            }
            
            Vector2 spawnPosition = GetRandomSpawnPoint(); 
            requestingClient.Position = spawnPosition;
            requestingClient.Velocity = Vector2.Zero;
            requestingClient.AimDirection = 0f;
            requestingClient.IsJumping = false;
            requestingClient.IsGrounded = true;
            requestingClient.Health = 100f;
            requestingClient.HoldingWeaponID = -1;
            requestingClient.IsSpawned = true;

            serverInstance.Log($"[GameLogic] Player {requestingClient.PlayerName} (Index: {requestingClient.PlayerServerIndex}) SPAWNED at {spawnPosition}. Health: {requestingClient.Health}");

            using (MemoryStream payloadStream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(payloadStream))
            {
                writer.Write(requestingClient.PlayerServerIndex);
                writer.Write(requestingClient.Position.X);
                writer.Write(requestingClient.Position.Y);
                writer.Write(requestingClient.PlayerName ?? string.Empty);
                writer.Write((int)requestingClient.PlayerServerIndex);
                
                writer.Write(requestingClient.Velocity.X);
                writer.Write(requestingClient.Velocity.Y);
                writer.Write(requestingClient.AimDirection);
                writer.Write(requestingClient.IsJumping);
                writer.Write(requestingClient.IsGrounded);
                writer.Write(requestingClient.HoldingWeaponID); 
                writer.Write(requestingClient.Health);

                byte[] spawnPayload = payloadStream.ToArray();
                byte[] spawnPacket = PacketHandler.WriteMessageBuffer(spawnPayload, MsgType.ClientSpawned);
                serverInstance.BroadcastMessage(spawnPacket, NetDeliveryMethod.ReliableOrdered);
                serverInstance.Log($"[GameLogic] Broadcasted ClientSpawned for P{requestingClient.PlayerServerIndex} ('{requestingClient.PlayerName}'). Payload: {spawnPayload.Length} bytes.");
            }
        }

        public void HandlePlayerUpdate(NetIncomingMessage msg, ConnectedClient requestingClient)
        {
            if (!requestingClient.IsSpawned || requestingClient.PlayerServerIndex == 255) return;

            requestingClient.Position = new Vector2(msg.ReadFloat(), msg.ReadFloat());
            requestingClient.Velocity = new Vector2(msg.ReadFloat(), msg.ReadFloat());
            requestingClient.AimDirection = msg.ReadFloat();
            requestingClient.IsJumping = msg.ReadBoolean();
            requestingClient.IsGrounded = msg.ReadBoolean();
            requestingClient.HoldingWeaponID = msg.ReadInt32();

            requestingClient.LastMessageTimestamp = DateTime.UtcNow;
        }

        public void HandleClientRequestWeaponPickUp(NetIncomingMessage msg, ConnectedClient client)
        {
            int weaponIdToPickUp = msg.ReadInt32();
            ServerWeapon weapon = worldWeapons.FirstOrDefault(w => w.WeaponID == weaponIdToPickUp && !w.IsHeld);
            if (weapon != null)
            {
                weapon.IsHeld = true;
                weapon.HeldByPlayerIndex = client.PlayerServerIndex;
                client.HoldingWeaponID = weapon.WeaponID;

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write(client.PlayerServerIndex);
                    writer.Write(weapon.WeaponID);
                    byte[] payload = ms.ToArray();
                    byte[] packet = PacketHandler.WriteMessageBuffer(payload, MsgType.WeaponWasPickedUp);
                    serverInstance.BroadcastMessage(packet, NetDeliveryMethod.ReliableOrdered);
                }
                serverInstance.Log($"Player {client.PlayerName} picked up weapon {weaponIdToPickUp}");
            }
        }

        public void HandleClientRequestWeaponDrop(NetIncomingMessage msg, ConnectedClient client)
        {
            if (client.HoldingWeaponID != -1)
            {
                ServerWeapon weapon = worldWeapons.FirstOrDefault(w => w.WeaponID == client.HoldingWeaponID && w.IsHeld && w.HeldByPlayerIndex == client.PlayerServerIndex);
                if (weapon != null && client.LastKnownPosition.HasValue)
                {
                    weapon.IsHeld = false;
                    weapon.HeldByPlayerIndex = 255;
                    weapon.PositionX = client.LastKnownPosition.Value.X;
                    weapon.PositionY = client.LastKnownPosition.Value.Y;
                    client.HoldingWeaponID = -1;

                    using (MemoryStream ms = new MemoryStream())
                    using (BinaryWriter writer = new BinaryWriter(ms))
                    {
                        writer.Write(client.PlayerServerIndex);
                        writer.Write(weapon.WeaponID);
                        writer.Write(weapon.PositionX);
                        writer.Write(weapon.PositionY);
                        byte[] payload = ms.ToArray();
                        byte[] packet = PacketHandler.WriteMessageBuffer(payload, MsgType.WeaponDropped);
                        serverInstance.BroadcastMessage(packet, NetDeliveryMethod.ReliableOrdered);
                    }
                    serverInstance.Log($"Player {client.PlayerName} dropped weapon {weapon.WeaponID}");
                }
            }
        }

        public void ProcessPlayerTalk(NetIncomingMessage msg, ConnectedClient client)
        {
            string text = msg.ReadString();
            byte targetID = msg.ReadByte();
            serverInstance.Log($"Player {client.PlayerName} talked: {text}, target: {targetID}");

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(client.PlayerServerIndex);
                writer.Write(text);
                writer.Write(targetID);
                byte[] payload = ms.ToArray();
                byte[] packet = PacketHandler.WriteMessageBuffer(payload, MsgType.PlayerTalked);
                serverInstance.BroadcastMessage(packet, NetDeliveryMethod.ReliableOrdered);
            }
        }

        public void HandlePlayerFallOut(NetIncomingMessage msg, ConnectedClient client)
        {
            if (!client.IsSpawned) return;
            client.IsSpawned = false;
            client.Health = 0;
            serverInstance.Log($"Player {client.PlayerName} (ID: {client.PlayerServerIndex}) fell out / died.");

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(client.PlayerServerIndex);
                byte[] payload = ms.ToArray();
                byte[] packet = PacketHandler.WriteMessageBuffer(payload, MsgType.PlayerFallOut);
                serverInstance.BroadcastMessage(packet, NetDeliveryMethod.ReliableOrdered);
            }
            CheckForWinnerAndEndRound();
        }

        private void CheckForWinnerAndEndRound()
        {
            List<ConnectedClient> alivePlayers = serverInstance.GetAllClients().Values.Where(c => c.IsSpawned && c.Health > 0).ToList();
            if (alivePlayers.Count == 1 && serverInstance.GetAllClients().Count > 1)
            {
                lastWinnerIndex = alivePlayers[0].PlayerServerIndex;
                serverInstance.Log($"Player {alivePlayers[0].PlayerName} (ID: {lastWinnerIndex}) is the winner of the round!");
                EndRoundAndSelectNextMap();
            }
            else if (alivePlayers.Count == 0 && serverInstance.GetAllClients().Count > 0)
            {
                lastWinnerIndex = 255;
                serverInstance.Log("Round ended in a draw or all players out.");
                EndRoundAndSelectNextMap();
            }
        }

        private void EndRoundAndSelectNextMap()
        {
            serverInstance.Log("Ending round...");
            SelectNextMap();
            
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(GetCurrentMapInfo().MapId);
                byte[] payload = ms.ToArray();
                byte[] packet = PacketHandler.WriteMessageBuffer(payload, MsgType.MapChange);
                serverInstance.BroadcastMessage(packet, NetDeliveryMethod.ReliableOrdered);
            }
            serverInstance.Log($"Sent MapChange (ID: {GetCurrentMapInfo().MapId}) to all clients for new round.");
            
            foreach (var client in serverInstance.GetAllClients().Values)
            {
                client.IsSpawned = false;
            }
            InitializeWorldWeapons();
        }

        public void Update(float deltaTime)
        {
            timeSinceLastStateBroadcast += deltaTime;
            if (timeSinceLastStateBroadcast >= PlayerStateBroadcastInterval)
            {
                BroadcastAllPlayerStates();
                timeSinceLastStateBroadcast = 0f;
            }
        }
        
        private void BroadcastAllPlayerStates()
        {
            if (serverInstance.GetAllClients().Count == 0) return;

            foreach (var clientEntry in serverInstance.GetAllClients())
            {
                ConnectedClient client = clientEntry.Value;
                if (client.IsSpawned)
                {
                    using (MemoryStream ms = new MemoryStream())
                    using (BinaryWriter writer = new BinaryWriter(ms))
                    {
                        writer.Write(client.PlayerServerIndex);
                        writer.Write(client.Position.X);
                        writer.Write(client.Position.Y);
                        writer.Write(client.Velocity.X);
                        writer.Write(client.Velocity.Y);
                        writer.Write(client.AimDirection);
                        writer.Write(client.IsJumping);
                        writer.Write(client.IsGrounded);
                        writer.Write(client.HoldingWeaponID);
                        
                        byte[] payload = ms.ToArray();
                        byte[] packet = PacketHandler.WriteMessageBuffer(payload, MsgType.PlayerUpdate);
                        
                        serverInstance.BroadcastMessage(packet, NetDeliveryMethod.UnreliableSequenced, 0);
                    }
                }
            }
        }

        public void HandleRequestWeaponThrow(NetIncomingMessage msg, ConnectedClient client)
        {
            if (client.HoldingWeaponID == -1) return;
            int thrownWeaponId = client.HoldingWeaponID;
            ServerWeapon weapon = worldWeapons.FirstOrDefault(w => w.WeaponID == thrownWeaponId && w.IsHeld && w.HeldByPlayerIndex == client.PlayerServerIndex);

            if (weapon != null && client.LastKnownPosition.HasValue)
            {
                client.HoldingWeaponID = -1;
                weapon.IsHeld = false;
                weapon.HeldByPlayerIndex = 255;
                weapon.PositionX = client.LastKnownPosition.Value.X + (spawnRandom.NextSingle() * 2f - 1f);
                weapon.PositionY = client.LastKnownPosition.Value.Y + (spawnRandom.NextSingle() * 2f - 1f);

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write(client.PlayerServerIndex);
                    writer.Write(thrownWeaponId);
                    writer.Write(weapon.PositionX);
                    writer.Write(weapon.PositionY);
                    byte[] payload = ms.ToArray();
                    byte[] packet = PacketHandler.WriteMessageBuffer(payload, MsgType.WeaponThrown);
                    serverInstance.BroadcastMessage(packet, NetDeliveryMethod.ReliableOrdered);
                }
                serverInstance.Log($"Player {client.PlayerName} threw weapon {thrownWeaponId}");
            }
        }

        public void ClientDisconnected(ConnectedClient client)
        {
            if (client != null)
            {
                serverInstance.Log($"[GameLogic] Handling disconnect for client: {client.PlayerName} (Index: {client.PlayerServerIndex})");
                // Additional logic if a player in an active game leaves, e.g., check for winner
                // For now, just log. The server already broadcasts a general disconnect.

                // Example of how BroadcastMessageToAllExcept might be used if it existed and was needed here:
                // NetOutgoingMessage om = serverInstance.CreateMessage();
                // om.Write((byte)MsgType.PlayerLeft); // Example message
                // om.Write(client.PlayerServerIndex);
                // serverInstance.BroadcastMessageToAllExcept(om, client.Connection, NetDeliveryMethod.ReliableOrdered, 0);
            }
        }

        // Stub for HandleClientRequestingIndex
        public void HandleClientRequestingIndex(ConnectedClient client, byte[] payload)
        {
            serverInstance.Log($"[GameLogic] Received ClientRequestingIndex from {client.PlayerName}. Payload length: {(payload?.Length ?? 0)}");
            // Logic to handle client requesting its index, potentially re-sending ClientAccepted if needed.
            // For now, we assume the client already has its index from the initial ClientAccepted message.
            // If you need to resend, you can do something like:
            // byte[] clientAcceptedPayload = new byte[] { client.PlayerServerIndex };
            // byte[] clientAcceptedPacket = PacketHandler.WriteMessageBuffer(clientAcceptedPayload, MsgType.ClientAccepted);
            // serverInstance.SendMessageToClient(client, clientAcceptedPacket, NetDeliveryMethod.ReliableOrdered);
        }

        // Stub for HandlePlayerTookDamage
        public void HandlePlayerTookDamage(NetIncomingMessage msg, ConnectedClient client)
        {
            byte victimIndex = msg.ReadByte();
            float damage = msg.ReadFloat();
            byte attackerIndex = msg.ReadByte(); // Assuming attacker index is also sent

            serverInstance.Log($"[GameLogic] Player {victimIndex} took {damage} damage from player {attackerIndex}.");
            ConnectedClient victimClient = serverInstance.GetClientByIndex(victimIndex);
            if (victimClient != null)
            {
                victimClient.Health -= damage;
                serverInstance.Log($"[GameLogic] Player {victimClient.PlayerName}'s health is now {victimClient.Health}");

                // Broadcast the damage event to all clients so they can update UI or effects
                using (MemoryStream ms = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(ms))
                    {
                        writer.Write(victimIndex);
                        writer.Write(damage);
                        writer.Write(attackerIndex);
                        writer.Write(victimClient.Health); // Send current health
                    }
                    byte[] damagePayload = ms.ToArray();
                    byte[] damagePacket = PacketHandler.WriteMessageBuffer(damagePayload, MsgType.PlayerTookDamage);
                    serverInstance.BroadcastMessage(damagePacket, NetDeliveryMethod.ReliableOrdered);
                }

                if (victimClient.Health <= 0)
                {
                    serverInstance.Log($"[GameLogic] Player {victimClient.PlayerName} has died.");
                    // Handle player death, e.g., respawn logic, notify clients, check for round end
                }
            }
        }

        // Stub for HandlePlayerReady
        public void HandlePlayerReady(NetIncomingMessage msg, ConnectedClient client)
        {
             if (client != null)
            {
                // Optionally read data from msg if PlayerReady carries a payload
                // For example: client.SelectedCharacter = msg.ReadByte();

                client.IsReady = true;
                serverInstance.Log($"[GameLogic] Client {client.PlayerName} (Index: {client.PlayerServerIndex}) is now READY (via PlayerReady message).");
                
                // Notify other clients that this player is ready
                using (MemoryStream ms = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(ms))
                    {
                        writer.Write(client.PlayerServerIndex);
                        writer.Write(client.IsReady); 
                    }
                    byte[] readyPayload = ms.ToArray();
                    byte[] readyPacket = PacketHandler.WriteMessageBuffer(readyPayload, MsgType.ClientReadyUp); // Use existing ClientReadyUp or PlayerReady type
                    serverInstance.BroadcastMessage(readyPacket, NetDeliveryMethod.ReliableOrdered);
                }
                CheckAndStartMatch();
            }
        }
    }
} 
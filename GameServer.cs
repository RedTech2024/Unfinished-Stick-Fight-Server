using System;
using System.Collections.Generic;
using System.Threading;
using Lidgren.Network;
using System.Linq;

namespace StickFightLanServer
{
    public class GameServer
    {
        private NetServer server;
        private Thread serverThread;
        private bool isRunning = false;
        private GameLogic gameLogicHandler;
        
        public Dictionary<long, ConnectedClient> ConnectedClients { get; private set; }
        private byte nextPlayerServerIndex = 0;
        private const int MaxPlayers = 4;
        private List<NetConnection> pendingConnections;

        public GameServer()
        {
            ConnectedClients = new Dictionary<long, ConnectedClient>();
            pendingConnections = new List<NetConnection>();
            gameLogicHandler = new GameLogic(this);
        }

        public void Start(int port = 1337)
        {
            if (isRunning) return;

            NetPeerConfiguration config = new NetPeerConfiguration("StickFight 1.0")
            {
                Port = port
            };
            config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
            config.EnableMessageType(NetIncomingMessageType.StatusChanged);
            config.EnableMessageType(NetIncomingMessageType.Data);
            config.EnableMessageType(NetIncomingMessageType.WarningMessage);
            config.EnableMessageType(NetIncomingMessageType.ErrorMessage);
            config.MaximumConnections = MaxPlayers * 2;
            config.AcceptIncomingConnections = true;
            config.ConnectionTimeout = 10;

            server = new NetServer(config);
            try
            {
                server.Start();
                isRunning = true;
                Console.WriteLine($"Server started on port {port}. Waiting for connections...");

                serverThread = new Thread(ServerLoop);
                serverThread.IsBackground = true;
                serverThread.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server start failed: {ex.Message}");
                isRunning = false;
            }
        }

        private void ServerLoop()
        {
            while (isRunning)
            {
                NetIncomingMessage msg;
                while ((msg = server.ReadMessage()) != null)
                {
                    switch (msg.MessageType)
                    {
                        case NetIncomingMessageType.DiscoveryRequest:
                            Console.WriteLine("Received discovery request from: " + msg.SenderEndPoint);
                            NetOutgoingMessage response = server.CreateMessage();
                            response.Write("StickFightLANServer_V1");
                            server.SendDiscoveryResponse(response, msg.SenderEndPoint);
                            break;

                        case NetIncomingMessageType.ConnectionApproval:
                            if (ConnectedClients.Count < MaxPlayers)
                            {
                                msg.SenderConnection.Approve();
                                Console.WriteLine("Approved new connection from: " + msg.SenderEndPoint);
                            }
                            else
                            {
                                msg.SenderConnection.Deny("Server is full.");
                                Console.WriteLine("Denied connection from: " + msg.SenderEndPoint + " (Server full)");
                            }
                            break;

                        case NetIncomingMessageType.StatusChanged:
                            HandleStatusChanged(msg);
                            break;

                        case NetIncomingMessageType.Data:
                            HandleDataMessage(msg);
                            break;
                        
                        case NetIncomingMessageType.VerboseDebugMessage:
                        case NetIncomingMessageType.DebugMessage:
                            Console.WriteLine("[Lidgren DEBUG] " + msg.ReadString());
                            break;
                        case NetIncomingMessageType.WarningMessage:
                            Console.WriteLine("[Lidgren WARNING] " + msg.ReadString());
                            break;
                        case NetIncomingMessageType.ErrorMessage:
                            Console.WriteLine("[Lidgren ERROR] " + msg.ReadString());
                            break;

                        default:
                            Console.WriteLine("Unhandled type: " + msg.MessageType + " from " + msg.SenderEndPoint);
                            break;
                    }
                    server.Recycle(msg);
                }

                float deltaTime = 0.015f;
                if (gameLogicHandler != null)
                {
                    gameLogicHandler.Update(deltaTime);
                }

                Thread.Sleep(15);
            }
            Console.WriteLine("Server loop ended.");
        }

        private void HandleStatusChanged(NetIncomingMessage msg)
        {
            NetConnectionStatus status = (NetConnectionStatus)msg.ReadByte();
            string reason = msg.ReadString();
            long clientId = msg.SenderConnection.RemoteUniqueIdentifier;

            Console.WriteLine($"Status changed for {msg.SenderEndPoint} ({clientId}): {status} - Reason: {reason}");

            if (status == NetConnectionStatus.Connected)
            {
                if (ConnectedClients.Count >= MaxPlayers && !ConnectedClients.ContainsKey(clientId))
                {
                     msg.SenderConnection.Disconnect("Server is unexpectedly full after approval.");
                     return;
                }
                
                if (!ConnectedClients.ContainsKey(clientId))
                {
                    byte assignedIndex = GetNextPlayerServerIndex();
                    ConnectedClient newClient = new ConnectedClient(msg.SenderConnection, assignedIndex);
                    
                    ConnectedClients.Add(clientId, newClient);
                    Console.WriteLine($"Client {newClient.PlayerName} (Index: {newClient.PlayerServerIndex}) connected. Total clients: {ConnectedClients.Count}");
                    
                    byte[] clientAcceptedPayload = new byte[] { newClient.PlayerServerIndex, newClient.ColorID };
                    byte[] clientAcceptedPacket = PacketHandler.WriteMessageBuffer(clientAcceptedPayload, MsgType.ClientAccepted);
                    
                    NetOutgoingMessage om = server.CreateMessage();
                    om.Write(clientAcceptedPacket);
                    server.SendMessage(om, newClient.Connection, NetDeliveryMethod.ReliableOrdered);
                    Console.WriteLine($"[SERVER] Sent ClientAccepted to {newClient.Connection.RemoteEndPoint} with PlayerServerIndex: {newClient.PlayerServerIndex} and ColorID: {newClient.ColorID}.");
                }
            }
            else if (status == NetConnectionStatus.Disconnected)
            {
                if (ConnectedClients.Remove(clientId, out ConnectedClient clientLeft))
                {
                    Console.WriteLine($"[SERVER] Client {clientLeft.PlayerName} (Index: {clientLeft.PlayerServerIndex}, ID: {clientLeft.RemoteUniqueIdentifier}) disconnected. Total clients: {ConnectedClients.Count}");
                    
                    byte[] playerLeftPayload = new byte[] { clientLeft.PlayerServerIndex };
                    byte[] playerLeftPacket = PacketHandler.WriteMessageBuffer(playerLeftPayload, MsgType.KickPlayer);
                    
                    BroadcastMessage(playerLeftPacket, NetDeliveryMethod.ReliableOrdered);
                    Console.WriteLine($"[SERVER] Broadcasted PlayerLeft (KickPlayer msg) for Index: {clientLeft.PlayerServerIndex}");

                    if (gameLogicHandler != null)
                    {
                         gameLogicHandler.ClientDisconnected(clientLeft);
                    }
                }
            }
        }

        private byte GetNextPlayerServerIndex()
        {
            if (nextPlayerServerIndex < MaxPlayers)
            {
                return nextPlayerServerIndex++;
            }
            else
            {
                for (byte i = 0; i < MaxPlayers; i++)
                {
                    if (!ConnectedClients.Values.Any(c => c.PlayerServerIndex == i))
                    {
                        return i;
                    }
                }
            }
            Console.WriteLine("[SERVER ERROR] Could not assign player index, server might be full or in an inconsistent state.");
            return 255; 
        }

        public ConnectedClient? GetClientByIndex(byte playerIndex)
        {
            foreach (var clientEntry in ConnectedClients)
            {
                if (clientEntry.Value.PlayerServerIndex == playerIndex)
                {
                    return clientEntry.Value;
                }
            }
            return null;
        }

        private void HandleDataMessage(NetIncomingMessage msg)
        {
            if (!ConnectedClients.TryGetValue(msg.SenderConnection.RemoteUniqueIdentifier, out ConnectedClient senderClient))
            {
                Console.WriteLine("Received data from unknown/unregistered client: " + msg.SenderEndPoint);
                return;
            }

            byte[] rawData = msg.ReadBytes(msg.LengthBytes);
            MsgType msgType = PacketHandler.ParseMessage(rawData, out uint timestamp, out byte[] payload);

            switch (msgType)
            {
                case MsgType.Ping:
                    byte[] pingResponsePacket = PacketHandler.WriteMessageBuffer(payload, MsgType.PingResponse);
                    SendMessageToClient(senderClient, pingResponsePacket, NetDeliveryMethod.ReliableOrdered);
                    break;

                case MsgType.ClientRequestingAccepting:
                    Console.WriteLine($"Received ClientRequestingAccepting from {senderClient.PlayerName}. Connection already approved by Lidgren.");
                    break;

                case MsgType.ClientAccepted:
                     Console.WriteLine($"Warning: Received ClientAccepted message from client {senderClient.PlayerName}. This is normally server-sent.");
                    break;

                case MsgType.ClientInit:
                    gameLogicHandler.HandleClientInit(senderClient, payload);
                    AnnounceNewPlayerToExistingPlayers(senderClient);
                    gameLogicHandler.SendInitialGameState(senderClient);
                    break;

                case MsgType.ClientRequestingIndex:
                    gameLogicHandler.HandleClientRequestingIndex(senderClient, payload);
                    break;

                case MsgType.ClientReadyUp:
                    gameLogicHandler.HandleClientReadyUp(senderClient, payload);
                    break;

                case MsgType.PlayerUpdate:
                    gameLogicHandler.HandlePlayerUpdate(msg, senderClient);
                    break;

                case MsgType.PlayerSpawnRequest:
                     if (senderClient != null )
                    {
                        gameLogicHandler.HandlePlayerSpawnRequest(msg, senderClient);
                    }
                    break;

                case MsgType.PlayerTookDamage:
                    gameLogicHandler.HandlePlayerTookDamage(msg, senderClient);
                    break;
                
                case MsgType.PlayerFallOut:
                    gameLogicHandler.HandlePlayerFallOut(msg, senderClient);
                    break;

                case MsgType.PlayerTalked:
                    gameLogicHandler.ProcessPlayerTalk(msg, senderClient);
                    break;

                case MsgType.RequestingWeaponThrow:
                     gameLogicHandler.HandleRequestWeaponThrow(msg, senderClient);
                    break;
                
                case MsgType.ClientRequestWeaponDrop:
                    gameLogicHandler.HandleClientRequestWeaponDrop(msg, senderClient);
                    break;

                case MsgType.ClientRequestingWeaponPickUp:
                    gameLogicHandler.HandleClientRequestWeaponPickUp(msg, senderClient);
                    break;
                
                case MsgType.PlayerReady:
                    gameLogicHandler.HandlePlayerReady(msg, senderClient);
                    break;
              
                case MsgType.MapChange: 
                    Console.WriteLine($"Received MapChange request/notification from {senderClient.PlayerName}. Server should control map changes.");
                    break;


                default:
                    if (Enum.IsDefined(typeof(MsgType), msgType))
                    {
                        Console.WriteLine($"Received known but unhandled MsgType: {msgType} from client {senderClient.PlayerName}");
                    }
                    else
                    {
                        Console.WriteLine($"Received UNKNOWN MsgType value: {(byte)msgType} from client {senderClient.PlayerName}");
                    }
                    break;
            }
        }
        
        public void AnnounceClientJoined(ConnectedClient newClient)
        {
            if (newClient == null || !newClient.IsInitialized)
            {
                Console.WriteLine("[AnnounceClientJoined] New client is null or not initialized. Aborting announcement.");
                return;
            }

            Console.WriteLine($"[AnnounceClientJoined] Announcing new player {newClient.PlayerName} (Index: {newClient.PlayerServerIndex}) to existing players.");

            byte[] newPlayerPacket = null;
            using (System.IO.MemoryStream msNewPlayer = new System.IO.MemoryStream())
            {
                using (System.IO.BinaryWriter writerNewPlayer = new System.IO.BinaryWriter(msNewPlayer))
                {
                    writerNewPlayer.Write(newClient.PlayerServerIndex);
                    writerNewPlayer.Write(newClient.PlayerName ?? "Player");
                    writerNewPlayer.Write(newClient.ColorID);
                    writerNewPlayer.Write(newClient.IsReady);
                    writerNewPlayer.Write(newClient.IsSpawned);
                    writerNewPlayer.Write(newClient.Position.X);
                    writerNewPlayer.Write(newClient.Position.Y);
                    writerNewPlayer.Write(newClient.IsInitialized);
                }
                byte[] clientJoinedPayload = msNewPlayer.ToArray();
                newPlayerPacket = PacketHandler.WriteMessageBuffer(clientJoinedPayload, MsgType.ClientJoined);
            }

            foreach (var existingClientEntry in ConnectedClients)
            {
                ConnectedClient existingClient = existingClientEntry.Value;
                if (existingClient.Connection != null && existingClient.RemoteUniqueIdentifier != newClient.RemoteUniqueIdentifier && existingClient.IsInitialized)
                {
                    SendMessageToClient(existingClient, newPlayerPacket, NetDeliveryMethod.ReliableOrdered);
                    Console.WriteLine($"    Sent ClientJoined about {newClient.PlayerName} to {existingClient.PlayerName}");
                }
            }

            Console.WriteLine($"[AnnounceClientJoined] Informing {newClient.PlayerName} about existing initialized players.");
            foreach (var existingClientEntry in ConnectedClients)
            {
                ConnectedClient existingClient = existingClientEntry.Value;
                if (existingClient.Connection != null && existingClient.RemoteUniqueIdentifier != newClient.RemoteUniqueIdentifier && existingClient.IsInitialized)
                {
                    using (System.IO.MemoryStream msExisting = new System.IO.MemoryStream())
                    {
                        using (System.IO.BinaryWriter writerExisting = new System.IO.BinaryWriter(msExisting))
                        {
                            writerExisting.Write(existingClient.PlayerServerIndex);
                            writerExisting.Write(existingClient.PlayerName ?? "Player");
                            writerExisting.Write(existingClient.ColorID);
                            writerExisting.Write(existingClient.IsReady);
                            writerExisting.Write(existingClient.IsSpawned);
                            writerExisting.Write(existingClient.Position.X);
                            writerExisting.Write(existingClient.Position.Y);
                            writerExisting.Write(existingClient.IsInitialized);
                        }
                        byte[] existingPlayerPayload = msExisting.ToArray();
                        byte[] existingPlayerPacket = PacketHandler.WriteMessageBuffer(existingPlayerPayload, MsgType.ClientJoined);
                        SendMessageToClient(newClient, existingPlayerPacket, NetDeliveryMethod.ReliableOrdered);
                        Console.WriteLine($"    Sent ClientJoined about {existingClient.PlayerName} to new player {newClient.PlayerName}");
                    }
                }
            }
        }

        public void SendMessageToClient(ConnectedClient client, byte[] data, NetDeliveryMethod deliveryMethod, int sequenceChannel = 0)
        {
            if (client == null || client.Connection == null || client.Connection.Status != NetConnectionStatus.Connected) return;
            NetOutgoingMessage om = server.CreateMessage(data.Length);
            om.Write(data);
            server.SendMessage(om, client.Connection, deliveryMethod, sequenceChannel);
        }

        public void BroadcastMessage(byte[] data, NetDeliveryMethod deliveryMethod, int sequenceChannel = 0)
        {
            if (ConnectedClients.Count == 0) return;
            NetOutgoingMessage om = server.CreateMessage(data.Length);
            om.Write(data);
            
            List<NetConnection> recipients = new List<NetConnection>();
            foreach (var client in ConnectedClients.Values)
            {
                if (client.Connection != null && client.Connection.Status == NetConnectionStatus.Connected)
                    recipients.Add(client.Connection);
            }

            if (recipients.Count > 0)
                server.SendMessage(om, recipients, deliveryMethod, sequenceChannel);
        }

        public void BroadcastMessageToOthers(ConnectedClient sender, byte[] data, NetDeliveryMethod deliveryMethod, int sequenceChannel = 0)
        {
            if (ConnectedClients.Count < 2 || sender == null) return;
            NetOutgoingMessage om = server.CreateMessage(data.Length);
            om.Write(data);

            List<NetConnection> recipients = new List<NetConnection>();
            foreach (var client in ConnectedClients.Values)
            {
                if (client.Connection != null && client.Connection.Status == NetConnectionStatus.Connected && client.RemoteUniqueIdentifier != sender.RemoteUniqueIdentifier)
                    recipients.Add(client.Connection);
            }
             if (recipients.Count > 0)
                server.SendMessage(om, recipients, deliveryMethod, sequenceChannel);
        }

        public void Stop()
        {
            if (!isRunning) return;
            isRunning = false;
            Console.WriteLine("Server shutting down...");
            Thread.Sleep(100);

            server?.Shutdown("Server is shutting down.");
            serverThread?.Join(500);
            Console.WriteLine("Server stopped.");
        }

        private void AnnounceNewPlayerToExistingPlayers(ConnectedClient newPlayer)
        {
            if (newPlayer == null || !newPlayer.IsInitialized)
            {
                Console.WriteLine($"[SERVER] AnnounceNewPlayer: Player {(newPlayer == null ? "UNKNOWN" : newPlayer.PlayerName)} is not yet initialized. Aborting announcement.");
                return;
            }

            Console.WriteLine($"[SERVER] Announcing newly initialized player {newPlayer.PlayerName} (Index: {newPlayer.PlayerServerIndex}) to other players.");
            byte[] newPlayerPacket = null;
            using (System.IO.MemoryStream payloadStream = new System.IO.MemoryStream())
            {
                using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(payloadStream))
                {
                    writer.Write(newPlayer.PlayerServerIndex);
                    writer.Write(newPlayer.PlayerName ?? string.Empty); 
                    writer.Write(newPlayer.ColorID);
                    writer.Write(newPlayer.IsReady);
                    writer.Write(newPlayer.IsSpawned); 
                    writer.Write(newPlayer.Position.X);
                    writer.Write(newPlayer.Position.Y);
                    writer.Write(newPlayer.IsInitialized);
                }
                byte[] clientJoinedPayload = payloadStream.ToArray();
                newPlayerPacket = PacketHandler.WriteMessageBuffer(clientJoinedPayload, MsgType.ClientJoined);
            }
            
            foreach (var clientEntry in ConnectedClients)
            {
                ConnectedClient existingClient = clientEntry.Value;
                if (existingClient.RemoteUniqueIdentifier != newPlayer.RemoteUniqueIdentifier && existingClient.IsInitialized)
                {
                    SendMessageToClient(existingClient, newPlayerPacket, NetDeliveryMethod.ReliableOrdered);
                }
            }
            Console.WriteLine($"[SERVER] Announced {newPlayer.PlayerName} to others. Packet size: {newPlayerPacket?.Length ?? 0}");
        }

        public void Log(string message)
        {
            Console.WriteLine(message);
        }

        public Dictionary<long, ConnectedClient> GetAllClients()
        {
            return new Dictionary<long, ConnectedClient>(ConnectedClients); 
        }
    }
}

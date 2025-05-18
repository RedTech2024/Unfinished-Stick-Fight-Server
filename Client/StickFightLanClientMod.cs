using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Lidgren.Network;
using System;
using System.Net;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace StickFightLanClientMod
{
    [BepInPlugin("com.yourname.stickfightlanclient", "Stick Fight LAN Client Mod", "0.1.1")]
    [BepInProcess("StickFight.exe")]
    public class LanClientMod : BaseUnityPlugin
    {
        private string serverIp = "127.0.0.1";
        private string serverPortStr = "1337";
        private NetClient client;
        private string connectionStatus = "未连接";
        private bool showUi = true;

        private byte myPlayerServerIndex = 255;
        private Dictionary<byte, GameObject> remotePlayers = new Dictionary<byte, GameObject>();
        public GameObject playerPrefab;
        private bool inGameMapLoaded = false;
        private string myName = "LAN_Player";
        private string currentMapName = "UnknownMap";

        private float timeSinceLastClientStateSent = 0f;
        private const float ClientStateSendInterval = 1f / 20f;
        private GameObject localPlayerGameObject;

        void Awake()
        {
            Logger.LogError("AWAKE METHOD HAS STARTED EXECUTION - LAN CLIENT MOD");
            NetPeerConfiguration config = new NetPeerConfiguration("StickFight 1.0");
            config.EnableMessageType(NetIncomingMessageType.StatusChanged);
            config.EnableMessageType(NetIncomingMessageType.Data);
            config.EnableMessageType(NetIncomingMessageType.DebugMessage);
            config.EnableMessageType(NetIncomingMessageType.WarningMessage);
            config.EnableMessageType(NetIncomingMessageType.ErrorMessage);

            client = new NetClient(config);
            client.Start();
            Logger.LogInfo("NetClient started in Awake.");
        }

        void Start()
        {
            Logger.LogInfo("[CLIENT] Start() method called.");

            if (MultiplayerManagerAssets.Instance == null)
            {
                Logger.LogWarning("[CLIENT] MultiplayerManagerAssets.Instance is null in Start().");
            }
            else
            {
                Logger.LogInfo("[CLIENT] MultiplayerManagerAssets.Instance is NOT null.");
                if (MultiplayerManagerAssets.Instance.PlayerPrefab == null)
                {
                    Logger.LogWarning("[CLIENT] MultiplayerManagerAssets.Instance.PlayerPrefab is null.");
                }
                else
                {
                    playerPrefab = MultiplayerManagerAssets.Instance.PlayerPrefab;
                    Logger.LogInfo("[CLIENT] playerPrefab assigned from MultiplayerManagerAssets.Instance.PlayerPrefab.");
                }
            }

            if (playerPrefab == null)
            {
                Logger.LogInfo("[CLIENT] playerPrefab is still null, attempting to load from LevelEditor.ResourcesManager.");
                if (LevelEditor.ResourcesManager.Instance == null)
                {
                    Logger.LogWarning("[CLIENT] LevelEditor.ResourcesManager.Instance is null.");
                }
                else
                {
                    Logger.LogInfo("[CLIENT] LevelEditor.ResourcesManager.Instance is NOT null.");
                    if (LevelEditor.ResourcesManager.Instance.CharacterObject == null)
                    {
                        Logger.LogWarning("[CLIENT] LevelEditor.ResourcesManager.Instance.CharacterObject is null.");
                    }
                    else
                    {
                        playerPrefab = LevelEditor.ResourcesManager.Instance.CharacterObject;
                        Logger.LogInfo("[CLIENT] playerPrefab assigned from LevelEditor.ResourcesManager.Instance.CharacterObject.");
                    }
                }
            }

            if (playerPrefab == null)
            {
                Logger.LogError("[CLIENT] CRITICAL: playerPrefab is STILL NULL after all attempts in Start(). Remote players cannot be instantiated.");
            }
            else
            {
                Logger.LogInfo($"[CLIENT] playerPrefab successfully loaded in Start(). Name: {playerPrefab.name}");
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                showUi = !showUi;
            }

            NetIncomingMessage msg;
            while ((msg = client.ReadMessage()) != null)
            {
                switch (msg.MessageType)
                {
                    case NetIncomingMessageType.StatusChanged:
                        NetConnectionStatus status = (NetConnectionStatus)msg.ReadByte();
                        string reason = msg.ReadString();
                        Logger.LogInfo($"Status changed: {status} - Reason: {reason}");
                        connectionStatus = $"状态: {status} - {reason}";
                        if (status == NetConnectionStatus.Connected)
                        {
                            connectionStatus = $"已连接到 {serverIp}:{serverPortStr}";
                            SendClientInit();
                        }
                        else if (status == NetConnectionStatus.Disconnected)
                        {
                            connectionStatus = $"已断开连接. 原因: {reason}";
                        }
                        break;
                    case NetIncomingMessageType.Data:
                        HandleDataMessage(msg);
                        break;
                    case NetIncomingMessageType.DebugMessage:
                    case NetIncomingMessageType.WarningMessage:
                    case NetIncomingMessageType.ErrorMessage:
                        Logger.LogWarning($"Lidgren: {msg.ReadString()}");
                        break;
                    default:
                        Logger.LogInfo($"Unhandled Lidgren message type: {msg.MessageType}");
                        break;
                }
                client.Recycle(msg);
            }

            timeSinceLastClientStateSent += Time.deltaTime;
            if (timeSinceLastClientStateSent >= ClientStateSendInterval)
            {
                if (client.ConnectionStatus == NetConnectionStatus.Connected &&
                    myPlayerServerIndex != 255 &&
                    localPlayerGameObject != null &&
                    inGameMapLoaded)
                {
                    SendPlayerStateUpdate();
                }
                timeSinceLastClientStateSent = 0f;
            }
        }

        public void HandleDataMessage(NetIncomingMessage msg)
        {
            float initialMessageLengthBytes = msg.LengthBytes;

            if (initialMessageLengthBytes < 5)
            {

                return;
            }

            uint timestamp = msg.ReadUInt32();
            MsgType type = (MsgType)msg.ReadByte();
            float payloadActualLength = initialMessageLengthBytes - 5;



            switch (type)
            {
                case MsgType.ClientAccepted:
                    Logger.LogInfo($"[CLIENT] Case ClientAccepted: Payload actual length: {payloadActualLength}. Expecting 1 byte for PlayerServerIndex.");

                    if (payloadActualLength >= 1)
                    {
                        myPlayerServerIndex = msg.ReadByte();
                        Logger.LogInfo($"[CLIENT] Assigned PlayerServerIndex: {myPlayerServerIndex}");
                        connectionStatus = $"Connected. My ID: {myPlayerServerIndex}";

                        if (inGameMapLoaded && myPlayerServerIndex != 255)
                        {
                            Logger.LogInfo($"[CLIENT] Map '{currentMapName}' already loaded, PlayerServerIndex {myPlayerServerIndex} valid. Sending spawn request.");
                            SendSpawnRequest();
                        }
                        else if (myPlayerServerIndex == 255)
                        {
                            Logger.LogWarning($"[CLIENT] Received valid ClientAccepted structure, but PlayerServerIndex from server is 255. Spawn request will be blocked.");
                        }
                        else if (!inGameMapLoaded)
                        {
                            Logger.LogInfo($"[CLIENT] PlayerServerIndex {myPlayerServerIndex} received. Waiting for MapChange to send spawn request.");
                        }
                    }
                    else
                    {
                        Logger.LogError($"[CLIENT] ClientAccepted payload too short or missing! Actual payload length: {payloadActualLength}. Expected 1 byte.");
                        myPlayerServerIndex = 255;
                        connectionStatus = "Error: ClientAccepted payload issue.";
                    }
                    break;

                case MsgType.ClientJoined:
                    HandleClientJoined(msg, payloadActualLength, initialMessageLengthBytes);
                    break;

                case MsgType.ClientSpawned:
                    HandleClientSpawned(msg, payloadActualLength, initialMessageLengthBytes);
                    break;

                case MsgType.MapChange:

                    float expectedMapChangePayloadBytes = 4f + 1f;
                    if (payloadActualLength >= expectedMapChangePayloadBytes)
                    {
                        int mapId = msg.ReadInt32();
                        byte lastWinner = msg.ReadByte();
                        currentMapName = $"MapID_{mapId}";



                        foreach (var pair in remotePlayers)
                        {
                            if (pair.Value != null)
                            {
                                Destroy(pair.Value);
                            }
                        }
                        remotePlayers.Clear();
                        if (localPlayerGameObject != null)
                        {

                            localPlayerGameObject = null;
                        }
                        inGameMapLoaded = false;

                        MapWrapper mapWrapper = new MapWrapper();
                        mapWrapper.MapType = 0;
                        mapWrapper.MapData = BitConverter.GetBytes(mapId);

                        if (GameManager.Instance == null)
                        {

                            return;
                        }


                        GameManager.Instance.StartMatch(mapWrapper, true);
                        inGameMapLoaded = true;



                        if (myPlayerServerIndex != 255)
                        {

                            SendSpawnRequest();
                        }




                    }
                    else
                    {


                        inGameMapLoaded = false;
                    }
                    break;

                case MsgType.PlayerUpdate:
                    HandlePlayerUpdate(msg, payloadActualLength, initialMessageLengthBytes);
                    break;

                case MsgType.KickPlayer:
                    HandleKickPlayer(msg, payloadActualLength, initialMessageLengthBytes);
                    break;

                case MsgType.WeaponSpawned:
                    HandleWeaponSpawned(msg, payloadActualLength, initialMessageLengthBytes);
                    break;


                default:
                    Logger.LogInfo($"[CLIENT] Received unhandled message type: {type}. Initial length: {initialMessageLengthBytes}, Payload actual length: {payloadActualLength}");
                    break;
            }
        }

        private void HandleClientSpawned(NetIncomingMessage msg, float payloadActualLength, float initialMessageLengthBytes)
        {
















            float expectedMinPayloadBytes = 1 + 8 + 1 + 4 + 8 + 4 + 1 + 1 + 4 + 4;



            if (payloadActualLength < 1) { Logger.LogError("[CLIENT] ClientSpawned payload too short for playerIndex."); return; }
            byte playerNetIndex = msg.ReadByte();
            float currentReadOffset = 1f;

            if (payloadActualLength < currentReadOffset + 8) { Logger.LogError($"[CLIENT] ClientSpawned payload too short for position. Read: {currentReadOffset}"); return; }
            Vector2 position = new Vector2(msg.ReadFloat(), msg.ReadFloat());
            currentReadOffset += 8f;

            string playerName = "[DEF_NAME]";
            try
            {
                if (payloadActualLength < currentReadOffset + 1)
                {
                    Logger.LogError($"[CLIENT] ClientSpawned payload too short for playerName length prefix. Read: {currentReadOffset}");

                }
                playerName = msg.ReadString();



            }
            catch (Exception e_rs)
            {
                Logger.LogError($"[CLIENT] EXCEPTION during msg.ReadString() for playerName in ClientSpawned: {e_rs.Message}");

            }




            float remainingFixedFieldsSize = 4 + 8 + 4 + 1 + 1 + 4 + 4;



            if ((initialMessageLengthBytes - msg.PositionInBytes) < remainingFixedFieldsSize)
            {
                Logger.LogError($"[CLIENT] ClientSpawned payload too short for remaining fixed fields after name. Remaining in buffer: {(initialMessageLengthBytes - msg.PositionInBytes)}, Expected: {remainingFixedFieldsSize}");
                return;
            }

            int playerColorID_int = msg.ReadInt32();


            Vector2 velocity = new Vector2(msg.ReadFloat(), msg.ReadFloat());
            float aimDirection = msg.ReadFloat();
            bool isJumping = msg.ReadBoolean();
            bool isGrounded = msg.ReadBoolean();
            int initialHoldingWeaponID = msg.ReadInt32();
            float health = msg.ReadFloat();

            Logger.LogInfo($"[CLIENT] ClientSpawned PARSED: P{playerNetIndex} '{playerName}', Pos({position.x:F2},{position.y:F2}), Vel({velocity.x:F2},{velocity.y:F2}), Aim({aimDirection:F1}), Jump({isJumping}), Ground({isGrounded}), Weapon({initialHoldingWeaponID}), Health({health:F0}), ColorID({playerColorID_int}). MyIdx={myPlayerServerIndex}");

            GameObject playerGameObject = null;

            if (playerNetIndex == myPlayerServerIndex)
            {
                Logger.LogInfo($"[CLIENT] Received spawn confirmation for MYSELF (P{playerNetIndex}). Attempting to find local player GameObject.");


                if (localPlayerGameObject == null)
                {
                    Controller[] controllers = FindObjectsOfType<Controller>();
                    foreach (Controller c in controllers)
                    {
                        if (c.playerID == myPlayerServerIndex && !c.isAI && !c.inactive)
                        {
                            localPlayerGameObject = c.gameObject;
                            Logger.LogInfo($"[CLIENT] Found and assigned localPlayerGameObject: {localPlayerGameObject.name} for P{myPlayerServerIndex} via playerID match.");
                            break;
                        }
                    }
                    if (localPlayerGameObject == null)
                    {
                        foreach (Controller c in controllers)
                        {
                            if (c.HasControl && c.PlayerActions != null && !c.isAI && !c.inactive)
                            {
                                localPlayerGameObject = c.gameObject;
                                Logger.LogInfo($"[CLIENT] Found localPlayerGameObject: {localPlayerGameObject.name} via HasControl.");
                                break;
                            }
                        }
                    }
                }
                playerGameObject = localPlayerGameObject;

                if (playerGameObject == null)
                {
                    Logger.LogWarning($"[CLIENT] CRITICAL: Could not find/assign localPlayerGameObject for P{myPlayerServerIndex} after spawn confirmation.");

                }
            }
            else
            {
                Logger.LogInfo($"[CLIENT] Received spawn info for REMOTE player P{playerNetIndex} ('{playerName}').");
                if (remotePlayers.TryGetValue(playerNetIndex, out GameObject existingPlayerGO))
                {
                    playerGameObject = existingPlayerGO;
                    playerGameObject.transform.position = position;
                    playerGameObject.name = $"RemotePlayer_{playerName}_{playerNetIndex}";
                    Logger.LogInfo($"[CLIENT] Updated existing remote player P{playerNetIndex} at {position}.");
                }
                else
                {
                    if (playerPrefab != null)
                    {
                        playerGameObject = Instantiate(playerPrefab, position, Quaternion.identity);
                        playerGameObject.name = $"RemotePlayer_{playerName}_{playerNetIndex}";
                        remotePlayers[playerNetIndex] = playerGameObject;
                        Logger.LogInfo($"[CLIENT] Instantiated new remote player P{playerNetIndex} ('{playerName}') at {position}.");
                    }
                    else
                    {
                        Logger.LogError($"[CLIENT] playerPrefab is NULL. Cannot instantiate remote player P{playerNetIndex} ('{playerName}').");
                    }
                }
            }


            if (playerGameObject != null)
            {
                HealthHandler healthHandler = playerGameObject.GetComponent<HealthHandler>();
                if (healthHandler != null)
                {
                    healthHandler.health = health;
                }
                else
                {
                    Logger.LogWarning($"[CLIENT] HealthHandler component not found on playerGameObject for P{playerNetIndex} ('{playerName}'). Cannot set health.");
                }

                CharacterInformation charInfo = playerGameObject.GetComponent<CharacterInformation>();
                if (charInfo != null)
                {
                    charInfo.isGrounded = isGrounded;
                }
                else
                {
                     Logger.LogWarning($"[CLIENT] CharacterInformation component not found on playerGameObject for P{playerNetIndex} ('{playerName}'). Cannot set isGrounded.");
                }

                Rigidbody2D rb = playerGameObject.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.position = position;
                    rb.velocity = velocity;
                }
                else
                {
                    playerGameObject.transform.position = position;
                }






                Animator animator = playerGameObject.GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    animator.SetBool("IsJumping", isJumping);
                    animator.SetBool("IsGrounded", isGrounded);
                }







                ApplyPlayerVisuals(playerGameObject, playerNetIndex, playerName, playerColorID_int);
                Logger.LogInfo($"[CLIENT] Applied initial states and visuals for P{playerNetIndex} ('{playerName}'). Health: {health}");
            }
            else
            {
                Logger.LogWarning($"[CLIENT] Could not apply initial state for P{playerNetIndex} as its GameObject is null.");
            }
        }

        private void HandleClientJoined(NetIncomingMessage msg, float payloadActualLength, float initialMessageLengthBytes)
        {

            Logger.LogInfo($"[CLIENT] Case ClientJoined: Initial total length was {initialMessageLengthBytes}. Payload actual length: {payloadActualLength}. Expecting playerIndex (1) + playerName (variable).");

            if (payloadActualLength < 1) { Logger.LogError("[CLIENT] ClientJoined payload too short for playerIndex."); return; }
            byte playerNetIndex = msg.ReadByte();


            if (payloadActualLength < 1 + 1) { Logger.LogError($"[CLIENT] ClientJoined payload too short for playerName length (after playerIndex). Actual: {payloadActualLength}"); return; }
            string playerName = msg.ReadString();

            Logger.LogInfo($"[CLIENT] ClientJoined: NetIndex={playerNetIndex}, Name='{playerName}'. MyIndex={myPlayerServerIndex}");

            if (playerNetIndex != myPlayerServerIndex)
            {


                Logger.LogInfo($"[CLIENT] Remote player {playerName} (NetIndex {playerNetIndex}) has joined the server. Waiting for their spawn message.");
            }
        }

        private void HandlePlayerUpdate(NetIncomingMessage msg, float payloadActualLength, float initialMessageLengthBytes)
        {












            float expectedMinPayloadBytes = 1 + 8 + 8 + 4 + 1 + 1 + 4;



            if (payloadActualLength < expectedMinPayloadBytes)
            {
                Logger.LogError($"[CLIENT] PlayerUpdate payload too short. Actual: {payloadActualLength}, Expected at least {expectedMinPayloadBytes} bytes.");


                return;
            }

            byte playerNetIndex = msg.ReadByte();
            Vector2 position = new Vector2(msg.ReadFloat(), msg.ReadFloat());
            Vector2 velocity = new Vector2(msg.ReadFloat(), msg.ReadFloat());
            float aimDirection = msg.ReadFloat();
            bool isJumping = msg.ReadBoolean();
            bool isGrounded = msg.ReadBoolean();
            int holdingWeaponID = msg.ReadInt32();




            if (playerNetIndex == myPlayerServerIndex)
            {



                return;
            }

            if (remotePlayers.TryGetValue(playerNetIndex, out GameObject playerGO))
            {
                if (playerGO != null)
                {


                    playerGO.transform.position = position;




                    Rigidbody2D rb = playerGO.GetComponent<Rigidbody2D>();
                    if (rb != null)
                    {



                    }





                    Animator animator = playerGO.GetComponentInChildren<Animator>();
                    if (animator != null)
                    {
                        animator.SetBool("IsJumping", isJumping);
                        animator.SetBool("IsGrounded", isGrounded);


                    }







                }
                else
                {
                    Logger.LogWarning($"[CLIENT] PlayerUpdate: GameObject for remote player {playerNetIndex} is null in dictionary.");
                }
            }

        }

        private void HandleKickPlayer(NetIncomingMessage msg, float payloadActualLength, float initialMessageLengthBytes)
        {

            Logger.LogInfo($"[CLIENT] Case KickPlayer: Initial total length was {initialMessageLengthBytes}. Payload actual length: {payloadActualLength}. Expecting 1 byte for playerIndex.");

            if (payloadActualLength < 1) { Logger.LogError("[CLIENT] KickPlayer payload too short."); return; }
            byte playerNetIndex = msg.ReadByte();

            Logger.LogInfo($"[CLIENT] Received KickPlayer for NetIndex: {playerNetIndex}. MyIndex: {myPlayerServerIndex}");

            if (playerNetIndex == myPlayerServerIndex)
            {
                Logger.LogWarning("[CLIENT] Received kick message for myself. Disconnecting.");
                connectionStatus = "Kicked from server.";
                client.Disconnect("Kicked from server.");
            }
            else if (remotePlayers.TryGetValue(playerNetIndex, out GameObject playerGO))
            {
                Destroy(playerGO);
                remotePlayers.Remove(playerNetIndex);
                Logger.LogInfo($"[CLIENT] Removed remote player with NetIndex: {playerNetIndex}.");
            }
        }

        private void HandleWeaponSpawned(NetIncomingMessage msg, float payloadActualLength, float initialMessageLengthBytes)
        {

            float expectedPayloadBytes = 4f + 4f + 4f;
            Logger.LogInfo($"[CLIENT] Case WeaponSpawned: Initial total length was {initialMessageLengthBytes}. Payload actual length: {payloadActualLength}. Expecting {expectedPayloadBytes} bytes.");

            if (payloadActualLength < expectedPayloadBytes)
            {
                Logger.LogError($"[CLIENT] WeaponSpawned payload too short. Actual: {payloadActualLength}, Expected: {expectedPayloadBytes}");
                return;
            }

            int weaponId = msg.ReadInt32();
            float posX = msg.ReadFloat();
            float posY = msg.ReadFloat();

            Logger.LogInfo($"[CLIENT] WeaponSpawned: ID={weaponId}, Position=({posX:F2}, {posY:F2})");

        }

        void OnGUI()
        {
            if (!showUi) return;

            GUILayout.BeginArea(new Rect(10, 10, 300, 200), GUI.skin.box);
            GUILayout.Label("Stick Fight LAN Client");
            GUILayout.Space(10);

            GUILayout.Label("Server IP:");
            serverIp = GUILayout.TextField(serverIp);
            GUILayout.Label("Server Port:");
            serverPortStr = GUILayout.TextField(serverPortStr);
            GUILayout.Space(10);

            if (client.ConnectionStatus == NetConnectionStatus.Disconnected || client.ConnectionStatus == NetConnectionStatus.None)
            {
                if (GUILayout.Button("连接"))
                {
                    ConnectToServer();
                }
            }
            else
            {
                if (GUILayout.Button("断开连接"))
                {
                    client.Disconnect("Player disconnected.");
                }
            }

            GUILayout.Space(10);
            GUILayout.Label(connectionStatus);
            GUILayout.EndArea();
        }

        void ConnectToServer()
        {
            if (int.TryParse(serverPortStr, out int port))
            {
                IPAddress ipAddr;
                if (!IPAddress.TryParse(serverIp, out ipAddr))
                {

                    try
                    {
                        IPHostEntry hostEntry = Dns.GetHostEntry(serverIp);
                        if (hostEntry.AddressList.Length > 0)
                        {
                            ipAddr = hostEntry.AddressList[0];
                            Logger.LogInfo($"DNS Resolved {serverIp} to {ipAddr}");
                        }
                        else
                        {
                            connectionStatus = "错误: DNS解析失败，未找到地址。";
                            Logger.LogError(connectionStatus);
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        connectionStatus = $"错误: 无效的IP或主机名: {e.Message}";
                        Logger.LogError(connectionStatus);
                        return;
                    }
                }

                Logger.LogInfo($"Attempting to connect to {ipAddr}:{port}...");
                connectionStatus = $"正在连接到 {ipAddr}:{port}...";



                client.Connect(new IPEndPoint(ipAddr, port));
            }
            else
            {
                connectionStatus = "错误: 端口号无效.";
                Logger.LogError(connectionStatus);
            }
        }

        void SendClientInit()
        {

            try
            {
                NetOutgoingMessage om = client.CreateMessage();
                om.Write((uint)0);
                om.Write((byte)MsgType.ClientInit);
                om.Write(myName);

                client.SendMessage(om, NetDeliveryMethod.ReliableOrdered);
                Logger.LogInfo($"Sent ClientInit with name: {myName}");
            }
            catch (Exception e)
            {
                Logger.LogError($"Error sending ClientInit: {e.Message}");
            }
        }

        void OnApplicationQuit()
        {
            if (client != null && client.Status != NetPeerStatus.NotRunning)
            {
                client.Disconnect("Application shutting down.");
                remotePlayers.Clear();
                client.Shutdown("Application shutting down.");
            }
        }

        void SendSpawnRequest()
        {
            if (!inGameMapLoaded)
            {
                Logger.LogWarning("[CLIENT] SendSpawnRequest: Map not loaded yet. Aborting.");
                return;
            }
            if (myPlayerServerIndex == 255)
            {
                Logger.LogWarning("[CLIENT] SendSpawnRequest: myPlayerServerIndex is invalid. Aborting.");
                return;
            }

            try
            {
                Logger.LogInfo($"[CLIENT] Sending ClientRequestingToSpawn to server. My Index: {myPlayerServerIndex}");
                NetOutgoingMessage om = client.CreateMessage();
                om.Write((uint)Environment.TickCount);
                om.Write((byte)MsgType.ClientRequestingToSpawn);

                client.SendMessage(om, NetDeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Logger.LogError($"[CLIENT] Error sending ClientRequestingToSpawn: {e.Message}");
            }
        }

        private void ApplyPlayerVisuals(GameObject playerGO, byte playerServerIndex, string playerName, int colorId)
        {
            if (MultiplayerManagerAssets.Instance != null && MultiplayerManagerAssets.Instance.Colors != null)
            {
                for (int i = 0; i < MultiplayerManagerAssets.Instance.Colors.Length; i++)
                {
                    if (MultiplayerManagerAssets.Instance.Colors[i] != null)
                    {
                        Logger.LogInfo($"[APPLY_VISUALS_DEBUG] Colors[{i}]: {MultiplayerManagerAssets.Instance.Colors[i].name}");
                    }
                    else
                    {
                        Logger.LogInfo($"[APPLY_VISUALS_DEBUG] Colors[{i}]: null");
                    }
                }
            }

            if (playerGO == null)
            {
                Logger.LogError($"ApplyPlayerVisuals: playerGO is null for playerIndex {playerServerIndex}");
                return;
            }

            Controller controller = playerGO.GetComponent<Controller>();
            if (controller == null)
            {
                Logger.LogError($"ApplyPlayerVisuals: Controller component not found on playerGO for playerIndex {playerServerIndex}");
                return;
            }

            CharacterInformation charInfo = playerGO.GetComponent<CharacterInformation>();
            if (charInfo == null)
            {
                Logger.LogError($"ApplyPlayerVisuals: CharacterInformation component not found on playerGO for playerIndex {playerServerIndex}");
                return;
            }

            AI aiComponent = playerGO.GetComponent<AI>();
            Standing standingComponent = playerGO.GetComponent<Standing>();

            controller.playerID = playerServerIndex;

            if (playerServerIndex == myPlayerServerIndex)
            {
                Logger.LogInfo($"ApplyPlayerVisuals: Setting up LOCAL player {playerServerIndex} ({playerName})");
                controller.inactive = false;
                controller.isAI = false;
                if (aiComponent != null)
                {
                    aiComponent.enabled = false;
                }
                if (standingComponent != null)
                {
                    standingComponent.enabled = true;
                }
            }
            else
            {
                Logger.LogInfo($"ApplyPlayerVisuals: Setting up REMOTE player {playerServerIndex} ({playerName})");
                controller.inactive = true;
                controller.isAI = true;

                if (aiComponent != null)
                {
                    aiComponent.enabled = false;
                    Logger.LogInfo($"ApplyPlayerVisuals: Disabled AI component for remote player {playerServerIndex}");
                }
                else
                {
                    Logger.LogInfo($"ApplyPlayerVisuals: AI component NOT found on remote player {playerServerIndex}. This is okay.");
                }

                if (standingComponent != null)
                {
                    standingComponent.enabled = true;
                    Logger.LogInfo($"ApplyPlayerVisuals: Kept Standing component ENABLED for remote player {playerServerIndex}");
                }
                else
                {
                    Logger.LogWarning($"ApplyPlayerVisuals: Standing component NOT found on remote player {playerServerIndex}.");
                }
            }


            if (MultiplayerManagerAssets.Instance != null && MultiplayerManagerAssets.Instance.Colors != null)
            {
                if (colorId >= 0 && colorId < MultiplayerManagerAssets.Instance.Colors.Length)
                {
                    Material playerMaterial = MultiplayerManagerAssets.Instance.Colors[colorId];
                    if (playerMaterial != null)
                    {
                        LineRenderer[] lineRenderers = playerGO.GetComponentsInChildren<LineRenderer>(true);
                        foreach (LineRenderer lr in lineRenderers)
                        {
                            lr.material = playerMaterial;
                        }

                        SpriteRenderer[] spriteRenderers = playerGO.GetComponentsInChildren<SpriteRenderer>(true);
                        foreach (SpriteRenderer sr in spriteRenderers)
                        {
                            if (sr.transform.tag != "DontChangeColor")
                            {
                                sr.color = playerMaterial.color;
                            }
                        }
                        charInfo.myMaterial = playerMaterial;

                        Logger.LogInfo($"Applied color ID {colorId} to player {playerServerIndex} ({playerName})");
                    }
                    else
                    {
                        Logger.LogWarning($"Player material for colorId {colorId} is null.");
                    }
                }
                else
                {
                    Logger.LogWarning($"Invalid colorId {colorId} for player {playerServerIndex}. Max available: {MultiplayerManagerAssets.Instance.Colors.Length - 1}");
                }
            }
            else
            {
                Logger.LogWarning($"MultiplayerManagerAssets.Instance or Colors array is null. Cannot apply colors.");
            }

            Logger.LogInfo($"Finished ApplyPlayerVisuals for player {playerServerIndex} ({playerName}). Inactive: {controller.inactive}, IsAI: {controller.isAI}, HasControl: {controller.HasControl}");
            if (aiComponent != null)
            {
                Logger.LogInfo($"AI Component on player {playerServerIndex} ({playerName}) is {(aiComponent.enabled ? "ENABLED" : "DISABLED")}.");
            }
            if (standingComponent != null)
            {
                Logger.LogInfo($"Standing Component on player {playerServerIndex} ({playerName}) is {(standingComponent.enabled ? "ENABLED" : "DISABLED")}.");
            }
        }

        void SendPlayerStateUpdate()
        {
            if (localPlayerGameObject == null || client == null || client.ConnectionStatus != NetConnectionStatus.Connected)
            {
                return;
            }

            Controller localController = localPlayerGameObject.GetComponent<Controller>();
            CharacterInformation localCharInfo = localPlayerGameObject.GetComponent<CharacterInformation>();
            Fighting localFighting = localPlayerGameObject.GetComponent<Fighting>();
            Rigidbody2D localRigidbody = localPlayerGameObject.GetComponent<Rigidbody2D>();

            if (localController == null || localCharInfo == null || localFighting == null || localRigidbody == null)
            {
                return;
            }

            NetOutgoingMessage msg = client.CreateMessage();
            msg.Write((byte)MsgType.PlayerUpdate);

            Vector3 currentPosition = localPlayerGameObject.transform.position;
            msg.Write(currentPosition.x);
            msg.Write(currentPosition.y);

            Vector2 currentVelocity = localRigidbody.velocity;
            msg.Write(currentVelocity.x);
            msg.Write(currentVelocity.y);

            float aimDirection = 0f;
            Transform aimerTransform = localPlayerGameObject.transform.Find("AimPosition");
            if (aimerTransform != null)
            {
                aimDirection = aimerTransform.rotation.eulerAngles.y;
            }
            msg.Write(aimDirection);

            bool isJumping = localCharInfo.sinceJumped < 0.3f;
            msg.Write(isJumping);

            bool isGrounded = localCharInfo.isGrounded;
            msg.Write(isGrounded);

            int holdingWeaponID = -1;
            byte cw_index = localFighting.CurrentWeaponIndex;
            if (cw_index == 0)
            {
                holdingWeaponID = -1;
            }
            else
            {
                holdingWeaponID = (int)cw_index;
            }
            msg.Write(holdingWeaponID);

            client.SendMessage(msg, NetDeliveryMethod.UnreliableSequenced);
        }
    }


    public enum MsgType : byte
    {

        Ping,

        PingResponse,

        ClientJoined,

        ClientRequestingAccepting,

        ClientAccepted,

        ClientInit,

        ClientRequestingIndex,

        ClientRequestingToSpawn,

        ClientSpawned,

        ClientReadyUp,

        PlayerUpdate,

        PlayerTookDamage,

        PlayerTalked,

        PlayerForceAdded,

        PlayerForceAddedAndBlock,

        PlayerLavaForceAdded,

        PlayerFallOut,

        PlayerWonWithRicochet,

        MapChange,

        WeaponSpawned,

        WeaponThrown,

        RequestingWeaponThrow,

        ClientRequestWeaponDrop,

        WeaponDropped,

        WeaponWasPickedUp,

        ClientRequestingWeaponPickUp,

        ObjectUpdate,

        ObjectSpawned,

        ObjectSimpleDestruction,

        ObjectInvokeDestructionEvent,

        ObjectDestructionCollision,

        GroundWeaponsInit,

        MapInfo,

        MapInfoSync,

        WorkshopMapsLoaded,

        StartMatch,

        ObjectHello,

        OptionsChanged,

        KickPlayer
    }
}
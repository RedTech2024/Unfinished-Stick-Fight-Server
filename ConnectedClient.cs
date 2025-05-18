using Lidgren.Network;
using System;
using System.Numerics; 

namespace StickFightLanServer
{
    public class ConnectedClient
    {
        public NetConnection Connection { get; private set; }
        public long RemoteUniqueIdentifier { get; private set; }
        public byte PlayerServerIndex { get; set; }
        public string PlayerName { get; set; }
        public byte ColorID { get; set; }

      
        public bool IsReady { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsSpawned { get; set; } 
        public float Health { get; set; }
        public int HoldingWeaponID { get; set; } 
  
        public Vector2 Position { get; set; }
        public Vector2 Velocity { get; set; }
        public float AimDirection { get; set; } 
        public bool IsJumping { get; set; }
        public bool IsGrounded { get; set; }
      
        public DateTime LastMessageTimestamp { get; set; } 
        public Vector2? LastKnownPosition { get; set; } 

       
        public ConnectedClient(NetConnection connection, byte playerServerIndex)
        {
            Connection = connection;
            RemoteUniqueIdentifier = connection.RemoteUniqueIdentifier;
            PlayerServerIndex = playerServerIndex;
            
          
            PlayerName = $"Player_{playerServerIndex}"; 
            ColorID = playerServerIndex;
            IsReady = false;
            IsInitialized = false;
            IsSpawned = false;
            Health = 100f;
            HoldingWeaponID = -1; 

            Position = Vector2.Zero;
            Velocity = Vector2.Zero;
            AimDirection = 0f;
            IsJumping = false;
            IsGrounded = true; 
            LastKnownPosition = null;

            LastMessageTimestamp = DateTime.UtcNow;
        }

        
        public ConnectedClient(NetConnection connection)
            : this(connection, 0xFF) 
        {
           
            if (PlayerServerIndex == 0xFF)
            {
                PlayerName = "Player_" + RemoteUniqueIdentifier.ToString().Substring(0, Math.Min(6, RemoteUniqueIdentifier.ToString().Length));
            }
        }
        
       
    }
} 
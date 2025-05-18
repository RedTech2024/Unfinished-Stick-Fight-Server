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

    PlayerSpawnRequest,
    PlayerReady,

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
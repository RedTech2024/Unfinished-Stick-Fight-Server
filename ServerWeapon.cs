using System;

namespace StickFightLanServer
{
    public class ServerWeapon
    {
        public int WeaponID { get; private set; }
        public bool IsHeld { get; set; }
        public byte HeldByPlayerIndex { get; set; }
        
        public float PositionX { get; set; }
        public float PositionY { get; set; }

   
        public ServerWeapon(int weaponId, float initialPosX = 0f, float initialPosY = 0f)
        {
            WeaponID = weaponId;
            IsHeld = false;
            HeldByPlayerIndex = 255;
            PositionX = initialPosX;
            PositionY = initialPosY;
        }

        public void PickUp(byte playerIndex)
        {
            IsHeld = true;
            HeldByPlayerIndex = playerIndex;
        }

        public void Drop(float dropPosX, float dropPosY)
        {
            IsHeld = false;
            HeldByPlayerIndex = 255; 
            PositionX = dropPosX;
            PositionY = dropPosY;
            Console.WriteLine($"[ServerWeapon] WeaponID {WeaponID} dropped at ({PositionX:F2}, {PositionY:F2}).");
        }
    }
} 
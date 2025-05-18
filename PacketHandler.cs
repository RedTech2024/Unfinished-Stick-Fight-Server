using System;
using System.IO;

namespace StickFightLanServer
{
    public static class PacketHandler
    {
        public const int HeaderSize = 5;

        public static byte[] WriteMessageBuffer(byte[] payload, MsgType msgType)
        {
            using (MemoryStream memoryStream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(memoryStream))
                {
                    uint finalTimestamp = (uint)Environment.TickCount;
                    writer.Write(finalTimestamp);
                    writer.Write((byte)msgType);

                    if (payload != null && payload.Length > 0)
                    {
                        Console.WriteLine($"[PacketHandler DEBUG] Writing payload for {msgType}. Payload Length: {payload.Length}. Current Stream Pos: {memoryStream.Position}");
                        writer.Write(payload);
                        Console.WriteLine($"[PacketHandler DEBUG] After writing payload for {msgType}. New Stream Pos: {memoryStream.Position}");
                    }
                    else
                    {
                        Console.WriteLine($"[PacketHandler DEBUG] No payload or empty payload for {msgType}. Stream Pos after header: {memoryStream.Position}");
                    }
                    byte[] packet = memoryStream.ToArray();
                    Console.WriteLine($"[PacketHandler DEBUG] Final packet created for {msgType}. Total Length: {packet.Length}. Timestamp: {finalTimestamp}, MsgTypeVal: {(byte)msgType}");
                    return packet;
                }
            }
        }


        public static MsgType ParseMessage(byte[] rawData, out uint timestamp, out byte[] payload)
        {
            payload = null;
            timestamp = 0;
            if (rawData == null || rawData.Length < HeaderSize)
            {
                Console.WriteLine("[PacketHandler] Error: Raw data is too short to contain a header.");
                return (MsgType)255;
            }

            using (MemoryStream ms = new MemoryStream(rawData))
            {
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    try
                    {
                        timestamp = reader.ReadUInt32();
                        MsgType msgType = (MsgType)reader.ReadByte();

                        if (rawData.Length > HeaderSize)
                        {
                            payload = reader.ReadBytes(rawData.Length - HeaderSize);
                        }
                        else
                        {
                            payload = new byte[0];
                        }
                        return msgType;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PacketHandler] Error parsing message: {ex.Message}");
                        return (MsgType)255;
                    }
                }
            }
        }
    }
}

using ProtoBuf;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.Common;

// Send once. Notifies that the entityId of your client will be connected next packet.
// IP addresses compared to verify.
[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class NotificationPacket
{
    public int port = 1;
}

[ProtoContract]
public class UDPPacket
{
    [ProtoMember(1)]
    public byte id;

    [ProtoMember(2)]
    public byte[] data;

    public IServerPlayer player;

    public UDPPacket()
    {

    }

    public UDPPacket(byte id, byte[] data)
    {
        this.id = id;
        this.data = data;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class ConnectionPacket
{
    public long entityId;

    public ConnectionPacket()
    {

    }

    public ConnectionPacket(long entityId)
    {
        this.entityId = entityId;
    }
}

// Animations / controls sent seperately from position.
[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class AnimationPacket
{
    public long entityId;

    public int[] activeAnimations;
    public int activeAnimationsCount;
    public int activeAnimationsLength;
    public int[] activeAnimationSpeeds;
    public int activeAnimationSpeedsCount;
    public int activeAnimationSpeedsLength;

    public AnimationPacket()
    {

    }

    public AnimationPacket(Entity entity)
    {
        entityId = entity.EntityId;

        if (entity.AnimManager == null) return;
        Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode = entity.AnimManager.ActiveAnimationsByAnimCode;
        if (activeAnimationsByAnimCode.Count <= 0) return;
        int[] activeAnimationsArr = new int[activeAnimationsByAnimCode.Count];
        int[] activeAnimationSpeedsArr = new int[activeAnimationsByAnimCode.Count];
        int index = 0;
        foreach (KeyValuePair<string, AnimationMetaData> anim in activeAnimationsByAnimCode)
        {
            if (!(anim.Value.TriggeredBy?.DefaultAnim ?? false))
            {
                activeAnimationSpeedsArr[index] = CollectibleNet.SerializeFloatPrecise(anim.Value.AnimationSpeed); // Test not serializing this float.
                activeAnimationsArr[index++] = (int)anim.Value.CodeCrc32;
            }
        }

        activeAnimations = activeAnimationsArr;
        activeAnimationsCount = activeAnimationsArr.Length;
        activeAnimationsLength = activeAnimationsArr.Length;

        activeAnimationSpeeds = activeAnimationSpeedsArr;
        activeAnimationSpeedsCount = activeAnimationSpeedsArr.Length;
        activeAnimationSpeedsLength = activeAnimationSpeedsArr.Length;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class BulkAnimationPacket
{
    public AnimationPacket[] packets;
}

// For entities, sends them all in one packet.
[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class BulkPositionPacket
{
    public PositionPacket[] packets;
    public MinPositionPacket[] minPackets;
}

// One position packet. When sent by client sets position on server for that player instead.
[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class PositionPacket
{
    public int positionVersion;
    public int entityId;

    public double x;
    public double y;
    public double z;

    public float yaw;
    public float pitch;
    public float roll;

    public float motionX;
    public float motionY;
    public float motionZ;

    // Only for agent.
    public float headYaw;
    public float headPitch;
    public float bodyYaw;

    public bool teleport;

    public int controls;

    public bool lowRes;

    public PositionPacket()
    {

    }

    public PositionPacket(Entity entity, bool lowRes)
    {
        positionVersion = entity.WatchedAttributes.GetInt("positionVersionNumber", 0);

        entityId = (int)entity.EntityId;
        EntityPos pos = entity.SidedPos;

        x = pos.X;
        y = pos.Y;
        z = pos.Z;

        yaw = pos.Yaw;
        pitch = pos.Pitch;
        roll = pos.Roll;

        motionX = (float)pos.Motion.X;
        motionY = (float)pos.Motion.Y;
        motionZ = (float)pos.Motion.Z;

        teleport = entity.IsTeleport;

        if (entity is EntityAgent agent)
        {
            headYaw = pos.HeadYaw;
            headPitch = pos.HeadPitch;
            bodyYaw = agent.BodyYawServer;

            controls = agent.Controls.ToInt();
        }

        this.lowRes = lowRes;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class MinPositionPacket
{
    public int entityId;

    public double x;
    public double y;
    public double z;

    public float yaw;
    public float pitch;
    public float roll;

    // Only for agent.
    public float headYaw;
    public float headPitch;
    public float bodyYaw;

    public int controls;

    public bool lowRes;

    public MinPositionPacket()
    {

    }

    public MinPositionPacket(Entity entity, bool lowRes)
    {
        entityId = (int)entity.EntityId;

        EntityPos pos = entity.SidedPos;

        EntityPos pPos = entity.PreviousServerPos;

        //if (pos.X != pPos.X) x = pos.X;
        x = pos.X;

        //if (pos.Y != pPos.Y) y = pos.Y;
        y = pos.Y;

        //if (pos.Z != pPos.Z) z = pos.Z;
        z = pos.Z;

        if (pos.Yaw != pPos.Yaw) yaw = pos.Yaw;
        if (pos.Pitch != pPos.Pitch) pitch = pos.Pitch;
        if (pos.Roll != pPos.Roll) roll = pos.Roll;

        if (entity is EntityAgent agent)
        {
            if (pos.HeadYaw != pPos.HeadYaw) headYaw = pos.HeadYaw;
            if (pos.HeadPitch != pPos.HeadPitch) headPitch = pos.HeadPitch;
            bodyYaw = agent.BodyYawServer;

            controls = agent.Controls.ToInt();
        }

        this.lowRes = lowRes;
    }
}
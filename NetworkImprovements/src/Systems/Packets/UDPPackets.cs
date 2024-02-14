using ProtoBuf;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

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

    public float headYaw;
    public float headPitch;
    public float bodyYaw;

    public bool teleport;

    public int controls;

    public int tick;

    public PositionPacket()
    {

    }

    public PositionPacket(Entity entity, int tick)
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

        this.tick = tick;
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

    public int tick;

    public MinPositionPacket()
    {

    }

    public MinPositionPacket(Entity entity, int tick)
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

        this.tick = tick;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class BulkPositionPacket
{
    public PositionPacket[] packets;
    public MinPositionPacket[] minPackets;
}
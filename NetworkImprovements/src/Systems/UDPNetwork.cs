using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

public class UDPNetwork
{
    public ICoreClientAPI capi;
    public ICoreServerAPI sapi;

    public ClientMain client;
    public ServerMain server;

    public int serverPort = 42420;
    public int clientPort = 41420;

    public IPAddress serverIp = IPAddress.Loopback;

    // Sending data.
    public Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    public IPEndPoint endPoint;

    public UDPNetwork(ICoreAPI api)
    {
        if (api is ICoreClientAPI clientApi)
        {
            capi = clientApi;
            client = capi.World as ClientMain;
            client.RegisterGameTickListener(ClientTick, 1);

            endPoint = new(serverIp, serverPort);

            // Start client listener.
            Thread thread = new(new ThreadStart(ListenClient));
            thread.Start();
        }

        if (api is ICoreServerAPI serverApi)
        {
            sapi = serverApi;
            server = sapi.World as ServerMain;
            server.RegisterGameTickListener(ServerTick, 1);

            endPoint = new(serverIp, clientPort);

            // Start server listener.
            Thread thread = new(new ThreadStart(ListenServer));
            thread.Start();
        }
    }

    public object messageLock = new();
    public Queue<BulkPositionPacket> clientPacketQueue = new();
    public Queue<PositionPacket> serverPacketQueue = new();

    // LISTEN FOR MESSAGES AND ENQUEUE.

    public void ListenClient()
    {
        UdpClient listener = new(clientPort);
        IPEndPoint groupEp = new(IPAddress.Any, clientPort);

        try
        {
            while (true)
            {
                byte[] bytes = listener.Receive(ref groupEp);

                lock (messageLock)
                {
                    BulkPositionPacket packet = SerializerUtil.Deserialize<BulkPositionPacket>(bytes);
                    clientPacketQueue.Enqueue(packet);
                }

                Console.WriteLine($"Received packet from id {groupEp}");
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine(e);
        }
        finally
        {
            listener.Close();
        }
    }

    public void ListenServer()
    {
        UdpClient listener = new(serverPort);
        IPEndPoint groupEp = new(IPAddress.Any, serverPort);

        try
        {
            while (true)
            {
                byte[] bytes = listener.Receive(ref groupEp);

                lock (messageLock)
                {
                    serverPacketQueue.Enqueue(SerializerUtil.Deserialize<PositionPacket>(bytes));
                }
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine(e);
        }
        finally
        {
            listener.Close();
        }
    }

    // PROCESS PACKETS EVERY TICK.

    public void ClientTick(float dt)
    {
        lock (messageLock)
        {
            while (clientPacketQueue.Count > 0)
            {
                BulkPositionPacket bulkPacket = clientPacketQueue.Dequeue();

                if (bulkPacket.packets != null)
                {
                    foreach (PositionPacket packet in bulkPacket.packets)
                    {
                        Entity entity = client.GetEntityById(packet.entityId);
                        if (entity == null) continue;
                        EntityPos pos = entity.ServerPos;

                        // Tick info.
                        int lastTick = entity.WatchedAttributes.GetInt("lastTick", 0);
                        if (packet.tick < lastTick) continue;
                        if (lastTick == 0) lastTick = packet.tick - 1;
                        entity.WatchedAttributes.SetInt("tickDiff", packet.tick - lastTick);
                        entity.WatchedAttributes.SetInt("lastTick", packet.tick);

                        pos.X = packet.x;
                        pos.Y = packet.y;
                        pos.Z = packet.z;

                        pos.Yaw = packet.yaw;
                        pos.Pitch = packet.pitch;
                        pos.Roll = packet.roll;

                        if (entity is EntityAgent agent)
                        {
                            pos.HeadYaw = packet.headYaw;
                            pos.HeadPitch = packet.headPitch;
                            agent.BodyYaw = packet.bodyYaw;

                            // Sets main controls, then server controls if not the player.
                            agent.Controls.FromInt(packet.controls & 0x210);
                            if (agent.EntityId != client.EntityPlayer.EntityId)
                            {
                                agent.ServerControls.FromInt(packet.controls);
                            }
                        }

                        entity.OnReceivedServerPos(packet.teleport);

                        // Animations.
                        if (entity.Properties?.Client?.LoadedShapeForEntity?.Animations != null)
                        {
                            float[] speeds = new float[packet.activeAnimationSpeedsCount];
                            for (int i = 0; i < speeds.Length; i++)
                            {
                                speeds[i] = CollectibleNet.DeserializeFloatPrecise(packet.activeAnimationSpeeds[i]);
                            }

                            entity.OnReceivedServerAnimations(packet.activeAnimations, packet.activeAnimationsCount, speeds);
                        }
                    }
                }

                if (bulkPacket.minPackets != null)
                {
                    foreach (MinPositionPacket packet in bulkPacket.minPackets)
                    {
                        Entity entity = client.GetEntityById(packet.entityId);
                        if (entity == null) continue;
                        EntityPos pos = entity.ServerPos;

                        // Tick info.
                        int lastTick = entity.WatchedAttributes.GetInt("lastTick", 0);
                        if (packet.tick < lastTick) continue;
                        if (lastTick == 0) lastTick = packet.tick - 1;
                        entity.WatchedAttributes.SetInt("tickDiff", packet.tick - lastTick);
                        entity.WatchedAttributes.SetInt("lastTick", packet.tick);

                        pos.X = packet.x;
                        pos.Y = packet.y;
                        pos.Z = packet.z;

                        pos.Yaw = packet.yaw;
                        pos.Pitch = packet.pitch;
                        pos.Roll = packet.roll;

                        if (entity is EntityAgent agent)
                        {
                            pos.HeadYaw = packet.headYaw;
                            pos.HeadPitch = packet.headPitch;
                            agent.BodyYaw = packet.bodyYaw;
                        }

                        entity.OnReceivedServerPos(false);
                    }
                }
            }
        }
    }
    
    // Player positions.
    // Tick is set in player physics once when initialized.
    // Tick is increased every time a position is sent. 1 / 15f interval.
    public void ServerTick(float dt)
    {
        lock (messageLock)
        {
            while (serverPacketQueue.Count > 0)
            {
                PositionPacket packet = serverPacketQueue.Dequeue();

                if (packet == null) continue;

                if (sapi.World.GetEntityById(packet.entityId) is not EntityPlayer entity) continue;

                ServerPlayer player = entity.Player as ServerPlayer;

                // Check version.
                int version = entity.WatchedAttributes.GetInt("positionVersionNumber");
                if (packet.positionVersion < version) continue;

                // Check tick.
                int tick = entity.WatchedAttributes.GetInt("ct");
                if (tick > packet.tick) continue;
                entity.WatchedAttributes.SetInt("ct", packet.tick);
                int tickDiff = packet.tick - tick;

                player.LastReceivedClientPosition = server.ElapsedMilliseconds;

                // Set entity server position.
                EntityPos pos = entity.ServerPos;

                pos.X = packet.x;
                pos.Y = packet.y;
                pos.Z = packet.z;

                pos.Yaw = packet.yaw;
                pos.Pitch = packet.pitch;
                pos.Roll = packet.roll;

                pos.HeadYaw = packet.headYaw;
                pos.HeadPitch = packet.headPitch;
                entity.BodyYaw = packet.bodyYaw;

                // Set entity local position.
                entity.Pos.SetFrom(entity.ServerPos);
                entity.Pos.SetAngles(entity.ServerPos);

                // Call physics.
                entity.GetBehavior<EntityPlayerPhysics>().OnReceivedClientPos(version, tickDiff);

                // Broadcast position to other players.
                // ----------

                BulkPositionPacket bulkPositionPacket = new()
                {
                    packets = new PositionPacket[1],
                    minPackets = new MinPositionPacket[0]
                };

                bulkPositionPacket.packets[0] = new(entity, tick);

                //SendToClient(bulkPositionPacket);
            }
        }
    }

    // Broadcast message to all clients on the server.
    public void Broadcast()
    {

    }

    // Send message to specific client.
    public void SendToClient(BulkPositionPacket bulkPositionPacket)
    {
        sender.SendTo(SerializerUtil.Serialize(bulkPositionPacket), endPoint);
    }

    // Send message to server.
    public void SendToServer(PositionPacket packet)
    {
        sender.SendTo(SerializerUtil.Serialize(packet), endPoint);
    }

    public void SendPlayerPacket(int tick)
    {
        EntityPlayer player = capi.World.Player.Entity;

        PositionPacket packet = new(player, tick);

        SendToServer(packet);
    }
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
    public int tick;
    public int positionVersion;
    public long entityId;

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

    // Animations.
    public int[] activeAnimations;
    public int activeAnimationsCount;
    public int activeAnimationsLength;
    public int[] activeAnimationSpeeds;
    public int activeAnimationSpeedsCount;
    public int activeAnimationSpeedsLength;

    public PositionPacket()
    {

    }

    public PositionPacket(Entity entity, int tick)
    {
        this.tick = tick;

        positionVersion = entity.WatchedAttributes.GetInt("positionVersionNumber", 0);

        entityId = entity.EntityId;
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
            bodyYaw = agent.BodyYaw;

            controls = agent.Controls.ToInt();
        }

        // Animations.

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
public class MinPositionPacket
{
    public int tick;
    public long entityId;

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

    public MinPositionPacket()
    {

    }

    public MinPositionPacket(Entity entity, int tick)
    {
        this.tick = tick;
        entityId = entity.EntityId;

        EntityPos pos = entity.SidedPos;

        x = pos.X;
        y = pos.Y;
        z = pos.Z;

        yaw = pos.Yaw;
        pitch = pos.Pitch;
        roll = pos.Roll;

        if (entity is EntityAgent agent)
        {
            headYaw = pos.HeadYaw;
            headPitch = pos.HeadPitch;
            bodyYaw = agent.BodyYaw;

            controls = agent.Controls.ToInt();
        }
    }
}
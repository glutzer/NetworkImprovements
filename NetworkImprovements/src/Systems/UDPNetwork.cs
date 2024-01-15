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
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;

public class UDPNetwork
{
    public ICoreClientAPI capi;
    public ICoreServerAPI sapi;

    public ClientMain client;
    public ServerMain server;

    public Action<byte[]>[] clientHandlers = new Action<byte[]>[128];
    public Action<byte[], IServerPlayer>[] serverHandlers = new Action<byte[], IServerPlayer>[128];

    public bool connected = false;

    public IPAddress serverIp;
    public int serverPort = 42420;

    // Sending data.
    public UdpClient udpClient;

    public UDPNetwork(ICoreAPI api)
    {
        if (api is ICoreClientAPI clientApi)
        {
            clientHandlers[1] = HandleBulkPacket;

            capi = clientApi;
            client = capi.World as ClientMain;
            client.RegisterGameTickListener(ClientTick, 1);

            GuiScreenRunningGame gui = client.GetField<GuiScreenRunningGame>("ScreenRunningGame");
            if (client.GetField<bool>("IsSingleplayer"))
            {
                serverIp = IPAddress.Loopback;
                serverPort = 42420;
            }
            else
            {
                ServerConnectData connectData = client.GetField<ServerConnectData>("Connectdata");
                serverIp = IPAddress.Parse(connectData.Host);
                serverPort = connectData.Port;
            }

            udpClient = new();
            udpClient.Connect(serverIp, serverPort);

            ListenClient();
        }

        if (api is ICoreServerAPI serverApi)
        {
            udpClient = new UdpClient(serverPort);

            serverHandlers[1] = HandlePlayerPosition;

            sapi = serverApi;
            server = sapi.World as ServerMain;
            server.RegisterGameTickListener(ServerTick, 1);

            ListenServer();
        }
    }

    public object messageLock = new();
    public Queue<UDPPacket> clientPacketQueue = new();
    public Queue<UDPPacket> serverPacketQueue = new();

    public Dictionary<long, IServerPlayer> connectingClients = new();
    public Dictionary<IServerPlayer, IPEndPoint> connectedClients = new();
    public Dictionary<IPEndPoint, IServerPlayer> endPoints = new();

    public IPEndPoint groupEp = new(IPAddress.Any, 0);
    public byte[] dataBuffer;

    // LISTEN FOR MESSAGES AND ENQUEUE.

    public void ListenClient()
    {
        udpClient.BeginReceive(new AsyncCallback(ClientReceiveCallback), null);
    }

    public void ClientReceiveCallback(IAsyncResult ar)
    {
        try
        {
            dataBuffer = udpClient.EndReceive(ar, ref groupEp);

            UDPPacket packet = SerializerUtil.Deserialize<UDPPacket>(dataBuffer);

            lock (messageLock)
            {
                if (packet != null) clientPacketQueue.Enqueue(packet);
            }

            udpClient.BeginReceive(new AsyncCallback(ClientReceiveCallback), null);
        }
        catch (Exception e)
        {

        }
        finally
        {

        }
    }

    public void ListenServer()
    {
        udpClient.BeginReceive(new AsyncCallback(ServerReceiveCallback), null);
    }

    public void ServerReceiveCallback(IAsyncResult ar)
    {
        try
        {
            dataBuffer = udpClient.EndReceive(ar, ref groupEp);

            UDPPacket packet = SerializerUtil.Deserialize<UDPPacket>(dataBuffer);

            if (packet?.id == 0)
            {
                HandleConnectionRequest(packet.data, new IPEndPoint(groupEp.Address, groupEp.Port));
            }
            else
            {
                IServerPlayer sp = endPoints.Get(groupEp); // Race condition?
                if (sp != null && packet != null)
                {
                    packet.player = sp;

                    lock (messageLock)
                    {
                        serverPacketQueue.Enqueue(packet);
                    }
                }
            }

            udpClient.BeginReceive(new AsyncCallback(ServerReceiveCallback), null);
        }
        catch (Exception e)
        {

        }
        finally
        {
            
        }
    }

    // PROCESS PACKETS EVERY TICK.

    public void ClientTick(float dt)
    {
        lock (messageLock)
        {
            while (clientPacketQueue.Count > 0)
            {
                UDPPacket packet = clientPacketQueue.Dequeue();
                clientHandlers[packet.id](packet.data);
            }
        }
    }

    public void ServerTick(float dt)
    {
        lock (messageLock)
        {
            while (serverPacketQueue.Count > 0)
            {
                UDPPacket packet = serverPacketQueue.Dequeue();
                serverHandlers[packet.id](packet.data, packet.player);
            }
        }
    }

    // Handle bulk positions on client.
    public void HandleBulkPacket(byte[] bytes)
    {
        BulkPositionPacket bulkPacket = SerializerUtil.Deserialize<BulkPositionPacket>(bytes);

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
                    agent.BodyYawServer = packet.bodyYaw;

                    // Sets main controls, then server controls if not the player.
                    agent.Controls.FromInt(packet.controls & 0x210);
                    if (agent.EntityId != client.EntityPlayer.EntityId)
                    {
                        agent.ServerControls.FromInt(packet.controls);
                    }
                }

                entity.OnReceivedServerPos(packet.teleport);
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
                    agent.BodyYawServer = packet.bodyYaw;
                }

                entity.OnReceivedServerPos(false);
            }
        }
    }

    public void HandlePlayerPosition(byte[] bytes, IServerPlayer player)
    {
        PositionPacket packet = SerializerUtil.Deserialize<PositionPacket>(bytes);

        if (packet == null) return;

        if (sapi.World.GetEntityById(packet.entityId) is not EntityPlayer entity) return;

        ServerPlayer serverPlayer = entity.Player as ServerPlayer;

        // Check version.
        int version = entity.WatchedAttributes.GetInt("positionVersionNumber");
        if (packet.positionVersion < version) return;

        // Check tick.
        int tick = entity.WatchedAttributes.GetInt("ct");
        if (tick > packet.tick) return;
        entity.WatchedAttributes.SetInt("ct", packet.tick);
        int tickDiff = packet.tick - tick;

        serverPlayer.LastReceivedClientPosition = server.ElapsedMilliseconds;

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
        entity.BodyYawServer = packet.bodyYaw;

        // Set entity local position.
        entity.Pos.SetFrom(entity.ServerPos);
        entity.Pos.SetAngles(entity.ServerPos);

        // Call physics.
        entity.GetBehavior<EntityPlayerPhysics>().OnReceivedClientPos(version, tickDiff);

        // Broadcast position to other players.
        BulkPositionPacket bulkPositionPacket = new()
        {
            packets = new PositionPacket[1],
            minPackets = Array.Empty<MinPositionPacket>()
        };
        bulkPositionPacket.packets[0] = new(entity, tick);

        byte[] packetBytes = MakePacket(1, bulkPositionPacket);

        foreach (IServerPlayer sp in connectedClients.Keys)
        {
            if (sp.Entity == entity) continue;
            SendToClient(packetBytes, sp);
        }
        entity.IsTeleport = false;

        if (entity.AnimManager?.AnimationsDirty == true)
        {
            AnimationPacket[] packets = new AnimationPacket[1];
            packets[0] = new AnimationPacket(entity);

            BulkAnimationPacket bulkAnimationPacket = new()
            {
                packets = packets
            };

            foreach (IServerPlayer sp in connectedClients.Keys)
            {
                if (sp.Entity == entity) continue;
                sapi.ModLoader.GetModSystem<NIM>().serverChannel.SendPacket(bulkAnimationPacket, sp);
            }

            entity.AnimManager.AnimationsDirty = false;
        }
    }

    // Sends a connection packet to the server.
    public void SendConnectionPacket()
    {
        ConnectionPacket connectionPacket = new(capi.World.Player.Entity.EntityId);
        byte[] data = MakePacket(0, connectionPacket);
        SendToServer(data);
    }

    // Handle connection request through UDP.
    public void HandleConnectionRequest(byte[] bytes, IPEndPoint endPoint)
    {
        ConnectionPacket packet = SerializerUtil.Deserialize<ConnectionPacket>(bytes);
        IServerPlayer player = connectingClients.Get(packet.entityId);
        if (player == null || connectedClients.ContainsKey(player)) return;

        // Kick illegal connections.
        if (endPoint.Address != IPAddress.Parse(player.IpAddress))
        {
            Console.WriteLine($"Invalid connection: {endPoint.Address}, {IPAddress.Parse(player.IpAddress)}, {player.IpAddress}");
        }

        connectingClients.Remove(player.Entity.EntityId);
        connectedClients.Add(player, endPoint);
        endPoints.Add(endPoint, player);

        NotificationPacket notificationPacket = new()
        {
            port = endPoint.Port
        };

        sapi.ModLoader.GetModSystem<NIM>().serverChannel.SendPacket(notificationPacket, player);
    }

    // Serialize a packet into a UDP packet with the id.
    public static byte[] MakePacket<T>(byte id, T toSerialize)
    {
        UDPPacket packet = new(id, SerializerUtil.Serialize(toSerialize));
        return SerializerUtil.Serialize(packet);
    }

    // Send packet to specific client.
    public void SendToClient(byte[] data, IServerPlayer player)
    {
        IPEndPoint clientEndPoint = connectedClients.Get(player);
        if (clientEndPoint == null) return;
        udpClient.BeginSend(data, data.Length, clientEndPoint, (ar) =>
        {
            udpClient.EndSend(ar);
        }, null);
    }

    // Send packet to server.
    public void SendToServer(byte[] data)
    {
        udpClient.BeginSend(data, data.Length, (ar) =>
        {
            udpClient.EndSend(ar);
        }, null);
    }

    public void SendPlayerPacket(int tick)
    {
        EntityPlayer player = capi.World.Player.Entity;
        player.BodyYawServer = player.BodyYaw;
        PositionPacket packet = new(player, tick);
        SendToServer(MakePacket(1, packet));
    }

    public void SendBulkPositionPacket(BulkPositionPacket packet, IServerPlayer player)
    {
        SendToClient(MakePacket(1, packet), player);
    }

    public void Dispose()
    {
        udpClient.Close();
    }
}
using HarmonyLib;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

public class ServerSystemPatches
{
    // Increase range at which hits are allowed, also increase valid picking range.
    // Minecraft also does this for lag compensation which is why kill aura can hit people farther away. The only other solution would be to keep a log of previous player locations and also test those.
    // Counter strike also does that.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "HandleEntityInteraction")]
    public static class CombatPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ServerSystemEntitySimulation __instance, Packet_Client packet, ConnectedClient client)
        {
            ServerPlayer player = client.Player;
            if (player.WorldData.CurrentGameMode == EnumGameMode.Spectator) return false;

            Packet_EntityInteraction p = packet.EntityInteraction;
            Entity[] entitiesAround = __instance.GetField<ServerMain>("server").GetEntitiesAround(player.Entity.ServerPos.XYZ, player.WorldData.PickingRange + 10f, player.WorldData.PickingRange + 10f, (Entity e) => e.EntityId == p.EntityId);
            if (entitiesAround == null || entitiesAround.Length == 0)
            {
                ServerMain.Logger.Debug("HandleEntityInteraction received from client " + client.PlayerName + " but no such entity found in his range");
                return false;
            }

            Entity entity = entitiesAround[0];
            Cuboidd cuboidd = entity.SelectionBox.ToDouble().Translate(entity.SidedPos.X, entity.SidedPos.Y, entity.SidedPos.Z);
            EntityPos sidedPos = client.Entityplayer.SidedPos;
            ItemStack itemStack = client.Player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            float range = itemStack?.Collectible.GetAttackRange(itemStack) ?? GlobalConstants.DefaultAttackRange;
            if ((cuboidd.ShortestDistanceFrom(sidedPos.X + client.Entityplayer.LocalEyePos.X, sidedPos.Y + client.Entityplayer.LocalEyePos.Y, sidedPos.Z + client.Entityplayer.LocalEyePos.Z) > (double)(range * 2) && p.MouseButton == 0) || (p.MouseButton == 0 && (((!__instance.GetField<ServerMain>("server").Config.AllowPvP || !player.HasPrivilege("attackplayers")) && entity is EntityPlayer) || (!player.HasPrivilege("attackcreatures") && entity is EntityAgent))))
            {
                return false;
            }

            EntityPlayer entityPlayer = entity as EntityPlayer;
            if (entityPlayer != null)
            {
                if (entityPlayer.Player is not IServerPlayer obj || obj.ConnectionState != EnumClientState.Playing)
                {
                    return false;
                }
            }

            Vec3d hitPosition = new(CollectibleNet.DeserializeDouble(p.HitX), CollectibleNet.DeserializeDouble(p.HitY), CollectibleNet.DeserializeDouble(p.HitZ));
            EnumHandling handling = EnumHandling.PassThrough;
            __instance.GetField<ServerMain>("server").EventManager.TriggerPlayerInteractEntity(entity, player, player.GetField<PlayerInventoryManager>("inventoryMgr").ActiveHotbarSlot, hitPosition, p.MouseButton, ref handling);
            if (handling == EnumHandling.PassThrough)
            {
                entity.OnInteract(player.Entity, player.InventoryManager.ActiveHotbarSlot, hitPosition, (p.MouseButton != 0) ? EnumInteractMode.Interact : EnumInteractMode.Attack);
            }
            return false;
        }
    }

    // Send entity attribute methods after physics ticks in physics manager instead. Default 15/s.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "UpdateEvery200ms")]
    public static class RemoveAttribUpdatesPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ServerSystemEntitySimulation __instance)
        {
            __instance.CallMethod("VerifyPlayerPositions");
            return false;
        }
    }

    // Call this if the version is correct. Does physics and broadcasts the newly received player position.
    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "HandlePlayerPosition")]
    public static class HandlePlayerPositionPostfix
    {
        [HarmonyPostfix]
        public static void Postfix(Packet_Client packet, ConnectedClient client, ref ServerMain ___server)
        {
            ServerPlayer player = client.Player;

            Packet_PlayerPosition playerPosition = packet.PlayerPosition;

            int version = player.Entity.WatchedAttributes.GetInt("positionVersionNumber");

            if (playerPosition.PositionVersionNumber >= version)
            {
                BroadcastNewPlayerPosition(client.Player.Entity, ___server);
                client.Player.Entity.GetBehavior<EntityPlayerPhysics>().OnReceivedClientPos(packet.PlayerPosition.PositionVersionNumber);
            }
        }

        // This should just broadcast a new entity moved packet. I just copied the bulk version from entity simulation since I couldn't find it. New entity attribute update doesn't send player positions. Instead player positions are sent as soon as the server receives them.
        public static void BroadcastNewPlayerPosition(EntityPlayer entity, ServerMain server)
        {
            Packet_EntityMoved packet = new()
            {
                EntityId = entity.EntityId,
                EntityPosition = ServerPackets.getEntityPositionPacket(entity.ServerPos, entity),
                MotionX = CollectibleNet.SerializeFloatVeryPrecise((float)entity.ServerPos.Motion.X),
                MotionY = CollectibleNet.SerializeFloatVeryPrecise((float)entity.ServerPos.Motion.Y),
                MotionZ = CollectibleNet.SerializeFloatVeryPrecise((float)entity.ServerPos.Motion.Z),
                Controls = entity?.Controls.ToInt() ?? 0,
                IsTeleport = entity.IsTeleport ? 1 : 0
            };

            if (entity.AnimManager == null)
            {
                return;
            }

            Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode = entity.AnimManager.ActiveAnimationsByAnimCode;

            int[] anims1 = new int[activeAnimationsByAnimCode.Count];
            int[] anims2 = new int[activeAnimationsByAnimCode.Count];

            int index = 0;
            foreach (KeyValuePair<string, AnimationMetaData> item in activeAnimationsByAnimCode)
            {
                if (!(item.Value.TriggeredBy?.DefaultAnim ?? false))
                {
                    anims2[index] = CollectibleNet.SerializeFloatPrecise(item.Value.AnimationSpeed);
                    anims1[index++] = (int)item.Value.CodeCrc32;
                }
            }

            packet.SetActiveAnimations(anims1);
            packet.SetActiveAnimationSpeeds(anims2);

            // Might need to initialize all arrays to test this.
            Packet_EntityMoved[] bulkPacket1 = new Packet_EntityMoved[1];
            bulkPacket1[0] = packet;
            Packet_BulkEntityAttributes bulkPacket2 = new();
            bulkPacket2.SetPosUpdates(bulkPacket1);
            Packet_Server packetServer = new()
            {
                Id = 60,
                BulkEntityAttributes = bulkPacket2
            };

            foreach (ConnectedClient client in server.Clients.Values)
            {
                if (client.State != EnumClientState.Connected && client.State != EnumClientState.Playing) continue;

                // Don't send updates to self.
                if (entity == client.Entityplayer) continue;

                // Don't send untracked players.
                if (!client.TrackedEntities.ContainsKey(entity.EntityId)) continue;

                server.SendPacket(client.Id, packetServer);
            }
        }
    }
}